using FieldProbe.Core;

namespace FieldProbe.Drivers.Modbus;

/// <summary>Explica byte a byte um pacote Modbus TCP (MBAP + PDU) ou RTU (unit + PDU + CRC).</summary>
public static class ModbusFrameDecoder
{
    public static DecodedFrame Decode(byte[] data, FrameDirection direction, bool rtu)
    {
        var request = direction == FrameDirection.Tx;
        return FrameDecoding.Run(rtu ? "Modbus RTU" : "Modbus TCP", data, c =>
        {
            byte unit;
            var pduEnd = c.Length;
            if (rtu)
            {
                c.Layer("Modbus RTU · endereço");
                unit = c.U8("Endereço do escravo (Unit ID)", note: "qual equipamento do barramento RS-485 responde");
                pduEnd = c.Length - 2; // os 2 últimos bytes são o CRC
            }
            else
            {
                c.Layer("Modbus TCP · cabeçalho MBAP");
                c.U16BE("Transaction ID", note: "número do pedido: a resposta volta com o mesmo número");
                c.U16BE("Protocol ID", v => v == 0 ? "0 (Modbus)" : $"{v} (inesperado: deveria ser 0)");
                var length = c.U16BE("Comprimento", v => $"{v} bytes", "bytes que vêm depois deste campo (Unit ID + PDU)");
                unit = c.U8("Unit ID", note: "em gateway TCP→serial, indica o equipamento do barramento");
                if (length != c.Length - 6) c.Warn($"O campo Comprimento diz {length} bytes, mas o pacote tem {c.Length - 6} depois dele.");
            }

            DecodePdu(c, request, pduEnd, out var summary);
            c.Frame.Summary = $"Unit {unit} · {summary}";

            if (rtu && c.Length >= 4)
            {
                c.Position = c.Length - 2;
                c.Layer("Modbus RTU · verificação");
                var received = (ushort)(c.Data[^2] | (c.Data[^1] << 8));
                var expected = Crc16(c.Data.AsSpan(0, c.Length - 2));
                c.Field(2, "CRC-16", $"0x{received:X4}",
                    received == expected ? "correto: o pacote chegou sem erro" : $"ERRADO: o certo seria 0x{expected:X4} (ruído na linha, baud rate ou paridade diferentes)");
                if (received != expected) c.Warn("CRC não confere: o pacote foi corrompido no caminho.");
            }
        });
    }

    private static void DecodePdu(FrameCursor c, bool request, int pduEnd, out string summary)
    {
        c.Layer(request ? "Modbus · PDU (requisição)" : "Modbus · PDU (resposta)");
        var function = c.U8("Código de função", f => (f & 0x80) != 0
            ? $"0x{f:X2} = exceção da função 0x{f & 0x7F:X2}"
            : $"0x{f:X2} ({ModbusDescriber.FunctionName(f)})");

        if ((function & 0x80) != 0)
        {
            var code = c.U8("Código de exceção", e => $"0x{e:X2}", ModbusException.Describe(c.Data[c.Position]));
            summary = $"EXCEÇÃO na FC{function & 0x7F:X2}: {ModbusException.Describe(code)}";
            return;
        }

        var name = ModbusDescriber.FunctionName(function);
        var area = function switch
        {
            0x01 or 0x05 or 0x0F => "CO",
            0x02 => "DI",
            0x03 or 0x06 or 0x10 => "HR",
            0x04 => "IR",
            _ => string.Empty
        };
        var classicBase = area switch { "CO" => 1, "DI" => 10001, "IR" => 30001, _ => 40001 };
        string Address(ushort a) => area.Length == 0 ? a.ToString() : $"{a} → {area}:{a} (clássico {classicBase + a})";

        switch (function)
        {
            case 0x01 or 0x02 or 0x03 or 0x04 when request:
            {
                var start = c.U16BE("Endereço inicial", Address, "offset começando em 0");
                var count = c.U16BE("Quantidade", q => function <= 0x02 ? $"{q} bit(s)" : $"{q} registrador(es)",
                    function <= 0x02 ? "máximo 2000 por pedido" : "máximo 125 por pedido");
                summary = $"Requisição · FC{function:X2} {name} · {area}:{start}, {count} {(function <= 0x02 ? "bit(s)" : "registrador(es)")}";
                return;
            }
            case 0x01 or 0x02:
            {
                var bytes = c.U8("Contagem de bytes", b => $"{b} bytes");
                var start = c.Position;
                c.Bytes(Math.Max(0, Math.Min(bytes, pduEnd - c.Position)), "Bits", null, "cada byte guarda 8 entradas; o bit 0 é o primeiro endereço pedido");
                var on = 0;
                for (var i = start; i < start + bytes && i < c.Length; i++) on += System.Numerics.BitOperations.PopCount(c.Data[i]);
                summary = $"Resposta · FC{function:X2} {name} · {bytes} byte(s), {on} bit(s) ligados";
                return;
            }
            case 0x03 or 0x04:
            {
                var bytes = c.U8("Contagem de bytes", b => $"{b} bytes = {b / 2} registrador(es)");
                var count = Math.Min(bytes / 2, (pduEnd - c.Position) / 2);
                for (var i = 0; i < count; i++)
                {
                    var shown = i < 64;
                    var v = c.PeekU16BE();
                    if (shown)
                        c.Field(2, $"Registrador [{i}]", $"{v}  (0x{v:X4}, com sinal {(short)v})", i == 0 ? "16 bits, big-endian. O endereço é o inicial do pedido + índice" : string.Empty, 1);
                    else
                        c.Position += 2;
                }
                if (count > 64) c.AddAt(c.Position - (count - 64) * 2, (count - 64) * 2, "…", $"mais {count - 64} registrador(es)", level: 1);
                summary = $"Resposta · FC{function:X2} {name} · {count} registrador(es)";
                return;
            }
            case 0x05:
            {
                var address = c.U16BE("Endereço", Address);
                var value = c.U16BE("Valor", v => v switch { 0xFF00 => "0xFF00 = LIGA", 0x0000 => "0x0000 = DESLIGA", _ => $"0x{v:X4} (inválido: só 0xFF00 ou 0x0000)" });
                summary = $"{(request ? "Requisição" : "Resposta (eco)")} · FC05 {name} · CO:{address} = {(value == 0xFF00 ? "LIGA" : "DESLIGA")}";
                return;
            }
            case 0x06:
            {
                var address = c.U16BE("Endereço", Address);
                var value = c.U16BE("Valor", v => $"{v} (0x{v:X4}, com sinal {(short)v})");
                summary = $"{(request ? "Requisição" : "Resposta (eco)")} · FC06 {name} · HR:{address} = {value}";
                return;
            }
            case 0x0F or 0x10 when request:
            {
                var start = c.U16BE("Endereço inicial", Address);
                var count = c.U16BE("Quantidade", q => function == 0x0F ? $"{q} bit(s)" : $"{q} registrador(es)");
                var bytes = c.U8("Contagem de bytes", b => $"{b} bytes");
                if (function == 0x10)
                {
                    for (var i = 0; i < bytes / 2 && c.Remaining >= 2 && c.Position + 2 <= pduEnd; i++)
                    {
                        var v = c.PeekU16BE();
                        c.Field(2, $"Valor [{i}] → HR:{start + i}", $"{v} (0x{v:X4})", level: 1);
                    }
                }
                else
                {
                    c.Bytes(Math.Max(0, Math.Min(bytes, pduEnd - c.Position)), "Bits", null, "bit 0 do primeiro byte = primeiro endereço");
                }
                summary = $"Requisição · FC{function:X2} {name} · {area}:{start}, {count} {(function == 0x0F ? "bit(s)" : "registrador(es)")}";
                return;
            }
            case 0x0F or 0x10:
            {
                var start = c.U16BE("Endereço inicial", Address);
                var count = c.U16BE("Quantidade escrita", q => q.ToString());
                summary = $"Resposta · FC{function:X2} {name} · {area}:{start}, {count} escrito(s)";
                return;
            }
            case 0x2B:
            {
                c.U8("Tipo MEI", m => m == 0x0E ? "0x0E (Read Device Identification)" : $"0x{m:X2}");
                c.U8("Código de leitura", r => r switch { 1 => "1 (básica: fabricante, código, revisão)", 2 => "2 (normal)", 3 => "3 (estendida)", 4 => "4 (objeto específico)", _ => r.ToString() });
                if (request)
                {
                    c.U8("ID do objeto inicial", o => o.ToString());
                    summary = "Requisição · FC2B Read Device Identification";
                    return;
                }
                c.U8("Nível de conformidade", l => $"0x{l:X2}");
                c.U8("Mais pacotes", m => m == 0xFF ? "sim" : "não");
                c.U8("Próximo objeto", o => o.ToString());
                var objects = c.U8("Quantidade de objetos", o => o.ToString());
                var parts = new List<string>();
                for (var i = 0; i < objects && pduEnd - c.Position >= 2; i++)
                {
                    var id = c.U8("ID do objeto", o => o switch { 0 => "0 (Fabricante)", 1 => "1 (Código do produto)", 2 => "2 (Revisão)", 3 => "3 (URL)", 4 => "4 (Nome do produto)", 5 => "5 (Modelo)", _ => o.ToString() }, level: 1);
                    var len = c.U8("Tamanho", l => $"{l} bytes", level: 1);
                    var text = c.Ascii(Math.Max(0, Math.Min(len, pduEnd - c.Position)), "Texto", level: 1);
                    if (id <= 2) parts.Add(text);
                }
                summary = $"Resposta · FC2B Identificação · {string.Join(" · ", parts)}";
                return;
            }
            default:
                if (pduEnd > c.Position) c.Bytes(pduEnd - c.Position, "Dados da função", null, "função não decodificada");
                summary = $"FC{function:X2} {name}";
                return;
        }
    }

    /// <summary>CRC-16/MODBUS (polinômio 0xA001, valor inicial 0xFFFF).</summary>
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }
}
