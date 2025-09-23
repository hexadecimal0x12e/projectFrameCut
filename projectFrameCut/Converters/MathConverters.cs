using System;
using System.Globalization;
using Microsoft.Maui.Controls;

namespace projectFrameCut.Converters
{
    public class SecondsToPixelsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double seconds && parameter is double pps)
                return seconds * pps;
            if (value is double seconds2 && parameter is string ppsStr && double.TryParse(ppsStr, out var ppsVal))
                return seconds2 * ppsVal;
            return 0d;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double pixels && parameter is double pps)
                return pixels / pps;
            if (value is double pixels2 && parameter is string ppsStr && double.TryParse(ppsStr, out var ppsVal))
                return pixels2 / ppsVal;
            return 0d;
        }
    }
}
