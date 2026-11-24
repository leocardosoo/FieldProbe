using System.Windows;
using FieldProbe.App.ViewModels;
using FieldProbe.App.Views;
using FieldProbe.Core;
using FieldProbe.Core.Codec;
using Microsoft.Win32;

namespace FieldProbe.App.Services;

/// <summary>Abre janelas e diálogos a partir dos ViewModels.</summary>
public static class DialogService
{
    private static Window? Owner => Application.Current?.MainWindow;

    public static ConnectionSettings? EditConnection(ConnectionSettings? existing, Func<string, bool> nameInUse, int connectionCount)
    {
        var vm = new ConnectionEditorViewModel(existing, nameInUse, connectionCount);
        var dialog = new ConnectionDialog(vm) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private static HmiWindow? _hmi;

    /// <summary>Abre a tela de teste (IHM). Janela própria, fora da principal, para poder ficar em outro monitor.</summary>
    public static void ShowHmi(MainViewModel main)
    {
        if (_hmi is not null)
        {
            if (_hmi.WindowState == WindowState.Minimized) _hmi.WindowState = WindowState.Normal;
            _hmi.Activate();
            return;
        }
        _hmi = new HmiWindow(main);
        _hmi.Closed += (_, _) => _hmi = null;
        _hmi.Show();
    }

    public static void ShowPacket(LogEntry entry, DecodedFrame frame)
    {
        new PacketDecoderWindow(entry, frame) { Owner = Owner }.Show();
    }

    public static void ShowDiagnostics(string title, IReadOnlyList<DiagnosticItem> items)
    {
        new DiagnosticsWindow(title, items) { Owner = Owner }.Show();
    }

    public static CustomTag? EditVariable(ConnectionViewModel connection, string folderKey, string folderTitle,
        CustomTag? existing, string addressHint, Func<string, bool> nameInUse)
    {
        var dialog = new VariableDialog(connection, folderKey, folderTitle, existing, addressHint, nameInUse) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public static IReadOnlyList<AddressBlock>? ScanAddresses(IAddressScanner scanner, string connectionName)
    {
        var dialog = new AddressScanWindow(scanner, connectionName) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Selected : null;
    }

    public static bool EditWatchItem(WatchItemViewModel item)
    {
        var dialog = new WatchItemDialog(new WatchItemEditorViewModel(item)) { Owner = Owner };
        return dialog.ShowDialog() == true;
    }

    public static TypeInterpretation? DetectType(IRegisterInspector inspector, string address, string title)
    {
        var active = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Owner;
        var window = new TypeDetectorWindow(inspector, address, title) { Owner = active };
        return window.ShowDialog() == true ? window.Selected : null;
    }

    public static ScannerWindow ShowScanner(ScannerViewModel viewModel)
    {
        var window = new ScannerWindow(viewModel) { Owner = Owner };
        window.Show();
        return window;
    }

    public static void ShowAbout() => new AboutWindow { Owner = Owner }.ShowDialog();

    public static bool Confirm(string message, string title = "FieldProbe") =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public static MessageBoxResult AskSave(string message) =>
        MessageBox.Show(Owner!, message, "FieldProbe", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

    public static void Info(string message, string title = "FieldProbe") =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static void Error(string message, string title = "FieldProbe") =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public static string? SaveFile(string filter, string fileName)
    {
        var dialog = new SaveFileDialog { Filter = filter, FileName = fileName, AddExtension = true };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public static string? OpenFile(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }
}
