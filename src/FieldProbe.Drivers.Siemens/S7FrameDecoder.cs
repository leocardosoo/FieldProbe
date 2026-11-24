using FieldProbe.Core;

namespace FieldProbe.Drivers.Siemens;

/// <summary>Explica byte a byte um pacote S7: TPKT · COTP · S7comm (Setup, Read/Write Var, leitura de SZL).</summary>
public static class S7FrameDecoder
{
    private static readonly Dictionary<byte, string> Areas = new()
    {
        [0x81] = "I (entradas)",
        [0x82] = "Q (saídas)",
        [0x83] = "M (marcadores)",
        [0x84] = "DB",
        [0x1C] = "C (contadores)",
        [0x1D] = "T (temporizadores)"
    };

    private static readonly Dictionary<byte, string> AreaPrefix = new()
    {
        [0x81] = "I", [0x82] = "Q", [0x83] = "M", [0x84] = "DB", [0x1C] = "C", [0x1D] = "T"
    };

    public static DecodedFrame Decode(byte[] data, FrameDirection direction) => FrameDecoding.Run("Siemens S7", data, c =>
    {
        // ---------- TPKT (RFC 1006) ----------
        c.Layer("TPKT (ISO-on-TCP)");
        c.U8("Versão", v => v == 3 ? "3" : $"{v} (deveria ser 3)");
        c.U8("Reservado", note: "sempre 0");
        var total = c.U16BE("Comprimento total", v => $"{v} bytes", "o pacote inteiro, incluindo este cabeçalho");
        if (total != c.Length) c.Warn($"O TPKT diz {total} bytes, mas o pacote tem {c.Length}.");

        // ---------- COTP (ISO 8073) ----------
        c.Layer("COTP (ISO 8073)");
        var cotpLength = c.U8("Comprimento do COTP", v => $"{v} bytes", "bytes do COTP depois deste campo");
        var pduType = c.U8("Tipo de PDU", t => t switch
        {
            0xE0 => "0xE0 (CR · pedido de conexão)",
            0xD0 => "0xD0 (CC · conexão aceita)",
            0xF0 => "0xF0 (DT · dados)",
            0x80 => "0x80 (DR · desconexão)",
            0x70 => "0x70 (ER · erro)",
            _ => $"0x{t:X2}"
        });

        if (pduType is 0xE0 or 0xD0)
        {
            c.U16BE("Referência de destino", FrameCursor.Hex16);
            c.U16BE("Referência de origem", FrameCursor.Hex16);
            c.U8("Classe", v => $"0x{v:X2}");
            var end = 5 + cotpLength; // TPKT (4) + byte de comprimento (1) + COTP
            while (c.Position + 2 <= end && c.Position + 2 <= c.Length)
            {
                var code = c.PeekU8();
                var len = c.PeekU8(1);
                var name = code switch { 0xC0 => "Tamanho do TPDU", 0xC1 => "TSAP de origem", 0xC2 => "TSAP de destino", _ => $"Parâmetro 0x{code:X2}" };
                c.U8(name + " · código", FrameCursor.Hex8, level: 1);
                c.U8(name + " · tamanho", v => $"{v} byte(s)", level: 1);
                if (code == 0xC2 && len == 2)
                {
                    var type = c.PeekU8();
                    var rackSlot = c.PeekU8(1);
                    c.Field(2, name, $"0x{type:X2}{rackSlot:X2}",
                        $"tipo {(type switch { 1 => "PG", 2 => "OP", 3 => "S7 básico", _ => type.ToString() })} · rack {rackSlot >> 5} / slot {rackSlot & 0x1F}", 1);
                }
                else if (code == 0xC0 && len == 1)
                {
                    var size = c.PeekU8();
                    c.Field(1, name, $"0x{size:X2} = {1 << size} bytes", level: 1);
                }
                else
                {
                    c.Bytes(len, name, level: 1);
                }
            }
            c.Frame.Summary = pduType == 0xE0
                ? "COTP Connection Request · pedindo conexão à CPU (rack/slot no TSAP de destino)"
                : "COTP Connection Confirm · a CPU aceitou a conexão";
            return;
        }

        if (pduType != 0xF0)
        {
            c.Rest("Dados do COTP");
            c.Frame.Summary = "COTP · " + (pduType == 0x80 ? "desconexão" : $"PDU 0x{pduType:X2}");
            return;
        }

        c.U8("Número do TPDU / EOT", v => (v & 0x80) != 0 ? $"0x{v:X2} (último fragmento)" : $"0x{v:X2} (há mais fragmentos)");
        if (c.Remaining == 0)
        {
            c.Frame.Summary = "COTP DT sem dados";
            return;
        }

        DecodeS7(c, direction);
    });

    private static void DecodeS7(FrameCursor c, FrameDirection direction)
    {
        c.Layer("S7comm · cabeçalho");
        c.U8("Protocol ID", v => v == 0x32 ? "0x32 (S7comm)" : $"0x{v:X2} (não é S7comm clássico)");
        var rosctr = c.U8("Tipo de mensagem (ROSCTR)", r => r switch
        {
            1 => "1 (Job · pedido)",
            2 => "2 (Ack)",
            3 => "3 (Ack_Data · resposta com dados)",
            7 => "7 (Userdata · funções de sistema/diagnóstico)",
            _ => r.ToString()
        });
        c.U16BE("Reservado", FrameCursor.Hex16);
        c.U16BE("Referência do PDU", note: "a resposta volta com o mesmo número");
        var paramLength = c.U16BE("Tamanho dos parâmetros", v => $"{v} bytes");
        var dataLength = c.U16BE("Tamanho dos dados", v => $"{v} bytes");
        if (rosctr is 2 or 3)
        {
            var errClass = c.U8("Classe de erro", v => v == 0 ? "0 (sem erro)" : $"0x{v:X2} ({ErrorClass(v)})");
            c.U8("Código de erro", v => $"0x{v:X2}");
            if (errClass != 0) c.Warn($"A CPU respondeu com erro: {ErrorClass(errClass)}.");
        }

        var paramStart = c.Position;
        var dataStart = paramStart + paramLength;

        if (rosctr == 7)
        {
            DecodeUserdata(c, paramLength, dataLength, dataStart);
            return;
        }

        if (paramLength == 0)
        {
            c.Frame.Summary = "S7comm · sem parâmetros";
            return;
        }

        c.Layer("S7comm · parâmetros");
        var function = c.U8("Função", f => f switch
        {
            0xF0 => "0xF0 (Setup Communication)",
            0x04 => "0x04 (Read Var · ler variáveis)",
            0x05 => "0x05 (Write Var · escrever variáveis)",
            0x1A => "0x1A (Request download)",
            0x1D => "0x1D (Start upload)",
            0x28 => "0x28 (PLC control · ex.: RUN)",
            0x29 => "0x29 (PLC stop)",
            _ => $"0x{f:X2}"
        });

        switch (function)
        {
            case 0xF0:
            {
                c.U8("Reservado", FrameCursor.Hex8);
                c.U16BE("Máx. pedidos paralelos (chamador)", v => v.ToString());
                c.U16BE("Máx. pedidos paralelos (chamado)", v => v.ToString());
                var pdu = c.U16BE("Tamanho do PDU", v => $"{v} bytes", "maior pacote S7 que os dois lados aceitam (S7-1200: 240 ou 480)");
                c.Frame.Summary = direction == FrameDirection.Tx
                    ? $"S7 Setup Communication · pedindo PDU de {pdu} bytes"
                    : $"S7 Setup Communication · CPU aceitou PDU de {pdu} bytes";
                return;
            }
            case 0x04 or 0x05:
            {
                var count = c.U8("Quantidade de itens", v => v.ToString());
                var isRead = function == 0x04;
                var items = new List<string>();
                if (rosctr == 1)
                {
                    for (var i = 0; i < count && c.Position < dataStart; i++)
                        items.Add(DecodeItemAddress(c, i));
                }
                c.Position = dataStart;
                if (dataLength > 0)
                {
                    c.Layer("S7comm · dados");
                    var errors = 0;
                    var errorTexts = new List<string>();
                    for (var i = 0; i < count && c.Remaining > 0; i++)
                    {
                        if (!isRead && rosctr == 3)
                        {
                            var rc = c.U8($"Item {i + 1} · resultado", ReturnCode, level: 1);
                            if (rc != 0xFF)
                            {
                                errors++;
                                errorTexts.Add($"item {i + 1} (byte {c.Position - 1}): {ReturnCode(rc)}");
                            }
                            continue;
                        }
                        var code = c.U8($"Item {i + 1} · resultado", ReturnCode, level: 1);
                        if (code != 0xFF && code != 0x00)
                        {
                            errors++;
                            errorTexts.Add($"item {i + 1} (byte {c.Position - 1}): {ReturnCode(code)}");
                        }
                        var transport = c.U8($"Item {i + 1} · tipo de transporte", Transport, level: 1);
                        var size = c.U16BE($"Item {i + 1} · tamanho", v => (transport is 0x03 or 0x04 or 0x05) ? $"{v} bits" : $"{v} bytes", level: 1);
                        var bytes = (transport is 0x03 or 0x04 or 0x05) ? (size + 7) / 8 : size;
                        if (code == 0xFF || (rosctr == 1 && !isRead))
                        {
                            if (bytes > 0) c.Bytes(Math.Min(bytes, c.Remaining), $"Item {i + 1} · valor", ValuePreview(c, bytes), level: 1);
                            if (bytes % 2 == 1 && i < count - 1 && c.Remaining > 0) c.Field(1, "Preenchimento", "0x00", "alinha o próximo item em byte par", 1);
                        }
                    }
                    foreach (var text in errorTexts.Take(5)) c.Warn($"Erro no {text}");
                    if (errorTexts.Count > 5) c.Warn($"… e mais {errorTexts.Count - 5} item(ns) com erro (veja os campos 'resultado' na seção S7comm · dados).");
                    c.Frame.Summary = $"S7 {(isRead ? "Read Var" : "Write Var")} · resposta com {count} item(ns){(errors > 0 ? $", {errors} com erro" : string.Empty)}";
                    return;
                }
                c.Frame.Summary = rosctr == 1
                    ? $"S7 {(isRead ? "Read Var" : "Write Var")} · {count} item(ns): {string.Join(", ", items.Take(4))}{(items.Count > 4 ? ", …" : string.Empty)}"
                    : $"S7 {(isRead ? "Read Var" : "Write Var")} · confirmação de {count} item(ns)";
                return;
            }
            default:
                if (c.Position < dataStart) c.Bytes(Math.Min(dataStart - c.Position, c.Remaining), "Parâmetros", level: 1);
                c.Frame.Summary = $"S7comm · função 0x{function:X2}";
                return;
        }
    }

    /// <summary>Item de endereço do Read/Write Var (12 bytes: área, DB, byte.bit, tamanho).</summary>
    private static string DecodeItemAddress(FrameCursor c, int index)
    {
        var p = $"Item {index + 1}";
        c.U8($"{p} · especificação", v => v == 0x12 ? "0x12 (variável)" : $"0x{v:X2}", level: 1);
        var len = c.U8($"{p} · tamanho do item", v => $"{v} bytes", level: 1);
        var syntax = c.U8($"{p} · sintaxe", v => v == 0x10 ? "0x10 (S7ANY: área + endereço)" : $"0x{v:X2}", level: 1);
        if (syntax != 0x10)
        {
            if (len > 2) c.Bytes(Math.Min(len - 2, c.Remaining), $"{p} · endereço", level: 1);
            return $"item {index + 1}";
        }
        var transport = c.U8($"{p} · tipo", v => v switch
        {
            0x01 => "0x01 (BIT)", 0x02 => "0x02 (BYTE)", 0x03 => "0x03 (CHAR)", 0x04 => "0x04 (WORD)",
            0x05 => "0x05 (INT)", 0x06 => "0x06 (DWORD)", 0x07 => "0x07 (DINT)", 0x08 => "0x08 (REAL)",
            _ => $"0x{v:X2}"
        }, level: 1);
        var count = c.U16BE($"{p} · quantidade", v => v.ToString(), level: 1);
        var db = c.U16BE($"{p} · número do DB", v => v == 0 ? "0 (não é DB)" : v.ToString(), level: 1);
        var area = c.U8($"{p} · área", a => Areas.TryGetValue(a, out var n) ? $"0x{a:X2} = {n}" : $"0x{a:X2}", level: 1);
        var b0 = c.PeekU8();
        var b1 = c.PeekU8(1);
        var b2 = c.PeekU8(2);
        var bitAddress = (b0 << 16) | (b1 << 8) | b2;
        var byteOffset = bitAddress >> 3;
        var bit = bitAddress & 7;
        var address = FormatAddress(area, db, byteOffset, bit, transport, count);
        c.Field(3, $"{p} · endereço", $"bit {bitAddress} = byte {byteOffset}.{bit}", $"→ {address}", 1);
        return address;
    }

    private static string FormatAddress(byte area, ushort db, int byteOffset, int bit, byte transport, ushort count)
    {
        var prefix = AreaPrefix.TryGetValue(area, out var p) ? p : $"área 0x{area:X2}";
        if (transport == 0x01)
            return area == 0x84 ? $"DB{db}.DBX{byteOffset}.{bit}" : $"{prefix}{byteOffset}.{bit}";
        var size = transport switch { 0x04 or 0x05 => 2, 0x06 or 0x07 or 0x08 => 4, _ => 1 } * count;
        var letter = size switch { 2 => "W", 4 => "D", _ => "B" };
        var suffix = (size is 1 or 2 or 4) ? string.Empty : $" ({size} bytes)";
        return area == 0x84 ? $"DB{db}.DB{letter}{byteOffset}{suffix}" : $"{prefix}{letter}{byteOffset}{suffix}";
    }

    private static void DecodeUserdata(FrameCursor c, ushort paramLength, ushort dataLength, int dataStart)
    {
        c.Layer("S7comm · parâmetros (Userdata)");
        c.Bytes(3, "Cabeçalho de parâmetros", note: "00 01 12 = parâmetros de Userdata");
        c.U8("Tamanho", v => $"{v} bytes");
        c.U8("Método", v => v switch { 0x11 => "0x11 (pedido)", 0x12 => "0x12 (resposta)", _ => $"0x{v:X2}" });
        var typeGroup = c.U8("Tipo / grupo de função", v =>
        {
            var type = (v >> 4) switch { 4 => "pedido", 8 => "resposta", 0 => "push", _ => (v >> 4).ToString() };
            var group = (v & 0x0F) switch
            {
                1 => "modo de programação", 2 => "dados cíclicos", 3 => "blocos", 4 => "CPU (SZL e diagnóstico)",
                5 => "segurança", 7 => "relógio", _ => (v & 0x0F).ToString()
            };
            return $"0x{v:X2} ({type} · {group})";
        });
        var sub = c.U8("Subfunção", v => (typeGroup & 0x0F) == 4 && v == 1 ? "0x01 (Read SZL · ler lista de estado do sistema)" : $"0x{v:X2}");
        c.U8("Sequência", v => v.ToString());
        if (c.Position < dataStart) c.Bytes(Math.Min(dataStart - c.Position, c.Remaining), "Restante dos parâmetros", level: 1);
        c.Position = dataStart;

        if (dataLength == 0 || c.Remaining == 0)
        {
            c.Frame.Summary = "S7 Userdata · sem dados";
            return;
        }

        c.Layer("S7comm · dados (Userdata)");
        var rc = c.U8("Resultado", ReturnCode);
        c.U8("Tipo de transporte", Transport);
        var length = c.U16BE("Tamanho", v => $"{v} bytes");
        if ((typeGroup & 0x0F) != 4 || sub != 1 || c.Remaining < 4)
        {
            c.Rest("Dados");
            c.Frame.Summary = $"S7 Userdata · grupo {typeGroup & 0x0F}, subfunção 0x{sub:X2}";
            return;
        }

        var szl = c.U16BE("ID da SZL", v => $"0x{v:X4} ({SzlName(v)})");
        var index = c.U16BE("Índice", v => $"0x{v:X4}");
        if (rc != 0xFF || length <= 4 || c.Remaining < 4)
        {
            c.Frame.Summary = $"S7 Read SZL · pedido da SZL 0x{szl:X4} ({SzlName(szl)}), índice 0x{index:X4}";
            return;
        }

        var recordLength = c.U16BE("Tamanho de cada registro", v => $"{v} bytes");
        var records = c.U16BE("Quantidade de registros", v => v.ToString());
        for (var i = 0; i < records && c.Remaining > 0; i++)
        {
            var take = Math.Min(recordLength, c.Remaining);
            var preview = (szl is 0x0011 or 0x001C) ? $"\"{FrameCursor.Printable(c.Data.AsSpan(c.Position + 2, Math.Max(0, Math.Min(take - 2, 32))))}\"" : null;
            c.Bytes(take, $"Registro {i + 1}", preview, level: 1);
        }
        c.Frame.Summary = $"S7 Read SZL · resposta da SZL 0x{szl:X4} ({SzlName(szl)}), {records} registro(s)";
    }

    private static string SzlName(ushort id) => id switch
    {
        0x0011 => "identificação do módulo: código de pedido e firmware",
        0x001C => "identificação: nome, número de série",
        0x0424 => "modo de operação da CPU (RUN/STOP)",
        0x0132 => "estado de comunicação/proteção",
        0x0074 => "LEDs da CPU",
        0x00A0 or 0x01A0 => "buffer de diagnóstico",
        _ => "lista de estado do sistema"
    };

    private static string ReturnCode(byte code) => code switch
    {
        0xFF => "0xFF (OK)",
        0x00 => "0x00 (reservado/pedido)",
        0x01 => "0x01 (erro de hardware)",
        0x03 => "0x03 (acesso negado: DB otimizado ou PUT/GET desabilitado)",
        0x05 => "0x05 (endereço além do tamanho do DB/área)",
        0x06 => "0x06 (tipo de dado não suportado)",
        0x07 => "0x07 (tipo de dado inconsistente)",
        0x0A => "0x0A (objeto não existe: DB inexistente)",
        _ => $"0x{code:X2}"
    };

    private static string Transport(byte t) => t switch
    {
        0x00 => "0x00 (nenhum)",
        0x03 => "0x03 (BIT, tamanho em bits)",
        0x04 => "0x04 (BYTE/WORD/DWORD, tamanho em bits)",
        0x05 => "0x05 (INTEGER, tamanho em bits)",
        0x07 => "0x07 (REAL)",
        0x09 => "0x09 (OCTET STRING, tamanho em bytes)",
        _ => $"0x{t:X2}"
    };

    private static string ErrorClass(byte v) => v switch
    {
        0x81 => "erro na aplicação",
        0x82 => "erro no objeto",
        0x83 => "sem recursos",
        0x84 => "erro de serviço",
        0x85 => "erro de fornecimento",
        0x87 => "erro de acesso",
        _ => $"classe 0x{v:X2}"
    };

    /// <summary>Valor lido em formatos comuns (1, 2 ou 4 bytes).</summary>
    private static string? ValuePreview(FrameCursor c, int bytes)
    {
        if (c.Remaining < bytes) return null;
        var s = c.Data.AsSpan(c.Position, bytes);
        return bytes switch
        {
            1 => $"0x{s[0]:X2} = {s[0]} (bits {Convert.ToString(s[0], 2).PadLeft(8, '0')})",
            2 => $"{FrameCursor.HexOf(s)} = Int {(short)((s[0] << 8) | s[1])}",
            4 => $"{FrameCursor.HexOf(s)} = DInt {(s[0] << 24) | (s[1] << 16) | (s[2] << 8) | s[3]} · Real {BitConverter.Int32BitsToSingle((s[0] << 24) | (s[1] << 16) | (s[2] << 8) | s[3]):0.####}",
            _ => null
        };
    }
}
