namespace FieldProbe.Core;

/// <summary>
/// Contrato de todo driver de protocolo. A interface gráfica conhece apenas este contrato.
/// </summary>
public interface IProtocolDriver : IAsyncDisposable
{
    string ProtocolName { get; }

    bool IsConnected { get; }

    DriverStatistics Statistics { get; }

    Task ConnectAsync(ConnectionSettings settings, CancellationToken ct = default);

    Task DisconnectAsync();

    /// <summary>Com <paramref name="parent"/> nulo retorna as raízes; senão, os filhos do nó (carregamento sob demanda).</summary>
    Task<IReadOnlyList<TagNode>> BrowseAsync(TagNode? parent = null, CancellationToken ct = default);

    /// <summary>Nunca lança por falha de comunicação: retorna Quality.Bad com a mensagem.</summary>
    Task<TagValue> ReadAsync(TagAddress address, CancellationToken ct = default);

    /// <summary>Escreve um valor (texto digitado pelo usuário ou valor tipado). Lança em caso de falha.</summary>
    Task WriteAsync(TagAddress address, object value, CancellationToken ct = default);

    /// <summary>Monitora um endereço (polling ou notificação nativa). Descartar o retorno encerra o monitoramento.</summary>
    IDisposable Subscribe(TagAddress address, int intervalMs, Action<TagValue> onValue);

    event EventHandler<RawFrame>? FrameLogged;

    event EventHandler<string>? StatusChanged;
}

/// <summary>Drivers que oferecem diagnóstico do device (identificação, segurança, etc.).</summary>
public interface IDiagnosticsProvider
{
    Task<IReadOnlyList<DiagnosticItem>> RunDiagnosticsAsync(CancellationToken ct = default);
}

/// <summary>Drivers baseados em registradores (Modbus) que permitem ler valores brutos para o detector de tipo.</summary>
public interface IRegisterInspector
{
    Task<ushort[]> ReadRawRegistersAsync(string address, int count, CancellationToken ct = default);
}
