using FieldProbe.Core;

namespace FieldProbe.Drivers.Mitsubishi;

/// <summary>Explica byte a byte um pacote MC Protocol (SLMP) frame 3E binário: pedido (50 00) ou resposta (D0 00).</summary>
public static class McFrameDecoder
{
    public static DecodedFrame Decode(byte[] data, McSeries series) => FrameDecoding.Run("Mitsubishi MC 3E", data, c =>
    {
        var request = data[0] == 0x50;
        c.Layer(request ? "MC 3E · cabeçalho (pedido)" : "MC 3E · cabeçalho (resposta)");
        c.Field(2, "Subcabeçalho", request ? "50 00" : "D0 00", request ? "pedido do frame 3E binário" : "resposta do frame 3E binário");
        c.U8("Nº da rede", v => v.ToString(), "0 = a própria CPU (outro valor: estação em outra rede)");
        c.U8("Nº do PC", v => v == 0xFF ? "255 (0xFF)" : v.ToString(), "0xFF = a CPU conectada");
        c.U16LE("Módulo de destino", v => FrameCursor.Hex16(v), "0x03FF = CPU de controle");
        c.U8("Estação do módulo", note: "0 = sem multidrop");
        var length = c.U16LE("Comprimento", v => $"{v} bytes", request ? "do timer até o fim do pedido" : "do código de término até o fim");
        if (length != c.Remaining) c.Warn($"O campo Comprimento diz {length} bytes, mas o pacote tem {c.Remaining} depois dele.");

        if (request) DecodeRequest(c, series);
        else DecodeResponse(c);
    });

    private static string CommandName(ushort command) => command switch
    {
        McClient.CmdBatchRead => "Leitura em lote (Batch Read)",
        McClient.CmdBatchWrite => "Escrita em lote (Batch Write)",
        McClient.CmdReadCpuModel => "Leitura do modelo da CPU",
        0x0403 => "Leitura aleatória (Random Read)",
        0x1402 => "Escrita aleatória (Random Write)",
        0x1001 => "Remote RUN",
        0x1002 => "Remote STOP",
        0x1003 => "Remote PAUSE",
        0x1005 => "Remote latch clear",
        0x1006 => "Remote RESET",
        0x0619 => "Teste de loopback",
        _ => "comando desconhecido"
    };

    private static void DecodeRequest(FrameCursor c, McSeries series)
    {
        c.U16LE("Timer de monitoração", v => $"{v} × 250 ms = {v * 0.25:0.##} s", "quanto tempo a CPU espera para responder");

        c.Layer("MC · comando");
        var command = c.U16LE("Comando", v => $"{FrameCursor.Hex16(v)} {CommandName(v)}");
        var sub = c.U16LE("Subcomando", v => v switch
        {
            McClient.SubWord => "0x0000 (em palavras de 16 bits)",
            McClient.SubBit => "0x0001 (em bits)",
            _ => FrameCursor.Hex16(v)
        });

        if (command is McClient.CmdBatchRead or McClient.CmdBatchWrite)
        {
            c.Layer("MC · dispositivo");
            var start = c.Position;
            var number = c.Data[start] | (c.Data[start + 1] << 8) | (c.Data[start + 2] << 16);
            var device = McAddress.FindByCode(c.Data[start + 3]);
            var name = device is null ? $"dispositivo 0x{c.Data[start + 3]:X2}" : device.Symbol + McAddress.FormatNumber(device, number, series);
            c.Field(3, "Nº do dispositivo", number.ToString(), $"3 bytes little-endian → {name}");
            c.U8("Código do dispositivo", v => device is null ? FrameCursor.Hex8(v) : $"{FrameCursor.Hex8(v)} = {device.Symbol} ({device.Description})");
            var points = c.U16LE("Quantidade de pontos", v => sub == McClient.SubBit ? $"{v} bit(s)" : $"{v} palavra(s)");

            var isWrite = command == McClient.CmdBatchWrite;
            c.Frame.Summary = $"Pedido · {(isWrite ? "Escrita" : "Leitura")} {name}, {points} {(sub == McClient.SubBit ? "bit(s)" : "palavra(s)")}";

            if (isWrite && c.Remaining > 0)
            {
                c.Layer("MC · dados escritos");
                if (sub == McClient.SubBit) DecodeBits(c, points, name);
                else DecodeWords(c, points, device, number, series);
            }
            return;
        }

        c.Frame.Summary = $"Pedido · {CommandName(command)}";
    }

    private static void DecodeResponse(FrameCursor c)
    {
        c.Layer("MC · resultado");
        var end = c.U16LE("Código de término", v => v == 0 ? "0x0000 (OK)" : FrameCursor.Hex16(v), McException.Describe(c.PeekU16LE()));

        if (end != 0)
        {
            c.Warn($"O CLP recusou o pedido: 0x{end:X4} · {McException.Describe(end)}");
            c.Frame.Summary = $"Resposta · ERRO 0x{end:X4}";
            if (c.Remaining >= 9)
            {
                c.Layer("MC · informação do erro");
                c.U8("Nº da rede");
                c.U8("Nº do PC");
                c.U16LE("Módulo de destino", v => FrameCursor.Hex16(v));
                c.U8("Estação do módulo");
                c.U16LE("Comando que falhou", v => $"{FrameCursor.Hex16(v)} {CommandName(v)}");
                c.U16LE("Subcomando", v => FrameCursor.Hex16(v));
            }
            return;
        }

        var size = c.Remaining;
        c.Frame.Summary = size == 0 ? "Resposta · OK (escrita confirmada)" : $"Resposta · OK, {size} byte(s) de dados";
        if (size == 0) return;

        c.Layer("MC · dados lidos");
        // A resposta não diz o que foi pedido: mostra como palavras (e como texto, se for o modelo da CPU)
        if (size >= 16 && size <= 18 && FrameCursor.Printable(c.Data.AsSpan(c.Position, 16)).Trim('·', ' ').Length >= 3)
        {
            c.Ascii(16, "Modelo da CPU");
            if (c.Remaining >= 2) c.U16LE("Código do modelo", v => FrameCursor.Hex16(v));
            return;
        }
        if (size % 2 != 0 || size > 64)
        {
            c.Rest("Dados", size % 2 != 0 ? "leitura em bits: cada byte traz 2 pontos (4 bits altos = 1º ponto)" : "palavras little-endian");
            return;
        }
        var index = 0;
        while (c.Remaining >= 2)
        {
            c.U16LE($"Palavra {index}", v => $"{(short)v} (0x{v:X4})", index == 0 ? "palavras de 16 bits, little-endian; 32 bits = palavra baixa primeiro" : string.Empty, 1);
            index++;
        }
    }

    private static void DecodeWords(FrameCursor c, int points, McDevice? device, int number, McSeries series)
    {
        for (var i = 0; i < points && c.Remaining >= 2; i++)
        {
            var label = device is null ? $"Palavra {i}" : device.Symbol + McAddress.FormatNumber(device, number + i, series);
            c.U16LE(label, v => $"{(short)v} (0x{v:X4})", i == 0 ? "little-endian" : string.Empty, 1);
        }
    }

    private static void DecodeBits(FrameCursor c, int points, string first)
    {
        for (var i = 0; i < points && c.Remaining >= 1; i += 2)
        {
            var b = c.Data[c.Position];
            var text = $"{((b >> 4) != 0 ? "ON" : "OFF")}" + (i + 1 < points ? $", {((b & 0x0F) != 0 ? "ON" : "OFF")}" : string.Empty);
            c.Field(1, i == 0 ? first : $"Pontos {i}–{i + 1}", text, i == 0 ? "4 bits altos = 1º ponto, 4 bits baixos = 2º" : string.Empty, 1);
        }
    }
}
