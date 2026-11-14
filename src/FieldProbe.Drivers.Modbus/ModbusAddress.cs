using System.Globalization;

namespace FieldProbe.Drivers.Modbus;

public enum ModbusArea
{
    Coil,
    DiscreteInput,
    InputRegister,
    HoldingRegister
}

/// <summary>
/// Endereço Modbus. Formatos aceitos:
///  - Prefixado, offset 0-based: "CO:0", "DI:5", "IR:10", "HR:100"
///  - Clássico, 1-based: 00001 (coil), 10001 (DI), 30001 (IR), 40001 (HR) e 6 dígitos (400001)
/// </summary>
public readonly record struct ModbusAddress(ModbusArea Area, ushort Offset)
{
    public bool IsBit => Area is ModbusArea.Coil or ModbusArea.DiscreteInput;

    public bool IsWritable => Area is ModbusArea.Coil or ModbusArea.HoldingRegister;

    public static string Prefix(ModbusArea area) => area switch
    {
        ModbusArea.Coil => "CO",
        ModbusArea.DiscreteInput => "DI",
        ModbusArea.InputRegister => "IR",
        _ => "HR"
    };

    public override string ToString() => $"{Prefix(Area)}:{Offset}";

    public static ModbusAddress Parse(string text)
    {
        var s = (text ?? string.Empty).Trim().ToUpperInvariant();
        if (s.Length == 0) throw new FormatException("Endereço vazio.");

        var colon = s.IndexOf(':');
        if (colon > 0)
        {
            var area = s[..colon] switch
            {
                "CO" or "C" or "0X" => ModbusArea.Coil,
                "DI" or "1X" => ModbusArea.DiscreteInput,
                "IR" or "3X" => ModbusArea.InputRegister,
                "HR" or "4X" => ModbusArea.HoldingRegister,
                var p => throw new FormatException($"Prefixo '{p}' inválido. Use CO, DI, IR ou HR.")
            };

            if (!ushort.TryParse(s[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset))
                throw new FormatException($"Offset inválido em '{text}' (0 a 65535).");

            return new ModbusAddress(area, offset);
        }

        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < 1)
            throw new FormatException($"Endereço '{text}' inválido. Ex.: HR:0, IR:10 ou 40001.");

        int areaDigit, oneBased;
        if (s.Length >= 6)
        {
            areaDigit = number / 100000;
            oneBased = number % 100000;
        }
        else
        {
            areaDigit = number / 10000;
            oneBased = number % 10000;
        }

        if (oneBased < 1 || oneBased > 65536)
            throw new FormatException($"Endereço '{text}' fora da faixa.");

        var classicArea = areaDigit switch
        {
            0 => ModbusArea.Coil,
            1 => ModbusArea.DiscreteInput,
            3 => ModbusArea.InputRegister,
            4 => ModbusArea.HoldingRegister,
            _ => throw new FormatException($"Endereço '{text}' inválido: o primeiro dígito deve ser 0, 1, 3 ou 4.")
        };

        return new ModbusAddress(classicArea, (ushort)(oneBased - 1));
    }
}
