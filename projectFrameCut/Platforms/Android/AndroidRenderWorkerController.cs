#if ANDROID
using Android.Content;
using Android.OS;
using projectFrameCut.Services;
using System.Text.Json;

namespace projectFrameCut.Platforms.Android;

internal sealed class AndroidRenderWorkerController : Java.Lang.Object, IServiceConnection, IAsyncDisposable
{
    internal const string ActionStartWorker = "com.hexadecimal0x12e.projectframecut.action.START_RENDER_WORKER";
    internal const string ActionStopWorker = "com.hexadecimal0x12e.projectframecut.action.STOP_RENDER_WORKER";
    internal const string ExtraSocketPath = "socketPath";
    internal const string ExtraToken = "token";
    internal const string ExtraDataRoot = "dataRoot";
    internal const string ExtraProjectRoot = "projectRoot";
    internal const string ExtraFfmpegRoot = "ffmpegRoot";
    internal const string ExtraIndependent = "independent";
    internal const string ExtraRenderOptions = "renderOptions";

    private readonly Context _context;
    private readonly string _socketPath;
    private readonly bool _stopOnDispose;
    private bool _bound;

    private AndroidRenderWorkerController(Context context, string socketPath, bool stopOnDispose)
    {
        _context = context;
        _socketPath = socketPath;
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
        string ffmpegRoot)
    {
        var context = global::Android.App.Application.Context;
        var controller = new AndroidRenderWorkerController(context, socketPath, stopOnDispose: true);
        var intent = CreateIntent(context, socketPath, token, dataRoot, projectRoot, ffmpegRoot, independent: false, options: null);
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
        CliRenderProcessOptions options)
    {
        var context = global::Android.App.Application.Context;
        var controller = new AndroidRenderWorkerController(context, socketPath, stopOnDispose: false);
        var intent = CreateIntent(context, socketPath, token, dataRoot, options.ProjectRoot, options.FFmpegLibraryPath, independent: true, options);
        context.StartForegroundService(intent);
        return controller;
    }

    private static Intent CreateIntent(
        Context context,
        string socketPath,
        string token,
        string dataRoot,
        string? projectRoot,
        string ffmpegRoot,
        bool independent,
        CliRenderProcessOptions? options)
    {
        var intent = new Intent(context, typeof(RenderWorkerService));
        intent.SetAction(ActionStartWorker);
        intent.PutExtra(ExtraSocketPath, socketPath);
        intent.PutExtra(ExtraToken, token);
        intent.PutExtra(ExtraDataRoot, dataRoot);
        intent.PutExtra(ExtraProjectRoot, projectRoot ?? string.Empty);
        intent.PutExtra(ExtraFfmpegRoot, ffmpegRoot ?? string.Empty);
        intent.PutExtra(ExtraIndependent, independent);
        if (options is not null) intent.PutExtra(ExtraRenderOptions, JsonSerializer.Serialize(options));
        return intent;
    }

    public void OnServiceConnected(ComponentName? name, IBinder? service) { }

    public void OnServiceDisconnected(ComponentName? name) => _bound = false;

    public ValueTask DisposeAsync()
    {
        if (_bound)
        {
            try { _context.UnbindService(this); } catch { }
            _bound = false;
        }
        if (_stopOnDispose)
        {
            try
            {
                var stopIntent = new Intent(_context, typeof(RenderWorkerService));
                stopIntent.SetAction(ActionStopWorker);
                stopIntent.PutExtra(ExtraSocketPath, _socketPath);
                _context.StartService(stopIntent);
            }
            catch { }
        }
        return ValueTask.CompletedTask;
    }
}
#endif
