using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Accounting101.WPF.ValueConverters;

[ValueConversion(typeof(bool), typeof(bool?))]
public sealed class BoolNullableBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            return b;
        }

        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            null => false,
            bool b => b,
            _ => DependencyProperty.UnsetValue
        };
    }
}