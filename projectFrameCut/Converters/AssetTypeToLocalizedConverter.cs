using System;
using System.Globalization;
using Microsoft.Maui.Controls;
using projectFrameCut.Shared;
using LocalizedResources;

namespace projectFrameCut.Converters
{
    public class AssetTypeToLocalizedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is AssetType type)
            {
                return type switch
                {
                    AssetType.Video => Localized.AssetPage_AssetType_Video,
                    AssetType.Audio => Localized.AssetPage_AssetType_Audio,
                    AssetType.Image => Localized.AssetPage_AssetType_Image,
                    AssetType.Font => Localized.AssetPage_AssetType_Font,
                    AssetType.Other => Localized.AssetPage_AssetType_Other,
                    _ => Localized.AssetPage_AssetType_Unknown
                };
            }
            return value?.ToString() ?? "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
