using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FieldProbe.Core;

namespace FieldProbe.Drivers.Omron;

/// <summary>Estado da CPU lido pelo comando 06 01 (Controller Status Read).</summary>
public sealed record FinsStatus(byte Status, byte Mode, ushort FatalFlags, ushort NonFatalFlags, string ErrorMessage)
{
    public string ModeText => Mode switch
    {
        0x00 => "PROGRAM",
        0x02 => "MONITOR",
        0x04 => "RUN",
        _ => $"modo 0x{Mode:X2}"
    };

    public bool IsStopped => Mode == 0x00 || Status == 0x00;
}

/// <summary>
/// Cliente Omron FINS sobre TCP (com o handshake FINS/TCP que atribui os nós) ou UDP (nós pelo IP).
/// Atende CS1, CJ1/CJ2, CP1L-E/CP1H (com Ethernet) e NJ/NX pela porta FINS.
/// </summary>
public sealed class FinsClient : IDisposable
{
    public const byte MrcMemory = 0x01;
    public const byte SrcRead = 0x01;
    public const byte SrcWrite = 0x02;
    public const byte MrcController = 0x05;
    public const byte MrcStatus = 0x06;

    /// <summary>Máximo de palavras por leitura (CS/CJ aceitam 999; folga para CP).</summary>
    public const int MaxWords = 990;

    private static readonly byte[] Magic = "FINS"u8.ToArray();

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _host;
    private readonly int _port;
    private readonly int _timeoutMs;
    private readonly bool _udp;
    private readonly byte _network;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private UdpClient? _udpClient;
    private byte _sid;

    public FinsClient(string host, int port, int timeoutMs, bool udp, byte network, byte plcNode, byte pcNode)
    {
        _host = host;
        _port = port;
        _timeoutMs = Math.Max(300, timeoutMs);
        _udp = udp;
        _network = network;
        PlcNode = plcNode;
        PcNode = pcNode;
    }

    /// <summary>Nó FINS do CLP (no TCP vem do handshake; no UDP é o configurado ou o último número do IP).</summary>
    public byte PlcNode { get; private set; }

    /// <summary>Nó FINS deste PC (no TCP é atribuído pelo CLP).</summary>
    public byte PcNode { get; private set; }

    public bool IsUdp => _udp;

    public event Action<FrameDirection, byte[], string>? Frame;

    public bool IsConnected => _udp ? _udpClient is not null : _tcp?.Connected == true && _stream is not null;

    public async Task ConnectAsync(CancellationToken ct)
    {
        Close();
        if (_udp)
        {
            var udp = new UdpClient(AddressFamily.InterNetwork);
            try
            {
                udp.Connect(_host, _port);
            }
            catch
            {
                udp.Dispose();
                throw;
            }
            _udpClient = udp;
            // Sem configuração: nó do CLP = último número do IP do CLP; nó do PC = último número do IP deste PC
            if (PlcNode == 0 && IPAddress.TryParse(_host, out var plcIp)) PlcNode = plcIp.GetAddressBytes()[^1];
            if (PcNode == 0 && udp.Client.LocalEndPoint is IPEndPoint local) PcNode = local.Address.GetAddressBytes()[^1];
            if (PcNode == PlcNode) PcNode = (byte)(PcNode == 254 ? 253 : PcNode + 1);
            return;
        }

        var tcp = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeoutMs);
        try
        {
            await tcp.ConnectAsync(_host, _port, cts.Token).ConfigureAwait(false);
            _tcp = tcp;
            _stream = tcp.GetStream();

            // Handshake FINS/TCP: pede um nó (0 = o CLP escolhe) e recebe o nó do PC e do CLP
            var request = TcpPacket(0, BitConverterBE(PcNode));
            Frame?.Invoke(FrameDirection.Tx, request, $"FINS/TCP · pedido de nó (cliente {(PcNode == 0 ? "automático" : PcNode.ToString())})");
            await _stream.WriteAsync(request, cts.Token).ConfigureAwait(false);
            var (command, error, body, raw) = await ReadTcpPacketAsync(_stream, cts.Token).ConfigureAwait(false);
            Frame?.Invoke(FrameDirection.Rx, raw, error == 0 && body.Length >= 8
                ? $"FINS/TCP · nós atribuídos: PC = {body[3]}, CLP = {body[7]}"
                : $"FINS/TCP · ERRO {FinsException.DescribeTcp(error)}");
            if (error != 0) throw new IOException($"O CLP recusou a conexão FINS/TCP: {FinsException.DescribeTcp(error)}.");
            if (command != 1 || body.Length < 8) throw new InvalidDataException("Resposta inesperada no handshake FINS/TCP.");
            PcNode = body[3];
            PlcNode = body[7];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Close();
            tcp.Dispose();
            throw new TimeoutException($"Timeout ao conectar em {_host}:{_port}. Confira IP, porta (padrão 9600) e se o FINS/TCP está habilitado na porta Ethernet do CLP.");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            Close();
            tcp.Dispose();
            throw new IOException($"Conexão recusada em {_host}:{_port}: o equipamento respondeu, mas não aceita FINS/TCP nesta porta. Alguns CP1L-E/CP1H só falam FINS/UDP: troque o transporte para UDP.", ex);
        }
        catch
        {
            Close();
            tcp.Dispose();
            throw;
        }
    }

    // ===================== Comandos =====================

    /// <summary>Lê palavras (cada uma big-endian, como o CLP devolve).</summary>
    public async Task<ushort[]> ReadWordsAsync(FinsArea area, int address, int count, CancellationToken ct)
    {
        if (count is < 1 or > MaxWords) throw new ArgumentOutOfRangeException(nameof(count), $"De 1 a {MaxWords} palavras por leitura.");
        var data = await SendAsync(MrcMemory, SrcRead, MemoryParams(area.WordCode, address + area.AddressOffset, 0, count),
            $"Leitura {area.Prefix}{address}, {count} palavra(s)", ct).ConfigureAwait(false);
        if (data.Length < count * 2) throw new InvalidDataException($"Resposta curta: esperava {count * 2} bytes, vieram {data.Length}.");
        var words = new ushort[count];
        for (var i = 0; i < count; i++) words[i] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i * 2));
        return words;
    }

    public async Task<bool> ReadBitAsync(FinsArea area, int address, int bit, CancellationToken ct)
    {
        var data = await SendAsync(MrcMemory, SrcRead, MemoryParams(area.BitCode, address + area.AddressOffset, bit, 1),
            $"Leitura {Name(area, address)}.{bit:00}", ct).ConfigureAwait(false);
        if (data.Length < 1) throw new InvalidDataException("Resposta sem dados.");
        return data[0] != 0;
    }

    public Task WriteWordsAsync(FinsArea area, int address, ushort[] words, CancellationToken ct)
    {
        var parameters = MemoryParams(area.WordCode, address + area.AddressOffset, 0, words.Length);
        var body = new byte[parameters.Length + words.Length * 2];
        parameters.CopyTo(body, 0);
        for (var i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(parameters.Length + i * 2), words[i]);
        return SendAsync(MrcMemory, SrcWrite, body, $"Escrita {Name(area, address)}, {words.Length} palavra(s)", ct);
    }

    public Task WriteBitAsync(FinsArea area, int address, int bit, bool value, CancellationToken ct)
    {
        var parameters = MemoryParams(area.BitCode, address + area.AddressOffset, bit, 1);
        var body = new byte[parameters.Length + 1];
        parameters.CopyTo(body, 0);
        body[^1] = value ? (byte)1 : (byte)0;
        return SendAsync(MrcMemory, SrcWrite, body, $"Escrita {Name(area, address)}.{bit:00} ← {(value ? "ON" : "OFF")}", ct);
    }

    /// <summary>Modelo e versão da CPU (comando 05 01 · Controller Data Read).</summary>
    public async Task<(string Model, string Version)> ReadModelAsync(CancellationToken ct)
    {
        var data = await SendAsync(MrcController, 0x01, Array.Empty<byte>(), "Leitura do modelo da CPU", ct).ConfigureAwait(false);
        if (data.Length < 20) throw new InvalidDataException("Resposta do modelo da CPU incompleta.");
        var model = Encoding.ASCII.GetString(data, 0, 20).Trim('\0', ' ');
        var version = data.Length >= 40 ? Encoding.ASCII.GetString(data, 20, 20).Trim('\0', ' ') : string.Empty;
        return (model, version);
    }

    /// <summary>Estado de operação da CPU (comando 06 01 · Controller Status Read).</summary>
    public async Task<FinsStatus> ReadStatusAsync(CancellationToken ct)
    {
        var data = await SendAsync(MrcStatus, 0x01, Array.Empty<byte>(), "Leitura do estado da CPU", ct).ConfigureAwait(false);
        if (data.Length < 6) throw new InvalidDataException("Resposta do estado da CPU incompleta.");
        var message = data.Length >= 26 ? Encoding.ASCII.GetString(data, 10, 16).Trim('\0', ' ') : string.Empty;
        return new FinsStatus(data[0], data[1], BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2)),
            BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4)), message);
    }

    private static string Name(FinsArea area, int address) => area.Prefix == "CIO" ? address.ToString() : area.Prefix + address;

    private static byte[] MemoryParams(byte areaCode, int address, int bit, int count)
    {
        var p = new byte[6];
        p[0] = areaCode;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(1), (ushort)address);
        p[3] = (byte)bit;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(4), (ushort)count);
        return p;
    }

    // ===================== Frames =====================

    /// <summary>Cabeçalho FINS (10 bytes) + MRC/SRC + parâmetros.</summary>
    public byte[] BuildFrame(byte mrc, byte src, byte[] parameters, byte sid)
    {
        var frame = new byte[12 + parameters.Length];
        frame[0] = 0x80;          // ICF: comando, resposta obrigatória
        frame[1] = 0x00;          // RSV
        frame[2] = 0x02;          // GCT: até 2 pontes de rede
        frame[3] = _network;      // DNA: rede de destino (0 = local)
        frame[4] = PlcNode;       // DA1: nó do CLP
        frame[5] = 0x00;          // DA2: unidade (0 = CPU)
        frame[6] = 0x00;          // SNA: rede de origem
        frame[7] = PcNode;        // SA1: nó deste PC
        frame[8] = 0x00;          // SA2
        frame[9] = sid;           // SID: número do pedido
        frame[10] = mrc;
        frame[11] = src;
        parameters.CopyTo(frame, 12);
        return frame;
    }

    private static byte[] BitConverterBE(uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        return b;
    }

    /// <summary>Pacote FINS/TCP: "FINS" + comprimento + comando + erro + dados.</summary>
    private static byte[] TcpPacket(uint command, byte[] payload)
    {
        var packet = new byte[16 + payload.Length];
        Magic.CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), (uint)(8 + payload.Length));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), command);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12), 0);
        payload.CopyTo(packet, 16);
        return packet;
    }

    private static async Task<(uint Command, uint Error, byte[] Body, byte[] Raw)> ReadTcpPacketAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[16];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException($"Resposta não é FINS/TCP (começa com {header[0]:X2} {header[1]:X2} {header[2]:X2} {header[3]:X2}).");
        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
        if (length < 8 || length > 4096) throw new InvalidDataException($"Comprimento FINS/TCP inválido: {length}.");
        var body = new byte[length - 8];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        var raw = new byte[header.Length + body.Length];
        header.CopyTo(raw, 0);
        body.CopyTo(raw, header.Length);
        return (BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)), BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12)), body, raw);
    }

    private async Task<byte[]> SendAsync(byte mrc, byte src, byte[] parameters, string description, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sid = unchecked(++_sid);
            var frame = BuildFrame(mrc, src, parameters, sid);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeoutMs);
            try
            {
                byte[] response;
                if (_udp)
                {
                    var udp = _udpClient ?? throw new IOException("Não conectado.");
                    Frame?.Invoke(FrameDirection.Tx, frame, description);
                    await udp.SendAsync(frame, cts.Token).ConfigureAwait(false);
                    while (true)
                    {
                        var result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                        Frame?.Invoke(FrameDirection.Rx, result.Buffer, ResponseText(result.Buffer, 0));
                        if (result.Buffer.Length >= 14 && result.Buffer[9] == sid) { response = result.Buffer; break; }
                    }
                }
                else
                {
                    var stream = _stream ?? throw new IOException("Não conectado.");
                    var packet = TcpPacket(2, frame);
                    Frame?.Invoke(FrameDirection.Tx, packet, description);
                    await stream.WriteAsync(packet, cts.Token).ConfigureAwait(false);
                    var (command, error, body, raw) = await ReadTcpPacketAsync(stream, cts.Token).ConfigureAwait(false);
                    Frame?.Invoke(FrameDirection.Rx, raw, error != 0 ? $"FINS/TCP · ERRO {FinsException.DescribeTcp(error)}" : ResponseText(body, 0));
                    if (error != 0) throw new IOException($"Erro FINS/TCP: {FinsException.DescribeTcp(error)}.");
                    if (command != 2) throw new InvalidDataException($"Comando FINS/TCP inesperado na resposta: {command}.");
                    response = body;
                }

                if (response.Length < 14) throw new InvalidDataException("Resposta FINS curta demais.");
                if (response[9] != sid) throw new InvalidDataException($"SID divergente (enviado {sid}, recebido {response[9]}).");
                var (code, relay, _, _) = FinsException.Split(response[12], response[13]);
                if (code != 0) throw new FinsException(code, description + (relay ? " · erro de repasse na rede" : string.Empty));
                return response[14..];
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (!_udp) Close();
                throw new TimeoutException(_udp
                    ? $"Sem resposta do CLP em {_timeoutMs} ms. No FINS/UDP confira o nó do CLP ({PlcNode}) e o nó do PC ({PcNode}): o CLP só responde se o nó de destino for o dele."
                    : $"Sem resposta do CLP em {_timeoutMs} ms.");
            }
            catch (InvalidDataException)
            {
                if (!_udp) Close();
                throw;
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException && !_udp)
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

    /// <summary>Texto curto da resposta para o log (end code).</summary>
    private static string ResponseText(byte[] frame, int offset)
    {
        if (frame.Length < offset + 14) return "Resposta FINS";
        var (code, _, nonFatal, fatal) = FinsException.Split(frame[offset + 12], frame[offset + 13]);
        var flags = fatal ? " · CPU com ERRO FATAL" : nonFatal ? " · CPU com erro não fatal" : string.Empty;
        return code == 0
            ? $"Resposta OK · {frame.Length - offset - 14} byte(s) de dados{flags}"
            : $"ERRO 0x{code:X4} · {FinsException.Describe(code)}{flags}";
    }

    private void Close()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _udpClient?.Dispose();
        _stream = null;
        _tcp = null;
        _udpClient = null;
    }

    public void Dispose() => Close();
}
