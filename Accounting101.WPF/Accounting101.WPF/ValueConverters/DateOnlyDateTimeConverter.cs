using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Accounting101.WPF.Extensions;

namespace Accounting101.WPF.ValueConverters;

[ValueConversion(typeof(DateOnly), typeof(DateTime))]
public sealed class DateOnlyDateTimeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateOnly date)
        {
            return date.ToDateTime();
        }

        return DependencyProperty.UnsetValue;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTime dt)
        {
            return dt.ToDateOnly();
        }

        return DependencyProperty.UnsetValue;
    }
}