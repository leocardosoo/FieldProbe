using System.Buffers.Binary;
using System.Text;

namespace FieldProbe.Drivers.Modbus;

/// <summary>Funções Modbus sobre qualquer transporte (TCP, RTU serial, RTU sobre TCP).</summary>
public sealed class ModbusClient
{
    private readonly IModbusTransport _transport;

    public ModbusClient(IModbusTransport transport)
    {
        _transport = transport;
    }

    public async Task<ushort[]> ReadRegistersAsync(byte unit, byte function, ushort start, ushort count, CancellationToken ct)
    {
        if (count is 0 or > 125) throw new ArgumentOutOfRangeException(nameof(count), "Quantidade deve ser de 1 a 125 registradores.");

        var response = await _transport.TransactAsync(unit, Request(function, start, count), ct).ConfigureAwait(false);
        if (response.Length < 2 || response[1] != count * 2 || response.Length < 2 + count * 2)
            throw new InvalidDataException($"Resposta com tamanho inesperado (esperado {count * 2} bytes de dados).");

        var registers = new ushort[count];
        for (var i = 0; i < count; i++)
            registers[i] = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2 + i * 2));
        return registers;
    }

    public async Task<bool[]> ReadBitsAsync(byte unit, byte function, ushort start, ushort count, CancellationToken ct)
    {
        if (count is 0 or > 2000) throw new ArgumentOutOfRangeException(nameof(count), "Quantidade deve ser de 1 a 2000 bits.");

        var response = await _transport.TransactAsync(unit, Request(function, start, count), ct).ConfigureAwait(false);
        var byteCount = (count + 7) / 8;
        if (response.Length < 2 + byteCount || response[1] != byteCount)
            throw new InvalidDataException("Resposta de bits com tamanho inesperado.");

        var bits = new bool[count];
        for (var i = 0; i < count; i++)
            bits[i] = (response[2 + i / 8] & (1 << (i % 8))) != 0;
        return bits;
    }

    public Task WriteSingleCoilAsync(byte unit, ushort address, bool value, CancellationToken ct) =>
        _transport.TransactAsync(unit, Request(0x05, address, value ? (ushort)0xFF00 : (ushort)0x0000), ct);

    public Task WriteSingleRegisterAsync(byte unit, ushort address, ushort value, CancellationToken ct) =>
        _transport.TransactAsync(unit, Request(0x06, address, value), ct);

    public Task WriteMultipleRegistersAsync(byte unit, ushort start, ushort[] values, CancellationToken ct)
    {
        if (values.Length is 0 or > 123) throw new ArgumentOutOfRangeException(nameof(values), "Quantidade deve ser de 1 a 123 registradores.");

        var pdu = new byte[6 + values.Length * 2];
        pdu[0] = 0x10;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), start);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)values.Length);
        pdu[5] = (byte)(values.Length * 2);
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6 + i * 2), values[i]);
        return _transport.TransactAsync(unit, pdu, ct);
    }

    /// <summary>FC 43 / MEI 14: identificação básica do device (fabricante, código, revisão).</summary>
    public async Task<IReadOnlyList<(string Name, string Value)>> ReadDeviceIdentificationAsync(byte unit, CancellationToken ct)
    {
        var response = await _transport.TransactAsync(unit, new byte[] { 0x2B, 0x0E, 0x01, 0x00 }, ct).ConfigureAwait(false);
        if (response.Length < 7) throw new InvalidDataException("Resposta de identificação muito curta.");

        var result = new List<(string, string)>();
        var count = response[6];
        var pos = 7;
        for (var i = 0; i < count && pos + 2 <= response.Length; i++)
        {
            var id = response[pos];
            var len = response[pos + 1];
            pos += 2;
            if (pos + len > response.Length) break;
            var value = Encoding.ASCII.GetString(response, pos, len);
            pos += len;
            result.Add((id switch
            {
                0x00 => "Fabricante",
                0x01 => "Código do produto",
                0x02 => "Revisão",
                0x03 => "URL do fabricante",
                0x04 => "Nome do produto",
                0x05 => "Modelo",
                0x06 => "Aplicação",
                _ => $"Objeto 0x{id:X2}"
            }, value));
        }
        return result;
    }

    private static byte[] Request(byte function, ushort first, ushort second)
    {
        var pdu = new byte[5];
        pdu[0] = function;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), first);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), second);
        return pdu;
    }
}
