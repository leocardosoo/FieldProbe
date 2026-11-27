using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FieldProbe.App.Hmi;
using FieldProbe.App.ViewModels;
using FieldProbe.Core;

namespace FieldProbe.App.Views;

/// <summary>
/// Tela de teste (IHM): o usuário monta um painel livre com lâmpadas, botões, mostradores, setpoints e barras
/// ligados às variáveis das conexões, e opera a máquina por ele no modo "Executar".
/// </summary>
public partial class HmiWindow : Window
{
    private const double GridSize = 10;

    private readonly MainViewModel _main;
    private readonly ObservableCollection<HmiElementViewModel> _elements = new();
    private const int MaxImageBytes = 8 * 1024 * 1024;

    private readonly Dictionary<HmiElementViewModel, List<IDisposable>> _subscriptions = new();
    private readonly DispatcherTimer _attachTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stack<HmiScreenData> _history = new();

    private bool _fullscreen;
    private WindowState _previousState;
    private bool _navigating;

    private HmiScreenData? _screen;
    private HmiElementViewModel? _selected;
    private bool _running;

    // arrastar
    private HmiElementViewModel? _dragging;
    private Point _dragStart;
    private double _dragOriginX;
    private double _dragOriginY;

    // botão de pulso pressionado
    private HmiElementViewModel? _pulseActive;

    public HmiWindow(MainViewModel main)
    {
        InitializeComponent();
        Language = XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag); // números com vírgula
        _main = main;
        ElementsHost.ItemsSource = _elements;

        TypeBox.ItemsSource = Enum.GetValues<DataType>();
        OrderBox.ItemsSource = Enum.GetValues<ByteOrder>();
        ButtonModeBox.ItemsSource = new Dictionary<HmiButtonMode, string>
        {
            [HmiButtonMode.Alternar] = "Alternar (liga ↔ desliga)",
            [HmiButtonMode.Pulso] = "Pulso (liga enquanto pressiona)",
            [HmiButtonMode.Ligar] = "Ligar (escreve 1)",
            [HmiButtonMode.Desligar] = "Desligar (escreve 0)",
            [HmiButtonMode.EscreverValor] = "Escrever um valor fixo"
        };
        var colors = HmiColors.All.Select(c => new ColorOption(c.Key, c.Value)).ToList();
        OnColorBox.ItemsSource = colors;
        OffColorBox.ItemsSource = colors;
        ThemeBox.ItemsSource = new Dictionary<HmiTheme, string>
        {
            [HmiTheme.Claro] = "Claro (moderno)",
            [HmiTheme.WinCC] = "Industrial",
            [HmiTheme.AltoDesempenho] = "Alto desempenho (ISA-101)"
        };
        ImageFitBox.ItemsSource = Enum.GetValues<HmiImageFit>();
        BlinkBox.ItemsSource = new Dictionary<HmiBlink, string>
        {
            [HmiBlink.Nunca] = "Nunca",
            [HmiBlink.EmAlarme] = "Quando estiver em alarme",
            [HmiBlink.QuandoLigado] = "Quando estiver ligado"
        };
        VisibleWhenBox.ItemsSource = new Dictionary<HmiVisibleWhen, string>
        {
            [HmiVisibleWhen.Sempre] = "Sempre",
            [HmiVisibleWhen.BitLigado] = "Só quando um bit estiver ligado",
            [HmiVisibleWhen.BitDesligado] = "Só quando um bit estiver desligado"
        };
        ZoomBox.ItemsSource = new[] { 50, 75, 100, 125, 150, 200 }.Select(z => new ZoomOption(z)).ToList();
        ZoomBox.SelectedIndex = 2;
        SurfaceScroll.PreviewMouseWheel += SurfaceScroll_PreviewMouseWheel;

        _attachTimer.Tick += (_, _) => AttachSubscriptions();
        _undoTimer.Tick += (_, _) => FlushUndo();
        _clock.Tick += (_, _) =>
        {
            ClockText.Text = DateTime.Now.ToString("dd/MM/yyyy  HH:mm:ss");
            UpdateAlarmBanner();
        };
        Activated += (_, _) => RefreshSources();
        Closed += (_, _) =>
        {
            StopRuntime();
            _clock.Stop();
        };
        _main.ProjectReplaced += OnProjectReplaced;
        Closed += (_, _) => _main.ProjectReplaced -= OnProjectReplaced;

        if (_main.HmiScreens.Count == 0)
            _main.HmiScreens.Add(new HmiScreenData { Name = "Tela 1" });
        ScreensBox.ItemsSource = _main.HmiScreens;
        ScreensBox.SelectedIndex = 0;
        SetRunning(false);
    }

    public sealed record ColorOption(string Name, string Hex);

    private void OnProjectReplaced(object? sender, EventArgs e) => Close();

    /// <summary>Conexões e itens podem mudar na janela principal: atualiza as listas ao voltar para cá.</summary>
    private void RefreshSources()
    {
        // As listas são trocadas com o painel desligado do elemento: trocar o ItemsSource de um ComboBox
        // ligado ao elemento apagaria a conexão/tela escolhida.
        var context = PropertiesPanel.DataContext;
        PropertiesPanel.DataContext = null;

        var names = _main.Connections.Select(c => c.Name).ToList();
        foreach (var name in _elements.Select(x => x.ConnectionName).Where(n => n.Length > 0 && !names.Contains(n)).Distinct().ToList())
            names.Add(name); // conexão renomeada/removida: o elemento não perde o vínculo
        if (ConnectionBox.ItemsSource is not List<string> current || !current.SequenceEqual(names))
        {
            ConnectionBox.ItemsSource = names;
            VisibilityConnectionBox.ItemsSource = names;
        }

        var screens = _main.HmiScreens.Select(x => x.Name).ToList();
        foreach (var target in _elements.Select(x => x.TargetScreen).Where(n => n.Length > 0 && !screens.Contains(n)).Distinct().ToList())
            screens.Add(target);
        if (TargetScreenBox.ItemsSource is not List<string> currentScreens || !currentScreens.SequenceEqual(screens))
            TargetScreenBox.ItemsSource = screens;

        WatchBox.ItemsSource = _main.WatchItems.ToList();
        PropertiesPanel.DataContext = context;
    }

    // ===================== Telas =====================

    private void ScreensBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScreensBox.SelectedItem is HmiScreenData screen) ShowScreen(screen);
    }

    private void ShowScreen(HmiScreenData screen)
    {
        if (!_navigating && _screen is not null && _running && !ReferenceEquals(_screen, screen)) _history.Push(_screen);
        StopRuntime();
        _screen = screen;
        _elements.Clear();
        foreach (var data in screen.Elements) _elements.Add(Wrap(data));
        Surface.Width = screen.Width;
        Surface.Height = screen.Height;
        ScreenNameBox.Text = screen.Name;
        ScreenWidthBox.Text = screen.Width.ToString(CultureInfo.InvariantCulture);
        ScreenHeightBox.Text = screen.Height.ToString(CultureInfo.InvariantCulture);
        HeaderTitle.Text = screen.Name;
        ThemeBox.SelectedValue = screen.Theme;
        ImageFitBox.SelectedItem = screen.BackgroundFit;
        ApplyTheme();
        ApplyBackground();
        Select(null);
        ResetUndo();
        if (_running) StartRuntime();
        UpdateStatus();
    }

    private HmiElementViewModel Wrap(HmiElementData data)
    {
        var vm = new HmiElementViewModel(data)
        {
            IsEditMode = !_running,
            IsWinCC = _screen?.Theme is HmiTheme.WinCC or HmiTheme.AltoDesempenho,
            IsIsa = _screen?.Theme == HmiTheme.AltoDesempenho
        };
        vm.Changed += (_, _) => OnElementChanged();
        return vm;
    }

    private void NewScreen_Click(object sender, RoutedEventArgs e)
    {
        var screen = new HmiScreenData { Name = $"Tela {_main.HmiScreens.Count + 1}" };
        _main.HmiScreens.Add(screen);
        ScreensBox.Items.Refresh();
        ScreensBox.SelectedItem = screen;
        _main.MarkDirty();
    }

    private void ScreenName_LostFocus(object sender, RoutedEventArgs e) => RenameScreen();

    private void ScreenName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) RenameScreen();
    }

    private void RenameScreen()
    {
        var name = ScreenNameBox.Text.Trim();
        if (_screen is null || name.Length == 0 || name == _screen.Name) return;
        // botões que navegam para esta tela acompanham o novo nome
        foreach (var element in _main.HmiScreens.SelectMany(s => s.Elements).Where(e => e.TargetScreen == _screen.Name))
            element.TargetScreen = name;
        _screen.Name = name;
        HeaderTitle.Text = name;
        ScreensBox.Items.Refresh();
        _main.MarkDirty();
    }

    private void DeleteScreen_Click(object sender, RoutedEventArgs e)
    {
        if (_screen is null) return;
        if (MessageBox.Show(this, $"Excluir a tela '{_screen.Name}' e os {_screen.Elements.Count} elemento(s) dela?", "Excluir tela",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _main.HmiScreens.Remove(_screen);
        if (_main.HmiScreens.Count == 0) _main.HmiScreens.Add(new HmiScreenData { Name = "Tela 1" });
        ScreensBox.Items.Refresh();
        ScreensBox.SelectedIndex = 0;
        _main.MarkDirty();
    }

    // ===================== Visual da tela: tema, imagem de fundo, tamanho =====================

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_screen is null || ThemeBox.SelectedValue is not HmiTheme theme || theme == _screen.Theme) return;
        _screen.Theme = theme;
        ApplyTheme();
        _main.MarkDirty();
    }

    private void ApplyTheme()
    {
        var theme = _screen?.Theme ?? HmiTheme.Claro;
        var isa = theme == HmiTheme.AltoDesempenho;
        var industrial = theme is HmiTheme.WinCC or HmiTheme.AltoDesempenho;
        foreach (var vm in _elements)
        {
            vm.IsWinCC = industrial;
            vm.IsIsa = isa;
        }
        // ISA-101: fundo cinza neutro (nem branco nem escuro) para a cor do alarme saltar aos olhos
        Surface.Background = ToBrush(isa ? "#D9DBDE" : industrial ? "#C9D0D7" : "#FFFFFF");
        SurfaceFrame.BorderBrush = ToBrush(isa ? "#9AA0A6" : industrial ? "#5C6670" : "#D0D7DE");
        RunHeader.Background = ToBrush(isa ? "#4A5056" : industrial ? "#1E2D3D" : "#24292F");
        HeaderTitle.FontFamily = new FontFamily(industrial ? "Arial" : "Segoe UI");
    }

    private static SolidColorBrush ToBrush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    private void BackgroundImage_Click(object sender, RoutedEventArgs e)
    {
        if (_screen is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Imagem de fundo da tela",
            Filter = "Imagens (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|Todos os arquivos (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        byte[] bytes;
        BitmapImage image;
        try
        {
            bytes = File.ReadAllBytes(dialog.FileName);
            if (bytes.Length > MaxImageBytes)
            {
                MessageBox.Show(this, $"A imagem tem {bytes.Length / 1024 / 1024} MB. Use uma de até {MaxImageBytes / 1024 / 1024} MB (ela fica guardada dentro do projeto).",
                    "Imagem grande demais", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            image = LoadImage(bytes);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or FormatException)
        {
            MessageBox.Show(this, $"Não foi possível abrir a imagem: {ex.Message}", "Imagem de fundo", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _screen.BackgroundImage = Convert.ToBase64String(bytes);
        var width = Math.Clamp(image.PixelWidth, 400, 3840);
        var height = Math.Clamp(image.PixelHeight, 300, 2160);
        if ((width != _screen.Width || height != _screen.Height) &&
            MessageBox.Show(this, $"A imagem tem {image.PixelWidth}×{image.PixelHeight} pixels.\n\nAjustar o tamanho da tela para {width}×{height}?",
                "Imagem de fundo", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _screen.Width = width;
            _screen.Height = height;
            Surface.Width = width;
            Surface.Height = height;
            ScreenWidthBox.Text = width.ToString(CultureInfo.InvariantCulture);
            ScreenHeightBox.Text = height.ToString(CultureInfo.InvariantCulture);
        }
        ApplyBackground();
        _main.MarkDirty();
        StatusText.Text = $"Imagem de fundo: {Path.GetFileName(dialog.FileName)} ({bytes.Length / 1024} KB, guardada no projeto).";
    }

    private void RemoveImage_Click(object sender, RoutedEventArgs e)
    {
        if (_screen?.BackgroundImage is null) return;
        _screen.BackgroundImage = null;
        ApplyBackground();
        _main.MarkDirty();
    }

    private void ImageFitBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_screen is null || ImageFitBox.SelectedItem is not HmiImageFit fit || fit == _screen.BackgroundFit) return;
        _screen.BackgroundFit = fit;
        ApplyBackground();
        _main.MarkDirty();
    }

    private void ApplyBackground()
    {
        RemoveImageButton.IsEnabled = _screen?.BackgroundImage is not null;
        BgImage.Source = null;
        if (_screen?.BackgroundImage is not { Length: > 0 } base64) return;
        try
        {
            BgImage.Source = LoadImage(Convert.FromBase64String(base64));
            BgImage.Stretch = _screen.BackgroundFit switch
            {
                HmiImageFit.Esticar => Stretch.Fill,
                HmiImageFit.Original => Stretch.None,
                _ => Stretch.Uniform
            };
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or IOException)
        {
            StatusText.Text = $"A imagem de fundo salva no projeto está corrompida: {ex.Message}";
        }
    }

    private static BitmapImage LoadImage(byte[] bytes)
    {
        var image = new BitmapImage();
        using var stream = new MemoryStream(bytes);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void ApplySize_Click(object sender, RoutedEventArgs e)
    {
        if (_screen is null) return;
        if (!int.TryParse(ScreenWidthBox.Text.Trim(), out var width) || !int.TryParse(ScreenHeightBox.Text.Trim(), out var height) ||
            width is < 400 or > 3840 || height is < 300 or > 2160)
        {
            StatusText.Text = "Tamanho inválido: largura de 400 a 3840 e altura de 300 a 2160 pixels.";
            return;
        }
        _screen.Width = width;
        _screen.Height = height;
        Surface.Width = width;
        Surface.Height = height;
        _main.MarkDirty();
        UpdateStatus();
    }

    // ===================== Navegação e tela cheia =====================

    private void GoToScreen(string name)
    {
        var target = _main.HmiScreens.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            StatusText.Text = name.Length == 0
                ? "Este botão não tem tela de destino: escolha no modo Editar."
                : $"A tela '{name}' não existe mais.";
            return;
        }
        ScreensBox.SelectedItem = target; // ShowScreen guarda a tela atual no histórico
    }

    private void GoBack()
    {
        while (_history.Count > 0)
        {
            var previous = _history.Pop();
            if (!_main.HmiScreens.Contains(previous)) continue;
            _navigating = true;
            try
            {
                ScreensBox.SelectedItem = previous;
            }
            finally
            {
                _navigating = false;
            }
            return;
        }
        StatusText.Text = "Não há tela anterior.";
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => SetFullscreen(!_fullscreen);

    private void SetFullscreen(bool on)
    {
        if (on == _fullscreen) return;
        _fullscreen = on;
        if (on)
        {
            _previousState = WindowState;
            WindowState = WindowState.Normal; // necessário para cobrir a barra de tarefas ao maximizar sem borda
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
            TopBar.Visibility = Visibility.Collapsed;
            StatusBar.Visibility = Visibility.Collapsed;
            FitBox.Stretch = Stretch.Uniform;
            FitBox.HorizontalAlignment = HorizontalAlignment.Center;
            FitBox.VerticalAlignment = VerticalAlignment.Center;
            SurfaceScroll.Padding = new Thickness(0);
            SurfaceScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            SurfaceScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            Background = ToBrush(_screen?.Theme switch
            {
                HmiTheme.WinCC => "#5C6670",
                HmiTheme.AltoDesempenho => "#8E949A",
                _ => "#1F2328"
            });
            ShowFullscreenHint();
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _previousState;
            TopBar.Visibility = Visibility.Visible;
            StatusBar.Visibility = Visibility.Visible;
            FitBox.Stretch = Stretch.None;
            FitBox.HorizontalAlignment = HorizontalAlignment.Left;
            FitBox.VerticalAlignment = VerticalAlignment.Top;
            SurfaceScroll.Padding = new Thickness(16);
            SurfaceScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            SurfaceScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            SetResourceReference(BackgroundProperty, "WindowBackground");
            FullscreenHint.Visibility = Visibility.Collapsed;
        }
    }

    private async void ShowFullscreenHint()
    {
        FullscreenHint.Visibility = Visibility.Visible;
        await Task.Delay(3000);
        FullscreenHint.Visibility = Visibility.Collapsed;
    }

    // ===================== Modo editar / executar =====================

    private void ModeButton_Changed(object sender, RoutedEventArgs e)
    {
        var running = ModeButton.IsChecked == true;
        FlushUndo();
        SetRunning(running);
    }

    private void SetRunning(bool running)
    {
        _running = running;
        foreach (var vm in _elements) vm.IsEditMode = !running;
        Toolbox.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        PropertiesHost.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        ScreenTools.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        ScreenSettingsBar.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        GridDots.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        RunHeader.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        AlarmBanner.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ApplyZoom(running ? 1 : _editZoom);
        if (running) UpdateAlarmBanner();
        FullscreenButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ScreensBox.IsEnabled = true;
        if (running)
        {
            ClockText.Text = DateTime.Now.ToString("dd/MM/yyyy  HH:mm:ss");
            _clock.Start();
        }
        else
        {
            _clock.Stop();
            _history.Clear();
            if (_fullscreen) SetFullscreen(false);
        }
        ModeButtonText.Text = running ? "✏  Editar" : "▶  Executar";
        ModeText.Text = running
            ? "Executando: os valores são lidos das conexões abertas e os botões escrevem no equipamento."
            : "Editando: monte a tela e clique em Executar para operar.";

        if (running)
        {
            Select(null);
            StartRuntime();
        }
        else
        {
            StopRuntime();
            foreach (var vm in _elements) vm.ResetLive("Modo edição");
        }
        UpdateStatus();
    }

    // ===================== Leitura ao vivo =====================

    private void StartRuntime()
    {
        AttachSubscriptions();
        _attachTimer.Start();
    }

    private void StopRuntime()
    {
        _attachTimer.Stop();
        foreach (var subscription in _subscriptions.Values.SelectMany(s => s)) subscription.Dispose();
        _subscriptions.Clear();
        _pulseActive = null;
    }

    /// <summary>Liga cada elemento à sua variável (só em conexões abertas) e solta os de conexões que fecharam.</summary>
    private void AttachSubscriptions()
    {
        if (!_running) return;

        foreach (var (vm, subscriptions) in _subscriptions.ToList())
        {
            var connection = FindConnection(vm);
            var linkStale = subscriptions.OfType<ItemLink>().Any(l => !_main.WatchItems.Contains(l.Item)) || (vm.IsLinked && !subscriptions.OfType<ItemLink>().Any() && FindItem(vm) is not null);
            if (connection is { IsOpen: true } && _elements.Contains(vm) && !linkStale) continue;
            foreach (var subscription in subscriptions) subscription.Dispose();
            _subscriptions.Remove(vm);
            vm.ResetLive(connection is null ? "Conexão não existe mais no projeto" : "Conexão fechada");
        }

        foreach (var vm in _elements.Where(e => (e.HasBinding || e.HasVisibilityCondition) && !_subscriptions.ContainsKey(e)))
        {
            if (!vm.NeedsReading)
            {
                if (vm.HasBinding) vm.ResetLive("Sem variável ligada (modo Editar → propriedades)");
                continue;
            }
            var connection = FindConnection(vm);
            if (connection is null)
            {
                vm.ResetLive($"A conexão '{vm.ConnectionName}' não existe no projeto");
                continue;
            }
            if (!connection.IsOpen)
            {
                vm.ResetLive($"Conexão '{connection.Name}' fechada: conecte na janela principal");
                continue;
            }
            try
            {
                var target = vm;
                var list = new List<IDisposable>();
                // Ligado a um item: usa a leitura do próprio item (sem tráfego extra), com escala, unidade e alarme dele
                var linkedItem = vm.IsLinked ? FindItem(vm) : null;
                if (linkedItem is not null) list.Add(new ItemLink(linkedItem, vm));
                foreach (var (index, address) in vm.ReadAddresses(includeMain: linkedItem is null))
                {
                    var i = index;
                    list.Add(connection.Driver.Subscribe(address, 500, value => Dispatcher.InvokeAsync(() => target.Apply(value, i))));
                }
                _subscriptions[vm] = list;
            }
            catch (Exception ex)
            {
                vm.ResetLive(ex.Message);
            }
        }
        UpdateStatus();
    }

    private ConnectionViewModel? FindConnection(HmiElementViewModel vm) =>
        _main.Connections.FirstOrDefault(c => c.Name == vm.ConnectionName);

    /// <summary>Item da Data Access View com a mesma conexão e endereço (de preferência com o mesmo nome).</summary>
    private WatchItemViewModel? FindItem(HmiElementViewModel vm)
    {
        var candidates = _main.WatchItems
            .Where(w => w.ConnectionName == vm.ConnectionName && string.Equals(w.AddressText, vm.Address, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return candidates.FirstOrDefault(w => w.Label == vm.LinkedItemName) ?? candidates.FirstOrDefault();
    }

    /// <summary>Acompanha um item da Data Access View: cada leitura/alteração do item atualiza o elemento.</summary>
    private sealed class ItemLink : IDisposable
    {
        private static readonly HashSet<string> Watched = new()
        {
            nameof(WatchItemViewModel.Timestamp), nameof(WatchItemViewModel.Value), nameof(WatchItemViewModel.Quality),
            nameof(WatchItemViewModel.AlarmState), nameof(WatchItemViewModel.Unit), nameof(WatchItemViewModel.Scale),
            nameof(WatchItemViewModel.Offset), nameof(WatchItemViewModel.IsRunning)
        };

        private readonly HmiElementViewModel _element;

        public ItemLink(WatchItemViewModel item, HmiElementViewModel element)
        {
            Item = item;
            _element = element;
            item.PropertyChanged += OnItemChanged;
            element.ApplyFromItem(item);
        }

        public WatchItemViewModel Item { get; }

        private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is null || Watched.Contains(e.PropertyName)) _element.ApplyFromItem(Item);
        }

        public void Dispose() => Item.PropertyChanged -= OnItemChanged;
    }

    private void UpdateStatus()
    {
        if (!_running)
        {
            StatusText.Text = $"{_elements.Count} elemento(s) nesta tela · tamanho {Surface.Width:0}×{Surface.Height:0} · a tela é salva junto com o projeto (.fpx)";
            return;
        }
        var bound = _elements.Count(e => e.HasBinding);
        StatusText.Text = $"Executando · {_subscriptions.Count} de {bound} elemento(s) lendo dados · conexões fechadas são ligadas automaticamente quando abrirem";
    }

    // ===================== Escrita =====================

    private async Task<bool> WriteAsync(HmiElementViewModel vm, string value, bool askConfirm, bool engineering = false)
    {
        if (!vm.IsBound)
        {
            StatusText.Text = $"'{Label(vm)}' não tem variável ligada: volte para Editar e preencha conexão e endereço.";
            return false;
        }
        var connection = FindConnection(vm);
        if (connection is not { IsOpen: true })
        {
            StatusText.Text = $"Conexão '{vm.ConnectionName}' não está conectada.";
            return false;
        }
        if (askConfirm && ConfirmBox.IsChecked == true &&
            MessageBox.Show(this,
                $"Escrever\n\n    {value}\n\nem {vm.Address} ({connection.Name}) · {Label(vm)}?\n\nConfirme que a escrita é segura para o processo/máquina.",
                "Confirmar escrita", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return false;

        // Ligado a um item com escala: o valor digitado está em unidade de engenharia, o equipamento recebe o bruto
        var address = vm.TagAddress;
        var raw = value;
        if (vm.IsLinked && FindItem(vm) is { } item)
        {
            address = item.Address;
            if (engineering) raw = item.ToRawText(value);
        }

        try
        {
            await connection.Driver.WriteAsync(address, raw);
            _main.AddLog(connection.Name, "WRITE", $"IHM · {Label(vm)}: {vm.Address} ← {raw}{(raw != value ? $" (valor de engenharia {value})" : string.Empty)}: OK");
            StatusText.Text = $"{Label(vm)}: {vm.Address} ← {value} ✔";
            return true;
        }
        catch (Exception ex)
        {
            _main.AddLog(connection.Name, "ERRO", $"IHM · escrita em {vm.Address} falhou: {ex.Message}");
            StatusText.Text = $"Escrita em {vm.Address} falhou: {ex.Message}";
            return false;
        }
    }

    private static string Label(HmiElementViewModel vm) => vm.Text.Length > 0 ? vm.Text : vm.KindName;

    private static HmiElementViewModel? ElementOf(object sender) => (sender as FrameworkElement)?.DataContext as HmiElementViewModel;

    private async void HmiButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_running || ElementOf(sender) is not { } vm) return;

        if (vm.ButtonMode == HmiButtonMode.IrParaTela)
        {
            GoToScreen(vm.TargetScreen);
            return;
        }
        if (vm.ButtonMode == HmiButtonMode.Voltar)
        {
            GoBack();
            return;
        }

        var value = vm.ButtonMode switch
        {
            HmiButtonMode.Alternar => vm.IsOn ? "0" : "1",
            HmiButtonMode.Ligar => "1",
            HmiButtonMode.Desligar => "0",
            HmiButtonMode.EscreverValor => vm.WriteValue,
            _ => null // pulso é tratado no pressionar/soltar
        };
        if (value is not null) await WriteAsync(vm, value, askConfirm: true, engineering: vm.ButtonMode == HmiButtonMode.EscreverValor);
    }

    private async void HmiButton_Down(object sender, MouseButtonEventArgs e)
    {
        if (!_running || ElementOf(sender) is not { ButtonMode: HmiButtonMode.Pulso } vm) return;

        if (ConfirmBox.IsChecked == true)
        {
            // Com confirmação não dá para segurar o botão: envia um pulso de 0,5 s.
            e.Handled = true;
            if (!await WriteAsync(vm, "1", askConfirm: true)) return;
            await Task.Delay(500);
            await WriteAsync(vm, "0", askConfirm: false);
            return;
        }

        _pulseActive = vm;
        await WriteAsync(vm, "1", askConfirm: false);
    }

    private async void HmiButton_Up(object sender, MouseButtonEventArgs e)
    {
        if (_pulseActive is not { } vm || !ReferenceEquals(ElementOf(sender), vm)) return;
        _pulseActive = null;
        await WriteAsync(vm, "0", askConfirm: false);
    }

    private async void SendSetpoint_Click(object sender, RoutedEventArgs e)
    {
        if (ElementOf(sender) is { } vm) await SendSetpointAsync(vm);
    }

    private async void Setpoint_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ElementOf(sender) is { } vm) await SendSetpointAsync(vm);
    }

    private async Task SendSetpointAsync(HmiElementViewModel vm)
    {
        if (!_running) return;
        var text = vm.SetpointText.Trim();
        if (text.Length == 0)
        {
            StatusText.Text = "Digite o valor do setpoint.";
            return;
        }
        if (vm.Max > vm.Min && double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var number) &&
            (number < vm.Min || number > vm.Max))
        {
            StatusText.Text = $"{text} está fora da faixa permitida ({vm.Min} a {vm.Max}). Ajuste a faixa no modo Editar se precisar.";
            return;
        }
        if (await WriteAsync(vm, text, askConfirm: true, engineering: true)) vm.SetpointText = string.Empty;
    }

    // ===================== Edição =====================

    private readonly List<HmiElementViewModel> _selection = new();
    private readonly Dictionary<HmiElementViewModel, Point> _dragOrigins = new();
    private bool _banding;
    private Point _bandStart;

    private void AddElement_Click(object sender, RoutedEventArgs e)
    {
        if (_screen is null || (sender as FrameworkElement)?.Tag is not string tag || !Enum.TryParse<HmiKind>(tag, out var kind)) return;

        var data = new HmiElementData { Kind = kind };
        (data.Width, data.Height, data.Text) = kind switch
        {
            HmiKind.Texto => (240d, 40d, "Título da tela"),
            HmiKind.Lampada => (120d, 130d, "Lâmpada"),
            HmiKind.Botao => (150d, 60d, "Botão"),
            HmiKind.Valor => (200d, 110d, "Valor"),
            HmiKind.Setpoint => (230d, 120d, "Setpoint"),
            HmiKind.Barra => (260d, 90d, "Nível"),
            HmiKind.Motor => (110d, 130d, "Motor"),
            HmiKind.Bomba => (110d, 130d, "Bomba"),
            HmiKind.Valvula => (110d, 130d, "Válvula"),
            HmiKind.Tanque => (160d, 230d, "Tanque"),
            HmiKind.Esteira => (280d, 115d, "Esteira"),
            HmiKind.Sinaleiro => (70d, 190d, string.Empty),
            HmiKind.Tubo => (200d, 22d, string.Empty),
            HmiKind.Estados => (220d, 100d, "Estado"),
            HmiKind.Medidor => (180d, 170d, "Pressão"),
            HmiKind.Tendencia => (300d, 170d, "Tendência"),
            HmiKind.Painel => (320d, 220d, "Área"),
            _ => (160d, 120d, string.Empty)
        };

        switch (kind)
        {
            case HmiKind.Tanque:
                data.OnColor = "Azul";
                data.Unit = "%";
                break;
            case HmiKind.Tubo:
                data.OnColor = "Azul";
                break;
            case HmiKind.Painel:
                data.OnColor = "Branco";
                data.FontSize = 13;
                break;
            case HmiKind.Estados:
                data.DataType = DataType.Int16;
                break;
            case HmiKind.Tendencia:
                data.Max = 0; // 0 = escala automática
                break;
            case HmiKind.Texto:
                data.FontSize = 20;
                break;
        }
        if (kind is HmiKind.Lampada or HmiKind.Botao or HmiKind.Motor or HmiKind.Bomba or HmiKind.Valvula
            or HmiKind.Esteira or HmiKind.Sinaleiro or HmiKind.Tubo)
            data.DataType = DataType.Bool;
        data.Faceplate = kind is HmiKind.Motor or HmiKind.Bomba or HmiKind.Valvula or HmiKind.Tanque or HmiKind.Esteira
            or HmiKind.Valor or HmiKind.Medidor or HmiKind.Estados or HmiKind.Lampada;

        // Nova peça já ligada à conexão selecionada na janela principal (se houver)
        if (kind is not (HmiKind.Texto or HmiKind.Painel or HmiKind.Imagem) && _main.SelectedConnection is { } connection)
            data.ConnectionName = connection.Name;

        var step = _screen.Elements.Count % 12;
        data.X = 40 + step * 30;
        data.Y = 40 + step * 30;

        // painel e imagem nascem atrás dos outros elementos
        var vm = Wrap(data);
        if (kind is HmiKind.Painel)
        {
            _screen.Elements.Insert(0, data);
            _elements.Insert(0, vm);
        }
        else
        {
            _screen.Elements.Add(data);
            _elements.Add(vm);
        }
        SetSelection(new[] { vm });
        FlushUndo();
        _main.MarkDirty();
        UpdateStatus();

        if (kind == HmiKind.Imagem) PickElementImage(vm);
    }

    private void Select(HmiElementViewModel? vm) =>
        SetSelection(vm is null ? Array.Empty<HmiElementViewModel>() : new[] { vm });

    private void SetSelection(IEnumerable<HmiElementViewModel> items)
    {
        var list = items.Distinct().ToList();
        _selection.Clear();
        _selection.AddRange(list);
        foreach (var element in _elements) element.IsSelected = _selection.Contains(element);
        _selected = _selection.Count == 1 ? _selection[0] : null;

        PropertiesPanel.DataContext = null;
        if (_selected is not null) RefreshSources();
        PropertiesPanel.DataContext = _selected;
        WatchBox.SelectedItem = null;

        PropertiesPanel.Visibility = _selected is null ? Visibility.Collapsed : Visibility.Visible;
        MultiSelectPanel.Visibility = _selection.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        MultiSelectText.Text = $"{_selection.Count} elementos selecionados";
        NoSelectionText.Visibility = _selection.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ----- seleção por retângulo (arrastar no fundo) -----

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_running || !ReferenceEquals(e.OriginalSource, Surface)) return;
        Keyboard.Focus(Surface);
        _banding = true;
        _bandStart = e.GetPosition(Surface);
        Canvas.SetLeft(SelectionRect, _bandStart.X);
        Canvas.SetTop(SelectionRect, _bandStart.Y);
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;
        SelectionRect.Visibility = Visibility.Visible;
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) Select(null);
        Surface.CaptureMouse();
        e.Handled = true;
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_banding) return;
        var p = e.GetPosition(Surface);
        Canvas.SetLeft(SelectionRect, Math.Min(p.X, _bandStart.X));
        Canvas.SetTop(SelectionRect, Math.Min(p.Y, _bandStart.Y));
        SelectionRect.Width = Math.Abs(p.X - _bandStart.X);
        SelectionRect.Height = Math.Abs(p.Y - _bandStart.Y);
    }

    private void Surface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_banding) return;
        _banding = false;
        Surface.ReleaseMouseCapture();
        SelectionRect.Visibility = Visibility.Collapsed;

        var band = new Rect(Canvas.GetLeft(SelectionRect), Canvas.GetTop(SelectionRect), SelectionRect.Width, SelectionRect.Height);
        if (band.Width < 3 && band.Height < 3) return; // foi só um clique no fundo
        var hit = _elements.Where(x => band.IntersectsWith(new Rect(x.X, x.Y, x.Width, x.Height)));
        SetSelection(Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? _selection.Concat(hit) : hit);
    }

    // ----- mover -----

    private static double Snap(double value) => Math.Round(value / GridSize) * GridSize;

    private void Element_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_running || ElementOf(sender) is not { } vm) return;
        if (e.OriginalSource is Thumb) return; // o canto azul é o redimensionar
        e.Handled = true;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SetSelection(_selection.Contains(vm) ? _selection.Where(x => x != vm).ToList() : _selection.Append(vm).ToList());
            return;
        }
        if (!_selection.Contains(vm)) Select(vm);
        if (vm.Locked) return;

        _dragging = vm;
        _dragStart = e.GetPosition(Surface);
        _dragOrigins.Clear();
        foreach (var item in _selection.Where(x => !x.Locked)) _dragOrigins[item] = new Point(item.X, item.Y);
        ((UIElement)sender).CaptureMouse();
    }

    private void Element_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging is null || e.LeftButton != MouseButtonState.Pressed || !_dragOrigins.TryGetValue(_dragging, out var origin)) return;
        var p = e.GetPosition(Surface);
        // o elemento agarrado encaixa na grade; os outros andam o mesmo tanto
        var dx = Snap(origin.X + p.X - _dragStart.X) - origin.X;
        var dy = Snap(origin.Y + p.Y - _dragStart.Y) - origin.Y;
        foreach (var (item, start) in _dragOrigins)
        {
            item.X = Math.Clamp(start.X + dx, 0, Math.Max(0, Surface.Width - item.Width));
            item.Y = Math.Clamp(start.Y + dy, 0, Math.Max(0, Surface.Height - item.Height));
        }
    }

    private void Element_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging is null) return;
        _dragging = null;
        _dragOrigins.Clear();
        ((UIElement)sender).ReleaseMouseCapture();
        FlushUndo();
    }

    private void Resize_DragStarted(object sender, DragStartedEventArgs e) => FlushUndo();

    private void Resize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (ElementOf(sender) is not { Locked: false } vm) return;
        vm.Width = Math.Round(Math.Min(vm.Width + e.HorizontalChange, Surface.Width - vm.X));
        vm.Height = Math.Round(Math.Min(vm.Height + e.VerticalChange, Surface.Height - vm.Y));
    }

    // ----- clique no modo Executar: faceplate -----

    private void Element_RunClick(object sender, MouseButtonEventArgs e)
    {
        if (!_running || ElementOf(sender) is not { IsClickable: true } vm) return;
        OpenFaceplate(vm);
        e.Handled = true;
    }

    // ----- propriedades -----

    private void WatchBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selected is not { } vm || WatchBox.SelectedItem is not WatchItemViewModel item) return;
        vm.IsLinked = true;
        vm.LinkedItemName = item.Label;
        vm.ConnectionName = item.ConnectionName;
        vm.Address = item.Address.Address;
        vm.DataType = item.Address.DataType;
        vm.ByteOrder = item.Address.ByteOrder;
        vm.Length = item.Address.Length;
        if (vm.Text is "Lâmpada" or "Botão" or "Valor" or "Setpoint" or "Nível" or "Motor" or "Bomba" or "Válvula" or "Tanque"
            or "Esteira" or "Estado" or "Pressão" or "Tendência")
            vm.Text = item.Label;
        StatusText.Text = $"'{Label(vm)}' ligado ao item '{item.Label}' ({item.ConnectionName} · {item.AddressText}): valor, escala, unidade e alarme vêm do item.";
    }

    private void Unlink_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } vm) return;
        // Leva para o elemento a unidade e os limites atuais do item, para a tela continuar igual
        if (FindItem(vm) is { } item)
        {
            if (vm.Unit.Length == 0) vm.Unit = item.Unit;
            vm.AlarmHighText = item.AlarmHigh?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
            vm.AlarmLowText = item.AlarmLow?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
            if (item.HasScaling)
                StatusText.Text = $"Atenção: o item '{item.Label}' tem escala ({item.ScalingText}); desligado, o elemento passa a mostrar o valor bruto.";
        }
        vm.IsLinked = false;
        vm.LinkedItemName = string.Empty;
    }

    private void ElementImage_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is { } vm) PickElementImage(vm);
    }

    // ----- duplicar, ordem, travar, excluir -----

    private void Duplicate_Click(object sender, RoutedEventArgs e) => DuplicateSelected();

    private void DuplicateSelected()
    {
        if (_screen is null || _selection.Count == 0) return;
        PasteData(_selection.Select(x => x.Data.Clone()).ToList(), 20);
    }

    private void BringFront_Click(object sender, RoutedEventArgs e) => Reorder(front: true);

    private void SendBack_Click(object sender, RoutedEventArgs e) => Reorder(front: false);

    private void Reorder(bool front)
    {
        if (_screen is null || _selection.Count == 0) return;
        var moving = _elements.Where(x => _selection.Contains(x)).ToList(); // mantém a ordem relativa
        foreach (var vm in moving)
        {
            _screen.Elements.Remove(vm.Data);
            _elements.Remove(vm);
        }
        if (front)
        {
            foreach (var vm in moving)
            {
                _screen.Elements.Add(vm.Data);
                _elements.Add(vm);
            }
        }
        else
        {
            for (var i = moving.Count - 1; i >= 0; i--)
            {
                _screen.Elements.Insert(0, moving[i].Data);
                _elements.Insert(0, moving[i]);
            }
        }
        SetSelection(moving);
        FlushUndo();
        _main.MarkDirty();
    }

    private void LockAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var vm in _selection) vm.Locked = true;
        FlushUndo();
    }

    private void UnlockAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var vm in _selection) vm.Locked = false;
        FlushUndo();
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        if (_screen is null || _selection.Count == 0) return;
        foreach (var vm in _selection.ToList())
        {
            _screen.Elements.Remove(vm.Data);
            _elements.Remove(vm);
        }
        Select(null);
        FlushUndo();
        _main.MarkDirty();
        UpdateStatus();
    }

    // ----- teclado -----

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _fullscreen)
        {
            SetFullscreen(false);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F11 && _running)
        {
            SetFullscreen(!_fullscreen);
            e.Handled = true;
            return;
        }
        if (_running || Keyboard.FocusedElement is TextBox) return;

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? GridSize : 1;
        switch (e.Key)
        {
            case Key.Z when ctrl:
                Undo();
                break;
            case Key.Y when ctrl:
                Redo();
                break;
            case Key.C when ctrl:
                CopySelection();
                break;
            case Key.V when ctrl:
                PasteClipboard();
                break;
            case Key.A when ctrl:
                SetSelection(_elements.ToList());
                break;
            case Key.D when ctrl:
                DuplicateSelected();
                break;
            case Key.Delete:
                DeleteSelected();
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down when _selection.Count > 0:
                foreach (var vm in _selection.Where(x => !x.Locked))
                {
                    if (e.Key == Key.Left) vm.X -= step;
                    if (e.Key == Key.Right) vm.X += step;
                    if (e.Key == Key.Up) vm.Y -= step;
                    if (e.Key == Key.Down) vm.Y += step;
                }
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
