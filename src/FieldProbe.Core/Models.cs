namespace FieldProbe.Core;

/// <summary>Tipos de dado. <see cref="Auto"/> deixa o driver decidir (tipo nativo do OPC UA, do tag, do payload...).</summary>
public enum DataType
{
    Auto,
    Bool,
    Byte,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    Float32,
    Float64,
    String
}

/// <summary>
/// Ordem dos bytes para valores de 32/64 bits em registradores de 16 bits (Modbus).
/// ABCD = big-endian, CDAB = word swap, BADC = byte swap, DCBA = little-endian.
/// </summary>
public enum ByteOrder
{
    ABCD,
    CDAB,
    BADC,
    DCBA
}

public enum Quality
{
    Good,
    Bad,
    Uncertain
}

public enum FrameDirection
{
    Tx,
    Rx,
    Info
}

/// <summary>Configuração de uma conexão. Opções específicas do protocolo ficam em <see cref="Options"/>.</summary>
public sealed class ConnectionSettings
{
    public string Name { get; set; } = "Device";
    public string Protocol { get; set; } = string.Empty;

    /// <summary>IP, hostname, URL de endpoint (OPC UA) ou porta COM (serial), conforme o protocolo.</summary>
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; }
    public int TimeoutMs { get; set; } = 3000;
    public Dictionary<string, string> Options { get; set; } = new();

    public string GetOption(string key, string defaultValue) =>
        Options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : defaultValue;

    public int GetIntOption(string key, int defaultValue) =>
        int.TryParse(GetOption(key, string.Empty), out var value) ? value : defaultValue;

    public bool GetBoolOption(string key, bool defaultValue)
    {
        var text = GetOption(key, string.Empty);
        return text.Length == 0 ? defaultValue : text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1";
    }

    public ConnectionSettings Clone() => new()
    {
        Name = Name,
        Protocol = Protocol,
        Host = Host,
        Port = Port,
        TimeoutMs = TimeoutMs,
        Options = new Dictionary<string, string>(Options)
    };
}

/// <summary>Endereço de uma variável. O formato de <see cref="Address"/> depende do protocolo.</summary>
/// <param name="Length">Strings: nº de caracteres/registradores.</param>
public sealed record TagAddress(
    string Address,
    DataType DataType = DataType.Auto,
    ByteOrder ByteOrder = ByteOrder.ABCD,
    int Length = 1);

public sealed record TagValue(
    TagAddress Address,
    object? Value,
    Quality Quality,
    DateTime Timestamp,
    TimeSpan Latency,
    string? Error = null,
    string? TypeName = null,
    bool DeviceFault = false);

/// <summary>Nó da árvore de navegação.</summary>
public sealed class TagNode
{
    public TagNode(string name, TagAddress? address = null)
    {
        Name = name;
        Address = address;
    }

    public string Name { get; }

    /// <summary>Endereço quando o nó é uma variável legível. Nulo em pastas.</summary>
    public TagAddress? Address { get; }

    /// <summary>Identificador interno do driver (NodeId no OPC UA, caminho no MQTT...).</summary>
    public string? Key { get; init; }

    public string? Description { get; init; }

    /// <summary>Verdadeiro quando os filhos ainda não foram carregados e devem ser buscados com BrowseAsync(node).</summary>
    public bool HasLazyChildren { get; init; }

    public bool IsFolder => Address is null;

    public List<TagNode> Children { get; } = new();
}

/// <summary>Frame/trace de comunicação para o log. Data vazio quando a biblioteca não expõe bytes brutos.</summary>
public sealed record RawFrame(DateTime Timestamp, FrameDirection Direction, byte[] Data, string? Description = null)
{
    private const int MaxHexBytes = 512;

    public string Hex => Data.Length == 0
        ? string.Empty
        : string.Join(" ", Data.Take(MaxHexBytes).Select(b => b.ToString("X2"))) + (Data.Length > MaxHexBytes ? $" ... (+{Data.Length - MaxHexBytes} bytes)" : string.Empty);
}

/// <summary>Resultado de diagnóstico (identificação do device, segurança, etc.).</summary>
public sealed record DiagnosticItem(string Category, string Message, DiagnosticSeverity Severity = DiagnosticSeverity.Info);

public enum DiagnosticSeverity
{
    Info,
    Ok,
    Warning,
    Critical
}
