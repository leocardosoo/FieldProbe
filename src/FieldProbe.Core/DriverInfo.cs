namespace FieldProbe.Core;

public enum OptionKind
{
    Text,
    Number,
    Password,
    Choice,
    Bool
}

/// <summary>Descreve um campo de configuração específico do protocolo (gera o formulário de conexão).</summary>
public sealed record DriverOption(
    string Key,
    string Label,
    OptionKind Kind,
    string DefaultValue,
    IReadOnlyList<string>? Choices = null,
    string? Help = null);

/// <summary>Metadados de um protocolo: como criar o driver e como configurar a conexão.</summary>
public sealed class DriverInfo
{
    public required string Protocol { get; init; }
    public required Func<IProtocolDriver> Factory { get; init; }
    public string Category { get; init; } = "Outros";
    public string Description { get; init; } = string.Empty;
    public string HostLabel { get; init; } = "Endereço IP";
    public string DefaultHost { get; init; } = "127.0.0.1";
    public bool UsesPort { get; init; } = true;
    public int DefaultPort { get; init; }
    public string AddressHelp { get; init; } = string.Empty;
    public string AddressExample { get; init; } = string.Empty;
    public DataType DefaultDataType { get; init; } = DataType.Auto;

    /// <summary>Se a ordem de bytes (ABCD/CDAB...) faz sentido neste protocolo.</summary>
    public bool SupportsByteOrder { get; init; }

    public IReadOnlyList<DriverOption> Options { get; init; } = Array.Empty<DriverOption>();
}
