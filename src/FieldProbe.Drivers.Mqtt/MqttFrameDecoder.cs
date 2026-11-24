using System.Text;
using FieldProbe.Core;

namespace FieldProbe.Drivers.Mqtt;

/// <summary>Explica byte a byte um pacote MQTT 3.1.1: cabeçalho fixo, cabeçalho variável e payload.</summary>
public static class MqttFrameDecoder
{
    private static readonly string[] Types =
    {
        "reservado", "CONNECT", "CONNACK", "PUBLISH", "PUBACK", "PUBREC", "PUBREL", "PUBCOMP",
        "SUBSCRIBE", "SUBACK", "UNSUBSCRIBE", "UNSUBACK", "PINGREQ", "PINGRESP", "DISCONNECT", "AUTH"
    };

    public static DecodedFrame Decode(byte[] data) => FrameDecoding.Run("MQTT 3.1.1", data, c =>
    {
        c.Layer("MQTT · cabeçalho fixo");
        var header = c.PeekU8();
        var type = header >> 4;
        var qos = (header >> 1) & 0x03;
        var flags = type == 3
            ? $"DUP={(header >> 3) & 1}, QoS={qos}, RETAIN={header & 1}"
            : $"flags 0x{header & 0x0F:X}";
        c.Field(1, "Tipo e flags", $"0x{header:X2} = {Types[type]} · {flags}",
            type == 3 ? "QoS 0 = no máximo uma vez · 1 = pelo menos uma vez · 2 = exatamente uma vez; RETAIN = broker guarda a última mensagem" : string.Empty);

        // Comprimento restante: inteiro de tamanho variável (1 a 4 bytes, 7 bits por byte)
        var start = c.Position;
        var remaining = 0;
        var multiplier = 1;
        byte digit;
        do
        {
            digit = c.Data[c.Position++];
            remaining += (digit & 0x7F) * multiplier;
            multiplier *= 128;
        } while ((digit & 0x80) != 0 && c.Position - start < 4);
        c.AddAt(start, c.Position - start, "Comprimento restante", $"{remaining} bytes", "codificado em 1 a 4 bytes (7 bits cada)");
        if (remaining != c.Length - c.Position) c.Warn($"O cabeçalho diz {remaining} bytes, mas o pacote tem {c.Length - c.Position}.");

        switch (type)
        {
            case 1:
                DecodeConnect(c);
                break;
            case 2:
            {
                c.Layer("CONNACK");
                var session = c.U8("Flags", f => (f & 1) != 0 ? "sessão já existia no broker" : "sessão nova");
                var code = c.U8("Código de retorno", r => r switch
                {
                    0 => "0 (conexão aceita)",
                    1 => "1 (versão do protocolo não aceita)",
                    2 => "2 (Client ID recusado)",
                    3 => "3 (broker indisponível)",
                    4 => "4 (usuário ou senha incorretos)",
                    5 => "5 (não autorizado)",
                    _ => r.ToString()
                });
                if (code != 0) c.Warn("O broker recusou a conexão.");
                c.Frame.Summary = code == 0 ? $"CONNACK · conexão aceita{((session & 1) != 0 ? " (sessão existente)" : string.Empty)}" : $"CONNACK · recusada (código {code})";
                break;
            }
            case 3:
            {
                c.Layer("PUBLISH");
                var topicLength = c.U16BE("Tamanho do tópico", v => $"{v} bytes");
                var topic = Utf8(c, topicLength, "Tópico");
                if (qos > 0) c.U16BE("Packet ID", v => v.ToString(), "usado para confirmar a entrega (PUBACK/PUBREC)");
                var payload = c.Data.AsSpan(c.Position).ToArray();
                if (payload.Length > 0) c.Bytes(payload.Length, "Payload", Preview(payload), $"{payload.Length} bytes");
                c.Frame.Summary = $"PUBLISH · {topic} · QoS {qos}{((header & 1) != 0 ? " · retain" : string.Empty)} · {payload.Length} bytes";
                break;
            }
            case 4 or 5 or 6 or 7 or 11:
                c.Layer(Types[type]);
                var id = c.U16BE("Packet ID", v => v.ToString(), "confirma o pacote com este número");
                c.Frame.Summary = $"{Types[type]} · pacote {id}";
                break;
            case 8:
            {
                c.Layer("SUBSCRIBE");
                c.U16BE("Packet ID", v => v.ToString());
                var topics = new List<string>();
                while (c.Remaining >= 3)
                {
                    var length = c.U16BE("Tamanho do filtro", v => $"{v} bytes", level: 1);
                    var filter = Utf8(c, length, "Filtro de tópico", 1);
                    c.U8("QoS pedido", q => q.ToString(), level: 1);
                    topics.Add(filter);
                }
                c.Frame.Summary = $"SUBSCRIBE · {string.Join(", ", topics)}";
                break;
            }
            case 9:
            {
                c.Layer("SUBACK");
                c.U16BE("Packet ID", v => v.ToString());
                var refused = 0;
                while (c.Remaining > 0)
                {
                    var r = c.U8("Resultado", v => v == 0x80 ? "0x80 (inscrição recusada)" : $"QoS {v} concedido", level: 1);
                    if (r == 0x80) refused++;
                }
                if (refused > 0) c.Warn($"{refused} inscrição(ões) recusada(s) pelo broker (permissão/ACL).");
                c.Frame.Summary = refused > 0 ? $"SUBACK · {refused} recusada(s)" : "SUBACK · inscrição aceita";
                break;
            }
            case 10:
                c.Layer("UNSUBSCRIBE");
                c.U16BE("Packet ID", v => v.ToString());
                while (c.Remaining >= 2)
                {
                    var length = c.U16BE("Tamanho do filtro", v => $"{v} bytes", level: 1);
                    Utf8(c, length, "Filtro de tópico", 1);
                }
                c.Frame.Summary = "UNSUBSCRIBE";
                break;
            case 12:
                c.Frame.Summary = "PINGREQ · \"ainda estou aqui\" (mantém a conexão viva)";
                break;
            case 13:
                c.Frame.Summary = "PINGRESP · o broker respondeu ao ping";
                break;
            case 14:
                c.Frame.Summary = "DISCONNECT · desconexão normal";
                break;
            default:
                c.Rest("Dados");
                c.Frame.Summary = Types[type];
                break;
        }
    });

    private static void DecodeConnect(FrameCursor c)
    {
        c.Layer("CONNECT · cabeçalho variável");
        var nameLength = c.U16BE("Tamanho do nome do protocolo", v => $"{v} bytes");
        Utf8(c, nameLength, "Nome do protocolo");
        c.U8("Nível do protocolo", v => v switch { 4 => "4 (MQTT 3.1.1)", 5 => "5 (MQTT 5)", 3 => "3 (MQTT 3.1)", _ => v.ToString() });
        var flags = c.U8("Flags de conexão", f =>
        {
            var parts = new List<string>();
            if ((f & 0x80) != 0) parts.Add("usuário");
            if ((f & 0x40) != 0) parts.Add("senha");
            if ((f & 0x04) != 0) parts.Add($"last will (QoS {(f >> 3) & 3}{((f & 0x20) != 0 ? ", retain" : string.Empty)})");
            if ((f & 0x02) != 0) parts.Add("sessão limpa");
            return $"0x{f:X2} ({(parts.Count == 0 ? "nenhuma" : string.Join(", ", parts))})";
        });
        c.U16BE("Keep alive", v => $"{v} s", "se nada for enviado nesse tempo, o cliente manda PINGREQ");

        c.Layer("CONNECT · payload");
        var idLength = c.U16BE("Tamanho do Client ID", v => $"{v} bytes");
        var clientId = Utf8(c, idLength, "Client ID", 0, "identifica o cliente no broker: dois clientes com o mesmo ID derrubam um ao outro");
        if ((flags & 0x04) != 0 && c.Remaining >= 2)
        {
            var willTopic = c.U16BE("Tamanho do tópico do last will", v => $"{v} bytes");
            Utf8(c, willTopic, "Tópico do last will");
            var willLength = c.U16BE("Tamanho da mensagem do last will", v => $"{v} bytes");
            c.Bytes(Math.Min(willLength, c.Remaining), "Mensagem do last will");
        }
        var user = string.Empty;
        if ((flags & 0x80) != 0 && c.Remaining >= 2)
        {
            var userLength = c.U16BE("Tamanho do usuário", v => $"{v} bytes");
            user = Utf8(c, userLength, "Usuário");
        }
        if ((flags & 0x40) != 0 && c.Remaining >= 2)
        {
            var passwordLength = c.U16BE("Tamanho da senha", v => $"{v} bytes");
            c.Field(Math.Min(passwordLength, c.Remaining), "Senha", new string('•', Math.Min((int)passwordLength, 12)),
                "o MQTT sem TLS envia a senha em texto aberto: qualquer um na rede consegue ler");
            c.Warn("Este CONNECT leva usuário e senha. Sem TLS (porta 8883), eles trafegam em texto aberto.");
        }
        c.Frame.Summary = $"CONNECT · Client ID \"{clientId}\"{(user.Length > 0 ? $" · usuário {user}" : string.Empty)}";
    }

    private static string Utf8(FrameCursor c, int length, string name, int level = 0, string note = "")
    {
        var count = Math.Min(length, c.Remaining);
        var text = Encoding.UTF8.GetString(c.Data, c.Position, count);
        c.Field(count, name, $"\"{text}\"", note, level);
        return text;
    }

    private static string Preview(byte[] payload)
    {
        var shown = payload.AsSpan(0, Math.Min(payload.Length, 120));
        var printable = shown.ToArray().All(b => b is >= 0x20 and < 0x7F or 0x0A or 0x0D or 0x09);
        if (!printable) return FrameCursor.HexOf(payload, 32);
        var text = Encoding.UTF8.GetString(shown).Replace("\r", " ").Replace("\n", " ");
        return $"\"{text}{(payload.Length > 120 ? "…" : string.Empty)}\"";
    }
}
