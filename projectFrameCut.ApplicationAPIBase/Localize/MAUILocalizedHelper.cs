using Microsoft.Maui.Controls.Xaml;
using projectFrameCut.ApplicationAPIBase.LocalizedResources;
using System.Diagnostics;

namespace projectFrameCut.ApplicationAPIBase.LocalizedResources
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

            if (APIBaseLocalizedResources.Localized == null)
            {
                return Key.Split('_').LastOrDefault(Key);
            }

            try
            {
                var key = APIBaseLocalizedResources.Localized.DynamicLookup(Key,$"Unknown localized string {Key}");
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

    

}