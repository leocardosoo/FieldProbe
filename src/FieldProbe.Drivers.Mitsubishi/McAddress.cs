using System.Globalization;

namespace FieldProbe.Drivers.Mitsubishi;

/// <summary>Série da CPU: muda a numeração de X/Y (hexadecimal no Q/L/iQ-R, octal no iQ-F/FX5).</summary>
public enum McSeries
{
    QL,
    IqR,
    IqF
}

/// <summary>Dispositivo (área de memória) do CLP Mitsubishi e seu código no MC Protocol binário.</summary>
/// <param name="Symbol">Como aparece no GX Works (D, M, X...).</param>
/// <param name="Code">Código do dispositivo no frame binário.</param>
/// <param name="IsBit">Dispositivo de bit (M, X, Y...) ou de palavra de 16 bits (D, W, R...).</param>
/// <param name="Radix">Base da numeração: 10 (D100), 16 (X1F, W1A) ou 8 (X17 no iQ-F).</param>
/// <param name="Folder">Pasta da árvore onde as variáveis deste dispositivo ficam.</param>
public sealed record McDevice(string Symbol, byte Code, bool IsBit, int Radix, string Folder, string Description);

/// <summary>
/// Endereço Mitsubishi no formato do GX Works: D100, M10, X1F, Y20, W1A, B0, R200, TN0, CN0, SM400, SD203.
/// Bit de uma palavra: D100.5 (bit 0 a F).
/// </summary>
public sealed record McAddress(McDevice Device, int Number, int Bit, McSeries Series)
{
    public const int MaxNumber = 0xFFFFFF;

    /// <summary>Dispositivos suportados (os de 2 letras vêm antes para o "SM" não ser lido como "S" + "M").</summary>
    public static readonly IReadOnlyList<McDevice> Devices = new McDevice[]
    {
        new("SM", 0x91, true, 10, "SM", "Relé especial (estado do sistema)"),
        new("SD", 0xA9, false, 10, "SD", "Registrador especial (estado do sistema)"),
        new("SB", 0xA1, true, 16, "B", "Relé especial de link"),
        new("SW", 0xB5, false, 16, "W", "Registrador especial de link"),
        new("TS", 0xC1, true, 10, "T", "Contato do temporizador"),
        new("TC", 0xC0, true, 10, "T", "Bobina do temporizador"),
        new("TN", 0xC2, false, 10, "T", "Valor atual do temporizador"),
        new("SS", 0xC7, true, 10, "T", "Contato do temporizador retentivo"),
        new("SC", 0xC6, true, 10, "T", "Bobina do temporizador retentivo"),
        new("SN", 0xC8, false, 10, "T", "Valor atual do temporizador retentivo"),
        new("CS", 0xC4, true, 10, "C", "Contato do contador"),
        new("CC", 0xC3, true, 10, "C", "Bobina do contador"),
        new("CN", 0xC5, false, 10, "C", "Valor atual do contador"),
        new("X", 0x9C, true, 16, "X", "Entrada"),
        new("Y", 0x9D, true, 16, "Y", "Saída"),
        new("M", 0x90, true, 10, "M", "Relé interno"),
        new("L", 0x92, true, 10, "M", "Relé de trava (latch)"),
        new("F", 0x93, true, 10, "M", "Anunciador"),
        new("V", 0x94, true, 10, "M", "Relé de borda"),
        new("B", 0xA0, true, 16, "B", "Relé de link"),
        new("D", 0xA8, false, 10, "D", "Registrador de dados"),
        new("W", 0xB4, false, 16, "W", "Registrador de link"),
        new("R", 0xAF, false, 10, "R", "Registrador de arquivo"),
        new("Z", 0xCC, false, 10, "D", "Registrador de índice")
    };

    public bool IsWordBit => Bit >= 0;

    /// <summary>Base efetiva da numeração (X/Y são octais no iQ-F).</summary>
    public int Radix => RadixOf(Device, Series);

    public static int RadixOf(McDevice device, McSeries series) =>
        series == McSeries.IqF && device.Symbol is "X" or "Y" ? 8 : device.Radix;

    public static string FormatNumber(McDevice device, int number, McSeries series) => RadixOf(device, series) switch
    {
        16 => number.ToString("X", CultureInfo.InvariantCulture),
        8 => Convert.ToString(number, 8),
        _ => number.ToString(CultureInfo.InvariantCulture)
    };

    public override string ToString() =>
        Device.Symbol + FormatNumber(Device, Number, Series) + (IsWordBit ? "." + Bit.ToString("X", CultureInfo.InvariantCulture) : string.Empty);

    public static McDevice? FindByCode(byte code) => Devices.FirstOrDefault(d => d.Code == code);

    public static bool TryParse(string text, McSeries series, out McAddress? address)
    {
        try
        {
            address = Parse(text, series);
            return true;
        }
        catch (FormatException)
        {
            address = null;
            return false;
        }
    }

    public static McAddress Parse(string text, McSeries series)
    {
        var s = (text ?? string.Empty).Trim().ToUpperInvariant();
        if (s.Length == 0) throw new FormatException("Endereço vazio.");

        // Bit de palavra: D100.5 / W10.F
        var bit = -1;
        var dot = s.IndexOf('.');
        if (dot > 0)
        {
            var bitText = s[(dot + 1)..];
            if (!int.TryParse(bitText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bit) || bit is < 0 or > 15 || bitText.Length > 2)
                throw new FormatException($"Bit inválido em '{text}': use de 0 a F (ex.: D100.5, D100.F).");
            s = s[..dot];
        }

        foreach (var device in Devices)
        {
            if (!s.StartsWith(device.Symbol, StringComparison.Ordinal)) continue;
            var digits = s[device.Symbol.Length..];
            if (digits.Length == 0) continue;

            var radix = RadixOf(device, series);
            if (!TryParseNumber(digits, radix, out var number)) continue;

            if (number > MaxNumber) throw new FormatException($"Número fora da faixa em '{text}'.");
            if (bit >= 0 && device.IsBit)
                throw new FormatException($"'{device.Symbol}' já é um dispositivo de bit: use {device.Symbol}{digits} sem o '.{bit:X}'.");
            return new McAddress(device, number, bit, series);
        }

        throw new FormatException(
            $"Endereço Mitsubishi inválido: '{text}'. Exemplos: D100, M10, X1F, Y20, W1A, R200, TN0, CN0, SM400, SD203, D100.5" +
            (series == McSeries.IqF ? " (no iQ-F, X e Y são octais: X0 a X7, X10...)." : "."));
    }

    private static bool TryParseNumber(string digits, int radix, out int number)
    {
        number = 0;
        foreach (var ch in digits)
        {
            var value = ch switch
            {
                >= '0' and <= '9' => ch - '0',
                >= 'A' and <= 'F' => ch - 'A' + 10,
                _ => 99
            };
            if (value >= radix) return false;
            number = number * radix + value;
            if (number > MaxNumber) return true; // o chamador acusa "fora da faixa"
        }
        return true;
    }

    /// <summary>Pasta da árvore do endereço (D, M, X, Y, W, B, R, T, C, SM, SD), ou nulo se não reconhecer.</summary>
    public static string? FolderOf(string text, McSeries series) =>
        TryParse(text, series, out var address) ? address!.Device.Folder : null;
}
