using Microsoft.Maui.Controls.Xaml;
using LocalizedResources;

namespace LocalizedResources
{
    [ContentProperty(nameof(Key))]
    public class LocalizedExtension : IMarkupExtension<string>
    {
        public string Key { get; set; } = string.Empty;

        public string ProvideValue(IServiceProvider serviceProvider)
        {
            if (string.IsNullOrEmpty(Key))
                return string.Empty;

            // 使用你现有的本地化系统
            if (SimpleLocalizerBaseGeneratedHelper.Localized == null)
            {
                // 如果还没有初始化，使用默认的本地化器
                SimpleLocalizerBaseGeneratedHelper.Localized = SimpleLocalizer.Init();
            }

            try
            {
                // 使用反射或动态查找来获取本地化字符串
                return SimpleLocalizerBaseGeneratedHelper.Localized.DynamicLookup(Key);
            }
            catch (KeyNotFoundException)
            {
                return $"@{Key}"; // 返回键名作为占位符
            }
        }

        object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider)
        {
            return ProvideValue(serviceProvider);
        }
    }
}