using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Shared;
using System.Collections.Concurrent;
using System.Text.Json;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppExtensions;

namespace projectFrameCut.Services;

internal sealed class WindowsAppExtensionPlugin
{
    public required PluginService.PluginItem Item { get; set; }
    public AppExtension? Extension { get; init; }
    public string? PackageFullName { get; init; }
    public string? PublicRoot { get; set; }
    public PluginMetadata? Metadata { get; set; }
    public string? Failure { get; set; }
    public bool HasConflict { get; set; }
    public bool RequiresRestart { get; set; }
    public string Key => $"{Item.MsixPackageFamilyName}!{Item.MsixExtensionId}";
    public bool IsAvailable => Metadata is not null && Failure is null && !HasConflict && !RequiresRestart;
}

internal static class WindowsAppExtensionService
{
    public const string ExtensionName = "projectFrameCut.ExternalPlugin";
    private static readonly object refreshLock = new();
    private static readonly SemaphoreSlim operationLock = new(1, 1);
    private static readonly ConcurrentDictionary<string, (string PackageFullName, IPluginBase Plugin)> loaded = new();
    private static readonly ConcurrentDictionary<string, string> loadFailures = new();
    private static readonly HashSet<string> restartRequired = [];
    private static Task<bool>? refreshTask;
    private static readonly JsonSerializerOptions jsonOptions = new() { PropertyNameCaseInsensitive = true };
    public static IReadOnlyList<WindowsAppExtensionPlugin> Plugins { get; private set; } = [];
    public static string? DiscoveryFailure { get; private set; }

    public static bool IsPluginEngineEnabled => !AdminServices.IsRunningAsAdministrator()
        && !SettingsManager.IsBoolSettingTrue("DisablePluginEngine")
        && !Environment.GetCommandLineArgs().Contains("--disablePlugins")
        && !File.Exists(Path.Combine(MauiProgram.BasicDataPath, "noplugin.flag"));

    public static Task<bool> RefreshAsync(IEnumerable<IPluginBase>? internalPlugins = null, bool persist = true)
    {
        lock (refreshLock)
        {
            if (refreshTask is null || refreshTask.IsCompleted)
                refreshTask = RefreshCoreAsync(internalPlugins, persist);
            return refreshTask;
        }
    }

    private static async Task<bool> RefreshCoreAsync(IEnumerable<IPluginBase>? internalPlugins, bool persist)
    {
        await operationLock.WaitAsync();
        try
        {
            var extensions = await AppExtensionCatalog.Open(ExtensionName).FindAllAsync();
            List<WindowsAppExtensionPlugin> discovered = [];
            foreach (var extension in extensions)
            {
                var plugin = new WindowsAppExtensionPlugin
                {
                    Item = new()
                    {
                        MsixPackageFamilyName = extension.Package.Id.FamilyName,
                        MsixExtensionId = extension.Id,
                        Name = extension.DisplayName,
                        BackendKind = PluginBackendKind.External,
                    },
                    Extension = extension,
                    PackageFullName = extension.Package.Id.FullName,
                };
                try
                {
                    if (!extension.Package.Status.VerifyIsOK())
                        throw new InvalidOperationException("The extension package is unavailable or requires repair.");
                    if (extension.Package.SignatureKind == PackageSignatureKind.None)
                        throw new InvalidDataException("The extension package must be signed.");
                    var folder = await extension.GetPublicFolderAsync();
                    plugin.PublicRoot = Path.GetFullPath(folder.Path);
                    plugin.Metadata = JsonSerializer.Deserialize<PluginMetadata>(
                        await File.ReadAllTextAsync(Path.Combine(plugin.PublicRoot, "metadata.json")), jsonOptions)
                        ?? throw new InvalidDataException("The extension metadata is empty.");
                    // MSIX deployment owns package trust; only validate the plugin metadata here.
                    PluginPackageSecurityService.ValidateMetadata(plugin.Metadata);
                    if (plugin.Metadata.BackendKind != PluginBackendKind.External || plugin.Metadata.ExternalBackend?.Windows is null)
                        throw new InvalidDataException("The extension must declare an external backend for Windows.");
                    if (plugin.Metadata.Version is null)
                        throw new InvalidDataException("The extension must declare a plugin version.");
                }
                catch (Exception ex)
                {
                    plugin.Metadata = null;
                    plugin.Failure = ex.Message;
                    Logger.Log(ex, $"Discover Windows plugin extension '{plugin.Key}'");
                }
                discovered.Add(plugin);
            }

            var items = PluginService.ReadPluginItems();
            foreach (var plugin in discovered)
            {
                var item = items.FirstOrDefault(c => c.IsMsixExtension
                    && c.MsixPackageFamilyName == plugin.Item.MsixPackageFamilyName
                    && c.MsixExtensionId == plugin.Item.MsixExtensionId);
                if (item is null)
                {
                    item = plugin.Item;
                    items.Add(item);
                }
                plugin.Item = item;
                if (plugin.Metadata is { } metadata)
                {
                    item.Id = metadata.PluginID;
                    item.Name = metadata.Name;
                    item.Author = metadata.Author;
                    item.Description = metadata.Description;
                    item.Version = metadata.Version;
                    item.PackageFormatVersion = metadata.PackageFormatVersion;
                    item.BackendKind = PluginBackendKind.External;
                    item.IsAppLevelPlugin = false;
                }
            }
            foreach (var item in items.Where(c => c.IsMsixExtension))
            {
                if (discovered.All(c => c.Item != item))
                    discovered.Add(new() { Item = item });
            }

            var reservedIds = items.Where(c => !c.IsMsixExtension && !c.ShouldRemove).Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            reservedIds.UnionWith((internalPlugins ?? PluginManager.LoadedPlugins.Values
                .Where(c => !IsLoadedExtension(c))).Select(c => c.PluginID));
            foreach (var group in discovered.Where(c => c.Metadata is not null).GroupBy(c => c.Item.Id, StringComparer.OrdinalIgnoreCase))
            {
                if (group.Count() > 1 || reservedIds.Contains(group.Key))
                    foreach (var plugin in group) plugin.HasConflict = true;
            }

            foreach (var pair in loaded.ToArray())
            {
                if (!PluginManager.LoadedPlugins.TryGetValue(pair.Value.Plugin.PluginID, out var instance)
                    || !ReferenceEquals(instance, pair.Value.Plugin))
                {
                    loaded.TryRemove(pair.Key, out _);
                    continue;
                }
                var current = discovered.FirstOrDefault(c => c.Key == pair.Key);
                if (current?.IsAvailable == true && current.PackageFullName == pair.Value.PackageFullName
                    && current.Item.Id == instance.PluginID && current.Item.Enabled) continue;
                if (current?.Extension is not null && (current.PackageFullName != pair.Value.PackageFullName
                    || current.Item.Id != instance.PluginID)) restartRequired.Add(pair.Key);
                PluginManager.Unload(instance.PluginID);
                loaded.TryRemove(pair.Key, out _);
            }
            foreach (var plugin in discovered) plugin.RequiresRestart = restartRequired.Contains(plugin.Key);

            if (persist) SaveItems(items);
            Plugins = discovered;
            DiscoveryFailure = null;
            return true;
        }
        catch (Exception ex)
        {
            DiscoveryFailure = ex.Message;
            Logger.Log(ex, "Refresh Windows plugin extensions");
            return false;
        }
        finally
        {
            operationLock.Release();
        }
    }

    private static void SaveItems(List<PluginService.PluginItem> items)
    {
        var path = Path.Combine(MauiProgram.BasicDataPath, "plugins.json");
        var json = JsonSerializer.Serialize(items);
        if (!File.Exists(path) || File.ReadAllText(path) != json)
            File.WriteAllText(path, json);
    }

    public static bool IsLoadedExtension(IPluginBase plugin) => loaded.Values.Any(c => ReferenceEquals(c.Plugin, plugin));

    public static string? GetLoadFailure(WindowsAppExtensionPlugin plugin) => loadFailures.GetValueOrDefault(plugin.Key);

    public static string? GetLoadedPluginRoot(string pluginId) => Plugins.FirstOrDefault(c => c.Item.Id == pluginId
        && loaded.TryGetValue(c.Key, out var instance)
        && PluginManager.LoadedPlugins.GetValueOrDefault(pluginId) == instance.Plugin)?.PublicRoot;

    public static bool IsVerifiedRoot(string pluginId, string root) => DiscoveryFailure is null && Plugins.Any(c =>
        c.IsAvailable && c.Item.Enabled && c.Item.Id == pluginId
        && string.Equals(c.PublicRoot, root, StringComparison.OrdinalIgnoreCase)
        && c.Extension!.Package.Status.VerifyIsOK()
        && c.Extension.Package.SignatureKind != PackageSignatureKind.None);

    public static List<IPluginBase> LoadEnabledPlugins()
    {
        List<IPluginBase> plugins = [];
        if (!IsPluginEngineEnabled || DiscoveryFailure is not null) return plugins;
        foreach (var item in Plugins.Where(c => c.Item.Enabled && c.IsAvailable).ToArray())
        {
            if (PluginManager.LoadedPlugins.ContainsKey(item.Item.Id)) continue;
            var result = TaskHelper.SyncWait(() => CreateAsync(item), CancellationToken.None);
            if (result.Plugin is not null) plugins.Add(result.Plugin);
        }
        return plugins;
    }

    public static async Task<(IPluginBase? Plugin, string FailReason)> CreateFromIDAsync(string pluginId)
    {
        if (refreshTask is null) await RefreshAsync();
        else await refreshTask;
        var matches = Plugins.Where(c => c.Item.Id == pluginId).ToArray();
        return matches.Length == 1
            ? await CreateAsync(matches[0])
            : (null, "The Windows plugin extension is unavailable or has a conflicting plugin id.");
    }

    private static async Task<(IPluginBase? Plugin, string FailReason)> CreateAsync(WindowsAppExtensionPlugin plugin)
    {
        await operationLock.WaitAsync();
        try
        {
            return await CreateCoreAsync(plugin);
        }
        finally
        {
            operationLock.Release();
        }
    }

    private static async Task<(IPluginBase? Plugin, string FailReason)> CreateCoreAsync(WindowsAppExtensionPlugin plugin)
    {
        try
        {
            plugin = Plugins.First(c => c.Key == plugin.Key);
            if (loaded.TryGetValue(plugin.Key, out var running)
                && PluginManager.LoadedPlugins.GetValueOrDefault(plugin.Item.Id) == running.Plugin)
                return (running.Plugin, string.Empty);
            if (!IsPluginEngineEnabled || DiscoveryFailure is not null || !plugin.IsAvailable || !plugin.Item.Enabled)
                throw new InvalidOperationException("The Windows plugin extension is disabled, unavailable, or requires a restart.");
            if (PluginManager.LoadedPlugins.ContainsKey(plugin.Item.Id))
                throw new InvalidOperationException("The plugin id is already in use.");
            var folder = await plugin.Extension!.GetPublicFolderAsync();
            if (!IsVerifiedRoot(plugin.Item.Id, Path.GetFullPath(folder.Path)))
                throw new InvalidOperationException("The Windows plugin extension package is no longer available.");
            Dictionary<string, string> configuration = [];
            var optionPath = Path.Combine(MauiProgram.BasicDataPath, "Plugins", plugin.Item.Id, "option.json");
            if (File.Exists(optionPath))
            {
                try { configuration = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(optionPath)) ?? []; }
                catch (Exception ex) { Logger.Log(ex, $"Load configuration for Windows plugin '{plugin.Item.Id}'"); }
            }
            var instance = await PluginIsolationFactory.CreateExternalAsync(plugin.Metadata!, plugin.PublicRoot!, configuration);
            if (!IsVerifiedRoot(plugin.Item.Id, plugin.PublicRoot!) || !plugin.Item.Enabled)
            {
                instance.OnClosing();
                throw new InvalidOperationException("The Windows plugin extension changed during startup.");
            }
            loaded[plugin.Key] = (plugin.PackageFullName!, instance);
            loadFailures.TryRemove(plugin.Key, out _);
            return (instance, string.Empty);
        }
        catch (Exception ex)
        {
            loadFailures[plugin.Key] = ex.Message;
            Logger.Log(ex, $"Load Windows plugin extension '{plugin.Key}'");
            return (null, ex.Message);
        }
    }

    public static async Task SetEnabledAsync(string key, bool enabled)
    {
        await RefreshAsync();
        await operationLock.WaitAsync();
        try
        {
            var plugin = Plugins.First(c => c.Key == key);
            if (enabled && (!IsPluginEngineEnabled || DiscoveryFailure is not null || !plugin.IsAvailable))
                throw new InvalidOperationException("The Windows plugin extension cannot be enabled in its current state.");
            var items = PluginService.ReadPluginItems();
            var item = items.First(c => c.MsixPackageFamilyName == plugin.Item.MsixPackageFamilyName
                && c.MsixExtensionId == plugin.Item.MsixExtensionId);
            item.Enabled = enabled;
            SaveItems(items);
            plugin.Item.Enabled = enabled;
            if (enabled)
            {
                var result = await CreateCoreAsync(plugin);
                if (result.Plugin is null) throw new InvalidOperationException(result.FailReason);
                PluginManager.LoadFrom(result.Plugin);
            }
            else
            {
                if (loaded.TryRemove(key, out var running)
                    && PluginManager.LoadedPlugins.GetValueOrDefault(running.Plugin.PluginID) == running.Plugin)
                    PluginManager.Unload(running.Plugin.PluginID);
                loadFailures.TryRemove(key, out _);
            }
        }
        finally
        {
            operationLock.Release();
        }
    }
}
