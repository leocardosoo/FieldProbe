using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using FieldProbe.Core;
using FieldProbe.Core.Codec;
using libplctag;
using FpDataType = FieldProbe.Core.DataType;

namespace FieldProbe.Drivers.EtherNetIP;

/// <summary>
/// Driver EtherNet/IP (CIP) via libplctag: ControlLogix/CompactLogix, Micro800, MicroLogix, SLC 500, PLC-5 e Omron NJ/NX.
/// Navega os tags do controlador (ControlLogix/CompactLogix) e identifica o device com ListIdentity.
/// </summary>
public sealed class EtherNetIpDriver : DriverBase, IDiagnosticsProvider
{
    public const string Protocol = "EtherNet/IP (Allen-Bradley)";

    private readonly Dictionary<string, Tag> _tags = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _tagLock = new(1, 1);
    private bool _connected;
    private EipIdentity? _identity;

    /// <summary>Tags com erro permanente (não existe, índice, Path): próxima tentativa só depois do prazo.</summary>
    private readonly Dictionary<string, (DateTime Until, string Message)> _failedTags = new(StringComparer.OrdinalIgnoreCase);

    public override string ProtocolName => Protocol;

    public override bool IsConnected => _connected;

    private PlcType CurrentPlcType => (Settings?.GetOption("PlcType", "ControlLogix") ?? "ControlLogix") switch
    {
        "Micro800" => PlcType.Micro800,
        "MicroLogix" => PlcType.MicroLogix,
        "SLC 500" => PlcType.Slc500,
        "PLC-5" => PlcType.Plc5,
        "Omron NJ/NX" => PlcType.Omron,
        _ => PlcType.ControlLogix
    };

    private string Gateway => Settings is null ? string.Empty
        : Settings.Port is > 0 and not 44818 ? $"{Settings.Host}:{Settings.Port}" : Settings.Host;

    protected override async Task OpenAsync(ConnectionSettings settings, CancellationToken ct)
    {
        // libplctag conecta sob demanda; validamos o device com ListIdentity (também testa a porta 44818).
        try
        {
            _identity = await ListIdentity.QueryAsync(settings.Host, settings.Port > 0 ? settings.Port : 44818, settings.TimeoutMs,
                (direction, data, description) => OnFrame(direction, data, description), ct).ConfigureAwait(false);
            OnTrace($"Device: {_identity.ProductName} · {_identity.VendorName} · rev {_identity.RevisionMajor}.{_identity.RevisionMinor}", FrameDirection.Rx);
        }
        catch (Exception ex) when (ex is TimeoutException or System.Net.Sockets.SocketException or IOException)
        {
            throw new IOException($"O device não respondeu ao EtherNet/IP em {settings.Host}: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            // Alguns devices não respondem ListIdentity por TCP: seguimos e deixamos a leitura de tags validar.
            OnTrace($"ListIdentity indisponível ({ex.Message}); seguindo sem identificação.");
        }
        _connected = true;
    }

    protected override async Task CloseAsync()
    {
        _connected = false;
        lock (_failedTags) _failedTags.Clear();
        lock (_lastProbe) _lastProbe.Clear();
        await _tagLock.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var tag in _tags.Values) tag.Dispose();
            _tags.Clear();
        }
        finally
        {
            _tagLock.Release();
        }
    }

    private async Task<Tag> GetTagAsync(string name, FpDataType type, CancellationToken ct)
    {
        var key = $"{name}|{type}";
        lock (_failedTags)
        {
            if (_failedTags.TryGetValue(name, out var failed))
            {
                if (DateTime.UtcNow < failed.Until) throw new TagFailedException(failed.Message);
                _failedTags.Remove(name);
            }
        }

        await _tagLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_tags.TryGetValue(key, out var existing)) return existing;

            var tag = new Tag
            {
                Name = name,
                Gateway = Gateway,
                PlcType = CurrentPlcType,
                Protocol = libplctag.Protocol.ab_eip,
                Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, Settings?.TimeoutMs ?? 3000))
            };

            var path = Settings?.GetOption("Path", "1,0") ?? "1,0";
            if (CurrentPlcType is PlcType.ControlLogix or PlcType.Omron || (path.Length > 0 && CurrentPlcType is not PlcType.Micro800))
                tag.Path = path;

            // PLCs PCCC (SLC/MicroLogix/PLC-5) exigem o tamanho do elemento.
            if (CurrentPlcType is PlcType.Slc500 or PlcType.MicroLogix or PlcType.Plc5)
                tag.ElementSize = type switch
                {
                    FpDataType.Float32 or FpDataType.Int32 or FpDataType.UInt32 => 4,
                    FpDataType.String => 84,
                    _ => 2
                };

            OnTrace($"Criando tag '{name}' (gateway {Gateway}, path {tag.Path ?? "-"}, {CurrentPlcType})", FrameDirection.Tx);
            try
            {
                await tag.InitializeAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                tag.Dispose();
                throw;
            }

            _tags[key] = tag;
            return tag;
        }
        finally
        {
            _tagLock.Release();
        }
    }

    // ----- Leitura / escrita -----

    protected override async Task<object?> ReadCoreAsync(TagAddress address, CancellationToken ct)
    {
        Tag tag;
        try
        {
            tag = await GetTagAsync(address.Address, address.DataType, ct).ConfigureAwait(false);
            await tag.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw await FailAsync(ex, address.Address, ct).ConfigureAwait(false);
        }
        return Decode(tag, address.DataType);
    }

    protected override async Task WriteCoreAsync(TagAddress address, object value, CancellationToken ct)
    {
        Tag tag;
        try
        {
            tag = await GetTagAsync(address.Address, address.DataType, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw await FailAsync(ex, address.Address, ct).ConfigureAwait(false);
        }

        var size = tag.GetSize();
        var type = address.DataType == FpDataType.Auto ? InferType(size, value) : address.DataType;
        var inv = CultureInfo.InvariantCulture;
        var normalized = ValueCodec.Normalize(value);

        switch (type)
        {
            case FpDataType.Bool:
                if (size == 1) tag.SetUInt8(0, ValueCodec.ParseBool(value) ? (byte)1 : (byte)0);
                else tag.SetBit(0, ValueCodec.ParseBool(value));
                break;
            case FpDataType.Byte: tag.SetUInt8(0, Convert.ToByte(normalized, inv)); break;
            case FpDataType.Int16: tag.SetInt16(0, Convert.ToInt16(normalized, inv)); break;
            case FpDataType.UInt16: tag.SetUInt16(0, Convert.ToUInt16(normalized, inv)); break;
            case FpDataType.Int32: tag.SetInt32(0, Convert.ToInt32(normalized, inv)); break;
            case FpDataType.UInt32: tag.SetUInt32(0, Convert.ToUInt32(normalized, inv)); break;
            case FpDataType.Int64: tag.SetInt64(0, Convert.ToInt64(normalized, inv)); break;
            case FpDataType.Float32: tag.SetFloat32(0, Convert.ToSingle(normalized, inv)); break;
            case FpDataType.Float64: tag.SetFloat64(0, Convert.ToDouble(normalized, inv)); break;
            case FpDataType.String: tag.SetString(0, Convert.ToString(value, inv) ?? string.Empty); break;
            default: throw new NotSupportedException($"Escrita de {type} não suportada.");
        }

        try
        {
            await tag.WriteAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw await FailAsync(ex, address.Address, ct).ConfigureAwait(false);
        }
    }

    private static object? Decode(Tag tag, FpDataType type)
    {
        var size = tag.GetSize();
        return type switch
        {
            FpDataType.Bool => size == 1 ? tag.GetUInt8(0) != 0 : tag.GetBit(0),
            FpDataType.Byte => tag.GetUInt8(0),
            FpDataType.Int16 => tag.GetInt16(0),
            FpDataType.UInt16 => tag.GetUInt16(0),
            FpDataType.Int32 => tag.GetInt32(0),
            FpDataType.UInt32 => tag.GetUInt32(0),
            FpDataType.Int64 => tag.GetInt64(0),
            FpDataType.Float32 => tag.GetFloat32(0),
            FpDataType.Float64 => tag.GetFloat64(0),
            FpDataType.String => tag.GetString(0),
            _ => size switch
            {
                1 => tag.GetInt8(0),            // BOOL / SINT
                2 => tag.GetInt16(0),           // INT
                4 => tag.GetInt32(0),           // DINT (para REAL escolha Float32)
                8 => tag.GetInt64(0),           // LINT
                88 => tag.GetString(0),         // STRING Logix
                _ => "0x" + Convert.ToHexString(ReadRaw(tag, Math.Min(size, 64))) + (size > 64 ? "…" : string.Empty)
            }
        };
    }

    private static FpDataType InferType(int size, object value) => size switch
    {
        1 => FpDataType.Byte,
        2 => FpDataType.Int16,
        4 => Convert.ToString(value, CultureInfo.InvariantCulture)?.IndexOfAny(new[] { '.', ',' }) >= 0 ? FpDataType.Float32 : FpDataType.Int32,
        8 => FpDataType.Int64,
        88 => FpDataType.String,
        _ => throw new NotSupportedException($"Não foi possível deduzir o tipo de um tag de {size} bytes. Escolha o tipo manualmente.")
    };

    private static byte[] ReadRaw(Tag tag, int count)
    {
        var bytes = new byte[count];
        for (var i = 0; i < count; i++) bytes[i] = tag.GetUInt8(i);
        return bytes;
    }

    // ----- Navegação (lista de tags do controlador) -----

    public override async Task<IReadOnlyList<TagNode>> BrowseAsync(TagNode? parent = null, CancellationToken ct = default)
    {
        if (CurrentPlcType != PlcType.ControlLogix)
        {
            return new[]
            {
                new TagNode($"Navegação disponível apenas para ControlLogix/CompactLogix. Digite o nome do tag (ex.: {(CurrentPlcType is PlcType.Slc500 or PlcType.MicroLogix or PlcType.Plc5 ? "N7:0, F8:1, B3:0/2" : "MeuTag")}).")
            };
        }

        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var program = parent?.Key; // "Program:Nome" para tags de programa
        var listing = program is null ? "@tags" : $"{program}.@tags";

        Tag tag;
        try
        {
            tag = await GetTagAsync(listing, FpDataType.Auto, ct).ConfigureAwait(false);
            await tag.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var reason = ex.GetBaseException().Message;
            return new[]
            {
                new TagNode(reason.Contains("Unsupported", StringComparison.OrdinalIgnoreCase)
                    ? "Este controlador não permite listar os tags (serviço CIP não suportado). Digite o nome do tag no campo Endereço."
                    : $"Não foi possível listar os tags: {reason}")
            };
        }

        var buffer = ReadRaw(tag, tag.GetSize());
        var entries = ParseTagList(buffer);
        OnTrace($"{listing}: {entries.Count} símbolos", FrameDirection.Rx);

        var nodes = new List<TagNode>();
        foreach (var entry in entries)
        {
            if (entry.Name.StartsWith("__", StringComparison.Ordinal)) continue;

            if (program is null && entry.Name.StartsWith("Program:", StringComparison.OrdinalIgnoreCase))
            {
                nodes.Add(new TagNode(entry.Name) { Key = entry.Name, HasLazyChildren = true, Description = "Tags do programa" });
                continue;
            }

            var fullName = program is null ? entry.Name : $"{program}.{entry.Name}";
            var label = $"{entry.Name}  ({entry.TypeName}{(entry.Dimensions > 0 ? $"[{entry.ArraySize}]" : string.Empty)})";

            if (entry.IsStruct)
            {
                nodes.Add(new TagNode(label) { Key = fullName, Description = "UDT/estrutura: monitore os membros pelo nome (ex.: Tag.Membro)" });
                continue;
            }

            var address = entry.Dimensions > 0 ? $"{fullName}[0]" : fullName;
            nodes.Add(new TagNode(label, new TagAddress(address, entry.DataType)) { Key = address, Description = address });
        }

        return nodes
            .OrderBy(n => n.HasLazyChildren ? 0 : 1)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed record TagEntry(string Name, ushort SymbolType, int Dimensions, uint ArraySize)
    {
        public bool IsStruct => (SymbolType & 0x8000) != 0;

        public FpDataType DataType => (SymbolType & 0x00FF) switch
        {
            0xC1 => FpDataType.Bool,
            0xC2 => FpDataType.Auto,    // SINT
            0xC3 => FpDataType.Int16,   // INT
            0xC4 => FpDataType.Int32,   // DINT
            0xC5 => FpDataType.Int64,   // LINT
            0xC6 => FpDataType.Byte,    // USINT
            0xC7 => FpDataType.UInt16,  // UINT
            0xC8 => FpDataType.UInt32,  // UDINT
            0xCA => FpDataType.Float32, // REAL
            0xCB => FpDataType.Float64, // LREAL
            0xD3 => FpDataType.UInt32,  // DWORD / BOOL[32]
            _ => FpDataType.Auto
        };

        public string TypeName => IsStruct ? "UDT" : (SymbolType & 0x00FF) switch
        {
            0xC1 => "BOOL",
            0xC2 => "SINT",
            0xC3 => "INT",
            0xC4 => "DINT",
            0xC5 => "LINT",
            0xC6 => "USINT",
            0xC7 => "UINT",
            0xC8 => "UDINT",
            0xCA => "REAL",
            0xCB => "LREAL",
            0xD3 => "DWORD",
            var t => $"0x{t:X2}"
        };
    }

    /// <summary>Formato do pseudo-tag @tags do libplctag: id(4) tipo(2) tamanho(2) dims(3x4) nomeLen(2) nome.</summary>
    private static List<TagEntry> ParseTagList(byte[] buffer)
    {
        var entries = new List<TagEntry>();
        var pos = 0;
        while (pos + 22 <= buffer.Length)
        {
            var symbolType = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(pos + 4));
            var dim0 = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(pos + 8));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(pos + 20));
            pos += 22;
            if (pos + nameLength > buffer.Length) break;
            var name = Encoding.ASCII.GetString(buffer, pos, nameLength);
            pos += nameLength;

            var dimensions = (symbolType >> 13) & 0x03;
            entries.Add(new TagEntry(name, symbolType, dimensions, dim0));
        }
        return entries;
    }

    // ----- Diagnóstico -----

    public async Task<IReadOnlyList<DiagnosticItem>> RunDiagnosticsAsync(CancellationToken ct = default)
    {
        var items = new List<DiagnosticItem>();
        var s = Settings ?? throw new InvalidOperationException("Conecte primeiro.");

        try
        {
            var id = await ListIdentity.QueryAsync(s.Host, s.Port > 0 ? s.Port : 44818, s.TimeoutMs,
                (direction, data, description) => OnFrame(direction, data, description), ct).ConfigureAwait(false);
            _identity = id;
            items.Add(new("Identificação", $"Produto: {id.ProductName}", DiagnosticSeverity.Ok));
            items.Add(new("Identificação", $"Fabricante: {id.VendorName} · Tipo: {id.DeviceTypeName} · Código: {id.ProductCode}"));
            items.Add(new("Identificação", $"Firmware: {id.RevisionMajor}.{id.RevisionMinor:D3} · Serial: {id.SerialNumber:X8}"));
            items.Add(new("Estado", $"Equipamento {id.StateText} · {id.StatusText} (status 0x{id.Status:X4}, estado {id.State})",
                id.StatusSeverity));
        }
        catch (Exception ex)
        {
            items.Add(new("Identificação", $"ListIdentity falhou: {ex.Message}", DiagnosticSeverity.Warning));
        }

        items.Add(new("Configuração", $"Tipo de CLP: {CurrentPlcType} · Path: {s.GetOption("Path", "1,0")} · Gateway: {Gateway}"));

        try
        {
            var session = await CipClient.RegisterSessionAsync(s.Host, s.Port > 0 ? s.Port : 44818, s.TimeoutMs,
                (direction, data, description) => OnFrame(direction, data, description), ct).ConfigureAwait(false);
            items.Add(new("Sessão CIP", $"RegisterSession aceito (sessão 0x{session:X8}): mensagens explícitas liberadas.", DiagnosticSeverity.Ok));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            items.Add(new("Sessão CIP", $"RegisterSession falhou: {ex.Message}", DiagnosticSeverity.Warning));
        }
        items.Add(new("Segurança", "EtherNet/IP clássico não autentica clientes. Restrinja o acesso à porta 44818 (firewall/VLAN) ou use CIP Security.", DiagnosticSeverity.Warning));
        items.Add(new("Estatísticas", Statistics.Snapshot().ToString()));
        return items;
    }

    /// <summary>
    /// Explica a falha com o código CIP exato: repete a leitura do tag por uma mensagem CIP própria
    /// (Read Tag) e traduz o General/Extended Status devolvido pelo controlador.
    /// Permanent = erro que não se resolve sozinho (tag inexistente, índice, Path...): espaça as novas tentativas.
    /// </summary>
    private async Task<(string Message, bool Permanent)> ExplainAsync(Exception ex, string tagName, CancellationToken ct)
    {
        var basic = Explain(ex, tagName);
        var s = Settings;
        if (s is null || CurrentPlcType is PlcType.Slc500 or PlcType.MicroLogix or PlcType.Plc5)
            return (basic, false); // PLCs PCCC não usam Read Tag simbólico

        lock (_lastProbe)
        {
            // erros passageiros (rede, timeout): no máximo uma consulta CIP extra a cada 15 s por tag
            if (_lastProbe.TryGetValue(tagName, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(15))
                return (basic, false);
            _lastProbe[tagName] = DateTime.UtcNow;
        }

        OnTrace($"libplctag: {ex.GetBaseException().Message} ('{tagName}')");
        try
        {
            var path = CurrentPlcType == PlcType.Micro800 ? string.Empty : s.GetOption("Path", "1,0");
            var result = await CipClient.ReadTagAsync(s.Host, s.Port > 0 ? s.Port : 44818, path, tagName,
                Math.Min(Math.Max(1000, s.TimeoutMs), 3000),
                (direction, data, description) => OnFrame(direction, data, description), ct).ConfigureAwait(false);

            if (result.Status.IsSuccess)
                return ($"A leitura CIP direta de '{tagName}' funciona (tipo {CipErrors.TypeName(result.DataType ?? 0)}): confira o Tipo escolhido no item", false);

            var message = CipErrors.Hint(result.Status, tagName);
            OnTrace(message, FrameDirection.Rx);
            return (message, CipErrors.IsPermanent(result.Status));
        }
        catch (Exception probeError) when (probeError is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            OnTrace($"Diagnóstico CIP indisponível: {probeError.Message}");
            return (basic, false);
        }
    }

    /// <summary>Lança a falha explicada e, se for permanente, guarda para não recriar o tag a cada ciclo.</summary>
    private async Task<Exception> FailAsync(Exception ex, string tagName, CancellationToken ct)
    {
        if (ex is TagFailedException) return new InvalidOperationException(ex.Message);

        var (message, permanent) = await ExplainAsync(ex, tagName, ct).ConfigureAwait(false);
        if (permanent)
            lock (_failedTags) _failedTags[tagName] = (DateTime.UtcNow + RetryPermanentAfter, message);
        return new InvalidOperationException(message, ex);
    }

    private static readonly TimeSpan RetryPermanentAfter = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, DateTime> _lastProbe = new(StringComparer.OrdinalIgnoreCase);

    private sealed class TagFailedException : Exception
    {
        public TagFailedException(string message) : base(message) { }
    }

    private string Explain(Exception ex, string tagName)
    {
        var message = ex.GetBaseException().Message;
        if (message.Contains("ErrorNotFound", StringComparison.OrdinalIgnoreCase) || message.Contains("NotFound", StringComparison.OrdinalIgnoreCase))
            return $"Tag '{tagName}' não encontrado. Tags de programa usam 'Program:NomeDoPrograma.Tag'. ({message})";
        if (message.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
            return $"Timeout no tag '{tagName}'. Confira o Path (ControlLogix: 1,<slot da CPU>) e o tipo de CLP. ({message})";
        if (message.Contains("BadConnection", StringComparison.OrdinalIgnoreCase) || message.Contains("BadGateway", StringComparison.OrdinalIgnoreCase))
            return $"Falha de conexão com {Gateway}. ({message})";
        return message;
    }
}

public static class EtherNetIpDrivers
{
    public static DriverInfo Info { get; } = new()
    {
        Protocol = EtherNetIpDriver.Protocol,
        Category = "Rockwell / CIP",
        Description = "EtherNet/IP (CIP) para ControlLogix, CompactLogix, Micro800, MicroLogix, SLC 500, PLC-5 e Omron NJ/NX.",
        Factory = () => new EtherNetIpDriver(),
        DefaultPort = 44818,
        AddressHelp = "Nome do tag: MeuTag, Program:MainProgram.Contador, Array[3], UDT.Membro · SLC/MicroLogix: N7:0, F8:1, B3:0/2",
        AddressExample = "MeuTag",
        DefaultDataType = FpDataType.Auto,
        Options = new[]
        {
            new DriverOption("PlcType", "Tipo de CLP", OptionKind.Choice, "ControlLogix",
                new[] { "ControlLogix", "Micro800", "MicroLogix", "SLC 500", "PLC-5", "Omron NJ/NX" },
                "ControlLogix também vale para CompactLogix."),
            new DriverOption("Path", "Path (rota CIP)", OptionKind.Text, "1,0", Help: "Backplane,slot da CPU. CompactLogix: 1,0. Micro800: deixe vazio."),
            new DriverOption("AutoReconnect", "Reconexão automática", OptionKind.Bool, "true")
        }
    };
}
