using System.Globalization;
using System.Runtime.CompilerServices;
using FieldProbe.App.Mvvm;
using FieldProbe.App.ViewModels;
using FieldProbe.Core;

namespace FieldProbe.App.Hmi;

/// <summary>Elemento da tela de teste: posição/aparência (salvas em <see cref="Data"/>) e estado ao vivo (valor lido).</summary>
public sealed class HmiElementViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isEditMode = true;
    private bool _isWinCC;
    private bool _isBad = true;
    private bool _isOn;
    private bool _isOn2;
    private bool _isOn3;
    private double _numeric;
    private string _displayText = "—";
    private string _errorText = "Aguardando leitura";
    private string _alarmText = string.Empty;
    private string _setpointText = string.Empty;
    private string _linkedUnit = string.Empty;

    public HmiElementViewModel(HmiElementData data) => Data = data;

    public HmiElementData Data { get; }

    /// <summary>Disparado a cada alteração de configuração (o projeto fica com '*').</summary>
    public event EventHandler? Changed;

    private void Update<T>(T current, T value, Action<T> assign, [CallerMemberName] string? name = null, params string[] also)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        assign(value);
        OnPropertyChanged(name);
        foreach (var other in also) OnPropertyChanged(other);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ===================== Tipo =====================

    public HmiKind Kind => Data.Kind;

    public string KindName => Kind switch
    {
        HmiKind.Texto => "Texto",
        HmiKind.Lampada => "Lâmpada",
        HmiKind.Botao => "Botão",
        HmiKind.Valor => "Mostrador de valor",
        HmiKind.Setpoint => "Campo de setpoint",
        HmiKind.Barra => "Barra",
        HmiKind.Motor => "Motor",
        HmiKind.Bomba => "Bomba",
        HmiKind.Valvula => "Válvula",
        HmiKind.Tanque => "Tanque",
        HmiKind.Esteira => "Esteira",
        _ => "Sinaleiro (torre de luz)"
    };

    public bool IsText => Kind == HmiKind.Texto;
    public bool IsLamp => Kind == HmiKind.Lampada;
    public bool IsButton => Kind == HmiKind.Botao;
    public bool IsValue => Kind == HmiKind.Valor;
    public bool IsSetpoint => Kind == HmiKind.Setpoint;
    public bool IsBar => Kind == HmiKind.Barra;
    public bool IsMotor => Kind == HmiKind.Motor;
    public bool IsPump => Kind == HmiKind.Bomba;
    public bool IsValve => Kind == HmiKind.Valvula;
    public bool IsTank => Kind == HmiKind.Tanque;
    public bool IsConveyor => Kind == HmiKind.Esteira;
    public bool IsStackLight => Kind == HmiKind.Sinaleiro;

    /// <summary>Símbolos desenhados (sem cartão em volta).</summary>
    public bool IsSymbol => Kind >= HmiKind.Motor;

    /// <summary>Elementos ligados a uma variável (todos menos o texto fixo e os botões de navegação).</summary>
    public bool HasBinding => Kind != HmiKind.Texto && !IsNavigation;
    public bool IsNavigation => Kind == HmiKind.Botao && Data.ButtonMode is HmiButtonMode.IrParaTela or HmiButtonMode.Voltar;
    public bool ShowUnit => Kind is HmiKind.Valor or HmiKind.Setpoint or HmiKind.Barra or HmiKind.Tanque;
    public bool ShowRange => Kind is HmiKind.Barra or HmiKind.Setpoint or HmiKind.Tanque;
    public bool ShowAlarm => Kind is HmiKind.Valor or HmiKind.Barra or HmiKind.Tanque;
    public bool ShowColors => Kind is HmiKind.Lampada or HmiKind.Botao or HmiKind.Motor or HmiKind.Bomba or HmiKind.Valvula or HmiKind.Esteira or HmiKind.Tanque;
    public bool ShowOffColor => Kind is HmiKind.Lampada or HmiKind.Motor or HmiKind.Bomba or HmiKind.Valvula or HmiKind.Esteira;
    public string OnColorLabel => Kind switch
    {
        HmiKind.Tanque => "Cor do líquido",
        HmiKind.Valvula => "Cor quando aberta",
        HmiKind.Botao => "Cor quando ligado (modo Alternar)",
        _ => "Cor quando ligado"
    };
    public string OffColorLabel => Kind == HmiKind.Valvula ? "Cor quando fechada" : "Cor quando desligado";
    public bool ShowButtonMode => Kind == HmiKind.Botao;
    public bool ShowWriteValue => Kind == HmiKind.Botao && Data.ButtonMode == HmiButtonMode.EscreverValor;
    public bool ShowTargetScreen => Kind == HmiKind.Botao && Data.ButtonMode == HmiButtonMode.IrParaTela;
    public bool HasCard => Kind is HmiKind.Lampada or HmiKind.Valor or HmiKind.Setpoint or HmiKind.Barra;
    public bool IsHorizontalBar => Width >= Height;
    public bool HasCaption => Text.Length > 0;

    // ===================== Posição e tamanho =====================

    public double X { get => Data.X; set => Update(Data.X, Math.Max(0, value), v => Data.X = v); }
    public double Y { get => Data.Y; set => Update(Data.Y, Math.Max(0, value), v => Data.Y = v); }
    public double Width { get => Data.Width; set => Update(Data.Width, Math.Max(30, value), v => Data.Width = v, nameof(Width), nameof(IsHorizontalBar)); }
    public double Height { get => Data.Height; set => Update(Data.Height, Math.Max(24, value), v => Data.Height = v, nameof(Height), nameof(IsHorizontalBar)); }

    // ===================== Aparência =====================

    public string Text { get => Data.Text; set => Update(Data.Text, value ?? string.Empty, v => Data.Text = v, nameof(Text), nameof(HasCaption)); }
    public double FontSize { get => Data.FontSize; set => Update(Data.FontSize, Math.Clamp(value, 8, 72), v => Data.FontSize = v, nameof(FontSize), nameof(BigFontSize)); }
    public double BigFontSize => FontSize * 2;
    public string Unit { get => Data.Unit; set => Update(Data.Unit, value ?? string.Empty, v => Data.Unit = v, nameof(Unit), nameof(UnitText)); }

    /// <summary>Unidade mostrada: a do elemento ou, se vazia, a do item ligado.</summary>
    public string UnitText => Data.Unit.Length > 0 ? Data.Unit : _linkedUnit;
    public int Decimals { get => Data.Decimals; set => Update(Data.Decimals, Math.Clamp(value, 0, 6), v => Data.Decimals = v); }
    public double Min { get => Data.Min; set => Update(Data.Min, value, v => Data.Min = v, nameof(Min), nameof(TankFraction)); }
    public double Max { get => Data.Max; set => Update(Data.Max, value, v => Data.Max = v, nameof(Max), nameof(TankFraction)); }

    public string AlarmHighText
    {
        get => Data.AlarmHigh?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
        set => Update(Data.AlarmHigh, ParseNullable(value), v => Data.AlarmHigh = v);
    }

    public string AlarmLowText
    {
        get => Data.AlarmLow?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
        set => Update(Data.AlarmLow, ParseNullable(value), v => Data.AlarmLow = v);
    }

    public string OnColor
    {
        get => Data.OnColor;
        set => Update(Data.OnColor, value ?? "Verde", v => Data.OnColor = v, nameof(OnColor), nameof(LampBrush), nameof(SymbolBrush), nameof(LiquidBrush), nameof(ButtonBrush), nameof(ButtonForeground));
    }

    public string OffColor
    {
        get => Data.OffColor;
        set => Update(Data.OffColor, value ?? "Cinza", v => Data.OffColor = v, nameof(OffColor), nameof(LampBrush), nameof(SymbolBrush));
    }

    public HmiButtonMode ButtonMode
    {
        get => Data.ButtonMode;
        set => Update(Data.ButtonMode, value, v => Data.ButtonMode = v, nameof(ButtonMode), nameof(ShowWriteValue), nameof(ShowTargetScreen),
            nameof(HasBinding), nameof(IsNavigation), nameof(ButtonBrush), nameof(ButtonForeground), nameof(BindingText));
    }

    public string WriteValue { get => Data.WriteValue; set => Update(Data.WriteValue, value ?? string.Empty, v => Data.WriteValue = v); }
    public string TargetScreen { get => Data.TargetScreen; set => Update(Data.TargetScreen, value ?? string.Empty, v => Data.TargetScreen = v, nameof(TargetScreen), nameof(BindingText)); }

    // ===================== Variável =====================

    public string ConnectionName { get => Data.ConnectionName; set => Update(Data.ConnectionName, value ?? string.Empty, v => Data.ConnectionName = v, nameof(ConnectionName), nameof(BindingText)); }
    public string Address { get => Data.Address; set => Update(Data.Address, value?.Trim() ?? string.Empty, v => Data.Address = v, nameof(Address), nameof(BindingText)); }
    public string Address2 { get => Data.Address2; set => Update(Data.Address2, value?.Trim() ?? string.Empty, v => Data.Address2 = v); }
    public string Address3 { get => Data.Address3; set => Update(Data.Address3, value?.Trim() ?? string.Empty, v => Data.Address3 = v); }
    public DataType DataType { get => Data.DataType; set => Update(Data.DataType, value, v => Data.DataType = v); }
    public ByteOrder ByteOrder { get => Data.ByteOrder; set => Update(Data.ByteOrder, value, v => Data.ByteOrder = v); }
    public int Length { get => Data.Length; set => Update(Data.Length, Math.Max(1, value), v => Data.Length = v); }

    public bool IsBound => HasBinding && Data.ConnectionName.Length > 0 && Data.Address.Length > 0;

    // ===================== Ligação com o item da Data Access View =====================

    public bool IsLinked
    {
        get => Data.LinkToItem;
        set => Update(Data.LinkToItem, value, v => Data.LinkToItem = v, nameof(IsLinked), nameof(IsNotLinked), nameof(ShowOwnAlarm), nameof(BindingText), nameof(LinkText));
    }

    public bool IsNotLinked => !Data.LinkToItem;

    public string LinkedItemName
    {
        get => Data.LinkedItemName;
        set => Update(Data.LinkedItemName, value ?? string.Empty, v => Data.LinkedItemName = v, nameof(LinkedItemName), nameof(LinkText));
    }

    public string LinkText => $"Ligado ao item \"{Data.LinkedItemName}\" da Data Access View: o valor (com escala), a unidade e o alarme vêm do item. Mudou no item, muda aqui.";

    /// <summary>Limites de alarme próprios só quando não está ligado a um item (ligado, vale o alarme do item).</summary>
    public bool ShowOwnAlarm => ShowAlarm && !Data.LinkToItem;

    public string BindingText => IsNavigation
        ? (Data.ButtonMode == HmiButtonMode.Voltar ? "→ tela anterior" : $"→ {(Data.TargetScreen.Length > 0 ? Data.TargetScreen : "(escolha a tela)")}")
        : IsBound ? $"{(Data.LinkToItem ? "🔗 " : string.Empty)}{Data.ConnectionName} · {Data.Address}" : HasBinding ? "sem variável" : string.Empty;

    public TagAddress TagAddress => new(Data.Address, Data.DataType, Data.ByteOrder, Data.Length);

    /// <summary>Variáveis lidas pelo elemento: (índice, endereço). O sinaleiro lê até três bits.</summary>
    public IEnumerable<(int Index, TagAddress Address)> ReadAddresses()
    {
        if (!IsBound) yield break;
        yield return (0, TagAddress);
        if (IsStackLight)
        {
            if (Data.Address2.Length > 0) yield return (1, new TagAddress(Data.Address2, DataType.Bool));
            if (Data.Address3.Length > 0) yield return (2, new TagAddress(Data.Address3, DataType.Bool));
        }
    }

    // ===================== Estado da tela =====================

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public bool IsEditMode
    {
        get => _isEditMode;
        set
        {
            if (!Set(ref _isEditMode, value)) return;
            OnPropertyChanged(nameof(IsRunMode));
            OnPropertyChanged(nameof(IsAnimating));
        }
    }

    public bool IsRunMode => !_isEditMode;

    /// <summary>Visual estilo WinCC (definido pela tela).</summary>
    public bool IsWinCC
    {
        get => _isWinCC;
        set
        {
            if (!Set(ref _isWinCC, value)) return;
            OnPropertyChanged(nameof(ButtonBrush));
            OnPropertyChanged(nameof(ButtonForeground));
        }
    }

    // ===================== Valor ao vivo =====================

    public bool IsBad { get => _isBad; private set => Set(ref _isBad, value); }
    public bool IsOn { get => _isOn; private set => Set(ref _isOn, value); }
    public double Numeric { get => _numeric; private set => Set(ref _numeric, value); }
    public string DisplayText { get => _displayText; private set => Set(ref _displayText, value); }
    public string ErrorText { get => _errorText; private set => Set(ref _errorText, value); }
    public string AlarmText { get => _alarmText; private set => Set(ref _alarmText, value); }
    public bool HasAlarm => _alarmText.Length > 0;
    public string SetpointText { get => _setpointText; set => Set(ref _setpointText, value ?? string.Empty); }

    /// <summary>Motor, bomba e esteira giram/andam quando ligados (só no modo Executar).</summary>
    public bool IsAnimating => IsRunMode && !IsBad && IsOn && Kind is HmiKind.Motor or HmiKind.Bomba or HmiKind.Esteira;

    /// <summary>Nível do tanque, de 0 a 1.</summary>
    public double TankFraction => IsBad || Max <= Min ? 0 : Math.Clamp((Numeric - Min) / (Max - Min), 0, 1);

    public string ValueForeground => IsBad ? "#8C959F" : HasAlarm ? "#CF222E" : "#1F2328";
    public string LampBrush => IsBad ? "#D0D7DE" : HmiColors.Hex(IsOn ? OnColor : OffColor);
    public string SymbolBrush => IsBad ? "#C9CED4" : HmiColors.Hex(IsOn ? OnColor : OffColor);
    public string LiquidBrush => HasAlarm ? "#CF222E" : HmiColors.Hex(OnColor);
    public string BarBrush => HasAlarm ? "#CF222E" : "#0F6CBD";

    public string ButtonBrush => ButtonMode == HmiButtonMode.Alternar && !IsBad && IsOn
        ? HmiColors.Hex(OnColor)
        : IsWinCC ? "#DCDFE3" : "#F6F8FA";

    public string ButtonForeground => ButtonMode == HmiButtonMode.Alternar && !IsBad && IsOn && OnColor != "Branco" && OnColor != "Amarelo" ? "#FFFFFF" : "#1F2328";

    // Sinaleiro: verde (endereço 1), amarelo (2), vermelho (3)
    public string StackGreen => !IsBad && _isOn ? "#2DA44E" : "#1B3A24";
    public string StackYellow => !IsBad && _isOn2 ? "#F2C744" : "#4A3D12";
    public string StackRed => !IsBad && _isOn3 ? "#E5534B" : "#4A1614";

    public string StatusTip => IsNavigation ? BindingText
        : IsBound ? (IsBad ? $"{BindingText}\n{ErrorText}" : $"{BindingText}\nvalor: {DisplayText}")
        : HasBinding ? "Elemento sem variável ligada" : Text;

    /// <summary>Aplica uma leitura. Índice 0 = variável principal; 1 e 2 = luzes amarela e vermelha do sinaleiro.</summary>
    public void Apply(TagValue value, int index = 0)
    {
        if (index > 0)
        {
            var on = value.Quality == Quality.Good && value.Value is not null && (ToNumber(value.Value) ?? 0) != 0;
            if (index == 1) _isOn2 = on; else _isOn3 = on;
            OnPropertyChanged(index == 1 ? nameof(StackYellow) : nameof(StackRed));
            return;
        }

        if (value.Quality != Quality.Good || value.Value is null)
        {
            SetLive(true, value.Error ?? "sem valor", null, null, string.Empty);
            return;
        }

        var number = ToNumber(value.Value);
        var alarm = number is double v && ShowAlarm
            ? (Data.AlarmHigh is double high && v > high ? "ALTO" : Data.AlarmLow is double low && v < low ? "BAIXO" : string.Empty)
            : string.Empty;
        SetLive(false, string.Empty, value.Value, number, alarm);
    }

    /// <summary>Aplica o estado do item da Data Access View ligado (valor com escala, unidade e alarme do item).</summary>
    public void ApplyFromItem(WatchItemViewModel item)
    {
        _linkedUnit = item.Unit;
        var raw = item.Value;
        if (!item.IsRunning || item.Quality != Quality.Good || raw is null)
        {
            SetLive(true, !item.IsRunning ? "Conexão fechada" : item.Error ?? "sem valor", null, null, string.Empty);
            return;
        }

        double? number = raw is bool flag ? (flag ? 1 : 0)
            : WatchItemViewModel.TryNumeric(raw, out var n) ? Math.Round(n * item.Scale + item.Offset, 9)
            : null;
        var alarm = item.AlarmState switch
        {
            AlarmState.High => "ALTO",
            AlarmState.Low => "BAIXO",
            _ => string.Empty
        };
        SetLive(false, string.Empty, raw, number, alarm);
    }

    private void SetLive(bool bad, string error, object? raw, double? number, string alarm)
    {
        IsBad = bad;
        ErrorText = error;
        if (bad)
        {
            IsOn = false;
            DisplayText = "—";
            AlarmText = string.Empty;
        }
        else
        {
            IsOn = raw is bool b ? b : number is double n && n != 0;
            Numeric = number ?? 0;
            DisplayText = raw switch
            {
                bool flag => flag ? "LIGADO" : "DESLIGADO",
                _ when number is double d => d.ToString("N" + Decimals, CultureInfo.CurrentCulture),
                _ => Convert.ToString(raw, CultureInfo.CurrentCulture) ?? string.Empty
            };
            AlarmText = alarm;
        }

        foreach (var name in new[]
                 {
                     nameof(HasAlarm), nameof(ValueForeground), nameof(LampBrush), nameof(SymbolBrush), nameof(LiquidBrush),
                     nameof(ButtonBrush), nameof(ButtonForeground), nameof(BarBrush), nameof(StatusTip), nameof(IsAnimating),
                     nameof(TankFraction), nameof(StackGreen), nameof(StackYellow), nameof(StackRed), nameof(UnitText)
                 })
            OnPropertyChanged(name);
    }

    /// <summary>Volta ao estado "sem leitura" (modo edição ou conexão fechada).</summary>
    public void ResetLive(string reason)
    {
        _isOn2 = false;
        _isOn3 = false;
        Apply(new TagValue(TagAddress, null, Quality.Bad, DateTime.Now, TimeSpan.Zero, reason));
    }

    private static double? ToNumber(object value) => value switch
    {
        bool b => b ? 1 : 0,
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out var d) => d,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null
    };

    private static double? ParseNullable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var v) ? v
            : double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v
            : null;
    }
}
