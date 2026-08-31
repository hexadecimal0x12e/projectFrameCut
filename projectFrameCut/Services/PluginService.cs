using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace projectFrameCut.Services
{
    public static class PluginService
    {
        public const int PluginAPIVersion = IPluginBase.CurrentPluginAPIVersion;

        public static IPluginRevocationSource RevocationSource
        {
            get => PluginPackageSecurityService.RevocationSource;
            set => PluginPackageSecurityService.RevocationSource = value ?? EmptyPluginRevocationSource.Instance;
        }

        public static Task RegisterDevelopmentRootCertificateAsync(byte[] certificateDer, string? label = null) =>
            PluginPackageSecurityService.RegisterDevelopmentRootCertificateAsync(certificateDer, label);

        public static Task RemoveDevelopmentRootCertificateAsync(string fingerprint) =>
            PluginPackageSecurityService.RemoveDevelopmentRootCertificateAsync(fingerprint);

        public static Task ForgetPublisherTrustAsync(string publisherId) =>
            PluginPackageSecurityService.ForgetPublisherAsync(publisherId);

        public static async Task<PluginTrustReport> ValidatePackageAsync(
            string pluginPath,
            CancellationToken cancellationToken = default)
        {
            var validationRoot = Path.Combine(
                FileSystem.CacheDirectory,
                "plugin-validation",
                Guid.NewGuid().ToString("N"));
            try
            {
                await PluginPackageSecurityService.ExtractPackageSafelyAsync(
                    pluginPath,
                    validationRoot,
                    cancellationToken);
                using var verification = await PluginPackageSecurityService.VerifyExtractedPackageAsync(
                    validationRoot,
                    requirePublisherTrust: false,
                    cancellationToken);
                return verification.TrustReport;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(validationRoot))
                    {
                        Directory.Delete(validationRoot, true);
                    }
                }
                catch (Exception ex)
                {
                    Log(ex, $"Failed to clean plugin validation directory: {validationRoot}");
                }
            }
        }

        public static async Task<byte[]> ExportVerifiedAssemblyAsync(
            string pluginID,
            CancellationToken cancellationToken = default)
        {
            var pluginRoot = Path.Combine(MauiProgram.BasicDataPath, "Plugins", pluginID);
            using var verification = await PluginPackageSecurityService.VerifyExtractedPackageAsync(
                pluginRoot,
                requirePublisherTrust: true,
                cancellationToken);
            return [.. verification.AssemblyBytes];
        }

        public static async Task AddAPlugin(string pluginPath, Page currentPage)
        {
            void CleanupTempPluginDirectory(string tempDir)
            {
                if (string.IsNullOrWhiteSpace(tempDir))
                {
                    return;
                }

                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }
                }
                catch (Exception ex)
                {
                    Log(ex, $"Failed to clean temporary plugin directory: {tempDir}");
                }
            }

            PluginPackageVerificationResult? verification = null;
            string failReason = "The plugin package could not be validated.";
            string pluginRoot = string.Empty;
            try
            {
                Directory.CreateDirectory(Path.Combine(MauiProgram.BasicDataPath, "Plugins"));
                pluginRoot = Path.Combine(
                    MauiProgram.BasicDataPath,
                    "Plugins",
                    $"{Path.GetFileNameWithoutExtension(pluginPath)}_{Guid.NewGuid():N}");

                await PluginPackageSecurityService.ExtractPackageSafelyAsync(pluginPath, pluginRoot);
                verification = await PluginPackageSecurityService.VerifyExtractedPackageAsync(
                    pluginRoot,
                    requirePublisherTrust: false);

                var metadata = verification.Metadata;
                var trust = verification.TrustReport;
                var trustDescription =
                    $"{SettingsManager.SettingLocalizedResources.Plugin_AddWarn(metadata.Name)}\r\r" +
                    $"Publisher: {trust.PublisherName ?? metadata.Author}\r\n" +
                    $"Publisher ID: {trust.PublisherId}\r\n" +
                    $"Signing certificate: {trust.SigningCertificateFingerprint}\r\n" +
                    $"Certificate chain: valid";
                var confirmed = await currentPage.DisplayAlertAsync(
                    Localized._Warn,
                    trustDescription,
                    Localized._OK,
                    Localized._Cancel);
                if (!confirmed)
                {
                    return;
                }

                await PluginPackageSecurityService.TrustPublisherAsync(trust);

                var destination = Path.Combine(MauiProgram.BasicDataPath, "Plugins", metadata.PluginID);
                if (Directory.Exists(destination))
                {
                    Directory.Delete(destination, true);
                }
                Directory.Move(pluginRoot, destination);
                pluginRoot = string.Empty;

                var itemsPath = Path.Combine(MauiProgram.BasicDataPath, "plugins.json");
                List<PluginItem> items = File.Exists(itemsPath)
                    ? JsonSerializer.Deserialize<List<PluginItem>>(await File.ReadAllTextAsync(itemsPath)) ?? []
                    : [];
                items.RemoveAll(item => item.Id == metadata.PluginID);
                items.Add(new PluginItem
                {
                    Name = metadata.Name,
                    Author = metadata.Author,
                    Description = metadata.Description,
                    Enabled = true,
                    Id = metadata.PluginID,
                    Version = metadata.Version,
                    PackageFormatVersion = metadata.PackageFormatVersion,
                    PublisherId = metadata.PublisherId,
                    SigningCertificateFingerprint = metadata.SigningCertificateFingerprint
                });
                await File.WriteAllTextAsync(itemsPath, JsonSerializer.Serialize(items));

                var (newInstance, loadFailure) = await CreateFromID(metadata.PluginID);
                if (newInstance is not null)
                {
                    PluginManager.LoadFrom(newInstance);
                }
                else if (!string.IsNullOrWhiteSpace(loadFailure))
                {
                    await currentPage.DisplayAlertAsync(
                        Localized._Error,
                        SettingsManager.SettingLocalizedResources.Plugin_FailLoad_FailedBeacuse(loadFailure),
                        Localized._OK);
                }
            }
            catch (Exception ex)
            {
                Log(ex, "Install certificate-chain plugin");
                failReason = ex.Message;
                await currentPage.DisplayAlertAsync(
                    Localized._Error,
                    SettingsManager.SettingLocalizedResources.Plugin_FailLoad_FailedBeacuse(failReason),
                    Localized._OK);
            }
            finally
            {
                verification?.Dispose();
                CleanupTempPluginDirectory(pluginRoot);
            }
        }

        public static async Task<Tuple<IPluginBase?, string?>> CreateFromID(string pluginID)
        {
            var result = await CreateFromIDCoreAsync(pluginID);
            return new(result.Plugin, result.FailReason);
        }

        public static IPluginBase? CreateFromID(string pluginID, out string failReason, string? pluginPem = null)
        {
            var result = TaskHelper.SyncWait(
                () => CreateFromIDCoreAsync(pluginID),
                CancellationToken.None);
            failReason = result.FailReason;
            return result.Plugin;
        }

        private static async Task<(IPluginBase? Plugin, string FailReason)> CreateFromIDCoreAsync(string pluginID)
        {
            var pluginRoot = Path.Combine(MauiProgram.BasicDataPath, "Plugins", pluginID);
            if (!Directory.Exists(pluginRoot))
            {
                return (null, "Plugin file not found.");
            }

            if (!File.Exists(Path.Combine(pluginRoot, PluginPackageSecurityService.ManifestFileName)))
            {
                return (null, "Legacy plugin packages are no longer supported. Reinstall this plugin using a certificate-chain package with format version 2.");
            }

            try
            {
                using var verification = await PluginPackageSecurityService.VerifyExtractedPackageAsync(
                    pluginRoot,
                    requirePublisherTrust: true);

                string? loadFailReason = null;
                ResolveEventHandler resolver = (s, e) =>
                {
                    var name = new AssemblyName(e.Name).Name;
                    var assembly = TryResolveAssembly(name, [pluginRoot, AppContext.BaseDirectory], false);
                    if (assembly is null)
                    {
                        loadFailReason = $"Dependency assembly '{name}' not found.";
                    }
                    return assembly;
                };

                AppDomain.CurrentDomain.AssemblyResolve += resolver;
                IPluginBase? plugin;
                var workingDirectory = Environment.CurrentDirectory;
                try
                {
                    Environment.CurrentDirectory = pluginRoot;
                    plugin = CreateIPluginFromAsb(Assembly.Load(verification.AssemblyBytes), pluginRoot);
                }
                finally
                {
                    Environment.CurrentDirectory = workingDirectory;
                    AppDomain.CurrentDomain.AssemblyResolve -= resolver;
                }

                if (plugin is null)
                {
                    return (null, "The type 'PluginLoader' was not found in the plugin assembly.");
                }

                if (!string.Equals(plugin.PluginID, verification.Metadata.PluginID, StringComparison.Ordinal))
                {
                    return (null, "The plugin assembly id does not match the signed package metadata.");
                }

                if (plugin is IApplicationPluginBase applicationPlugin &&
                    applicationPlugin.AppLevelPluginAPIVersion != IApplicationPluginBase.CurrentAppLevelPluginAPIVersion)
                {
                    return (null, "The plugin application API version is not compatible with this application version.");
                }

                var optionFilePath = Path.Combine(pluginRoot, "option.json");
                if (File.Exists(optionFilePath))
                {
                    try
                    {
                        var savedConfiguration = JsonSerializer.Deserialize<Dictionary<string, string>>(
                            await File.ReadAllTextAsync(optionFilePath)) ?? [];
                        foreach (var item in savedConfiguration)
                        {
                            if (plugin.Configuration.ContainsKey(item.Key))
                            {
                                plugin.Configuration[item.Key] = item.Value;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log(ex, $"Failed to load plugin configuration from {optionFilePath}");
                    }
                }

                if (!plugin.OnLoaded(out var onLoadedFailure))
                {
                    return (null, onLoadedFailure);
                }

                return (plugin, string.Empty);
            }
            catch (Exception ex)
            {
                Log(ex, $"Load certificate-chain plugin: {pluginID}");
                return (null, ex.Message);
            }
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
                var ver = ldr.GetMethod("get_PluginAPIVersion", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
                if (ver is int apiVer)
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
                else
                {
                    Log($"Plugin has no version defined in LoaderClass.", "error");

                    string? localizedFailReason = null;
                    try
                    {
                        localizedFailReason = SettingsManager.SettingLocalizedResources.Plugin_VersionMismatch;
                    }
                    catch { }
                    var failReason = localizedFailReason ?? "plugin may be not up-to-date with the base API inside projectFrameCut. Try upgrade it.";
                    throw new FeatureNotSupportedException(failReason);
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

        public static List<IPluginBase> LoadUserPlugins(Func<string, string>? legacyPemGetter = null)
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
                    var p = CreateFromID(item.Id, out string fail);
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
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Author { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public Version Version { get; set; } = new(0, 0);
            public int PackageFormatVersion { get; set; }
            public string PublisherId { get; set; } = string.Empty;
            public string SigningCertificateFingerprint { get; set; } = string.Empty;
            public bool Enabled { get; set; }
            public bool ShouldRemove { get; set; } = false;
        }
    }


}
