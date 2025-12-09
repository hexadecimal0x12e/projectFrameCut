using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace projectFrameCut.Render.Plugins
{
    public static class PluginManager
    {
        public const int CurrentPluginAPIVersion = 1;
        public static Dictionary<string, IPluginBase> loadedPlugins { get; private set; } = new();

        public static void Init()
        {
            loadedPlugins.Clear();
            loadedPlugins.Add("projectFrameCut.Render.Plugins.InternalPluginBase", new InternalPluginBase());
        }

        public static void LoadFrom(Assembly asb)
        {
            try
            {
                foreach (Type type in asb.GetTypes())
                {
                    if (type.IsClass && !type.IsAbstract && typeof(IPluginBase).IsAssignableFrom(type))
                    {
                        IPluginBase pluginInstance = (IPluginBase)Activator.CreateInstance(type)!;
                        if (pluginInstance.PluginAPIVersion == CurrentPluginAPIVersion)
                        {
                            loadedPlugins.Add(type.FullName!, pluginInstance);
                        }
                        else
                        {
                            Logger.Log($"Plugin {pluginInstance.Name} has incompatible API version {pluginInstance.PluginAPIVersion}, expected {CurrentPluginAPIVersion}.", "warning");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "load plugins from assembly", "PluginManager");
            }
        }

    }
}
