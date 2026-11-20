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
public sealed class ModbusDriver : DriverBase, IDiagnosticsProvider, IRegisterInspector, IAddressScanner
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

        // Modbus não tem browse: cada área mostra as variáveis criadas pelo usuário
        // e, depois de "Procurar registradores", os endereços que o equipamento aceitou.
        var found = ParseScanRanges(Settings?.GetOption("ScanRanges", string.Empty) ?? string.Empty);
        var custom = CustomTags.Load(Settings);

        TagNode Folder(string name, ModbusArea area, DataType type)
        {
            var isRegister = area is ModbusArea.InputRegister or ModbusArea.HoldingRegister;
            var access = area is ModbusArea.Coil or ModbusArea.HoldingRegister ? "Leitura e escrita. " : "Somente leitura. ";
            var typeNote = isRegister
                ? "\nCada registrador tem 16 bits: Float, Int32 e textos ocupam 2 ou mais. Confira o tipo no manual do equipamento ou use o 'Detectar tipo'."
                : string.Empty;

            // Depois de "Procurar registradores": a pasta mostra quantos endereços existem (faixas no tooltip),
            // mas só as variáveis criadas pelo usuário aparecem dentro dela.
            var ranges = found?.Where(r => r.Area == area).ToList();
            if (ranges is { Count: > 0 })
            {
                var total = ranges.Sum(r => r.Count);
                var prefix = ModbusAddress.Prefix(area);
                var list = string.Join(", ", ranges.Take(12).Select(r => r.Count == 1 ? $"{prefix}:{r.Start}" : $"{prefix}:{r.Start}–{r.Start + r.Count - 1}"));
                if (ranges.Count > 12) list += $" e mais {ranges.Count - 12} faixa(s)";
                var foundFolder = new TagNode(name)
                {
                    Subtitle = $"{total} encontrados",
                    CustomFolder = prefix,
                    AddressHint = $"{prefix}:{ranges[0].Start}",
                    Description = access + $"Endereços que o equipamento aceitou na busca: {list}." + AddNote + typeNote
                };
                CustomTags.AddTo(foundFolder, custom);
                return foundFolder;
            }

            // Sem busca: a pasta começa vazia, só com o "+" (o usuário cria as variáveis com nome e tipo).
            var folder = new TagNode(name)
            {
                CustomFolder = ModbusAddress.Prefix(area),
                AddressHint = $"{ModbusAddress.Prefix(area)}:0",
                Description = access + "Clique em + para criar uma variável com o endereço e o tipo do manual do equipamento (ex.: Temperatura = HR:100, Float32). " +
                              "Use 'Procurar endereços...' para descobrir quais endereços existem." + typeNote
            };
            CustomTags.AddTo(folder, custom);
            return folder;
        }

        IReadOnlyList<TagNode> roots = new[]
        {
            Folder("Coils (0x)", ModbusArea.Coil, DataType.Bool),
            Folder("Discrete Inputs (1x)", ModbusArea.DiscreteInput, DataType.Bool),
            Folder("Input Registers (3x)", ModbusArea.InputRegister, DataType.UInt16),
            Folder("Holding Registers (4x)", ModbusArea.HoldingRegister, DataType.UInt16)
        };
        return Task.FromResult(roots);
    }

    private const string AddNote = "\nClique em + para criar uma variável com nome, tipo e ordem dos bytes (ex.: Temperatura = HR:100, Float32).";

    // ===================== Procurar registradores =====================

    private sealed record FoundRange(ModbusArea Area, int Start, int Count);

    private static List<FoundRange>? ParseScanRanges(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var list = new List<FoundRange>();
        foreach (var range in AddressRangeParser.Parse(text, 0, 65535, int.MaxValue))
        {
            var area = range.Prefix switch
            {
                "CO" => ModbusArea.Coil,
                "DI" => ModbusArea.DiscreteInput,
                "IR" => ModbusArea.InputRegister,
                _ => ModbusArea.HoldingRegister
            };
            list.Add(new FoundRange(area, range.Start, range.Count));
        }
        return list;
    }

    public string AddressScanTitle => "Procurar registradores";

    public string AddressScanHelp =>
        "Procura quais endereços o equipamento aceita, por área. Faixa em offset (começando em 0), ex.: 0-999. " +
        "Para uma área só, use o prefixo: HR:0-499, IR:0-99, CO:0-63, DI:0-63. " +
        "Endereço que não existe custa 1 leitura (o equipamento responde 'Illegal Data Address'); faixas que existem são lidas em bloco. " +
        "Em rede lenta, faixas grandes levam alguns minutos: dá para parar a qualquer momento. " +
        "Só lê, não escreve nada. O tipo de cada registrador continua vindo do manual (ou do Detectar tipo).";

    public string DefaultScanRange => "0-999";

    public async Task ScanAddressesAsync(string range, IProgress<double>? progress, Action<AddressBlock> onFound, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var client = _client ?? throw new InvalidOperationException("Não conectado.");
        var ranges = AddressRangeParser.Parse(range, 0, 65535, 4 * 65536);

        // Sem prefixo: procura nas 4 áreas
        var jobs = new List<(ModbusArea Area, int Start, int End)>();
        foreach (var r in ranges)
        {
            var areas = r.Prefix switch
            {
                "HR" or "4X" => new[] { ModbusArea.HoldingRegister },
                "IR" or "3X" => new[] { ModbusArea.InputRegister },
                "CO" or "0X" => new[] { ModbusArea.Coil },
                "DI" or "1X" => new[] { ModbusArea.DiscreteInput },
                null => new[] { ModbusArea.HoldingRegister, ModbusArea.InputRegister, ModbusArea.Coil, ModbusArea.DiscreteInput },
                _ => throw new FormatException($"Prefixo '{r.Prefix}' inválido. Use HR, IR, CO ou DI.")
            };
            jobs.AddRange(areas.Select(a => (a, r.Start, r.End)));
        }

        var totalAddresses = jobs.Sum(j => (double)(j.End - j.Start + 1));
        var covered = 0.0;

        foreach (var (area, start, end) in jobs)
        {
            var isBit = area is ModbusArea.Coil or ModbusArea.DiscreteInput;
            var function = area switch
            {
                ModbusArea.Coil => (byte)0x01,
                ModbusArea.DiscreteInput => (byte)0x02,
                ModbusArea.HoldingRegister => (byte)0x03,
                _ => (byte)0x04
            };
            var maxCount = isBit ? 2000 : 125;
            var valid = new List<(int Start, int Count, int NonZero)>();
            var unsupported = false;

            // Lê "count" endereços a partir de "first"; devolve nulo se o equipamento disser que o endereço não existe.
            async Task<int?> TryReadAsync(int first, int count)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    return isBit
                        ? (await client.ReadBitsAsync(_unitId, function, (ushort)first, (ushort)count, ct).ConfigureAwait(false)).Count(b => b)
                        : (await client.ReadRegistersAsync(_unitId, function, (ushort)first, (ushort)count, ct).ConfigureAwait(false)).Count(v => v != 0);
                }
                catch (ModbusException ex) when (ex.ExceptionCode is 0x02 or 0x03)
                {
                    return null;
                }
            }

            // Anda endereço por endereço; ao achar um que existe, dobra o tamanho da leitura até falhar
            // e faz busca binária do fim da faixa. Custo: ~1 pedido por endereço inexistente + poucos por faixa.
            try
            {
                var a = start;
                while (a <= end)
                {
                    var single = await TryReadAsync(a, 1).ConfigureAwait(false);
                    if (single is null)
                    {
                        a++;
                        if ((a - start) % 20 == 0) progress?.Report((covered + a - start) / totalAddresses);
                        continue;
                    }

                    int ok = 1, okNonZero = single.Value;
                    int? fail = null;
                    var limit = Math.Min(maxCount, end - a + 1);
                    while (fail is null && ok < limit)
                    {
                        var n = Math.Min(ok * 2, limit);
                        var r = await TryReadAsync(a, n).ConfigureAwait(false);
                        if (r is null) fail = n; else { ok = n; okNonZero = r.Value; }
                    }
                    while (fail is { } f && f - ok > 1)
                    {
                        var m = (ok + f) / 2;
                        var r = await TryReadAsync(a, m).ConfigureAwait(false);
                        if (r is null) fail = m; else { ok = m; okNonZero = r.Value; }
                    }

                    valid.Add((a, ok, okNonZero));
                    a += ok;
                    progress?.Report((covered + a - start) / totalAddresses);
                }
            }
            catch (ModbusException ex) when (ex.ExceptionCode == 0x01)
            {
                unsupported = true; // área (função) não suportada pelo equipamento
            }

            covered += end - start + 1;
            progress?.Report(covered / totalAddresses);

            var prefix = ModbusAddress.Prefix(area);
            var unit = isBit ? "bits" : "registradores";
            if (unsupported)
            {
                onFound(new AddressBlock(prefix, start, 0, $"{prefix} (área inteira)", "o equipamento não suporta esta área (Illegal Function)", false));
                continue;
            }

            // Junta blocos vizinhos em faixas contínuas
            foreach (var group in MergeRanges(valid))
            {
                var last = group.Start + group.Count - 1;
                var detail = isBit ? $"{group.Count} {unit} · {group.NonZero} ligados" : $"{group.Count} {unit} · {group.NonZero} com valor ≠ 0";
                onFound(new AddressBlock(prefix, group.Start, group.Count, group.Count == 1 ? $"{prefix}:{group.Start}" : $"{prefix}:{group.Start}–{last}", detail));
            }
        }
        progress?.Report(1.0);
    }

    private static IEnumerable<(int Start, int Count, int NonZero)> MergeRanges(IEnumerable<(int Start, int Count, int NonZero)> blocks)
    {
        (int Start, int Count, int NonZero)? current = null;
        foreach (var b in blocks.OrderBy(b => b.Start))
        {
            if (current is { } c && c.Start + c.Count == b.Start)
            {
                current = (c.Start, c.Count + b.Count, c.NonZero + b.NonZero);
                continue;
            }
            if (current is { } done) yield return done;
            current = b;
        }
        if (current is { } final) yield return final;
    }

    public void ApplyScanResults(ConnectionSettings settings, IReadOnlyList<AddressBlock> selected)
    {
        settings.Options["ScanRanges"] = string.Join("; ", selected
            .Where(b => b.Accessible && b.Count > 0)
            .OrderBy(b => b.Area).ThenBy(b => b.Start)
            .Select(b => $"{b.Area}:{b.Start}-{b.Start + b.Count - 1}"));
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
            Options = new[] { UnitId, AutoReconnect }
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
            Options = new[] { UnitId, AutoReconnect }
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
