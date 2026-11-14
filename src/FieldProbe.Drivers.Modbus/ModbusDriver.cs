using System.IO.Ports;
using FieldProbe.Core;
using FieldProbe.Core.Codec;

namespace FieldProbe.Drivers.Modbus;

public enum ModbusMode
{
    Tcp,
    RtuSerial,
    RtuOverTcp
}

/// <summary>Driver Modbus (TCP, RTU serial e RTU sobre TCP), com identificação do device (FC 43).</summary>
public sealed class ModbusDriver : DriverBase, IDiagnosticsProvider, IRegisterInspector
{
    private readonly ModbusMode _mode;
    private IModbusTransport? _transport;
    private ModbusClient? _client;
    private byte _unitId = 1;

    public ModbusDriver(ModbusMode mode)
    {
        _mode = mode;
    }

    public override string ProtocolName => _mode switch
    {
        ModbusMode.RtuSerial => ModbusDrivers.RtuSerialName,
        ModbusMode.RtuOverTcp => ModbusDrivers.RtuTcpName,
        _ => ModbusDrivers.TcpName
    };

    public override bool IsConnected => _transport?.IsConnected == true;

    protected override string Endpoint(ConnectionSettings s) =>
        _mode == ModbusMode.RtuSerial
            ? $"{s.Host} ({s.GetOption("BaudRate", "9600")} {s.GetOption("Parity", "None")[0]}{s.GetOption("DataBits", "8")}{s.GetOption("StopBits", "1")}, Unit {s.GetIntOption("UnitId", 1)})"
            : $"{s.Host}:{s.Port} (Unit {s.GetIntOption("UnitId", 1)})";

    protected override async Task OpenAsync(ConnectionSettings settings, CancellationToken ct)
    {
        _unitId = (byte)Math.Clamp(settings.GetIntOption("UnitId", 1), 0, 255);

        IModbusTransport transport = _mode switch
        {
            ModbusMode.RtuSerial => new RtuSerialTransport(
                settings.Host,
                settings.GetIntOption("BaudRate", 9600),
                Enum.Parse<Parity>(settings.GetOption("Parity", "None"), ignoreCase: true),
                settings.GetIntOption("DataBits", 8),
                settings.GetOption("StopBits", "1") == "2" ? StopBits.Two : StopBits.One,
                settings.TimeoutMs),
            ModbusMode.RtuOverTcp => new RtuTcpTransport(settings.Host, settings.Port, settings.TimeoutMs),
            _ => new ModbusTcpTransport(settings.Host, settings.Port, settings.TimeoutMs)
        };

        transport.Frame += (direction, data, description) => OnFrame(direction, data, description);
        await transport.ConnectAsync(ct).ConfigureAwait(false);
        _transport = transport;
        _client = new ModbusClient(transport);
    }

    protected override Task CloseAsync()
    {
        _transport?.Dispose();
        _transport = null;
        _client = null;
        return Task.CompletedTask;
    }

    public override Task<IReadOnlyList<TagNode>> BrowseAsync(TagNode? parent = null, CancellationToken ct = default)
    {
        if (parent is not null)
            return Task.FromResult<IReadOnlyList<TagNode>>(parent.Children);

        // Modbus não tem browse: árvore sugerida com os primeiros endereços de cada área.
        var count = Math.Clamp(Settings?.GetIntOption("BrowseCount", 10) ?? 10, 1, 500);

        TagNode Folder(string name, ModbusArea area, DataType type)
        {
            var folder = new TagNode(name);
            for (var i = 0; i < count; i++)
            {
                var address = new ModbusAddress(area, (ushort)i).ToString();
                folder.Children.Add(new TagNode(address, new TagAddress(address, type)));
            }
            return folder;
        }

        IReadOnlyList<TagNode> roots = new[]
        {
            Folder("Coils (0x) · leitura/escrita", ModbusArea.Coil, DataType.Bool),
            Folder("Discrete Inputs (1x) · leitura", ModbusArea.DiscreteInput, DataType.Bool),
            Folder("Input Registers (3x) · leitura", ModbusArea.InputRegister, DataType.UInt16),
            Folder("Holding Registers (4x) · leitura/escrita", ModbusArea.HoldingRegister, DataType.UInt16)
        };
        return Task.FromResult(roots);
    }

    protected override async Task<object?> ReadCoreAsync(TagAddress address, CancellationToken ct)
    {
        var modbus = ModbusAddress.Parse(address.Address);
        var client = _client ?? throw new InvalidOperationException("Não conectado.");

        switch (modbus.Area)
        {
            case ModbusArea.Coil:
                return (await client.ReadBitsAsync(_unitId, 0x01, modbus.Offset, 1, ct).ConfigureAwait(false))[0];
            case ModbusArea.DiscreteInput:
                return (await client.ReadBitsAsync(_unitId, 0x02, modbus.Offset, 1, ct).ConfigureAwait(false))[0];
            default:
                var type = address.DataType == DataType.Auto ? DataType.UInt16 : address.DataType;
                var function = modbus.Area == ModbusArea.HoldingRegister ? (byte)0x03 : (byte)0x04;
                var count = (ushort)ValueCodec.RegisterCount(type, address.Length);
                var registers = await client.ReadRegistersAsync(_unitId, function, modbus.Offset, count, ct).ConfigureAwait(false);
                return ValueCodec.FromRegisters(registers, type, address.ByteOrder);
        }
    }

    protected override async Task WriteCoreAsync(TagAddress address, object value, CancellationToken ct)
    {
        var modbus = ModbusAddress.Parse(address.Address);
        if (!modbus.IsWritable)
            throw new InvalidOperationException($"{modbus} é somente leitura (Discrete Inputs e Input Registers não aceitam escrita).");

        var client = _client ?? throw new InvalidOperationException("Não conectado.");

        if (modbus.Area == ModbusArea.Coil)
        {
            await client.WriteSingleCoilAsync(_unitId, modbus.Offset, ValueCodec.ParseBool(value), ct).ConfigureAwait(false);
            return;
        }

        var type = address.DataType == DataType.Auto ? DataType.UInt16 : address.DataType;
        var registers = ValueCodec.ToRegisters(value, type, address.ByteOrder, address.Length);
        if (registers.Length == 1)
            await client.WriteSingleRegisterAsync(_unitId, modbus.Offset, registers[0], ct).ConfigureAwait(false);
        else
            await client.WriteMultipleRegistersAsync(_unitId, modbus.Offset, registers, ct).ConfigureAwait(false);
    }

    public async Task<ushort[]> ReadRawRegistersAsync(string address, int count, CancellationToken ct = default)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var modbus = ModbusAddress.Parse(address);
        if (modbus.IsBit)
            throw new InvalidOperationException("Coils e Discrete Inputs são bits: não há tipo para detectar.");

        var client = _client ?? throw new InvalidOperationException("Não conectado.");
        var function = modbus.Area == ModbusArea.HoldingRegister ? (byte)0x03 : (byte)0x04;
        return await client.ReadRegistersAsync(_unitId, function, modbus.Offset, (ushort)Math.Clamp(count, 1, 125), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DiagnosticItem>> RunDiagnosticsAsync(CancellationToken ct = default)
    {
        var items = new List<DiagnosticItem>
        {
            new("Conexão", $"{ProtocolName} · {(Settings is null ? "-" : Endpoint(Settings))}"),
            new("Estatísticas", Statistics.Snapshot().ToString())
        };

        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            var identification = await _client!.ReadDeviceIdentificationAsync(_unitId, ct).ConfigureAwait(false);
            if (identification.Count == 0)
                items.Add(new("Identificação", "O device respondeu ao FC 43 sem objetos.", DiagnosticSeverity.Warning));
            foreach (var (name, value) in identification)
                items.Add(new("Identificação", $"{name}: {value}", DiagnosticSeverity.Ok));
        }
        catch (ModbusException ex) when (ex.ExceptionCode == 0x01)
        {
            items.Add(new("Identificação", "O device não suporta Read Device Identification (FC 43). É comum em CLPs e inversores mais simples."));
        }
        catch (Exception ex)
        {
            items.Add(new("Identificação", $"Falhou: {ex.Message}", DiagnosticSeverity.Warning));
        }

        if (_mode == ModbusMode.Tcp)
            items.Add(new("Segurança", "Modbus TCP não tem autenticação nem criptografia. Restrinja o acesso por firewall/VLAN.", DiagnosticSeverity.Warning));

        return items;
    }
}

/// <summary>Registro dos três modos Modbus.</summary>
public static class ModbusDrivers
{
    public const string TcpName = "Modbus TCP";
    public const string RtuSerialName = "Modbus RTU (serial)";
    public const string RtuTcpName = "Modbus RTU over TCP";

    private const string AddressHelp = "HR:0, IR:10, CO:3, DI:7 (offset 0-based) ou 40001, 30011, 00004, 10008 (clássico 1-based)";

    private static readonly DriverOption UnitId = new("UnitId", "Unit ID (escravo)", OptionKind.Number, "1", Help: "Endereço do escravo. Em gateways indica o device na rede serial.");
    private static readonly DriverOption BrowseCount = new("BrowseCount", "Endereços na árvore", OptionKind.Number, "10", Help: "Quantos endereços de cada área aparecem na árvore.");
    private static readonly DriverOption AutoReconnect = new("AutoReconnect", "Reconexão automática", OptionKind.Bool, "true");

    public static IEnumerable<DriverInfo> All()
    {
        yield return new DriverInfo
        {
            Protocol = TcpName,
            Category = "Modbus",
            Description = "Modbus TCP/IP (MBAP). CLPs, inversores, medidores, gateways.",
            Factory = () => new ModbusDriver(ModbusMode.Tcp),
            DefaultPort = 502,
            AddressHelp = AddressHelp,
            AddressExample = "HR:0",
            DefaultDataType = DataType.UInt16,
            SupportsByteOrder = true,
            Options = new[] { UnitId, BrowseCount, AutoReconnect }
        };

        yield return new DriverInfo
        {
            Protocol = RtuSerialName,
            Category = "Modbus",
            Description = "Modbus RTU via RS-485/RS-232 (adaptador USB-serial).",
            Factory = () => new ModbusDriver(ModbusMode.RtuSerial),
            HostLabel = "Porta COM",
            DefaultHost = "COM3",
            UsesPort = false,
            AddressHelp = AddressHelp,
            AddressExample = "HR:0",
            DefaultDataType = DataType.UInt16,
            SupportsByteOrder = true,
            Options = new[]
            {
                UnitId,
                new DriverOption("BaudRate", "Baud rate", OptionKind.Choice, "9600",
                    new[] { "1200", "2400", "4800", "9600", "19200", "38400", "57600", "115200" }),
                new DriverOption("Parity", "Paridade", OptionKind.Choice, "None", new[] { "None", "Even", "Odd" },
                    "O padrão Modbus é Even, mas muitos devices vêm de fábrica com None."),
                new DriverOption("DataBits", "Data bits", OptionKind.Choice, "8", new[] { "8", "7" }),
                new DriverOption("StopBits", "Stop bits", OptionKind.Choice, "1", new[] { "1", "2" }),
                BrowseCount,
                AutoReconnect
            }
        };

        yield return new DriverInfo
        {
            Protocol = RtuTcpName,
            Category = "Modbus",
            Description = "Frames RTU (com CRC) encapsulados em TCP, para conversores serial/Ethernet em modo transparente.",
            Factory = () => new ModbusDriver(ModbusMode.RtuOverTcp),
            DefaultPort = 4001,
            AddressHelp = AddressHelp,
            AddressExample = "HR:0",
            DefaultDataType = DataType.UInt16,
            SupportsByteOrder = true,
            Options = new[] { UnitId, BrowseCount, AutoReconnect }
        };
    }

    /// <summary>Portas COM disponíveis no PC (para sugestões na interface).</summary>
    public static string[] SerialPorts()
    {
        try
        {
            return SerialPort.GetPortNames().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
