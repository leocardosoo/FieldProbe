using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace FieldProbe.App.Hmi;

/// <summary>
/// Ângulo (graus) → RotateTransform em volta do ponto 60,60 (ponteiro do medidor).
/// Ligar o RenderTransform inteiro evita o aviso "FrameworkElement de governança" de binding dentro de transforms.
/// </summary>
public sealed class NeedleRotateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new RotateTransform(value is double angle ? angle : 0, 60, 60);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Fração 0..1 → ScaleTransform vertical (nível do tanque).</summary>
public sealed class FractionScaleYConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new ScaleTransform(1, value is double fraction ? fraction : 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
