using System.Windows;
using System.Windows.Input;
using FieldProbe.Core;
using FieldProbe.Core.Codec;

namespace FieldProbe.App.Views;

/// <summary>Lê os registradores brutos e mostra todas as interpretações possíveis.</summary>
public partial class TypeDetectorWindow : Window
{
    private readonly IRegisterInspector _inspector;
    private readonly string _address;

    public TypeDetectorWindow(IRegisterInspector inspector, string address, string title)
    {
        InitializeComponent();
        _inspector = inspector;
        _address = address;
        HeaderText.Text = $"Detectar tipo · {title}";
        Loaded += async (_, _) => await LoadAsync();
    }

    public TypeInterpretation? Selected { get; private set; }

    private async Task LoadAsync()
    {
        StatusText.Text = "Lendo registradores...";
        RawText.Text = string.Empty;
        ResultsGrid.ItemsSource = null;

        try
        {
            ushort[]? registers = null;
            foreach (var count in new[] { 4, 2, 1 })
            {
                try
                {
                    registers = await _inspector.ReadRawRegistersAsync(_address, count);
                    break;
                }
                catch (Exception) when (count > 1)
                {
                    // perto do fim da área o device pode recusar 4 registradores: tenta menos
                }
            }

            var regs = registers!;
            RawText.Text = "Registradores brutos:  " + string.Join("   ", regs.Select((r, i) => $"[{i}] {r}  (0x{r:X4})"));

            var items = TypeDetector.Interpret(regs).OrderByDescending(x => x.Plausible).ToList();
            ResultsGrid.ItemsSource = items;
            StatusText.Text = $"{items.Count(x => x.Plausible)} interpretações plausíveis de {items.Count}. Lido em {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Falha na leitura: {ex.Message}";
        }
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not TypeInterpretation selected)
        {
            StatusText.Text = "Selecione uma linha.";
            return;
        }
        Selected = selected;
        DialogResult = true;
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsGrid.SelectedItem is TypeInterpretation) Use_Click(sender, e);
    }
}
