using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using FieldProbe.Core;

namespace FieldProbe.App.Views;

/// <summary>Procura, sob demanda, quais DBs (S7) ou registradores (Modbus) existem no equipamento.</summary>
public partial class AddressScanWindow : Window
{
    private readonly IAddressScanner _scanner;
    private readonly ObservableCollection<FoundRow> _rows = new();
    private CancellationTokenSource? _cts;

    public AddressScanWindow(IAddressScanner scanner, string connectionName)
    {
        InitializeComponent();
        _scanner = scanner;
        Title = $"{scanner.AddressScanTitle} · {connectionName}";
        HeaderText.Text = $"{scanner.AddressScanTitle} · {connectionName}";
        HelpText.Text = scanner.AddressScanHelp;
        RangeBox.Text = scanner.DefaultScanRange;
        ResultsGrid.ItemsSource = _rows;
        Closing += (_, _) => _cts?.Cancel();
        UpdateSelection();
    }

    /// <summary>Blocos marcados quando o usuário clicou em "Usar na árvore".</summary>
    public IReadOnlyList<AddressBlock>? Selected { get; private set; }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            return;
        }

        _rows.Clear();
        UpdateSelection();
        Progress.Value = 0;
        StatusText.Text = "Procurando... (pode parar a qualquer momento)";
        StartButton.Content = "Parar";
        RangeBox.IsEnabled = false;
        _cts = new CancellationTokenSource();
        var started = DateTime.Now;

        var progress = new Progress<double>(p => Progress.Value = p);
        void OnFound(AddressBlock block) => Dispatcher.InvokeAsync(() =>
        {
            _rows.Add(new FoundRow(block));
            UpdateSelection();
        });

        try
        {
            var token = _cts.Token;
            var range = RangeBox.Text; // lido aqui: controles da tela só podem ser acessados na thread da interface
            await Task.Run(() => _scanner.ScanAddressesAsync(range, progress, OnFound, token), token);
            await Dispatcher.InvokeAsync(() => { }); // garante que os últimos resultados entraram na lista
            var seconds = (DateTime.Now - started).TotalSeconds;
            var ok = _rows.Count(r => r.Block.Accessible);
            StatusText.Text = _rows.Count == 0
                ? $"Nada encontrado nessa faixa ({seconds:0.0} s). Tente uma faixa maior ou confira rack/slot/unit ID."
                : $"Concluído em {seconds:0.0} s · {ok} legível(is), {_rows.Count - ok} sem acesso.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"Busca interrompida · {_rows.Count} resultado(s) até aqui.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Erro: {ex.Message}";
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            StartButton.Content = "Procurar";
            RangeBox.IsEnabled = true;
            UpdateSelection();
        }
    }

    private void Check_Click(object sender, RoutedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        var count = _rows.Count(r => r.IsSelected);
        SelectionText.Text = _rows.Count == 0 ? string.Empty : $"{count} de {_rows.Count} marcado(s) para a árvore";
        ApplyButton.IsEnabled = count > 0 && _cts is null;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        Selected = _rows.Where(r => r.IsSelected).Select(r => r.Block).ToList();
        DialogResult = true;
    }

    public sealed class FoundRow : INotifyPropertyChanged
    {
        private bool _isSelected;

        public FoundRow(AddressBlock block)
        {
            Block = block;
            // DBs sem acesso também vão para a árvore (aparecem com o aviso); área Modbus não suportada não.
            _isSelected = block.Accessible || block.Area == "DB";
        }

        public AddressBlock Block { get; }

        public string StateText => Block.Accessible ? "✔ legível" : "sem acesso";

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
