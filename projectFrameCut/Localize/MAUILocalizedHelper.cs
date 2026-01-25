using Microsoft.Maui.Controls.Xaml;
using LocalizedResources;
using System.Diagnostics;

namespace LocalizedResources
{
    [ContentProperty(nameof(Key))]
    [AcceptEmptyServiceProvider]
    [DebuggerNonUserCode()]
    public class LocalizedExtension : IMarkupExtension<string>
    {
        public string Key { get; set; } = string.Empty;

        public string ProvideValue(IServiceProvider serviceProvider)
        {
            if (string.IsNullOrEmpty(Key))
                return string.Empty;

            if (Localized == null)
            {
                return $"@{Key}(Localized not inited yet.)";
            }

            try
            {
                var key = Localized.DynamicLookup(Key,$"Unknown localized string {Key}");
                return key;
            }
            catch (Exception)
            {
                return $"@{Key}"; 
            }
        }

        object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider)
        {
            return ProvideValue(serviceProvider);
        }
    }

    [ContentProperty(nameof(Value))]
    [AcceptEmptyServiceProvider]
    [DebuggerNonUserCode()]
    public class EnumLocalizedExtension : IMarkupExtension<string>
    {
        public object? Value { get; set; }
        public string Prefix { get; set; } = string.Empty;

        public string ProvideValue(IServiceProvider serviceProvider)
        {
            if (Value == null)
                return string.Empty;

            string key = Prefix + Value.ToString();

            if (Localized == null)
            {
                return $"@{key}(Localized not inited yet.)";
            }

            try
            {
                var result = Localized.DynamicLookup(key, $"Unknown localized string {key}");
                return result;
            }
            catch (Exception)
            {
                return $"@{key}";
            }
        }

        object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider)
        {
            return ProvideValue(serviceProvider);
        }
    }

    //[DebuggerNonUserCode()]
    public class EnumLocalizedConverter : IValueConverter
    {
        public string? Prefix { get; set; }

        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value == null)
                return null;

            string prefix = Prefix ?? parameter?.ToString() ?? string.Empty;

            if (value is System.Collections.IEnumerable collection && value is not string)
            {
                var list = new List<string>();
                foreach (var item in collection)
                {
                    list.Add(GetLocalizedResult(item, prefix));
                }
                return list;
            }

            return GetLocalizedResult(value, prefix);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is string strValue && targetType.IsEnum)
            {
                string prefix = Prefix ?? parameter?.ToString() ?? string.Empty;
                foreach (var item in Enum.GetValues(targetType))
                {
                    if (GetLocalizedResult(item, prefix) == strValue)
                        return item;
                }
            }
            return value;
        }

        private string GetLocalizedResult(object value, string prefix)
        {
            string key = prefix + value.ToString();

            if (Localized == null)
            {
                return $"@{key}(Localized not inited yet.)";
            }

            try
            {
                var result = Localized.DynamicLookup(key, $"Unknown localized string {key}");
                return result;
            }
            catch (Exception)
            {
                return $"@{key}";
            }
        }
    }
}