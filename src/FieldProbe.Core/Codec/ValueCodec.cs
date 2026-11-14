using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FieldProbe.Core.Codec;

/// <summary>
/// Conversões entre bytes/registradores e valores tipados, reutilizadas pelos drivers.
/// Modbus usa registradores de 16 bits com ordem configurável; Siemens S7 é big-endian puro.
/// </summary>
public static class ValueCodec
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Tamanho em bytes de um tipo (strings: <paramref name="length"/> bytes).</summary>
    public static int ByteSize(DataType type, int length = 1) => type switch
    {
        DataType.Bool or DataType.Byte => 1,
        DataType.Int16 or DataType.UInt16 or DataType.Auto => 2,
        DataType.Int32 or DataType.UInt32 or DataType.Float32 => 4,
        DataType.Int64 or DataType.Float64 => 8,
        DataType.String => Math.Max(1, length),
        _ => 2
    };

    // ===================== Big-endian (S7 e base para Modbus) =====================

    public static object DecodeBigEndian(ReadOnlySpan<byte> bytes, DataType type) => type switch
    {
        DataType.Bool => bytes[0] != 0,
        DataType.Byte => bytes[0],
        DataType.Int16 => BinaryPrimitives.ReadInt16BigEndian(bytes),
        DataType.UInt16 or DataType.Auto => BinaryPrimitives.ReadUInt16BigEndian(bytes),
        DataType.Int32 => BinaryPrimitives.ReadInt32BigEndian(bytes),
        DataType.UInt32 => BinaryPrimitives.ReadUInt32BigEndian(bytes),
        DataType.Int64 => BinaryPrimitives.ReadInt64BigEndian(bytes),
        DataType.Float32 => BinaryPrimitives.ReadSingleBigEndian(bytes),
        DataType.Float64 => BinaryPrimitives.ReadDoubleBigEndian(bytes),
        DataType.String => Encoding.ASCII.GetString(bytes).TrimEnd('\0', ' '),
        _ => throw new NotSupportedException($"Tipo {type} não suportado.")
    };

    public static byte[] EncodeBigEndian(object value, DataType type, int length = 1)
    {
        byte[] bytes;
        switch (type)
        {
            case DataType.Bool:
                bytes = new[] { ParseBool(value) ? (byte)1 : (byte)0 };
                break;
            case DataType.Byte:
                bytes = new[] { Convert.ToByte(Normalize(value), Inv) };
                break;
            case DataType.Int16:
                bytes = new byte[2];
                BinaryPrimitives.WriteInt16BigEndian(bytes, Convert.ToInt16(Normalize(value), Inv));
                break;
            case DataType.UInt16:
            case DataType.Auto:
                bytes = new byte[2];
                BinaryPrimitives.WriteUInt16BigEndian(bytes, Convert.ToUInt16(Normalize(value), Inv));
                break;
            case DataType.Int32:
                bytes = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(bytes, Convert.ToInt32(Normalize(value), Inv));
                break;
            case DataType.UInt32:
                bytes = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(bytes, Convert.ToUInt32(Normalize(value), Inv));
                break;
            case DataType.Int64:
                bytes = new byte[8];
                BinaryPrimitives.WriteInt64BigEndian(bytes, Convert.ToInt64(Normalize(value), Inv));
                break;
            case DataType.Float32:
                bytes = new byte[4];
                BinaryPrimitives.WriteSingleBigEndian(bytes, Convert.ToSingle(Normalize(value), Inv));
                break;
            case DataType.Float64:
                bytes = new byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(bytes, Convert.ToDouble(Normalize(value), Inv));
                break;
            case DataType.String:
                var text = Convert.ToString(value, Inv) ?? string.Empty;
                bytes = new byte[Math.Max(1, length)];
                Encoding.ASCII.GetBytes(text, 0, Math.Min(text.Length, bytes.Length), bytes, 0);
                break;
            default:
                throw new NotSupportedException($"Tipo {type} não suportado.");
        }
        return bytes;
    }

    // ===================== Registradores de 16 bits (Modbus) =====================

    /// <summary>Quantidade de registradores. Strings: <paramref name="length"/> = nº de registradores (2 caracteres cada).</summary>
    public static int RegisterCount(DataType type, int length = 1) => type switch
    {
        DataType.Bool or DataType.Byte or DataType.Int16 or DataType.UInt16 or DataType.Auto => 1,
        DataType.Int32 or DataType.UInt32 or DataType.Float32 => 2,
        DataType.Int64 or DataType.Float64 => 4,
        DataType.String => Math.Max(1, length),
        _ => 1
    };

    public static object FromRegisters(ushort[] registers, DataType type, ByteOrder order)
    {
        var bytes = new byte[registers.Length * 2];
        for (var i = 0; i < registers.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2), registers[i]);

        ApplyOrder(bytes, order, type == DataType.String);

        return type switch
        {
            DataType.Bool => BinaryPrimitives.ReadUInt16BigEndian(bytes) != 0,
            DataType.Byte => (byte)(BinaryPrimitives.ReadUInt16BigEndian(bytes) & 0xFF),
            _ => DecodeBigEndian(bytes, type)
        };
    }

    public static ushort[] ToRegisters(object value, DataType type, ByteOrder order, int length = 1)
    {
        byte[] bytes = type switch
        {
            DataType.Bool => new byte[] { 0, ParseBool(value) ? (byte)1 : (byte)0 },
            DataType.Byte => new byte[] { 0, Convert.ToByte(Normalize(value), Inv) },
            DataType.String => EncodeBigEndian(value, type, Math.Max(1, length) * 2),
            _ => EncodeBigEndian(value, type)
        };

        ApplyOrder(bytes, order, type == DataType.String);

        var registers = new ushort[bytes.Length / 2];
        for (var i = 0; i < registers.Length; i++)
            registers[i] = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(i * 2));
        return registers;
    }

    /// <summary>
    /// Word swap inverte a ordem dos registradores; byte swap troca os bytes dentro de cada registrador.
    /// São involuções e comutam, então a mesma função serve para ler e escrever. Strings não sofrem word swap.
    /// </summary>
    public static void ApplyOrder(byte[] bytes, ByteOrder order, bool isString)
    {
        var wordSwap = !isString && order is ByteOrder.CDAB or ByteOrder.DCBA;
        var byteSwap = order is ByteOrder.BADC or ByteOrder.DCBA;

        if (byteSwap)
        {
            for (var i = 0; i + 1 < bytes.Length; i += 2)
                (bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
        }

        if (wordSwap)
        {
            var words = bytes.Length / 2;
            for (var i = 0; i < words / 2; i++)
            {
                var a = i * 2;
                var b = (words - 1 - i) * 2;
                (bytes[a], bytes[b]) = (bytes[b], bytes[a]);
                (bytes[a + 1], bytes[b + 1]) = (bytes[b + 1], bytes[a + 1]);
            }
        }
    }

    // ===================== Conversão de texto digitado =====================

    public static bool ParseBool(object value)
    {
        if (value is bool b) return b;
        var text = Convert.ToString(value, Inv)?.Trim().ToLowerInvariant();
        return text switch
        {
            "1" or "true" or "on" or "sim" or "verdadeiro" => true,
            "0" or "false" or "off" or "nao" or "não" or "falso" => false,
            _ => Convert.ToDouble(Normalize(value), Inv) != 0
        };
    }

    /// <summary>Converte o texto digitado para o tipo .NET informado (usado no OPC UA e EtherNet/IP).</summary>
    public static object ConvertTo(object value, Type target)
    {
        if (target.IsInstanceOfType(value)) return value;
        if (target == typeof(bool)) return ParseBool(value);
        if (target == typeof(string)) return Convert.ToString(value, Inv) ?? string.Empty;
        if (target == typeof(DateTime)) return DateTime.Parse(Convert.ToString(value, Inv) ?? string.Empty, CultureInfo.CurrentCulture);
        if (target.IsEnum) return Enum.Parse(target, Convert.ToString(value, Inv) ?? string.Empty, ignoreCase: true);
        return Convert.ChangeType(Normalize(value), target, Inv);
    }

    /// <summary>Converte para o <see cref="DataType"/> pedido (MQTT, parsing de payload).</summary>
    public static object ConvertTo(object value, DataType type) => type switch
    {
        DataType.Bool => ParseBool(value),
        DataType.Byte => Convert.ToByte(Normalize(value), Inv),
        DataType.Int16 => Convert.ToInt16(Normalize(value), Inv),
        DataType.UInt16 => Convert.ToUInt16(Normalize(value), Inv),
        DataType.Int32 => Convert.ToInt32(Normalize(value), Inv),
        DataType.UInt32 => Convert.ToUInt32(Normalize(value), Inv),
        DataType.Int64 => Convert.ToInt64(Normalize(value), Inv),
        DataType.Float32 => Convert.ToSingle(Normalize(value), Inv),
        DataType.Float64 => Convert.ToDouble(Normalize(value), Inv),
        DataType.String => Convert.ToString(value, Inv) ?? string.Empty,
        _ => value
    };

    /// <summary>Aceita vírgula como separador decimal ("12,5").</summary>
    public static object Normalize(object value) =>
        value is string s ? s.Trim().Replace(',', '.') : value;
}
