using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FieldProbe.App.Mvvm;
using FieldProbe.App.ViewModels;
using FieldProbe.Core;

namespace FieldProbe.App.Hmi;

/// <summary>Elemento da tela de teste: posição/aparência (salvas em <see cref="Data"/>) e estado ao vivo (valor lido).</summary>
public sealed class HmiElementViewModel : ObservableObject
{
    private const int TrendCapacity = 150;

    private bool _isSelected;
    private bool _isEditMode = true;
    private bool _isWinCC;
    private bool _isIsa;
    private double? _previous;
    private string _trendArrow = string.Empty;
    private double? _linkedHigh;
    private double? _linkedLow;
    private double _trendLow;
    private double _trendHigh = 1;
    private Geometry? _gaugeTicks;
    private Geometry? _gaugeHighBand;
    private Geometry? _gaugeHighWarn;
    private Geometry? _gaugeLowBand;
    private Geometry? _gaugeLowWarn;
    private bool _isBad = true;
    private bool _isOn;
    private bool _isOn2;
    private bool _isOn3;
    private bool _visibilityKnown;
    private bool _visibilityBit;
    private double _numeric;
    private string _displayText = "—";
    private string _errorText = "Aguardando leitura";
    private string _alarmText = string.Empty;
    private string _setpointText = string.Empty;
    private string _linkedUnit = string.Empty;
    private string _stateText = "—";
    private string _stateBrush = "#AFB8C1";
    private readonly Queue<double> _trend = new();
    private PointCollection _trendPoints = new();
    private ImageSource? _image;
    private bool _imageLoaded;

    public HmiElementViewModel(HmiElementData data)
    {
        Data = data;
        RebuildScale();
    }

    public HmiElementData Data { get; }

    /// <summary>Disparado a cada alteração de configuração (o projeto fica com '*' e entra no desfazer).</summary>
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
        HmiKind.Sinaleiro => "Sinaleiro (torre de luz)",
        HmiKind.Tubo => "Tubulação",
        HmiKind.Estados => "Indicador de estados",
        HmiKind.Medidor => "Medidor de ponteiro",
        HmiKind.Tendencia => "Mini tendência",
        HmiKind.Painel => "Painel / retângulo",
        _ => "Imagem"
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
    public bool IsPipe => Kind == HmiKind.Tubo;
    public bool IsStates => Kind == HmiKind.Estados;
    public bool IsGauge => Kind == HmiKind.Medidor;
    public bool IsTrend => Kind == HmiKind.Tendencia;
    public bool IsPanel => Kind == HmiKind.Painel;
    public bool IsImage => Kind == HmiKind.Imagem;

    /// <summary>Símbolos desenhados (motor até sinaleiro), sem cartão em volta.</summary>
    public bool IsSymbol => Kind is >= HmiKind.Motor and <= HmiKind.Sinaleiro;

    /// <summary>Elementos ligados a uma variável (não: texto, painel, imagem e botões de navegação).</summary>
    public bool HasBinding => Kind is not (HmiKind.Texto or HmiKind.Painel or HmiKind.Imagem) && !IsNavigation;
    public bool IsNavigation => Kind == HmiKind.Botao && Data.ButtonMode is HmiButtonMode.IrParaTela or HmiButtonMode.Voltar;
    public bool ShowUnit => Kind is HmiKind.Valor or HmiKind.Setpoint or HmiKind.Barra or HmiKind.Tanque or HmiKind.Medidor or HmiKind.Tendencia;
    public bool ShowRange => Kind is HmiKind.Barra or HmiKind.Setpoint or HmiKind.Tanque or HmiKind.Medidor or HmiKind.Tendencia;
    public bool ShowAlarm => Kind is HmiKind.Valor or HmiKind.Barra or HmiKind.Tanque or HmiKind.Medidor or HmiKind.Tendencia;
    public bool ShowColors => Kind is HmiKind.Lampada or HmiKind.Botao or HmiKind.Motor or HmiKind.Bomba or HmiKind.Valvula
        or HmiKind.Esteira or HmiKind.Tanque or HmiKind.Tubo or HmiKind.Painel;
    public bool ShowOffColor => Kind is HmiKind.Lampada or HmiKind.Motor or HmiKind.Bomba or HmiKind.Valvula or HmiKind.Esteira;
    public bool ShowFlowReverse => Kind == HmiKind.Tubo;
    public bool ShowStates => Kind == HmiKind.Estados;
    public bool ShowImagePicker => Kind == HmiKind.Imagem;
    public bool ShowFaceplateOption => HasBinding && Kind != HmiKind.Setpoint && Kind != HmiKind.Botao;

    public string OnColorLabel => Kind switch
    {
        HmiKind.Tanque => "Cor do líquido",
        HmiKind.Tubo => "Cor do fluido (quando passa)",
        HmiKind.Painel => "Cor de fundo",
        HmiKind.Valvula => "Cor quando aberta",
        HmiKind.Botao => "Cor quando ligado (modo Alternar)",
        _ => "Cor quando ligado"
    };

    public string OffColorLabel => Kind == HmiKind.Valvula ? "Cor quando fechada" : "Cor quando desligado";
    public bool ShowButtonMode => Kind == HmiKind.Botao;
    public bool ShowWriteValue => Kind == HmiKind.Botao && Data.ButtonMode == HmiButtonMode.EscreverValor;
    public bool ShowTargetScreen => Kind == HmiKind.Botao && Data.ButtonMode == HmiButtonMode.IrParaTela;
    public bool HasCard => Kind is HmiKind.Lampada or HmiKind.Valor or HmiKind.Setpoint or HmiKind.Barra
        or HmiKind.Estados or HmiKind.Medidor or HmiKind.Tendencia;
    public bool IsHorizontalBar => Width >= Height;
    public bool IsVerticalBar => Width < Height;
    public bool HasCaption => Text.Length > 0;

    // ===================== Posição e tamanho =====================

    public double X { get => Data.X; set => Update(Data.X, Math.Max(0, value), v => Data.X = v); }
    public double Y { get => Data.Y; set => Update(Data.Y, Math.Max(0, value), v => Data.Y = v); }

    public double Width
    {
        get => Data.Width;
        set
        {
            Update(Data.Width, Math.Max(12, value), v => Data.Width = v, nameof(Width), nameof(IsHorizontalBar), nameof(IsVerticalBar), nameof(PipeThickness));
            RebuildTrend();
        }
    }

    public double Height
    {
        get => Data.Height;
        set
        {
            Update(Data.Height, Math.Max(12, value), v => Data.Height = v, nameof(Height), nameof(IsHorizontalBar), nameof(IsVerticalBar), nameof(PipeThickness));
            RebuildTrend();
        }
    }

    /// <summary>Espessura do fluido dentro do tubo (40% da menor dimensão).</summary>
    public double PipeThickness => Math.Max(2, Math.Min(Width, Height) * 0.4);

    // ===================== Aparência =====================

    public string Text { get => Data.Text; set => Update(Data.Text, value ?? string.Empty, v => Data.Text = v, nameof(Text), nameof(HasCaption), nameof(Label)); }
    public string Label => Data.Text.Length > 0 ? Data.Text : KindName;
    public double FontSize { get => Data.FontSize; set => Update(Data.FontSize, Math.Clamp(value, 8, 72), v => Data.FontSize = v, nameof(FontSize), nameof(BigFontSize)); }
    public double BigFontSize => FontSize * 2;
    public string Unit { get => Data.Unit; set => Update(Data.Unit, value ?? string.Empty, v => Data.Unit = v, nameof(Unit), nameof(UnitText)); }

    /// <summary>Unidade mostrada: a do elemento ou, se vazia, a do item ligado.</summary>
    public string UnitText => Data.Unit.Length > 0 ? Data.Unit : _linkedUnit;

    public int Decimals { get => Data.Decimals; set => Update(Data.Decimals, Math.Clamp(value, 0, 6), v => Data.Decimals = v); }

    public double Min
    {
        get => Data.Min;
        set
        {
            Update(Data.Min, value, v => Data.Min = v, nameof(Min), nameof(Fraction), nameof(NeedleAngle), nameof(MinText), nameof(MidText));
            RebuildScale();
            RebuildTrend();
        }
    }

    public double Max
    {
        get => Data.Max;
        set
        {
            Update(Data.Max, value, v => Data.Max = v, nameof(Max), nameof(Fraction), nameof(NeedleAngle), nameof(MaxText), nameof(MidText));
            RebuildScale();
            RebuildTrend();
        }
    }

    public string MinText => Data.Min.ToString("G4", CultureInfo.CurrentCulture);
    public string MaxText => Data.Max.ToString("G4", CultureInfo.CurrentCulture);
    public string MidText => ((Data.Min + Data.Max) / 2).ToString("G4", CultureInfo.CurrentCulture);

    public string AlarmHighText
    {
        get => Data.AlarmHigh?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
        set
        {
            Update(Data.AlarmHigh, ParseNullable(value), v => Data.AlarmHigh = v);
            RebuildScale();
            RebuildTrend();
        }
    }

    public string AlarmLowText
    {
        get => Data.AlarmLow?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
        set
        {
            Update(Data.AlarmLow, ParseNullable(value), v => Data.AlarmLow = v);
            RebuildScale();
            RebuildTrend();
        }
    }

    public string OnColor
    {
        get => Data.OnColor;
        set => Update(Data.OnColor, value ?? "Verde", v => Data.OnColor = v, nameof(OnColor), nameof(LampBrush), nameof(SymbolBrush),
            nameof(LiquidBrush), nameof(ButtonBrush), nameof(ButtonForeground), nameof(PanelBrush), nameof(FlowBrush));
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
            nameof(HasBinding), nameof(IsNavigation), nameof(ButtonBrush), nameof(ButtonForeground), nameof(BindingText), nameof(ShowFaceplateOption));
    }

    public string WriteValue { get => Data.WriteValue; set => Update(Data.WriteValue, value ?? string.Empty, v => Data.WriteValue = v); }
    public string TargetScreen { get => Data.TargetScreen; set => Update(Data.TargetScreen, value ?? string.Empty, v => Data.TargetScreen = v, nameof(TargetScreen), nameof(BindingText)); }

    public bool FlowReverse { get => Data.FlowReverse; set => Update(Data.FlowReverse, value, v => Data.FlowReverse = v, nameof(FlowReverse), nameof(IsFlowingForward), nameof(IsFlowingReverse)); }

    public string States { get => Data.States; set => Update(Data.States, value ?? string.Empty, v => Data.States = v); }

    // ===================== Comportamento =====================

    public bool Locked { get => Data.Locked; set => Update(Data.Locked, value, v => Data.Locked = v, nameof(Locked), nameof(CanResize)); }
    public bool CanResize => IsSelected && !Data.Locked;
    public HmiBlink Blink { get => Data.Blink; set => Update(Data.Blink, value, v => Data.Blink = v, nameof(Blink), nameof(IsBlinking)); }

    public HmiVisibleWhen VisibleWhen
    {
        get => Data.VisibleWhen;
        set => Update(Data.VisibleWhen, value, v => Data.VisibleWhen = v, nameof(VisibleWhen), nameof(IsHiddenByCondition), nameof(HasVisibilityCondition));
    }

    public string VisibilityAddress
    {
        get => Data.VisibilityAddress;
        set => Update(Data.VisibilityAddress, value?.Trim() ?? string.Empty, v => Data.VisibilityAddress = v, nameof(VisibilityAddress), nameof(HasVisibilityCondition));
    }

    public bool HasVisibilityCondition => Data.VisibleWhen != HmiVisibleWhen.Sempre && Data.VisibilityAddress.Length > 0;

    public bool Faceplate { get => Data.Faceplate; set => Update(Data.Faceplate, value, v => Data.Faceplate = v); }

    // ===================== Variável =====================

    public string ConnectionName { get => Data.ConnectionName; set => Update(Data.ConnectionName, value ?? string.Empty, v => Data.ConnectionName = v, nameof(ConnectionName), nameof(BindingText)); }
    public string Address { get => Data.Address; set => Update(Data.Address, value?.Trim() ?? string.Empty, v => Data.Address = v, nameof(Address), nameof(BindingText)); }
    public string Address2 { get => Data.Address2; set => Update(Data.Address2, value?.Trim() ?? string.Empty, v => Data.Address2 = v); }
    public string Address3 { get => Data.Address3; set => Update(Data.Address3, value?.Trim() ?? string.Empty, v => Data.Address3 = v); }
    public DataType DataType { get => Data.DataType; set => Update(Data.DataType, value, v => Data.DataType = v); }
    public ByteOrder ByteOrder { get => Data.ByteOrder; set => Update(Data.ByteOrder, value, v => Data.ByteOrder = v); }
    public int Length { get => Data.Length; set => Update(Data.Length, Math.Max(1, value), v => Data.Length = v); }

    public bool IsBound => HasBinding && Data.ConnectionName.Length > 0 && Data.Address.Length > 0;

    /// <summary>Precisa ler algo do equipamento (variável principal ou condição de visibilidade).</summary>
    public bool NeedsReading => Data.ConnectionName.Length > 0 && (IsBound || HasVisibilityCondition);

    // ===================== Ligação com o item da Data Access View =====================

    public bool IsLinked
    {
        get => Data.LinkToItem;
        set
        {
            Update(Data.LinkToItem, value, v => Data.LinkToItem = v, nameof(IsLinked), nameof(IsNotLinked), nameof(ShowOwnAlarm), nameof(BindingText), nameof(LinkText));
            RebuildScale();
            RebuildTrend();
        }
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

    /// <summary>
    /// Leituras do elemento: (índice, endereço). 0 = variável principal · 1 e 2 = luzes amarela e vermelha do sinaleiro
    /// · 3 = bit de visibilidade.
    /// </summary>
    public IEnumerable<(int Index, TagAddress Address)> ReadAddresses(bool includeMain = true)
    {
        if (includeMain && IsBound)
        {
            yield return (0, TagAddress);
            if (IsStackLight)
            {
                if (Data.Address2.Length > 0) yield return (1, new TagAddress(Data.Address2, DataType.Bool));
                if (Data.Address3.Length > 0) yield return (2, new TagAddress(Data.Address3, DataType.Bool));
            }
        }
        if (HasVisibilityCondition) yield return (3, new TagAddress(Data.VisibilityAddress, DataType.Bool));
    }

    // ===================== Estado da tela =====================

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value)) OnPropertyChanged(nameof(CanResize));
        }
    }

    public bool IsEditMode
    {
        get => _isEditMode;
        set
        {
            if (!Set(ref _isEditMode, value)) return;
            foreach (var name in new[] { nameof(IsRunMode), nameof(IsAnimating), nameof(IsFlowingForward), nameof(IsFlowingReverse), nameof(IsBlinking), nameof(IsHiddenByCondition), nameof(IsClickable), nameof(ShowBadOverlay) })
                OnPropertyChanged(name);
        }
    }

    public bool IsRunMode => !_isEditMode;

    /// <summary>No modo Executar, clicar abre o faceplate.</summary>
    public bool IsClickable => IsRunMode && Data.Faceplate && ShowFaceplateOption;

    /// <summary>Visual industrial (definido pela tela).</summary>
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

    /// <summary>
    /// Visual de alto desempenho (ISA-101): equipamentos e mostradores em cinza; cor só em alarme ou falha.
    /// </summary>
    public bool IsIsa
    {
        get => _isIsa;
        set
        {
            if (!Set(ref _isIsa, value)) return;
            foreach (var name in new[]
                     {
                         nameof(LampBrush), nameof(SymbolBrush), nameof(LiquidBrush), nameof(FlowBrush), nameof(PanelBrush), nameof(BarBrush),
                         nameof(TrendBrush), nameof(ButtonBrush), nameof(ButtonForeground), nameof(GaugeTrackBrush), nameof(TrackBrush)
                     })
                OnPropertyChanged(name);
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
    public string StateText => _stateText;
    public string QualityText => IsBad ? "Bad" : "Good";

    /// <summary>Elementos de liga/desliga (no faceplate mostram Ligar/Desligar em vez de campo de valor).</summary>
    public bool IsBoolKind => Data.DataType == DataType.Bool || Kind is HmiKind.Lampada or HmiKind.Motor or HmiKind.Bomba
        or HmiKind.Valvula or HmiKind.Esteira or HmiKind.Tubo or HmiKind.Sinaleiro;
    public string StateBrush => _stateBrush;
    public PointCollection TrendPoints => _trendPoints;

    /// <summary>Motor, bomba e esteira giram/andam quando ligados (só no modo Executar).</summary>
    public bool IsAnimating => IsRunMode && !IsBad && IsOn && Kind is HmiKind.Motor or HmiKind.Bomba or HmiKind.Esteira;
    public bool IsFlowingForward => IsRunMode && !IsBad && IsOn && IsPipe && !Data.FlowReverse;
    public bool IsFlowingReverse => IsRunMode && !IsBad && IsOn && IsPipe && Data.FlowReverse;

    public bool IsBlinking => IsRunMode && !IsBad && Data.Blink switch
    {
        HmiBlink.EmAlarme => HasAlarm,
        HmiBlink.QuandoLigado => IsOn,
        _ => false
    };

    /// <summary>No modo Executar, some quando a condição de visibilidade não é atendida (ou ainda não foi lida).</summary>
    public bool IsHiddenByCondition => IsRunMode && HasVisibilityCondition &&
        (!_visibilityKnown || (Data.VisibleWhen == HmiVisibleWhen.BitLigado ? !_visibilityBit : _visibilityBit));

    /// <summary>Posição do valor entre mínimo e máximo, de 0 a 1.</summary>
    public double Fraction => IsBad || Max <= Min ? 0 : Math.Clamp((Numeric - Min) / (Max - Min), 0, 1);
    public double TankFraction => Fraction;

    /// <summary>Ponteiro do medidor: -135° (mínimo) a +135° (máximo).</summary>
    public double NeedleAngle => -135 + 270 * Fraction;

    public string ValueForeground => IsBad ? "#8C959F" : HasAlarm ? "#CF222E" : "#1F2328";
    // ISA-101: ligado = cinza escuro, desligado = cinza claro, alarme = vermelho; as cores escolhidas valem nos outros visuais
    public string LampBrush => IsBad ? "#D0D7DE" : _isIsa ? (IsOn ? "#3E444A" : "#E8E9EB") : HmiColors.Hex(IsOn ? OnColor : OffColor);
    public string SymbolBrush => IsBad ? "#C9CED4" : _isIsa ? (IsOn ? "#5F656B" : "#E8E9EB") : HmiColors.Hex(IsOn ? OnColor : OffColor);
    public string LiquidBrush => HasAlarm ? "#CF222E" : _isIsa ? "#9AA0A6" : HmiColors.Hex(OnColor);
    public string FlowBrush => IsBad || !IsOn ? "#AFB8C1" : _isIsa ? "#5F656B" : HmiColors.Hex(OnColor);
    public string PanelBrush => _isIsa ? "#E3E5E8" : HmiColors.Hex(OnColor);
    public string BarBrush => HasAlarm ? "#CF222E" : _isIsa ? "#3E444A" : "#0F6CBD";
    public string TrendBrush => HasAlarm ? "#CF222E" : _isIsa ? "#1F2328" : "#0F6CBD";
    public string GaugeTrackBrush => _isIsa ? "#C4C7CB" : "#D0D7DE";
    public string TrackBrush => _isIsa ? "#D3D6DA" : "#EEF1F5";

    public string ButtonBrush => ButtonMode == HmiButtonMode.Alternar && !IsBad && IsOn
        ? (_isIsa ? "#3E444A" : HmiColors.Hex(OnColor))
        : _isIsa ? "#C4C7CB" : IsWinCC ? "#DCDFE3" : "#F6F8FA";

    public string ButtonForeground => ButtonMode == HmiButtonMode.Alternar && !IsBad && IsOn && (_isIsa || OnColor != "Branco" && OnColor != "Amarelo") ? "#FFFFFF" : "#1F2328";

    /// <summary>Selo de alarme ("ALARME ALTO"), vazio sem alarme.</summary>
    public string AlarmBadgeText => _alarmText.Length > 0 ? $"ALARME {_alarmText}" : string.Empty;

    /// <summary>Seta de tendência do valor (↑ subindo, ↓ descendo, vazio parado).</summary>
    public string TrendArrow => _trendArrow;

    /// <summary>No modo Executar, elemento com variável mas sem leitura boa: hachura cinza + aviso, como nos SCADAs.</summary>
    public bool ShowBadOverlay => IsRunMode && IsBad && (IsBound || Data.LinkToItem) && !IsHiddenByCondition;

    // Sinaleiro: verde (endereço 1), amarelo (2), vermelho (3)
    public string StackGreen => !IsBad && _isOn ? "#2DA44E" : "#1B3A24";
    public string StackYellow => !IsBad && _isOn2 ? "#F2C744" : "#4A3D12";
    public string StackRed => !IsBad && _isOn3 ? "#E5534B" : "#4A1614";

    public string StatusTip => IsNavigation ? BindingText
        : IsBound ? (IsBad ? $"{BindingText}\n{ErrorText}" : $"{BindingText}\nvalor: {DisplayText} {UnitText}")
        : HasBinding ? "Elemento sem variável ligada" : Text;

    // ===================== Imagem =====================

    public ImageSource? ImageSource
    {
        get
        {
            if (_imageLoaded) return _image;
            _imageLoaded = true;
            _image = Data.ImageData is { Length: > 0 } data ? TryLoad(data) : null;
            return _image;
        }
    }

    public void SetImage(string? base64)
    {
        Update(Data.ImageData, base64, v => Data.ImageData = v, nameof(ImageSource));
        _imageLoaded = false;
        OnPropertyChanged(nameof(ImageSource));
    }

    private static ImageSource? TryLoad(string base64)
    {
        try
        {
            var image = new BitmapImage();
            using var stream = new MemoryStream(Convert.FromBase64String(base64));
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    // ===================== Aplicar leituras =====================

    /// <summary>Aplica uma leitura (índices em <see cref="ReadAddresses"/>).</summary>
    public void Apply(TagValue value, int index = 0)
    {
        if (index > 0)
        {
            var on = value.Quality == Quality.Good && value.Value is not null && (ToNumber(value.Value) ?? 0) != 0;
            switch (index)
            {
                case 1:
                    _isOn2 = on;
                    OnPropertyChanged(nameof(StackYellow));
                    break;
                case 2:
                    _isOn3 = on;
                    OnPropertyChanged(nameof(StackRed));
                    break;
                default:
                    _visibilityKnown = value.Quality == Quality.Good && value.Value is not null;
                    _visibilityBit = on;
                    OnPropertyChanged(nameof(IsHiddenByCondition));
                    break;
            }
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
        if (_linkedHigh != item.AlarmHigh || _linkedLow != item.AlarmLow)
        {
            _linkedHigh = item.AlarmHigh;
            _linkedLow = item.AlarmLow;
            RebuildScale();
            RebuildTrend();
        }
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
            _stateText = "—";
            _stateBrush = "#AFB8C1";
            _previous = null;
            _trendArrow = string.Empty;
        }
        else
        {
            IsOn = raw is bool b ? b : number is double n && n != 0;
            if (number is double current && raw is not bool)
            {
                // seta só quando muda de verdade (ignora ruído abaixo de 0,1% da faixa)
                var span = Max > Min ? Max - Min : Math.Max(1, Math.Abs(current));
                if (_previous is double last && Math.Abs(current - last) > span * 0.001)
                    _trendArrow = current > last ? "↑" : "↓";
                else if (_previous is null)
                    _trendArrow = string.Empty;
                _previous = current;
            }
            Numeric = number ?? 0;
            DisplayText = raw switch
            {
                bool flag => flag ? "LIGADO" : "DESLIGADO",
                _ when number is double d => d.ToString("N" + Decimals, CultureInfo.CurrentCulture),
                _ => Convert.ToString(raw, CultureInfo.CurrentCulture) ?? string.Empty
            };
            AlarmText = alarm;
            if (IsStates) ResolveState(number);
            if (IsTrend && number is double sample) AddTrendSample(sample);
        }

        foreach (var name in new[]
                 {
                     nameof(HasAlarm), nameof(ValueForeground), nameof(LampBrush), nameof(SymbolBrush), nameof(LiquidBrush),
                     nameof(FlowBrush), nameof(ButtonBrush), nameof(ButtonForeground), nameof(BarBrush), nameof(TrendBrush),
                     nameof(StatusTip), nameof(IsAnimating), nameof(IsFlowingForward), nameof(IsFlowingReverse), nameof(IsBlinking),
                     nameof(Fraction), nameof(TankFraction), nameof(NeedleAngle), nameof(StackGreen), nameof(StackYellow), nameof(StackRed),
                     nameof(UnitText), nameof(StateText), nameof(StateBrush), nameof(QualityText),
                     nameof(AlarmBadgeText), nameof(TrendArrow), nameof(ShowBadOverlay)
                 })
            OnPropertyChanged(name);
    }

    /// <summary>Volta ao estado "sem leitura" (modo edição ou conexão fechada).</summary>
    public void ResetLive(string reason)
    {
        _isOn2 = false;
        _isOn3 = false;
        _visibilityKnown = false;
        OnPropertyChanged(nameof(IsHiddenByCondition));
        Apply(new TagValue(TagAddress, null, Quality.Bad, DateTime.Now, TimeSpan.Zero, reason));
    }

    // ===================== Estados =====================

    /// <summary>Lê as linhas "valor = texto = cor" do indicador de estados.</summary>
    public IReadOnlyList<(double Value, string Text, string Color)> ParseStates()
    {
        var list = new List<(double, string, string)>();
        foreach (var line in Data.States.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('=', StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;
            var key = parts[0].Replace(',', '.');
            if (!double.TryParse(key, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                if (key.Equals("true", StringComparison.OrdinalIgnoreCase) || key.Equals("ligado", StringComparison.OrdinalIgnoreCase)) value = 1;
                else if (key.Equals("false", StringComparison.OrdinalIgnoreCase) || key.Equals("desligado", StringComparison.OrdinalIgnoreCase)) value = 0;
                else continue;
            }
            list.Add((value, parts[1], parts.Length > 2 ? parts[2] : "Cinza"));
        }
        return list;
    }

    private void ResolveState(double? number)
    {
        var match = number is double n ? ParseStates().FirstOrDefault(s => Math.Abs(s.Value - n) < 1e-9) : default;
        if (match.Text is { Length: > 0 })
        {
            _stateText = match.Text;
            _stateBrush = HmiColors.Hex(match.Color);
        }
        else
        {
            _stateText = number is double v ? $"Estado {v.ToString("G6", CultureInfo.CurrentCulture)}" : DisplayText;
            _stateBrush = "#8C959F";
        }
    }

    // ===================== Tendência =====================

    private void AddTrendSample(double value)
    {
        _trend.Enqueue(value);
        while (_trend.Count > TrendCapacity) _trend.Dequeue();
        RebuildTrend();
    }

    /// <summary>Limite de alarme em vigor: o do item ligado ou o do próprio elemento.</summary>
    private double? EffectiveHigh => Data.LinkToItem ? _linkedHigh : Data.AlarmHigh;
    private double? EffectiveLow => Data.LinkToItem ? _linkedLow : Data.AlarmLow;

    public double TrendWidth => Math.Max(10, Width - 24);
    public double TrendHeight => Math.Max(10, Height - 52);
    public string TrendTopText => _trendHigh.ToString("G4", CultureInfo.CurrentCulture);
    public string TrendBottomText => _trendLow.ToString("G4", CultureInfo.CurrentCulture);
    public bool HasTrendHigh => EffectiveHigh is double h && h > _trendLow && h < _trendHigh;
    public bool HasTrendLow => EffectiveLow is double l && l > _trendLow && l < _trendHigh;
    public double TrendHighY => EffectiveHigh is double h ? TrendY(h) : 0;
    public double TrendLowY => EffectiveLow is double l ? TrendY(l) : 0;

    private double TrendY(double value) =>
        TrendHeight - TrendHeight * Math.Clamp((value - _trendLow) / Math.Max(1e-9, _trendHigh - _trendLow), 0, 1);

    /// <summary>Recalcula a linha da mini tendência no tamanho atual do elemento.</summary>
    private void RebuildTrend()
    {
        if (!IsTrend) return;
        var width = TrendWidth;
        var height = TrendHeight;
        var values = _trend.ToArray();
        var points = new PointCollection();
        double low = Min, high = Max;
        if (high <= low)
        {
            // escala automática: inclui os limites de alarme para a linha tracejada aparecer
            var all = values.Concat(new[] { EffectiveHigh, EffectiveLow }.OfType<double>()).ToList();
            low = all.Count > 0 ? all.Min() : 0;
            high = all.Count > 0 ? all.Max() : 1;
            var margin = Math.Max((high - low) * 0.1, 1e-6);
            if (high - low < 1e-9) margin = 1;
            low -= margin;
            high += margin;
        }
        _trendLow = low;
        _trendHigh = high;
        if (values.Length > 1)
        {
            for (var i = 0; i < values.Length; i++)
            {
                var x = width * i / (TrendCapacity - 1);
                var y = height - height * Math.Clamp((values[i] - low) / (high - low), 0, 1);
                points.Add(new Point(x, y));
            }
        }
        points.Freeze();
        _trendPoints = points;
        foreach (var name in new[]
                 {
                     nameof(TrendPoints), nameof(TrendWidth), nameof(TrendHeight), nameof(TrendTopText), nameof(TrendBottomText),
                     nameof(HasTrendHigh), nameof(HasTrendLow), nameof(TrendHighY), nameof(TrendLowY)
                 })
            OnPropertyChanged(name);
    }

    // ===================== Escala: medidor e barra =====================

    /// <summary>Marcas da escala do medidor (11 traços, os de 0%, 50% e 100% maiores).</summary>
    public Geometry? GaugeTicks => _gaugeTicks;

    /// <summary>Faixas coloridas no arco: alarme (vermelho) e atenção (amarelo, os 10% antes do alarme).</summary>
    public Geometry? GaugeHighBand => _gaugeHighBand;
    public Geometry? GaugeHighWarn => _gaugeHighWarn;
    public Geometry? GaugeLowBand => _gaugeLowBand;
    public Geometry? GaugeLowWarn => _gaugeLowWarn;

    /// <summary>Posição (0 a 1) da marca de alarme alto/baixo na barra; nulo se não há limite dentro da faixa.</summary>
    private double? HighFraction => EffectiveHigh is double h && Max > Min && h > Min && h < Max ? (h - Min) / (Max - Min) : null;
    private double? LowFraction => EffectiveLow is double l && Max > Min && l > Min && l < Max ? (l - Min) / (Max - Min) : null;

    public bool HasHighMarker => HighFraction is not null;
    public bool HasLowMarker => LowFraction is not null;
    public GridLength HighBefore => new(HighFraction ?? 1, GridUnitType.Star);
    public GridLength HighAfter => new(1 - (HighFraction ?? 1), GridUnitType.Star);
    public GridLength LowBefore => new(LowFraction ?? 0, GridUnitType.Star);
    public GridLength LowAfter => new(1 - (LowFraction ?? 0), GridUnitType.Star);

    private const double GaugeCx = 60, GaugeCy = 60, GaugeR = 50;

    private double GaugeAngle(double value) =>
        -135 + 270 * (Max > Min ? Math.Clamp((value - Min) / (Max - Min), 0, 1) : 0);

    private static string GaugePoint(double angle, double radius)
    {
        var rad = angle * Math.PI / 180;
        var x = GaugeCx + radius * Math.Sin(rad);
        var y = GaugeCy - radius * Math.Cos(rad);
        return x.ToString("F2", CultureInfo.InvariantCulture) + "," + y.ToString("F2", CultureInfo.InvariantCulture);
    }

    private static Geometry? Arc(double fromAngle, double toAngle)
    {
        if (toAngle - fromAngle < 0.5) return null;
        var large = toAngle - fromAngle > 180 ? 1 : 0;
        var geometry = Geometry.Parse($"M {GaugePoint(fromAngle, GaugeR)} A {GaugeR},{GaugeR} 0 {large} 1 {GaugePoint(toAngle, GaugeR)}");
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Recalcula as marcas e as faixas do medidor e as marcas de alarme da barra.</summary>
    private void RebuildScale()
    {
        var ticks = new System.Text.StringBuilder();
        for (var i = 0; i <= 10; i++)
        {
            var angle = -135 + 27 * i;
            var inner = i % 5 == 0 ? 35 : 39;
            ticks.Append($"M {GaugePoint(angle, inner)} L {GaugePoint(angle, 43)} ");
        }
        _gaugeTicks = Geometry.Parse(ticks.ToString());
        _gaugeTicks.Freeze();

        var span = Max - Min;
        _gaugeHighBand = _gaugeHighWarn = _gaugeLowBand = _gaugeLowWarn = null;
        if (span > 0)
        {
            if (EffectiveHigh is double high && high < Max)
            {
                _gaugeHighBand = Arc(GaugeAngle(high), 135);
                _gaugeHighWarn = Arc(GaugeAngle(Math.Max(Min, high - span * 0.1)), GaugeAngle(high));
            }
            if (EffectiveLow is double low && low > Min)
            {
                _gaugeLowBand = Arc(-135, GaugeAngle(low));
                _gaugeLowWarn = Arc(GaugeAngle(low), GaugeAngle(Math.Min(Max, low + span * 0.1)));
            }
        }

        foreach (var name in new[]
                 {
                     nameof(GaugeTicks), nameof(GaugeHighBand), nameof(GaugeHighWarn), nameof(GaugeLowBand), nameof(GaugeLowWarn),
                     nameof(HasHighMarker), nameof(HasLowMarker), nameof(HighBefore), nameof(HighAfter), nameof(LowBefore), nameof(LowAfter)
                 })
            OnPropertyChanged(name);
    }

    // ===================== Utilitários =====================

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
