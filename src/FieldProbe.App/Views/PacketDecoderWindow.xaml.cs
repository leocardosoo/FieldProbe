using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using FieldProbe.App.ViewModels;
using FieldProbe.Core;

namespace FieldProbe.App.Views;

/// <summary>"Mini Wireshark": explica, campo por campo, um pacote TX/RX do log de comunicação.</summary>
public partial class PacketDecoderWindow : Window
{
    private const int MaxHexBytes = 4096;

    private readonly byte[] _data;
    private readonly DecodedFrame _frame;
    private readonly List<FieldRow> _rows = new();
    private readonly List<ByteCell> _cells = new();

    public PacketDecoderWindow(LogEntry entry, DecodedFrame frame)
    {
        InitializeComponent();
        _data = entry.Data ?? Array.Empty<byte>();
        _frame = frame;

        var direction = entry.Kind == "TX" ? "TX · enviado pelo FieldProbe" : "RX · recebido do equipamento";
        Title = $"Decodificar pacote · {frame.Protocol}";
        HeaderText.Text = $"{frame.Protocol} · {(entry.Kind == "TX" ? "pedido" : "resposta")}";
        InfoText.Text = $"{entry.Time} · {entry.Source} · {direction} · {_data.Length} bytes";
        SummaryText.Text = frame.Summary.Length > 0 ? frame.Summary : entry.Message;
        WarningsList.ItemsSource = frame.Warnings;

        foreach (var layer in frame.Layers)
        {
            _rows.Add(FieldRow.ForLayer(layer.Name));
            foreach (var field in layer.Fields)
                _rows.Add(FieldRow.ForField(field, _data));
        }
        FieldsGrid.ItemsSource = _rows;

        var shown = Math.Min(_data.Length, MaxHexBytes);
        var hexRows = new List<HexRow>();
        for (var start = 0; start < shown; start += 16)
        {
            var cells = new List<ByteCell>();
            for (var i = start; i < Math.Min(start + 16, shown); i++)
            {
                var cell = new ByteCell(i, _data[i], FieldAt(i)?.Name);
                cells.Add(cell);
                _cells.Add(cell);
            }
            hexRows.Add(new HexRow($"{start:X4}", cells, FrameCursor.Printable(_data.AsSpan(start, cells.Count))));
        }
        HexRows.ItemsSource = hexRows;
    }

    /// <summary>Campo mais específico (mais interno) que cobre o byte.</summary>
    private DecodedField? FieldAt(int offset) => _frame.Layers
        .SelectMany(l => l.Fields)
        .Where(f => f.Length > 0 && offset >= f.Offset && offset < f.Offset + f.Length)
        .OrderBy(f => f.Length)
        .FirstOrDefault();

    private void FieldsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = FieldsGrid.SelectedItem as FieldRow;
        foreach (var cell in _cells)
            cell.IsHighlighted = row?.Field is { Length: > 0 } f && cell.Offset >= f.Offset && cell.Offset < f.Offset + f.Length;
    }

    private void Cell_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ByteCell cell || FieldAt(cell.Offset) is not { } field) return;
        var row = _rows.FirstOrDefault(r => ReferenceEquals(r.Field, field));
        if (row is null) return;
        FieldsGrid.SelectedItem = row;
        FieldsGrid.ScrollIntoView(row);
    }

    private void CopyHex_Click(object sender, RoutedEventArgs e) =>
        Clipboard.SetText(string.Join(" ", _data.Select(b => b.ToString("X2"))));

    private void CopyReport_Click(object sender, RoutedEventArgs e) =>
        Clipboard.SetText($"{InfoText.Text}\n{_frame.ToReport(_data)}");

    // ===================== linhas da tela =====================

    public sealed class FieldRow
    {
        public DecodedField? Field { get; private init; }

        public bool IsLayer { get; private init; }

        public string OffsetText { get; private init; } = string.Empty;

        public string BytesText { get; private init; } = string.Empty;

        public string NameText { get; private init; } = string.Empty;

        public string Value { get; private init; } = string.Empty;

        public string Note { get; private init; } = string.Empty;

        public static FieldRow ForLayer(string name) => new() { IsLayer = true, NameText = name };

        public static FieldRow ForField(DecodedField field, byte[] data)
        {
            var bytes = field.Length > 0 && field.Offset + field.Length <= data.Length
                ? FrameCursor.HexOf(data.AsSpan(field.Offset, field.Length), 8)
                : string.Empty;
            return new FieldRow
            {
                Field = field,
                OffsetText = field.Length > 0 ? field.Offset.ToString() : string.Empty,
                BytesText = bytes,
                NameText = new string(' ', 2 + field.Level * 3) + field.Name,
                Value = field.Value,
                Note = field.Note
            };
        }
    }

    public sealed record HexRow(string OffsetText, List<ByteCell> Cells, string Ascii);

    public sealed class ByteCell : INotifyPropertyChanged
    {
        private bool _isHighlighted;

        public ByteCell(int offset, byte value, string? fieldName)
        {
            Offset = offset;
            Text = value.ToString("X2");
            ToolTip = $"byte {offset} = 0x{value:X2} ({value}){(fieldName is null ? string.Empty : $"\n{fieldName}")}";
        }

        public int Offset { get; }

        public string Text { get; }

        public string ToolTip { get; }

        public bool IsHighlighted
        {
            get => _isHighlighted;
            set
            {
                if (_isHighlighted == value) return;
                _isHighlighted = value;
                OnChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
