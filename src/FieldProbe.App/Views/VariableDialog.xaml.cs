using System.Windows;
using System.Windows.Controls;
using FieldProbe.App.ViewModels;
using System.Text.RegularExpressions;
using FieldProbe.Core;
using FieldProbe.Drivers.Mitsubishi;
using FieldProbe.Drivers.Modbus;
using FieldProbe.Drivers.Omron;

namespace FieldProbe.App.Views;

/// <summary>Cria ou edita uma variável do usuário numa pasta da árvore (DB de S7, área Modbus, dispositivo Mitsubishi, área Omron).</summary>
public partial class VariableDialog : Window
{
    private readonly ConnectionViewModel _connection;
    private readonly string _folderKey;   // vazio = endereço livre (a pasta sai do endereço)
    private readonly Func<string, bool> _nameInUse;
    private readonly bool _isModbus;
    private readonly bool _isMelsec;
    private readonly bool _isFins;
    private readonly McSeries _series;
    private readonly int? _dbSize;

    public VariableDialog(ConnectionViewModel connection, string folderKey, string folderTitle, CustomTag? existing,
        string addressHint, Func<string, bool> nameInUse)
    {
        InitializeComponent();
        _connection = connection;
        _folderKey = folderKey;
        _nameInUse = nameInUse;
        _isModbus = connection.Protocol.Contains("Modbus", StringComparison.OrdinalIgnoreCase);
        _isMelsec = connection.Protocol.StartsWith("Mitsubishi", StringComparison.OrdinalIgnoreCase);
        _isFins = connection.Protocol.StartsWith("Omron", StringComparison.OrdinalIgnoreCase);
        _series = MitsubishiDriver.ParseSeries(connection.Settings.GetOption("Series", MitsubishiDrivers.SeriesQ));
        _dbSize = FindDbSize(connection.Settings, folderKey);

        Title = existing is null ? "Adicionar variável" : "Editar variável";
        SaveButton.Content = existing is null ? "Adicionar" : "Salvar";
        HeaderText.Text = folderKey.Length == 0 ? $"{Title} · {connection.Name}" : $"{Title} · {folderTitle}";
        SubHeaderText.Text = _isModbus
            ? "Use o endereço e o tipo do manual do equipamento (ex.: HR:100, Float32). Valores de 32 bits ocupam 2 registradores."
            : _isMelsec
                ? "Use o endereço e o tipo do projeto do GX Works. Ex.: D100 = Int16, D200 = Float32 (usa D200 e D201), M10 = Bool, D100.5 = bit 5 do D100." +
                  (_series == McSeries.IqF ? " No iQ-F, X e Y são octais (X0 a X7, X10...)." : string.Empty)
            : _isFins
                ? "Use o endereço e o tipo do projeto do CX-Programmer. Ex.: D100 = INT, D200 = REAL/Float32 (usa D200 e D201), D100.05 = bit 5 do D100, 0.00 = entrada, 100.00 = saída."
                : "Use o endereço e o tipo do projeto do TIA Portal (coluna Offset do DB). Ex.: DB1.DBD4 = Real, DB1.DBW2 = Int, DB1.DBX0.0 = Bool.";
        if (_dbSize is int size && size > 0)
        {
            var limits = $"{folderKey} tem {size} bytes (offset 0 a {size - 1}): último bit {folderKey}.DBX{size - 1}.7";
            if (size >= 2) limits += $" · último Int {folderKey}.DBW{size - 2}";
            if (size >= 4) limits += $" · último Real/DInt {folderKey}.DBD{size - 4}";
            SubHeaderText.Text += "\n" + limits + ".";
        }

        TypeBox.ItemsSource = Enum.GetValues<DataType>().Where(t => t != DataType.Auto).ToArray();
        OrderBox.ItemsSource = Enum.GetValues<ByteOrder>();

        NameBox.Text = existing?.Name ?? string.Empty;
        AddressBox.Text = existing?.Address ?? addressHint;
        TypeBox.SelectedItem = existing?.DataType ?? (_isMelsec ? GuessMelsecType(addressHint) : _isFins ? GuessFinsType(addressHint) : GuessType(addressHint));
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

    /// <summary>Mitsubishi: bit para M/X/Y/B... e para D100.5; Int16 (padrão do GX Works) para D/W/R/TN/CN/SD.</summary>
    private DataType GuessMelsecType(string address) =>
        McAddress.TryParse(address, _series, out var a) && (a!.Device.IsBit || a.IsWordBit) ? DataType.Bool : DataType.Int16;

    /// <summary>Omron: bit para endereços com ".bit" (D100.05, 0.00); Int16 para palavras.</summary>
    private static DataType GuessFinsType(string address) =>
        FinsAddress.TryParse(address, out var a) && a!.IsBit ? DataType.Bool : DataType.Int16;

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
        if (address.Length == 0) return Fail("Informe o endereço.");

        // Endereço livre: a pasta é descoberta pelo endereço (DB12.DBD0 -> DB12, M10.0 -> M, HR:100 -> HR)
        var folder = _folderKey.Length > 0 ? _folderKey : FolderFromAddress(address);
        if (folder is null)
            return Fail(_isModbus
                ? "Endereço Modbus inválido. Ex.: HR:100, IR:5, CO:0, DI:3 (ou 40101, 30006...)."
                : _isMelsec
                    ? "Endereço Mitsubishi inválido. Ex.: D100, M10, X1F, Y20, W1A, R0, TN0, CN0, SM400, SD203, D100.5" +
                      (_series == McSeries.IqF ? " (no iQ-F, X e Y são octais: só dígitos de 0 a 7)." : ".")
                    : _isFins
                        ? "Endereço Omron inválido. Ex.: D100, D100.05, 0.00, 100.00, CIO100, W10.03, H5, A100, T0, C0, E0_100."
                        : "Endereço S7 inválido. Ex.: DB12.DBD4, DB1.DBX0.0, MW10, M0.1, I0.0, QW2.");
        if (checkName && _nameInUse(name)) return Fail($"Já existe uma variável '{name}' em {folder}.");

        // Mitsubishi: o endereço precisa ser válido e da mesma pasta (D100 na pasta D, M10 na pasta M...)
        if (_isMelsec && _folderKey.Length > 0)
        {
            var addressFolder = FolderFromAddress(address);
            if (addressFolder is null) return Fail($"Endereço Mitsubishi inválido: '{address}'. Ex.: D100, M10, X1F, D100.5.");
            if (!string.Equals(addressFolder, _folderKey, StringComparison.OrdinalIgnoreCase))
                return Fail($"{address} é da pasta {addressFolder}, não da {_folderKey}. Adicione na pasta certa ou clique na conexão para usar endereço livre.");
            if (type == DataType.String && McAddress.TryParse(address, _series, out var stringAddress) && (stringAddress!.Device.IsBit || stringAddress.IsWordBit))
                return Fail("String só faz sentido em registradores de palavra (D, W, R...).");
        }

        // Omron: o endereço precisa ser válido e da mesma pasta (D100 na pasta D, 0.00 na pasta CIO...)
        if (_isFins && _folderKey.Length > 0)
        {
            var addressFolder = FolderFromAddress(address);
            if (addressFolder is null) return Fail($"Endereço Omron inválido: '{address}'. Ex.: D100, D100.05, 0.00, W10.03.");
            if (!string.Equals(addressFolder, _folderKey, StringComparison.OrdinalIgnoreCase))
                return Fail($"{address} é da pasta {addressFolder}, não da {_folderKey}. Adicione na pasta certa ou clique na conexão para usar endereço livre.");
            if (type == DataType.String && FinsAddress.TryParse(address, out var finsString) && finsString!.IsBit)
                return Fail("String só faz sentido em palavras (D100, W10...), não em bits.");
        }

        // Variável de um DB precisa estar nesse DB
        if (_folderKey.StartsWith("DB", StringComparison.OrdinalIgnoreCase) &&
            !address.StartsWith(_folderKey + ".", StringComparison.OrdinalIgnoreCase))
            return Fail($"O endereço precisa ser do {_folderKey} (ex.: {_folderKey}.DBD0).");

        var length = 1;
        if (type == DataType.String && (!int.TryParse(LengthBox.Text.Trim(), out length) || length is < 1 or > 254))
            return Fail("Tamanho da String: de 1 a 254 caracteres." + (_isMelsec || _isFins ? " Cada palavra guarda 2 caracteres." : string.Empty));

        // Tamanho do DB conhecido (Procurar DBs): avisa antes de ler se o endereço passa do fim
        if (_dbSize is int dbSize && DbRange(address, type, length) is { } range && range.Start + range.Bytes > dbSize)
            return Fail($"{_folderKey} tem {dbSize} bytes (offset 0 a {dbSize - 1}). " +
                        $"{address} ({type}) ocupa os bytes {range.Start} a {range.Start + range.Bytes - 1}: passa do fim do DB." +
                        (dbSize >= 4 ? $" O último Real/DInt possível é {_folderKey}.DBD{dbSize - 4}." : string.Empty));

        var order = _isModbus && OrderBox.SelectedItem is ByteOrder o ? o : ByteOrder.ABCD;
        return new CustomTag(folder, name.Length > 0 ? name : address, address, type, order, length);

        CustomTag? Fail(string message)
        {
            ErrorText.Text = message;
            return null;
        }
    }

    /// <summary>Pasta da árvore em que o endereço se encaixa, ou nulo se o endereço não for reconhecido.</summary>
    private string? FolderFromAddress(string address)
    {
        if (_isMelsec) return McAddress.FolderOf(address, _series);
        if (_isFins) return FinsAddress.FolderOf(address);

        if (_isModbus)
        {
            try
            {
                return ModbusAddress.Prefix(ModbusAddress.Parse(address).Area);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        var a = address.Trim().ToUpperInvariant();
        var db = Regex.Match(a, @"^DB(\d+)\.DB[XBWD]\d+");
        if (db.Success && int.TryParse(db.Groups[1].Value, out var number) && number > 0) return $"DB{number}";
        if (Regex.IsMatch(a, @"^M[BWD]?\d+(\.[0-7])?$")) return "M";
        if (Regex.IsMatch(a, @"^[IE][BWD]?\d+(\.[0-7])?$")) return "I";
        if (Regex.IsMatch(a, @"^[QA][BWD]?\d+(\.[0-7])?$")) return "Q";
        return null;
    }

    /// <summary>Tamanho do DB guardado pelo "Procurar DBs" (opção DbSizes = "1:48,2:20"), se houver.</summary>
    private static int? FindDbSize(ConnectionSettings settings, string folderKey)
    {
        if (!folderKey.StartsWith("DB", StringComparison.OrdinalIgnoreCase) || !int.TryParse(folderKey[2..], out var db)) return null;
        if (!settings.Options.TryGetValue("DbSizes", out var text)) return null;
        foreach (var pair in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out var n) && n == db && int.TryParse(parts[1], out var size))
                return size;
        }
        return null;
    }

    /// <summary>Primeiro byte e quantidade de bytes de um endereço de DB (DBX/DBB/DBW/DBD). Nulo se não reconhecer.</summary>
    private static (int Start, int Bytes)? DbRange(string address, DataType type, int length)
    {
        var dot = address.IndexOf('.');
        if (dot < 0 || address.Length < dot + 5) return null;
        var kind = address.Substring(dot + 1, 3).ToUpperInvariant();
        var rest = address[(dot + 4)..];
        var number = rest.Split('.')[0];
        if (!int.TryParse(number, out var start)) return null;

        var bytes = type switch
        {
            DataType.String => length + 2,
            DataType.Float64 or DataType.Int64 => 8,
            _ => kind switch { "DBX" or "DBB" => 1, "DBW" => 2, "DBD" => 4, _ => 0 }
        };
        return bytes == 0 ? null : (start, bytes);
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
