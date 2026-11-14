using System.Collections.ObjectModel;
using System.Windows.Input;
using FieldProbe.App.Mvvm;
using FieldProbe.App.Services;
using FieldProbe.Core;
using FieldProbe.Drivers.Modbus;

namespace FieldProbe.App.ViewModels;

/// <summary>Um campo do formulário de conexão gerado a partir de <see cref="DriverOption"/>.</summary>
public sealed class OptionFieldViewModel : ObservableObject
{
    private string _value;

    public OptionFieldViewModel(DriverOption option, string? value)
    {
        Option = option;
        _value = value ?? option.DefaultValue;
    }

    public DriverOption Option { get; }
    public string Key => Option.Key;
    public string Label => Option.Label;
    public string? Help => Option.Help;
    public IReadOnlyList<string> Choices => Option.Choices ?? Array.Empty<string>();

    public bool IsText => Option.Kind is OptionKind.Text or OptionKind.Number;
    public bool IsPassword => Option.Kind == OptionKind.Password;
    public bool IsChoice => Option.Kind == OptionKind.Choice;
    public bool IsBool => Option.Kind == OptionKind.Bool;

    public string Value
    {
        get => _value;
        set => Set(ref _value, value ?? string.Empty);
    }

    public bool BoolValue
    {
        get => Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        set
        {
            Value = value ? "true" : "false";
            OnPropertyChanged();
        }
    }
}

/// <summary>ViewModel do diálogo "Nova conexão / Editar conexão".</summary>
public sealed class ConnectionEditorViewModel : ObservableObject
{
    private readonly Func<string, bool> _nameInUse;
    private readonly ConnectionSettings? _original;
    private DriverInfo _selectedDriver;
    private string _name;
    private string _host;
    private int _port;
    private int _timeoutMs;
    private string _testResult = string.Empty;
    private string _error = string.Empty;

    public ConnectionEditorViewModel(ConnectionSettings? existing, Func<string, bool> nameInUse, int connectionCount)
    {
        _original = existing;
        _nameInUse = nameInUse;
        Drivers = DriverRegistry.All;

        _selectedDriver = existing is null ? Drivers[0] : DriverRegistry.Find(existing.Protocol) ?? Drivers[0];
        _name = existing?.Name ?? $"Device {connectionCount + 1}";
        _host = existing?.Host ?? _selectedDriver.DefaultHost;
        _port = existing?.Port ?? _selectedDriver.DefaultPort;
        _timeoutMs = existing?.TimeoutMs ?? 3000;
        BuildOptions(existing?.Options);

        TestCommand = new AsyncRelayCommand(TestAsync, ex => TestResult = ex.Message, () => !IsSerial && Host.Trim().Length > 0);
    }

    public string Title => _original is null ? "Nova conexão" : $"Editar conexão · {_original.Name}";

    public IReadOnlyList<DriverInfo> Drivers { get; }

    public ObservableCollection<OptionFieldViewModel> Options { get; } = new();

    public ObservableCollection<string> HostSuggestions { get; } = new();

    public ICommand TestCommand { get; }

    public DriverInfo SelectedDriver
    {
        get => _selectedDriver;
        set
        {
            if (value is null || !Set(ref _selectedDriver, value)) return;
            Host = value.DefaultHost;
            Port = value.DefaultPort;
            BuildOptions(null);
            OnPropertyChanged(nameof(HostLabel));
            OnPropertyChanged(nameof(UsesPort));
            OnPropertyChanged(nameof(IsSerial));
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(AddressHelp));
            TestResult = string.Empty;
        }
    }

    public string Description => SelectedDriver.Description;
    public string AddressHelp => "Endereços: " + SelectedDriver.AddressHelp;
    public string HostLabel => SelectedDriver.HostLabel;
    public bool UsesPort => SelectedDriver.UsesPort;
    public bool IsSerial => !SelectedDriver.UsesPort && SelectedDriver.HostLabel.Contains("COM", StringComparison.OrdinalIgnoreCase);

    public string Name { get => _name; set => Set(ref _name, value ?? string.Empty); }
    public string Host { get => _host; set => Set(ref _host, value ?? string.Empty); }
    public int Port { get => _port; set => Set(ref _port, value); }
    public int TimeoutMs { get => _timeoutMs; set => Set(ref _timeoutMs, Math.Clamp(value, 100, 120000)); }
    public string TestResult { get => _testResult; private set => Set(ref _testResult, value); }
    public string Error { get => _error; private set => Set(ref _error, value); }

    private void BuildOptions(IDictionary<string, string>? existing)
    {
        Options.Clear();
        foreach (var option in SelectedDriver.Options)
        {
            string? value = null;
            existing?.TryGetValue(option.Key, out value);
            Options.Add(new OptionFieldViewModel(option, value));
        }

        HostSuggestions.Clear();
        if (IsSerial)
        {
            foreach (var port in ModbusDrivers.SerialPorts()) HostSuggestions.Add(port);
        }
    }

    private async Task TestAsync()
    {
        TestResult = "Testando...";
        var (host, port) = NetworkCheck.ParseEndpoint(Host, UsesPort ? Port : SelectedDriver.DefaultPort);
        if (!UsesPort && port == SelectedDriver.DefaultPort && SelectedDriver.DefaultPort == 0) port = 4840;

        var ping = await NetworkCheck.PingAsync(host, TimeoutMs);
        var (_, portMessage) = await NetworkCheck.CheckTcpPortAsync(host, port, TimeoutMs);
        TestResult = $"{ping}\n{portMessage}";
    }

    /// <summary>Valida e devolve as configurações; retorna nulo e preenche <see cref="Error"/> se algo estiver errado.</summary>
    public ConnectionSettings? Build()
    {
        var name = Name.Trim();
        if (name.Length == 0)
        {
            Error = "Informe um nome para a conexão.";
            return null;
        }
        if (_nameInUse(name) && !string.Equals(name, _original?.Name, StringComparison.OrdinalIgnoreCase))
        {
            Error = $"Já existe uma conexão chamada '{name}'.";
            return null;
        }
        if (Host.Trim().Length == 0)
        {
            Error = $"Informe: {HostLabel}.";
            return null;
        }
        if (UsesPort && (Port < 1 || Port > 65535))
        {
            Error = "Porta inválida (1 a 65535).";
            return null;
        }

        return new ConnectionSettings
        {
            Name = name,
            Protocol = SelectedDriver.Protocol,
            Host = Host.Trim(),
            Port = UsesPort ? Port : 0,
            TimeoutMs = TimeoutMs,
            Options = Options.ToDictionary(o => o.Key, o => o.Value.Trim())
        };
    }
}
