using System.Diagnostics;

namespace FieldProbe.Core;

/// <summary>
/// Base comum: latência, estatísticas, eventos, subscription por polling e reconexão automática.
/// </summary>
public abstract class DriverBase : IProtocolDriver
{
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private DateTime _lastReconnectAttempt = DateTime.MinValue;

    protected ConnectionSettings? Settings { get; private set; }

    /// <summary>Falso quando o usuário desconectou; verdadeiro enquanto a sessão deve ser mantida.</summary>
    protected bool SessionRequested { get; private set; }

    /// <summary>Incrementado a cada (re)conexão; subscriptions nativas usam para se recriar.</summary>
    public int ConnectionGeneration { get; private set; }

    public abstract string ProtocolName { get; }

    public abstract bool IsConnected { get; }

    public DriverStatistics Statistics { get; } = new();

    public event EventHandler<RawFrame>? FrameLogged;

    public event EventHandler<string>? StatusChanged;

    protected virtual bool AutoReconnect => Settings?.GetBoolOption("AutoReconnect", true) ?? true;

    // ----- Implementação específica do protocolo -----

    protected abstract Task OpenAsync(ConnectionSettings settings, CancellationToken ct);

    protected abstract Task CloseAsync();

    public abstract Task<IReadOnlyList<TagNode>> BrowseAsync(TagNode? parent = null, CancellationToken ct = default);

    protected abstract Task<object?> ReadCoreAsync(TagAddress address, CancellationToken ct);

    protected abstract Task WriteCoreAsync(TagAddress address, object value, CancellationToken ct);

    /// <summary>Descrição do endpoint para mensagens de status.</summary>
    protected virtual string Endpoint(ConnectionSettings s) => s.Port > 0 ? $"{s.Host}:{s.Port}" : s.Host;

    // ----- Conexão -----

    public async Task ConnectAsync(ConnectionSettings settings, CancellationToken ct = default)
    {
        Settings = settings;
        SessionRequested = true;
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SafeCloseAsync().ConfigureAwait(false);
            OnStatus($"Conectando em {Endpoint(settings)}...");
            await OpenAsync(settings, ct).ConfigureAwait(false);
            ConnectionGeneration++;
            OnStatus($"Conectado em {Endpoint(settings)}");
        }
        catch
        {
            SessionRequested = false;
            await SafeCloseAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        SessionRequested = false;
        await _connectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await SafeCloseAsync().ConfigureAwait(false);
        }
        finally
        {
            _connectLock.Release();
        }
        OnStatus("Desconectado");
    }

    /// <summary>Garante conexão ativa, reconectando (com intervalo mínimo) se a sessão caiu.</summary>
    protected async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (IsConnected) return;
        if (!SessionRequested || Settings is null) throw new InvalidOperationException("Não conectado.");
        if (!AutoReconnect) throw new InvalidOperationException("Conexão perdida (reconexão automática desligada).");

        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsConnected) return;
            if (DateTime.Now - _lastReconnectAttempt < ReconnectInterval)
                throw new InvalidOperationException("Conexão perdida. Aguardando nova tentativa de reconexão...");

            _lastReconnectAttempt = DateTime.Now;
            OnStatus($"Conexão perdida. Reconectando em {Endpoint(Settings)}...");
            await SafeCloseAsync().ConfigureAwait(false);
            try
            {
                await OpenAsync(Settings, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnStatus($"Falha na reconexão: {ex.Message}");
                throw;
            }
            ConnectionGeneration++;
            OnStatus($"Reconectado em {Endpoint(Settings)}");
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task SafeCloseAsync()
    {
        try
        {
            await CloseAsync().ConfigureAwait(false);
        }
        catch
        {
            // Fechamento é best-effort.
        }
    }

    // ----- Leitura / escrita -----

    public async Task<TagValue> ReadAsync(TagAddress address, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            var value = await ReadCoreAsync(address, ct).ConfigureAwait(false);
            sw.Stop();
            Statistics.RecordSuccess(sw.Elapsed);
            return new TagValue(address, value, Quality.Good, DateTime.Now, sw.Elapsed, null, value?.GetType().Name);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Statistics.RecordError();
            return new TagValue(address, null, Quality.Bad, DateTime.Now, sw.Elapsed, ex.Message);
        }
    }

    public async Task WriteAsync(TagAddress address, object value, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        await WriteCoreAsync(address, value, ct).ConfigureAwait(false);
        OnTrace($"Escrita OK: {address.Address} <= {value}");
    }

    public virtual IDisposable Subscribe(TagAddress address, int intervalMs, Action<TagValue> onValue) =>
        new PollingSubscription(ct => ReadAsync(address, ct), intervalMs, onValue);

    // ----- Eventos -----

    protected void OnFrame(FrameDirection direction, byte[] data, string? description = null) =>
        FrameLogged?.Invoke(this, new RawFrame(DateTime.Now, direction, data, description));

    /// <summary>Registro descritivo (sem bytes) para protocolos cuja biblioteca não expõe o tráfego bruto.</summary>
    protected void OnTrace(string description, FrameDirection direction = FrameDirection.Info) =>
        FrameLogged?.Invoke(this, new RawFrame(DateTime.Now, direction, Array.Empty<byte>(), description));

    protected void OnStatus(string message) => StatusChanged?.Invoke(this, message);

    public virtual async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
