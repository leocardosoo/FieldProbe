using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FieldProbe.App.Services;

/// <summary>Diagnóstico rápido de rede: ping e teste de porta TCP.</summary>
public static class NetworkCheck
{
    public static async Task<string> PingAsync(string host, int timeoutMs)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, Math.Max(500, timeoutMs));
            return reply.Status == IPStatus.Success
                ? $"Ping {host}: OK ({reply.RoundtripTime} ms)"
                : $"Ping {host}: {reply.Status} (o device pode bloquear ICMP; o teste de porta é mais confiável)";
        }
        catch (Exception ex)
        {
            return $"Ping {host}: falhou ({ex.GetBaseException().Message})";
        }
    }

    public static async Task<(bool Open, string Message)> CheckTcpPortAsync(string host, int port, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        using var tcp = new TcpClient();
        using var cts = new CancellationTokenSource(Math.Max(500, timeoutMs));
        try
        {
            await tcp.ConnectAsync(host, port, cts.Token);
            return (true, $"Porta {host}:{port}: ABERTA ({sw.ElapsedMilliseconds} ms)");
        }
        catch (OperationCanceledException)
        {
            return (false, $"Porta {host}:{port}: sem resposta em {timeoutMs} ms (firewall, rota ou IP errado)");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return (false, $"Porta {host}:{port}: FECHADA (o host respondeu, mas nenhum serviço escuta nessa porta)");
        }
        catch (Exception ex)
        {
            return (false, $"Porta {host}:{port}: erro ({ex.GetBaseException().Message})");
        }
    }

    /// <summary>Extrai host e porta de entradas como "opc.tcp://10.0.0.5:4840/path" ou "10.0.0.5".</summary>
    public static (string Host, int Port) ParseEndpoint(string hostOrUrl, int defaultPort)
    {
        var text = hostOrUrl.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            return (uri.Host, uri.Port > 0 ? uri.Port : defaultPort);
        return (text, defaultPort);
    }
}
