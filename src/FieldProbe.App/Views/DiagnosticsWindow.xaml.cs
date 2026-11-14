using System.Text;
using System.Windows;
using FieldProbe.Core;

namespace FieldProbe.App.Views;

public partial class DiagnosticsWindow : Window
{
    private readonly string _title;
    private readonly IReadOnlyList<DiagnosticItem> _items;

    public DiagnosticsWindow(string title, IReadOnlyList<DiagnosticItem> items)
    {
        InitializeComponent();
        _title = title;
        _items = items;
        Title = title;
        HeaderText.Text = title;
        ResultsGrid.RowHeight = double.NaN; // permite quebra de linha nas mensagens
        ResultsGrid.ItemsSource = items;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine(_title);
        sb.AppendLine($"Gerado em {DateTime.Now:dd/MM/yyyy HH:mm:ss} pelo FieldProbe");
        sb.AppendLine();
        foreach (var item in _items)
            sb.AppendLine($"[{item.Severity}] {item.Category}: {item.Message}");
        Clipboard.SetText(sb.ToString());
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
