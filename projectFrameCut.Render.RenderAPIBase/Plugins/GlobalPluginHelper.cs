namespace projectFrameCut.Render.RenderAPIBase.Plugins
{
    public static class GlobalPluginHelper
    {
        /// <summary>
        /// Get the data root path for the plugin.
        /// </summary>
        public static string GetPluginDataRoot(this IPluginBase plugin)
        {
            if (PluginsDataRootPath is null)
            {
                throw new InvalidOperationException("PluginsDataRootPath is not initialized.");
            }
            return Path.Combine(PluginsDataRootPath, plugin.PluginID);
        }
        /// <summary>
        /// Get the shared cache root.
        /// </summary>
        public static string GetCacheRoot()
        {
            if (CacheRootPath is string path)
                return path;
            if (PluginsDataRootPath is string pluginsPath)
                return Path.Combine(pluginsPath, "__Cache__");
            throw new InvalidOperationException("CacheRootPath is not initialized.");
        }

        internal static string? PluginsDataRootPath
        {
            get;
            set
            {
                if (PluginsDataRootPath is not null)
                {
                    throw new InvalidOperationException("PluginsDataRootPath is already initialized.");
                }
                field = value;
            }
        }

        internal static string? CacheRootPath
        {
            get => field;
            set
            {
                if (field is not null)
                {
                    throw new InvalidOperationException("CacheRootPath is already initialized.");
                }
                field = value;
            }
        }

        /// <summary>
        /// The global getter.
        /// </summary>
        /// <remarks>
        /// Don't set this property directly, it will cause a exception.
        /// </remarks>
        public static Func<string, IPluginBase?>? PluginGetter
        {
            get;
            internal set
            {
                if (PluginGetter is not null)
                {
                    throw new InvalidOperationException("PluginGetter is already initialized.");
                }
                field = value;
            }
        } = null;

        public static IMessagingService? MessagingService
        {
            get;
            internal set
            {
                if (MessagingService is not null)
                {
                    throw new InvalidOperationException("MessagingService is already initialized.");
                }
                field = value;
            }
        } = null;

        /// <summary>
        /// Get the specific plugin by its ID.
        /// </summary>
        public static IPluginBase GetPlugin(string pluginID)
        {
            if (PluginGetter is null) throw new InvalidOperationException("PluginGetter is not initialized.");
            return PluginGetter(pluginID) ?? throw new KeyNotFoundException($"Plugin with ID '{pluginID}' maybe not found.");
        }

    }

}
