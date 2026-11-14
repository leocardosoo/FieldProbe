namespace FieldProbe.Core;

/// <summary>Subscription por polling para protocolos sem notificação nativa (Modbus, S7, EtherNet/IP).</summary>
public sealed class PollingSubscription : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public PollingSubscription(Func<CancellationToken, Task<TagValue>> read, int intervalMs, Action<TagValue> onValue)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(20, intervalMs));
        var token = _cts.Token;
        _ = Task.Run(() => LoopAsync(read, interval, onValue, token));
    }

    private static async Task LoopAsync(
        Func<CancellationToken, Task<TagValue>> read,
        TimeSpan interval,
        Action<TagValue> onValue,
        CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            do
            {
                var value = await read(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;
                try
                {
                    onValue(value);
                }
                catch
                {
                    // Erro do consumidor não derruba o loop.
                }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose() => _cts.Cancel();
}
