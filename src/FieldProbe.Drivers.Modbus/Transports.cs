using System.Buffers.Binary;
using System.IO.Ports;
using System.Net.Sockets;
using FieldProbe.Core;

namespace FieldProbe.Drivers.Modbus;

/// <summary>Camada de transporte Modbus: recebe uma PDU e devolve a PDU da resposta.</summary>
public interface IModbusTransport : IDisposable
{
    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken ct);

    /// <summary>Envia a PDU (function code + dados) e retorna a PDU da resposta. Lança <see cref="ModbusException"/>.</summary>
    Task<byte[]> TransactAsync(byte unitId, byte[] pdu, CancellationToken ct);

    /// <summary>Frame bruto + descrição decodificada.</summary>
    event Action<FrameDirection, byte[], string>? Frame;
}

// =====================================================================================
//  Modbus TCP (cabeçalho MBAP)
// =====================================================================================

public sealed class ModbusTcpTransport : IModbusTransport
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _host;
    private readonly int _port;
    private readonly int _timeoutMs;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private ushort _transactionId;

    public ModbusTcpTransport(string host, int port, int timeoutMs)
    {
        _host = host;
        _port = port;
        _timeoutMs = Math.Max(100, timeoutMs);
    }

    public event Action<FrameDirection, byte[], string>? Frame;

    public bool IsConnected => _tcp?.Connected == true && _stream is not null;

    public async Task ConnectAsync(CancellationToken ct)
    {
        Close();
        _tcp = await TcpHelper.ConnectAsync(_host, _port, _timeoutMs, ct).ConfigureAwait(false);
        _stream = _tcp.GetStream();
    }

    public async Task<byte[]> TransactAsync(byte unitId, byte[] pdu, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stream = _stream ?? throw new IOException("Não conectado.");
            var tid = unchecked(++_transactionId);

            var adu = new byte[7 + pdu.Length];
            BinaryPrimitives.WriteUInt16BigEndian(adu.AsSpan(0), tid);
            BinaryPrimitives.WriteUInt16BigEndian(adu.AsSpan(2), 0);
            BinaryPrimitives.WriteUInt16BigEndian(adu.AsSpan(4), (ushort)(pdu.Length + 1));
            adu[6] = unitId;
            pdu.CopyTo(adu, 7);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeoutMs);

            try
            {
                Frame?.Invoke(FrameDirection.Tx, adu, ModbusDescriber.Describe(FrameDirection.Tx, unitId, pdu, $"TID={tid} "));
                await stream.WriteAsync(adu, cts.Token).ConfigureAwait(false);

                var header = new byte[7];
                await stream.ReadExactlyAsync(header, cts.Token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                if (length < 2 || length > 260)
                    throw new InvalidDataException($"Campo Length inválido na resposta: {length}.");

                var body = new byte[length - 1];
                await stream.ReadExactlyAsync(body, cts.Token).ConfigureAwait(false);

                var full = new byte[header.Length + body.Length];
                header.CopyTo(full, 0);
                body.CopyTo(full, header.Length);

                var responseTid = BinaryPrimitives.ReadUInt16BigEndian(header);
                Frame?.Invoke(FrameDirection.Rx, full, ModbusDescriber.Describe(FrameDirection.Rx, header[6], body, $"TID={responseTid} "));

                if (responseTid != tid)
                    throw new InvalidDataException($"Transaction ID divergente (enviado {tid}, recebido {responseTid}).");

                return ModbusDescriber.ValidateResponse(pdu[0], body);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Close();
                throw new TimeoutException($"Sem resposta do device em {_timeoutMs} ms (confira IP, porta e Unit ID).");
            }
            catch (InvalidDataException)
            {
                Close();
                throw;
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                Close();
                throw new IOException($"Conexão perdida: {ex.Message}", ex);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private void Close()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
    }

    public void Dispose() => Close();
}

// =====================================================================================
//  Modbus RTU (endereço + PDU + CRC16) sobre serial ou TCP
// =====================================================================================

public abstract class ModbusRtuTransport : IModbusTransport
{
    private const int SilenceGapMs = 60;
    private readonly SemaphoreSlim _lock = new(1, 1);

    protected ModbusRtuTransport(int timeoutMs)
    {
        TimeoutMs = Math.Max(100, timeoutMs);
    }

    protected int TimeoutMs { get; }

    public event Action<FrameDirection, byte[], string>? Frame;

    public abstract bool IsConnected { get; }

    public abstract Task ConnectAsync(CancellationToken ct);

    protected abstract Task WriteAsync(byte[] data, CancellationToken ct);

    /// <summary>Lê exatamente <paramref name="count"/> bytes ou lança <see cref="TimeoutException"/>.</summary>
    protected abstract Task<byte[]> ReadAsync(int count, int timeoutMs, CancellationToken ct);

    protected abstract void DiscardInput();

    protected abstract void Close();

    /// <summary>Lê até haver silêncio na linha (respostas de tamanho variável, ex.: FC 43).</summary>
    protected virtual async Task<byte[]> ReadUntilSilenceAsync(CancellationToken ct)
    {
        var data = new List<byte>();
        while (data.Count < 256)
        {
            try
            {
                data.AddRange(await ReadAsync(1, SilenceGapMs, ct).ConfigureAwait(false));
            }
            catch (TimeoutException)
            {
                break;
            }
        }
        return data.ToArray();
    }

    public async Task<byte[]> TransactAsync(byte unitId, byte[] pdu, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsConnected) throw new IOException("Não conectado.");

            var frame = new byte[pdu.Length + 3];
            frame[0] = unitId;
            pdu.CopyTo(frame, 1);
            var crc = Crc16.Compute(frame.AsSpan(0, frame.Length - 2));
            frame[^2] = (byte)(crc & 0xFF);
            frame[^1] = (byte)(crc >> 8);

            DiscardInput();
            Frame?.Invoke(FrameDirection.Tx, frame, ModbusDescriber.Describe(FrameDirection.Tx, unitId, pdu, string.Empty));
            await WriteAsync(frame, ct).ConfigureAwait(false);

            if (unitId == 0)
                return pdu; // broadcast: escravos não respondem

            byte[] response;
            try
            {
                var head = await ReadAsync(2, TimeoutMs, ct).ConfigureAwait(false);
                var function = head[1];
                byte[] rest;

                if ((function & 0x80) != 0)
                {
                    rest = await ReadAsync(3, TimeoutMs, ct).ConfigureAwait(false);
                }
                else if (function is 0x01 or 0x02 or 0x03 or 0x04)
                {
                    var count = await ReadAsync(1, TimeoutMs, ct).ConfigureAwait(false);
                    var data = await ReadAsync(count[0] + 2, TimeoutMs, ct).ConfigureAwait(false);
                    rest = count.Concat(data).ToArray();
                }
                else if (function is 0x05 or 0x06 or 0x0F or 0x10)
                {
                    rest = await ReadAsync(6, TimeoutMs, ct).ConfigureAwait(false);
                }
                else
                {
                    rest = await ReadUntilSilenceAsync(ct).ConfigureAwait(false);
                }

                response = head.Concat(rest).ToArray();
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"Sem resposta em {TimeoutMs} ms. Confira Unit ID, baud rate, paridade, stop bits e a polaridade A/B do RS-485.");
            }

            var pduLength = response.Length - 3;
            var responsePdu = response.AsSpan(1, Math.Max(0, pduLength)).ToArray();
            Frame?.Invoke(FrameDirection.Rx, response, ModbusDescriber.Describe(FrameDirection.Rx, response[0], responsePdu, string.Empty));

            if (response.Length < 4)
                throw new InvalidDataException("Resposta RTU muito curta.");

            var expectedCrc = Crc16.Compute(response.AsSpan(0, response.Length - 2));
            var receivedCrc = (ushort)(response[^2] | (response[^1] << 8));
            if (expectedCrc != receivedCrc)
                throw new InvalidDataException($"CRC inválido (recebido {receivedCrc:X4}, calculado {expectedCrc:X4}). Ruído na linha ou configuração serial errada.");

            if (response[0] != unitId)
                throw new InvalidDataException($"Resposta de outro escravo (esperado {unitId}, recebido {response[0]}). Possível conflito de endereço.");

            return ModbusDescriber.ValidateResponse(pdu[0], responsePdu);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or UnauthorizedAccessException)
        {
            Close();
            throw new IOException($"Conexão perdida: {ex.Message}", ex);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => Close();
}

public sealed class RtuSerialTransport : ModbusRtuTransport
{
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly Parity _parity;
    private readonly int _dataBits;
    private readonly StopBits _stopBits;
    private SerialPort? _port;

    public RtuSerialTransport(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, int timeoutMs)
        : base(timeoutMs)
    {
        _portName = portName;
        _baudRate = baudRate;
        _parity = parity;
        _dataBits = dataBits;
        _stopBits = stopBits;
    }

    public override bool IsConnected => _port?.IsOpen == true;

    public override Task ConnectAsync(CancellationToken ct)
    {
        Close();
        var port = new SerialPort(_portName, _baudRate, _parity, _dataBits, _stopBits)
        {
            Handshake = Handshake.None,
            ReadTimeout = TimeoutMs,
            WriteTimeout = TimeoutMs
        };
        port.Open();
        _port = port;
        return Task.CompletedTask;
    }

    protected override Task WriteAsync(byte[] data, CancellationToken ct)
    {
        var port = _port ?? throw new IOException("Porta serial fechada.");
        port.Write(data, 0, data.Length);
        return Task.CompletedTask;
    }

    protected override Task<byte[]> ReadAsync(int count, int timeoutMs, CancellationToken ct)
    {
        var port = _port ?? throw new IOException("Porta serial fechada.");
        return Task.Run(() =>
        {
            port.ReadTimeout = timeoutMs;
            var buffer = new byte[count];
            var read = 0;
            while (read < count)
            {
                ct.ThrowIfCancellationRequested();
                read += port.Read(buffer, read, count - read); // lança TimeoutException
            }
            return buffer;
        }, ct);
    }

    protected override void DiscardInput()
    {
        if (_port is { IsOpen: true }) _port.DiscardInBuffer();
    }

    protected override void Close()
    {
        try
        {
            _port?.Close();
        }
        catch
        {
            // ignora
        }
        _port?.Dispose();
        _port = null;
    }
}

/// <summary>Modbus RTU encapsulado em TCP (conversores serial/Ethernet em modo "transparente").</summary>
public sealed class RtuTcpTransport : ModbusRtuTransport
{
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _tcp;
    private NetworkStream? _stream;

    public RtuTcpTransport(string host, int port, int timeoutMs) : base(timeoutMs)
    {
        _host = host;
        _port = port;
    }

    public override bool IsConnected => _tcp?.Connected == true && _stream is not null;

    public override async Task ConnectAsync(CancellationToken ct)
    {
        Close();
        _tcp = await TcpHelper.ConnectAsync(_host, _port, TimeoutMs, ct).ConfigureAwait(false);
        _stream = _tcp.GetStream();
    }

    protected override Task WriteAsync(byte[] data, CancellationToken ct)
    {
        var stream = _stream ?? throw new IOException("Não conectado.");
        return stream.WriteAsync(data, 0, data.Length, ct);
    }

    protected override async Task<byte[]> ReadAsync(int count, int timeoutMs, CancellationToken ct)
    {
        var stream = _stream ?? throw new IOException("Não conectado.");
        var buffer = new byte[count];
        var read = 0;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        // Polling com DataAvailable evita cancelar leituras pendentes no socket.
        while (read < count)
        {
            ct.ThrowIfCancellationRequested();
            if (stream.DataAvailable)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
                if (n == 0) throw new IOException("Conexão fechada pelo conversor.");
                read += n;
                continue;
            }
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(2, ct).ConfigureAwait(false);
        }
        return buffer;
    }

    protected override void DiscardInput()
    {
        var stream = _stream;
        if (stream is null) return;
        var junk = new byte[256];
        while (stream.DataAvailable)
        {
            if (stream.Read(junk, 0, junk.Length) == 0) break;
        }
    }

    protected override void Close()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
    }
}

// =====================================================================================

public static class Crc16
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }
}

internal static class TcpHelper
{
    public static async Task<TcpClient> ConnectAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return tcp;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new TimeoutException($"Timeout ao conectar em {host}:{port} ({timeoutMs} ms). Confira IP, rota e firewall.");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            tcp.Dispose();
            throw new IOException($"Conexão recusada em {host}:{port}: o host respondeu, mas nenhum serviço escuta nessa porta.", ex);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }
}
