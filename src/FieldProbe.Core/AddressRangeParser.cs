using System.Globalization;

namespace FieldProbe.Core;

/// <summary>Interpreta faixas digitadas pelo usuário: "1-1000", "1-200, 500-600", "10" e, opcionalmente, "HR:0-99".</summary>
public static class AddressRangeParser
{
    public sealed record Range(string? Prefix, int Start, int End)
    {
        public int Count => End - Start + 1;
    }

    public static List<Range> Parse(string text, int min, int max, int maxTotal)
    {
        var ranges = new List<Range>();
        var total = 0;
        foreach (var raw in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? prefix = null;
            var part = raw;
            var colon = part.IndexOf(':');
            if (colon > 0)
            {
                prefix = part[..colon].Trim().ToUpperInvariant();
                part = part[(colon + 1)..].Trim();
            }

            var dash = part.IndexOfAny(new[] { '-', '–' });
            var start = ParseNumber(dash < 0 ? part : part[..dash]);
            var end = dash < 0 ? start : ParseNumber(part[(dash + 1)..]);
            if (end < start) (start, end) = (end, start);
            if (start < min || end > max)
                throw new FormatException($"Faixa '{raw}' fora do permitido ({min} a {max}).");

            total += end - start + 1;
            if (total > maxTotal)
                throw new FormatException($"Faixa grande demais: no máximo {maxTotal} endereços por busca.");
            ranges.Add(new Range(prefix, start, end));
        }

        if (ranges.Count == 0) throw new FormatException("Informe uma faixa, ex.: 1-1000.");
        return ranges;
    }

    private static int ParseNumber(string text) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException($"Número inválido na faixa: '{text.Trim()}'.");
}
