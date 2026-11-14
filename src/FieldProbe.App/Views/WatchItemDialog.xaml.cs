using System.Windows;
using FieldProbe.App.ViewModels;

namespace FieldProbe.App.Views;

public partial class WatchItemDialog : Window
{
    private readonly WatchItemEditorViewModel _viewModel;

    public WatchItemDialog(WatchItemEditorViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.TryApply()) DialogResult = true;
    }
}
