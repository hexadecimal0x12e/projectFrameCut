#if !HEADLESS
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System.Diagnostics;

namespace projectFrameCut.Services;

internal static class CustomEffectPortEditorService
{
    private sealed class EditorSession(DraftPage? page, IEffectProvider provider, string fieldId)
    {
        public DraftPage? Page { get; } = page;
        public IEffectProvider Provider { get; } = provider;
        public string FieldId { get; } = fieldId;
        public string PipeToken { get; set; } = string.Empty;
        public bool Connected { get; set; }
    }

    private static readonly object Gate = new();
    private static EditorSession? activeEditor;
    internal static event Action? StateChanged;

    internal static bool IsOpen { get { lock (Gate) return activeEditor is not null; } }

    internal static bool IsConnecting(IEffectProvider provider, string fieldId)
    {
        lock (Gate)
            return activeEditor is { Connected: false } editor && ReferenceEquals(editor.Provider, provider) && editor.FieldId == fieldId;
    }

    private static void NotifyStateChanged() => MainThread.BeginInvokeOnMainThread(() => StateChanged?.Invoke());

    internal static void ConnectionChanged(DraftPage page, string pipeToken, bool connected)
    {
        lock (Gate)
        {
            if (activeEditor is not { } editor || !ReferenceEquals(editor.Page, page) || editor.PipeToken != pipeToken) return;
            if (connected) editor.Connected = true;
            else activeEditor = null;
        }
        NotifyStateChanged();
    }

    internal static void Reset(DraftPage page)
    {
        lock (Gate)
        {
            if (activeEditor is null || !ReferenceEquals(activeEditor.Page, page)) return;
            activeEditor = null;
        }
        NotifyStateChanged();
    }

    public static bool IsAvailable(DraftPage? page, ClipElementUI clip, IEffectProvider provider, string fieldId)
    {
        try
        {
            ResolveStartInfo(page, clip, provider, fieldId);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    public static async Task OpenAsync(DraftPage? page, ClipElementUI clip, IEffectProvider provider, string fieldId)
    {
        var editor = new EditorSession(page, provider, fieldId);
        lock (Gate)
        {
            if (activeEditor is not null) return;
            activeEditor = editor;
        }
        NotifyStateChanged();
        try
        {
            var info = ResolveStartInfo(page, clip, provider, fieldId);
            var pipe = await page!.CreateCustomEffectEditorPipeAsync(provider.Name);
            lock (Gate)
            {
                if (!ReferenceEquals(activeEditor, editor)) throw new InvalidOperationException("The editor session was closed.");
                editor.PipeToken = pipe[RenderProtocol.AdditionalPipePrefix.Length..];
            }
            ValidateContext(page, clip, provider);
            ExternalRpcPipeSettings.EnsureAllowed(MauiProgram.BasicDataPath);
            if (!info.UseShellExecute && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(info.FileName, File.GetUnixFileMode(info.FileName) | UnixFileMode.UserExecute);
            foreach (var argument in new[]
            {
                $"--pipe={pipe}", $"--clipId={clip.Id}", $"--providerId={provider.Id}",
                $"--fieldId={fieldId}", $"--parentPid={Environment.ProcessId}",
            }) info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            if (process is null && !info.UseShellExecute)
                throw new InvalidOperationException("The external effect editor did not start.");
        }
        catch (Exception ex)
        {
            lock (Gate)
            {
                if (ReferenceEquals(activeEditor, editor)) activeEditor = null;
            }
            NotifyStateChanged();
            Logger.Log(ex, $"Open external editor for provider {provider.Id}, field '{fieldId}'", typeof(CustomEffectPortEditorService));
            if (page is not null && !page.IsClosed)
                await page.DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
        }
    }

    private static void ValidateContext(DraftPage? page, ClipElementUI clip, IEffectProvider provider)
    {
        if (page is null || page.IsClosed || page.IsReadonly || page.IsRemoteProject)
            throw new InvalidOperationException("The external effect editor requires an open, writable local project.");
        if (!ReferenceEquals(page.Clips.GetValueOrDefault(clip.Id), clip)
            || !ReferenceEquals(clip.EffectProviders?.GetValueOrDefault(provider.Id), provider))
            throw new InvalidOperationException("The clip or effect provider is no longer available.");
    }

    private static ProcessStartInfo ResolveStartInfo(DraftPage? page, ClipElementUI clip, IEffectProvider provider, string fieldId)
    {
        ValidateContext(page, clip, provider);
        if (!RenderRpcBootstrap.SupportsCliRenderProcess)
            throw new PlatformNotSupportedException("The external effect editor requires an RPC render backend.");
        var field = provider.Fields.GetValueOrDefault(fieldId) ?? provider.InFields.GetValueOrDefault(fieldId);
        if (field?.TypeContract is null || !field.FieldType.HasFlag(EffectArgumentFieldType.CustomType))
            throw new InvalidOperationException("The field is not a custom port.");
        var format = field.DataFormat ?? provider.InFields.GetValueOrDefault(fieldId)?.DataFormat;
        var launch = format is CustomEffectPortEditorConfiguration editor ? editor.GetCurrentPlatform() : null;
        if (launch is null || string.IsNullOrWhiteSpace(launch.EntryPoint))
            throw new PlatformNotSupportedException("The custom port has no editor for this platform.");
        var root = GetPluginRoot(provider.FromPlugin);
        var entryPoint = Path.GetFullPath(Path.IsPathRooted(launch.EntryPoint) ? launch.EntryPoint : Path.Combine(root, launch.EntryPoint));
        if (!Path.IsPathRooted(launch.EntryPoint)
            && !entryPoint.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The external effect editor entry point escapes the plugin directory.");
        if (!File.Exists(entryPoint)) throw new FileNotFoundException("The external effect editor was not found.", entryPoint);
        var info = new ProcessStartInfo
        {
            FileName = entryPoint, WorkingDirectory = root,
            UseShellExecute = launch.UseShellExecute, CreateNoWindow = launch.CreateNoWindow,
        };
        foreach (var argument in launch.Arguments) info.ArgumentList.Add(argument);
        return info;
    }

    private static string GetPluginRoot(string pluginId)
    {
        if (!PluginManager.LoadedPlugins.TryGetValue(pluginId, out var plugin))
            throw new InvalidOperationException("The effect plugin is no longer loaded.");
        if (ProjectPluginService.GetLoadedPluginRoot(pluginId) is { } projectRoot) return Path.GetFullPath(projectRoot);
#if WINDOWS
        if (WindowsAppExtensionService.GetLoadedPluginRoot(pluginId) is { } extensionRoot) return extensionRoot;
#endif
        if (plugin.Properties.TryGetValue("IsInternalPlugin", out var value) && bool.TryParse(value, out var isInternal) && isInternal)
            return AppContext.BaseDirectory;
        var root = Path.GetFullPath(Path.Combine(MauiProgram.BasicDataPath, "Plugins"));
        var pluginRoot = Path.GetFullPath(Path.Combine(root, pluginId));
        if (!string.Equals(Path.GetDirectoryName(pluginRoot), root,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The effect plugin directory is invalid.");
        if (!Directory.Exists(pluginRoot)) throw new DirectoryNotFoundException("The effect plugin directory was not found.");
        return pluginRoot;
    }
}
#endif
