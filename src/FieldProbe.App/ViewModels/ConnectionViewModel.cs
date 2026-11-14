using System.Collections.ObjectModel;
using System.Windows;
using FieldProbe.App.Mvvm;
using FieldProbe.Core;

namespace FieldProbe.App.ViewModels;

/// <summary>Uma conexão do projeto (um device) e sua árvore de navegação.</summary>
public sealed class ConnectionViewModel : ObservableObject
{
    private bool _isOpen;
    private bool _isBusy;
    private bool _isExpanded = true;
    private bool _isLinkUp;
    private string _status = "Desconectado";

    public ConnectionViewModel(ConnectionSettings settings)
    {
        Settings = settings;
        Info = DriverRegistry.Get(settings.Protocol);
        Driver = Info.Factory();
        Driver.StatusChanged += (_, message) => Ui(() =>
        {
            Status = message;
            IsLinkUp = Driver.IsConnected;
        });
    }

    public ConnectionSettings Settings { get; private set; }

    public DriverInfo Info { get; }

    public IProtocolDriver Driver { get; }

    public string Name => Settings.Name;

    public string Protocol => Settings.Protocol;

    public string Endpoint => Info.UsesPort ? $"{Settings.Host}:{Settings.Port}" : Settings.Host;

    public bool SupportsDiagnostics => Driver is IDiagnosticsProvider;

    public ObservableCollection<TagNodeViewModel> Nodes { get; } = new();

    /// <summary>Sessão aberta pelo usuário (pode estar momentaneamente caída e reconectando).</summary>
    public bool IsOpen
    {
        get => _isOpen;
        private set
        {
            if (Set(ref _isOpen, value)) RaiseState();
        }
    }

    /// <summary>Link de comunicação ativo neste instante.</summary>
    public bool IsLinkUp
    {
        get => _isLinkUp;
        private set
        {
            if (Set(ref _isLinkUp, value)) RaiseState();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) RaiseState();
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    public string Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value)) RaiseState();
        }
    }

    private int _itemCount;
    private bool _lastAttemptFailed;

    /// <summary>Nome curto do protocolo para o selo colorido da árvore.</summary>
    public string ProtocolBadge => Settings.Protocol switch
    {
        var p when p.StartsWith("Modbus RTU", StringComparison.OrdinalIgnoreCase) => "RTU",
        var p when p.StartsWith("Modbus", StringComparison.OrdinalIgnoreCase) => "MODBUS",
        var p when p.StartsWith("OPC", StringComparison.OrdinalIgnoreCase) => "OPC UA",
        var p when p.StartsWith("MQTT", StringComparison.OrdinalIgnoreCase) => "MQTT",
        var p when p.StartsWith("Siemens", StringComparison.OrdinalIgnoreCase) => "S7",
        var p when p.StartsWith("EtherNet", StringComparison.OrdinalIgnoreCase) => "EIP",
        var p => p.Length > 6 ? p[..6].ToUpperInvariant() : p.ToUpperInvariant()
    };

    /// <summary>Família do protocolo (usada nas cores): Modbus, OpcUa, Mqtt, S7, Eip.</summary>
    public string ProtocolFamily => ProtocolBadge switch
    {
        "MODBUS" or "RTU" => "Modbus",
        "OPC UA" => "OpcUa",
        "MQTT" => "Mqtt",
        "S7" => "S7",
        "EIP" => "Eip",
        _ => "Other"
    };

    /// <summary>Estado resumido: Connected, Connecting, Reconnecting, Failed, Disconnected.</summary>
    public string StateKind =>
        IsBusy ? "Connecting"
        : IsOpen && IsLinkUp ? "Connected"
        : IsOpen ? "Reconnecting"
        : _lastAttemptFailed ? "Failed"
        : "Disconnected";

    public string StateText => StateKind switch
    {
        "Connecting" => "Conectando...",
        "Connected" => "Conectado",
        "Reconnecting" => "Reconectando...",
        "Failed" => "Falha",
        _ => "Desconectado"
    };

    /// <summary>Mensagem detalhada só quando há algo a explicar (falha ou reconexão).</summary>
    public bool ShowDetail => StateKind is "Failed" or "Reconnecting";

    public int ItemCount
    {
        get => _itemCount;
        set
        {
            if (Set(ref _itemCount, value)) OnPropertyChanged(nameof(Subtitle));
        }
    }

    public string Subtitle => ItemCount switch
    {
        0 => Endpoint,
        1 => $"{Endpoint} · 1 item",
        var n => $"{Endpoint} · {n} itens"
    };

    private void RaiseState()
    {
        OnPropertyChanged(nameof(StateKind));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(ShowDetail));
    }

    public event EventHandler? Opened;

    public event EventHandler? Closing;

    public void UpdateSettings(ConnectionSettings settings)
    {
        if (IsOpen) throw new InvalidOperationException("Desconecte antes de editar a conexão.");
        Settings = settings;
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Endpoint));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(ProtocolBadge));
        OnPropertyChanged(nameof(ProtocolFamily));
    }

    public async Task ConnectAsync()
    {
        IsBusy = true;
        _lastAttemptFailed = false;
        try
        {
            await Driver.ConnectAsync(Settings);
            IsOpen = true;
            IsLinkUp = true;
            try
            {
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                Nodes.Clear();
                Nodes.Add(TagNodeViewModel.Message(this, $"Navegação indisponível: {ex.Message}"));
            }
            Opened?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _lastAttemptFailed = true;
            Status = $"Falha: {ex.Message}";
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DisconnectAsync()
    {
        Closing?.Invoke(this, EventArgs.Empty);
        await Driver.DisconnectAsync();
        _lastAttemptFailed = false;
        IsOpen = false;
        IsLinkUp = false;
        Nodes.Clear();
    }

    public async Task RefreshAsync()
    {
        var roots = await Driver.BrowseAsync();
        Nodes.Clear();
        foreach (var node in roots)
            Nodes.Add(new TagNodeViewModel(node, this));
        IsExpanded = true;
    }

    private static void Ui(Action action) => Application.Current?.Dispatcher.InvokeAsync(action);
}
