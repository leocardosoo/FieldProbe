using System.Globalization;
using System.Text.RegularExpressions;

namespace FieldProbe.Drivers.Omron;

/// <summary>Área de memória do CLP Omron (CS/CJ/CP/NJ em modo FINS) com os códigos de área de palavra e de bit.</summary>
/// <param name="Prefix">Como aparece no CX-Programmer (D, W, H, A, T, C, E0_...; CIO sem prefixo).</param>
/// <param name="WordCode">Código da área para acesso em palavras.</param>
/// <param name="BitCode">Código da área para acesso em bits (0 = não existe acesso por bit).</param>
/// <param name="Folder">Pasta da árvore.</param>
/// <param name="AddressOffset">Somado ao endereço (contadores ficam em 0x8000 na mesma área dos temporizadores).</param>
public sealed record FinsArea(string Prefix, byte WordCode, byte BitCode, string Folder, string Description, int AddressOffset = 0, int MaxAddress = 65535);

/// <summary>
/// Endereço Omron no formato do CX-Programmer: D100, D100.05, CIO100 (ou 100), 0.05, W10.03, H5, A100, T0, C0, E0_100.
/// Bit: ponto + número do bit de 0 a 15 (D100.05 = bit 5 do D100).
/// </summary>
public sealed record FinsAddress(FinsArea Area, int Word, int Bit)
{
    public static readonly FinsArea Dm = new("D", 0x82, 0x02, "D", "Memória de dados (DM)", MaxAddress: 32767);
    public static readonly FinsArea Cio = new("CIO", 0xB0, 0x30, "CIO", "Entradas, saídas e relés de trabalho (CIO)", MaxAddress: 6143);
    public static readonly FinsArea Work = new("W", 0xB1, 0x31, "W", "Área de trabalho (W)", MaxAddress: 511);
    public static readonly FinsArea Holding = new("H", 0xB2, 0x32, "H", "Área retentiva (H)", MaxAddress: 1535);
    public static readonly FinsArea Auxiliary = new("A", 0xB3, 0x33, "A", "Área auxiliar do sistema (A)", MaxAddress: 1471);
    public static readonly FinsArea Timer = new("T", 0x89, 0x00, "T", "Valor atual do temporizador (PV)", MaxAddress: 4095);
    public static readonly FinsArea Counter = new("C", 0x89, 0x00, "C", "Valor atual do contador (PV)", AddressOffset: 0x8000, MaxAddress: 4095);

    /// <summary>Banco da memória estendida (E0_ a EC_): palavra 0xA0 + banco, bit 0x20 + banco.</summary>
    public static FinsArea Em(int bank) =>
        new($"E{bank:X}_", (byte)(0xA0 + bank), (byte)(0x20 + bank), "E", $"Memória estendida, banco {bank:X}", MaxAddress: 32767);

    public static IEnumerable<FinsArea> KnownAreas() =>
        new[] { Dm, Cio, Work, Holding, Auxiliary, Timer, Counter }.Concat(Enumerable.Range(0, 13).Select(Em));

    public bool IsBit => Bit >= 0;

    /// <summary>Endereço enviado no pacote (contadores com o deslocamento 0x8000).</summary>
    public int WireAddress => Word + Area.AddressOffset;

    public override string ToString()
    {
        var word = Area == Cio ? Word.ToString(CultureInfo.InvariantCulture) : Area.Prefix + Word.ToString(CultureInfo.InvariantCulture);
        return IsBit ? $"{word}.{Bit:00}" : word;
    }

    public static FinsArea? FindByCode(byte code, int wireAddress, out bool isBit)
    {
        isBit = false;
        if (code == 0x89) return wireAddress >= 0x8000 ? Counter : Timer;
        foreach (var area in KnownAreas())
        {
            if (area.WordCode == code) return area;
            if (area.BitCode != 0 && area.BitCode == code)
            {
                isBit = true;
                return area;
            }
        }
        return null;
    }

    private static readonly Regex Pattern = new(
        @"^(?<prefix>DM|D|CIO|WR|W|HR|H|AR|A|TIM|T|CNT|C|E(?<bank>[0-9A-C])_|EM(?<bank2>[0-9A-C])_)?(?<word>\d{1,5})(\.(?<bit>\d{1,2}))?$",
        RegexOptions.CultureInvariant);

    public static bool TryParse(string text, out FinsAddress? address)
    {
        try
        {
            address = Parse(text);
            return true;
        }
        catch (FormatException)
        {
            address = null;
            return false;
        }
    }

    public static FinsAddress Parse(string text)
    {
        var s = (text ?? string.Empty).Trim().ToUpperInvariant().Replace(" ", string.Empty);
        if (s.Length == 0) throw new FormatException("Endereço vazio.");

        var m = Pattern.Match(s);
        if (!m.Success)
            throw new FormatException($"Endereço Omron inválido: '{text}'. Exemplos: D100, D100.05, CIO100 (ou 100), 0.05, W10.03, H5, A100, T0, C0, E0_100.");

        var prefix = m.Groups["prefix"].Value;
        var bank = m.Groups["bank"].Success ? m.Groups["bank"].Value : m.Groups["bank2"].Success ? m.Groups["bank2"].Value : null;
        var area = bank is not null
            ? Em(int.Parse(bank, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            : prefix switch
            {
                "D" or "DM" => Dm,
                "" or "CIO" => Cio,
                "W" or "WR" => Work,
                "H" or "HR" => Holding,
                "A" or "AR" => Auxiliary,
                "T" or "TIM" => Timer,
                "C" or "CNT" => Counter,
                _ => throw new FormatException($"Área '{prefix}' desconhecida.")
            };

        var word = int.Parse(m.Groups["word"].Value, CultureInfo.InvariantCulture);
        if (word > area.MaxAddress)
            throw new FormatException($"{area.Prefix}{word} está fora da faixa da área ({area.Description}: 0 a {area.MaxAddress}).");

        var bit = -1;
        if (m.Groups["bit"].Success)
        {
            if (area.BitCode == 0)
                throw new FormatException($"Temporizadores e contadores ({area.Prefix}) são lidos como valor atual: não use '.bit'.");
            bit = int.Parse(m.Groups["bit"].Value, CultureInfo.InvariantCulture);
            if (bit > 15) throw new FormatException($"Bit inválido em '{text}': de 00 a 15 (ex.: D100.05).");
        }

        return new FinsAddress(area, word, bit);
    }

    /// <summary>Pasta da árvore do endereço (D, CIO, W, H, A, T, C, E), ou nulo se não reconhecer.</summary>
    public static string? FolderOf(string text) => TryParse(text, out var a) ? a!.Area.Folder : null;
}
