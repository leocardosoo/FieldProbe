using System.Text;
using FieldProbe.Core;
using FieldProbe.Core.Codec;
using S7.Net;
using FpDataType = FieldProbe.Core.DataType;
using S7Area = S7.Net.DataType;

namespace FieldProbe.Drivers.Siemens;

/// <summary>
/// Driver Siemens S7 (S7-200/300/400/1200/1500) via S7netplus.
/// Para S7-1200/1500: habilitar PUT/GET e usar DBs com acesso "não otimizado".
/// </summary>
public sealed class S7Driver : DriverBase, IDiagnosticsProvider
{
    public const string Protocol = "Siemens S7";

    private Plc? _plc;

    public override string ProtocolName => Protocol;

    public override bool IsConnected => _plc?.IsConnected == true;

    protected override string Endpoint(ConnectionSettings s) =>
        $"{s.Host}:{s.Port} ({s.GetOption("Cpu", "S7-1200")}, rack {s.GetIntOption("Rack", 0)}, slot {s.GetIntOption("Slot", 1)})";

    protected override async Task OpenAsync(ConnectionSettings settings, CancellationToken ct)
    {
        var cpu = settings.GetOption("Cpu", "S7-1200") switch
        {
            "S7-200" => CpuType.S7200,
            "S7-300" => CpuType.S7300,
            "S7-400" => CpuType.S7400,
            "S7-1500" => CpuType.S71500,
            _ => CpuType.S71200
        };

        var plc = new Plc(cpu, settings.Host, settings.Port > 0 ? settings.Port : 102,
            (short)settings.GetIntOption("Rack", 0), (short)settings.GetIntOption("Slot", 1))
        {
            ReadTimeout = settings.TimeoutMs,
            WriteTimeout = settings.TimeoutMs
        };

        OnTrace($"COTP Connect + S7 Setup Communication → {Endpoint(settings)}", FrameDirection.Tx);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Math.Max(3000, settings.TimeoutMs));
        try
        {
            await plc.OpenAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            plc.Close();
            throw new TimeoutException("O CLP não respondeu. Confira IP, rack/slot e se a porta 102 está liberada.");
        }
        catch (Exception ex)
        {
            plc.Close();
            throw new InvalidOperationException(Explain(ex), ex);
        }

        _plc = plc;
        OnTrace($"Conectado · PDU negociada: {plc.MaxPDUSize} bytes", FrameDirection.Rx);
    }

    protected override Task CloseAsync()
    {
        _plc?.Close();
        _plc = null;
        return Task.CompletedTask;
    }

    public override Task<IReadOnlyList<TagNode>> BrowseAsync(TagNode? parent = null, CancellationToken ct = default)
    {
        if (parent is not null)
            return Task.FromResult<IReadOnlyList<TagNode>>(parent.Children);

        // S7 "clássico" não expõe símbolos: árvore sugerida com os DBs configurados.
        var dbs = (Settings?.GetOption("DbList", "1") ?? "1")
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => int.TryParse(x, out var n) ? n : -1)
            .Where(n => n > 0)
            .Distinct()
            .ToList();

        var roots = new List<TagNode>();
        foreach (var db in dbs)
        {
            var folder = new TagNode($"DB{db}");
            for (var i = 0; i < 8; i++) folder.Children.Add(Leaf($"DB{db}.DBX0.{i}", FpDataType.Bool));
            for (var i = 0; i < 20; i += 2) folder.Children.Add(Leaf($"DB{db}.DBW{i}", FpDataType.Int16));
            for (var i = 0; i < 20; i += 4) folder.Children.Add(Leaf($"DB{db}.DBD{i}", FpDataType.Float32));
            roots.Add(folder);
        }

        var memory = new TagNode("Marcadores (M)");
        for (var i = 0; i < 8; i++) memory.Children.Add(Leaf($"M0.{i}", FpDataType.Bool));
        for (var i = 0; i < 10; i += 2) memory.Children.Add(Leaf($"MW{i}", FpDataType.Int16));
        roots.Add(memory);

        var inputs = new TagNode("Entradas (I)");
        for (var i = 0; i < 8; i++) inputs.Children.Add(Leaf($"I0.{i}", FpDataType.Bool));
        inputs.Children.Add(Leaf("IW0", FpDataType.Int16));
        roots.Add(inputs);

        var outputs = new TagNode("Saídas (Q)");
        for (var i = 0; i < 8; i++) outputs.Children.Add(Leaf($"Q0.{i}", FpDataType.Bool));
        outputs.Children.Add(Leaf("QW0", FpDataType.Int16));
        roots.Add(outputs);

        return Task.FromResult<IReadOnlyList<TagNode>>(roots);
    }

    private static TagNode Leaf(string address, FpDataType type) => new(address, new TagAddress(address, type));

    protected override async Task<object?> ReadCoreAsync(TagAddress address, CancellationToken ct)
    {
        var plc = _plc ?? throw new InvalidOperationException("Não conectado.");
        var s7 = S7Address.Parse(address.Address);
        var type = address.DataType == FpDataType.Auto ? s7.DefaultType : address.DataType;

        try
        {
            if (s7.IsBit || type == FpDataType.Bool)
            {
                var data = await plc.ReadBytesAsync(s7.Area, s7.Db, s7.ByteOffset, 1, ct).ConfigureAwait(false);
                return (data[0] & (1 << s7.Bit)) != 0;
            }

            if (type == FpDataType.String)
            {
                // S7 STRING: [tamanho máximo][tamanho atual][caracteres...]
                var max = Math.Clamp(address.Length, 1, 254);
                var data = await plc.ReadBytesAsync(s7.Area, s7.Db, s7.ByteOffset, max + 2, ct).ConfigureAwait(false);
                var actual = Math.Min(data[1], (byte)max);
                return Encoding.ASCII.GetString(data, 2, actual);
            }

            var bytes = await plc.ReadBytesAsync(s7.Area, s7.Db, s7.ByteOffset, ValueCodec.ByteSize(type), ct).ConfigureAwait(false);
            return ValueCodec.DecodeBigEndian(bytes, type);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(Explain(ex), ex);
        }
    }

    protected override async Task WriteCoreAsync(TagAddress address, object value, CancellationToken ct)
    {
        var plc = _plc ?? throw new InvalidOperationException("Não conectado.");
        var s7 = S7Address.Parse(address.Address);
        var type = address.DataType == FpDataType.Auto ? s7.DefaultType : address.DataType;

        try
        {
            if (s7.IsBit || type == FpDataType.Bool)
            {
                await plc.WriteBitAsync(s7.Area, s7.Db, s7.ByteOffset, s7.Bit, ValueCodec.ParseBool(value), ct).ConfigureAwait(false);
                return;
            }

            byte[] bytes;
            if (type == FpDataType.String)
            {
                var max = Math.Clamp(address.Length, 1, 254);
                var text = Convert.ToString(value) ?? string.Empty;
                var chars = Encoding.ASCII.GetBytes(text.Length > max ? text[..max] : text);
                bytes = new byte[2 + chars.Length];
                bytes[0] = (byte)max;
                bytes[1] = (byte)chars.Length;
                chars.CopyTo(bytes, 2);
            }
            else
            {
                bytes = ValueCodec.EncodeBigEndian(value, type);
            }

            await plc.WriteBytesAsync(s7.Area, s7.Db, s7.ByteOffset, bytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not FormatException)
        {
            throw new InvalidOperationException(Explain(ex), ex);
        }
    }

    public async Task<IReadOnlyList<DiagnosticItem>> RunDiagnosticsAsync(CancellationToken ct = default)
    {
        var items = new List<DiagnosticItem>();
        var s = Settings;
        if (s is not null) items.Add(new("Conexão", Endpoint(s)));

        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        items.Add(new("Sessão", $"Conectado · tamanho de PDU negociado: {_plc!.MaxPDUSize} bytes", DiagnosticSeverity.Ok));

        var firstDb = (s?.GetOption("DbList", "1") ?? "1").Split(',')[0].Trim();
        try
        {
            await _plc.ReadBytesAsync(S7Area.DataBlock, int.Parse(firstDb), 0, 1, ct).ConfigureAwait(false);
            items.Add(new("Acesso a dados", $"Leitura do DB{firstDb} OK: acesso PUT/GET liberado.", DiagnosticSeverity.Ok));
            items.Add(new("Segurança", "O CLP aceita leitura/escrita remota sem senha (PUT/GET). Proteja a rede da máquina.", DiagnosticSeverity.Warning));
        }
        catch (Exception ex)
        {
            items.Add(new("Acesso a dados", $"Leitura do DB{firstDb} falhou: {Explain(ex)}", DiagnosticSeverity.Warning));
        }

        if (s is not null) await AddCpuDiagnosticsAsync(s, items, ct).ConfigureAwait(false);

        items.Add(new("Estatísticas", Statistics.Snapshot().ToString()));
        return items;
    }

    /// <summary>Identificação, modo RUN/STOP e buffer de diagnóstico, por uma conexão S7 separada (só leitura de SZL).</summary>
    private async Task AddCpuDiagnosticsAsync(ConnectionSettings s, List<DiagnosticItem> items, CancellationToken ct)
    {
        S7SystemClient system;
        try
        {
            system = await S7SystemClient.ConnectAsync(s.Host, s.Port > 0 ? s.Port : 102,
                s.GetIntOption("Rack", 0), s.GetIntOption("Slot", 1), s.TimeoutMs, ct,
                (direction, data, description) => OnFrame(direction, data, description)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            items.Add(new("CPU", $"Não foi possível abrir a conexão de diagnóstico: {ex.Message}", DiagnosticSeverity.Warning));
            return;
        }

        await using (system)
        {
            try
            {
                var identity = await system.ReadIdentityAsync(ct).ConfigureAwait(false);
                if (identity is not null) items.Add(new("CPU", identity.ToString(), DiagnosticSeverity.Ok));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                items.Add(new("CPU", $"Identificação indisponível: {ex.Message}"));
            }

            try
            {
                var mode = await system.ReadCpuModeAsync(ct).ConfigureAwait(false);
                var severity = mode switch
                {
                    S7CpuMode.Run => DiagnosticSeverity.Ok,
                    S7CpuMode.Stop => DiagnosticSeverity.Critical,
                    S7CpuMode.Unknown => DiagnosticSeverity.Info,
                    _ => DiagnosticSeverity.Warning
                };
                var hint = mode == S7CpuMode.Stop ? " · o programa não está rodando: veja o buffer de diagnóstico abaixo" : string.Empty;
                items.Add(new("Estado da CPU", $"Modo de operação: {S7SystemClient.DescribeMode(mode)}{hint}", severity));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                items.Add(new("Estado da CPU", $"Não foi possível ler o modo de operação: {ex.Message}"));
            }

            try
            {
                var events = await system.ReadDiagnosticBufferAsync(10, ct).ConfigureAwait(false);
                if (events.Count == 0)
                    items.Add(new("Buffer de diagnóstico", "Nenhum evento retornado pela CPU."));
                foreach (var e in events)
                    items.Add(new("Buffer de diagnóstico", $"{S7SystemClient.FormatTime(e.Timestamp)} · {e.Description}", e.Severity));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                items.Add(new("Buffer de diagnóstico",
                    $"Não disponível por este caminho ({ex.Message}). Em S7-1200/1500 o buffer completo fica no TIA Portal ou no web server da CPU."));
            }
        }
    }

    private static string Explain(Exception ex)
    {
        var message = ex.GetBaseException().Message;
        if (message.Contains("Access", StringComparison.OrdinalIgnoreCase) || message.Contains("0x05", StringComparison.OrdinalIgnoreCase) || message.Contains("Object does not exist", StringComparison.OrdinalIgnoreCase))
            return $"{message}. No TIA Portal: habilite 'Permit access with PUT/GET', desmarque 'Optimized block access' no DB e confira se o DB/offset existe.";
        if (message.Contains("connect", StringComparison.OrdinalIgnoreCase) || message.Contains("TPKT", StringComparison.OrdinalIgnoreCase) || message.Contains("COTP", StringComparison.OrdinalIgnoreCase))
            return $"{message}. Confira IP, rack/slot (S7-300: 0/2 · S7-1200/1500: 0/1) e se a porta 102 está liberada.";
        return message;
    }
}

public static class S7Drivers
{
    public static DriverInfo Info { get; } = new()
    {
        Protocol = S7Driver.Protocol,
        Category = "Siemens",
        Description = "Siemens S7 (ISO-on-TCP) para S7-200/300/400/1200/1500.",
        Factory = () => new S7Driver(),
        DefaultPort = 102,
        AddressHelp = "DB1.DBW0 (INT), DB1.DBD4 (REAL), DB1.DBX0.0 (BOOL), MW10, M0.1, I0.0, QW2 · String S7: endereço DBB + tipo String + Tam. = tamanho máximo",
        AddressExample = "DB1.DBW0",
        DefaultDataType = FpDataType.Auto,
        Options = new[]
        {
            new DriverOption("Cpu", "CPU", OptionKind.Choice, "S7-1200", new[] { "S7-1200", "S7-1500", "S7-300", "S7-400", "S7-200" }),
            new DriverOption("Rack", "Rack", OptionKind.Number, "0"),
            new DriverOption("Slot", "Slot", OptionKind.Number, "1", Help: "S7-1200/1500: 1 · S7-300: 2 · S7-400: conforme o hardware."),
            new DriverOption("DbList", "DBs na árvore", OptionKind.Text, "1", Help: "Ex.: 1, 10, 100"),
            new DriverOption("AutoReconnect", "Reconexão automática", OptionKind.Bool, "true")
        }
    };
}
