namespace FieldProbe.Core;

/// <summary>Catálogo de protocolos disponíveis (registrados na inicialização do app).</summary>
public static class DriverRegistry
{
    private static readonly List<DriverInfo> Items = new();

    public static void Register(DriverInfo info)
    {
        Items.RemoveAll(i => i.Protocol.Equals(info.Protocol, StringComparison.OrdinalIgnoreCase));
        Items.Add(info);
    }

    public static IReadOnlyList<DriverInfo> All => Items;

    public static IReadOnlyList<string> Protocols => Items.Select(i => i.Protocol).ToList();

    public static DriverInfo? Find(string protocol) =>
        Items.FirstOrDefault(i => i.Protocol.Equals(protocol, StringComparison.OrdinalIgnoreCase));

    public static DriverInfo Get(string protocol) =>
        Find(protocol) ?? throw new NotSupportedException($"Protocolo '{protocol}' não está disponível nesta versão.");

    public static IProtocolDriver Create(string protocol) => Get(protocol).Factory();
}
