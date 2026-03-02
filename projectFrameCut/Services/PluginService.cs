using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace projectFrameCut.Services
{
    public static class PluginService
    {
        public const int PluginAPIVersion = IPluginBase.CurrentPluginAPIVersion;

        public static async Task AddAPlugin(string pluginPath, Page currentPage)
        {
            IPluginBase? pluginInstance = null;
            PluginMetadata metadata = null!;
            string failReason = string.Empty;
            string pluginRoot = string.Empty;
            bool PEMExists = false;
            await Task.Run(async () =>
            {
                try
                {
                    string? localizedPluginBrokenReason = null;
                    try
                    {
                        localizedPluginBrokenReason = SettingsManager.SettingLocalizedResources.Plugin_FileMissing;
                    }
                    catch { }
                    failReason = localizedPluginBrokenReason ?? "Some of the plugin files are missing. Try reinstall it.";
                    Directory.CreateDirectory(Path.Combine(MauiProgram.BasicDataPath, "Plugins"));
                    pluginRoot = Path.Combine(MauiProgram.BasicDataPath, "Plugins", $"{Path.GetFileNameWithoutExtension(pluginPath)}_{Guid.NewGuid()}");

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
                        return;
                    }

                    if (!File.Exists(Path.Combine(pluginRoot, metadata.PluginID + ".dll.enc")) ||
                       !File.Exists(Path.Combine(pluginRoot, metadata.PluginID + ".dll.sig")) ||
                       !File.Exists(Path.Combine(pluginRoot, "hashtable.json")))
                    {
                        return;
                    }
                    var htb = File.ReadAllText(Path.Combine(pluginRoot, "hashtable.json"));
                    //3 chech hashtable
                    if (!ChechHashtable(pluginRoot, htb, out failReason))
                    {
                        return;
                    }

                    //3 decrypt plugin
                    var sigPath = Path.Combine(pluginRoot, metadata.PluginID + ".dll.sig");
                    var encPath = Path.Combine(pluginRoot, metadata.PluginID + ".dll.enc");
                    var pemPath = Path.Combine(pluginRoot, "publickey.pem");
                    if (await HashServices.ComputeFileHashAsync(pemPath, SHA512.Create()) != metadata.PluginKey)
                    {
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
                        return;
                    }
                    if (HashServices.ComputeBytesHash(decBytes) != metadata.PluginHash)
                    {
                        return;
                    }
                    var pluginSig = File.ReadAllText(sigPath);
                    var pluginPem = File.ReadAllText(pemPath);
                    if (!FileSignerService.VerifyFileSignature(pluginPem, decBytes, pluginSig))
                    {
                        return;
                    }
                    ResolveEventHandler resolver = (s, e) =>
                    {
                        var name = new AssemblyName(e.Name).Name;
                        return TryResolveAssembly(name, [pluginRoot, AppContext.BaseDirectory], true);
                    };


                    AppDomain.CurrentDomain.AssemblyResolve += resolver;
                    try
                    {
                        Assembly plugin = Assembly.Load(decBytes);
                        try
                        {
                            var workingDir = Environment.CurrentDirectory;
                            Environment.CurrentDirectory = pluginRoot;
                            pluginInstance = CreateIPluginFromAsb(plugin, pluginRoot);
                            Environment.CurrentDirectory = workingDir;

                            if (pluginInstance != null)
                            {
                                bool removeDependencies = true;
                                if (pluginInstance.Properties.TryGetValue("RemoveCommonDependency", out var propVal))
                                {
                                    if (bool.TryParse(propVal, out bool result) && result)
                                    {
                                        removeDependencies = false;
                                    }
                                }

                                if (removeDependencies)
                                {
                                    Log("Merging common dependencies...");
                                    var htbDict = JsonSerializer.Deserialize<Dictionary<string, string>>(htb) ?? new();
                                    var baseDir = AppContext.BaseDirectory;
                                    var filesToCheck = Directory.GetFiles(pluginRoot, "*.dll");
                                    bool hashtableChanged = false;

                                    foreach (var file in filesToCheck.Where(f => !Path.GetFileNameWithoutExtension(f).StartsWith("projectFrameCut.")))
                                    {
                                        var fileName = Path.GetFileName(file);
                                        var destPath = Path.Combine(baseDir, fileName);

                                        if (File.Exists(destPath))
                                        {
                                            var localHash = HashServices.ComputeFileHash(file);
                                            var destHash = HashServices.ComputeFileHash(destPath);
                                            if (localHash == destHash)
                                            {
                                                try
                                                {
                                                    File.Delete(file);
                                                    if (htbDict.ContainsKey(fileName))
                                                    {
                                                        htbDict.Remove(fileName);
                                                        hashtableChanged = true;
                                                    }
                                                    Log($"Merged common dependency {fileName}.");
                                                }
                                                catch (Exception ex)
                                                {
                                                    Log(ex, $"Failed to remove common dependency {fileName}");
                                                }
                                            }
                                        }
                                    }

                                    try
                                    {
                                        static void removeLangDependence(string key)
                                        {
                                            if (Directory.GetFiles(key).Length == 1 && Path.GetFileName(Directory.GetFiles(key)[0]) == "Microsoft.Maui.Controls.resources.dll")
                                            {
                                                Directory.Delete(key, true);
                                                Log($"Deleted directory {key}.");
                                            }
                                        }

                                        foreach (var key in Directory.GetDirectories(pluginRoot, "??", SearchOption.TopDirectoryOnly))
                                        {
                                            removeLangDependence(key);
                                        }
                                        foreach (var key in Directory.GetDirectories(pluginRoot, "??-??", SearchOption.TopDirectoryOnly))
                                        {
                                            removeLangDependence(key);
                                        }
                                        foreach (var key in Directory.GetDirectories(pluginRoot, "??-????", SearchOption.TopDirectoryOnly))
                                        {
                                            removeLangDependence(key);
                                        }
                                    }
                                    catch { }

                                    if (hashtableChanged)
                                    {
                                        File.WriteAllText(Path.Combine(pluginRoot, "hashtable.json"), JsonSerializer.Serialize(htbDict));
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            failReason = ex.Message;
                            return;
                        }
                    }
                    finally
                    {
                        AppDomain.CurrentDomain.AssemblyResolve -= resolver;
                    }
                }
                catch (Exception ex)
                {
                    Log(ex);
                    failReason = Localized._ExceptionTemplate(ex);
                    return;
                }
                finally
                {

                }
                if (pluginInstance is null)
                {
                    return;
                }


            });

            if (pluginInstance is null)
            {
                try
                {
                    if (pluginInstance is null)
                    {
                        if (!string.IsNullOrEmpty(pluginRoot) && Directory.Exists(pluginRoot))
                        {
                            Directory.Delete(pluginRoot, true);
                        }
                    }
                }
                catch { }
                await currentPage.DisplayAlertAsync(Localized._Error, SettingsManager.SettingLocalizedResources.Plugin_FailLoad_FailedBeacuse(failReason), Localized._OK);
                return;
            }
            else
            {
                failReason = "";
                var conf = await currentPage.DisplayAlertAsync(Localized._Warn, SettingsManager.SettingLocalizedResources.Plugin_AddWarn(pluginInstance.Name), Localized._OK, Localized._Cancel);
                if (conf)
                {
                    if (!PEMExists)
                    {
                        var pemPath = Path.Combine(pluginRoot, "publickey.pem");
                        var pem = File.ReadAllText(pemPath);
                        await SecureStorage.Default.SetAsync($"plugin_pem_{pluginInstance.PluginID}", pem);
                    }
                    if (Directory.Exists(Path.Combine(MauiProgram.BasicDataPath, "Plugins", pluginInstance.PluginID))) Directory.Delete(Path.Combine(MauiProgram.BasicDataPath, "Plugins", pluginInstance.PluginID), true);
                    Directory.Move(pluginRoot, Path.Combine(MauiProgram.BasicDataPath, "Plugins", pluginInstance.PluginID));
                    File.Delete(Path.Combine(MauiProgram.BasicDataPath, "Plugins", pluginInstance.PluginID, "metadata.json"));
                    File.Delete(Path.Combine(MauiProgram.BasicDataPath, "Plugins", pluginInstance.PluginID, "publickey.pem"));
                    FileCryptoService.EncryptToFileWithPassword(metadata.PluginKey, Path.Combine(MauiProgram.BasicDataPath, "Plugins", pluginInstance.PluginID, "hashtable.json"), Path.Combine(MauiProgram.BasicDataPath, "Plugins", pluginInstance.PluginID, "hashtable.json.enc"));
                    File.Delete(Path.Combine(MauiProgram.BasicDataPath, "Plugins", pluginInstance.PluginID, "hashtable.json"));
                    List<PluginItem> items = new();
                    if (File.Exists(Path.Combine(MauiProgram.BasicDataPath, "plugins.json")))
                    {
                        items = JsonSerializer.Deserialize<List<PluginItem>>(File.ReadAllText(Path.Combine(MauiProgram.BasicDataPath, "plugins.json"))) ?? new();
                    }
                    if (items.Any(i => i.Id == pluginInstance.PluginID))
                    {
                        items = items.RemoveRange(items.Where(i => i.Id == pluginInstance.PluginID)).ToList();
                    }
                    items.Add(new PluginItem
                    {
                        Name = pluginInstance.Name,
                        Author = pluginInstance.Author,
                        Description = pluginInstance.Description,
                        Enabled = true,
                        Id = pluginInstance.PluginID,
                        Version = pluginInstance.Version
                    });
                    File.WriteAllText(Path.Combine(MauiProgram.BasicDataPath, "Plugins.json"), JsonSerializer.Serialize(items));
                    var (newInstance, _) = await CreateFromID(pluginInstance.PluginID);
                    if (newInstance is IPluginBase pluginBase)
                    {
                        PluginManager.LoadFrom(newInstance);
                    }
                }
            }

        }

        private static bool ChechHashtable(string pluginRoot, string hashtable, out string failReason)
        {
            var hashTable = JsonSerializer.Deserialize<Dictionary<string, string>>(hashtable);
            var files = Directory.GetFiles(pluginRoot);
            if (hashTable is null)
            {
                failReason = "Failed to read hashtable.";
                return false;
            }
            foreach (var item in hashTable)
            {
                if (File.Exists(Path.Combine(pluginRoot, item.Key)))
                {
                    var hash = HashServices.ComputeFileHash(Path.Combine(pluginRoot, item.Key));
                    if (hash != item.Value)
                    {
                        string? localizedFailReason = null;
                        try
                        {
                            localizedFailReason = SettingsManager.SettingLocalizedResources.Plugin_InvaildFileHash(item.Key);
                        }
                        catch { }
                        failReason = localizedFailReason ?? $"File hash mismatch for {item.Key}.";
                        return false;
                    }
                }
                else
                {
                    if (item.Key == "metadata.json") break;
                    string? localizedFailReason = null;
                    try
                    {
                        localizedFailReason = SettingsManager.SettingLocalizedResources.Plugin_FileMissing + $" (File {item.Key} is missing.)";
                    }
                    catch { }
                    failReason = localizedFailReason ?? $"File {item.Key} missing.";
                    return false;
                }
            }
            failReason = "";
            return true;
        }

        public static async Task<Tuple<IPluginBase?, string?>> CreateFromID(string pluginID)
        {
            var pluginPem = await SecureStorage.Default.GetAsync($"plugin_pem_{pluginID}");
            var plugin = CreateFromID(pluginID, out string failReason, pluginPem);
            return new(plugin, failReason);

        }

        public static IPluginBase? CreateFromID(string pluginID, out string failReason, string? pluginPem = null)
        {
            failReason = "";
            try
            {
                var pluginRoot = Path.Combine(MauiProgram.BasicDataPath, "Plugins", pluginID);
                if (Directory.Exists(pluginRoot))
                {
                    pluginPem ??= TaskHelper.SyncWait(() => SecureStorage.Default.GetAsync($"plugin_pem_{pluginID}"));
                    if (string.IsNullOrEmpty(pluginPem))
                    {
                        throw new FileNotFoundException("Plugin PEM not found in secure storage", pluginID);
                    }

                    if (!File.Exists(Path.Combine(pluginRoot, pluginID + ".dll.enc")) || !File.Exists(Path.Combine(pluginRoot, pluginID + ".dll.sig")) || !File.Exists(Path.Combine(pluginRoot, "hashtable.json.enc")))
                    {
                        string? localizedPluginBrokenReason = null;
                        try
                        {
                            localizedPluginBrokenReason = SettingsManager.SettingLocalizedResources.Plugin_FileMissing;
                        }
                        catch { }
                        failReason = localizedPluginBrokenReason ?? "Some of the plugin files are missing. Try reinstall it.";
                        return null;
                    }

                    var pemHash = HashServices.ComputeStringHash(pluginPem ?? string.Empty, SHA512.Create());
                    var pluginEnc = File.ReadAllBytes(Path.Combine(pluginRoot, pluginID + ".dll.enc"));
                    var htbEnc = File.ReadAllBytes(Path.Combine(pluginRoot, "hashtable.json.enc"));
                    var decBytes = FileCryptoService.DecryptToFileWithPassword(pemHash, pluginEnc);
                    var decHashtable = FileCryptoService.DecryptToFileWithPassword(pemHash, htbEnc);
                    var htbJson = Encoding.UTF8.GetString(decHashtable);
                    if (!ChechHashtable(pluginRoot, htbJson, out failReason))
                    {
                        return null;
                    }
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
                    string? loadFailReason = null;
                    ResolveEventHandler resolver = (s, e) =>
                    {
                        var name = new AssemblyName(e.Name).Name;
                        var asb = TryResolveAssembly(name, new[] { pluginRoot, AppContext.BaseDirectory }, false);
                        if (asb is null)
                        {
                            loadFailReason = $"Dependency assembly '{name}' not found.";
                        }
                        return asb;
                    };
                    AppDomain.CurrentDomain.AssemblyResolve += resolver;
                    IPluginBase? plugin;
                    try
                    {
                        var workingDir = Environment.CurrentDirectory;
                        Environment.CurrentDirectory = pluginRoot;
                        var asb = Assembly.Load(decBytes);
                        plugin = CreateIPluginFromAsb(asb, pluginRoot);
                        Environment.CurrentDirectory = workingDir;
                    }
                    catch (EntryPointNotFoundException ex)
                    {
                        string? localizedFailReason = null;
                        try
                        {
                            localizedFailReason = SettingsManager.SettingLocalizedResources.Plugin_CannotCreateInstance(ex.Message);
                        }
                        catch { }
                        failReason = localizedFailReason ?? ex.Message;
                        return null;
                    }
                    catch (Exception ex)
                    {
                        if (loadFailReason is not null)
                        {
                            string? localizedPluginBrokenReason = null;
                            try
                            {
                                localizedPluginBrokenReason = SettingsManager.SettingLocalizedResources.Plugin_FileMissing;
                            }
                            catch { }
                            failReason = $"{localizedPluginBrokenReason ?? "Some of the plugin files are missing. Try reinstall it."} ({loadFailReason})";
                            return null;
                        }
                        string? localizedFailReason = null, localizedExcMessage = null;
                        try
                        {
                            localizedFailReason = SettingsManager.SettingLocalizedResources.Plugin_VersionMismatch;
                        }
                        catch { }
                        try
                        {
                            localizedExcMessage = Localized._ExceptionTemplate(ex);
                        }
                        catch { }
                        failReason = $"{localizedFailReason ?? "plugin may be not up-to-date with the base API inside projectFrameCut. Try upgrade it."} ({localizedExcMessage ?? ex.Message})";
                        return null;
                    }
                    finally
                    {
                        AppDomain.CurrentDomain.AssemblyResolve -= resolver;
                    }

                    if (plugin is null)
                    {
                        string? localizedFailReason = null;
                        try
                        {
                            localizedFailReason = SettingsManager.SettingLocalizedResources.Plugin_CannotCreateInstance("The type 'PluginLoader' not found in the assembly.");
                        }
                        catch { }
                        failReason = localizedFailReason ?? $"The type 'PluginLoader' not found in the assembly.";
                        return null;
                    }

                    if(plugin is IApplicationPluginBase apb)
                    {
                        if(apb.AppLevelPluginAPIVersion != IApplicationPluginBase.CurrentAppLevelPluginAPIVersion)
                        {
                            string? localizedFailReason = null;
                            try
                            {
                                localizedFailReason = SettingsManager.SettingLocalizedResources.Plugin_VersionMismatch;
                            }
                            catch { }
                            failReason = localizedFailReason ?? "plugin may be not up-to-date with the base API inside projectFrameCut. Try upgrade it.";
                            return null;
                        }
                    }

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

                    return plugin.OnLoaded(out failReason) ? plugin : null;
                }
                else
                {
                    string? localizedPluginBrokenReason = null;
                    try
                    {
                        localizedPluginBrokenReason = SettingsManager.SettingLocalizedResources.Plugin_FileMissing_DirectoryNotFound;
                    }
                    catch { }
                    failReason = localizedPluginBrokenReason ?? "Plugin file not found.";
                    return null;
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
                string? localizedPluginBrokenReason = null;
                try
                {
                    localizedPluginBrokenReason = Localized._ExceptionTemplate(ex);
                }
                catch { }
                failReason = localizedPluginBrokenReason ?? $"An unhandled {ex.GetType().Name} exception occurs when trying to load plugin.\r\n({ex.Message})";
            }
            return null;
        }

        private static Assembly? TryResolveAssembly(string name, string[] paths, bool keepInMemory)
        {
            Log($"Try loading assembly {name}...");
            foreach (var item in paths)
            {
                var p = Path.Combine(item, name + ".dll");
                if (!File.Exists(p)) continue;
                Log($"Found assembly {name} in {p}.");
                if (keepInMemory)
                {
                    var fs = File.OpenRead(p);
                    var buf = new byte[fs.Length];
                    fs.ReadExactly(buf);
                    fs.Dispose();
                    return Assembly.Load(buf);
                }
                else
                {
                    return Assembly.LoadFile(p);
                }

            }
            Log($"Assembly {name} not found.");
            return null;
        }

        public static IPluginBase? CreateIPluginFromAsb(Assembly asb, string workingPath)
        {
            try
            {
                var module = asb.GetModule(asb.GetName().Name + ".dll");
                var types = module?.GetTypes();
                var ldr = types?.FirstOrDefault(a => a.Name == "AppLevelPluginLoader", types?.FirstOrDefault(a => a.Name == "PluginLoader", null));
                if (ldr is null)
                {
                    throw new EntryPointNotFoundException($"No suitable PluginLoader class found. Do you forget to add it?");
                }
                var ver = ldr.GetField("PluginAPIVersion", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if(ver is not null && ver is int apiVer)
                {
                    if (apiVer != PluginAPIVersion)
                    {
                        Log($"Plugin has a mismatch PluginAPIVersion. Excepted {PluginAPIVersion}, got {apiVer}.", "error");
                        string? localizedFailReason = null;
                        try
                        {
                            localizedFailReason = SettingsManager.SettingLocalizedResources.Plugin_VersionMismatch;
                        }
                        catch { }
                        var failReason = localizedFailReason ?? "plugin may be not up-to-date with the base API inside projectFrameCut. Try upgrade it.";
                        throw new FeatureNotSupportedException(failReason);
                    }
                }

                var ldrMethod = ldr.GetMethod("CreateInstance");
                var pluginObj = ldrMethod?.Invoke(null, [Localized._LocaleId_, workingPath]);
                if (pluginObj is IPluginBase plugin)
                {
                    if (plugin.PluginAPIVersion != PluginAPIVersion)
                    {
                        Log($"Plugin {plugin.Name} has a mismatch PluginAPIVersion. Excepted {PluginAPIVersion}, got {plugin.PluginAPIVersion}.", "error");
                        string? localizedFailReason = null;
                        try
                        {
                            localizedFailReason = SettingsManager.SettingLocalizedResources.Plugin_VersionMismatch;
                        }
                        catch { }
                        var failReason = localizedFailReason ?? "plugin may be not up-to-date with the base API inside projectFrameCut. Try upgrade it.";
                        throw new FeatureNotSupportedException(failReason);
                    }
                    
                    return plugin;
                }
                return null;
            }
            catch (Exception ex)
            {
                Log(ex, "Load userplugin", asb);
                throw;

            }
        }

        public static Dictionary<string, string> FailedLoadPlugin = new();

        public static List<IPluginBase> LoadUserPlugins(Func<string, string> pemGetter = null)
        {
            List<IPluginBase> plugins = new();
            if (!File.Exists(Path.Combine(MauiProgram.BasicDataPath, "plugins.json"))) return new();
            var items = JsonSerializer.Deserialize<List<PluginItem>>(File.ReadAllText(Path.Combine(MauiProgram.BasicDataPath, "plugins.json"))) ?? new();
            bool someRemoved = false;
            foreach (var item in items.Where(c => c.ShouldRemove))
            {
                someRemoved = true;
                try
                {
                    Log($"Removing marked plugin: {item.Id}");
                    var pluginRoot = Path.Combine(MauiProgram.BasicDataPath, "Plugins", item.Id);
                    if (Directory.Exists(pluginRoot))
                    {
                        Directory.Delete(pluginRoot, true);
                    }
                }
                catch { } //ok because of assemblies may be locked
            }

            if (someRemoved)
            {
                items = items.RemoveRange(items.Where(c => c.ShouldRemove)).ToList();
                File.WriteAllText(Path.Combine(MauiProgram.BasicDataPath, "plugins.json"), JsonSerializer.Serialize(items));
            }

            foreach (var item in items.Where(c => c.Enabled))
            {
                try
                {
#if WINDOWS
                    if (Helper.HelperProgram.SplashShowing)
                    {
                        Helper.HelperProgram.UpdatePluginLoadingStat(item.Name ?? item.Id);
                    }
#endif
                    Log($"Loading userPlugin: {item.Id}");
                    var p = CreateFromID(item.Id, out string fail, pemGetter?.Invoke(item.Id));
                    if (p is not null)
                    {
                        if (p is IApplicationPluginBase b) b.OnApplicationPluginLoaded();
                        plugins.Add(p);
                    }
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
                    if (!FailedLoadPlugin.TryAdd(item.Id, msg))
                    {
                        Log($"The plugin {item.Id} has been added many times.");
                    }

                }

            }



            return plugins;
        }

        public static void RemovePlugin(string pluginID)
        {
            var items = JsonSerializer.Deserialize<List<PluginItem>>(File.ReadAllText(Path.Combine(MauiProgram.BasicDataPath, "plugins.json"))) ?? new();
            items.FirstOrDefault(i => i.Id == pluginID)?.ShouldRemove = true;
            File.WriteAllText(Path.Combine(MauiProgram.BasicDataPath, "Plugins.json"), JsonSerializer.Serialize(items));
            var pluginRoot = Path.Combine(MauiProgram.BasicDataPath, "Plugins", pluginID);
            if (PluginManager.LoadedPlugins.TryGetValue(pluginID, out var instance))
            {
                instance.OnClosing();
            }

            try
            {
                if (Directory.Exists(pluginRoot))
                {
                    Directory.Delete(pluginRoot, true);
                }
            }
            catch { } //ok because of assemblies may be locked

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
                try
                {
                    PluginManager.Unload(pluginID);
                }
                catch { }
            }
        }


        public class PluginItem
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Author { get; set; }
            public string Description { get; set; }
            public Version Version { get; set; }
            public bool Enabled { get; set; }
            public bool ShouldRemove { get; set; } = false;
        }
    }


}
