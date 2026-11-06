using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Brisk;

public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c) =>
        value?.ToString() == parameter?.ToString();
    public object ConvertBack(object value, Type t, object parameter, CultureInfo c) => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object parameter, CultureInfo c)
    {
        bool b = value switch { bool v => v, null => false, string s => s.Length > 0, _ => true };
        return b ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object parameter, CultureInfo c) => Binding.DoNothing;
}

public sealed class PercentConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c) => value is double d ? d * 100 : 0.0;
    public object ConvertBack(object value, Type t, object parameter, CultureInfo c) => Binding.DoNothing;
}

/// <summary>0-100 → 0-1, for ScaleTransform progress bars.</summary>
public sealed class FractionConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c) => value is double d ? Math.Clamp(d / 100, 0, 1) : 0.0;
    public object ConvertBack(object value, Type t, object parameter, CultureInfo c) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c) => value is not true;
    public object ConvertBack(object value, Type t, object parameter, CultureInfo c) => value is not true;
}

public sealed class LevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c)
    {
        string key = (value as string) switch
        {
            "danger" => "DangerBrush",
            "warn" => "WarnBrush",
            "info" => "InfoBrush",
            "purple" => "PurpleBrush",
            _ => "AccentBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }
    public object ConvertBack(object value, Type t, object parameter, CultureInfo c) => Binding.DoNothing;
}
