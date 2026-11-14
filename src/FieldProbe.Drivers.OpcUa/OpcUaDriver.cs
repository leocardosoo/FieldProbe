using System.Net;
using FieldProbe.Core;
using FieldProbe.Core.Codec;
using Workstation.ServiceModel.Ua;
using Workstation.ServiceModel.Ua.Channels;
using FpDataType = FieldProbe.Core.DataType;

namespace FieldProbe.Drivers.OpcUa;

/// <summary>
/// Driver OPC UA (cliente) baseado no Workstation.UaClient (MIT).
/// Navegação sob demanda, leitura/escrita com tipo nativo, subscriptions nativas e diagnóstico de segurança.
/// </summary>
public sealed class OpcUaDriver : DriverBase, IDiagnosticsProvider
{
    public const string Protocol = "OPC UA";

    private const string PolicyPrefix = "http://opcfoundation.org/UA/SecurityPolicy#";

    private ClientSessionChannel? _channel;

    public override string ProtocolName => Protocol;

    public override bool IsConnected => _channel?.State == CommunicationState.Opened;

    internal ClientSessionChannel? Channel => _channel;

    protected override string Endpoint(ConnectionSettings s) => NormalizeUrl(s);

    // ----- Conexão -----

    protected override async Task OpenAsync(ConnectionSettings settings, CancellationToken ct)
    {
        var url = NormalizeUrl(settings);
        var policyName = settings.GetOption("SecurityPolicy", "Auto");

        var description = new ApplicationDescription
        {
            ApplicationName = "FieldProbe",
            ApplicationUri = $"urn:{Dns.GetHostName()}:FieldProbe",
            ApplicationType = ApplicationType.Client
        };

        // Certificado da aplicação é criado automaticamente nesta pasta na primeira conexão segura.
        var pki = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FieldProbe", "pki");
        var store = new DirectoryStore(pki);

        var user = settings.GetOption("User", string.Empty);
        IUserIdentity identity = user.Length > 0
            ? new UserNameIdentity(user, settings.GetOption("Password", string.Empty))
            : new AnonymousIdentity();

        var channel = new ClientSessionChannel(description, store, identity, url, PolicyUri(policyName));
        OnTrace($"OpenSecureChannel + CreateSession → {url} · política {policyName} · login {(user.Length > 0 ? user : "anônimo")}", FrameDirection.Tx);

        var timeout = TimeSpan.FromMilliseconds(Math.Max(5000, settings.TimeoutMs * 2));
        try
        {
            await channel.OpenAsync().WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await AbortQuietly(channel).ConfigureAwait(false);
            throw new TimeoutException($"O servidor não respondeu em {timeout.TotalSeconds:F0} s. Confira a URL, a porta e o firewall.");
        }
        catch (Exception ex)
        {
            await AbortQuietly(channel).ConfigureAwait(false);
            throw new InvalidOperationException(ExplainOpenError(ex), ex);
        }

        _channel = channel;
        var endpoint = channel.RemoteEndpoint;
        OnTrace($"Sessão aberta · {endpoint?.EndpointUrl} · {ShortPolicy(endpoint?.SecurityPolicyUri)} / {endpoint?.SecurityMode}", FrameDirection.Rx);
    }

    protected override async Task CloseAsync()
    {
        var channel = _channel;
        _channel = null;
        if (channel is null) return;

        try
        {
            await channel.CloseAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch
        {
            await AbortQuietly(channel).ConfigureAwait(false);
        }
    }

    internal Task EnsureSessionAsync(CancellationToken ct) => EnsureConnectedAsync(ct);

    internal void Trace(string message) => OnTrace(message);

    // ----- Navegação -----

    public override async Task<IReadOnlyList<TagNode>> BrowseAsync(TagNode? parent = null, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var channel = _channel ?? throw new InvalidOperationException("Não conectado.");
        var nodeId = parent?.Key ?? ObjectIds.ObjectsFolder;

        var request = new BrowseRequest
        {
            NodesToBrowse = new[]
            {
                new BrowseDescription
                {
                    NodeId = NodeId.Parse(nodeId),
                    BrowseDirection = BrowseDirection.Forward,
                    ReferenceTypeId = NodeId.Parse(ReferenceTypeIds.HierarchicalReferences),
                    NodeClassMask = (uint)NodeClass.Variable | (uint)NodeClass.Object,
                    IncludeSubtypes = true,
                    ResultMask = (uint)BrowseResultMask.All
                }
            },
            RequestedMaxReferencesPerNode = 2000
        };

        OnTrace($"Browse {nodeId}", FrameDirection.Tx);
        var response = await channel.BrowseAsync(request).ConfigureAwait(false);
        var references = response.Results?.FirstOrDefault()?.References ?? Array.Empty<ReferenceDescription>();
        OnTrace($"Browse {nodeId}: {references.Length} referências", FrameDirection.Rx);

        var nodes = new List<TagNode>();
        foreach (var reference in references)
        {
            if (reference.NodeId is null) continue;
            var id = ExpandedNodeId.ToNodeId(reference.NodeId, channel.NamespaceUris)?.ToString();
            if (string.IsNullOrEmpty(id)) continue;

            var name = reference.DisplayName?.Text;
            if (string.IsNullOrWhiteSpace(name)) name = reference.BrowseName?.Name ?? id;

            nodes.Add(reference.NodeClass == NodeClass.Variable
                ? new TagNode(name, new TagAddress(id, FpDataType.Auto)) { Key = id, Description = id, HasLazyChildren = false }
                : new TagNode(name) { Key = id, Description = id, HasLazyChildren = true });
        }

        return nodes
            .OrderBy(n => n.IsFolder ? 0 : 1)
            .ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // ----- Leitura / escrita -----

    protected override async Task<object?> ReadCoreAsync(TagAddress address, CancellationToken ct)
    {
        var dataValue = await ReadDataValueAsync(address.Address).ConfigureAwait(false);
        if (StatusCode.IsBad(dataValue.StatusCode))
            throw new InvalidOperationException($"O servidor retornou {dataValue.StatusCode} para {address.Address}.");

        return ConvertValue(dataValue.Value, address.DataType);
    }

    protected override async Task WriteCoreAsync(TagAddress address, object value, CancellationToken ct)
    {
        var channel = _channel ?? throw new InvalidOperationException("Não conectado.");

        object typed;
        if (address.DataType != FpDataType.Auto)
        {
            typed = ValueCodec.ConvertTo(value, address.DataType);
        }
        else
        {
            // Descobre o tipo nativo lendo o valor atual: o servidor exige o tipo exato.
            var current = await ReadDataValueAsync(address.Address).ConfigureAwait(false);
            if (current.Value is Array)
                throw new NotSupportedException("Escrita de arrays ainda não é suportada.");
            typed = current.Value is null ? value : ValueCodec.ConvertTo(value, current.Value.GetType());
        }

        OnTrace($"Write {address.Address} = {typed} ({typed.GetType().Name})", FrameDirection.Tx);
        var response = await channel.WriteAsync(new WriteRequest
        {
            NodesToWrite = new[]
            {
                new WriteValue
                {
                    NodeId = NodeId.Parse(address.Address),
                    AttributeId = AttributeIds.Value,
                    Value = new DataValue(typed)
                }
            }
        }).ConfigureAwait(false);

        var result = response.Results?.FirstOrDefault() ?? default;
        if (StatusCode.IsBad(result))
            throw new InvalidOperationException($"O servidor recusou a escrita: {result}. Verifique se o nó permite escrita (AccessLevel) e o tipo de dado.");
    }

    internal async Task<DataValue> ReadDataValueAsync(string nodeId)
    {
        var channel = _channel ?? throw new InvalidOperationException("Não conectado.");
        var response = await channel.ReadAsync(new ReadRequest
        {
            NodesToRead = new[] { new ReadValueId { NodeId = NodeId.Parse(nodeId), AttributeId = AttributeIds.Value } },
            TimestampsToReturn = TimestampsToReturn.Both
        }).ConfigureAwait(false);

        return response.Results?.FirstOrDefault() ?? throw new InvalidDataException("Resposta de leitura vazia.");
    }

    internal static object? ConvertValue(object? value, FpDataType type) =>
        type == FpDataType.Auto || value is null || value is Array ? value : ValueCodec.ConvertTo(value, type);

    public override IDisposable Subscribe(TagAddress address, int intervalMs, Action<TagValue> onValue) =>
        new OpcUaMonitoredItem(this, address, intervalMs, onValue);

    // ----- Diagnóstico -----

    public async Task<IReadOnlyList<DiagnosticItem>> RunDiagnosticsAsync(CancellationToken ct = default)
    {
        var items = new List<DiagnosticItem>();
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var channel = _channel!;
        var endpoint = channel.RemoteEndpoint;

        items.Add(new("Endpoint", endpoint?.EndpointUrl ?? "-"));

        var policy = ShortPolicy(endpoint?.SecurityPolicyUri);
        var mode = endpoint?.SecurityMode.ToString() ?? "-";
        var severity = policy switch
        {
            "None" => DiagnosticSeverity.Critical,
            "Basic128Rsa15" or "Basic256" => DiagnosticSeverity.Warning,
            _ => DiagnosticSeverity.Ok
        };
        items.Add(new("Segurança", $"Política: {policy} · Modo: {mode}", severity));
        if (policy == "None")
            items.Add(new("Segurança", "Sessão sem criptografia nem assinatura. Em produção, exija Basic256Sha256 ou superior.", DiagnosticSeverity.Critical));
        if (policy is "Basic128Rsa15" or "Basic256")
            items.Add(new("Segurança", $"A política {policy} está obsoleta (deprecada pela OPC Foundation).", DiagnosticSeverity.Warning));

        var tokens = endpoint?.UserIdentityTokens?.Select(t => t.TokenType.ToString()).Distinct().ToList() ?? new List<string>();
        items.Add(new("Autenticação", $"Tipos de login aceitos neste endpoint: {(tokens.Count == 0 ? "-" : string.Join(", ", tokens))}",
            tokens.Contains("Anonymous") ? DiagnosticSeverity.Warning : DiagnosticSeverity.Ok));
        if (tokens.Contains("Anonymous"))
            items.Add(new("Autenticação", "Acesso anônimo permitido: qualquer cliente na rede pode ler (e talvez escrever).", DiagnosticSeverity.Warning));

        try
        {
            var status = await ReadDataValueAsync(VariableIds.Server_ServerStatus).ConfigureAwait(false);
            if (status.Value is ServerStatusDataType serverStatus)
            {
                var build = serverStatus.BuildInfo;
                items.Add(new("Servidor", $"Produto: {build?.ProductName} · Fabricante: {build?.ManufacturerName}"));
                items.Add(new("Servidor", $"Versão: {build?.SoftwareVersion} (build {build?.BuildNumber})"));
                items.Add(new("Servidor", $"Estado: {serverStatus.State} · Hora do servidor: {serverStatus.CurrentTime.ToLocalTime():dd/MM/yyyy HH:mm:ss}",
                    serverStatus.State == ServerState.Running ? DiagnosticSeverity.Ok : DiagnosticSeverity.Warning));

                var drift = (DateTime.UtcNow - serverStatus.CurrentTime.ToUniversalTime()).TotalSeconds;
                if (Math.Abs(drift) > 5)
                    items.Add(new("Servidor", $"Relógio do servidor difere {drift:F0} s deste PC. Diferenças grandes quebram a validação de certificados.", DiagnosticSeverity.Warning));
            }
        }
        catch (Exception ex)
        {
            items.Add(new("Servidor", $"Não foi possível ler ServerStatus: {ex.Message}", DiagnosticSeverity.Warning));
        }

        items.Add(new("Namespaces", string.Join(" | ", channel.NamespaceUris.Select((uri, i) => $"ns={i}: {uri}"))));
        items.Add(new("Estatísticas", Statistics.Snapshot().ToString()));
        return items;
    }

    // ----- Utilitários -----

    private static string NormalizeUrl(ConnectionSettings settings)
    {
        var url = settings.Host.Trim();
        if (url.StartsWith("opc.tcp://", StringComparison.OrdinalIgnoreCase)) return url;
        var port = settings.Port > 0 ? settings.Port : 4840;
        return url.Contains(':') ? $"opc.tcp://{url}" : $"opc.tcp://{url}:{port}";
    }

    private static string? PolicyUri(string name) =>
        name.Equals("Auto", StringComparison.OrdinalIgnoreCase) || name.Length == 0 ? null : PolicyPrefix + name;

    private static string ShortPolicy(string? uri) =>
        string.IsNullOrEmpty(uri) ? "-" : uri[(uri.LastIndexOf('#') + 1)..];

    private static string ExplainOpenError(Exception ex)
    {
        var message = ex.GetBaseException().Message;
        if (message.Contains("same certificate", StringComparison.OrdinalIgnoreCase))
            return "O servidor devolveu um certificado diferente do anunciado no endpoint. Isso acontece em servidores sem certificado configurado " +
                   "(comum em servidores Python asyncua/python-opcua e em alguns gateways). Configure um certificado de aplicação no servidor. " +
                   $"Detalhe: {message}";
        if (message.Contains("BadIdentityToken", StringComparison.OrdinalIgnoreCase) || message.Contains("BadUserAccessDenied", StringComparison.OrdinalIgnoreCase))
            return $"Login recusado pelo servidor ({message}). Confira usuário/senha ou se o acesso anônimo está habilitado.";
        if (message.Contains("BadSecurityChecksFailed", StringComparison.OrdinalIgnoreCase) || message.Contains("BadCertificate", StringComparison.OrdinalIgnoreCase))
            return $"Falha de segurança/certificado ({message}). O servidor provavelmente ainda não confia no certificado do FieldProbe: aceite-o na lista de certificados do servidor (pasta 'rejected' → 'trusted') e tente de novo.";
        if (message.Contains("BadSecurityPolicyRejected", StringComparison.OrdinalIgnoreCase) || message.Contains("No endpoint", StringComparison.OrdinalIgnoreCase))
            return $"O servidor não oferece a política de segurança escolhida ({message}). Use 'Auto' ou rode o diagnóstico.";
        return message;
    }

    private static async Task AbortQuietly(ClientSessionChannel channel)
    {
        try
        {
            await channel.AbortAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignora
        }
    }
}

/// <summary>Monitoramento nativo (Subscription + MonitoredItem), recriado automaticamente após reconexão.</summary>
internal sealed class OpcUaMonitoredItem : IDisposable, IObserver<PublishResponse>
{
    private readonly OpcUaDriver _driver;
    private readonly TagAddress _address;
    private readonly double _intervalMs;
    private readonly Action<TagValue> _onValue;
    private readonly CancellationTokenSource _cts = new();
    private ClientSessionChannel? _channel;
    private IDisposable? _streamToken;
    private uint _subscriptionId;
    private int _generation = -1;
    private bool _receivedAny;

    public OpcUaMonitoredItem(OpcUaDriver driver, TagAddress address, int intervalMs, Action<TagValue> onValue)
    {
        _driver = driver;
        _address = address;
        _intervalMs = Math.Max(50, intervalMs);
        _onValue = onValue;
        var token = _cts.Token;
        _ = Task.Run(() => SupervisorAsync(token));
    }

    private async Task SupervisorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_generation != _driver.ConnectionGeneration || !_driver.IsConnected || _channel?.State != CommunicationState.Opened)
                {
                    await _driver.EnsureSessionAsync(ct).ConfigureAwait(false);
                    await CreateAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Emit(new TagValue(_address, null, Quality.Bad, DateTime.Now, TimeSpan.Zero, ex.Message));
            }

            try
            {
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await DeleteAsync().ConfigureAwait(false);
    }

    private async Task CreateAsync()
    {
        await DeleteAsync().ConfigureAwait(false);

        var channel = _driver.Channel ?? throw new InvalidOperationException("Não conectado.");
        var generation = _driver.ConnectionGeneration;

        var subscription = await channel.CreateSubscriptionAsync(new CreateSubscriptionRequest
        {
            RequestedPublishingInterval = _intervalMs,
            RequestedMaxKeepAliveCount = 10,
            RequestedLifetimeCount = 30,
            PublishingEnabled = true
        }).ConfigureAwait(false);

        _channel = channel;
        _subscriptionId = subscription.SubscriptionId;
        _streamToken = channel.Subscribe(this);

        var created = await channel.CreateMonitoredItemsAsync(new CreateMonitoredItemsRequest
        {
            SubscriptionId = _subscriptionId,
            TimestampsToReturn = TimestampsToReturn.Both,
            ItemsToCreate = new[]
            {
                new MonitoredItemCreateRequest
                {
                    ItemToMonitor = new ReadValueId { NodeId = NodeId.Parse(_address.Address), AttributeId = AttributeIds.Value },
                    MonitoringMode = MonitoringMode.Reporting,
                    RequestedParameters = new MonitoringParameters
                    {
                        ClientHandle = 1,
                        SamplingInterval = _intervalMs,
                        QueueSize = 1,
                        DiscardOldest = true
                    }
                }
            }
        }).ConfigureAwait(false);

        var status = created.Results?.FirstOrDefault()?.StatusCode ?? default;
        if (StatusCode.IsBad(status))
            throw new InvalidOperationException($"O servidor recusou o monitoramento de {_address.Address}: {status}");

        _generation = generation;
        _driver.Trace($"Subscription {_subscriptionId} criada para {_address.Address} (amostragem {_intervalMs:F0} ms)");

        if (!_receivedAny)
            Emit(new TagValue(_address, null, Quality.Uncertain, DateTime.Now, TimeSpan.Zero, "Aguardando a primeira notificação"));
    }

    private async Task DeleteAsync()
    {
        _streamToken?.Dispose();
        _streamToken = null;

        var channel = _channel;
        var id = _subscriptionId;
        _channel = null;
        _subscriptionId = 0;

        if (channel is { State: CommunicationState.Opened } && id != 0)
        {
            try
            {
                await channel.DeleteSubscriptionsAsync(new DeleteSubscriptionsRequest { SubscriptionIds = new[] { id } }).ConfigureAwait(false);
            }
            catch
            {
                // ignora: a sessão pode já ter sido encerrada
            }
        }
    }

    public void OnNext(PublishResponse response)
    {
        if (response.SubscriptionId != _subscriptionId || _subscriptionId == 0) return;
        var data = response.NotificationMessage?.NotificationData;
        if (data is null) return;

        foreach (var change in data.OfType<DataChangeNotification>())
        {
            foreach (var item in change.MonitoredItems ?? Array.Empty<MonitoredItemNotification>())
            {
                var dataValue = item.Value;
                if (dataValue is null) continue;

                _receivedAny = true;
                var bad = StatusCode.IsBad(dataValue.StatusCode);
                var timestamp = dataValue.SourceTimestamp == default ? DateTime.Now : dataValue.SourceTimestamp.ToLocalTime();
                object? value = null;
                string? error = bad ? $"Status {dataValue.StatusCode}" : null;

                if (!bad)
                {
                    try
                    {
                        value = OpcUaDriver.ConvertValue(dataValue.Value, _address.DataType);
                    }
                    catch (Exception ex)
                    {
                        bad = true;
                        error = $"Conversão para {_address.DataType} falhou: {ex.Message}";
                    }
                }

                Emit(new TagValue(_address, value, bad ? Quality.Bad : Quality.Good, timestamp, TimeSpan.Zero, error, dataValue.Value?.GetType().Name));
            }
        }
    }

    public void OnError(Exception error)
    {
    }

    public void OnCompleted()
    {
    }

    private void Emit(TagValue value)
    {
        try
        {
            _onValue(value);
        }
        catch
        {
            // erro do consumidor não derruba a subscription
        }
    }

    public void Dispose() => _cts.Cancel();
}

public static class OpcUaDrivers
{
    public static DriverInfo Info { get; } = new()
    {
        Protocol = OpcUaDriver.Protocol,
        Category = "OPC",
        Description = "Cliente OPC UA (opc.tcp): navegação, leitura/escrita, subscriptions e diagnóstico de segurança.",
        Factory = () => new OpcUaDriver(),
        HostLabel = "Endpoint URL",
        DefaultHost = "opc.tcp://127.0.0.1:4840",
        UsesPort = false,
        AddressHelp = "NodeId: ns=2;s=Canal.Device.Tag, ns=3;i=1001 (ou navegue na árvore e dê duplo clique)",
        AddressExample = "ns=2;s=Temperatura",
        DefaultDataType = FpDataType.Auto,
        Options = new[]
        {
            new DriverOption("SecurityPolicy", "Política de segurança", OptionKind.Choice, "Auto",
                new[] { "Auto", "None", "Basic256Sha256", "Aes128_Sha256_RsaOaep", "Aes256_Sha256_RsaPss", "Basic256", "Basic128Rsa15" },
                "Auto escolhe o endpoint mais seguro oferecido pelo servidor."),
            new DriverOption("User", "Usuário", OptionKind.Text, string.Empty, Help: "Vazio = login anônimo."),
            new DriverOption("Password", "Senha", OptionKind.Password, string.Empty),
            new DriverOption("AutoReconnect", "Reconexão automática", OptionKind.Bool, "true")
        }
    };
}
