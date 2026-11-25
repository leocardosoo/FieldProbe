using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FieldProbe.App.Hmi;

namespace FieldProbe.App.Views;

/// <summary>Editor profissional (desfazer, copiar, alinhar, zoom) e recursos de SCADA (alarmes, faceplate, usuários).</summary>
public partial class HmiWindow
{
    private const int MaxUndo = 100;
    private const int MaxElementImageBytes = 4 * 1024 * 1024;

    // Área de transferência interna: copiar numa tela e colar em outra (ou em outra janela da IHM)
    private static List<HmiElementData> _clipboard = new();

    private static readonly JsonSerializerOptions UndoJson = new();

    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private string? _lastState;
    private bool _restoring;
    private double _editZoom = 1;

    public sealed record ZoomOption(int Percent)
    {
        public override string ToString() => $"{Percent}%";
    }

    // ===================== Desfazer / refazer =====================

    private string Snapshot() => JsonSerializer.Serialize(_screen?.Elements ?? new List<HmiElementData>(), UndoJson);

    private void ResetUndo()
    {
        _undoTimer.Stop();
        _undo.Clear();
        _redo.Clear();
        _lastState = _screen is null ? null : Snapshot();
        UpdateUndoButtons();
    }

    /// <summary>Cada alteração reinicia um pequeno atraso: arrastar ou digitar vira um passo só de desfazer.</summary>
    private void OnElementChanged()
    {
        _main.MarkDirty();
        if (_restoring || _running) return;
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    /// <summary>Fecha o passo de desfazer pendente (chamado também depois de adicionar, apagar, colar, alinhar...).</summary>
    private void FlushUndo()
    {
        _undoTimer.Stop();
        if (_screen is null || _restoring) return;
        var now = Snapshot();
        if (now == _lastState) return;
        if (_lastState is not null)
        {
            _undo.Push(_lastState);
            if (_undo.Count > MaxUndo)
            {
                var keep = _undo.Take(MaxUndo).Reverse().ToList();
                _undo.Clear();
                foreach (var state in keep) _undo.Push(state);
            }
        }
        _redo.Clear();
        _lastState = now;
        UpdateUndoButtons();
    }

    private void Undo()
    {
        FlushUndo();
        if (_undo.Count == 0 || _lastState is null) return;
        _redo.Push(_lastState);
        Restore(_undo.Pop());
        StatusText.Text = "Desfeito.";
    }

    private void Redo()
    {
        FlushUndo();
        if (_redo.Count == 0 || _lastState is null) return;
        _undo.Push(_lastState);
        Restore(_redo.Pop());
        StatusText.Text = "Refeito.";
    }

    private void Restore(string json)
    {
        if (_screen is null) return;
        _restoring = true;
        try
        {
            var list = JsonSerializer.Deserialize<List<HmiElementData>>(json, UndoJson) ?? new List<HmiElementData>();
            _screen.Elements.Clear();
            _screen.Elements.AddRange(list);
            _elements.Clear();
            foreach (var data in list) _elements.Add(Wrap(data));
            _lastState = json;
            Select(null);
        }
        finally
        {
            _restoring = false;
        }
        _main.MarkDirty();
        UpdateUndoButtons();
        UpdateStatus();
    }

    private void UpdateUndoButtons()
    {
        UndoButton.IsEnabled = _undo.Count > 0 || _undoTimer.IsEnabled;
        RedoButton.IsEnabled = _redo.Count > 0;
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    // ===================== Copiar / colar =====================

    private void Copy_Click(object sender, RoutedEventArgs e) => CopySelection();

    private void Paste_Click(object sender, RoutedEventArgs e) => PasteClipboard();

    private void CopySelection()
    {
        if (_selection.Count == 0) return;
        _clipboard = _selection.Select(x => x.Data.Clone()).ToList();
        StatusText.Text = $"{_clipboard.Count} elemento(s) copiado(s). Ctrl+V cola nesta ou em outra tela.";
    }

    private void PasteClipboard()
    {
        if (_clipboard.Count == 0)
        {
            StatusText.Text = "Nada copiado ainda: selecione elementos e use Ctrl+C.";
            return;
        }
        PasteData(_clipboard.Select(x => x.Clone()).ToList(), 20);
    }

    /// <summary>Insere cópias deslocadas e seleciona o que foi colado.</summary>
    private void PasteData(List<HmiElementData> items, double offset)
    {
        if (_screen is null || items.Count == 0) return;
        var pasted = new List<HmiElementViewModel>();
        foreach (var data in items)
        {
            data.X = Math.Clamp(data.X + offset, 0, Math.Max(0, Surface.Width - data.Width));
            data.Y = Math.Clamp(data.Y + offset, 0, Math.Max(0, Surface.Height - data.Height));
            data.Locked = false;
            _screen.Elements.Add(data);
            var vm = Wrap(data);
            _elements.Add(vm);
            pasted.Add(vm);
        }
        SetSelection(pasted);
        FlushUndo();
        _main.MarkDirty();
        UpdateStatus();
    }

    // ===================== Zoom =====================

    private void ZoomBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ZoomBox.SelectedItem is not ZoomOption option) return;
        _editZoom = option.Percent / 100.0;
        if (!_running) ApplyZoom(_editZoom);
    }

    private void ApplyZoom(double zoom)
    {
        ZoomTransform.ScaleX = zoom;
        ZoomTransform.ScaleY = zoom;
    }

    private void SurfaceScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_running || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || ZoomBox.ItemsSource is not List<ZoomOption> options) return;
        var index = ZoomBox.SelectedIndex + (e.Delta > 0 ? 1 : -1);
        ZoomBox.SelectedIndex = Math.Clamp(index, 0, options.Count - 1);
        e.Handled = true;
    }

    // ===================== Imagem do elemento =====================

    private void PickElementImage(HmiElementViewModel vm)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Imagem do elemento",
            Filter = "Imagens (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Todos os arquivos (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var bytes = File.ReadAllBytes(dialog.FileName);
            if (bytes.Length > MaxElementImageBytes)
            {
                StatusText.Text = $"Imagem grande demais ({bytes.Length / 1024 / 1024} MB). Use uma de até {MaxElementImageBytes / 1024 / 1024} MB.";
                return;
            }
            LoadImage(bytes); // valida o formato antes de guardar
            vm.SetImage(Convert.ToBase64String(bytes));
            FlushUndo();
            StatusText.Text = $"Imagem: {Path.GetFileName(dialog.FileName)} ({bytes.Length / 1024} KB, guardada no projeto).";
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or FormatException)
        {
            StatusText.Text = $"Não foi possível abrir a imagem: {ex.Message}";
        }
    }

    // ===================== Faixa de alarmes =====================

    private void UpdateAlarmBanner()
    {
        if (!_running) return;
        var active = _main.WatchItems.Count(w => w.IsInAlarm);
        var unacknowledged = _main.Alarms.Count(a => !a.Acknowledged);
        var latest = _main.Alarms.FirstOrDefault(a => !a.IsReturnToNormal);

        if (active == 0 && unacknowledged == 0)
        {
            AlarmBanner.Background = Brushes.SeaGreen;
            AlarmBannerText.Text = "✔  Sem alarmes ativos";
            AckButton.Visibility = Visibility.Collapsed;
            return;
        }

        AlarmBanner.Background = new SolidColorBrush(unacknowledged > 0 ? Color.FromRgb(0xCF, 0x22, 0x2E) : Color.FromRgb(0x9A, 0x67, 0x00));
        var last = latest is null ? string.Empty : $" · último: {latest.Time}  {latest.Item} ({latest.Address}): {latest.Description} · valor {latest.Value}";
        AlarmBannerText.Text = $"⚠  {active} alarme(s) ativo(s) · {unacknowledged} não reconhecido(s){last}";
        AckButton.Visibility = unacknowledged > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Acknowledge_Click(object sender, RoutedEventArgs e)
    {
        if (_main.AcknowledgeAlarmsCommand.CanExecute(null)) _main.AcknowledgeAlarmsCommand.Execute(null);
        LogAction("RECONHECER ALARMES", null, string.Empty, "OK");
        UpdateAlarmBanner();
    }

    // ===================== Faceplate =====================

    private void OpenFaceplate(HmiElementViewModel vm)
    {
        var window = new FaceplateWindow(vm) { Owner = this };
        window.Show();
    }

    // ===================== Registro das ações do operador =====================

    private static string ActionLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FieldProbe", "ihm-acoes.csv");

    /// <summary>Grava quem fez o quê e quando (CSV com ';', abre direto no Excel).</summary>
    private void LogAction(string action, HmiElementViewModel? vm, string value, string result)
    {
        try
        {
            var path = ActionLogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var isNew = !File.Exists(path);
            static string Csv(string text) => text.Contains(';') || text.Contains('"') ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
            var line = string.Join(";", new[]
            {
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Csv(Environment.UserName),
                Csv(action),
                Csv(_screen?.Name ?? string.Empty),
                Csv(vm is null ? string.Empty : Label(vm)),
                Csv(vm?.ConnectionName ?? string.Empty),
                Csv(vm?.Address ?? string.Empty),
                Csv(value),
                Csv(result)
            });
            var text = (isNew ? "data_hora;usuario;acao;tela;elemento;conexao;endereco;valor;resultado" + Environment.NewLine : string.Empty)
                       + line + Environment.NewLine;
            File.AppendAllText(path, text, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // registro é best-effort: não pode travar a operação
        }
    }
}
