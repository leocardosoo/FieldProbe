using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using FieldProbe.App.Mvvm;
using FieldProbe.Core;

namespace FieldProbe.App.ViewModels;

public enum AlarmState
{
    Normal,
    High,
    Low,
    CommFailure,
    DeviceFault
}

/// <summary>Uma linha da watch list: nome amigável, escala/unidade, alarmes e histórico para a tendência.</summary>
public sealed class WatchItemViewModel : ObservableObject
{
    public const int MaxHistory = 1200;

    private readonly object _historyLock = new();
    private readonly List<(DateTime Time, double Value)> _history = new();
    private IDisposable? _subscription;
    private volatile bool _active;
    private object? _value;
    private Quality _quality = Quality.Uncertain;
    private DateTime? _timestamp;
    private double _latencyMs;
    private long _readCount;
    private long _errorCount;
    private string? _error = "Aguardando conexão";
    private string? _typeName;
    private string _displayName = string.Empty;
    private double _scale = 1;
    private double _offset;
    private string _unit = string.Empty;
    private double? _alarmHigh;
    private double? _alarmLow;
    private bool _alarmOnCommFailure;
    private AlarmState _alarmState;
    private double? _lastNumeric;
    private bool _isTrended;
    private Color _trendColor = Color.FromRgb(0x0F, 0x6C, 0xBD);

    public WatchItemViewModel(ConnectionViewModel connection, TagAddress address, int intervalMs)
    {
        Connection = connection;
        Address = address;
        IntervalMs = intervalMs;
    }

    public ConnectionViewModel Connection { get; }

    public TagAddress Address { get; private set; }

    public int IntervalMs { get; private set; }

    public string ConnectionName => Connection.Name;

    public string AddressText => Address.Address;

    public DataType DataType => Address.DataType;

    public string ByteOrderText => Connection.Info.SupportsByteOrder ? Address.ByteOrder.ToString() : "-";

    /// <summary>Disparado (fora da thread de UI) a cada nova amostra.</summary>
    public event Action<WatchItemViewModel, TagValue>? Sampled;

    // ===================== Nome, escala e unidade =====================

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (Set(ref _displayName, value?.Trim() ?? string.Empty))
            {
                OnPropertyChanged(nameof(Label));
                OnPropertyChanged(nameof(LabelToolTip));
                OnPropertyChanged(nameof(ShowAddressUnderName));
            }
        }
    }

    /// <summary>Nome amigável ou, se vazio, o endereço.</summary>
    /// <summary>Nome exibido. Sem nome definido, usa o endereço (OPC UA: só o identificador, sem "ns=2;s=").</summary>
    public string Label => _displayName.Length > 0 ? _displayName : ShortAddress(AddressText);

    /// <summary>Texto do tooltip das colunas Nome/Endereço.</summary>
    public string LabelToolTip => _displayName.Length > 0 ? $"{_displayName}\n{AddressText}" : AddressText;

    private static string ShortAddress(string address)
    {
        // ns=2;s=Linha1.Temperatura -> Linha1.Temperatura
        var marker = address.IndexOf(";s=", StringComparison.Ordinal);
        return address.StartsWith("ns=", StringComparison.Ordinal) && marker > 0 && marker + 3 < address.Length
            ? address[(marker + 3)..]
            : address;
    }

    /// <summary>Valor exibido = valor bruto × Escala + Offset.</summary>
    public double Scale
    {
        get => _scale;
        set
        {
            if (Set(ref _scale, value == 0 ? 1 : value)) ScalingChanged();
        }
    }

    public double Offset
    {
        get => _offset;
        set
        {
            if (Set(ref _offset, value)) ScalingChanged();
        }
    }

    public string Unit
    {
        get => _unit;
        set
        {
            if (Set(ref _unit, value?.Trim() ?? string.Empty)) OnPropertyChanged(nameof(ValueText));
        }
    }

    public bool HasScaling => _scale != 1 || _offset != 0;

    public string ScalingText
    {
        get
        {
            if (!HasScaling) return string.Empty;
            var c = CultureInfo.CurrentCulture;
            var text = $"× {_scale.ToString("G6", c)}";
            if (_offset != 0) text += $" {(_offset > 0 ? "+" : "−")} {Math.Abs(_offset).ToString("G6", c)}";
            return text;
        }
    }

    private void ScalingChanged()
    {
        lock (_historyLock) _history.Clear();
        OnPropertyChanged(nameof(HasScaling));
        OnPropertyChanged(nameof(ScalingText));
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(ValueNumberText));
        ReevaluateAlarm();
    }

    // ===================== Alarmes =====================

    /// <summary>Disparado (na thread de UI) quando o estado de alarme muda: item, estado anterior, novo estado.</summary>
    public event Action<WatchItemViewModel, AlarmState, AlarmState>? AlarmChanged;

    public double? AlarmHigh
    {
        get => _alarmHigh;
        set
        {
            if (Set(ref _alarmHigh, value)) AlarmLimitsChanged();
        }
    }

    public double? AlarmLow
    {
        get => _alarmLow;
        set
        {
            if (Set(ref _alarmLow, value)) AlarmLimitsChanged();
        }
    }

    public bool AlarmOnCommFailure
    {
        get => _alarmOnCommFailure;
        set
        {
            if (Set(ref _alarmOnCommFailure, value)) AlarmLimitsChanged();
        }
    }

    public bool HasAlarms => _alarmHigh.HasValue || _alarmLow.HasValue || _alarmOnCommFailure;

    public AlarmState AlarmState => _alarmState;

    public bool IsInAlarm => _alarmState != AlarmState.Normal;

    public string AlarmText => _alarmState switch
    {
        AlarmState.High => "Alto",
        AlarmState.Low => "Baixo",
        AlarmState.CommFailure => "Sem comunicação",
        AlarmState.DeviceFault => "Equip. em falha",
        _ => HasAlarms ? "Normal" : string.Empty
    };

    public string AlarmLimitsText
    {
        get
        {
            var parts = new List<string>();
            if (_alarmLow.HasValue) parts.Add($"< {FormatLimit(_alarmLow)}");
            if (_alarmHigh.HasValue) parts.Add($"> {FormatLimit(_alarmHigh)}");
            if (_alarmOnCommFailure) parts.Add("com.");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Último valor numérico (com escala), usado nas mensagens de alarme.</summary>
    public double? LastNumeric => _lastNumeric;

    private static string FormatLimit(double? value) => value?.ToString("G6", CultureInfo.CurrentCulture) ?? "-";

    private void AlarmLimitsChanged()
    {
        OnPropertyChanged(nameof(HasAlarms));
        OnPropertyChanged(nameof(AlarmLimitsText));
        if (!HasAlarms) SetAlarmState(AlarmState.Normal);
        else ReevaluateAlarm();
        OnPropertyChanged(nameof(AlarmText));
    }

    /// <summary>Reavalia o alarme com o valor atual (ao mudar limites/escala, sem esperar a próxima leitura).</summary>
    private void ReevaluateAlarm()
    {
        if (!HasAlarms || !_active || Quality != Quality.Good || _value is null) return;
        EvaluateAlarm(new TagValue(Address, _value, Quality.Good, DateTime.Now, TimeSpan.Zero));
    }

    private void EvaluateAlarm(TagValue sample)
    {
        var next = _alarmState;
        if (sample.Quality == Quality.Bad)
        {
            if (_alarmOnCommFailure) next = sample.DeviceFault ? AlarmState.DeviceFault : AlarmState.CommFailure;
        }
        else if (sample.Quality == Quality.Good && sample.Value is not null && TryNumeric(sample.Value, out var numeric))
        {
            if (TryScale(sample.Value, out var scaled)) numeric = scaled;
            _lastNumeric = numeric;
            next = _alarmHigh.HasValue && numeric > _alarmHigh.Value ? AlarmState.High
                 : _alarmLow.HasValue && numeric < _alarmLow.Value ? AlarmState.Low
                 : AlarmState.Normal;
        }
        else if (sample.Quality == Quality.Good && _alarmState is AlarmState.CommFailure or AlarmState.DeviceFault)
        {
            next = AlarmState.Normal; // voltou a comunicar com valor não numérico
        }

        SetAlarmState(next);
    }

    private void SetAlarmState(AlarmState next)
    {
        if (next == _alarmState) return;
        var previous = _alarmState;
        _alarmState = next;
        OnPropertyChanged(nameof(AlarmState));
        OnPropertyChanged(nameof(IsInAlarm));
        OnPropertyChanged(nameof(AlarmText));
        AlarmChanged?.Invoke(this, previous, next);
    }

    // ===================== Tendência =====================

    /// <summary>Disparado quando o item entra ou sai do gráfico.</summary>
    public event Action<WatchItemViewModel>? TrendedChanged;

    public bool IsTrended
    {
        get => _isTrended;
        set
        {
            if (Set(ref _isTrended, value)) TrendedChanged?.Invoke(this);
        }
    }

    public Color TrendColor
    {
        get => _trendColor;
        set
        {
            if (Set(ref _trendColor, value)) OnPropertyChanged(nameof(TrendBrush));
        }
    }

    public Brush TrendBrush
    {
        get
        {
            var brush = new SolidColorBrush(_trendColor);
            brush.Freeze();
            return brush;
        }
    }

    // ===================== Valor e estado =====================

    public object? Value
    {
        get => _value;
        private set
        {
            if (!Set(ref _value, value)) return;
            OnPropertyChanged(nameof(ValueText));
            OnPropertyChanged(nameof(ValueNumberText));
            OnPropertyChanged(nameof(IsNumericValue));
            OnPropertyChanged(nameof(RawValueText));
        }
    }

    /// <summary>Valor de engenharia (com escala e unidade).</summary>
    public string ValueText => DisplayValue(_value);

    /// <summary>Só o número/texto (com escala), sem a unidade: a tabela mostra a unidade separada, em cinza.</summary>
    public string ValueNumberText => _value is null ? "-" : TryScale(_value, out var scaled) ? Format(scaled) : Format(_value);

    /// <summary>Valores numéricos ficam alinhados à direita na tabela.</summary>
    public bool IsNumericValue => _value is not null and not bool and not string && TryNumeric(_value, out _);

    /// <summary>Mostrar o endereço embaixo do nome (só quando o nome é diferente do endereço).</summary>
    public bool ShowAddressUnderName => !string.Equals(Label, AddressText, StringComparison.Ordinal);

    /// <summary>Valor bruto, como veio do device.</summary>
    public string RawValueText => Format(_value);

    public string? TypeName { get => _typeName; private set => Set(ref _typeName, value); }

    public Quality Quality { get => _quality; private set => Set(ref _quality, value); }

    public DateTime? Timestamp { get => _timestamp; private set => Set(ref _timestamp, value); }

    public double LatencyMs { get => _latencyMs; private set => Set(ref _latencyMs, value); }

    public long ReadCount { get => _readCount; private set => Set(ref _readCount, value); }

    public long ErrorCount { get => _errorCount; private set => Set(ref _errorCount, value); }

    public string? Error { get => _error; private set => Set(ref _error, value); }

    public bool IsRunning => _active;

    /// <summary>Aplica escala e unidade a um valor bruto (seguro fora da thread de UI).</summary>
    public string DisplayValue(object? raw)
    {
        if (raw is null) return "-";
        var text = TryScale(raw, out var scaled) ? Format(scaled) : Format(raw);
        return _unit.Length > 0 ? $"{text} {_unit}" : text;
    }

    private bool TryScale(object raw, out double scaled)
    {
        scaled = 0;
        if (!HasScaling || raw is bool || !TryNumeric(raw, out var numeric)) return false;
        scaled = Math.Round(numeric * _scale + _offset, 9);
        return true;
    }

    /// <summary>Converte um valor de engenharia digitado para o valor bruto a escrever (inverso da escala).</summary>
    public string ToRawText(string engineeringValue)
    {
        if (!HasScaling) return engineeringValue;
        if (!double.TryParse(engineeringValue.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var eng))
            return engineeringValue;

        var raw = (eng - _offset) / _scale;
        var integerType = DataType is DataType.Byte or DataType.Int16 or DataType.UInt16 or DataType.Int32 or DataType.UInt32 or DataType.Int64
                          || (DataType == DataType.Auto && Value is byte or short or ushort or int or uint or long or ulong or sbyte);
        return integerType
            ? Math.Round(raw).ToString(CultureInfo.InvariantCulture)
            : raw.ToString("R", CultureInfo.InvariantCulture);
    }

    // ===================== Ciclo de vida =====================

    public void Start()
    {
        if (_subscription is not null) return;
        Error = null;
        // _active antes do Subscribe: alguns drivers (MQTT) entregam o valor atual/retido
        // de forma síncrona, ainda dentro da chamada, antes de _subscription ser atribuído.
        _active = true;
        _subscription = Connection.Driver.Subscribe(Address, IntervalMs, OnSample);
    }

    public void Stop()
    {
        _active = false;
        _subscription?.Dispose();
        _subscription = null;
        Quality = Quality.Uncertain;
        Error = "Parado";
        SetAlarmState(AlarmState.Normal);
    }

    /// <summary>Troca endereço/tipo/ordem/ciclo sem remover o item; reinicia a leitura se estava ativa.</summary>
    public void Reconfigure(TagAddress address, int intervalMs)
    {
        var wasActive = _active;
        _active = false;
        _subscription?.Dispose();
        _subscription = null;

        Address = address;
        IntervalMs = intervalMs;
        Value = null;
        TypeName = null;
        ResetStatistics();
        Quality = Quality.Uncertain;
        Error = wasActive ? null : "Parado";

        OnPropertyChanged(nameof(Address));
        OnPropertyChanged(nameof(AddressText));
        OnPropertyChanged(nameof(DataType));
        OnPropertyChanged(nameof(ByteOrderText));
        OnPropertyChanged(nameof(IntervalMs));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(LabelToolTip));
        OnPropertyChanged(nameof(ShowAddressUnderName));

        if (wasActive) Start();
    }

    public void ResetStatistics()
    {
        ReadCount = 0;
        ErrorCount = 0;
        lock (_historyLock) _history.Clear();
    }

    public List<(DateTime Time, double Value)> HistorySnapshot()
    {
        lock (_historyLock) return _history.ToList();
    }

    private void OnSample(TagValue sample)
    {
        if (!_active) return;

        if (sample.Quality == Quality.Good && sample.Value is not null && TryNumeric(sample.Value, out var numeric))
        {
            if (TryScale(sample.Value, out var scaled)) numeric = scaled;
            lock (_historyLock)
            {
                _history.Add((sample.Timestamp, numeric));
                if (_history.Count > MaxHistory) _history.RemoveRange(0, _history.Count - MaxHistory);
            }
        }

        Sampled?.Invoke(this, sample);
        Application.Current?.Dispatcher.InvokeAsync(() => Apply(sample));
    }

    private void Apply(TagValue sample)
    {
        if (!_active) return;

        ReadCount++;
        Quality = sample.Quality;
        Timestamp = sample.Timestamp;
        LatencyMs = sample.Latency.TotalMilliseconds;
        Error = sample.Error;
        if (sample.TypeName is not null) TypeName = sample.TypeName;

        if (sample.Quality == Quality.Good)
        {
            Value = sample.Value;
            if (sample.Value is null)
            {
                TypeName = "null";
                Error = "Nó sem valor (nulo): é um nó de configuração, escolha uma variável de processo.";
            }
        }
        else if (sample.Quality == Quality.Bad)
        {
            // Falha do equipamento (CPU em STOP): a comunicação funciona, então o valor é mostrado e não conta como erro.
            if (sample.DeviceFault) Value = sample.Value;
            else ErrorCount++;
        }

        if (HasAlarms) EvaluateAlarm(sample);
    }

    // ===================== Utilitários =====================

    public static bool TryNumeric(object? value, out double result)
    {
        switch (value)
        {
            case bool b:
                result = b ? 1 : 0;
                return true;
            case string or null or Array:
                result = 0;
                return false;
            case IConvertible convertible:
                try
                {
                    result = convertible.ToDouble(CultureInfo.InvariantCulture);
                    return !double.IsNaN(result) && !double.IsInfinity(result);
                }
                catch
                {
                    result = 0;
                    return false;
                }
            default:
                result = 0;
                return false;
        }
    }

    public static string Format(object? value) => value switch
    {
        null => "-",
        float f => f.ToString("G7", CultureInfo.CurrentCulture),
        double d => d.ToString("G10", CultureInfo.CurrentCulture),
        bool b => b ? "true (1)" : "false (0)",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        string s => s,
        IEnumerable sequence => "[" + string.Join(", ", sequence.Cast<object?>().Take(50).Select(Format)) + "]",
        _ => Convert.ToString(value, CultureInfo.CurrentCulture) ?? "-"
    };
}
