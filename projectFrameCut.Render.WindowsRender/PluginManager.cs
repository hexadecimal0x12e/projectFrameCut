using projectFrameCut.Render.RenderAPIBase.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;

namespace projectFrameCut.Render.WindowsRender
{
    public static class PluginPipeLoader
    {
        private const uint Magic = 0x504A4643; 
        private const int ProtocolVersion = 1;

        public static IEnumerable<IPluginBase> Load(string pipeName)
        {
            var plugins = new List<IPluginBase>();
            if (string.IsNullOrWhiteSpace(pipeName))
                return plugins;

            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.In, PipeOptions.Asynchronous);
            try
            {
                client.Connect(timeout: 30000);
            }
            catch
            {
                Log($"Failed to connect plugin pipe '{pipeName}'.", "warn");
                return plugins;
            }

            using var reader = new BinaryReader(client, new UTF8Encoding(false), leaveOpen: true);
            try
            {
                var magic = reader.ReadUInt32();
                var version = reader.ReadInt32();
                if (magic != Magic || version != ProtocolVersion)
                {
                    Log($"Plugin pipe protocol mismatch. magic={magic:X8}, version={version}", "warn");
                    return plugins;
                }

                var locale = reader.ReadString();
                var count = reader.ReadInt32();
                if (count < 0 || count > 1024)
                {
                    Log($"Invalid plugin count {count} from pipe.", "warn");
                    return plugins;
                }

                for (var i = 0; i < count; i++)
                {
                    var id = reader.ReadString();
                    var len = reader.ReadInt32();
                    if (len <= 0 || len > 256 * 1024 * 1024)
                    {
                        Log($"Skip plugin '{id}': invalid dll length {len}.", "warn");
                        return plugins;
                    }

                    var bytes = reader.ReadBytes(len);
                    var configCount = reader.ReadInt32();
                    var config = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (configCount < 0 || configCount > 4096)
                        configCount = 0;
                    for (var c = 0; c < configCount; c++)
                    {
                        var k = reader.ReadString();
                        var v = reader.ReadString();
                        if (!string.IsNullOrEmpty(k))
                            config[k] = v;
                    }

                    try
                    {
                        var asb = Assembly.Load(bytes);
                        var plugin = CreateIPluginFromAssembly(asb, locale);
                        if (plugin is null)
                        {
                            Log($"Skip plugin '{id}': cannot create IPluginBase instance.", "warn");
                            continue;
                        }

                        foreach (var kvp in config)
                        {
                            if (plugin.Configuration.ContainsKey(kvp.Key))
                                plugin.Configuration[kvp.Key] = kvp.Value;
                        }

                        if (!plugin.OnLoaded(out var failedReason))
                        {
                            Log($"Skip plugin '{plugin.PluginID}': OnLoaded failed: {failedReason}", "warn");
                            continue;
                        }

                        plugins.Add(plugin);
                        Log($"Plugin '{plugin.PluginID}' received via pipe.");
                    }
                    catch (Exception ex)
                    {
                        Log(ex, $"load plugin '{id}' from pipe", nameof(PluginPipeLoader));
                    }
                }
            }
            catch (EndOfStreamException)
            {
                Log("Plugin pipe closed unexpectedly.", "warn");
            }
            catch (Exception ex)
            {
                Log(ex, "read plugin pipe", nameof(PluginPipeLoader));
            }

            return plugins;
        }

        private static IPluginBase? CreateIPluginFromAssembly(Assembly asb, string locale)
        {
            try
            {
                var types = asb.GetTypes();
                var loaderType = types.FirstOrDefault(t => t.Name == "PluginLoader");
                if (loaderType is null)
                    return null;
                var loadMethod = loaderType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static);
                var pluginObj = loadMethod?.Invoke(null, new object[] { locale });
                return pluginObj as IPluginBase;
            }
            catch (ReflectionTypeLoadException rtle)
            {
                var msg = string.Join("; ", rtle.LoaderExceptions.Where(e => e != null).Select(e => e!.Message));
                Log($"Plugin assembly type load failed: {msg}", "warn");
                return null;
            }
            catch
            {
                return null;
            }
        }
    }
}
