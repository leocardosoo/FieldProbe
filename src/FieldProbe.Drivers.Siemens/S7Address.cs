using System.Globalization;
using System.Text.RegularExpressions;
using FpDataType = FieldProbe.Core.DataType;
using S7Area = S7.Net.DataType;

namespace FieldProbe.Drivers.Siemens;

/// <summary>
/// Endereço absoluto S7. Exemplos:
///   DB1.DBX0.0  DB1.DBB2  DB1.DBW4  DB1.DBD6        (bloco de dados)
///   M10.0  MB10  MW10  MD10                          (marcadores)
///   I0.0 / E0.0  IB0  IW0  ID0                       (entradas)
///   Q0.0 / A0.0  QB0  QW0  QD0                       (saídas)
/// </summary>
public sealed record S7Address(S7Area Area, int Db, int ByteOffset, int Bit, char Size)
{
    private static readonly Regex DbPattern = new(@"^DB(\d+)\.DB([XBWD])(\d+)(?:\.([0-7]))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AreaPattern = new(@"^([MIEQA])([XBWD]?)(\d+)(?:\.([0-7]))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public bool IsBit => Size == 'X';

    /// <summary>Tipo sugerido pelo tamanho: X→Bool, B→Byte, W→Int16 (INT), D→Float32 (REAL).</summary>
    public FpDataType DefaultType => Size switch
    {
        'X' => FpDataType.Bool,
        'B' => FpDataType.Byte,
        'W' => FpDataType.Int16,
        _ => FpDataType.Float32
    };

    public static S7Address Parse(string text)
    {
        var s = (text ?? string.Empty).Trim().ToUpperInvariant().Replace(" ", string.Empty);

        var db = DbPattern.Match(s);
        if (db.Success)
        {
            var size = db.Groups[2].Value[0];
            return Build(S7Area.DataBlock, Int(db.Groups[1].Value), Int(db.Groups[3].Value), db.Groups[4], size, text);
        }

        var area = AreaPattern.Match(s);
        if (area.Success)
        {
            var kind = area.Groups[1].Value[0] switch
            {
                'M' => S7Area.Memory,
                'I' or 'E' => S7Area.Input,
                _ => S7Area.Output
            };
            var sizeText = area.Groups[2].Value;
            var size = sizeText.Length == 0 ? 'X' : sizeText[0];
            return Build(kind, 0, Int(area.Groups[3].Value), area.Groups[4], size, text);
        }

        throw new FormatException($"Endereço S7 inválido: '{text}'. Exemplos: DB1.DBW0, DB1.DBX0.0, MW10, M0.1, I0.0, QW2.");
    }

    private static S7Address Build(S7Area area, int db, int offset, Group bitGroup, char size, string original)
    {
        if (size == 'X')
        {
            if (!bitGroup.Success) throw new FormatException($"Endereço de bit precisa do número do bit (0-7): '{original}'. Ex.: DB1.DBX0.3");
            return new S7Address(area, db, offset, Int(bitGroup.Value), 'X');
        }
        if (bitGroup.Success) throw new FormatException($"Somente endereços de bit (X) levam '.bit': '{original}'.");
        return new S7Address(area, db, offset, 0, size);
    }

    private static int Int(string value) => int.Parse(value, CultureInfo.InvariantCulture);

    public override string ToString()
    {
        var prefix = Area switch
        {
            S7Area.DataBlock => $"DB{Db}.DB",
            S7Area.Memory => "M",
            S7Area.Input => "I",
            _ => "Q"
        };
        if (Area == S7Area.DataBlock) return IsBit ? $"{prefix}X{ByteOffset}.{Bit}" : $"{prefix}{Size}{ByteOffset}";
        return IsBit ? $"{prefix}{ByteOffset}.{Bit}" : $"{prefix}{Size}{ByteOffset}";
    }
}
