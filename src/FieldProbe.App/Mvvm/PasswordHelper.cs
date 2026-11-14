using System.Windows;
using System.Windows.Controls;

namespace FieldProbe.App.Mvvm;

/// <summary>Permite binding no PasswordBox (a propriedade Password não é DependencyProperty).</summary>
public static class PasswordHelper
{
    public static readonly DependencyProperty BoundPasswordProperty = DependencyProperty.RegisterAttached(
        "BoundPassword", typeof(string), typeof(PasswordHelper),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged)); // padrão nulo: garante o callback inicial mesmo com senha vazia

    private static readonly DependencyProperty IsUpdatingProperty = DependencyProperty.RegisterAttached(
        "IsUpdating", typeof(bool), typeof(PasswordHelper));

    public static string? GetBoundPassword(DependencyObject d) => (string?)d.GetValue(BoundPasswordProperty);

    public static void SetBoundPassword(DependencyObject d, string? value) => d.SetValue(BoundPasswordProperty, value);

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;

        box.PasswordChanged -= OnPasswordChanged;
        if (!(bool)box.GetValue(IsUpdatingProperty))
            box.Password = e.NewValue as string ?? string.Empty;
        box.PasswordChanged += OnPasswordChanged;
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        box.SetValue(IsUpdatingProperty, true);
        SetBoundPassword(box, box.Password);
        box.SetValue(IsUpdatingProperty, false);
    }
}
