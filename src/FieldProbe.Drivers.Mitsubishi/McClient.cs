using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using FieldProbe.Core;

namespace FieldProbe.Drivers.Mitsubishi;

/// <summary>
/// Cliente MC Protocol (SLMP) em frame 3E binário sobre TCP.
/// Atende Q/L, iQ-R e iQ-F (FX5) com a porta configurada como "MC Protocol / SLMP", código Binário.
/// </summary>
public sealed class McClient : IDisposable
{
    public const ushort CmdBatchRead = 0x0401;
    public const ushort CmdBatchWrite = 0x1401;
    public const ushort CmdReadCpuModel = 0x0101;
    public const ushort SubWord = 0x0000;
    public const ushort SubBit = 0x0001;

    /// <summary>Limites por pedido (os menores entre as séries suportadas).</summary>
    public const int MaxWordPoints = 960;
    public const int MaxBitPoints = 3584;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _host;
    private readonly int _port;
    private readonly int _timeoutMs;
    private readonly byte _network;
    private readonly byte _station;
    private readonly McSeries _series;
    private TcpClient? _tcp;
    private NetworkStream? _stream;

    public McClient(string host, int port, int timeoutMs, byte network, byte station, McSeries series)
    {
        _host = host;
        _port = port;
        _timeoutMs = Math.Max(300, timeoutMs);
        _network = network;
        _station = station;
        _series = series;
    }

    /// <summary>Frame bruto (TX/RX) + descrição, para o log.</summary>
    public event Action<FrameDirection, byte[], string>? Frame;

    public bool IsConnected => _tcp?.Connected == true && _stream is not null;

    public async Task ConnectAsync(CancellationToken ct)
    {
        Close();
        var tcp = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeoutMs);
        try
        {
            await tcp.ConnectAsync(_host, _port, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new TimeoutException($"Timeout ao conectar em {_host}:{_port}. Confira IP, rota e se a porta do MC Protocol está aberta na CPU.");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            tcp.Dispose();
            throw new IOException(
                $"Conexão recusada em {_host}:{_port}: o CLP respondeu, mas a porta não está aberta para MC Protocol. " +
                "No GX Works, adicione nas configurações Ethernet da CPU uma conexão 'MC Protocol' (iQ-F: 'SLMP'), TCP, código Binário, nesta porta.", ex);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    // ===================== Comandos =====================

    /// <summary>Lê <paramref name="words"/> palavras de 16 bits (bytes na ordem do CLP: little-endian).</summary>
    public async Task<byte[]> ReadWordsAsync(McDevice device, int number, int words, CancellationToken ct)
    {
        if (words is < 1 or > MaxWordPoints) throw new ArgumentOutOfRangeException(nameof(words), $"De 1 a {MaxWordPoints} palavras por leitura.");
        var body = DeviceBlock(device, number, words);
        var data = await TransactAsync(CmdBatchRead, SubWord, body, $"Leitura {Name(device, number)}, {words} palavra(s)", ct).ConfigureAwait(false);
        if (data.Length < words * 2) throw new InvalidDataException($"Resposta curta: esperava {words * 2} bytes, vieram {data.Length}.");
        return data[..(words * 2)];
    }

    /// <summary>Lê <paramref name="count"/> pontos de bit (2 pontos por byte: bit alto = primeiro ponto).</summary>
    public async Task<bool[]> ReadBitsAsync(McDevice device, int number, int count, CancellationToken ct)
    {
        if (count is < 1 or > MaxBitPoints) throw new ArgumentOutOfRangeException(nameof(count), $"De 1 a {MaxBitPoints} bits por leitura.");
        var body = DeviceBlock(device, number, count);
        var data = await TransactAsync(CmdBatchRead, SubBit, body, $"Leitura {Name(device, number)}, {count} bit(s)", ct).ConfigureAwait(false);
        if (data.Length < (count + 1) / 2) throw new InvalidDataException($"Resposta curta: esperava {(count + 1) / 2} bytes, vieram {data.Length}.");
        var bits = new bool[count];
        for (var i = 0; i < count; i++)
        {
            var b = data[i / 2];
            bits[i] = (i % 2 == 0 ? b >> 4 : b & 0x0F) != 0;
        }
        return bits;
    }

    public Task WriteWordsAsync(McDevice device, int number, byte[] data, CancellationToken ct)
    {
        if (data.Length == 0 || data.Length % 2 != 0) throw new ArgumentException("Dados de palavra precisam ter tamanho par.", nameof(data));
        var words = data.Length / 2;
        var block = DeviceBlock(device, number, words);
        var body = new byte[block.Length + data.Length];
        block.CopyTo(body, 0);
        data.CopyTo(body, block.Length);
        return TransactAsync(CmdBatchWrite, SubWord, body, $"Escrita {Name(device, number)}, {words} palavra(s)", ct);
    }

    public Task WriteBitsAsync(McDevice device, int number, bool[] values, CancellationToken ct)
    {
        var block = DeviceBlock(device, number, values.Length);
        var body = new byte[block.Length + (values.Length + 1) / 2];
        block.CopyTo(body, 0);
        for (var i = 0; i < values.Length; i++)
            if (values[i]) body[block.Length + i / 2] |= (byte)(i % 2 == 0 ? 0x10 : 0x01);
        var text = values.Length == 1 ? (values[0] ? "ON" : "OFF") : $"{values.Length} bit(s)";
        return TransactAsync(CmdBatchWrite, SubBit, body, $"Escrita {Name(device, number)} ← {text}", ct);
    }

    /// <summary>Lê o nome do modelo da CPU (comando 0101). Algumas CPUs/módulos não suportam.</summary>
    public async Task<(string Model, ushort Code)> ReadCpuModelAsync(CancellationToken ct)
    {
        var data = await TransactAsync(CmdReadCpuModel, 0x0000, Array.Empty<byte>(), "Leitura do modelo da CPU", ct).ConfigureAwait(false);
        if (data.Length < 16) throw new InvalidDataException("Resposta do modelo da CPU incompleta.");
        var model = Encoding.ASCII.GetString(data, 0, 16).Trim('\0', ' ');
        var code = data.Length >= 18 ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(16)) : (ushort)0;
        return (model, code);
    }

    private string Name(McDevice device, int number) => device.Symbol + McAddress.FormatNumber(device, number, _series);

    /// <summary>Nº do dispositivo (3 bytes) + código (1 byte) + quantidade de pontos (2 bytes).</summary>
    private static byte[] DeviceBlock(McDevice device, int number, int points)
    {
        var block = new byte[6];
        block[0] = (byte)number;
        block[1] = (byte)(number >> 8);
        block[2] = (byte)(number >> 16);
        block[3] = device.Code;
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(4), (ushort)points);
        return block;
    }

    // ===================== Frame 3E =====================

    /// <summary>Monta o pedido 3E binário (subcabeçalho 50 00).</summary>
    public byte[] BuildRequest(ushort command, ushort subcommand, byte[] body)
    {
        var timer = (ushort)Math.Clamp(_timeoutMs / 250, 1, 0xFFFF);
        var frame = new byte[15 + body.Length];
        frame[0] = 0x50;
        frame[1] = 0x00;
        frame[2] = _network;
        frame[3] = _station;                                        // nº do PC (0xFF = CPU acessada)
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), 0x03FF); // módulo de destino: CPU
        frame[6] = 0x00;                                             // estação do módulo (multidrop)
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(7), (ushort)(6 + body.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(9), timer);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(11), command);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(13), subcommand);
        body.CopyTo(frame, 15);
        return frame;
    }

    private async Task<byte[]> TransactAsync(ushort command, ushort subcommand, byte[] body, string description, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stream = _stream ?? throw new IOException("Não conectado.");
            var request = BuildRequest(command, subcommand, body);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeoutMs);
            try
            {
                Frame?.Invoke(FrameDirection.Tx, request, description);
                await stream.WriteAsync(request, cts.Token).ConfigureAwait(false);

                // Resposta: D0 00 | rede | PC | módulo (2) | estação | comprimento (2) | end code (2) | dados
                var header = new byte[9];
                await stream.ReadExactlyAsync(header, cts.Token).ConfigureAwait(false);
                if (header[0] != 0xD0 || header[1] != 0x00)
                    throw new InvalidDataException(
                        $"Resposta não é MC 3E binário (começa com {header[0]:X2} {header[1]:X2}). " +
                        "Confira se a porta está em MC Protocol com código Binário (não ASCII) e se não é a porta do GX Works.");

                var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(7));
                if (length < 2 || length > 8192) throw new InvalidDataException($"Comprimento inválido na resposta: {length}.");
                var rest = new byte[length];
                await stream.ReadExactlyAsync(rest, cts.Token).ConfigureAwait(false);

                var full = new byte[header.Length + rest.Length];
                header.CopyTo(full, 0);
                rest.CopyTo(full, header.Length);

                var endCode = BinaryPrimitives.ReadUInt16LittleEndian(rest);
                Frame?.Invoke(FrameDirection.Rx, full, endCode == 0
                    ? $"Resposta OK · {rest.Length - 2} byte(s) de dados"
                    : $"ERRO 0x{endCode:X4} · {McException.Describe(endCode)}");

                if (endCode != 0) throw new McException(endCode, description);
                return rest[2..];
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Close();
                throw new TimeoutException($"Sem resposta do CLP em {_timeoutMs} ms. Confira se a porta está em MC Protocol (Binário) e a série da CPU.");
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
