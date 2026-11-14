using System.Windows;
using FieldProbe.App.ViewModels;
using FieldProbe.Core;

namespace FieldProbe.App.Views;

public partial class ConnectionDialog : Window
{
    private readonly ConnectionEditorViewModel _viewModel;

    public ConnectionDialog(ConnectionEditorViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    public ConnectionSettings? Result { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = _viewModel.Build();
        if (Result is not null) DialogResult = true;
    }
}
