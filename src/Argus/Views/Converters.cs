using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Argus.Probes;

namespace Argus.Views;

/// <summary>
/// State to colour. Frozen brushes, created once.
///
/// Freezable brushes are shareable across the whole visual tree and skip
/// per-use cloning. On a list that redraws every few seconds that is the
/// difference between allocating nothing and allocating a brush per row.
/// </summary>
public sealed class StateToBrushConverter : IValueConverter
{
    public static readonly SolidColorBrush Live    = Frozen("#22D3EE");
    public static readonly SolidColorBrush Warn    = Frozen("#F5A524");
    public static readonly SolidColorBrush Bad     = Frozen("#FF5C5C");
    public static readonly SolidColorBrush Idle    = Frozen("#3D4F62");
    public static readonly SolidColorBrush Unknown = Frozen("#2A3846");

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SignalState s
            ? s switch
            {
                SignalState.Live => Live,
                SignalState.Warn => Warn,
                SignalState.Bad  => Bad,
                SignalState.Idle => Idle,
                _                => Unknown,
            }
            : Unknown;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Collapses a row element when its text is absent.</summary>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            null => Visibility.Collapsed,
            string s when string.IsNullOrWhiteSpace(s) => Visibility.Collapsed,
            bool b => b ? Visibility.Visible : Visibility.Collapsed,
            _ => Visibility.Visible,
        };

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>
/// Turns a 0..1 fraction into a pixel width against the bar track, so the
/// meters fill correctly at any panel width without a layout pass per tick.
/// </summary>
public sealed class FractionToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return 0d;
        if (values[0] is not double fraction || values[1] is not double total) return 0d;
        if (double.IsNaN(total) || total <= 0) return 0d;
        return Math.Clamp(fraction, 0, 1) * total;
    }

    public object[] ConvertBack(object value, Type[] t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}
