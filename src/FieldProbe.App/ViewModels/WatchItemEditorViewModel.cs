using System.Globalization;
using System.Windows.Input;
using FieldProbe.App.Mvvm;
using FieldProbe.App.Services;
using FieldProbe.Core;

namespace FieldProbe.App.ViewModels;

/// <summary>Diálogo "Editar item": nome, endereço, tipo, ordem, ciclo, escala e unidade.</summary>
public sealed class WatchItemEditorViewModel : ObservableObject
{
    private readonly IRegisterInspector? _inspector;
    private string _displayName;
    private string _address;
    private DataType _dataType;
    private ByteOrder _byteOrder;
    private int _length;
    private int _intervalMs;
    private string _scaleText;
    private string _offsetText;
    private string _unit;
    private string _alarmHighText;
    private string _alarmLowText;
    private bool _alarmOnCommFailure;
    private string _error = string.Empty;

    public WatchItemEditorViewModel(WatchItemViewModel item)
    {
        Item = item;
        var c = CultureInfo.CurrentCulture;
        _displayName = item.DisplayName;
        _address = item.AddressText;
        _dataType = item.DataType;
        _byteOrder = item.Address.ByteOrder;
        _length = item.Address.Length;
        _intervalMs = item.IntervalMs;
        _scaleText = item.Scale.ToString("G10", c);
        _offsetText = item.Offset.ToString("G10", c);
        _unit = item.Unit;
        _alarmHighText = item.AlarmHigh?.ToString("G10", c) ?? string.Empty;
        _alarmLowText = item.AlarmLow?.ToString("G10", c) ?? string.Empty;
        _alarmOnCommFailure = item.AlarmOnCommFailure;
        _inspector = item.Connection.Driver as IRegisterInspector;

        DetectCommand = new RelayCommand(Detect, () => CanDetect && Item.Connection.IsOpen && Address.Trim().Length > 0);
    }

    public WatchItemViewModel Item { get; }

    public string ConnectionText => $"{Item.ConnectionName} · {Item.Connection.Protocol} · {Item.Connection.Endpoint}";

    public string AddressHelp => Item.Connection.Info.AddressHelp;

    public bool ByteOrderEnabled => Item.Connection.Info.SupportsByteOrder;

    public bool CanDetect => _inspector is not null;

    public DataType[] DataTypes { get; } = Enum.GetValues<DataType>();

    public ByteOrder[] ByteOrders { get; } = Enum.GetValues<ByteOrder>();

    public ICommand DetectCommand { get; }

    public string DisplayName { get => _displayName; set => Set(ref _displayName, value ?? string.Empty); }
    public string Address { get => _address; set => Set(ref _address, value ?? string.Empty); }
    public DataType DataType { get => _dataType; set => Set(ref _dataType, value); }
    public ByteOrder ByteOrder { get => _byteOrder; set => Set(ref _byteOrder, value); }
    public int Length { get => _length; set => Set(ref _length, Math.Clamp(value, 1, 250)); }
    public int IntervalMs { get => _intervalMs; set => Set(ref _intervalMs, value); }
    public string ScaleText { get => _scaleText; set => Set(ref _scaleText, value ?? string.Empty); }
    public string OffsetText { get => _offsetText; set => Set(ref _offsetText, value ?? string.Empty); }
    public string Unit { get => _unit; set => Set(ref _unit, value ?? string.Empty); }
    public string AlarmHighText { get => _alarmHighText; set => Set(ref _alarmHighText, value ?? string.Empty); }
    public string AlarmLowText { get => _alarmLowText; set => Set(ref _alarmLowText, value ?? string.Empty); }
    public bool AlarmOnCommFailure { get => _alarmOnCommFailure; set => Set(ref _alarmOnCommFailure, value); }
    public string Error { get => _error; private set => Set(ref _error, value); }

    private void Detect()
    {
        try
        {
            if (FieldProbe.Drivers.Modbus.ModbusAddress.Parse(Address).IsBit)
            {
                Error = "Coils e Discrete Inputs são bits: não há tipo para detectar.";
                return;
            }
        }
        catch (FormatException ex)
        {
            Error = ex.Message;
            return;
        }
        Error = string.Empty;

        var result = DialogService.DetectType(_inspector!, Address.Trim(), $"{Item.ConnectionName} · {Address.Trim()}");
        if (result is null) return;
        DataType = result.Type;
        ByteOrder = result.Order;
        if (result.Type == DataType.String) Length = result.Length;
    }

    /// <summary>Valida e aplica no item. Retorna falso e preenche <see cref="Error"/> se algo estiver inválido.</summary>
    public bool TryApply()
    {
        if (Address.Trim().Length == 0)
        {
            Error = "Informe o endereço.";
            return false;
        }
        if (IntervalMs < 20)
        {
            Error = "O ciclo mínimo é 20 ms.";
            return false;
        }
        if (!TryParse(ScaleText, out var scale) || scale == 0)
        {
            Error = "Escala inválida (use um número diferente de zero, ex.: 0,1).";
            return false;
        }
        if (!TryParse(OffsetText, out var offset))
        {
            Error = "Offset inválido (ex.: 0 ou -40).";
            return false;
        }

        if (!TryParseOptional(AlarmHighText, out var alarmHigh))
        {
            Error = "Limite alto inválido (deixe vazio para desativar).";
            return false;
        }
        if (!TryParseOptional(AlarmLowText, out var alarmLow))
        {
            Error = "Limite baixo inválido (deixe vazio para desativar).";
            return false;
        }
        if (alarmHigh.HasValue && alarmLow.HasValue && alarmLow >= alarmHigh)
        {
            Error = "O limite baixo precisa ser menor que o limite alto.";
            return false;
        }

        Item.AlarmHigh = alarmHigh;
        Item.AlarmLow = alarmLow;
        Item.AlarmOnCommFailure = AlarmOnCommFailure;
        Item.DisplayName = DisplayName;
        Item.Unit = Unit;
        Item.Scale = scale;
        Item.Offset = offset;

        var address = new TagAddress(Address.Trim(), DataType, ByteOrder, Length);
        if (address != Item.Address || IntervalMs != Item.IntervalMs)
            Item.Reconfigure(address, IntervalMs);

        return true;
    }

    private static bool TryParseOptional(string text, out double? value)
    {
        value = null;
        if (text.Trim().Length == 0) return true;
        if (!TryParse(text, out var parsed)) return false;
        value = parsed;
        return true;
    }

    /// <summary>Aceita "72.4", "72,4" e "1.234,5" (formato brasileiro com milhar).</summary>
    private static bool TryParse(string text, out double value)
    {
        var t = text.Trim().Replace(" ", string.Empty);
        if (t.Contains(',') && t.Contains('.')) t = t.Replace(".", string.Empty);
        return double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
