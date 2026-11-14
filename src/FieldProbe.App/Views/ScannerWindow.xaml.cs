using System.Windows;
using FieldProbe.App.ViewModels;

namespace FieldProbe.App.Views;

public partial class ScannerWindow : Window
{
    private readonly ScannerViewModel _viewModel;

    public ScannerWindow(ScannerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Closing += (_, _) => _viewModel.Cancel();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
