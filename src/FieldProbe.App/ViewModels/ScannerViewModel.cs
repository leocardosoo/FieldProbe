using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Input;
using FieldProbe.App.Mvvm;
using FieldProbe.App.Services;
using FieldProbe.Core;

namespace FieldProbe.App.ViewModels;

public sealed class PortOption : ObservableObject
{
    private bool _isSelected = true;

    public PortOption(PortDefinition definition)
    {
        Definition = definition;
    }

    public PortDefinition Definition { get; }

    public string Label => $"{Definition.Port} · {Definition.Name}";

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>Janela do scanner de rede OT.</summary>
public sealed class ScannerViewModel : ObservableObject
{
    private readonly Action<ConnectionSettings> _createConnection;
    private readonly Action _completed;
    private CancellationTokenSource? _cts;
    private string _rangeText;
    private int _timeoutMs = 400;
    private int _parallelism = 32;
    private double _progress;
    private string _statusText = "Informe a faixa e clique em Iniciar varredura.";
    private bool _isRunning;
    private ScanResult? _selectedResult;

    public ScannerViewModel(ObservableCollection<ScanResult> results, Action<ConnectionSettings> createConnection, Action completed)
    {
        Results = results;
        _createConnection = createConnection;
        _completed = completed;
        _rangeText = IpRangeParser.SuggestLocalRange();
        Ports = NetworkScanner.KnownPorts.Select(p => new PortOption(p)).ToList();

        StartCommand = new AsyncRelayCommand(ScanAsync, ex => StatusText = $"Erro: {ex.Message}", () => !IsRunning);
        StopCommand = new RelayCommand(() => _cts?.Cancel(), () => IsRunning);
        CreateConnectionCommand = new RelayCommand(CreateConnection, () => SelectedResult?.SuggestedProtocol is not null);
        ExportCommand = new RelayCommand(Export, () => Results.Count > 0 && !IsRunning);
        SelectIndustrialCommand = new RelayCommand(() =>
        {
            foreach (var p in Ports) p.IsSelected = p.Definition.Industrial;
        });
        SelectAllCommand = new RelayCommand(() =>
        {
            foreach (var p in Ports) p.IsSelected = true;
        });
    }

    public ObservableCollection<ScanResult> Results { get; }

    public IReadOnlyList<PortOption> Ports { get; }

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand CreateConnectionCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand SelectIndustrialCommand { get; }
    public ICommand SelectAllCommand { get; }

    public string RangeText { get => _rangeText; set => Set(ref _rangeText, value ?? string.Empty); }
    public int TimeoutMs { get => _timeoutMs; set => Set(ref _timeoutMs, Math.Clamp(value, 50, 10000)); }
    public int Parallelism { get => _parallelism; set => Set(ref _parallelism, Math.Clamp(value, 1, 256)); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (Set(ref _isRunning, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    public ScanResult? SelectedResult
    {
        get => _selectedResult;
        set => Set(ref _selectedResult, value);
    }

    public void Cancel() => _cts?.Cancel();

    private async Task ScanAsync()
    {
        List<IPAddress> hosts;
        try
        {
            hosts = IpRangeParser.Parse(RangeText);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            StatusText = $"Faixa inválida: {ex.Message} Ex.: 192.168.1.0/24, 192.168.1.10-50 ou 10.0.0.5";
            return;
        }

        var ports = Ports.Where(p => p.IsSelected).Select(p => p.Definition.Port).ToList();
        if (hosts.Count == 0 || ports.Count == 0)
        {
            StatusText = "Informe ao menos um IP e uma porta.";
            return;
        }

        Results.Clear();
        SelectedResult = null;
        Progress = 0;
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        StatusText = $"Varrendo {hosts.Count} hosts × {ports.Count} portas...";

        var progress = new Progress<ScanProgress>(p =>
        {
            Progress = 100.0 * p.Done / Math.Max(1, p.Total);
            StatusText = $"{p.Done}/{p.Total} hosts · {p.Found} ativos encontrados · último: {p.Current}";
        });

        try
        {
            // Roda em segundo plano: a interface continua respondendo e os resultados aparecem conforme chegam.
            var token = _cts.Token;
            var timeout = TimeoutMs;
            var parallelism = Parallelism;
            var dispatcher = Application.Current.Dispatcher;
            await Task.Run(() => new NetworkScanner().ScanAsync(hosts, ports, timeout, parallelism, progress,
                result => dispatcher.InvokeAsync(() => Results.Add(result)), token));
        }
        finally
        {
            IsRunning = false;
        }

        var interrupted = _cts.IsCancellationRequested;
        var critical = Results.Sum(r => r.Findings.Count(f => f.Severity == DiagnosticSeverity.Critical));
        var warning = Results.Sum(r => r.Findings.Count(f => f.Severity == DiagnosticSeverity.Warning));
        StatusText = $"{(interrupted ? "Interrompido" : "Concluído")} em {sw.Elapsed.TotalSeconds:F1} s · {Results.Count} ativos · {critical} críticos · {warning} pontos de atenção. Use 'Relatório de auditoria' para gerar o documento.";
        Progress = interrupted ? Progress : 100;
        _completed();
    }

    private void CreateConnection()
    {
        var result = SelectedResult!;
        var info = DriverRegistry.Get(result.SuggestedProtocol!);
        var settings = new ConnectionSettings
        {
            Name = result.ShortName,
            Protocol = info.Protocol,
            Host = info.UsesPort ? result.Ip : $"opc.tcp://{result.Ip}:{result.SuggestedPort}",
            Port = info.UsesPort ? result.SuggestedPort : 0,
            TimeoutMs = 3000,
            Options = info.Options.ToDictionary(o => o.Key, o => o.DefaultValue)
        };
        _createConnection(settings);
        StatusText = $"Conexão {info.Protocol} criada para {result.Ip}. Conecte-se pela janela principal.";
    }

    private void Export()
    {
        var path = DialogService.SaveFile("CSV (*.csv)|*.csv", $"scanner_ot_{DateTime.Now:yyyyMMdd_HHmm}.csv");
        if (path is null) return;

        var sb = new StringBuilder("ip;hostname;mac;portas;protocolos;identificacao;severidade;achados\n");
        foreach (var r in Results.OrderBy(r => r.Ip, StringComparer.Ordinal))
        {
            sb.Append(r.Ip).Append(';')
              .Append(CsvRecorder.Escape(r.Hostname)).Append(';')
              .Append(r.Mac).Append(';')
              .Append(CsvRecorder.Escape(r.PortsText)).Append(';')
              .Append(CsvRecorder.Escape(r.ProtocolsText)).Append(';')
              .Append(CsvRecorder.Escape(r.IdentityText)).Append(';')
              .Append(r.SeverityText).Append(';')
              .Append(CsvRecorder.Escape(string.Join(" | ", r.Findings.Select(f => $"[{f.SeverityText}] {f.Message}"))))
              .Append('\n');
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        StatusText = $"Exportado: {path}";
    }
}
