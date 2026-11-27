using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;
using FieldProbe.Drivers.Mitsubishi;
using FieldProbe.Drivers.Omron;
using FieldProbe.Drivers.Siemens;

namespace FieldProbe.App.Services;

/// <summary>CPU Mitsubishi que respondeu em MC Protocol: modelo (se informado) e série sugerida para a conexão.</summary>
public sealed record MelsecProbe(string? Model, string? Series);

/// <summary>CLP Omron que respondeu em FINS/TCP: modelo e modo (RUN, MONITOR, PROGRAM), se informados.</summary>
public sealed record FinsProbe(string? Model, string? Mode);

/// <summary>Sondas leves de identificação de protocolo (só handshake, sem ler/escrever dados de processo).</summary>
public static class Probes
{
    /// <summary>
    /// Mitsubishi MC Protocol (3E binário): pede o modelo da CPU (comando 0101). Não lê nem escreve dispositivos.
    /// Nulo se a porta não fala MC Protocol (outro serviço, ASCII ou sem resposta).
    /// </summary>
    public static async Task<FinsProbe?> FinsAsync(string ip, int port, int timeoutMs, CancellationToken ct)
    {
        using var client = new FinsClient(ip, port, timeoutMs, udp: false, 0, 0, 0);
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null; // não completou o handshake FINS/TCP
        }

        string? model = null;
        string? mode = null;
        try
        {
            model = (await client.ReadModelAsync(ct).ConfigureAwait(false)).Model;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // handshake ok: é FINS, só não informou o modelo
        }
        try
        {
            mode = (await client.ReadStatusAsync(ct).ConfigureAwait(false)).ModeText;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // modo indisponível
        }
        return new FinsProbe(string.IsNullOrWhiteSpace(model) ? null : model, mode);
    }

    public static async Task<MelsecProbe?> MelsecAsync(string ip, int port, int timeoutMs, CancellationToken ct)
    {
        using var client = new McClient(ip, port, timeoutMs, 0, 0xFF, McSeries.QL);
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
            var (model, _) = await client.ReadCpuModelAsync(ct).ConfigureAwait(false);
            var series = model.StartsWith("FX5", StringComparison.OrdinalIgnoreCase) ? MitsubishiDrivers.SeriesIqF
                : model.Length > 1 && model[0] is 'R' or 'r' && char.IsDigit(model[1]) ? MitsubishiDrivers.SeriesIqR
                : model.Length > 0 && model[0] is 'Q' or 'L' ? MitsubishiDrivers.SeriesQ
                : null;
            return new MelsecProbe(model.Length > 0 ? model : null, series);
        }
        catch (McException)
        {
            return new MelsecProbe(null, null); // respondeu em MC Protocol, só não informou o modelo
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

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
    /// Siemens S7: tenta conectar nos slots mais comuns e, se a CPU aceitar, lê a identificação
    /// (modelo, código de pedido, firmware, série, nome) e o modo de operação (RUN/STOP).
    /// É a mesma leitura do "Accessible devices" do TIA Portal: não lê nem escreve dados de processo.
    /// </summary>
    public static async Task<string?> S7CotpAsync(string ip, int timeoutMs, CancellationToken ct)
    {
        foreach (var slot in new[] { 1, 2, 0, 3 })
        {
            S7SystemClient client;
            try
            {
                client = await S7SystemClient.ConnectAsync(ip, 102, 0, slot, timeoutMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                continue; // tenta o próximo slot
            }

            await using (client)
            {
                string? identity = null;
                string? mode = null;
                try
                {
                    identity = (await client.ReadIdentityAsync(ct).ConfigureAwait(false))?.ToString();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // CPU aceitou a conexão, mas não respondeu à identificação
                }

                try
                {
                    var cpuMode = await client.ReadCpuModeAsync(ct).ConfigureAwait(false);
                    if (cpuMode != S7CpuMode.Unknown) mode = $"CPU em {S7SystemClient.DescribeMode(cpuMode)}";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // modo indisponível
                }

                var text = identity ?? "Siemens S7 · CPU aceitou conexão";
                return $"{text}{(mode is null ? string.Empty : $" · {mode}")} · rack 0 / slot {slot}";
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
