using System.Globalization;
using Microsoft.Maui.Controls;

namespace projectFrameCut.Converters;

public class EqualConverter : IValueConverter
{
    public object? ComparisonValue { get; set; } = "";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && value.ToString() == ComparisonValue?.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
