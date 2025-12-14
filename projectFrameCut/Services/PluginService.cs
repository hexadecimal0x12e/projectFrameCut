using projectFrameCut.Render.Plugins;
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
        public static async Task AddAPlugin(string pluginPath, Page currentPage)
        {
            IPluginBase? pluginInstance = null;
            PluginMetadata metadata = null!;
            string failReason = string.Empty;
            string pluginRoot = string.Empty;
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
                if(storPluginPem is not null && storPluginPem != File.ReadAllText(pemPath))
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
                    var pemPath = Path.Combine(pluginRoot, "publickey.pem");
                    var pem = File.ReadAllText(pemPath);
                    await SecureStorage.Default.SetAsync($"plugin_pem_{pluginInstance.PluginID}", pem);
                    //File.Delete(pemPath);
                    //File.Delete(Path.Combine(pluginRoot, "metadata.json"));
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
                    await Setting.SettingPages.GeneralSettingPage.RebootApp(currentPage);
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
                        failReason = "Plugin may be modified, and it's sign is mismatch.";
                        return null;
                    }
                    var asb = Assembly.Load(decBytes);
                    var plugin = CreateIPluginFromAsb(asb)!;

                    // 读取保存的配置文件
                    var optionFilePath = Path.Combine(pluginRoot, "option.json");
                    if (File.Exists(optionFilePath))
                    {
                        try
                        {
                            var configJson = File.ReadAllText(optionFilePath);
                            var savedConfig = JsonSerializer.Deserialize<Dictionary<string, string>>(configJson) ?? new();

                            // 将保存的配置复制到插件对象
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
            }catch(Exception ex)
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
            return pluginObj as IPluginBase;
        }

        public static Dictionary<string,string> FailedLoadPlugin = new();

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
                    var p = CreateFromID(item.Id,out fail);
                    if (p is not null)
                        plugins.Add(p);
                    else
                    {
                        Log($"Failed to load user plugin {item.Id}: {fail}");
                        FailedLoadPlugin.Add(item.Id, fail);
                    }
                }
                catch (Exception ex)
                {
                    Log(ex, $"load user plugin: {item.Id}");
                    var msg = $"An unhandled {ex.GetType().Name} exception occurs when trying to load plugin.\r\n({ex.Message})";
                    FailedLoadPlugin.Add(item.Id, msg);

                }

            }



            return plugins;
        }

        public static void RemovePlugin(string pluginID)
        {
            if (pluginID.StartsWith("projectFrameCut")) throw new InvalidOperationException(SettingsManager.SettingLocalizedResources?.Plugin_CannotRemoveInternalPlugin ?? "Cannot remove a internal plugin.");
            var pluginRoot = Path.Combine(MauiProgram.BasicDataPath, "Plugins", pluginID);
            if (Directory.Exists(pluginRoot))
            {
                Directory.Delete(pluginRoot, true);
            }
            var items = JsonSerializer.Deserialize<List<PluginItem>>(File.ReadAllText(Path.Combine(MauiProgram.BasicDataPath, "plugins.json"))) ?? new();
            items.RemoveAll(c => c.Id == pluginID);
            File.WriteAllText(Path.Combine(MauiProgram.BasicDataPath, "Plugins.json"), JsonSerializer.Serialize(items));

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
