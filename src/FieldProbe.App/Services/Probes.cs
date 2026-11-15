using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.IO;

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

    /// <summary>
    /// Siemens S7: COTP Connection Request (ISO-on-TCP) e, se a CPU aceitar, leitura da identificação
    /// (SZL 0x0011 = código de pedido e firmware · SZL 0x001C = tipo da CPU, nome e número de série).
    /// É a mesma leitura que o TIA Portal faz em "Accessible devices": não lê nem escreve dados de processo.
    /// </summary>
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
                {
                    string? identity = null;
                    try
                    {
                        identity = await ReadS7IdentityAsync(stream, cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        // CPU aceitou a conexão, mas não respondeu à leitura de identificação
                    }

                    return identity is null
                        ? $"Siemens S7 · CPU aceitou conexão em rack 0 / slot {slot}"
                        : $"{identity} · rack 0 / slot {slot}";
                }
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

    /// <summary>Setup Communication + leitura das SZL 0x0011 e 0x001C. Retorna null se não conseguir nada.</summary>
    private static async Task<string?> ReadS7IdentityAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] setup =
        {
            0x03, 0x00, 0x00, 0x19, 0x02, 0xF0, 0x80,       // TPKT + COTP DT
            0x32, 0x01, 0x00, 0x00, 0x00, 0x00,             // S7 job
            0x00, 0x08, 0x00, 0x00,                         // parâmetros: 8 bytes, dados: 0
            0xF0, 0x00, 0x00, 0x01, 0x00, 0x01, 0x01, 0xE0  // Setup Communication, PDU 480
        };
        var setupResponse = await S7ExchangeAsync(stream, setup, ct).ConfigureAwait(false);
        if (setupResponse.Length < 5 || setupResponse[3] != 0x32) return null;

        string? orderNumber = null, firmware = null;
        var moduleRecords = await ReadSzlAsync(stream, 0x0011, ct).ConfigureAwait(false);
        foreach (var record in moduleRecords)
        {
            if (record.Length < 28) continue;
            var index = (record[0] << 8) | record[1];
            if (index == 1) orderNumber = AsciiText(record, 2, 20);
            if (record[24] == (byte)'V') firmware = $"V{record[25]}.{record[26]}.{record[27]}";
        }

        string? typeName = null, plcName = null, serial = null;
        try
        {
            var componentRecords = await ReadSzlAsync(stream, 0x001C, ct).ConfigureAwait(false);
            foreach (var record in componentRecords)
            {
                if (record.Length < 4) continue;
                var index = (record[0] << 8) | record[1];
                var text = AsciiText(record, 2, record.Length - 2);
                switch (index)
                {
                    case 1: plcName = text; break;
                    case 5: serial = text; break;
                    case 7: typeName = text; break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // algumas CPUs antigas não têm a SZL 0x001C
        }

        var parts = new List<string> { string.IsNullOrEmpty(typeName) ? "Siemens S7" : $"Siemens {typeName}" };
        if (!string.IsNullOrEmpty(orderNumber)) parts.Add(orderNumber);
        if (!string.IsNullOrEmpty(firmware)) parts.Add($"FW {firmware}");
        if (!string.IsNullOrEmpty(serial)) parts.Add($"S/N {serial}");
        if (!string.IsNullOrEmpty(plcName)) parts.Add($"nome '{plcName}'");
        return typeName is null && orderNumber is null && firmware is null ? null : string.Join(" · ", parts);
    }

    /// <summary>Lê uma SZL (índice 0) e devolve os registros.</summary>
    private static async Task<List<byte[]>> ReadSzlAsync(NetworkStream stream, ushort szlId, CancellationToken ct)
    {
        byte[] request =
        {
            0x03, 0x00, 0x00, 0x21, 0x02, 0xF0, 0x80,                   // TPKT (33 bytes) + COTP DT
            0x32, 0x07, 0x00, 0x00, 0x00, 0x01,                         // S7 userdata
            0x00, 0x08, 0x00, 0x08,                                     // parâmetros: 8 bytes, dados: 8
            0x00, 0x01, 0x12, 0x04, 0x11, 0x44, 0x01, 0x00,             // CPU functions · Read SZL
            0xFF, 0x09, 0x00, 0x04,                                     // dados: 4 bytes
            (byte)(szlId >> 8), (byte)szlId, 0x00, 0x00                 // SZL-ID e índice 0
        };
        var body = await S7ExchangeAsync(stream, request, ct).ConfigureAwait(false);

        // body = COTP DT (3) + cabeçalho S7 userdata (10) + parâmetros + dados
        const int s7 = 3;
        if (body.Length < s7 + 10 || body[s7] != 0x32 || body[s7 + 1] != 0x07)
            throw new InvalidDataException("Resposta S7 inesperada.");

        var paramLength = (body[s7 + 6] << 8) | body[s7 + 7];
        var data = s7 + 10 + paramLength;
        if (body.Length < data + 12 || body[data] != 0xFF)
            throw new InvalidDataException("A CPU recusou a leitura da SZL.");

        var recordLength = (body[data + 8] << 8) | body[data + 9];
        var recordCount = (body[data + 10] << 8) | body[data + 11];
        var records = new List<byte[]>();
        var position = data + 12;
        for (var i = 0; i < recordCount && recordLength > 0 && position + recordLength <= body.Length; i++)
        {
            records.Add(body.AsSpan(position, recordLength).ToArray());
            position += recordLength;
        }
        return records;
    }

    private static async Task<byte[]> S7ExchangeAsync(NetworkStream stream, byte[] request, CancellationToken ct)
    {
        await stream.WriteAsync(request, ct).ConfigureAwait(false);
        var tpkt = new byte[4];
        await stream.ReadExactlyAsync(tpkt, ct).ConfigureAwait(false);
        var length = (tpkt[2] << 8) | tpkt[3];
        if (length is < 7 or > 4096) throw new InvalidDataException("TPKT inválido.");
        var body = new byte[length - 4];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return body;
    }

    private static string AsciiText(byte[] buffer, int offset, int count)
    {
        count = Math.Min(count, buffer.Length - offset);
        return count <= 0 ? string.Empty : Encoding.ASCII.GetString(buffer, offset, count).Trim('\0', ' ');
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
