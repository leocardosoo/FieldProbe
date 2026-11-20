using System.Windows;
using System.Windows.Controls;
using FieldProbe.App.ViewModels;
using FieldProbe.Core;

namespace FieldProbe.App.Views;

/// <summary>Cria ou edita uma variável do usuário numa pasta da árvore (DB de S7, área Modbus).</summary>
public partial class VariableDialog : Window
{
    private readonly ConnectionViewModel _connection;
    private readonly string _folderKey;
    private readonly Func<string, bool> _nameInUse;
    private readonly bool _isModbus;

    public VariableDialog(ConnectionViewModel connection, string folderKey, string folderTitle, CustomTag? existing,
        string addressHint, Func<string, bool> nameInUse)
    {
        InitializeComponent();
        _connection = connection;
        _folderKey = folderKey;
        _nameInUse = nameInUse;
        _isModbus = connection.Protocol.Contains("Modbus", StringComparison.OrdinalIgnoreCase);

        Title = existing is null ? "Adicionar variável" : "Editar variável";
        SaveButton.Content = existing is null ? "Adicionar" : "Salvar";
        HeaderText.Text = $"{Title} · {folderTitle}";
        SubHeaderText.Text = _isModbus
            ? "Use o endereço e o tipo do manual do equipamento (ex.: HR:100, Float32). Valores de 32 bits ocupam 2 registradores."
            : "Use o endereço e o tipo do projeto do TIA Portal (coluna Offset do DB). Ex.: DB1.DBD4 = Real, DB1.DBW2 = Int, DB1.DBX0.0 = Bool.";

        TypeBox.ItemsSource = Enum.GetValues<DataType>().Where(t => t != DataType.Auto).ToArray();
        OrderBox.ItemsSource = Enum.GetValues<ByteOrder>();

        NameBox.Text = existing?.Name ?? string.Empty;
        AddressBox.Text = existing?.Address ?? addressHint;
        TypeBox.SelectedItem = existing?.DataType ?? GuessType(addressHint);
        OrderBox.SelectedItem = existing?.ByteOrder ?? ByteOrder.ABCD;
        LengthBox.Text = (existing is { Length: > 1 } ? existing.Length : 20).ToString();

        OrderLabel.Visibility = OrderBox.Visibility = _isModbus ? Visibility.Visible : Visibility.Collapsed;
        UpdateLength();
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public CustomTag? Result { get; private set; }

    /// <summary>Tipo provável pelo formato do endereço (DBX = Bool, DBW = Int16, DBD = Float32...).</summary>
    private static DataType GuessType(string address)
    {
        var a = address.ToUpperInvariant();
        if (a.Contains(".DBX") || a.StartsWith("CO:") || a.StartsWith("DI:") ||
            (a.Length > 1 && "MIQE".Contains(a[0]) && a.Contains('.') && !a.Contains("DB")))
            return DataType.Bool;
        if (a.Contains(".DBW") || a.StartsWith("MW") || a.StartsWith("IW") || a.StartsWith("QW")) return DataType.Int16;
        if (a.Contains(".DBB") || a.StartsWith("MB")) return DataType.Byte;
        if (a.StartsWith("HR:") || a.StartsWith("IR:")) return DataType.UInt16;
        return DataType.Float32;
    }

    private void TypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateLength();

    private void UpdateLength()
    {
        var isString = TypeBox.SelectedItem is DataType.String;
        LengthLabel.Visibility = LengthBox.Visibility = isString ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Monta a variável a partir dos campos; devolve nulo e mostra o erro se algo estiver inválido.</summary>
    private CustomTag? BuildTag(bool checkName)
    {
        ErrorText.Text = string.Empty;
        var name = NameBox.Text.Trim();
        var address = AddressBox.Text.Trim();
        var type = TypeBox.SelectedItem is DataType t ? t : DataType.Float32;

        if (checkName && name.Length == 0) return Fail("Informe um nome para a variável.");
        if (checkName && _nameInUse(name)) return Fail($"Já existe uma variável '{name}' nesta pasta.");
        if (address.Length == 0) return Fail("Informe o endereço.");

        // Variável de um DB precisa estar nesse DB
        if (_folderKey.StartsWith("DB", StringComparison.OrdinalIgnoreCase) &&
            !address.StartsWith(_folderKey + ".", StringComparison.OrdinalIgnoreCase))
            return Fail($"O endereço precisa ser do {_folderKey} (ex.: {_folderKey}.DBD0).");

        var length = 1;
        if (type == DataType.String && (!int.TryParse(LengthBox.Text.Trim(), out length) || length is < 1 or > 254))
            return Fail("Tamanho da String: de 1 a 254 caracteres.");

        var order = _isModbus && OrderBox.SelectedItem is ByteOrder o ? o : ByteOrder.ABCD;
        return new CustomTag(_folderKey, name.Length > 0 ? name : address, address, type, order, length);

        CustomTag? Fail(string message)
        {
            ErrorText.Text = message;
            return null;
        }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var tag = BuildTag(checkName: false);
        if (tag is null) return;
        if (!_connection.IsOpen)
        {
            TestText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
            TestText.Text = "Conecte para testar a leitura.";
            return;
        }

        TestButton.IsEnabled = false;
        TestText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        TestText.Text = "Lendo...";
        try
        {
            var value = await _connection.Driver.ReadAsync(tag.ToAddress());
            var ok = value.Quality == Quality.Good;
            TestText.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "GoodBrush" : "BadBrush");
            TestText.Text = ok ? $"✔ {FormatValue(value.Value)}" : $"✖ {value.Error}";
        }
        catch (Exception ex)
        {
            TestText.Foreground = (System.Windows.Media.Brush)FindResource("BadBrush");
            TestText.Text = $"✖ {ex.Message}";
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "(nulo)",
        float f => f.ToString("0.###"),
        double d => d.ToString("0.###"),
        _ => value.ToString() ?? string.Empty
    };

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var tag = BuildTag(checkName: true);
        if (tag is null) return;
        Result = tag;
        DialogResult = true;
    }
}
