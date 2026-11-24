using System.Buffers.Binary;
using FieldProbe.Core;

namespace FieldProbe.Drivers.EtherNetIP;

/// <summary>Explica byte a byte um pacote EtherNet/IP: encapsulamento · itens CPF · mensagem CIP.</summary>
public static class EipFrameDecoder
{
    public static DecodedFrame Decode(byte[] data, FrameDirection direction) => FrameDecoding.Run("EtherNet/IP", data, c =>
    {
        c.Layer("EtherNet/IP · encapsulamento (24 bytes, little-endian)");
        var command = c.U16LE("Comando", v => $"0x{v:X4} ({CommandName(v)})");
        var length = c.U16LE("Tamanho dos dados", v => $"{v} bytes", "bytes depois deste cabeçalho de 24");
        c.U32LE("Session handle", FrameCursor.Hex32, "número da sessão aberta pelo RegisterSession (0 antes de registrar)");
        var status = c.U32LE("Status", v => v == 0 ? "0 (sucesso)" : $"0x{v:X8}", "");
        if (status != 0) c.Warn(CipErrors.DescribeEncapsulation(status));
        c.Bytes(8, "Sender context", note: "valor livre do cliente; volta igual na resposta");
        c.U32LE("Opções", v => v.ToString());
        if (length != c.Length - 24) c.Warn($"O cabeçalho diz {length} bytes de dados, mas o pacote tem {c.Length - 24}.");

        var request = direction == FrameDirection.Tx;
        switch (command)
        {
            case 0x0065:
                c.Layer("RegisterSession");
                if (c.Remaining >= 4)
                {
                    c.U16LE("Versão do protocolo", v => v.ToString());
                    c.U16LE("Opções", v => v.ToString());
                }
                c.Frame.Summary = request ? "RegisterSession · abrindo sessão" : $"RegisterSession · sessão aberta (handle 0x{BinaryPrimitives.ReadUInt32LittleEndian(c.Data.AsSpan(4)):X8})";
                return;
            case 0x0066:
                c.Frame.Summary = "UnRegisterSession · fechando a sessão";
                return;
            case 0x0063 when request || c.Remaining == 0:
                c.Frame.Summary = "ListIdentity · perguntando quem é o equipamento";
                return;
            case 0x0063:
                DecodeListIdentity(c);
                return;
            case 0x0004:
                c.Rest("Serviços");
                c.Frame.Summary = request ? "ListServices · pergunta" : "ListServices · resposta";
                return;
            case 0x006F or 0x0070:
                DecodeCpf(c, command, request);
                return;
            default:
                c.Rest("Dados");
                c.Frame.Summary = $"Comando 0x{command:X4}";
                return;
        }
    });

    private static void DecodeCpf(FrameCursor c, ushort command, bool request)
    {
        c.Layer(command == 0x006F ? "SendRRData · mensagem sem conexão" : "SendUnitData · mensagem conectada");
        c.U32LE("Interface handle", v => v == 0 ? "0 (CIP)" : v.ToString());
        c.U16LE("Timeout", v => $"{v} s");
        var items = c.U16LE("Quantidade de itens (CPF)", v => v.ToString());

        for (var i = 0; i < items && c.Remaining >= 4; i++)
        {
            var type = c.U16LE($"Item {i + 1} · tipo", t => $"0x{t:X4} ({ItemName(t)})", level: 1);
            var size = c.U16LE($"Item {i + 1} · tamanho", v => $"{v} bytes", level: 1);
            if (size == 0) continue;
            if (type == 0x00A1 && size == 4)
            {
                c.U32LE("Connection ID", FrameCursor.Hex32, "conexão criada pelo Forward Open", 1);
                continue;
            }
            if (type is 0x00B2 or 0x00B1)
            {
                var end = Math.Min(c.Position + size, c.Length);
                if (type == 0x00B1) c.U16LE("Sequência", v => v.ToString(), "conta as mensagens da conexão", 1);
                DecodeCip(c, end, request, "CIP");
                c.Position = end;
                continue;
            }
            c.Bytes(Math.Min(size, c.Remaining), $"Item {i + 1} · dados", level: 1);
        }
    }

    /// <summary>Mensagem CIP: serviço + caminho (pedido) ou serviço + status (resposta).</summary>
    private static void DecodeCip(FrameCursor c, int end, bool request, string title)
    {
        c.Layer(title + (request ? " · pedido" : " · resposta"));
        var service = c.U8("Serviço", s => $"0x{s:X2} ({ServiceName((byte)(s & 0x7F))}{((s & 0x80) != 0 ? " · resposta" : string.Empty)})");
        var code = (byte)(service & 0x7F);

        if ((service & 0x80) != 0)
        {
            c.U8("Reservado", FrameCursor.Hex8);
            var general = c.U8("Status geral", g => $"0x{g:X2}", CipErrors.Describe(c.Data[c.Position], null));
            var extSize = c.U8("Tamanho do status estendido", v => $"{v} word(s)");
            ushort? extended = null;
            for (var i = 0; i < extSize && c.Position + 2 <= end; i++)
            {
                var ext = c.U16LE("Status estendido", FrameCursor.Hex16, level: 1);
                extended ??= ext;
            }
            var description = CipErrors.Describe(general, extended);
            if (general != 0 && general != 0x06) c.Warn($"O equipamento respondeu com erro CIP: {description}.");

            if (code is 0x4C or 0x52 && general is 0 or 0x06 && end - c.Position >= 2 && title == "CIP")
            {
                var type = c.U16LE("Tipo de dado", t => $"0x{t:X4} ({CipErrors.TypeName(t)})");
                if (type == 0x02A0 && end - c.Position >= 2) c.U16LE("Handle da estrutura", FrameCursor.Hex16);
                if (end > c.Position) c.Bytes(end - c.Position, "Valor", ValuePreview(c, type, end - c.Position), "little-endian");
            }
            else if (end > c.Position)
            {
                c.Bytes(end - c.Position, "Dados da resposta");
            }
            c.Frame.Summary = $"CIP {ServiceName(code)} · resposta: {(general == 0 ? "sucesso" : description)}";
            return;
        }

        var pathWords = c.U8("Tamanho do caminho", v => $"{v} word(s) = {v * 2} bytes", "o caminho diz para qual objeto/tag vai o pedido");
        var pathEnd = Math.Min(c.Position + pathWords * 2, end);
        var path = DecodePath(c, pathEnd);
        c.Position = pathEnd;

        if (code == 0x52 && path.Contains("classe 0x06"))
        {
            // Unconnected Send ao Connection Manager: traz outra mensagem CIP dentro
            c.U8("Tick de tempo", v => $"{v}");
            c.U8("Timeout em ticks", v => $"{v}");
            var embedded = c.U16LE("Tamanho da mensagem embutida", v => $"{v} bytes");
            var embeddedEnd = Math.Min(c.Position + embedded, end);
            DecodeCip(c, embeddedEnd, request, "CIP embutido (destino final)");
            c.Position = embeddedEnd;
            if (embedded % 2 == 1 && c.Position < end) c.Field(1, "Preenchimento", "0x00", "alinha em word");
            if (c.Position + 2 <= end)
            {
                c.Layer("Unconnected Send · rota");
                var routeWords = c.U8("Tamanho da rota", v => $"{v} word(s)");
                c.U8("Reservado", FrameCursor.Hex8);
                if (routeWords > 0 && c.Position < end)
                {
                    var route = c.Data.AsSpan(c.Position, Math.Min(routeWords * 2, end - c.Position)).ToArray();
                    c.Bytes(route.Length, "Rota (Path)", FrameCursor.HexOf(route), RouteText(route));
                }
            }
            c.Frame.Summary = $"CIP Unconnected Send · {c.Frame.Summary}";
            return;
        }

        if (code is 0x4C or 0x52 && end - c.Position >= 2)
            c.U16LE("Quantidade de elementos", v => v.ToString());
        if (code == 0x52 && end - c.Position >= 4)
            c.U32LE("Deslocamento (bytes)", v => v.ToString(), "leitura fragmentada: a partir de qual byte");
        if (end > c.Position)
            c.Bytes(end - c.Position, "Dados do serviço");

        c.Frame.Summary = $"CIP {ServiceName(code)} · {path}";
    }

    private static string DecodePath(FrameCursor c, int pathEnd)
    {
        var parts = new List<string>();
        while (c.Position < pathEnd)
        {
            var segment = c.PeekU8();
            if (segment == 0x91)
            {
                var len = c.PeekU8(1);
                c.Field(2, "Segmento simbólico", $"0x91, {len} caracteres", "nome do tag (ANSI)", 1);
                var name = c.Ascii(Math.Min(len, pathEnd - c.Position), "Nome", level: 2);
                parts.Add(name);
                if (len % 2 == 1 && c.Position < pathEnd) c.Field(1, "Preenchimento", "0x00", level: 2);
                continue;
            }
            if ((segment & 0xE0) == 0x20)
            {
                var kind = (segment & 0x1C) >> 2;
                var wide = (segment & 0x03) == 1;
                var label = kind switch { 0 => "classe", 1 => "instância", 2 => "elemento", 3 => "ponto de conexão", 4 => "atributo", _ => "lógico" };
                int value;
                int size;
                if (wide && c.Position + 4 <= pathEnd)
                {
                    value = BinaryPrimitives.ReadUInt16LittleEndian(c.Data.AsSpan(c.Position + 2));
                    size = 4;
                }
                else
                {
                    value = c.PeekU8(1);
                    size = 2;
                }
                var known = kind == 0 ? ClassName(value) : string.Empty;
                c.Field(size, $"Segmento lógico · {label}", $"0x{value:X2}{(known.Length > 0 ? $" ({known})" : string.Empty)}", level: 1);
                parts.Add(kind == 2 ? $"[{value}]" : $"{label} 0x{value:X2}");
                continue;
            }
            if ((segment & 0xE0) == 0x00)
            {
                var port = segment & 0x0F;
                var link = c.PeekU8(1);
                c.Field(2, "Segmento de porta", $"porta {port}, endereço {link}", "porta 1 = backplane; o endereço é o slot", 1);
                parts.Add($"porta {port}/slot {link}");
                continue;
            }
            c.Bytes(pathEnd - c.Position, "Caminho", level: 1);
            break;
        }
        return string.Join(" · ", parts);
    }

    private static void DecodeListIdentity(FrameCursor c)
    {
        c.Layer("ListIdentity · resposta");
        c.U16LE("Quantidade de itens", v => v.ToString());
        c.U16LE("Tipo do item", t => $"0x{t:X4} (identidade CIP)");
        c.U16LE("Tamanho do item", v => $"{v} bytes");
        c.U16LE("Versão do encapsulamento", v => v.ToString());
        c.Bytes(16, "Endereço de socket", note: "família, porta TCP e IP (big-endian)");
        var vendor = c.U16LE("Fabricante (Vendor ID)", v => v switch { 1 => "1 (Rockwell Automation)", 2 => "2 (Namco)", 0x2C => "44 (Omron)", 0x11B => "283 (Schneider)", _ => v.ToString() });
        c.U16LE("Tipo de equipamento", t => t switch { 0x0E => "14 (controlador/CLP)", 0x0C => "12 (adaptador de comunicação)", 0x02 => "2 (inversor CA)", 0x2B => "43 (equipamento genérico)", _ => t.ToString() });
        c.U16LE("Código do produto", v => v.ToString());
        var major = c.U8("Revisão (maior)", v => v.ToString());
        var minor = c.U8("Revisão (menor)", v => v.ToString());
        c.U16LE("Status", s => $"0x{s:X4}", "bits de falha e estado do módulo");
        c.U32LE("Número de série", FrameCursor.Hex32);
        var nameLength = c.U8("Tamanho do nome", v => $"{v} caracteres");
        var name = c.Ascii(Math.Min(nameLength, c.Remaining), "Nome do produto");
        if (c.Remaining > 0) c.U8("Estado", s => s switch { 3 => "3 (operando)", 4 => "4 (falha recuperável)", 5 => "5 (falha grave)", 255 => "255 (padrão/não informado)", _ => s.ToString() });
        c.Frame.Summary = $"ListIdentity · {name} · revisão {major}.{minor:D3} · fabricante {vendor}";
    }

    private static string? ValuePreview(FrameCursor c, ushort type, int bytes)
    {
        var s = c.Data.AsSpan(c.Position, Math.Min(bytes, c.Remaining));
        return type switch
        {
            0x00C1 when s.Length >= 1 => s[0] != 0 ? "1 (verdadeiro)" : "0 (falso)",
            0x00C2 when s.Length >= 1 => ((sbyte)s[0]).ToString(),
            0x00C3 when s.Length >= 2 => BinaryPrimitives.ReadInt16LittleEndian(s).ToString(),
            0x00C4 when s.Length >= 4 => BinaryPrimitives.ReadInt32LittleEndian(s).ToString(),
            0x00CA when s.Length >= 4 => BinaryPrimitives.ReadSingleLittleEndian(s).ToString("0.####"),
            0x00CB when s.Length >= 8 => BinaryPrimitives.ReadDoubleLittleEndian(s).ToString("0.######"),
            _ => null
        };
    }

    private static string RouteText(byte[] route) =>
        route.Length >= 2 && (route[0] & 0xE0) == 0 ? $"porta {route[0] & 0x0F}, slot {route[1]} (o Path '{route[0] & 0x0F},{route[1]}' da conexão)" : string.Empty;

    private static string CommandName(ushort command) => command switch
    {
        0x0004 => "ListServices",
        0x0063 => "ListIdentity",
        0x0064 => "ListInterfaces",
        0x0065 => "RegisterSession",
        0x0066 => "UnRegisterSession",
        0x006F => "SendRRData · mensagem sem conexão",
        0x0070 => "SendUnitData · mensagem conectada",
        _ => "desconhecido"
    };

    private static string ItemName(ushort type) => type switch
    {
        0x0000 => "endereço nulo",
        0x000C => "identidade CIP",
        0x00A1 => "endereço conectado",
        0x00B1 => "dados conectados",
        0x00B2 => "dados sem conexão",
        0x8000 => "socket O→T",
        0x8001 => "socket T→O",
        _ => "desconhecido"
    };

    private static string ServiceName(byte service) => service switch
    {
        0x01 => "Get Attributes All",
        0x0A => "Multiple Service Packet",
        0x0E => "Get Attribute Single",
        0x10 => "Set Attribute Single",
        0x4C => "Read Tag",
        0x4D => "Write Tag",
        0x4E => "Forward Close",
        0x52 => "Read Tag Fragmented / Unconnected Send",
        0x53 => "Write Tag Fragmented",
        0x54 => "Forward Open",
        0x55 => "Get Instance Attribute List (lista de tags)",
        0x5B => "Large Forward Open",
        _ => $"serviço 0x{service:X2}"
    };

    private static string ClassName(int classId) => classId switch
    {
        0x01 => "Identity",
        0x02 => "Message Router",
        0x06 => "Connection Manager",
        0x6B => "Symbol (tags)",
        0x6C => "Template (estruturas)",
        0x8E => "Controller",
        _ => string.Empty
    };
}
