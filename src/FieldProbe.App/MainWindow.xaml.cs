using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    private void SaveTrendPng_Click(object sender, RoutedEventArgs e)
    {
        var path = FieldProbe.App.Services.DialogService.SaveFile("Imagem PNG (*.png)|*.png", $"tendencia_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        if (path is null) return;
        try
        {
            TrendChartControl.SaveAsPng(path);
            _viewModel.AddLog("FieldProbe", "INFO", $"Gráfico salvo em {path}");
        }
        catch (Exception ex)
        {
            _viewModel.AddLog("FieldProbe", "ERRO", $"Não foi possível salvar o gráfico: {ex.Message}");
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
}
