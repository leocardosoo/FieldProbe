namespace FieldProbe.Drivers.Modbus;

/// <summary>Resposta de exceção do escravo Modbus (function code com bit 0x80).</summary>
public sealed class ModbusException : Exception
{
    public ModbusException(byte functionCode, byte exceptionCode)
        : base($"Exceção Modbus {exceptionCode:X2} em FC {functionCode:X2}: {Describe(exceptionCode)}")
    {
        FunctionCode = functionCode;
        ExceptionCode = exceptionCode;
    }

    public byte FunctionCode { get; }

    public byte ExceptionCode { get; }

    public static string Describe(byte code) => code switch
    {
        0x01 => "Illegal Function (função não suportada pelo device)",
        0x02 => "Illegal Data Address (endereço não existe no device)",
        0x03 => "Illegal Data Value (valor ou quantidade inválida)",
        0x04 => "Server Device Failure (falha interna do device)",
        0x05 => "Acknowledge (processando, tente depois)",
        0x06 => "Server Device Busy (device ocupado)",
        0x08 => "Memory Parity Error",
        0x0A => "Gateway Path Unavailable (gateway sem rota)",
        0x0B => "Gateway Target Failed to Respond (device atrás do gateway não respondeu; confira o Unit ID)",
        _ => "Código desconhecido"
    };
}
