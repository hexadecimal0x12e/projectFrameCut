using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.Services
{
    public static class PluginService
    {
        public const int PluginAPIVersion = 1;

        public sealed record PluginPayload(string Id, byte[] AssemblyBytes, Dictionary<string, string> Configuration);

        public static List<PluginPayload> GetEnabledPluginPayloads()
        {
            var result = new List<PluginPayload>();

            string pluginsListPath = Path.Combine(MauiProgram.BasicDataPath, "plugins.json");
            if (!File.Exists(pluginsListPath))
                pluginsListPath = Path.Combine(MauiProgram.BasicDataPath, "Plugins.json");

            if (!File.Exists(pluginsListPath))
                return result;

            var items = JsonSerializer.Deserialize<List<PluginItem>>(File.ReadAllText(pluginsListPath)) ?? new();
            foreach (var item in items.Where(c => c.Enabled))
            {
                try
                {
                    var pluginID = item.Id;
                    var pluginRoot = Path.Combine(MauiProgram.BasicDataPath, "Plugins", pluginID);
                    if (!Directory.Exists(pluginRoot))
                        continue;

                    var pluginPem = SecureStorage.Default.GetAsync($"plugin_pem_{pluginID}").GetAwaiter().GetResult();
                    if (string.IsNullOrEmpty(pluginPem))
                        continue;

                    var pluginEncPath = Path.Combine(pluginRoot, pluginID + ".dll.enc");
                    var pluginSigPath = Path.Combine(pluginRoot, pluginID + ".dll.sig");
                    if (!File.Exists(pluginEncPath) || !File.Exists(pluginSigPath))
                        continue;

                    var pemHash = HashServices.ComputeStringHash(pluginPem ?? string.Empty, SHA512.Create());
                    var pluginEnc = File.ReadAllBytes(pluginEncPath);
                    var decBytes = FileCryptoService.DecryptToFileWithPassword(pemHash, pluginEnc);
                    var pluginSig = File.ReadAllText(pluginSigPath);
                    if (!FileSignerService.VerifyFileSignature(pluginPem, decBytes, pluginSig))
                    {
                        Log($"Skip sending plugin {pluginID}: signature mismatch.", "warn");
                        continue;
                    }

                    var config = new Dictionary<string, string>();
                    var optionFilePath = Path.Combine(pluginRoot, "option.json");
                    if (File.Exists(optionFilePath))
                    {
                        try
                        {
                            var configJson = File.ReadAllText(optionFilePath);
                            config = JsonSerializer.Deserialize<Dictionary<string, string>>(configJson) ?? new();
                        }
                        catch (Exception ex)
                        {
                            Log(ex, $"Failed to read plugin configuration for {pluginID}");
                        }
                    }

                    result.Add(new PluginPayload(pluginID, decBytes, config));
                }
                catch (Exception ex)
                {
                    Log(ex, $"export plugin payload: {item.Id}");
                }
            }

            return result;
        }

        public static async Task AddAPlugin(string pluginPath, Page currentPage)
        {
            IPluginBase? pluginInstance = null;
            PluginMetadata metadata = null!;
            string failReason = string.Empty;
            string pluginRoot = string.Empty;
            bool PEMExists = false;
            await Task.Run(async () =>
            {
                Directory.CreateDirectory(Path.Combine(MauiProgram.BasicDataPath, "Plugins"));
                pluginRoot = Path.Combine(MauiProgram.BasicDataPath, "Plugins", Path.GetFileNameWithoutExtension(pluginPath));

                //1 extract plugin
                ZipFile.ExtractToDirectory(pluginPath, pluginRoot, true);

                //2 read metadata
                var metadataFilePath = Path.Combine(pluginRoot, "metadata.json");
                if (File.Exists(metadataFilePath))
                {
                    var metadataJson = await File.ReadAllTextAsync(metadataFilePath);
                    metadata = JsonSerializer.Deserialize<PluginMetadata>(metadataJson);

                }

                if (metadata is null)
                {
                    failReason = "Unable to read plugin metadata.";
                    return;
                }

                //3 decrypt plugin
                var sigPath = Path.Combine(pluginRoot, metadata.PluginID + ".dll.sig");
                var encPath = Path.Combine(pluginRoot, metadata.PluginID + ".dll.enc");
                var pemPath = Path.Combine(pluginRoot, "publickey.pem");
                if (await HashServices.ComputeFileHashAsync(pemPath, SHA512.Create()) != metadata.PluginKey)
                {
                    failReason = "Plugin key mismatch.";
                    return;
                }

                var storPluginPem = await SecureStorage.Default.GetAsync($"plugin_pem_{metadata.PluginID}");
                PEMExists = storPluginPem is not null;
                if (PEMExists && storPluginPem != File.ReadAllText(pemPath))
                {
                    failReason = SettingsManager.SettingLocalizedResources.Plugin_InvaildSignToPreviousOne;
                    return;
                }


                var decBytes = FileCryptoService.DecryptToFileWithPassword(metadata.PluginKey, await File.ReadAllBytesAsync(encPath));
                if (decBytes.Length < 64)
                {
                    failReason = "Decrypted plugin data is too short or failed to decrypt.";
                    return;
                }
                if (HashServices.ComputeBytesHash(decBytes) != metadata.PluginHash)
                {
                    failReason = "Plugin hash mismatch.";
                    return;
                }
                var pluginSig = File.ReadAllText(sigPath);
                var pluginPem = File.ReadAllText(pemPath);
                if (!FileSignerService.VerifyFileSignature(pluginPem, decBytes, pluginSig))
                {
                    failReason = "Plugin signature verification failed.";
                    return;
                }
                Assembly plugin = Assembly.Load(decBytes);
                pluginInstance = CreateIPluginFromAsb(plugin);
                if (pluginInstance is null)
                {
                    failReason = "Unable to create plugin instance from assembly.";
                    return;
                }

            });

            if (pluginInstance is null)
            {
                await currentPage.DisplayAlertAsync(Localized._Error, $"Failed to add the plugin.\r\n({failReason})", Localized._OK);
                return;
            }
            else
            {
                var conf = await currentPage.DisplayAlertAsync(Localized._Warn, SettingsManager.SettingLocalizedResources.Plugin_AddWarn(pluginInstance.Name), Localized._OK, Localized._Cancel);
                if (conf)
                {
                    if (!PEMExists)
                    {
                        var pemPath = Path.Combine(pluginRoot, "publickey.pem");
                        var pem = File.ReadAllText(pemPath);
                        await SecureStorage.Default.SetAsync($"plugin_pem_{pluginInstance.PluginID}", pem);
                    }
                    if(Directory.Exists(Path.Combine(MauiProgram.BasicDataPath, "Plugins", pluginInstance.PluginID))) Directory.Delete(Path.Combine(MauiProgram.BasicDataPath, "Plugins", pluginInstance.PluginID), true);
                    Directory.CreateDirectory(Path.Combine(MauiProgram.BasicDataPath, "Plugins", pluginInstance.PluginID));
                    File.Move(Path.Combine(pluginRoot, pluginInstance.PluginID + ".dll.enc"), Path.Combine(MauiProgram.BasicDataPath, "Plugins", pluginInstance.PluginID, pluginInstance.PluginID + ".dll.enc"));
                    File.Move(Path.Combine(pluginRoot, pluginInstance.PluginID + ".dll.sig"), Path.Combine(MauiProgram.BasicDataPath, "Plugins", pluginInstance.PluginID, pluginInstance.PluginID + ".dll.sig"));
                    Directory.Delete(pluginRoot, true);
                    List<PluginItem> items = new();
                    if (File.Exists(Path.Combine(MauiProgram.BasicDataPath, "plugins.json")))
                    {
                        items = JsonSerializer.Deserialize<List<PluginItem>>(File.ReadAllText(Path.Combine(MauiProgram.BasicDataPath, "plugins.json"))) ?? new();
                    }
                    items.Add(new PluginItem
                    {
                        Author = pluginInstance.Author,
                        Description = pluginInstance.Description,
                        Enabled = true,
                        Id = pluginInstance.PluginID,
                        Version = pluginInstance.Version
                    });
                    File.WriteAllText(Path.Combine(MauiProgram.BasicDataPath, "Plugins.json"), JsonSerializer.Serialize(items));
                    await MainSettingsPage.RebootApp(currentPage);
                }
            }

        }


        public static IPluginBase? CreateFromID(string pluginID, out string failReason)
        {
            failReason = "";
            try
            {


                var pluginRoot = Path.Combine(MauiProgram.BasicDataPath, "Plugins", pluginID);
                if (Directory.Exists(pluginRoot))
                {
                    var pluginPem = SecureStorage.Default.GetAsync($"plugin_pem_{pluginID}").GetAwaiter().GetResult();
                    if (string.IsNullOrEmpty(pluginPem))
                    {
                        throw new FileNotFoundException("Plugin PEM not found in secure storage", pluginID);
                    }
                    var pemHash = HashServices.ComputeStringHash(pluginPem ?? string.Empty, SHA512.Create());
                    var pluginEnc = File.ReadAllBytes(Path.Combine(pluginRoot, pluginID + ".dll.enc"));
                    var decBytes = FileCryptoService.DecryptToFileWithPassword(pemHash, pluginEnc);
                    var pluginSig = File.ReadAllText(Path.Combine(pluginRoot, pluginID + ".dll.sig"));
                    if (!FileSignerService.VerifyFileSignature(pluginPem, decBytes, pluginSig))
                    {
                        string? localizedFailReason = null;
                        try
                        {
                            localizedFailReason = SettingsManager.SettingLocalizedResources.Plugin_InvaildSignToPreviousOne;
                        }
                        catch { }
                        failReason = localizedFailReason ?? "Plugin may be modified, and it's sign is mismatch.";
                        return null;
                    }
                    var asb = Assembly.Load(decBytes);
                    var plugin = CreateIPluginFromAsb(asb)!;

                    var optionFilePath = Path.Combine(pluginRoot, "option.json");
                    if (File.Exists(optionFilePath))
                    {
                        try
                        {
                            var configJson = File.ReadAllText(optionFilePath);
                            var savedConfig = JsonSerializer.Deserialize<Dictionary<string, string>>(configJson) ?? new();

                            foreach (var kvp in savedConfig)
                            {
                                if (plugin.Configuration.ContainsKey(kvp.Key))
                                {
                                    plugin.Configuration[kvp.Key] = kvp.Value;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log(ex, $"Failed to load plugin configuration from {optionFilePath}");
                        }
                    }

                    var success = plugin.OnLoaded(out failReason);

                    if (!success)
                    {
                        return null;
                    }

                    return plugin;
                }
                else
                {
                    failReason = $"Plugin file for {pluginID} not found.";
                }
            }
            catch (ReflectionTypeLoadException)
            {
                string? localizedFailReason = null;
                try
                {
                    localizedFailReason = SettingsManager.SettingLocalizedResources.Plugin_VersionMismatch;
                }
                catch { }
                failReason = localizedFailReason ?? "plugin may be not up-to-date with the base API inside projectFrameCut. Try upgrade it.";
            }

            catch (Exception ex)
            {
                failReason = $"An unhandled {ex.GetType().Name} exception occurs when trying to load plugin.\r\n({ex.Message})";
            }
            return null;
        }

        public static IPluginBase? CreateIPluginFromAsb(Assembly asb)
        {
            var module = asb.GetModule(asb.GetName().Name + ".dll");
            var types = module?.GetTypes();
            var ldr = types?.First(a => a.Name == "PluginLoader");
            if (ldr is null)
            {
                return null;
            }
            var ldrMethod = ldr.GetMethod("Load");
            var pluginObj = ldrMethod?.Invoke(null, [Localized._LocaleId_]);
            if(pluginObj is IPluginBase plugin)
            {
                if (plugin.PluginAPIVersion != PluginAPIVersion)
                {
                    Log($"Plugin {plugin.Name} has a mismatch PluginAPIVersion. Excepted {PluginAPIVersion}, got {plugin.PluginAPIVersion}.","error");
                    return null;
                }
                return plugin;
            }
            return null;
        }

        public static Dictionary<string, string> FailedLoadPlugin = new();

        public static List<IPluginBase> LoadUserPlugins()
        {
            List<IPluginBase> plugins = new();
            if (!File.Exists(Path.Combine(MauiProgram.BasicDataPath, "plugins.json"))) return new();
            var items = JsonSerializer.Deserialize<List<PluginItem>>(File.ReadAllText(Path.Combine(MauiProgram.BasicDataPath, "plugins.json"))) ?? new();
            foreach (var item in items.Where(c => c.Enabled))
            {
                try
                {
                    Log($"Loading userPlugin: {item.Id}");
                    string fail = "";
                    var p = CreateFromID(item.Id, out fail);
                    if (p is not null)
                        plugins.Add(p);
                    else
                    {
                        Log($"Failed to load user plugin {item.Id}: {fail}");
                        if (!FailedLoadPlugin.TryAdd(item.Id, fail))
                        {
                            Log($"The plugin {item.Id} has been added many times.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log(ex, $"load user plugin: {item.Id}");
                    var msg = $"An unhandled {ex.GetType().Name} exception occurs when trying to load plugin.\r\n({ex.Message})";
                    if(!FailedLoadPlugin.TryAdd(item.Id, msg))
                    {
                        Log($"The plugin {item.Id} has been added many times.");
                    }

                }

            }



            return plugins;
        }

        public static void RemovePlugin(string pluginID)
        {
            if (pluginID.StartsWith("projectFrameCut")) throw new InvalidOperationException(SettingsManager.SettingLocalizedResources?.Plugin_CannotRemoveInternalPlugin ?? "Cannot remove a internal plugin.");
            var items = JsonSerializer.Deserialize<List<PluginItem>>(File.ReadAllText(Path.Combine(MauiProgram.BasicDataPath, "plugins.json"))) ?? new();
            items.RemoveAll(c => c.Id == pluginID);
            File.WriteAllText(Path.Combine(MauiProgram.BasicDataPath, "Plugins.json"), JsonSerializer.Serialize(items));
            var pluginRoot = Path.Combine(MauiProgram.BasicDataPath, "Plugins", pluginID);
            if (Directory.Exists(pluginRoot))
            {
                Directory.Delete(pluginRoot, true);
            }

        }


        public static List<PluginItem> GetDisabledPlugins()
        {
            if (!File.Exists(Path.Combine(MauiProgram.BasicDataPath, "plugins.json"))) return new();
            var items = JsonSerializer.Deserialize<List<PluginItem>>(File.ReadAllText(Path.Combine(MauiProgram.BasicDataPath, "plugins.json"))) ?? new();
            return items.Where(c => !c.Enabled).ToList();
        }

        public static void EnablePlugin(string pluginID)
        {
            var path = Path.Combine(MauiProgram.BasicDataPath, "plugins.json");
            if (!File.Exists(path)) return;
            var items = JsonSerializer.Deserialize<List<PluginItem>>(File.ReadAllText(path)) ?? new();
            var item = items.FirstOrDefault(c => c.Id == pluginID);
            if (item != null)
            {
                item.Enabled = true;
                File.WriteAllText(path, JsonSerializer.Serialize(items));
            }
        }

        public static void DisablePlugin(string pluginID)
        {
            var path = Path.Combine(MauiProgram.BasicDataPath, "plugins.json");
            if (!File.Exists(path)) return;
            var items = JsonSerializer.Deserialize<List<PluginItem>>(File.ReadAllText(path)) ?? new();
            var item = items.FirstOrDefault(c => c.Id == pluginID);
            if (item != null)
            {
                item.Enabled = false;
                File.WriteAllText(path, JsonSerializer.Serialize(items));
            }
        }


        public class PluginItem
        {
            public string Id { get; set; }
            public string Author { get; set; }
            public string Description { get; set; }
            public Version Version { get; set; }
            public bool Enabled { get; set; }
        }
    }


}
