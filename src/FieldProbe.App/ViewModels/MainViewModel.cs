using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using FieldProbe.App.Mvvm;
using FieldProbe.App.Services;
using FieldProbe.Core;
using FieldProbe.Drivers.Modbus;

namespace FieldProbe.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxLogEntries = 20000;

    private readonly ConcurrentQueue<LogEntry> _pendingLog = new();
    private readonly DispatcherTimer _logTimer;
    private readonly CsvRecorder _recorder = new();

    private object? _selectedTreeItem;
    private ConnectionViewModel? _selectedConnection;
    private WatchItemViewModel? _selectedWatchItem;
    private string _manualAddress = string.Empty;
    private DataType _manualDataType = DataType.Auto;
    private ByteOrder _manualByteOrder = ByteOrder.ABCD;
    private int _manualLength = 1;
    private int _pollIntervalMs = 1000;
    private string _writeValue = string.Empty;
    private string _logFilter = string.Empty;
    private bool _captureTraffic = true;
    private bool _onlyErrors;
    private bool _confirmWrites = true;
    private string _statusText = "Pronto";
    private string? _projectPath;
    private bool _isDirty;
    private DateTime? _lastScanTime;
    private string? _addressProtocol;
    private DateTime _lastRecordingRefresh;
    private IReadOnlyList<WatchItemViewModel> _trendItems = Array.Empty<WatchItemViewModel>();
    private int _trendWindowSeconds;
    private bool _trendPaused;
    private Views.ScannerWindow? _scannerWindow;

    public MainViewModel()
    {
        LogView = CollectionViewSource.GetDefaultView(Log);
        LogView.Filter = FilterLog;

        _logTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _logTimer.Tick += (_, _) =>
        {
            FlushLog();
            if (_recorder.IsRecording && (DateTime.Now - _lastRecordingRefresh).TotalSeconds >= 1)
            {
                _lastRecordingRefresh = DateTime.Now;
                OnPropertyChanged(nameof(RecordingText));
            }
        };
        _logTimer.Start();

        NewConnectionCommand = new RelayCommand(NewConnection);
        EditConnectionCommand = new RelayCommand(EditConnection, () => SelectedConnection is { IsOpen: false });
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, HandleError, () => SelectedConnection is { IsOpen: false, IsBusy: false });
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, HandleError, () => SelectedConnection is { IsOpen: true });
        ConnectAllCommand = new AsyncRelayCommand(ConnectAllAsync, HandleError, () => Connections.Any(c => !c.IsOpen));
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, HandleError, () => SelectedConnection is { IsOpen: true });
        DiagnosticsCommand = new AsyncRelayCommand(DiagnosticsAsync, HandleError, () => SelectedConnection is { IsOpen: true, SupportsDiagnostics: true });
        RemoveConnectionCommand = new AsyncRelayCommand(RemoveConnectionAsync, HandleError, () => SelectedConnection is not null);

        AddManualWatchCommand = new RelayCommand(AddManualWatch, () => TargetConnection is not null && ManualAddress.Trim().Length > 0);
        AddSelectedNodeCommand = new RelayCommand(AddSelectedNode, () => SelectedTreeItem is TagNodeViewModel { IsVariable: true });
        RemoveWatchCommand = new RelayCommand(RemoveWatch, () => SelectedWatchItem is not null);
        ClearWatchCommand = new RelayCommand(ClearWatch, () => WatchItems.Count > 0);
        WriteCommand = new AsyncRelayCommand(WriteAsync, HandleError, () => SelectedWatchItem is not null && WriteValue.Length > 0);
        ResetStatsCommand = new RelayCommand(ResetStats);
        ToggleRecordingCommand = new RelayCommand(ToggleRecording);
        ExportSnapshotCommand = new RelayCommand(ExportSnapshot, () => WatchItems.Count > 0);

        NewProjectCommand = new AsyncRelayCommand(NewProjectAsync, HandleError);
        OpenProjectCommand = new AsyncRelayCommand(OpenProjectAsync, HandleError);
        SaveProjectCommand = new RelayCommand(() => SaveProject(false));
        SaveProjectAsCommand = new RelayCommand(() => SaveProject(true));
        ExportLogCommand = new RelayCommand(ExportLog, () => Log.Count > 0);
        ClearLogCommand = new RelayCommand(() => Log.Clear());
        AboutCommand = new RelayCommand(DialogService.ShowAbout);

        EditWatchCommand = new RelayCommand(EditWatch, () => SelectedWatchItem is not null);
        DetectTypeCommand = new RelayCommand(DetectType, () => SelectedConnection is { IsOpen: true, Driver: IRegisterInspector } && ManualAddress.Trim().Length > 0);
        OpenScannerCommand = new RelayCommand(OpenScanner);
        AuditReportCommand = new AsyncRelayCommand(GenerateAuditAsync, HandleError);
        AcknowledgeAlarmsCommand = new RelayCommand(AcknowledgeAlarms, () => Alarms.Any(a => !a.Acknowledged));
        ClearAlarmsCommand = new RelayCommand(() => Alarms.Clear(), () => Alarms.Count > 0);
        Alarms.CollectionChanged += (_, _) => RaiseAlarmCounters(); // contador da aba sempre em dia

        AddLog("FieldProbe", "INFO", $"FieldProbe {AppInfo.Version} iniciado · protocolos: {string.Join(", ", DriverRegistry.Protocols)}");
    }

    // ===================== Coleções =====================

    public ObservableCollection<ConnectionViewModel> Connections { get; } = new();

    public ObservableCollection<WatchItemViewModel> WatchItems { get; } = new();

    public ObservableCollection<LogEntry> Log { get; } = new();

    public ICollectionView LogView { get; }

    public DataType[] DataTypes { get; } = Enum.GetValues<DataType>();

    public ByteOrder[] ByteOrders { get; } = Enum.GetValues<ByteOrder>();

    // ===================== Seleção =====================

    public object? SelectedTreeItem
    {
        get => _selectedTreeItem;
        set
        {
            if (!Set(ref _selectedTreeItem, value)) return;

            SelectedConnection = value switch
            {
                ConnectionViewModel c => c,
                TagNodeViewModel n => n.Connection,
                _ => SelectedConnection
            };

            if (value is TagNodeViewModel { Node.Address: { } address } node)
            {
                _addressProtocol = node.Connection.Protocol;
                ManualAddress = address.Address;
                ManualDataType = address.DataType;
                ManualLength = address.Length;
            }
        }
    }

    public ConnectionViewModel? SelectedConnection
    {
        get => _selectedConnection;
        private set
        {
            if (!Set(ref _selectedConnection, value)) return;
            OnPropertyChanged(nameof(AddressHelp));
            OnPropertyChanged(nameof(ByteOrderEnabled));
            OnPropertyChanged(nameof(CanDetectType));
            if (value is not null && value.Protocol != _addressProtocol)
            {
                ManualAddress = value.Info.AddressExample;
                ManualDataType = value.Info.DefaultDataType;
                ManualByteOrder = ByteOrder.ABCD;
                ManualLength = 1;
                _addressProtocol = value.Protocol;
            }
        }
    }

    public WatchItemViewModel? SelectedWatchItem
    {
        get => _selectedWatchItem;
        set
        {
            if (Set(ref _selectedWatchItem, value)) RefreshTrendItems();
        }
    }

    public string AddressHelp => SelectedConnection is null
        ? "Selecione ou crie uma conexão para adicionar variáveis."
        : $"{SelectedConnection.Protocol}: {SelectedConnection.Info.AddressHelp}";

    public bool ByteOrderEnabled => SelectedConnection?.Info.SupportsByteOrder == true;

    // ===================== Formulário de item =====================

    public string ManualAddress { get => _manualAddress; set => Set(ref _manualAddress, value ?? string.Empty); }
    public DataType ManualDataType { get => _manualDataType; set => Set(ref _manualDataType, value); }
    public ByteOrder ManualByteOrder { get => _manualByteOrder; set => Set(ref _manualByteOrder, value); }
    public int ManualLength { get => _manualLength; set => Set(ref _manualLength, Math.Clamp(value, 1, 250)); }
    public int PollIntervalMs { get => _pollIntervalMs; set => Set(ref _pollIntervalMs, Math.Clamp(value, 20, 3600000)); }
    public string WriteValue { get => _writeValue; set => Set(ref _writeValue, value ?? string.Empty); }

    // ===================== Log / opções =====================

    public string LogFilter
    {
        get => _logFilter;
        set
        {
            if (Set(ref _logFilter, value ?? string.Empty)) LogView.Refresh();
        }
    }

    public bool CaptureTraffic { get => _captureTraffic; set => Set(ref _captureTraffic, value); }

    public bool OnlyErrors
    {
        get => _onlyErrors;
        set
        {
            if (Set(ref _onlyErrors, value)) LogView.Refresh();
        }
    }

    public bool AutoScroll { get; set; } = true;

    public bool ConfirmWrites { get => _confirmWrites; set => Set(ref _confirmWrites, value); }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public bool IsRecording => _recorder.IsRecording;

    public string RecordingText => _recorder.IsRecording
        ? $"● Gravando {Path.GetFileName(_recorder.FilePath)} · {_recorder.Rows} linhas · {DateTime.Now - _recorder.StartedAt:hh\\:mm\\:ss}"
        : string.Empty;

    public string RecordButtonText => _recorder.IsRecording ? "Parar gravação" : "Gravar CSV";

    public string Title => $"FieldProbe · {(ProjectPath is null ? "Novo projeto" : Path.GetFileName(ProjectPath))}{(IsDirty ? " *" : string.Empty)}";

    public string Summary => $"{Connections.Count(c => c.IsOpen)}/{Connections.Count} conexões ativas · {WatchItems.Count} itens";

    private string? ProjectPath
    {
        get => _projectPath;
        set
        {
            _projectPath = value;
            OnPropertyChanged(nameof(Title));
        }
    }

    private bool IsDirty
    {
        get => _isDirty;
        set
        {
            _isDirty = value;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Summary));
        }
    }

    // ===================== Comandos =====================

    public ICommand NewConnectionCommand { get; }
    public ICommand EditConnectionCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ConnectAllCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand DiagnosticsCommand { get; }
    public ICommand RemoveConnectionCommand { get; }
    public ICommand AddManualWatchCommand { get; }
    public ICommand AddSelectedNodeCommand { get; }
    public ICommand RemoveWatchCommand { get; }
    public ICommand ClearWatchCommand { get; }
    public ICommand WriteCommand { get; }
    public ICommand ResetStatsCommand { get; }
    public ICommand ToggleRecordingCommand { get; }
    public ICommand ExportSnapshotCommand { get; }
    public ICommand NewProjectCommand { get; }
    public ICommand OpenProjectCommand { get; }
    public ICommand SaveProjectCommand { get; }
    public ICommand SaveProjectAsCommand { get; }
    public ICommand ExportLogCommand { get; }
    public ICommand ClearLogCommand { get; }
    public ICommand AboutCommand { get; }
    public ICommand EditWatchCommand { get; }
    public ICommand DetectTypeCommand { get; }
    public ICommand OpenScannerCommand { get; }
    public ICommand AuditReportCommand { get; }
    public ICommand AcknowledgeAlarmsCommand { get; }
    public ICommand ClearAlarmsCommand { get; }

    // ===================== Tendência =====================

    /// <summary>Item exibido no gráfico (o selecionado na Data Access View).</summary>
    public IReadOnlyList<WatchItemViewModel> TrendItems
    {
        get => _trendItems;
        private set => Set(ref _trendItems, value);
    }

    public IReadOnlyList<TrendWindowOption> TrendWindows { get; } = new[]
    {
        new TrendWindowOption("Tudo", 0),
        new TrendWindowOption("1 min", 60),
        new TrendWindowOption("5 min", 300),
        new TrendWindowOption("15 min", 900),
        new TrendWindowOption("1 h", 3600)
    };

    public int TrendWindowSeconds { get => _trendWindowSeconds; set => Set(ref _trendWindowSeconds, value); }
    public bool TrendPaused { get => _trendPaused; set => Set(ref _trendPaused, value); }

    private void RefreshTrendItems() =>
        TrendItems = SelectedWatchItem is null ? Array.Empty<WatchItemViewModel>() : new[] { SelectedWatchItem };

    // ===================== Alarmes =====================

    public ObservableCollection<AlarmEvent> Alarms { get; } = new();

    public int ActiveAlarmCount => WatchItems.Count(w => w.IsInAlarm);

    public string ActiveAlarmText => ActiveAlarmCount switch
    {
        0 => string.Empty,
        1 => "⚠ 1 alarme ativo",
        var n => $"⚠ {n} alarmes ativos"
    };

    public int UnacknowledgedCount => Alarms.Count(a => !a.Acknowledged);

    public string AlarmsTabHeader => UnacknowledgedCount > 0 ? $"Alarmes ({UnacknowledgedCount})" : "Alarmes";

    private void OnAlarmChanged(WatchItemViewModel item, AlarmState previous, AlarmState current)
    {
        // Saída de alarme por desconexão/parada não gera evento (não é normalização real).
        if (current == AlarmState.Normal && !item.IsRunning) return;

        var value = item.LastNumeric.HasValue
            ? $"{item.LastNumeric.Value.ToString("G6", System.Globalization.CultureInfo.CurrentCulture)} {item.Unit}".Trim()
            : item.ValueText;

        var description = current switch
        {
            AlarmState.High => $"ALARME ALTO (> {item.AlarmHigh?.ToString("G6")})",
            AlarmState.Low => $"ALARME BAIXO (< {item.AlarmLow?.ToString("G6")})",
            AlarmState.CommFailure => "FALHA DE COMUNICAÇÃO",
            _ => previous == AlarmState.CommFailure ? "Comunicação restabelecida" : "Retornou ao normal"
        };

        Alarms.Insert(0, new AlarmEvent(DateTime.Now, item.ConnectionName, item.Label, item.AddressText, current, description, value));
        while (Alarms.Count > 2000) Alarms.RemoveAt(Alarms.Count - 1);

        AddLog(item.ConnectionName, current == AlarmState.Normal ? "INFO" : "ALARME", $"{item.Label} ({item.AddressText}): {description} · valor {value}");

        RaiseAlarmCounters();
    }

    private void AcknowledgeAlarms()
    {
        foreach (var alarm in Alarms.Where(a => !a.Acknowledged)) alarm.Acknowledged = true;
        RaiseAlarmCounters();
    }

    private void RaiseAlarmCounters()
    {
        OnPropertyChanged(nameof(ActiveAlarmCount));
        OnPropertyChanged(nameof(ActiveAlarmText));
        OnPropertyChanged(nameof(UnacknowledgedCount));
        OnPropertyChanged(nameof(AlarmsTabHeader));
    }

    /// <summary>Resultados da última varredura de rede (usados no relatório de auditoria).</summary>
    public ObservableCollection<ScanResult> ScanResults { get; } = new();

    public bool CanDetectType => SelectedConnection?.Driver is IRegisterInspector;

    // ===================== Conexões =====================

    private bool NameInUse(string name) => Connections.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private void NewConnection()
    {
        var settings = DialogService.EditConnection(null, NameInUse, Connections.Count);
        if (settings is null) return;

        var connection = CreateConnection(settings);
        SelectedTreeItem = connection;
        ManualAddress = connection.Info.AddressExample;
        ManualDataType = connection.Info.DefaultDataType;
        IsDirty = true;
        AddLog(settings.Name, "INFO", $"Conexão criada: {settings.Protocol} · {connection.Endpoint}");
    }

    private void EditConnection()
    {
        var connection = SelectedConnection!;
        var settings = DialogService.EditConnection(connection.Settings.Clone(), NameInUse, Connections.Count);
        if (settings is null) return;

        if (!settings.Protocol.Equals(connection.Protocol, StringComparison.OrdinalIgnoreCase))
        {
            // Protocolo mudou: recria a conexão (o driver é outro); itens monitorados são descartados.
            foreach (var item in WatchItems.Where(w => w.Connection == connection).ToList()) RemoveWatchItem(item);
            Connections.Remove(connection);
            SelectedTreeItem = CreateConnection(settings);
        }
        else
        {
            connection.UpdateSettings(settings);
        }

        IsDirty = true;
        AddLog(settings.Name, "INFO", "Conexão editada.");
    }

    private ConnectionViewModel CreateConnection(ConnectionSettings settings)
    {
        var connection = new ConnectionViewModel(settings);

        connection.Driver.StatusChanged += (_, message) => AddLog(connection.Name, "STATUS", message);
        connection.Driver.FrameLogged += (_, frame) =>
        {
            if (!CaptureTraffic) return;
            var kind = frame.Direction switch
            {
                FrameDirection.Tx => "TX",
                FrameDirection.Rx => "RX",
                _ => "TRACE"
            };
            Enqueue(new LogEntry(frame.Timestamp, connection.Name, kind, frame.Description ?? string.Empty, frame.Hex));
        };

        connection.Opened += (_, _) =>
        {
            foreach (var item in WatchItems.Where(w => w.Connection == connection)) item.Start();
            OnPropertyChanged(nameof(Summary));
        };
        connection.Closing += (_, _) =>
        {
            foreach (var item in WatchItems.Where(w => w.Connection == connection)) item.Stop();
        };

        Connections.Add(connection);
        OnPropertyChanged(nameof(Summary));
        return connection;
    }

    private async Task ConnectAsync()
    {
        var connection = SelectedConnection!;
        StatusText = $"Conectando {connection.Name}...";
        await connection.ConnectAsync();
        StatusText = $"{connection.Name}: conectado";
        OnPropertyChanged(nameof(Summary));
    }

    private async Task ConnectAllAsync()
    {
        foreach (var connection in Connections.Where(c => !c.IsOpen).ToList())
        {
            try
            {
                await connection.ConnectAsync();
            }
            catch (Exception ex)
            {
                AddLog(connection.Name, "ERRO", $"Falha ao conectar: {ex.Message}");
            }
        }
        OnPropertyChanged(nameof(Summary));
    }

    private async Task DisconnectAsync()
    {
        var connection = SelectedConnection!;
        await connection.DisconnectAsync();
        StatusText = $"{connection.Name}: desconectado";
        OnPropertyChanged(nameof(Summary));
    }

    private async Task RefreshAsync()
    {
        var connection = SelectedConnection!;
        await connection.RefreshAsync();
        StatusText = $"{connection.Name}: árvore atualizada";
    }

    private async Task DiagnosticsAsync()
    {
        var connection = SelectedConnection!;
        if (connection.Driver is not IDiagnosticsProvider provider) return;

        StatusText = $"Diagnosticando {connection.Name}...";
        var items = await provider.RunDiagnosticsAsync();
        foreach (var item in items)
            AddLog(connection.Name, item.Severity is DiagnosticSeverity.Critical or DiagnosticSeverity.Warning ? "AVISO" : "DIAG", $"[{item.Category}] {item.Message}");

        DialogService.ShowDiagnostics($"Diagnóstico · {connection.Name} ({connection.Protocol})", items);
        StatusText = $"Diagnóstico de {connection.Name} concluído";
    }

    private async Task RemoveConnectionAsync()
    {
        var connection = SelectedConnection!;
        var items = WatchItems.Count(w => w.Connection == connection);
        if (!DialogService.Confirm($"Remover a conexão '{connection.Name}'{(items > 0 ? $" e seus {items} itens monitorados" : string.Empty)}?"))
            return;

        if (connection.IsOpen) await connection.DisconnectAsync();
        foreach (var item in WatchItems.Where(w => w.Connection == connection).ToList()) RemoveWatchItem(item);

        Connections.Remove(connection);
        SelectedTreeItem = null;
        SelectedConnection = null;
        IsDirty = true;
        AddLog(connection.Name, "INFO", "Conexão removida.");
    }

    // ===================== Watch list =====================

    /// <summary>Conexão que recebe o item: a selecionada ou, se houver só uma no projeto, ela.</summary>
    private ConnectionViewModel? TargetConnection =>
        SelectedConnection ?? (Connections.Count == 1 ? Connections[0] : null);

    private void AddManualWatch()
    {
        var connection = TargetConnection;
        if (connection is null) return;
        AddWatch(connection, new TagAddress(ManualAddress.Trim(), ManualDataType, ManualByteOrder, ManualLength), PollIntervalMs);
        StatusText = $"{ManualAddress.Trim()} adicionado em {connection.Name}{(connection.IsOpen ? string.Empty : " (conecte para começar a ler)")}";
    }

    private void AddSelectedNode()
    {
        if (SelectedTreeItem is not TagNodeViewModel { Node.Address: { } address } node) return;
        AddWatch(node.Connection, address with { ByteOrder = ManualByteOrder }, PollIntervalMs);
    }

    public WatchItemViewModel AddWatch(ConnectionViewModel connection, TagAddress address, int intervalMs)
    {
        var item = new WatchItemViewModel(connection, address, intervalMs);
        item.Sampled += (watch, value) =>
        {
            if (_recorder.IsRecording) _recorder.Write(watch.ConnectionName, watch.Label, value, watch.DisplayValue(value.Value), watch.Unit);
        };

        item.AlarmChanged += OnAlarmChanged;
        WatchItems.Add(item);
        connection.ItemCount = WatchItems.Count(w => w.Connection == connection);
        if (connection.IsOpen) item.Start();
        SelectedWatchItem = item;
        IsDirty = true;
        return item;
    }

    private void EditWatch()
    {
        var item = SelectedWatchItem!;
        if (!DialogService.EditWatchItem(item)) return;
        IsDirty = true;
        AddLog(item.ConnectionName, "INFO", $"Item editado: {item.Label} ({item.AddressText}, {item.DataType}{(item.HasScaling ? $", {item.ScalingText}" : string.Empty)}{(item.Unit.Length > 0 ? $", {item.Unit}" : string.Empty)})");
    }

    private void DetectType()
    {
        if (SelectedConnection?.Driver is not IRegisterInspector inspector) return;

        ModbusAddress address;
        try
        {
            address = ModbusAddress.Parse(ManualAddress);
        }
        catch (FormatException ex)
        {
            DialogService.Error($"{ex.Message}\n\nO detector de tipo funciona com registradores Modbus, por exemplo HR:0, IR:1 ou 40001.", "Detectar tipo");
            return;
        }
        if (address.IsBit)
        {
            DialogService.Error($"{address} é um bit (Coil/Discrete Input): não há tipo para detectar. Use um registrador HR ou IR.", "Detectar tipo");
            return;
        }

        var result = DialogService.DetectType(inspector, ManualAddress.Trim(), $"{SelectedConnection.Name} · {ManualAddress.Trim()}");
        if (result is null) return;
        ManualDataType = result.Type;
        ManualByteOrder = result.Order;
        if (result.Type == DataType.String) ManualLength = result.Length;
        StatusText = $"Tipo escolhido: {result.Type} {result.OrderText} = {result.Value}. Clique em Monitorar.";
    }

    private void RemoveWatch() => RemoveWatchItem(SelectedWatchItem!);

    private void RemoveWatchItem(WatchItemViewModel item)
    {
        item.Stop();
        item.AlarmChanged -= OnAlarmChanged;
        WatchItems.Remove(item);
        item.Connection.ItemCount = WatchItems.Count(w => w.Connection == item.Connection);
        RefreshTrendItems();
        RaiseAlarmCounters();
        IsDirty = true;
    }

    private void ClearWatch()
    {
        if (!DialogService.Confirm($"Remover todos os {WatchItems.Count} itens monitorados?")) return;
        foreach (var item in WatchItems.ToList()) RemoveWatchItem(item);
    }

    private async Task WriteAsync()
    {
        var item = SelectedWatchItem!;
        var engineering = WriteValue;
        var value = item.ToRawText(engineering);
        var scaledNote = value != engineering ? $"\n(valor bruto enviado ao device: {value})" : string.Empty;

        if (ConfirmWrites && !DialogService.Confirm(
                $"Escrever o valor\n\n    {engineering} {item.Unit}{scaledNote}\n\nem {item.Label} · {item.AddressText} ({item.ConnectionName})?\n\nConfirme que a escrita é segura para o processo/máquina.",
                "Confirmar escrita"))
            return;

        try
        {
            await item.Connection.Driver.WriteAsync(item.Address, value);
            AddLog(item.ConnectionName, "WRITE", $"{item.AddressText} ← {value} ({item.DataType}): OK");
            StatusText = $"Escrita OK em {item.AddressText}";
        }
        catch (Exception ex)
        {
            AddLog(item.ConnectionName, "ERRO", $"Escrita em {item.AddressText} falhou: {ex.Message}");
            StatusText = $"Escrita falhou: {ex.Message}";
        }
    }

    private void ResetStats()
    {
        foreach (var item in WatchItems) item.ResetStatistics();
        foreach (var connection in Connections) connection.Driver.Statistics.Reset();
        AddLog("FieldProbe", "INFO", "Estatísticas e histórico zerados.");
    }

    private void ToggleRecording()
    {
        if (_recorder.IsRecording)
        {
            var rows = _recorder.Rows;
            var path = _recorder.FilePath!;
            var duration = DateTime.Now - _recorder.StartedAt;
            _recorder.Stop();
            AddLog("FieldProbe", "INFO", $"Gravação encerrada: {rows} linhas em {path}");
            RaiseRecording();

            if (DialogService.Confirm($"Gravação concluída.\n\n{rows} leituras em {duration:hh\\:mm\\:ss}\n{path}\n\nAbrir o arquivo agora?", "Gravar CSV"))
                OpenFile(path);
            return;
        }

        if (WatchItems.Count == 0)
        {
            DialogService.Error("Adicione itens à Data Access View antes de gravar: a gravação registra cada leitura desses itens.", "Gravar CSV");
            return;
        }

        var file = DialogService.SaveFile("CSV (*.csv)|*.csv", $"fieldprobe_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        if (file is null) return;
        _recorder.Start(file);
        AddLog("FieldProbe", "INFO", $"Gravação iniciada em {file}. Cada leitura dos itens monitorados vira uma linha; clique em 'Parar gravação' para finalizar.");
        StatusText = "Gravando... clique em 'Parar gravação' para finalizar.";
        RaiseRecording();
    }

    private void RaiseRecording()
    {
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(RecordingText));
        OnPropertyChanged(nameof(RecordButtonText));
    }

    /// <summary>Exporta os valores atuais da Data Access View (uma linha por item).</summary>
    private void ExportSnapshot()
    {
        var path = DialogService.SaveFile("CSV (*.csv)|*.csv", $"fieldprobe_valores_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        if (path is null) return;

        var sb = new StringBuilder("conexao;nome;endereco;tipo;valor_bruto;valor;unidade;qualidade;timestamp;alarme;erro\n");
        foreach (var w in WatchItems)
        {
            sb.Append(CsvRecorder.Escape(w.ConnectionName)).Append(';')
              .Append(CsvRecorder.Escape(w.Label)).Append(';')
              .Append(CsvRecorder.Escape(w.AddressText)).Append(';')
              .Append(w.TypeName ?? w.DataType.ToString()).Append(';')
              .Append(CsvRecorder.Escape(w.RawValueText)).Append(';')
              .Append(CsvRecorder.Escape(w.Unit.Length > 0 && w.ValueText.EndsWith(" " + w.Unit, StringComparison.Ordinal) ? w.ValueText[..^(w.Unit.Length + 1)] : w.ValueText)).Append(';')
              .Append(CsvRecorder.Escape(w.Unit)).Append(';')
              .Append(w.Quality).Append(';')
              .Append(w.Timestamp?.ToString("yyyy-MM-dd HH:mm:ss.fff") ?? string.Empty).Append(';')
              .Append(CsvRecorder.Escape(w.AlarmText)).Append(';')
              .Append(CsvRecorder.Escape(w.Error ?? string.Empty)).Append('\n');
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        AddLog("FieldProbe", "INFO", $"Valores atuais exportados: {WatchItems.Count} itens em {path}");

        if (DialogService.Confirm($"{WatchItems.Count} itens exportados para\n{path}\n\nAbrir o arquivo agora?", "Exportar valores"))
            OpenFile(path);
    }

    private void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AddLog("FieldProbe", "AVISO", $"Não foi possível abrir {path}: {ex.Message}");
        }
    }

    // ===================== Scanner e auditoria =====================

    private void OpenScanner()
    {
        if (_scannerWindow is { IsLoaded: true })
        {
            _scannerWindow.Activate();
            return;
        }

        var vm = new ScannerViewModel(ScanResults, AddConnectionFromScan, () =>
        {
            _lastScanTime = DateTime.Now;
            AddLog("Scanner", "INFO", $"Varredura concluída: {ScanResults.Count} ativos.");
        });
        _scannerWindow = DialogService.ShowScanner(vm);
    }

    private void AddConnectionFromScan(ConnectionSettings settings)
    {
        var baseName = settings.Name;
        var name = baseName;
        for (var i = 2; NameInUse(name); i++) name = $"{baseName} ({i})";
        settings.Name = name;

        var connection = CreateConnection(settings);
        SelectedTreeItem = connection;
        IsDirty = true;
        AddLog(name, "INFO", $"Conexão criada pelo scanner: {settings.Protocol} · {connection.Endpoint}");
    }

    private async Task GenerateAuditAsync()
    {
        if (ScanResults.Count == 0 && Connections.Count == 0)
        {
            DialogService.Error("Nada para auditar ainda. Rode o Scanner de rede e/ou adicione conexões ao projeto.");
            return;
        }

        StatusText = "Executando diagnósticos para o relatório...";
        var audits = new List<ConnectionAudit>();
        foreach (var connection in Connections)
        {
            if (!connection.IsOpen)
            {
                audits.Add(new(connection.Name, connection.Protocol, connection.Endpoint, Array.Empty<DiagnosticItem>(),
                    "Conexão desconectada no momento da auditoria: conecte-a para incluir o diagnóstico."));
                continue;
            }
            if (connection.Driver is not IDiagnosticsProvider provider)
            {
                audits.Add(new(connection.Name, connection.Protocol, connection.Endpoint, Array.Empty<DiagnosticItem>(), "Protocolo sem diagnóstico disponível."));
                continue;
            }
            try
            {
                audits.Add(new(connection.Name, connection.Protocol, connection.Endpoint, await provider.RunDiagnosticsAsync(), null));
            }
            catch (Exception ex)
            {
                audits.Add(new(connection.Name, connection.Protocol, connection.Endpoint, Array.Empty<DiagnosticItem>(), $"Diagnóstico falhou: {ex.Message}"));
            }
        }

        var path = DialogService.SaveFile("Relatório HTML (*.html)|*.html", $"auditoria_ot_{DateTime.Now:yyyyMMdd_HHmm}.html");
        if (path is null)
        {
            StatusText = "Relatório cancelado.";
            return;
        }

        var projectName = ProjectPath is null ? "Projeto sem nome" : Path.GetFileNameWithoutExtension(ProjectPath);
        File.WriteAllText(path, AuditReport.Build(projectName, ScanResults.ToList(), _lastScanTime, audits), new UTF8Encoding(false));
        AddLog("FieldProbe", "INFO", $"Relatório de auditoria gerado: {path}");
        StatusText = "Relatório gerado. No navegador, use Ctrl+P → Salvar como PDF.";

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AddLog("FieldProbe", "AVISO", $"Não foi possível abrir o navegador: {ex.Message}");
        }
    }

    // ===================== Projeto =====================

    private async Task NewProjectAsync()
    {
        if (!ConfirmDiscard()) return;
        await ClearAllAsync();
        ProjectPath = null;
        IsDirty = false;
    }

    private async Task OpenProjectAsync()
    {
        if (!ConfirmDiscard()) return;
        var path = DialogService.OpenFile(ProjectFile.Filter);
        if (path is null) return;
        await LoadProjectAsync(path);
    }

    public async Task LoadProjectAsync(string path)
    {
        var project = ProjectFile.Load(path);
        await ClearAllAsync();

        foreach (var settings in project.Connections)
        {
            try
            {
                CreateConnection(settings);
            }
            catch (NotSupportedException ex)
            {
                AddLog("FieldProbe", "AVISO", ex.Message);
            }
        }

        foreach (var entry in project.WatchItems)
        {
            var connection = Connections.FirstOrDefault(c => c.Name == entry.ConnectionName);
            if (connection is null) continue;
            var item = AddWatch(connection, new TagAddress(entry.Address, entry.DataType, entry.ByteOrder, entry.Length), entry.IntervalMs);
            item.DisplayName = entry.DisplayName;
            item.Scale = entry.Scale == 0 ? 1 : entry.Scale;
            item.Offset = entry.Offset;
            item.Unit = entry.Unit;
            item.AlarmHigh = entry.AlarmHigh;
            item.AlarmLow = entry.AlarmLow;
            item.AlarmOnCommFailure = entry.AlarmOnCommFailure;
        }

        ProjectPath = path;
        IsDirty = false;
        AddLog("FieldProbe", "INFO", $"Projeto aberto: {Path.GetFileName(path)} · {Connections.Count} conexões, {WatchItems.Count} itens. Use 'Conectar todas'.");
    }

    private bool SaveProject(bool askPath)
    {
        var path = ProjectPath;
        if (askPath || path is null)
        {
            path = DialogService.SaveFile(ProjectFile.Filter, ProjectPath is null ? "projeto.fpx" : Path.GetFileName(ProjectPath));
            if (path is null) return false;
        }

        new ProjectFile
        {
            Connections = Connections.Select(c => c.Settings).ToList(),
            WatchItems = WatchItems.Select(w => new WatchEntry
            {
                ConnectionName = w.ConnectionName,
                Address = w.Address.Address,
                DataType = w.Address.DataType,
                ByteOrder = w.Address.ByteOrder,
                Length = w.Address.Length,
                IntervalMs = w.IntervalMs,
                DisplayName = w.DisplayName,
                Scale = w.Scale,
                Offset = w.Offset,
                Unit = w.Unit,
                AlarmHigh = w.AlarmHigh,
                AlarmLow = w.AlarmLow,
                AlarmOnCommFailure = w.AlarmOnCommFailure
            }).ToList()
        }.Save(path);

        ProjectPath = path;
        IsDirty = false;
        AddLog("FieldProbe", "INFO", $"Projeto salvo em {path}");
        return true;
    }

    private bool ConfirmDiscard()
    {
        if (!IsDirty) return true;
        return DialogService.AskSave("Salvar as alterações do projeto atual?") switch
        {
            MessageBoxResult.Yes => SaveProject(false),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    /// <summary>Chamado ao fechar a janela. Retorna falso se o usuário cancelar.</summary>
    public bool CanClose() => ConfirmDiscard();

    private async Task ClearAllAsync()
    {
        foreach (var item in WatchItems.ToList()) item.Stop();
        WatchItems.Clear();
        Alarms.Clear();
        RefreshTrendItems();
        RaiseAlarmCounters();
        foreach (var connection in Connections.Where(c => c.IsOpen).ToList())
            await connection.DisconnectAsync();
        Connections.Clear();
        SelectedTreeItem = null;
        SelectedConnection = null;
        OnPropertyChanged(nameof(Summary));
    }

    // ===================== Log =====================

    public void AddLog(string source, string kind, string message) =>
        Enqueue(new LogEntry(DateTime.Now, source, kind, message));

    private void Enqueue(LogEntry entry) => _pendingLog.Enqueue(entry);

    /// <summary>Descarrega o log em lote (evita travar a interface com tráfego intenso).</summary>
    private void FlushLog()
    {
        if (_pendingLog.IsEmpty) return;
        var count = 0;
        while (count < 2000 && _pendingLog.TryDequeue(out var entry))
        {
            Log.Add(entry);
            count++;
        }

        var excess = Log.Count - MaxLogEntries;
        for (var i = 0; i < excess; i++) Log.RemoveAt(0);

        LogFlushed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Disparado após cada lote (a janela usa para rolar até o fim).</summary>
    public event EventHandler? LogFlushed;

    private bool FilterLog(object obj)
    {
        if (obj is not LogEntry entry) return false;
        if (OnlyErrors && entry.Kind is not ("ERRO" or "AVISO")) return false;
        if (LogFilter.Length == 0) return true;
        return entry.Message.Contains(LogFilter, StringComparison.OrdinalIgnoreCase)
               || entry.Source.Contains(LogFilter, StringComparison.OrdinalIgnoreCase)
               || entry.Kind.Contains(LogFilter, StringComparison.OrdinalIgnoreCase)
               || entry.Hex.Contains(LogFilter, StringComparison.OrdinalIgnoreCase);
    }

    private void ExportLog()
    {
        var path = DialogService.SaveFile("CSV (*.csv)|*.csv|Texto (*.txt)|*.txt", $"fieldprobe_log_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        if (path is null) return;

        var sb = new StringBuilder("hora;origem;tipo;mensagem;hex\n");
        foreach (var entry in Log)
        {
            sb.Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(';')
              .Append(CsvRecorder.Escape(entry.Source)).Append(';')
              .Append(entry.Kind).Append(';')
              .Append(CsvRecorder.Escape(entry.Message)).Append(';')
              .Append(entry.Hex).Append('\n');
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        StatusText = $"Log exportado: {path}";
    }

    private void HandleError(Exception ex)
    {
        AddLog("FieldProbe", "ERRO", ex.Message);
        StatusText = "Erro: " + ex.Message;
    }

    public void Dispose()
    {
        _logTimer.Stop();
        _scannerWindow?.Close();
        foreach (var item in WatchItems) item.Stop();
        foreach (var connection in Connections)
        {
            try
            {
                connection.Driver.DisconnectAsync().Wait(1500);
            }
            catch
            {
                // encerrando
            }
        }
        _recorder.Dispose();
    }
}

public sealed record TrendWindowOption(string Label, int Seconds);
