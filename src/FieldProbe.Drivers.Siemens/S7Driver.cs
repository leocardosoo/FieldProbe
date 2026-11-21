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
public sealed class S7Driver : DriverBase, IDiagnosticsProvider, IAddressScanner, IRemovableFolders
{
    public const string Protocol = "Siemens S7";

    private Plc? _plc;
    private S7SystemClient? _health;
    private bool _healthUnsupported;

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

    protected override async Task CloseAsync()
    {
        _plc?.Close();
        _plc = null;
        var health = _health;
        _health = null;
        _healthUnsupported = false;
        if (health is not null) await health.DisposeAsync().ConfigureAwait(false);
    }

    protected override bool SupportsHealthCheck => !_healthUnsupported;

    /// <summary>Consulta o modo da CPU (RUN/STOP) por uma conexão de diagnóstico mantida aberta.</summary>
    protected override async Task<string?> CheckDeviceHealthAsync(CancellationToken ct)
    {
        var s = Settings;
        if (s is null) return null;
        try
        {
            _health ??= await S7SystemClient.ConnectAsync(s.Host, s.Port > 0 ? s.Port : 102,
                s.GetIntOption("Rack", 0), s.GetIntOption("Slot", 1), s.TimeoutMs, ct).ConfigureAwait(false);

            return await _health.ReadCpuModeAsync(ct).ConfigureAwait(false) switch
            {
                S7CpuMode.Stop => "CPU em STOP: o programa não está rodando (valores congelados)",
                S7CpuMode.Startup => "CPU em STARTUP (partida): valores ainda não atualizados",
                S7CpuMode.Hold => "CPU em HOLD: programa pausado",
                _ => null
            };
        }
        catch (S7SzlException)
        {
            _healthUnsupported = true; // esta CPU não informa o modo por SZL: para de consultar
            return null;
        }
        catch
        {
            var broken = _health;
            _health = null;
            if (broken is not null) await broken.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public override Task<IReadOnlyList<TagNode>> BrowseAsync(TagNode? parent = null, CancellationToken ct = default)
    {
        if (parent is not null)
            return Task.FromResult<IReadOnlyList<TagNode>>(parent.Children);

        // S7 "clássico" não expõe símbolos: árvore sugerida com os DBs configurados.
        var dbs = (Settings?.GetOption("DbList", string.Empty) ?? string.Empty)
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => int.TryParse(x, out var n) ? n : -1)
            .Where(n => n > 0)
            .Distinct()
            .ToList();

        // Resultado de "Procurar DBs": tamanho de cada DB e DBs que existem mas recusam leitura
        var sizes = ParseSizes(Settings?.GetOption("DbSizes", string.Empty) ?? string.Empty);
        var locked = (Settings?.GetOption("DbLocked", string.Empty) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var n) ? n : -1)
            .ToHashSet();

        var custom = CustomTags.Load(Settings);
        var roots = new List<TagNode>();
        foreach (var db in dbs)
        {
            if (locked.Contains(db))
            {
                roots.Add(new TagNode($"DB{db} (sem acesso: DB otimizado ou protegido)")
                {
                    Key = $"DB{db}",
                    Description = "No TIA Portal, desmarque 'Optimized block access' nas propriedades do DB"
                });
                continue;
            }

            // O CLP não informa as variáveis do DB: o usuário cria as dele (botão "+") com endereço e tipo do TIA Portal.
            var folder = new TagNode($"DB{db}")
            {
                Subtitle = sizes.TryGetValue(db, out var size) ? $"{size} bytes" : null,
                CustomFolder = $"DB{db}",
                AddressHint = $"DB{db}.DBD0",
                Description = (sizes.ContainsKey(db)
                        ? $"DB{db} existe no CLP e tem {size} bytes."
                        : $"DB{db} configurado na conexão (use 'Procurar endereços...' para confirmar se existe).") +
                    "\nO CLP não informa as variáveis do DB: clique em + para adicionar com o endereço e o tipo do projeto do TIA Portal."
            };
            CustomTags.AddTo(folder, custom);
            roots.Add(folder);
        }

        TagNode Area(string name, string key, string hint, string description)
        {
            var area = new TagNode(name) { CustomFolder = key, AddressHint = hint, Description = description + "\nClique em + para adicionar variáveis." };
            CustomTags.AddTo(area, custom);
            return area;
        }

        roots.Add(Area("Marcadores (M)", "M", "M0.0", "Memória interna do programa (M0.0, MW10, MD20...)."));
        roots.Add(Area("Entradas (I)", "I", "I0.0", "Entradas físicas (I0.0, IW64...). Escrever em I não adianta: o CLP sobrescreve a cada ciclo."));
        roots.Add(Area("Saídas (Q)", "Q", "Q0.0", "Saídas físicas (Q0.0, QW64...). Se o programa também escreve na saída, o CLP sobrescreve a cada ciclo."));

        return Task.FromResult<IReadOnlyList<TagNode>>(roots);
    }


    private static Dictionary<int, int> ParseSizes(string text)
    {
        var sizes = new Dictionary<int, int>();
        foreach (var pair in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out var db) && int.TryParse(parts[1], out var size))
                sizes[db] = size;
        }
        return sizes;
    }

    // ===================== Remover DB da árvore =====================

    public bool CanRemoveFolder(string key) =>
        key.StartsWith("DB", StringComparison.OrdinalIgnoreCase) && int.TryParse(key[2..], out _);

    public void RemoveFolder(ConnectionSettings settings, string key)
    {
        if (!CanRemoveFolder(key)) return;
        var db = int.Parse(key[2..]);

        static string Without(string text, int db, bool pairs) => string.Join(pairs ? "," : ", ", text
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !int.TryParse(pairs ? x.Split(':')[0] : x, out var n) || n != db));

        settings.Options["DbList"] = Without(settings.GetOption("DbList", string.Empty), db, false);
        settings.Options["DbSizes"] = Without(settings.GetOption("DbSizes", string.Empty), db, true);
        settings.Options["DbLocked"] = Without(settings.GetOption("DbLocked", string.Empty), db, false);
        CustomTags.Save(settings, CustomTags.Load(settings).Where(t => !string.Equals(t.Folder, key, StringComparison.OrdinalIgnoreCase)));
    }

    // ===================== Procurar DBs =====================

    public string AddressScanTitle => "Procurar DBs";

    public string AddressScanHelp =>
        "Procura quais DBs existem no CLP e o tamanho de cada um. Faixa de números de DB, ex.: 1-1000 ou 1-200, 500-600. " +
        "As leituras são de 1 byte, em lote (até 18 DBs por pedido): nada é escrito e o CLP quase não sente. " +
        "O tipo de cada posição continua vindo do projeto do TIA Portal.";

    public string DefaultScanRange => "1-1000";

    public async Task ScanAddressesAsync(string range, IProgress<double>? progress, Action<AddressBlock> onFound, CancellationToken ct)
    {
        var s = Settings ?? throw new InvalidOperationException("Conecte primeiro.");
        var numbers = AddressRangeParser.Parse(range, 1, 65535, 10000)
            .SelectMany(r => Enumerable.Range(r.Start, r.Count))
            .Distinct()
            .ToList();

        OnTrace($"Procurando DBs: {numbers.Count} números ({range})", FrameDirection.Tx);
        await using var system = await S7SystemClient.ConnectAsync(s.Host, s.Port > 0 ? s.Port : 102,
            s.GetIntOption("Rack", 0), s.GetIntOption("Slot", 1), s.TimeoutMs, ct).ConfigureAwait(false);

        // 1ª etapa: quais DBs existem (1 byte do início de cada um, em lote)
        var readable = new List<int>();
        var denied = new List<int>();
        var empty = new List<int>();
        for (var i = 0; i < numbers.Count; i += S7SystemClient.MaxProbeItems)
        {
            var chunk = numbers.Skip(i).Take(S7SystemClient.MaxProbeItems).ToList();
            var codes = await system.ProbeDbBytesAsync(chunk.Select(db => (db, 0)).ToList(), ct).ConfigureAwait(false);
            for (var k = 0; k < chunk.Count; k++)
            {
                switch (codes[k])
                {
                    case S7SystemClient.ReturnOk: readable.Add(chunk[k]); break;
                    case S7SystemClient.ReturnAccessDenied: denied.Add(chunk[k]); break;
                    case S7SystemClient.ReturnOutOfRange: empty.Add(chunk[k]); break;
                }
            }
            progress?.Report(0.6 * Math.Min(numbers.Count, i + chunk.Count) / numbers.Count);
        }

        if (readable.Count == 0 && empty.Count == 0 && denied.Count == numbers.Count && numbers.Count >= 5)
            throw new InvalidOperationException(
                "O CLP recusou a leitura de todos os DBs: o acesso PUT/GET parece desabilitado. No TIA Portal: Propriedades da CPU → Proteção → 'Permit access with PUT/GET'.");

        foreach (var db in denied)
            onFound(new AddressBlock("DB", db, 0, $"DB{db}", "existe, mas recusa leitura: DB otimizado ou protegido", false));
        foreach (var db in empty)
            onFound(new AddressBlock("DB", db, 0, $"DB{db}", "existe, mas não permite leitura por offset (vazio ou otimizado)", false));

        // 2ª etapa: tamanho de cada DB legível (busca binária, todos os DBs juntos em cada rodada)
        var low = readable.ToDictionary(db => db, _ => 0);        // último offset que leu
        var high = readable.ToDictionary(db => db, _ => 65536);   // primeiro offset que falhou
        var rounds = 0;
        while (true)
        {
            var active = readable.Where(db => high[db] - low[db] > 1).ToList();
            if (active.Count == 0) break;
            for (var i = 0; i < active.Count; i += S7SystemClient.MaxProbeItems)
            {
                var chunk = active.Skip(i).Take(S7SystemClient.MaxProbeItems).ToList();
                var probes = chunk.Select(db => (db, (low[db] + high[db]) / 2)).ToList();
                var codes = await system.ProbeDbBytesAsync(probes, ct).ConfigureAwait(false);
                for (var k = 0; k < chunk.Count; k++)
                {
                    if (codes[k] == S7SystemClient.ReturnOk) low[chunk[k]] = probes[k].Item2;
                    else high[chunk[k]] = probes[k].Item2;
                }
            }
            rounds++;
            progress?.Report(0.6 + 0.4 * Math.Min(1.0, rounds / 17.0));
        }

        foreach (var db in readable)
            onFound(new AddressBlock("DB", db, low[db] + 1, $"DB{db}", $"{low[db] + 1} bytes", true));

        progress?.Report(1.0);
        OnTrace($"Busca concluída: {readable.Count} DB(s) legíveis, {denied.Count + empty.Count} sem acesso", FrameDirection.Rx);
    }

    public void ApplyScanResults(ConnectionSettings settings, IReadOnlyList<AddressBlock> selected)
    {
        var dbs = selected.Where(b => b.Area == "DB").OrderBy(b => b.Start).ToList();
        settings.Options["DbList"] = string.Join(", ", dbs.Select(b => b.Start));
        settings.Options["DbSizes"] = string.Join(",", dbs.Where(b => b.Accessible).Select(b => $"{b.Start}:{b.Count}"));
        settings.Options["DbLocked"] = string.Join(",", dbs.Where(b => !b.Accessible).Select(b => b.Start));
    }

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

        var firstDb = (s?.GetOption("DbList", string.Empty) ?? string.Empty).Split(',')[0].Trim();
        if (!int.TryParse(firstDb, out _)) firstDb = "1";
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
        if (message.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
            return $"O DB (ou área) não existe no CLP: confira o número do DB no projeto do TIA Portal. ({message})";
        if (message.Contains("out of range", StringComparison.OrdinalIgnoreCase))
            return $"Endereço além do tamanho do DB/área: o DB é menor que esse offset; confira o offset no TIA Portal. ({message})";
        if (message.Contains("not allowed", StringComparison.OrdinalIgnoreCase) || message.Contains("Access", StringComparison.OrdinalIgnoreCase))
            return $"Acesso negado pelo CLP: no TIA Portal habilite 'Permit access with PUT/GET' e desmarque 'Optimized block access' no DB. ({message})";
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
            new DriverOption("DbList", "DBs na árvore", OptionKind.Text, "", Help: "Opcional. Preenchido pelo 'Procurar endereços...' ou ao adicionar uma variável. Ex.: 1, 10, 100"),
            new DriverOption("AutoReconnect", "Reconexão automática", OptionKind.Bool, "true")
        }
    };
}
