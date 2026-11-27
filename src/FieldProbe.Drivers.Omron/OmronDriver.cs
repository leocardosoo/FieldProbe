using FieldProbe.Core;
using FieldProbe.Core.Codec;

namespace FieldProbe.Drivers.Omron;

/// <summary>
/// Driver Omron FINS (TCP ou UDP, porta 9600): CS1, CJ1/CJ2, CP1L-E/CP1H com Ethernet e NJ/NX pela porta FINS.
/// Valores de 32 bits seguem o padrão Omron: palavra baixa no endereço menor (D100 = baixa, D101 = alta).
/// </summary>
public sealed class OmronDriver : DriverBase, IDiagnosticsProvider, IFrameDecoder
{
    public const string Protocol = "Omron FINS";

    private FinsClient? _client;
    private bool _healthUnsupported;

    public override string ProtocolName => Protocol;

    public override bool IsConnected => _client?.IsConnected == true;

    protected override string Endpoint(ConnectionSettings s) =>
        $"{s.Host}:{s.Port} (FINS/{s.GetOption("Transport", OmronDrivers.TransportTcp)}" +
        (_client is { } c ? $", nó CLP {c.PlcNode}, nó PC {c.PcNode})" : ")");

    protected override async Task OpenAsync(ConnectionSettings settings, CancellationToken ct)
    {
        _healthUnsupported = false;
        var udp = settings.GetOption("Transport", OmronDrivers.TransportTcp) == OmronDrivers.TransportUdp;
        var client = new FinsClient(settings.Host, settings.Port > 0 ? settings.Port : OmronDrivers.DefaultPort, settings.TimeoutMs, udp,
            (byte)Math.Clamp(settings.GetIntOption("Network", 0), 0, 127),
            (byte)Math.Clamp(settings.GetIntOption("PlcNode", 0), 0, 254),
            (byte)Math.Clamp(settings.GetIntOption("PcNode", 0), 0, 254));
        client.Frame += (direction, data, description) => OnFrame(direction, data, description);

        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
            // No UDP não existe conexão: um pedido curto (estado da CPU) confirma que o CLP responde
            if (udp) await client.ReadStatusAsync(ct).ConfigureAwait(false);
        }
        catch (FinsException)
        {
            // O CLP respondeu em FINS (só recusou o comando de estado): a conexão vale
        }
        catch
        {
            client.Dispose();
            throw;
        }
        _client = client;
    }

    protected override Task CloseAsync()
    {
        _client?.Dispose();
        _client = null;
        return Task.CompletedTask;
    }

    // ===================== Árvore =====================

    public override Task<IReadOnlyList<TagNode>> BrowseAsync(TagNode? parent = null, CancellationToken ct = default)
    {
        if (parent is not null)
            return Task.FromResult<IReadOnlyList<TagNode>>(parent.Children);

        var custom = CustomTags.Load(Settings);

        TagNode Folder(string name, string key, string hint, string description)
        {
            var folder = new TagNode(name)
            {
                CustomFolder = key,
                AddressHint = hint,
                Description = description + "\nO CLP não informa os nomes das variáveis: clique em + para adicionar com o endereço e o tipo do projeto do CX-Programmer."
            };
            CustomTags.AddTo(folder, custom);
            return folder;
        }

        IReadOnlyList<TagNode> roots = new[]
        {
            Folder("Memória de dados (D)", "D", "D0", "Palavras de 16 bits (D0 a D32767). INT/UINT ocupam 1 palavra; DINT/REAL ocupam 2 (D100 = parte baixa, D101 = parte alta); LREAL ocupa 4. Bit de palavra: D100.05."),
            Folder("Entradas e saídas (CIO)", "CIO", "0.00", "Área CIO: entradas, saídas e relés. Bit = palavra.bit (0.00 = 1ª entrada de uma CP1, 100.00 = 1ª saída). Palavra inteira: CIO100 ou 100."),
            Folder("Área de trabalho (W)", "W", "W0.00", "Bits e palavras internos (W0 a W511): W10.03 = bit 3 do W10."),
            Folder("Área retentiva (H)", "H", "H0", "Bits e palavras que mantêm o valor ao desligar (H0 a H1535): H5.01 = bit 1 do H5."),
            Folder("Área auxiliar (A)", "A", "A0", "Informações do sistema (A0 a A1471). Muitas são somente leitura."),
            Folder("Temporizadores (T)", "T", "T0", "Valor atual (PV) do temporizador T0 a T4095. Pode estar em BCD, conforme a configuração da CPU."),
            Folder("Contadores (C)", "C", "C0", "Valor atual (PV) do contador C0 a C4095. Pode estar em BCD, conforme a configuração da CPU."),
            Folder("Memória estendida (E)", "E", "E0_0", "Bancos E0_ a EC_ (ex.: E0_100 = palavra 100 do banco 0). Nem toda CPU tem EM.")
        };
        return Task.FromResult(roots);
    }

    public DecodedFrame? DecodeFrame(byte[] data, FrameDirection direction) =>
        FinsFrameDecoder.CanDecode(data) ? FinsFrameDecoder.Decode(data) : null;

    // ===================== Leitura e escrita =====================

    private static DataType Resolve(FinsAddress a, DataType requested) =>
        requested != DataType.Auto ? requested : a.IsBit ? DataType.Bool : DataType.Int16;

    private static int WordCount(DataType type, int length) => type switch
    {
        DataType.Int32 or DataType.UInt32 or DataType.Float32 => 2,
        DataType.Int64 or DataType.Float64 => 4,
        DataType.String => (Math.Clamp(length, 1, 1980) + 1) / 2,
        _ => 1
    };

    /// <summary>32/64 bits: palavra baixa primeiro (= ordem CDAB do Modbus); texto: 1º caractere no byte alto.</summary>
    private static ByteOrder OrderFor(DataType type) => type == DataType.String ? ByteOrder.ABCD : ByteOrder.CDAB;

    protected override async Task<object?> ReadCoreAsync(TagAddress address, CancellationToken ct)
    {
        var client = _client ?? throw new InvalidOperationException("Não conectado.");
        var a = FinsAddress.Parse(address.Address);
        var type = Resolve(a, address.DataType);

        if (a.IsBit)
            return await client.ReadBitAsync(a.Area, a.Word, a.Bit, ct).ConfigureAwait(false);

        var words = await client.ReadWordsAsync(a.Area, a.Word, WordCount(type, address.Length), ct).ConfigureAwait(false);
        return ValueCodec.FromRegisters(words, type, OrderFor(type));
    }

    protected override async Task WriteCoreAsync(TagAddress address, object value, CancellationToken ct)
    {
        var client = _client ?? throw new InvalidOperationException("Não conectado.");
        var a = FinsAddress.Parse(address.Address);
        var type = Resolve(a, address.DataType);

        if (a.IsBit)
        {
            await client.WriteBitAsync(a.Area, a.Word, a.Bit, ValueCodec.ParseBool(value), ct).ConfigureAwait(false);
            return;
        }

        var words = ValueCodec.ToRegisters(value, type, OrderFor(type), WordCount(type, address.Length));
        await client.WriteWordsAsync(a.Area, a.Word, words, ct).ConfigureAwait(false);
    }

    // ===================== Estado da CPU =====================

    protected override bool SupportsHealthCheck => !_healthUnsupported;

    protected override async Task<string?> CheckDeviceHealthAsync(CancellationToken ct)
    {
        var client = _client;
        if (client is null) return null;
        try
        {
            var status = await client.ReadStatusAsync(ct).ConfigureAwait(false);
            if (status.FatalFlags != 0) return $"CPU com ERRO FATAL (0x{status.FatalFlags:X4}){(status.ErrorMessage.Length > 0 ? ": " + status.ErrorMessage : string.Empty)}";
            return status.IsStopped ? "CPU em PROGRAM: o programa não está rodando (valores congelados)" : null;
        }
        catch (FinsException)
        {
            _healthUnsupported = true; // CPU não responde ao 06 01: para de consultar
            return null;
        }
    }

    public async Task<IReadOnlyList<DiagnosticItem>> RunDiagnosticsAsync(CancellationToken ct = default)
    {
        var items = new List<DiagnosticItem>();
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var client = _client!;
        if (Settings is { } s) items.Add(new DiagnosticItem("Conexão", Endpoint(s)));
        items.Add(new DiagnosticItem("Sessão", client.IsUdp
            ? $"FINS/UDP · nó do CLP {client.PlcNode}, nó deste PC {client.PcNode}"
            : $"FINS/TCP · nós atribuídos pelo CLP: PC = {client.PcNode}, CLP = {client.PlcNode}", DiagnosticSeverity.Ok));

        try
        {
            var (model, version) = await client.ReadModelAsync(ct).ConfigureAwait(false);
            items.Add(new DiagnosticItem("CPU", $"Modelo: {model}{(version.Length > 0 ? $" · versão {version}" : string.Empty)}", DiagnosticSeverity.Ok));
        }
        catch (FinsException ex)
        {
            items.Add(new DiagnosticItem("CPU", $"A CPU não informou o modelo ({FinsException.Describe(ex.EndCode)})."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            items.Add(new DiagnosticItem("CPU", $"Modelo indisponível: {ex.Message}"));
        }

        try
        {
            var status = await client.ReadStatusAsync(ct).ConfigureAwait(false);
            var severity = status.IsStopped ? DiagnosticSeverity.Critical : status.Mode == 0x02 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Ok;
            var hint = status.Mode == 0x02 ? " (MONITOR: roda o programa e aceita alterações online)" : status.IsStopped ? " (o programa não está rodando)" : string.Empty;
            items.Add(new DiagnosticItem("Estado da CPU", $"Modo de operação: {status.ModeText}{hint}", severity));
            items.Add(status.FatalFlags != 0
                ? new DiagnosticItem("Erros da CPU", $"ERRO FATAL (sinalizadores 0x{status.FatalFlags:X4}){(status.ErrorMessage.Length > 0 ? ": " + status.ErrorMessage : string.Empty)}", DiagnosticSeverity.Critical)
                : status.NonFatalFlags != 0
                    ? new DiagnosticItem("Erros da CPU", $"Erro não fatal (sinalizadores 0x{status.NonFatalFlags:X4}){(status.ErrorMessage.Length > 0 ? ": " + status.ErrorMessage : string.Empty)}", DiagnosticSeverity.Warning)
                    : new DiagnosticItem("Erros da CPU", "Nenhum erro ativo.", DiagnosticSeverity.Ok));
        }
        catch (FinsException ex)
        {
            items.Add(new DiagnosticItem("Estado da CPU", $"Não foi possível ler o estado: {FinsException.Describe(ex.EndCode)}"));
        }

        items.Add(new DiagnosticItem("Segurança",
            "FINS não tem autenticação nem criptografia: qualquer PC da rede que alcance a porta 9600 lê e escreve na CPU (e pode mudar o modo). " +
            "Restrinja por firewall/VLAN e, no CX-Programmer, use a proteção por IP da porta FINS/TCP e a proteção de escrita da CPU.",
            DiagnosticSeverity.Warning));
        items.Add(new DiagnosticItem("Estatísticas", Statistics.Snapshot().ToString()));
        return items;
    }
}

public static class OmronDrivers
{
    public const string TransportTcp = "TCP";
    public const string TransportUdp = "UDP";
    public const int DefaultPort = 9600;

    public static DriverInfo Info { get; } = new()
    {
        Protocol = OmronDriver.Protocol,
        Category = "Omron",
        Description = "Omron FINS por TCP ou UDP (porta 9600): CS1, CJ1/CJ2, CP1L-E/CP1H e NJ/NX.",
        Factory = () => new OmronDriver(),
        DefaultPort = DefaultPort,
        AddressHelp = "D100 (INT), D200 + tipo Float32 (usa D200 e D201), D100.05 (bit), 0.00 / 100.00 (CIO), CIO100, W10.03, H5, A100, T0, C0, E0_100",
        AddressExample = "D0",
        DefaultDataType = DataType.Auto,
        Options = new[]
        {
            new DriverOption("Transport", "Transporte", OptionKind.Choice, TransportTcp, new[] { TransportTcp, TransportUdp },
                "TCP é o padrão em CJ2/CS1/NJ. Alguns CP1L-E/CP1H só aceitam UDP."),
            new DriverOption("PlcNode", "Nó do CLP", OptionKind.Number, "0", Help: "Só no UDP. 0 = último número do IP do CLP (padrão Omron). No TCP o CLP informa sozinho."),
            new DriverOption("PcNode", "Nó do PC", OptionKind.Number, "0", Help: "0 = automático (TCP: o CLP atribui · UDP: último número do IP deste PC)."),
            new DriverOption("Network", "Nº da rede", OptionKind.Number, "0", Help: "0 = rede local. Outro valor só para acessar um CLP em outra rede pela tabela de roteamento."),
            new DriverOption("AutoReconnect", "Reconexão automática", OptionKind.Bool, "true")
        }
    };
}
