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
            catch (Exception ex)
            {
                return $"@{Key}"; 
            }
        }

        object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider)
        {
            return ProvideValue(serviceProvider);
        }
    }
}