using System.Globalization;

namespace projectFrameCut.Converters;

public class StringNullOrWhitespaceSelectionConventor : IValueConverter
{
    public string NullOrWhitespaceOrEmptyText { get; set; } = "";
    public string NullText { get; set; } = "";
    public string WhitespaceText { get; set; } = "";
    public string EmptyText { get; set; } = "";
    public string NormalText { get; set; } = "";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if(!string.IsNullOrWhiteSpace(NullOrWhitespaceOrEmptyText) )
        {
            if (value is not string s1 || string.IsNullOrWhiteSpace(s1) || string.IsNullOrEmpty(s1)) return NullOrWhitespaceOrEmptyText;
            return NormalText;
        }
        if (value is null) return NullText;
        var s = value as string;
        if (string.IsNullOrEmpty(s)) return EmptyText;
        if (string.IsNullOrWhiteSpace(s)) return WhitespaceText;
        return NormalText;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}