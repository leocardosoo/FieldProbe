using System.Buffers.Binary;
using FieldProbe.Core;

namespace FieldProbe.Drivers.Omron;

/// <summary>Explica byte a byte um pacote Omron FINS: cabeçalho FINS/TCP (se houver), cabeçalho FINS, comando e dados.</summary>
public static class FinsFrameDecoder
{
    public static bool CanDecode(byte[] data) =>
        (data.Length >= 16 && data[0] == (byte)'F' && data[1] == (byte)'I' && data[2] == (byte)'N' && data[3] == (byte)'S') ||
        (data.Length >= 12 && (data[0] & 0xBE) == 0x80);

    public static DecodedFrame Decode(byte[] data) => FrameDecoding.Run("Omron FINS", data, c =>
    {
        if (data[0] == (byte)'F' && data.Length >= 16 && data[1] == (byte)'I')
        {
            c.Layer("FINS/TCP · cabeçalho");
            c.Ascii(4, "Identificador", "sempre \"FINS\"");
            var length = c.U32BE("Comprimento", v => $"{v} bytes", "do campo Comando até o fim");
            var command = c.U32BE("Comando", v => v switch
            {
                0 => "0 = pedido de nó (cliente → CLP)",
                1 => "1 = nós atribuídos (CLP → cliente)",
                2 => "2 = quadro FINS",
                _ => v.ToString()
            });
            var error = c.U32BE("Erro", v => v == 0 ? "0 (OK)" : $"0x{v:X} · {FinsException.DescribeTcp(v)}");
            if (length != c.Length - 8) c.Warn($"O campo Comprimento diz {length} bytes, mas o pacote tem {c.Length - 8} depois dele.");
            if (error != 0) c.Warn($"Erro FINS/TCP: {FinsException.DescribeTcp(error)}");

            switch (command)
            {
                case 0:
                    c.Layer("FINS/TCP · pedido de nó");
                    c.U32BE("Nó do cliente", v => v == 0 ? "0 (o CLP escolhe)" : v.ToString());
                    c.Frame.Summary = "Handshake · cliente pede um número de nó";
                    return;
                case 1:
                    c.Layer("FINS/TCP · nós atribuídos");
                    var pc = c.U32BE("Nó do cliente (PC)");
                    var plc = c.U32BE("Nó do servidor (CLP)");
                    c.Frame.Summary = $"Handshake · PC = nó {pc}, CLP = nó {plc}";
                    return;
                case 2:
                    break;
                default:
                    c.Frame.Summary = $"FINS/TCP · comando {command}";
                    return;
            }
        }

        DecodeFins(c);
    });

    private static string CommandName(byte mrc, byte src) => (mrc, src) switch
    {
        (0x01, 0x01) => "Leitura de memória (Memory Area Read)",
        (0x01, 0x02) => "Escrita de memória (Memory Area Write)",
        (0x01, 0x03) => "Preenchimento de memória (Memory Area Fill)",
        (0x01, 0x04) => "Leitura múltipla (Multiple Memory Area Read)",
        (0x01, 0x05) => "Transferência de memória",
        (0x04, 0x01) => "RUN (muda o modo da CPU)",
        (0x04, 0x02) => "STOP (muda a CPU para PROGRAM)",
        (0x05, 0x01) => "Leitura de dados da CPU (modelo/versão)",
        (0x06, 0x01) => "Leitura do estado da CPU",
        (0x07, 0x01) => "Leitura do relógio",
        (0x21, 0x01) => "Limpar erro",
        _ => "comando"
    };

    private static void DecodeFins(FrameCursor c)
    {
        c.Layer("FINS · cabeçalho");
        var icf = c.U8("ICF", v => (v & 0x40) != 0 ? $"0x{v:X2} (resposta)" : $"0x{v:X2} (comando{((v & 0x01) != 0 ? ", sem resposta" : ", pede resposta")})");
        var isResponse = (icf & 0x40) != 0;
        c.U8("RSV", note: "reservado (0)");
        c.U8("GCT", note: "quantas pontes entre redes o pacote pode atravessar");
        c.U8("DNA · rede de destino", v => v == 0 ? "0 (local)" : v.ToString());
        c.U8("DA1 · nó de destino", note: isResponse ? "o PC" : "o CLP");
        c.U8("DA2 · unidade de destino", v => v == 0 ? "0 (CPU)" : FrameCursor.Hex8(v));
        c.U8("SNA · rede de origem", v => v == 0 ? "0 (local)" : v.ToString());
        c.U8("SA1 · nó de origem", note: isResponse ? "o CLP" : "o PC");
        c.U8("SA2 · unidade de origem");
        var sid = c.U8("SID", note: "número do pedido: a resposta volta com o mesmo");

        c.Layer(isResponse ? "FINS · resposta" : "FINS · comando");
        var mrc = c.U8("MRC", v => FrameCursor.Hex8(v), "código principal do comando");
        var src = c.U8("SRC", v => $"{FrameCursor.Hex8(v)} · {CommandName(mrc, v)}", "código secundário");
        var name = CommandName(mrc, src);

        if (isResponse)
        {
            var mres = c.Data[c.Position];
            var sres = c.Data.Length > c.Position + 1 ? c.Data[c.Position + 1] : (byte)0;
            var (code, relay, nonFatal, fatal) = FinsException.Split(mres, sres);
            c.Field(2, "Código de término", code == 0 ? "0x0000 (OK)" : $"0x{code:X4}", FinsException.Describe(code) +
                (relay ? " · erro de repasse na rede" : string.Empty) + (fatal ? " · CPU com ERRO FATAL" : nonFatal ? " · CPU com erro não fatal" : string.Empty));
            if (code != 0) c.Warn($"O CLP recusou o pedido: 0x{code:X4} · {FinsException.Describe(code)}");
            if (fatal) c.Warn("A CPU está com erro fatal (sinalizado na resposta).");
            else if (nonFatal) c.Warn("A CPU está com erro não fatal (sinalizado na resposta).");
            c.Frame.Summary = code != 0 ? $"Resposta · {name} · ERRO 0x{code:X4}" : $"Resposta · {name} · OK (SID {sid})";
            if (code != 0 || c.Remaining == 0) return;

            c.Layer("FINS · dados");
            if (mrc == 0x05 && src == 0x01 && c.Remaining >= 20)
            {
                c.Ascii(20, "Modelo da CPU");
                if (c.Remaining >= 20) c.Ascii(20, "Versão");
                c.Rest("Outros dados da CPU");
                return;
            }
            if (mrc == 0x06 && src == 0x01 && c.Remaining >= 2)
            {
                c.U8("Estado", v => v switch { 0x00 => "0x00 (parada)", 0x01 => "0x01 (rodando)", 0x80 => "0x80 (CPU em espera)", _ => FrameCursor.Hex8(v) });
                c.U8("Modo", v => v switch { 0x00 => "0x00 (PROGRAM)", 0x02 => "0x02 (MONITOR)", 0x04 => "0x04 (RUN)", _ => FrameCursor.Hex8(v) });
                if (c.Remaining >= 2) c.U16BE("Erros fatais", v => v == 0 ? "nenhum" : FrameCursor.Hex16(v));
                if (c.Remaining >= 2) c.U16BE("Erros não fatais", v => v == 0 ? "nenhum" : FrameCursor.Hex16(v));
                if (c.Remaining >= 4) c.Bytes(4, "Mensagens / FAL");
                if (c.Remaining >= 16) c.Ascii(16, "Mensagem de erro");
                return;
            }
            if (c.Remaining % 2 != 0 || c.Remaining > 64)
            {
                c.Rest("Dados", c.Remaining % 2 != 0 ? "leitura de bits: 1 byte por bit (00 = OFF, 01 = ON)" : "palavras big-endian");
                return;
            }
            var index = 0;
            while (c.Remaining >= 2)
            {
                c.U16BE($"Palavra {index}", v => $"{(short)v} (0x{v:X4})", index == 0 ? "big-endian; 32 bits = palavra baixa primeiro" : string.Empty, 1);
                index++;
            }
            return;
        }

        c.Frame.Summary = $"Comando · {name} (SID {sid})";
        if (mrc != 0x01 || src is not (0x01 or 0x02) || c.Remaining < 6) return;

        c.Layer("FINS · área de memória");
        var areaCode = c.Data[c.Position];
        var wire = BinaryPrimitives.ReadUInt16BigEndian(c.Data.AsSpan(c.Position + 1));
        var area = FinsAddress.FindByCode(areaCode, wire, out var isBit);
        c.U8("Código da área", v => area is null ? FrameCursor.Hex8(v) : $"{FrameCursor.Hex8(v)} = {area.Description} ({(isBit ? "bits" : "palavras")})");
        var word = c.U16BE("Endereço", v => area is null ? v.ToString() : $"{v} → {area.Prefix}{v - area.AddressOffset}");
        var bit = c.U8("Bit", v => isBit ? v.ToString("00") : $"{v} (acesso em palavras)");
        var count = c.U16BE("Quantidade", v => isBit ? $"{v} bit(s)" : $"{v} palavra(s)");
        var label = area is null ? $"área 0x{areaCode:X2}:{word}" : $"{(area.Prefix == "CIO" ? string.Empty : area.Prefix)}{word - area.AddressOffset}{(isBit ? $".{bit:00}" : string.Empty)}";
        c.Frame.Summary = $"Comando · {(src == 0x02 ? "Escrita" : "Leitura")} {label}, {count} {(isBit ? "bit(s)" : "palavra(s)")} (SID {sid})";

        if (src != 0x02 || c.Remaining == 0) return;
        c.Layer("FINS · dados escritos");
        if (isBit)
        {
            for (var i = 0; i < count && c.Remaining >= 1; i++)
                c.U8($"Bit {i}", v => v != 0 ? "ON" : "OFF", level: 1);
            return;
        }
        for (var i = 0; i < count && c.Remaining >= 2; i++)
            c.U16BE(area is null ? $"Palavra {i}" : $"{(area.Prefix == "CIO" ? string.Empty : area.Prefix)}{word - area.AddressOffset + i}",
                v => $"{(short)v} (0x{v:X4})", i == 0 ? "big-endian" : string.Empty, 1);
    }
}
