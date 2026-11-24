using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FieldProbe.App.Services;
using FieldProbe.App.ViewModels;

namespace FieldProbe.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        _viewModel.LogFlushed += (_, _) =>
        {
            if (_viewModel.AutoScroll && LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        };

        Closing += OnClosingWindow;
        Closed += (_, _) => _viewModel.Dispose();

        ApplyColumnSettings(LoadHiddenColumns());
    }

    // ===================== Colunas da Data Access View =====================

    /// <summary>Colunas técnicas, escondidas na primeira vez (o usuário liga pelo botão direito no cabeçalho).</summary>
    private static readonly string[] DefaultHiddenColumns =
        { "Endereço", "Tipo", "Ordem", "Escala", "Tipo nativo", "Latência (ms)", "Ciclo (ms)", "Leituras", "Erros" };

    /// <summary>Colunas que não podem ser escondidas.</summary>
    private static readonly string[] FixedColumns = { "Nome", "Valor" };

    private static string UiSettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FieldProbe", "ui.json");

    private sealed record UiSettings(List<string> HiddenColumns);

    private static IReadOnlyCollection<string> LoadHiddenColumns()
    {
        try
        {
            if (File.Exists(UiSettingsPath))
            {
                var settings = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(UiSettingsPath));
                if (settings?.HiddenColumns is not null) return settings.HiddenColumns;
            }
        }
        catch
        {
            // arquivo corrompido: volta ao padrão
        }
        return DefaultHiddenColumns;
    }

    private void SaveHiddenColumns()
    {
        try
        {
            var hidden = WatchGrid.Columns
                .Where(c => c.Visibility != Visibility.Visible)
                .Select(c => c.Header?.ToString() ?? string.Empty)
                .ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(UiSettingsPath)!);
            File.WriteAllText(UiSettingsPath, JsonSerializer.Serialize(new UiSettings(hidden)));
        }
        catch
        {
            // preferência visual: falha ao salvar não atrapalha o uso
        }
    }

    private void ApplyColumnSettings(IReadOnlyCollection<string> hidden)
    {
        foreach (var column in WatchGrid.Columns)
        {
            var header = column.Header?.ToString() ?? string.Empty;
            column.Visibility = hidden.Contains(header) && !FixedColumns.Contains(header) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Botão direito no cabeçalho da tabela: menu para escolher as colunas visíveis.</summary>
    private void WatchGrid_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not System.Windows.Controls.Primitives.DataGridColumnHeader && source is not DataGridRow)
            source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        if (source is not System.Windows.Controls.Primitives.DataGridColumnHeader) return;

        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Colunas visíveis", IsEnabled = false });
        foreach (var column in WatchGrid.Columns)
        {
            var header = column.Header?.ToString() ?? string.Empty;
            if (FixedColumns.Contains(header)) continue;

            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = column.Visibility == Visibility.Visible, StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                column.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                SaveHiddenColumns();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var reset = new MenuItem { Header = "Restaurar padrão" };
        reset.Click += (_, _) =>
        {
            ApplyColumnSettings(DefaultHiddenColumns);
            SaveHiddenColumns();
        };
        menu.Items.Add(reset);

        menu.PlacementTarget = WatchGrid;
        menu.IsOpen = true;
        e.Handled = true;
    }

    public async void OpenProjectOnStartup(string path)
    {
        try
        {
            await _viewModel.LoadProjectAsync(path);
        }
        catch (Exception ex)
        {
            _viewModel.AddLog("FieldProbe", "ERRO", $"Não foi possível abrir {path}: {ex.Message}");
        }
    }

    private void OnClosingWindow(object? sender, CancelEventArgs e)
    {
        if (!_viewModel.CanClose()) e.Cancel = true;
    }

    // TreeView.SelectedItem não aceita binding: repassamos pelo code-behind.
    private void ProjectTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _viewModel.SelectedTreeItem = e.NewValue;

    private void ProjectTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.AddSelectedNodeCommand.CanExecute(null))
        {
            _viewModel.AddSelectedNodeCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ===================== Decodificador de pacotes (log) =====================

    private void LogList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LogList.SelectedItem is LogEntry entry) OpenDecoder(entry);
    }

    private void DecodePacket_Click(object sender, RoutedEventArgs e)
    {
        if (LogList.SelectedItem is LogEntry entry) OpenDecoder(entry);
    }

    private void CopyPacketHex_Click(object sender, RoutedEventArgs e)
    {
        if (LogList.SelectedItem is LogEntry { Data: { Length: > 0 } data })
            Clipboard.SetText(string.Join(" ", data.Select(b => b.ToString("X2"))));
        else if (LogList.SelectedItem is LogEntry { Hex.Length: > 0 } entry)
            Clipboard.SetText(entry.Hex);
    }

    private void CopyLogLine_Click(object sender, RoutedEventArgs e)
    {
        if (LogList.SelectedItem is LogEntry entry)
            Clipboard.SetText($"{entry.Time}\t{entry.Source}\t{entry.Kind}\t{entry.Text}");
    }

    private void OpenDecoder(LogEntry entry)
    {
        if (!entry.CanDecode)
        {
            DialogService.Info(entry.Data is null && entry.IsTraffic
                ? "Esta linha não tem bytes brutos para decodificar (a biblioteca do protocolo não expõe o pacote)."
                : "Só linhas TX/RX com bytes podem ser decodificadas.", "Decodificar pacote");
            return;
        }

        var frame = entry.Decoder!.DecodeFrame(entry.Data!, entry.Direction);
        if (frame is null)
        {
            DialogService.Info("O formato deste pacote não foi reconhecido pelo decodificador.", "Decodificar pacote");
            return;
        }
        DialogService.ShowPacket(entry, frame);
    }

    /// <summary>Botão "+" de uma pasta da árvore: cria uma variável nela.</summary>
    private void AddVariableButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not TagNodeViewModel folder) return;
        _viewModel.SelectedTreeItem = folder;
        if (_viewModel.AddVariableCommand.CanExecute(null))
            _viewModel.AddVariableCommand.Execute(null);
    }

    /// <summary>Seleciona o item sob o cursor no clique direito (para o menu de contexto agir nele).</summary>
    private void ProjectTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not TreeViewItem)
            source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);

        if (source is TreeViewItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    /// <summary>Duplo clique numa linha da watch list abre a edição do item.</summary>
    private void WatchGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not DataGridRow)
            source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);

        if (source is DataGridRow && _viewModel.EditWatchCommand.CanExecute(null))
        {
            _viewModel.EditWatchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
}
