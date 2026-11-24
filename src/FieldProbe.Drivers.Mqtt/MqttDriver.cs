using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FieldProbe.Core;
using FieldProbe.Core.Codec;

namespace FieldProbe.Drivers.Mqtt;

/// <summary>
/// Driver MQTT 3.1.1. Descobre tópicos automaticamente, monta a árvore pela hierarquia "/",
/// monitora por notificação nativa e extrai campos de payload JSON com "tópico|campo.subcampo".
/// </summary>
public sealed class MqttDriver : DriverBase, IDiagnosticsProvider, IFrameDecoder
{
    public const string Protocol = "MQTT";

    /// <summary>Explica os bytes de um pacote MQTT do log.</summary>
    public DecodedFrame? DecodeFrame(byte[] data, FrameDirection direction) =>
        data.Length >= 2 ? MqttFrameDecoder.Decode(data) : null;

    private readonly ConcurrentDictionary<string, CachedMessage> _cache = new();
    private readonly List<Subscriber> _subscribers = new();
    private readonly object _subscribersLock = new();
    private MqttConnection? _connection;
    private CancellationTokenSource? _reconnectCts;
    private long _messagesReceived;

    private sealed record CachedMessage(byte[] Payload, DateTime Timestamp, bool Retain);

    private sealed class Subscriber
    {
        public required string Filter { get; init; }
        public required TagAddress Address { get; init; }
        public required Action<TagValue> Callback { get; init; }
        public bool OwnSubscription { get; set; }
    }

    public override string ProtocolName => Protocol;

    public override bool IsConnected => _connection?.IsConnected == true;

    private string DiscoveryFilter => Settings?.GetOption("DiscoveryTopic", "#") ?? "#";

    private int Qos => Settings?.GetIntOption("Qos", 0) ?? 0;

    // ----- Conexão -----

    protected override async Task OpenAsync(ConnectionSettings settings, CancellationToken ct)
    {
        var useTls = settings.GetBoolOption("UseTls", false);
        var clientId = settings.GetOption("ClientId", string.Empty);
        if (clientId.Length == 0) clientId = $"fieldprobe-{Guid.NewGuid():N}"[..20];

        var connection = new MqttConnection();
        connection.Frame += (direction, data, description) => OnFrame(direction, data, description);
        connection.MessageReceived += OnMessage;
        connection.ConnectionLost += OnConnectionLost;

        var user = settings.GetOption("User", string.Empty);
        await connection.ConnectAsync(new MqttOptions(
            settings.Host,
            settings.Port > 0 ? settings.Port : (useTls ? 8883 : 1883),
            clientId,
            user.Length > 0 ? user : null,
            user.Length > 0 ? settings.GetOption("Password", string.Empty) : null,
            useTls,
            settings.GetBoolOption("AcceptAnyCertificate", true),
            settings.GetIntOption("KeepAlive", 30),
            settings.TimeoutMs), ct).ConfigureAwait(false);

        _connection = connection;

        // Tópico de descoberta + inscrições específicas que não estão cobertas por ele.
        var filters = new List<string>();
        if (DiscoveryFilter.Length > 0) filters.Add(DiscoveryFilter);
        lock (_subscribersLock)
        {
            foreach (var s in _subscribers.Where(s => s.OwnSubscription)) filters.Add(s.Filter);
        }

        if (filters.Count > 0)
            await connection.SubscribeAsync(filters.Distinct().ToList(), Qos, ct).ConfigureAwait(false);

        _reconnectCts?.Cancel();
        _reconnectCts = new CancellationTokenSource();
    }

    protected override async Task CloseAsync()
    {
        var connection = _connection;
        _connection = null;
        if (connection is null) return;

        connection.ConnectionLost -= OnConnectionLost;
        connection.MessageReceived -= OnMessage;
        await connection.DisconnectAsync().ConfigureAwait(false);
    }

    private void OnConnectionLost(string reason)
    {
        OnStatus($"Conexão com o broker perdida: {reason}");
        NotifyConnectionLost(reason);
        if (!SessionRequested || !AutoReconnect) return;

        var token = _reconnectCts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            while (SessionRequested && !IsConnected && !token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(3000, token).ConfigureAwait(false);
                    await EnsureConnectedAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // nova tentativa no próximo ciclo
                }
            }
        }, token);
    }

    /// <summary>Sem broker não chegam mensagens: todos os itens monitorados passam a Bad até a próxima mensagem.</summary>
    private void NotifyConnectionLost(string reason)
    {
        List<Subscriber> subscribers;
        lock (_subscribersLock) subscribers = _subscribers.ToList();
        foreach (var subscriber in subscribers)
        {
            try
            {
                subscriber.Callback(new TagValue(subscriber.Address, null, Quality.Bad, DateTime.Now, TimeSpan.Zero,
                    $"Sem conexão com o broker ({reason}): valor não está sendo atualizado"));
            }
            catch
            {
                // um item com problema não impede os outros
            }
        }
    }

    // ----- Mensagens -----

    private void OnMessage(string topic, byte[] payload, bool retain)
    {
        Interlocked.Increment(ref _messagesReceived);
        var message = new CachedMessage(payload, DateTime.Now, retain);
        _cache[topic] = message;

        Subscriber[] targets;
        lock (_subscribersLock)
        {
            targets = _subscribers.Where(s => TopicMatches(s.Filter, topic)).ToArray();
        }

        foreach (var subscriber in targets)
            Emit(subscriber, topic, message);
    }

    private void Emit(Subscriber subscriber, string topic, CachedMessage message)
    {
        TagValue value;
        try
        {
            var decoded = Decode(message.Payload, ParseAddress(subscriber.Address.Address).Path, subscriber.Address.DataType);
            value = new TagValue(subscriber.Address, decoded, Quality.Good, message.Timestamp, TimeSpan.Zero, null,
                IsWildcard(subscriber.Filter) ? topic : decoded?.GetType().Name);
        }
        catch (Exception ex)
        {
            value = new TagValue(subscriber.Address, null, Quality.Bad, message.Timestamp, TimeSpan.Zero, ex.Message);
        }

        try
        {
            subscriber.Callback(value);
        }
        catch
        {
            // erro do consumidor não derruba o driver
        }
    }

    // ----- Navegação -----

    public override Task<IReadOnlyList<TagNode>> BrowseAsync(TagNode? parent = null, CancellationToken ct = default)
    {
        if (parent is not null)
            return Task.FromResult<IReadOnlyList<TagNode>>(parent.Children);

        if (_cache.IsEmpty)
        {
            IReadOnlyList<TagNode> empty = new[]
            {
                new TagNode($"(nenhuma mensagem ainda em '{DiscoveryFilter}' · use Atualizar em alguns segundos)")
            };
            return Task.FromResult(empty);
        }

        var root = new TrieNode(string.Empty, string.Empty);
        foreach (var topic in _cache.Keys)
        {
            var current = root;
            var levels = topic.Split('/');
            for (var i = 0; i < levels.Length; i++)
            {
                var path = string.Join('/', levels.Take(i + 1));
                if (!current.Children.TryGetValue(levels[i], out var next))
                {
                    next = new TrieNode(levels[i].Length == 0 ? "(vazio)" : levels[i], path);
                    current.Children[levels[i]] = next;
                }
                current = next;
            }
            current.IsTopic = true;
        }

        IReadOnlyList<TagNode> nodes = root.Children.Values
            .OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(ToTagNode)
            .ToList();
        return Task.FromResult(nodes);
    }

    private TagNode ToTagNode(TrieNode trie)
    {
        var preview = trie.IsTopic && _cache.TryGetValue(trie.Path, out var message)
            ? $"{trie.Name}  =  {MqttConnection.Preview(message.Payload)}"
            : trie.Name;

        var node = trie.IsTopic
            ? new TagNode(preview, new TagAddress(trie.Path, DataType.Auto)) { Key = trie.Path, Description = trie.Path }
            : new TagNode(trie.Name) { Key = trie.Path, Description = trie.Path };

        foreach (var child in trie.Children.Values.OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase))
            node.Children.Add(ToTagNode(child));
        return node;
    }

    private sealed class TrieNode
    {
        public TrieNode(string name, string path)
        {
            Name = name;
            Path = path;
        }

        public string Name { get; }
        public string Path { get; }
        public bool IsTopic { get; set; }
        public Dictionary<string, TrieNode> Children { get; } = new();
    }

    // ----- Leitura / escrita / monitoramento -----

    protected override Task<object?> ReadCoreAsync(TagAddress address, CancellationToken ct)
    {
        var (topic, path) = ParseAddress(address.Address);
        if (IsWildcard(topic))
            throw new InvalidOperationException("Leitura pontual não aceita curingas. Use monitoramento (duplo clique) para tópicos com + ou #.");

        if (!_cache.TryGetValue(topic, out var message))
            throw new InvalidOperationException(TopicMatches(DiscoveryFilter, topic)
                ? "Nenhuma mensagem recebida neste tópico ainda."
                : $"O tópico não está coberto pelo tópico de descoberta '{DiscoveryFilter}'. Adicione-o à watch list para inscrever.");

        return Task.FromResult(Decode(message.Payload, path, address.DataType));
    }

    protected override async Task WriteCoreAsync(TagAddress address, object value, CancellationToken ct)
    {
        var connection = _connection ?? throw new InvalidOperationException("Não conectado.");
        var (topic, _) = ParseAddress(address.Address);
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        await connection.PublishAsync(topic, Encoding.UTF8.GetBytes(text), Qos, Settings?.GetBoolOption("Retain", false) ?? false, ct).ConfigureAwait(false);
    }

    public override IDisposable Subscribe(TagAddress address, int intervalMs, Action<TagValue> onValue)
    {
        var (topic, _) = ParseAddress(address.Address);
        var subscriber = new Subscriber
        {
            Filter = topic,
            Address = address,
            Callback = onValue,
            OwnSubscription = !TopicMatchesFilterCoverage(DiscoveryFilter, topic)
        };

        lock (_subscribersLock) _subscribers.Add(subscriber);

        if (subscriber.OwnSubscription && _connection is { IsConnected: true } connection)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await connection.SubscribeAsync(new[] { topic }, Qos, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    subscriber.Callback(new TagValue(address, null, Quality.Bad, DateTime.Now, TimeSpan.Zero, ex.Message));
                }
            });
        }

        // Valor atual (mensagem retida ou última recebida), se houver.
        var existing = _cache.Where(kv => TopicMatches(topic, kv.Key)).OrderByDescending(kv => kv.Value.Timestamp).FirstOrDefault();
        if (existing.Key is not null)
            Emit(subscriber, existing.Key, existing.Value);
        else
            onValue(new TagValue(address, null, Quality.Uncertain, DateTime.Now, TimeSpan.Zero, "Aguardando a primeira mensagem"));

        return new Unsubscriber(this, subscriber);
    }

    private sealed class Unsubscriber : IDisposable
    {
        private readonly MqttDriver _driver;
        private readonly Subscriber _subscriber;

        public Unsubscriber(MqttDriver driver, Subscriber subscriber)
        {
            _driver = driver;
            _subscriber = subscriber;
        }

        public void Dispose()
        {
            bool stillUsed;
            lock (_driver._subscribersLock)
            {
                _driver._subscribers.Remove(_subscriber);
                stillUsed = _driver._subscribers.Any(s => s.Filter == _subscriber.Filter);
            }

            if (_subscriber.OwnSubscription && !stillUsed && _driver._connection is { IsConnected: true } connection)
                _ = connection.UnsubscribeAsync(new[] { _subscriber.Filter }, CancellationToken.None);
        }
    }

    // ----- Payload -----

    internal static (string Topic, string Path) ParseAddress(string address)
    {
        var bar = address.IndexOf('|');
        return bar < 0 ? (address.Trim(), string.Empty) : (address[..bar].Trim(), address[(bar + 1)..].Trim());
    }

    /// <summary>Decodifica o payload: texto, número, booleano ou campo JSON (caminho "a.b[0].c").</summary>
    internal static object? Decode(byte[] payload, string jsonPath, DataType type)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(payload);
        }
        catch (DecoderFallbackException)
        {
            return "0x" + Convert.ToHexString(payload);
        }

        if (jsonPath.Length > 0)
        {
            using var document = JsonDocument.Parse(text);
            var element = document.RootElement;
            foreach (var segment in SplitPath(jsonPath))
            {
                if (int.TryParse(segment, out var index) && element.ValueKind == JsonValueKind.Array)
                {
                    if (index >= element.GetArrayLength()) throw new InvalidOperationException($"Índice [{index}] fora do array.");
                    element = element[index];
                }
                else if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(segment, out var child))
                {
                    element = child;
                }
                else
                {
                    throw new InvalidOperationException($"Campo '{segment}' não existe no JSON.");
                }
            }

            object? value = element.ValueKind switch
            {
                JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Null => null,
                _ => element.GetRawText()
            };
            return type == DataType.Auto || value is null ? value : ValueCodec.ConvertTo(value, type);
        }

        if (type != DataType.Auto)
            return ValueCodec.ConvertTo(text.Trim(), type);

        var trimmed = text.Trim();
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return integer;
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
        if (bool.TryParse(trimmed, out var boolean)) return boolean;
        return trimmed.Length > 2000 ? trimmed[..2000] + "…" : trimmed;
    }

    private static IEnumerable<string> SplitPath(string path)
    {
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bracket = part.IndexOf('[');
            if (bracket < 0)
            {
                yield return part;
                continue;
            }

            if (bracket > 0) yield return part[..bracket];
            foreach (var index in part[bracket..].Split(new[] { '[', ']' }, StringSplitOptions.RemoveEmptyEntries))
                yield return index;
        }
    }

    // ----- Curingas MQTT -----

    internal static bool IsWildcard(string topic) => topic.Contains('+') || topic.Contains('#');

    /// <summary>Verifica se um tópico casa com um filtro MQTT (+ = um nível, # = todos os níveis seguintes).</summary>
    internal static bool TopicMatches(string filter, string topic)
    {
        if (filter.Length == 0) return false;
        if (topic.StartsWith('$') && !filter.StartsWith('$')) return false; // tópicos de sistema não casam com curinga inicial

        var f = filter.Split('/');
        var t = topic.Split('/');
        for (var i = 0; i < f.Length; i++)
        {
            if (f[i] == "#") return true;
            if (i >= t.Length) return false;
            if (f[i] != "+" && f[i] != t[i]) return false;
        }
        return f.Length == t.Length;
    }

    /// <summary>Se tudo que o filtro <paramref name="inner"/> recebe já chega pelo filtro <paramref name="outer"/>.</summary>
    private static bool TopicMatchesFilterCoverage(string outer, string inner)
    {
        if (outer.Length == 0) return false;
        if (outer == "#") return !inner.StartsWith('$');
        if (!IsWildcard(inner)) return TopicMatches(outer, inner);
        return outer == inner;
    }

    // ----- Diagnóstico -----

    public Task<IReadOnlyList<DiagnosticItem>> RunDiagnosticsAsync(CancellationToken ct = default)
    {
        var s = Settings;
        var items = new List<DiagnosticItem>
        {
            new("Broker", s is null ? "-" : $"{s.Host}:{s.Port}"),
            new("Sessão", IsConnected ? $"Conectado · sessão existente no broker: {(_connection?.SessionPresent == true ? "sim" : "não")}" : "Desconectado",
                IsConnected ? DiagnosticSeverity.Ok : DiagnosticSeverity.Warning),
            new("Tráfego", $"{Interlocked.Read(ref _messagesReceived)} mensagens recebidas · {_cache.Count} tópicos distintos · {_cache.Values.Count(m => m.Retain)} retidos"),
            new("Descoberta", $"Tópico de descoberta: '{DiscoveryFilter}'")
        };

        if (s is not null)
        {
            items.Add(s.GetBoolOption("UseTls", false)
                ? new DiagnosticItem("Segurança", "TLS habilitado.", DiagnosticSeverity.Ok)
                : new DiagnosticItem("Segurança", "Sem TLS: usuário, senha e dados trafegam em texto aberto.", DiagnosticSeverity.Warning));

            if (s.GetOption("User", string.Empty).Length == 0)
                items.Add(new("Segurança", "Conectado sem usuário: o broker aceita clientes anônimos.", DiagnosticSeverity.Warning));

            if (s.GetBoolOption("UseTls", false) && s.GetBoolOption("AcceptAnyCertificate", true))
                items.Add(new("Segurança", "Qualquer certificado do broker está sendo aceito (útil em teste, inseguro em produção).", DiagnosticSeverity.Warning));
        }

        var busiest = _cache.OrderByDescending(kv => kv.Value.Timestamp).Take(5).Select(kv => kv.Key).ToList();
        if (busiest.Count > 0)
            items.Add(new("Tópicos recentes", string.Join(" | ", busiest)));

        return Task.FromResult<IReadOnlyList<DiagnosticItem>>(items);
    }
}

public static class MqttDrivers
{
    public static DriverInfo Info { get; } = new()
    {
        Protocol = MqttDriver.Protocol,
        Category = "IIoT",
        Description = "Cliente MQTT 3.1.1 (TCP/TLS): descoberta de tópicos, publish, JSON e retained.",
        Factory = () => new MqttDriver(),
        HostLabel = "Broker (host/IP)",
        DefaultHost = "127.0.0.1",
        DefaultPort = 1883,
        AddressHelp = "Tópico: fabrica/linha1/temperatura · campo JSON: fabrica/linha1|dados.temp · curingas no monitoramento: fabrica/+/status",
        AddressExample = "fabrica/linha1/temperatura",
        DefaultDataType = DataType.Auto,
        Options = new[]
        {
            new DriverOption("ClientId", "Client ID", OptionKind.Text, string.Empty, Help: "Vazio = gerado automaticamente."),
            new DriverOption("User", "Usuário", OptionKind.Text, string.Empty),
            new DriverOption("Password", "Senha", OptionKind.Password, string.Empty),
            new DriverOption("UseTls", "Usar TLS (porta 8883)", OptionKind.Bool, "false"),
            new DriverOption("AcceptAnyCertificate", "Aceitar qualquer certificado", OptionKind.Bool, "true", Help: "Para brokers com certificado autoassinado."),
            new DriverOption("DiscoveryTopic", "Tópico de descoberta", OptionKind.Text, "#", Help: "Filtro inscrito ao conectar para montar a árvore. Ex.: fabrica/#. Vazio = nenhum."),
            new DriverOption("Qos", "QoS", OptionKind.Choice, "0", new[] { "0", "1" }),
            new DriverOption("Retain", "Publicar com retain", OptionKind.Bool, "false"),
            new DriverOption("KeepAlive", "Keep alive (s)", OptionKind.Number, "30"),
            new DriverOption("AutoReconnect", "Reconexão automática", OptionKind.Bool, "true")
        }
    };
}
