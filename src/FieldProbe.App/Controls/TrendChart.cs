using System.Collections;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FieldProbe.App.ViewModels;

namespace FieldProbe.App.Controls;

/// <summary>
/// Gráfico de tendência leve (desenho direto, sem bibliotecas) com várias séries, janela de tempo,
/// escala compartilhada ou independente, pausa, linhas de alarme e exportação em PNG.
/// </summary>
public sealed class TrendChart : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IEnumerable), typeof(TrendChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Janela de tempo em segundos (0 = todo o histórico).</summary>
    public static readonly DependencyProperty WindowSecondsProperty = DependencyProperty.Register(
        nameof(WindowSeconds), typeof(int), typeof(TrendChart),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Cada série usa a própria escala (0–100% do seu mínimo/máximo).</summary>
    public static readonly DependencyProperty IndependentScaleProperty = DependencyProperty.Register(
        nameof(IndependentScale), typeof(bool), typeof(TrendChart),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsPausedProperty = DependencyProperty.Register(
        nameof(IsPaused), typeof(bool), typeof(TrendChart),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnPausedChanged));

    private static readonly Brush BackgroundBrush = Freeze(new SolidColorBrush(Colors.White));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x65, 0x6D, 0x76)));
    private static readonly Brush StrongTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x28)));
    private static readonly Pen GridPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0xEE, 0xF0, 0xF3))), 1));
    private static readonly Pen AxisPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0xDD, 0xE1, 0xE6))), 1));
    private static readonly Pen AlarmPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0xCF, 0x22, 0x2E))), 1) { DashStyle = DashStyles.Dash });
    private static readonly Typeface Font = new("Segoe UI");
    private static readonly Typeface BoldFont = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private readonly DispatcherTimer _timer;
    private Dictionary<WatchItemViewModel, List<(DateTime Time, double Value)>>? _frozen;
    private DateTime _frozenAt;

    public TrendChart()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) =>
        {
            if (!IsPaused && IsVisible) InvalidateVisual();
        };
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
        ClipToBounds = true;
    }

    public IEnumerable? Items
    {
        get => (IEnumerable?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public int WindowSeconds
    {
        get => (int)GetValue(WindowSecondsProperty);
        set => SetValue(WindowSecondsProperty, value);
    }

    public bool IndependentScale
    {
        get => (bool)GetValue(IndependentScaleProperty);
        set => SetValue(IndependentScaleProperty, value);
    }

    public bool IsPaused
    {
        get => (bool)GetValue(IsPausedProperty);
        set => SetValue(IsPausedProperty, value);
    }

    private static void OnPausedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (TrendChart)d;
        if ((bool)e.NewValue)
        {
            chart._frozenAt = DateTime.Now;
            chart._frozen = chart.CurrentItems().ToDictionary(i => i, i => i.HistorySnapshot());
        }
        else
        {
            chart._frozen = null;
        }
    }

    /// <summary>Salva o gráfico atual como PNG.</summary>
    public void SaveAsPng(string path)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        var bitmap = new RenderTargetBitmap(Math.Max(1, width), Math.Max(1, height), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(this);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private List<WatchItemViewModel> CurrentItems() =>
        Items?.OfType<WatchItemViewModel>().ToList() ?? new List<WatchItemViewModel>();

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 120 || height < 70) return;

        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, width, height));

        var items = CurrentItems();
        if (items.Count == 0)
        {
            Center(dc, "Selecione um item na Data Access View para ver a tendência.", width, height);
            return;
        }

        // Dados (congelados se pausado) e janela de tempo
        var reference = _frozen is not null ? _frozenAt : DateTime.Now;
        var cutoff = WindowSeconds > 0 ? reference.AddSeconds(-WindowSeconds) : DateTime.MinValue;
        var series = items
            .Select(i => (Item: i, Data: (_frozen is not null && _frozen.TryGetValue(i, out var frozen) ? frozen : i.HistorySnapshot())
                .Where(p => p.Time >= cutoff).ToList()))
            .ToList();

        var drawable = series.Where(s => s.Data.Count >= 2).ToList();
        if (drawable.Count == 0)
        {
            var single = items.Count == 1 ? items[0] : null;
            var message = single?.Value is string
                ? "Este item é texto: a tendência mostra apenas valores numéricos ou booleanos."
                : "Aguardando valores numéricos...";
            Center(dc, message, width, height);
            return;
        }

        // Eixo de tempo comum
        var t0 = WindowSeconds > 0 ? cutoff : drawable.Min(s => s.Data[0].Time);
        var t1 = WindowSeconds > 0 ? reference : drawable.Max(s => s.Data[^1].Time);
        var span = Math.Max(1, (t1 - t0).TotalMilliseconds);

        // Legenda (uma linha por série no topo)
        var legendHeight = DrawLegend(dc, drawable, width);
        var plot = new Rect(64, 8 + legendHeight, Math.Max(10, width - 64 - 14), Math.Max(10, height - 8 - legendHeight - 24));

        // Escalas
        var independent = IndependentScale && drawable.Count > 1;
        double globalMin = 0, globalMax = 1;
        if (!independent)
        {
            globalMin = drawable.Min(s => s.Data.Min(p => p.Value));
            globalMax = drawable.Max(s => s.Data.Max(p => p.Value));
            if (drawable.Count == 1)
            {
                var item = drawable[0].Item;
                if (item.AlarmHigh.HasValue) globalMax = Math.Max(globalMax, item.AlarmHigh.Value);
                if (item.AlarmLow.HasValue) globalMin = Math.Min(globalMin, item.AlarmLow.Value);
            }
            (globalMin, globalMax) = Pad(globalMin, globalMax);
        }

        double X(DateTime t) => plot.Left + (t - t0).TotalMilliseconds / span * plot.Width;

        // Grade e eixo Y
        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Top + i * plot.Height / 4;
            dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = independent
                ? $"{100 - i * 25}%"
                : FormatValue(globalMax - i * (globalMax - globalMin) / 4);
            var text = Text(label, 10.5, TextBrush);
            dc.DrawText(text, new Point(plot.Left - text.Width - 6, y - text.Height / 2));
        }
        dc.DrawLine(AxisPen, new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));

        // Eixo X
        foreach (var fraction in new[] { 0.0, 0.5, 1.0 })
        {
            var t = t0.AddMilliseconds(span * fraction);
            var text = Text(t.ToString("HH:mm:ss"), 10.5, TextBrush);
            dc.DrawText(text, new Point(plot.Left + plot.Width * fraction - text.Width * fraction, plot.Bottom + 4));
        }

        dc.PushClip(new RectangleGeometry(plot));

        // Linhas de alarme (apenas com uma série em escala compartilhada)
        if (!independent && drawable.Count == 1)
        {
            var item = drawable[0].Item;
            foreach (var limit in new[] { item.AlarmHigh, item.AlarmLow })
            {
                if (!limit.HasValue) continue;
                var y = plot.Bottom - (limit.Value - globalMin) / (globalMax - globalMin) * plot.Height;
                dc.DrawLine(AlarmPen, new Point(plot.Left, y), new Point(plot.Right, y));
            }
        }

        // Séries
        foreach (var (item, data) in drawable)
        {
            double min = globalMin, max = globalMax;
            if (independent) (min, max) = Pad(data.Min(p => p.Value), data.Max(p => p.Value));
            double Y(double v) => plot.Bottom - (v - min) / (max - min) * plot.Height;

            var brush = item.TrendBrush;
            var pen = new Pen(brush, 1.8) { LineJoin = PenLineJoin.Round };
            pen.Freeze();

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(X(data[0].Time), Y(data[0].Value)), false, false);
                for (var i = 1; i < data.Count; i++)
                    ctx.LineTo(new Point(X(data[i].Time), Y(data[i].Value)), true, true);
            }
            geometry.Freeze();

            if (drawable.Count == 1)
            {
                // Área preenchida só com uma série (com várias poluiria o gráfico)
                var area = new StreamGeometry();
                using (var ctx = area.Open())
                {
                    ctx.BeginFigure(new Point(X(data[0].Time), plot.Bottom), true, true);
                    foreach (var p in data) ctx.LineTo(new Point(X(p.Time), Y(p.Value)), false, false);
                    ctx.LineTo(new Point(X(data[^1].Time), plot.Bottom), false, false);
                }
                area.Freeze();
                var fill = new SolidColorBrush(Color.FromArgb(0x22, item.TrendColor.R, item.TrendColor.G, item.TrendColor.B));
                fill.Freeze();
                dc.DrawGeometry(fill, null, area);
            }

            dc.DrawGeometry(null, pen, geometry);
            dc.DrawEllipse(brush, null, new Point(X(data[^1].Time), Y(data[^1].Value)), 3.5, 3.5);
        }

        dc.Pop();

        if (IsPaused)
        {
            var paused = Text("⏸ PAUSADO", 11, StrongTextBrush, bold: true);
            dc.DrawText(paused, new Point(plot.Right - paused.Width - 4, plot.Top + 4));
        }
    }

    /// <summary>Desenha a legenda (cor, nome, último valor, mín/máx) e devolve a altura usada.</summary>
    private double DrawLegend(DrawingContext dc, List<(WatchItemViewModel Item, List<(DateTime Time, double Value)> Data)> series, double width)
    {
        double x = 12, y = 4, rowHeight = 18;
        foreach (var (item, data) in series)
        {
            var last = data[^1].Value;
            var unit = item.Unit.Length > 0 ? " " + item.Unit : string.Empty;
            var name = Text(item.Label, 11.5, StrongTextBrush, bold: true);
            var detail = Text($" = {FormatValue(last)}{unit}   (mín {FormatValue(data.Min(p => p.Value))} · máx {FormatValue(data.Max(p => p.Value))})", 11, TextBrush);
            var entryWidth = 18 + name.Width + detail.Width + 20;

            if (x + entryWidth > width - 8 && x > 12)
            {
                x = 12;
                y += rowHeight;
            }

            dc.DrawRoundedRectangle(item.TrendBrush, null, new Rect(x, y + 5, 12, 4), 2, 2);
            dc.DrawText(name, new Point(x + 18, y));
            dc.DrawText(detail, new Point(x + 18 + name.Width, y + 0.5));
            x += entryWidth;
        }
        return y + rowHeight + 2;
    }

    private static (double Min, double Max) Pad(double min, double max)
    {
        if (Math.Abs(max - min) < 1e-12)
        {
            var pad = Math.Abs(max) * 0.1;
            if (pad < 1e-9) pad = 1;
            return (min - pad, max + pad);
        }
        var margin = (max - min) * 0.08;
        return (min - margin, max + margin);
    }

    private void Center(DrawingContext dc, string message, double width, double height)
    {
        var text = Text(message, 12, TextBrush);
        dc.DrawText(text, new Point(Math.Max(8, (width - text.Width) / 2), (height - text.Height) / 2));
    }

    private FormattedText Text(string text, double size, Brush brush, bool bold = false) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? BoldFont : Font, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static string FormatValue(double value)
    {
        var abs = Math.Abs(value);
        return abs >= 100000 || (abs > 0 && abs < 0.01)
            ? value.ToString("G4", CultureInfo.CurrentCulture)
            : value.ToString(abs >= 100 ? "F0" : abs >= 10 ? "F1" : "F2", CultureInfo.CurrentCulture);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
