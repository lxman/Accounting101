using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Accounting101.WPF.ValueConverters;

[ValueConversion(typeof(decimal), typeof(string))]
public class DecimalToAccountingStringConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is decimal d)
        {
            return d.ToString("#,##0.00;(#,##0.00);0", culture);
        }

        return DependencyProperty.UnsetValue;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s)
        {
            return DependencyProperty.UnsetValue;
        }

        string toParse = s.Contains("(") || s.Contains(")") ? "-" : string.Empty;
        toParse = $"{toParse}{s.Replace(culture.NumberFormat.CurrencySymbol, string.Empty).Replace("(", string.Empty).Replace(")", string.Empty)}";
        return decimal.TryParse(toParse, NumberStyles.Currency, culture, out decimal result)
            ? result
            : DependencyProperty.UnsetValue;
    }
}