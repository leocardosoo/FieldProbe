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

/// <summary>Bloco de endereços encontrado pela busca (ex.: DB10 com 6 bytes, ou HR:100–149).</summary>
/// <param name="Area">Área ("DB", "HR", "IR", "CO", "DI").</param>
/// <param name="Start">S7: número do DB · Modbus: primeiro endereço (offset 0-based).</param>
/// <param name="Count">S7: tamanho em bytes · Modbus: quantidade de endereços.</param>
/// <param name="Accessible">Falso quando o bloco existe mas recusa leitura (ex.: DB otimizado).</param>
public sealed record AddressBlock(string Area, int Start, int Count, string Title, string Detail, bool Accessible = true);

/// <summary>
/// Drivers sem navegação nativa (S7 PUT/GET, Modbus) que conseguem procurar quais endereços existem no equipamento.
/// A busca é sempre iniciada pelo usuário e feita em lote, para não sobrecarregar o equipamento.
/// </summary>
public interface IAddressScanner
{
    /// <summary>Título da janela (ex.: "Procurar DBs").</summary>
    string AddressScanTitle { get; }

    /// <summary>Explicação curta do que será procurado e do formato da faixa.</summary>
    string AddressScanHelp { get; }

    /// <summary>Faixa sugerida (ex.: "1-1000" ou "0-999").</summary>
    string DefaultScanRange { get; }

    Task ScanAddressesAsync(string range, IProgress<double>? progress, Action<AddressBlock> onFound, CancellationToken ct);

    /// <summary>Grava os blocos escolhidos nas opções da conexão, para a árvore mostrar só o que existe.</summary>
    void ApplyScanResults(ConnectionSettings settings, IReadOnlyList<AddressBlock> selected);
}

/// <summary>Drivers em que o usuário pode tirar pastas da árvore (ex.: um DB do S7 que não interessa).</summary>
public interface IRemovableFolders
{
    bool CanRemoveFolder(string key);

    /// <summary>Tira a pasta das opções da conexão (a árvore é recriada em seguida).</summary>
    void RemoveFolder(ConnectionSettings settings, string key);
}
