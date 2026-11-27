using System.Text;
using FieldProbe.Core;
using FieldProbe.Core.Codec;

namespace FieldProbe.Drivers.Mitsubishi;

/// <summary>
/// Driver Mitsubishi MELSEC por MC Protocol (SLMP), frame 3E binário: Q/L, iQ-R e iQ-F (FX5).
/// Na CPU é preciso abrir uma porta "MC Protocol" (iQ-F: "SLMP"), TCP, código Binário.
/// </summary>
public sealed class MitsubishiDriver : DriverBase, IDiagnosticsProvider, IFrameDecoder
{
    public const string Protocol = "Mitsubishi MC (SLMP)";

    /// <summary>SD203: estado de operação da CPU (bits 0 a 3: 0 = RUN, 2 = STOP, 3 = PAUSE).</summary>
    private const int StatusRegister = 203;

    private McClient? _client;
    private McSeries _series = McSeries.QL;
    private bool _healthUnsupported;

    public override string ProtocolName => Protocol;

    public override bool IsConnected => _client?.IsConnected == true;

    protected override string Endpoint(ConnectionSettings s) => $"{s.Host}:{s.Port} ({s.GetOption("Series", MitsubishiDrivers.SeriesQ)})";

    public static McSeries ParseSeries(string text) => text switch
    {
        MitsubishiDrivers.SeriesIqR => McSeries.IqR,
        MitsubishiDrivers.SeriesIqF => McSeries.IqF,
        _ => McSeries.QL
    };

    protected override async Task OpenAsync(ConnectionSettings settings, CancellationToken ct)
    {
        _series = ParseSeries(settings.GetOption("Series", MitsubishiDrivers.SeriesQ));
        _healthUnsupported = false;
        var client = new McClient(settings.Host, settings.Port > 0 ? settings.Port : MitsubishiDrivers.DefaultPort, settings.TimeoutMs,
            (byte)Math.Clamp(settings.GetIntOption("Network", 0), 0, 255),
            (byte)Math.Clamp(settings.GetIntOption("PcNo", 255), 0, 255),
            _series);
        client.Frame += (direction, data, description) => OnFrame(direction, data, description);

        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
            // O TCP abrir não garante que é MC Protocol: um pedido curto confirma (lê SD203)
            await client.ReadWordsAsync(Device("SD"), StatusRegister, 1, ct).ConfigureAwait(false);
        }
        catch (McException)
        {
            // A CPU respondeu em MC Protocol (só recusou o SD203): a conexão vale
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

    private static McDevice Device(string symbol) => McAddress.Devices.First(d => d.Symbol == symbol);

    private McAddress ParseAddress(string text) => McAddress.Parse(text, _series);

    // ===================== Árvore =====================

    public override Task<IReadOnlyList<TagNode>> BrowseAsync(TagNode? parent = null, CancellationToken ct = default)
    {
        if (parent is not null)
            return Task.FromResult<IReadOnlyList<TagNode>>(parent.Children);

        var series = ParseSeries(Settings?.GetOption("Series", MitsubishiDrivers.SeriesQ) ?? MitsubishiDrivers.SeriesQ);
        var octal = series == McSeries.IqF;
        var custom = CustomTags.Load(Settings);

        TagNode Folder(string name, string key, string hint, string description)
        {
            var folder = new TagNode(name)
            {
                CustomFolder = key,
                AddressHint = hint,
                Description = description + "\nO CLP não informa os nomes das variáveis: clique em + para adicionar com o endereço e o tipo do projeto do GX Works."
            };
            CustomTags.AddTo(folder, custom);
            return folder;
        }

        IReadOnlyList<TagNode> roots = new[]
        {
            Folder("Registradores de dados (D)", "D", "D0", "Palavras de 16 bits (D0, D100...). Int32/Real ocupam 2 registradores (D100 e D101), Float64 ocupa 4. Bit de um registrador: D100.5. Também aceita Z (índice)."),
            Folder("Relés internos (M)", "M", "M0", "Bits internos do programa (M0, M100...). Também aceita L (latch), F (anunciador) e V."),
            Folder("Entradas (X)", "X", "X0", octal
                ? "Entradas físicas. No iQ-F (FX5) a numeração é octal: X0 a X7, X10 a X17..."
                : "Entradas físicas, numeração hexadecimal (X0 a XF, X10...). Escrever em X não adianta: o CLP sobrescreve a cada ciclo."),
            Folder("Saídas (Y)", "Y", "Y0", octal
                ? "Saídas físicas. No iQ-F (FX5) a numeração é octal: Y0 a Y7, Y10 a Y17..."
                : "Saídas físicas, numeração hexadecimal (Y0 a YF, Y10...). Se o programa também escreve na saída, o CLP sobrescreve a cada ciclo."),
            Folder("Registradores de link (W)", "W", "W0", "Palavras de link (CC-Link IE etc.), numeração hexadecimal (W0, W1A...). Também aceita SW."),
            Folder("Relés de link (B)", "B", "B0", "Bits de link, numeração hexadecimal (B0, B1F...). Também aceita SB."),
            Folder("Registradores de arquivo (R)", "R", "R0", "Palavras de 16 bits na memória de arquivo (R0, R100...)."),
            Folder("Temporizadores (T)", "T", "TN0", "TN = valor atual (palavra), TS = contato, TC = bobina. Retentivos: SN, SS, SC."),
            Folder("Contadores (C)", "C", "CN0", "CN = valor atual (palavra), CS = contato, CC = bobina."),
            Folder("Relés especiais (SM)", "SM", "SM400", "Bits do sistema. Ex.: SM400 = sempre ligado em RUN, SM412 = pulso de 1 s."),
            Folder("Registradores especiais (SD)", "SD", "SD203", "Palavras do sistema. Ex.: SD203 = estado da CPU (0 = RUN, 2 = STOP), SD0 = código do último erro.")
        };
        return Task.FromResult(roots);
    }

    public DecodedFrame? DecodeFrame(byte[] data, FrameDirection direction) =>
        data.Length >= 9 && data[1] == 0x00 && (data[0] == 0x50 || data[0] == 0xD0)
            ? McFrameDecoder.Decode(data, _series)
            : null;

    // ===================== Leitura e escrita =====================

    private static int WordCount(DataType type, int length) => type switch
    {
        DataType.Int32 or DataType.UInt32 or DataType.Float32 => 2,
        DataType.Int64 or DataType.Float64 => 4,
        DataType.String => (Math.Clamp(length, 1, 1920) + 1) / 2,
        _ => 1
    };

    /// <summary>Tipo padrão: bit para dispositivos de bit, Int16 (com sinal, padrão Mitsubishi) para palavras.</summary>
    private static DataType Resolve(McAddress a, DataType requested) =>
        requested != DataType.Auto ? requested : a.Device.IsBit || a.IsWordBit ? DataType.Bool : DataType.Int16;

    protected override async Task<object?> ReadCoreAsync(TagAddress address, CancellationToken ct)
    {
        var client = _client ?? throw new InvalidOperationException("Não conectado.");
        var a = ParseAddress(address.Address);
        var type = Resolve(a, address.DataType);

        if (a.IsWordBit)
        {
            var word = await client.ReadWordsAsync(a.Device, a.Number, 1, ct).ConfigureAwait(false);
            return ((word[0] | (word[1] << 8)) & (1 << a.Bit)) != 0;
        }

        if (a.Device.IsBit && type == DataType.Bool)
            return (await client.ReadBitsAsync(a.Device, a.Number, 1, ct).ConfigureAwait(false))[0];

        var bytes = await client.ReadWordsAsync(a.Device, a.Number, WordCount(type, address.Length), ct).ConfigureAwait(false);
        return Decode(bytes, type, address.Length);
    }

    protected override async Task WriteCoreAsync(TagAddress address, object value, CancellationToken ct)
    {
        var client = _client ?? throw new InvalidOperationException("Não conectado.");
        var a = ParseAddress(address.Address);
        var type = Resolve(a, address.DataType);

        if (a.IsWordBit)
        {
            // Bit de palavra: lê a palavra, muda só o bit e escreve de volta
            var word = await client.ReadWordsAsync(a.Device, a.Number, 1, ct).ConfigureAwait(false);
            var current = (ushort)(word[0] | (word[1] << 8));
            var updated = ValueCodec.ParseBool(value) ? (ushort)(current | (1 << a.Bit)) : (ushort)(current & ~(1 << a.Bit));
            await client.WriteWordsAsync(a.Device, a.Number, new[] { (byte)updated, (byte)(updated >> 8) }, ct).ConfigureAwait(false);
            return;
        }

        if (a.Device.IsBit && type == DataType.Bool)
        {
            await client.WriteBitsAsync(a.Device, a.Number, new[] { ValueCodec.ParseBool(value) }, ct).ConfigureAwait(false);
            return;
        }

        await client.WriteWordsAsync(a.Device, a.Number, Encode(value, type, address.Length), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Mitsubishi guarda cada palavra em little-endian e valores de 32/64 bits com a palavra baixa primeiro
    /// (D100 = parte baixa, D101 = parte alta): o bloco inteiro é little-endian.
    /// </summary>
    public static object Decode(byte[] bytes, DataType type, int length)
    {
        switch (type)
        {
            case DataType.Bool:
                return bytes[0] != 0 || bytes[1] != 0;
            case DataType.Byte:
                return bytes[0];
            case DataType.String:
                var count = Math.Min(Math.Clamp(length, 1, bytes.Length), bytes.Length);
                return Encoding.ASCII.GetString(bytes, 0, count).TrimEnd('\0', ' ');
            default:
                var size = ValueCodec.ByteSize(type);
                var big = bytes.Take(size).Reverse().ToArray();
                return ValueCodec.DecodeBigEndian(big, type);
        }
    }

    public static byte[] Encode(object value, DataType type, int length)
    {
        switch (type)
        {
            case DataType.Bool:
                return new byte[] { ValueCodec.ParseBool(value) ? (byte)1 : (byte)0, 0 };
            case DataType.Byte:
                return new byte[] { ValueCodec.EncodeBigEndian(value, DataType.Byte)[0], 0 };
            case DataType.String:
                var words = WordCount(DataType.String, length);
                var text = Convert.ToString(value) ?? string.Empty;
                var bytes = new byte[words * 2];
                Encoding.ASCII.GetBytes(text, 0, Math.Min(text.Length, Math.Clamp(length, 1, bytes.Length)), bytes, 0);
                return bytes;
            default:
                var big = ValueCodec.EncodeBigEndian(value, type);
                Array.Reverse(big);
                return big;
        }
    }

    // ===================== Estado da CPU =====================

    protected override bool SupportsHealthCheck => !_healthUnsupported;

    protected override async Task<string?> CheckDeviceHealthAsync(CancellationToken ct)
    {
        var client = _client;
        if (client is null) return null;
        try
        {
            var data = await client.ReadWordsAsync(Device("SD"), StatusRegister, 1, ct).ConfigureAwait(false);
            return (data[0] & 0x0F) switch
            {
                2 => "CPU em STOP: o programa não está rodando (valores congelados)",
                3 => "CPU em PAUSE: programa pausado",
                _ => null
            };
        }
        catch (McException)
        {
            _healthUnsupported = true; // esta CPU não deixa ler o SD203: para de consultar
            return null;
        }
    }

    private static string DescribeState(int state) => state switch
    {
        0 => "RUN",
        1 => "STEP-RUN",
        2 => "STOP",
        3 => "PAUSE",
        _ => $"desconhecido ({state})"
    };

    public async Task<IReadOnlyList<DiagnosticItem>> RunDiagnosticsAsync(CancellationToken ct = default)
    {
        var items = new List<DiagnosticItem>();
        if (Settings is { } s) items.Add(new("Conexão", Endpoint(s)));

        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        var client = _client!;
        items.Add(new("Sessão", "Conectado · MC Protocol frame 3E binário", DiagnosticSeverity.Ok));

        try
        {
            var (model, code) = await client.ReadCpuModelAsync(ct).ConfigureAwait(false);
            items.Add(new("CPU", $"Modelo: {model} (código 0x{code:X4})", DiagnosticSeverity.Ok));
        }
        catch (McException ex)
        {
            items.Add(new("CPU", $"A CPU não informou o modelo ({McException.Describe(ex.EndCode)})."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            items.Add(new("CPU", $"Modelo indisponível: {ex.Message}"));
        }

        try
        {
            var data = await client.ReadWordsAsync(Device("SD"), StatusRegister, 1, ct).ConfigureAwait(false);
            var state = data[0] & 0x0F;
            var severity = state switch { 0 => DiagnosticSeverity.Ok, 2 => DiagnosticSeverity.Critical, _ => DiagnosticSeverity.Warning };
            items.Add(new("Estado da CPU", $"Modo de operação (SD203): {DescribeState(state)}", severity));

            var error = await client.ReadWordsAsync(Device("SD"), 0, 1, ct).ConfigureAwait(false);
            var errorCode = error[0] | (error[1] << 8);
            items.Add(errorCode == 0
                ? new DiagnosticItem("Erro da CPU", "SD0 = 0: nenhum erro registrado.", DiagnosticSeverity.Ok)
                : new DiagnosticItem("Erro da CPU", $"SD0 = {errorCode} (0x{errorCode:X4}): código do último erro. Veja o diagnóstico da CPU no GX Works.", DiagnosticSeverity.Warning));
        }
        catch (McException ex)
        {
            items.Add(new("Estado da CPU", $"Não foi possível ler SD203/SD0: {McException.Describe(ex.EndCode)}"));
        }

        items.Add(new("Segurança",
            "MC Protocol não tem autenticação nem criptografia: qualquer PC da rede que alcance esta porta lê e escreve na CPU. " +
            "Restrinja o acesso por firewall/VLAN e, se possível, use a senha remota (remote password) da porta ou deixe a escrita em RUN desabilitada.",
            DiagnosticSeverity.Warning));
        items.Add(new("Estatísticas", Statistics.Snapshot().ToString()));
        return items;
    }
}

public static class MitsubishiDrivers
{
    public const string SeriesQ = "Q / L";
    public const string SeriesIqR = "iQ-R";
    public const string SeriesIqF = "iQ-F (FX5)";
    public const int DefaultPort = 5000;

    public static DriverInfo Info { get; } = new()
    {
        Protocol = MitsubishiDriver.Protocol,
        Category = "Mitsubishi",
        Description = "Mitsubishi MELSEC (Q, L, iQ-R, iQ-F/FX5) por MC Protocol / SLMP, frame 3E binário.",
        Factory = () => new MitsubishiDriver(),
        DefaultPort = DefaultPort,
        AddressHelp = "D100 (Int16), D200 + tipo Float32 (usa D200 e D201), M10, X1F, Y20 (iQ-F: octal), W1A, R0, TN0, CN0, SM400, SD203, D100.5 (bit de palavra)",
        AddressExample = "D0",
        DefaultDataType = DataType.Auto,
        Options = new[]
        {
            new DriverOption("Series", "Série da CPU", OptionKind.Choice, SeriesQ, new[] { SeriesQ, SeriesIqR, SeriesIqF },
                "Muda a numeração de X/Y: hexadecimal no Q/L/iQ-R, octal no iQ-F (FX5)."),
            new DriverOption("Network", "Nº da rede", OptionKind.Number, "0", Help: "0 = a própria CPU. Só muda para acessar outra estação pela rede (CC-Link IE)."),
            new DriverOption("PcNo", "Nº do PC", OptionKind.Number, "255", Help: "255 (0xFF) = a CPU conectada."),
            new DriverOption("AutoReconnect", "Reconexão automática", OptionKind.Bool, "true")
        }
    };
}
