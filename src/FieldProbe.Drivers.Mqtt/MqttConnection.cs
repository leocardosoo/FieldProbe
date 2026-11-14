using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using FieldProbe.Core;

namespace FieldProbe.Drivers.Mqtt;

internal sealed record MqttOptions(
    string Host,
    int Port,
    string ClientId,
    string? User,
    string? Password,
    bool UseTls,
    bool AcceptAnyCertificate,
    int KeepAliveSeconds,
    int TimeoutMs);

/// <summary>
/// Cliente MQTT 3.1.1 implementado direto em socket (sem dependências), com TLS opcional.
/// Suporta CONNECT, SUBSCRIBE/UNSUBSCRIBE, PUBLISH QoS 0/1 (recebe também QoS 2), PING e log de cada pacote.
/// </summary>
internal sealed class MqttConnection : IDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<ushort, TaskCompletionSource<byte[]>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private TaskCompletionSource<byte[]>? _connAck;
    private TcpClient? _tcp;
    private Stream? _stream;
    private MqttOptions? _options;
    private int _packetId;
    private volatile bool _connected;

    /// <summary>Pacote bruto + descrição.</summary>
    public event Action<FrameDirection, byte[], string>? Frame;

    /// <summary>Tópico, payload, flag retain.</summary>
    public event Action<string, byte[], bool>? MessageReceived;

    /// <summary>Motivo da queda (não é disparado em desconexão pedida pelo usuário).</summary>
    public event Action<string>? ConnectionLost;

    public bool IsConnected => _connected && _tcp?.Connected == true;

    public bool SessionPresent { get; private set; }

    public async Task ConnectAsync(MqttOptions options, CancellationToken ct)
    {
        _options = options;
        var tcp = new TcpClient { NoDelay = true };
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(options.TimeoutMs);
            try
            {
                await tcp.ConnectAsync(options.Host, options.Port, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                tcp.Dispose();
                throw new TimeoutException($"Timeout ao conectar no broker {options.Host}:{options.Port}.");
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                tcp.Dispose();
                throw new IOException($"Conexão recusada em {options.Host}:{options.Port}. O broker está rodando? A porta é 1883 (TCP) ou 8883 (TLS)?", ex);
            }
        }

        _tcp = tcp;
        Stream stream = tcp.GetStream();

        if (options.UseTls)
        {
            var ssl = new SslStream(stream, false, (_, _, _, errors) => errors == SslPolicyErrors.None || options.AcceptAnyCertificate);
            try
            {
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = options.Host }, ct).ConfigureAwait(false);
            }
            catch (AuthenticationException ex)
            {
                Dispose();
                throw new IOException($"Falha no TLS: {ex.Message}. Para certificados autoassinados, habilite 'Aceitar qualquer certificado'.", ex);
            }
            stream = ssl;
            Frame?.Invoke(FrameDirection.Info, Array.Empty<byte>(), $"TLS estabelecido ({ssl.SslProtocol}, {ssl.CipherAlgorithm})");
        }

        _stream = stream;
        _connAck = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() => ReadLoopAsync(_cts.Token));

        await SendAsync(BuildConnect(options), $"CONNECT clientId={options.ClientId} keepAlive={options.KeepAliveSeconds}s{(options.User is { Length: > 0 } ? $" user={options.User}" : string.Empty)}", ct).ConfigureAwait(false);

        byte[] ack;
        try
        {
            ack = await _connAck.Task.WaitAsync(TimeSpan.FromMilliseconds(options.TimeoutMs), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Dispose();
            throw new TimeoutException("O broker não respondeu ao CONNECT (CONNACK). Confira se a porta é MQTT e se TLS está correto.");
        }
        catch
        {
            Dispose();
            throw;
        }

        SessionPresent = (ack[0] & 0x01) != 0;
        var code = ack[1];
        if (code != 0)
        {
            Dispose();
            throw new InvalidOperationException($"Broker recusou a conexão: {ConnAckReason(code)}");
        }

        _connected = true;
        _ = Task.Run(() => KeepAliveLoopAsync(_cts.Token));
    }

    public async Task<byte[]> SubscribeAsync(IReadOnlyList<string> filters, int qos, CancellationToken ct)
    {
        var id = NextPacketId();
        var body = new List<byte>();
        AddUInt16(body, id);
        foreach (var filter in filters)
        {
            AddString(body, filter);
            body.Add((byte)Math.Clamp(qos, 0, 1));
        }

        var result = await TransactAsync(0x82, body, id, $"SUBSCRIBE id={id} [{string.Join(", ", filters)}] QoS{qos}", ct).ConfigureAwait(false);
        var codes = result.Skip(2).ToArray();
        for (var i = 0; i < codes.Length && i < filters.Count; i++)
        {
            if (codes[i] == 0x80)
                throw new InvalidOperationException($"Broker recusou a inscrição em '{filters[i]}' (sem permissão/ACL).");
        }
        return codes;
    }

    public Task UnsubscribeAsync(IReadOnlyList<string> filters, CancellationToken ct)
    {
        var id = NextPacketId();
        var body = new List<byte>();
        AddUInt16(body, id);
        foreach (var filter in filters) AddString(body, filter);
        return TransactAsync(0xA2, body, id, $"UNSUBSCRIBE id={id} [{string.Join(", ", filters)}]", ct);
    }

    public async Task PublishAsync(string topic, byte[] payload, int qos, bool retain, CancellationToken ct)
    {
        if (topic.Contains('+') || topic.Contains('#'))
            throw new ArgumentException("Não é possível publicar em tópico com curinga (+ ou #).");

        qos = Math.Clamp(qos, 0, 1);
        var body = new List<byte>();
        AddString(body, topic);
        ushort id = 0;
        if (qos > 0)
        {
            id = NextPacketId();
            AddUInt16(body, id);
        }
        body.AddRange(payload);

        var header = (byte)(0x30 | (qos << 1) | (retain ? 1 : 0));
        var description = $"PUBLISH {topic} QoS{qos}{(retain ? " retain" : string.Empty)} {payload.Length} bytes: {Preview(payload)}";

        if (qos == 0)
            await SendAsync(Packet(header, body), description, ct).ConfigureAwait(false);
        else
            await TransactAsync(header, body, id, description, ct).ConfigureAwait(false);
    }

    public async Task DisconnectAsync()
    {
        if (_connected)
        {
            try
            {
                await SendAsync(new byte[] { 0xE0, 0x00 }, "DISCONNECT", CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // ignora
            }
        }
        _connected = false;
        Dispose();
    }

    // ----- Internos -----

    private async Task<byte[]> TransactAsync(byte header, List<byte> body, ushort id, string description, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(Packet(header, body), description, ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(_options?.TimeoutMs ?? 5000), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Broker não confirmou o pacote {id} a tempo.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendAsync(byte[] packet, string description, CancellationToken ct)
    {
        var stream = _stream ?? throw new IOException("Não conectado ao broker.");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Frame?.Invoke(FrameDirection.Tx, packet, description);
            await stream.WriteAsync(packet, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Lost($"Falha ao enviar: {ex.Message}");
            throw new IOException($"Conexão com o broker perdida: {ex.Message}", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            var stream = _stream!;
            var one = new byte[1];
            while (!ct.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(one, ct).ConfigureAwait(false);
                var header = one[0];

                // Remaining Length (varint de até 4 bytes)
                var length = 0;
                var multiplier = 1;
                var lengthBytes = new List<byte>();
                while (true)
                {
                    await stream.ReadExactlyAsync(one, ct).ConfigureAwait(false);
                    lengthBytes.Add(one[0]);
                    length += (one[0] & 0x7F) * multiplier;
                    if ((one[0] & 0x80) == 0) break;
                    multiplier *= 128;
                    if (lengthBytes.Count > 4) throw new InvalidDataException("Remaining Length inválido.");
                }

                var body = new byte[length];
                if (length > 0) await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);

                var raw = new byte[1 + lengthBytes.Count + length];
                raw[0] = header;
                lengthBytes.CopyTo(raw, 1);
                body.CopyTo(raw, 1 + lengthBytes.Count);

                await HandlePacketAsync(header, body, raw, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var reason = ex is EndOfStreamException ? "O broker fechou a conexão." : ex.Message;
            _connAck?.TrySetException(new IOException(reason, ex));
            Lost(reason);
        }
    }

    private async Task HandlePacketAsync(byte header, byte[] body, byte[] raw, CancellationToken ct)
    {
        switch (header >> 4)
        {
            case 2: // CONNACK
                Frame?.Invoke(FrameDirection.Rx, raw, body.Length >= 2 ? $"CONNACK {ConnAckReason(body[1])}{((body[0] & 1) != 0 ? " (sessão existente)" : string.Empty)}" : "CONNACK");
                _connAck?.TrySetResult(body.Length >= 2 ? body : new byte[] { 0, 0xFF });
                break;

            case 3: // PUBLISH
            {
                var qos = (header >> 1) & 0x03;
                var retain = (header & 0x01) != 0;
                var topicLength = BinaryPrimitives.ReadUInt16BigEndian(body);
                var topic = Encoding.UTF8.GetString(body, 2, topicLength);
                var pos = 2 + topicLength;
                ushort id = 0;
                if (qos > 0)
                {
                    id = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(pos));
                    pos += 2;
                }
                var payload = body.AsSpan(pos).ToArray();
                Frame?.Invoke(FrameDirection.Rx, raw, $"PUBLISH {topic} QoS{qos}{(retain ? " retain" : string.Empty)} {payload.Length} bytes: {Preview(payload)}");

                if (qos == 1)
                    await SendAsync(new byte[] { 0x40, 0x02, (byte)(id >> 8), (byte)id }, $"PUBACK id={id}", ct).ConfigureAwait(false);
                else if (qos == 2)
                    await SendAsync(new byte[] { 0x50, 0x02, (byte)(id >> 8), (byte)id }, $"PUBREC id={id}", ct).ConfigureAwait(false);

                MessageReceived?.Invoke(topic, payload, retain);
                break;
            }

            case 4: // PUBACK
            case 9: // SUBACK
            case 11: // UNSUBACK
            {
                var id = body.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(body) : (ushort)0;
                var name = (header >> 4) switch { 4 => "PUBACK", 9 => "SUBACK", _ => "UNSUBACK" };
                var extra = (header >> 4) == 9 ? $" códigos=[{string.Join(",", body.Skip(2).Select(b => b == 0x80 ? "FALHA" : $"QoS{b}"))}]" : string.Empty;
                Frame?.Invoke(FrameDirection.Rx, raw, $"{name} id={id}{extra}");
                if (_pending.TryGetValue(id, out var tcs)) tcs.TrySetResult(body);
                break;
            }

            case 6: // PUBREL (fluxo QoS 2 de recebimento)
            {
                var id = BinaryPrimitives.ReadUInt16BigEndian(body);
                Frame?.Invoke(FrameDirection.Rx, raw, $"PUBREL id={id}");
                await SendAsync(new byte[] { 0x70, 0x02, (byte)(id >> 8), (byte)id }, $"PUBCOMP id={id}", ct).ConfigureAwait(false);
                break;
            }

            case 13: // PINGRESP
                Frame?.Invoke(FrameDirection.Rx, raw, "PINGRESP");
                break;

            default:
                Frame?.Invoke(FrameDirection.Rx, raw, $"Pacote tipo {header >> 4}");
                break;
        }
    }

    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        var seconds = Math.Max(5, _options?.KeepAliveSeconds ?? 30);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds / 2.0));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!_connected) break;
                await SendAsync(new byte[] { 0xC0, 0x00 }, "PINGREQ", ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // a queda é tratada pelo loop de leitura
        }
    }

    private void Lost(string reason)
    {
        if (!_connected) return;
        _connected = false;
        foreach (var pending in _pending.Values) pending.TrySetException(new IOException(reason));
        ConnectionLost?.Invoke(reason);
    }

    private static byte[] BuildConnect(MqttOptions o)
    {
        var body = new List<byte>();
        AddString(body, "MQTT");
        body.Add(0x04); // protocolo 3.1.1

        byte flags = 0x02; // clean session
        var hasUser = !string.IsNullOrEmpty(o.User);
        if (hasUser) flags |= 0x80;
        if (hasUser && o.Password is not null) flags |= 0x40;
        body.Add(flags);
        AddUInt16(body, (ushort)Math.Clamp(o.KeepAliveSeconds, 0, 65535));

        AddString(body, o.ClientId);
        if (hasUser) AddString(body, o.User!);
        if (hasUser && o.Password is not null) AddString(body, o.Password);

        return Packet(0x10, body);
    }

    private static byte[] Packet(byte header, List<byte> body)
    {
        var packet = new List<byte>(body.Count + 5) { header };
        var length = body.Count;
        do
        {
            var digit = (byte)(length % 128);
            length /= 128;
            if (length > 0) digit |= 0x80;
            packet.Add(digit);
        }
        while (length > 0);
        packet.AddRange(body);
        return packet.ToArray();
    }

    private static void AddString(List<byte> buffer, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        AddUInt16(buffer, (ushort)bytes.Length);
        buffer.AddRange(bytes);
    }

    private static void AddUInt16(List<byte> buffer, ushort value)
    {
        buffer.Add((byte)(value >> 8));
        buffer.Add((byte)value);
    }

    private ushort NextPacketId()
    {
        var id = (ushort)(Interlocked.Increment(ref _packetId) & 0xFFFF);
        return id == 0 ? NextPacketId() : id;
    }

    internal static string Preview(byte[] payload)
    {
        if (payload.Length == 0) return "(vazio)";
        try
        {
            var text = new UTF8Encoding(false, true).GetString(payload).Replace("\r", " ").Replace("\n", " ");
            return text.Length > 120 ? text[..120] + "…" : text;
        }
        catch (DecoderFallbackException)
        {
            return "(binário)";
        }
    }

    private static string ConnAckReason(byte code) => code switch
    {
        0 => "aceita",
        1 => "versão de protocolo não suportada",
        2 => "client ID rejeitado",
        3 => "broker indisponível",
        4 => "usuário ou senha inválidos",
        5 => "não autorizado",
        _ => $"código {code}"
    };

    public void Dispose()
    {
        _connected = false;
        _cts.Cancel();
        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // ignora
        }
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
    }
}
