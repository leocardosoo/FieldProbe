using System.Globalization;
using System.Text;

namespace FieldProbe.Core.Codec;

/// <summary>Uma forma possível de interpretar registradores brutos.</summary>
public sealed record TypeInterpretation(DataType Type, ByteOrder Order, string Value, bool Plausible, string Note, int Length = 1)
{
    public string OrderText => Type is DataType.UInt16 or DataType.Int16 or DataType.String ? (Order == ByteOrder.ABCD ? "-" : Order.ToString()) : Order.ToString();

    public string PlausibleText => Plausible ? "✔" : string.Empty;
}

/// <summary>
/// Mostra todas as interpretações de um conjunto de registradores (UInt16, Int16, Int32, Float32, Float64, String
/// em todas as ordens de byte) e marca as "prováveis". Resolve o clássico "o valor veio estranho" do Modbus.
/// </summary>
public static class TypeDetector
{
    private static readonly ByteOrder[] Orders = { ByteOrder.ABCD, ByteOrder.CDAB, ByteOrder.BADC, ByteOrder.DCBA };

    public static IReadOnlyList<TypeInterpretation> Interpret(ushort[] registers)
    {
        var c = CultureInfo.CurrentCulture;
        var list = new List<TypeInterpretation>();
        if (registers.Length == 0) return list;

        var r0 = registers[0];
        list.Add(new(DataType.UInt16, ByteOrder.ABCD, r0.ToString(c), true, "1 registrador, sem sinal (contadores, estados, valores inteiros)"));
        list.Add(new(DataType.Int16, ByteOrder.ABCD, ((short)r0).ToString(c), (short)r0 < 0, "1 registrador, com sinal (quando o valor pode ser negativo)"));
        list.Add(new(DataType.UInt16, ByteOrder.BADC, ((ushort)((r0 << 8) | (r0 >> 8))).ToString(c), false, "1 registrador com bytes trocados (raro)"));

        if (registers.Length >= 2)
        {
            var two = registers[..2];
            foreach (var order in Orders)
            {
                var f = (float)ValueCodec.FromRegisters(two, DataType.Float32, order);
                list.Add(new(DataType.Float32, order, FormatReal(f, c), IsPlausibleReal(f), "2 registradores, ponto flutuante · " + OrderNote(order)));
            }
            foreach (var order in Orders)
            {
                var i = (int)ValueCodec.FromRegisters(two, DataType.Int32, order);
                list.Add(new(DataType.Int32, order, i.ToString(c), Math.Abs((long)i) < 10_000_000 && (i < 0 || i > ushort.MaxValue), "2 registradores, inteiro 32 bits · " + OrderNote(order)));
            }
        }

        if (registers.Length >= 4)
        {
            var four = registers[..4];
            foreach (var order in Orders)
            {
                var d = (double)ValueCodec.FromRegisters(four, DataType.Float64, order);
                list.Add(new(DataType.Float64, order, FormatReal(d, c), IsPlausibleReal(d) && d != 0, "4 registradores, ponto flutuante 64 bits · " + OrderNote(order)));
            }
        }

        var text = AsciiText(registers);
        if (text is not null)
            list.Add(new(DataType.String, ByteOrder.ABCD, $"\"{text}\"", true, $"texto ASCII ({registers.Length} registradores)", registers.Length));

        return list;
    }

    private static string OrderNote(ByteOrder order) => order switch
    {
        ByteOrder.ABCD => "big-endian (padrão Modbus)",
        ByteOrder.CDAB => "word swap (muito comum em CLPs e medidores)",
        ByteOrder.BADC => "byte swap",
        _ => "little-endian"
    };

    private static bool IsPlausibleReal(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return false;
        var abs = Math.Abs(value);
        return abs == 0 || (abs >= 1e-3 && abs <= 1e7);
    }

    private static string FormatReal(double value, CultureInfo c) =>
        double.IsNaN(value) ? "NaN" : double.IsInfinity(value) ? "∞" : value.ToString("G7", c);

    private static string? AsciiText(ushort[] registers)
    {
        var bytes = new List<byte>();
        foreach (var r in registers)
        {
            bytes.Add((byte)(r >> 8));
            bytes.Add((byte)r);
        }

        var trimmed = bytes.TakeWhile(b => b != 0).ToList();
        if (trimmed.Count < 2 || trimmed.Any(b => b < 32 || b > 126)) return null;
        return Encoding.ASCII.GetString(trimmed.ToArray());
    }
}
