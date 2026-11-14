using System.Buffers.Binary;
using FieldProbe.Core;

namespace FieldProbe.Drivers.Modbus;

/// <summary>Decodificação legível de PDUs Modbus para o log de tráfego.</summary>
public static class ModbusDescriber
{
    public static string FunctionName(byte function) => function switch
    {
        0x01 => "Read Coils",
        0x02 => "Read Discrete Inputs",
        0x03 => "Read Holding Registers",
        0x04 => "Read Input Registers",
        0x05 => "Write Single Coil",
        0x06 => "Write Single Register",
        0x0F => "Write Multiple Coils",
        0x10 => "Write Multiple Registers",
        0x2B => "Read Device Identification",
        _ => "Função desconhecida"
    };

    public static string Describe(FrameDirection direction, byte unit, ReadOnlySpan<byte> pdu, string prefix)
    {
        if (pdu.Length == 0) return $"{prefix}Unit={unit} (vazio)";

        var function = pdu[0];
        if ((function & 0x80) != 0)
        {
            var code = pdu.Length > 1 ? pdu[1] : (byte)0;
            return $"{prefix}Unit={unit} EXCEÇÃO FC{function & 0x7F:X2}: {ModbusException.Describe(code)}";
        }

        var detail = string.Empty;
        if (direction == FrameDirection.Tx && pdu.Length >= 5 && function != 0x2B)
        {
            var first = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
            var second = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
            detail = function is 0x05 or 0x06 ? $" end={first} valor=0x{second:X4}" : $" início={first} qtd={second}";
        }
        else if (direction == FrameDirection.Rx && function is >= 0x01 and <= 0x04 && pdu.Length >= 2)
        {
            detail = $" {pdu[1]} bytes";
        }

        return $"{prefix}Unit={unit} FC{function:X2} {FunctionName(function)}{detail}";
    }

    /// <summary>Lança <see cref="ModbusException"/> para respostas de exceção e valida o function code.</summary>
    public static byte[] ValidateResponse(byte requestFunction, byte[] responsePdu)
    {
        if (responsePdu.Length == 0)
            throw new InvalidDataException("Resposta vazia.");

        var function = responsePdu[0];
        if ((function & 0x80) != 0)
            throw new ModbusException((byte)(function & 0x7F), responsePdu.Length > 1 ? responsePdu[1] : (byte)0);
        if (function != requestFunction)
            throw new InvalidDataException($"Function code divergente (enviado {requestFunction:X2}, recebido {function:X2}).");

        return responsePdu;
    }
}
