using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace FieldProbe.App.Services;

/// <summary>Sondas leves de identificação de protocolo (só handshake, sem ler/escrever dados de processo).</summary>
public static class Probes
{
    /// <summary>OPC UA: envia HEL e espera ACK (ou ERR, que também indica um stack OPC UA).</summary>
    public static async Task<bool> OpcUaHelloAsync(string ip, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient { NoDelay = true };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            await tcp.ConnectAsync(ip, port, cts.Token).ConfigureAwait(false);
            var stream = tcp.GetStream();

            var url = Encoding.UTF8.GetBytes($"opc.tcp://{ip}:{port}");
            var message = new byte[32 + url.Length];
            Encoding.ASCII.GetBytes("HELF").CopyTo(message, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4), (uint)message.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8), 0);       // ProtocolVersion
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(12), 65535);  // ReceiveBufferSize
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(16), 65535);  // SendBufferSize
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(20), 0);      // MaxMessageSize
            BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(24), 0);      // MaxChunkCount
            BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(28), url.Length);
            url.CopyTo(message, 32);

            await stream.WriteAsync(message, cts.Token).ConfigureAwait(false);
            var head = new byte[3];
            await stream.ReadExactlyAsync(head, cts.Token).ConfigureAwait(false);
            var type = Encoding.ASCII.GetString(head);
            return type is "ACK" or "ERR";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Siemens S7: COTP Connection Request (ISO-on-TCP). Retorna a descrição se a CPU aceitar algum rack/slot.</summary>
    public static async Task<string?> S7CotpAsync(string ip, int timeoutMs, CancellationToken ct)
    {
        foreach (var slot in new byte[] { 1, 2, 0, 3 })
        {
            try
            {
                using var tcp = new TcpClient { NoDelay = true };
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);
                await tcp.ConnectAsync(ip, 102, cts.Token).ConfigureAwait(false);
                var stream = tcp.GetStream();

                byte[] request =
                {
                    0x03, 0x00, 0x00, 0x16,                   // TPKT (22 bytes)
                    0x11, 0xE0, 0x00, 0x00, 0x00, 0x01, 0x00, // COTP CR
                    0xC0, 0x01, 0x0A,                         // TPDU size 1024
                    0xC1, 0x02, 0x01, 0x00,                   // TSAP de origem
                    0xC2, 0x02, 0x01, slot                    // TSAP de destino: PG, rack 0, slot N
                };
                await stream.WriteAsync(request, cts.Token).ConfigureAwait(false);

                var tpkt = new byte[4];
                await stream.ReadExactlyAsync(tpkt, cts.Token).ConfigureAwait(false);
                var length = (tpkt[2] << 8) | tpkt[3];
                if (length is < 6 or > 512) continue;
                var cotp = new byte[length - 4];
                await stream.ReadExactlyAsync(cotp, cts.Token).ConfigureAwait(false);

                if (cotp.Length > 1 && cotp[1] == 0xD0)
                    return $"Siemens S7 · CPU aceitou conexão em rack 0 / slot {slot}";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // tenta o próximo slot
            }
        }
        return null;
    }

    /// <summary>MQTT: tenta CONNECT anônimo. Retorna o código do CONNACK (0 = aceito) ou nulo se não for MQTT.</summary>
    public static async Task<int?> MqttAnonymousAsync(string ip, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient { NoDelay = true };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            await tcp.ConnectAsync(ip, port, cts.Token).ConfigureAwait(false);
            var stream = tcp.GetStream();

            var clientId = Encoding.ASCII.GetBytes("fieldprobe-scan");
            var body = new List<byte> { 0x00, 0x04, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 0x04, 0x02, 0x00, 0x0A, 0x00, (byte)clientId.Length };
            body.AddRange(clientId);
            var packet = new List<byte> { 0x10, (byte)body.Count };
            packet.AddRange(body);

            await stream.WriteAsync(packet.ToArray(), cts.Token).ConfigureAwait(false);
            var ack = new byte[4];
            await stream.ReadExactlyAsync(ack, cts.Token).ConfigureAwait(false);
            if (ack[0] != 0x20) return null;

            try
            {
                await stream.WriteAsync(new byte[] { 0xE0, 0x00 }, cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // ignora
            }
            return ack[3];
        }
        catch
        {
            return null;
        }
    }
}
