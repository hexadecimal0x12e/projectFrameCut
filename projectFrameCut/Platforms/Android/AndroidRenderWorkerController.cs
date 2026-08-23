using Android.Content;
using Android.OS;
using projectFrameCut.Services;
using System.Text.Json;


namespace projectFrameCut.Platforms.Android;

internal sealed class AndroidRenderWorkerController : Java.Lang.Object, IServiceConnection, IAsyncDisposable
{
    internal const string ActionStartWorker = "com.hexadecimal0x12e.projectframecut.action.START_RENDER_WORKER";
    internal const string ActionStopWorker = "com.hexadecimal0x12e.projectframecut.action.STOP_RENDER_WORKER";
    internal const string ActionForceStopWorker = "com.hexadecimal0x12e.projectframecut.action.FORCE_STOP_RENDER_WORKER";
    internal const string ExtraSocketPath = "socketPath";
    internal const string ExtraToken = "token";
    internal const string ExtraDataRoot = "dataRoot";
    internal const string ExtraProjectRoot = "projectRoot";
    internal const string ExtraFfmpegRoot = "ffmpegRoot";
    internal const string ExtraLocale = "locale";
    internal const string ExtraIndependent = "independent";
    internal const string ExtraRenderOptions = "renderOptions";
    internal const string ExtraProcessIdPath = "processIdPath";
    internal const string ProjectNameOption = "projectName";

    private readonly Context _context;
    private readonly string _socketPath;
    private readonly string? _processIdPath;
    private readonly Type _serviceType;
    private readonly bool _stopOnDispose;
    private bool _bound;
    private int _disposed;

    private AndroidRenderWorkerController(Context context, Type serviceType, string socketPath, string? processIdPath, bool stopOnDispose)
    {
        _context = context;
        _serviceType = serviceType;
        _socketPath = socketPath;
        _processIdPath = processIdPath;
        _stopOnDispose = stopOnDispose;
    }

    public static string CreateSocketPath()
    {
        var cacheRoot = global::Android.App.Application.Context.CacheDir?.AbsolutePath
            ?? throw new InvalidOperationException("Android cache directory is unavailable.");
        return Path.Combine(cacheRoot, $"r-{Guid.NewGuid():N}.sock");
    }

    public static AndroidRenderWorkerController StartEditorWorker(
        string socketPath,
        string token,
        string dataRoot,
        string? projectRoot,
        string projectName,
        string ffmpegRoot)
    {
        var context = global::Android.App.Application.Context;
        var processIdPath = $"{socketPath}.pid";
        var controller = new AndroidRenderWorkerController(context, typeof(RenderRpcService), socketPath, processIdPath, stopOnDispose: true);
        var intent = CreateIntent(context, typeof(RenderRpcService), socketPath, token, dataRoot, projectRoot, ffmpegRoot, independent: false, projectName, options: null, locale: Localized._LocaleId_, processIdPath);
        context.StartForegroundService(intent);
        controller._bound = context.BindService(intent, controller, Bind.AutoCreate);
        if (!controller._bound)
        {
            context.StopService(intent);
            controller.Dispose();
            throw new InvalidOperationException("Android refused to bind the render worker service.");
        }
        return controller;
    }

    public static AndroidRenderWorkerController StartRenderTask(
        string socketPath,
        string token,
        string dataRoot,
        string projectName,
        CliRenderProcessOptions options)
    {
        var context = global::Android.App.Application.Context;
        var controller = new AndroidRenderWorkerController(context, typeof(RenderWorkerService), socketPath, processIdPath: null, stopOnDispose: false);
        var intent = CreateIntent(context, typeof(RenderWorkerService), socketPath, token, dataRoot, options.ProjectRoot, options.FFmpegLibraryPath, independent: true, projectName, options, locale: Localized._LocaleId_, processIdPath: null);
        context.StartForegroundService(intent);
        return controller;
    }

    private static Intent CreateIntent(
        Context context,
        Type serviceType,
        string socketPath,
        string token,
        string dataRoot,
        string? projectRoot,
        string ffmpegRoot,
        bool independent,
        string projectName,
        CliRenderProcessOptions? options,
        string locale,
        string? processIdPath)
    {
        var intent = new Intent(context, serviceType);
        intent.SetAction(ActionStartWorker);
        intent.PutExtra(ExtraSocketPath, socketPath);
        intent.PutExtra(ExtraToken, token);
        intent.PutExtra(ExtraDataRoot, dataRoot);
        intent.PutExtra(ExtraProjectRoot, projectRoot ?? string.Empty);
        intent.PutExtra(ExtraFfmpegRoot, ffmpegRoot ?? string.Empty);
        intent.PutExtra(ExtraLocale, locale ?? string.Empty);
        intent.PutExtra(ExtraIndependent, independent);
        intent.PutExtra(ProjectNameOption, projectName);
        if (!string.IsNullOrWhiteSpace(processIdPath)) intent.PutExtra(ExtraProcessIdPath, processIdPath);
        if (options is not null) intent.PutExtra(ExtraRenderOptions, JsonSerializer.Serialize(options));
        return intent;
    }

    public void OnServiceConnected(ComponentName? name, IBinder? service) { }

    public void OnServiceDisconnected(ComponentName? name) => _bound = false;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        Unbind();
        if (_stopOnDispose) SendStopIntent(ActionStopWorker);
        return ValueTask.CompletedTask;
    }

    public async ValueTask ForceStopAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        int? processId = TryReadProcessId();
        Unbind();

        if (_stopOnDispose)
        {
            SendStopIntent(ActionForceStopWorker);
            if (processId is int pid)
                await WaitForProcessExitAsync(pid, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            CleanupLifecycleFiles();
        }
    }

    private void Unbind()
    {
        if (_bound)
        {
            try { _context.UnbindService(this); } catch { }
            _bound = false;
        }
    }

    private void SendStopIntent(string action)
    {
        try
        {
            var stopIntent = new Intent(_context, _serviceType);
            stopIntent.SetAction(action);
            stopIntent.PutExtra(ExtraSocketPath, _socketPath);
            if (!string.IsNullOrWhiteSpace(_processIdPath)) stopIntent.PutExtra(ExtraProcessIdPath, _processIdPath);
            _context.StartService(stopIntent);
        }
        catch { }
    }

    private int? TryReadProcessId()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_processIdPath)
                && int.TryParse(File.ReadAllText(_processIdPath), out int processId)
                && processId > 0)
                return processId;
        }
        catch { }
        return null;
    }

    private static async Task WaitForProcessExitAsync(int processId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var processPath = $"/proc/{processId}";
        while (Directory.Exists(processPath) && DateTime.UtcNow < deadline)
            await Task.Delay(50).ConfigureAwait(false);
    }

    private void CleanupLifecycleFiles()
    {
        try { if (File.Exists(_socketPath)) File.Delete(_socketPath); } catch { }
        try { if (!string.IsNullOrWhiteSpace(_processIdPath) && File.Exists(_processIdPath)) File.Delete(_processIdPath); } catch { }
    }
}
