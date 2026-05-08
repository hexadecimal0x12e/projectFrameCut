using System;
using System.Globalization;
using Microsoft.Maui.Controls;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Shared;
using LocalizedResources;

namespace projectFrameCut.Converters
{
    public class AssetTypeToLocalizedConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is AssetItem asset)
            {
                return BuildTypeDisplayWithInfo(asset);
            }

            if (value is AssetType type)
            {
                return type switch
                {
                    AssetType.Video => Localized.AssetPage_AssetType_Video,
                    AssetType.Audio => Localized.AssetPage_AssetType_Audio,
                    AssetType.Image => Localized.AssetPage_AssetType_Image,
                    AssetType.Font => Localized.AssetPage_AssetType_Font,
                    AssetType.Other => Localized.AssetPage_AssetType_Other,
                    _ => "Unknown"
                };
            }
            return value?.ToString() ?? "";
        }

        private static string BuildTypeDisplayWithInfo(AssetItem asset)
        {
            var typeText = TypeToDisplayName(asset.AssetType);
            switch (asset.AssetType)
            {
                case AssetType.Video:
                    {
                        return $"{TypeToDisplayName(asset.AssetType)} ({asset.Width}*{asset.Height}, {asset.DurationTimeDisplay}, {asset.BitPerPixel}bit)";
                    }
                case AssetType.Audio:
                    {
                        return $"{TypeToDisplayName(asset.AssetType)} ({asset.DurationTimeDisplay})";
                    }
                default:
                    return TypeToDisplayName(asset.AssetType);
            }
        }

        public static string TypeToDisplayName(AssetType type)
        {
            return type switch
            {
                AssetType.Video => Localized.AssetPage_AssetType_Video,
                AssetType.Audio => Localized.AssetPage_AssetType_Audio,
                AssetType.Image => Localized.AssetPage_AssetType_Image,
                AssetType.Font => Localized.AssetPage_AssetType_Font,
                AssetType.Other => Localized.AssetPage_AssetType_Other,
                _ => "Unknown"
            };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
