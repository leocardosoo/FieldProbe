namespace FieldProbe.Core;

/// <summary>Estatísticas de comunicação de um driver (thread-safe).</summary>
public sealed class DriverStatistics
{
    private readonly object _lock = new();
    private long _success;
    private long _errors;
    private double _totalMs;
    private double _minMs = double.MaxValue;
    private double _maxMs;

    public void RecordSuccess(TimeSpan latency)
    {
        var ms = latency.TotalMilliseconds;
        lock (_lock)
        {
            _success++;
            _totalMs += ms;
            if (ms < _minMs) _minMs = ms;
            if (ms > _maxMs) _maxMs = ms;
        }
    }

    public void RecordError()
    {
        lock (_lock) _errors++;
    }

    public void Reset()
    {
        lock (_lock)
        {
            _success = _errors = 0;
            _totalMs = _maxMs = 0;
            _minMs = double.MaxValue;
        }
    }

    public StatisticsSnapshot Snapshot()
    {
        lock (_lock)
        {
            var total = _success + _errors;
            return new StatisticsSnapshot(
                total,
                _errors,
                _success == 0 ? 0 : _minMs,
                _success == 0 ? 0 : _totalMs / _success,
                _maxMs,
                total == 0 ? 0 : 100.0 * _errors / total);
        }
    }
}

public sealed record StatisticsSnapshot(long Requests, long Errors, double MinMs, double AvgMs, double MaxMs, double ErrorRatePercent)
{
    public override string ToString() =>
        $"Requisições: {Requests} | Erros: {Errors} ({ErrorRatePercent:F1}%) | Latência mín/méd/máx: {MinMs:F1} / {AvgMs:F1} / {MaxMs:F1} ms";
}
