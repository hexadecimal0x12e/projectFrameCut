#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
using projectFrameCut.Render.RPCProtocol;
using projectFrameCut.Services;
using OperationCanceledException = System.OperationCanceledException;

namespace projectFrameCut.Platforms.Android;

/// <summary>
/// Hosts the editor RPC backend in its own process. Keeping it separate from
/// <see cref="RenderWorkerService"/> lets the editor force-restart RPC without
/// terminating independent background render jobs.
/// </summary>
[Service(
    Name = "com.hexadecimal0x12e.projectFrameCut.RenderRpcService",
    Exported = false,
    Process = ":rpcserver")]
public sealed class RenderRpcService : Service
{
    private const string NotificationChannelId = "projectframecut-render-rpc";
    private const int ForegroundNotificationId = 4101;

    private readonly Binder _binder = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CancellationTokenSource? _workerCancellation;
    private Task? _workerTask;
    private string? _socketPath;
    private bool _localizationInitialized;

    public override void OnCreate()
    {
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            if (e.ExceptionObject is Exception ex) Log(ex, "Unhandled exception in RenderWorkerService");
        };

        // Handle Android runtime (Java/Managed interop) layer exceptions
        global::Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (sender, e) =>
        {
            Log(e.Exception, "Unhandled Android runtime exception in RenderWorkerService");
        };
        base.OnCreate();
        EnsureNotificationChannel();
    }

    public override IBinder OnBind(Intent? intent)
    {
        return _binder;
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent is null) return StartCommandResult.NotSticky;

        if (string.Equals(intent.Action, AndroidRenderWorkerController.ActionForceStopWorker, StringComparison.Ordinal))
        {
            ForceStopProcess();
            return StartCommandResult.NotSticky;
        }

        if (string.Equals(intent.Action, AndroidRenderWorkerController.ActionStopWorker, StringComparison.Ordinal))
        {
            _ = StopServiceAsync(
                startId,
                intent.GetStringExtra(AndroidRenderWorkerController.ExtraProcessIdPath));
            return StartCommandResult.NotSticky;
        }

        InitializeLocalization(intent);
        StartForeground(ForegroundNotificationId, BuildNotification(
            Localized.DraftPage_RenderWorkerWorking(
                intent.GetStringExtra(AndroidRenderWorkerController.ProjectNameOption) ?? Localized._Unknown)));
        QueueStart(intent);
        return StartCommandResult.NotSticky;
    }

    private void InitializeLocalization(Intent intent)
    {
        if (_localizationInitialized) return;
        MauiProgram.InitializeWorkerLocalization(
            intent.GetStringExtra(AndroidRenderWorkerController.ExtraLocale));
        _localizationInitialized = true;
    }

    private void QueueStart(Intent intent)
    {
        var socketPath = intent.GetStringExtra(AndroidRenderWorkerController.ExtraSocketPath);
        var token = intent.GetStringExtra(AndroidRenderWorkerController.ExtraToken);
        if (string.IsNullOrWhiteSpace(socketPath) || string.IsNullOrWhiteSpace(token)) return;

        var dataRoot = intent.GetStringExtra(AndroidRenderWorkerController.ExtraDataRoot);
        if (string.IsNullOrWhiteSpace(dataRoot)) dataRoot = MauiProgram.BasicDataPath;
        var ffmpegRoot = intent.GetStringExtra(AndroidRenderWorkerController.ExtraFfmpegRoot) ?? string.Empty;
        var processIdPath = intent.GetStringExtra(AndroidRenderWorkerController.ExtraProcessIdPath);
        WriteProcessId(processIdPath);
        _ = ReplaceWorkerAsync(socketPath, token, dataRoot, ffmpegRoot);
    }

    private async Task ReplaceWorkerAsync(string socketPath, string token, string dataRoot, string ffmpegRoot)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (string.Equals(_socketPath, socketPath, StringComparison.Ordinal)
                && _workerTask is { IsCompleted: false }) return;

            await StopWorkerCoreAsync().ConfigureAwait(false);
            var cancellation = new CancellationTokenSource();
            _workerCancellation = cancellation;
            _socketPath = socketPath;
            _workerTask = Task.Run(
                () => RunWorkerAsync(socketPath, token, dataRoot, ffmpegRoot, cancellation.Token),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log(ex, "Start Android render RPC service");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private static async Task RunWorkerAsync(
        string socketPath,
        string token,
        string dataRoot,
        string ffmpegRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            CLIProgram.InitializeRenderRuntime(dataRoot, ffmpegRoot);
            await using var backend = new RenderBackendService(
                stateRoot: dataRoot,
                completionSink: RenderCompletionNotifier.Notify,
                progressSink: RenderCompletionNotifier.NotifyProgress);
            await new UnixSocketRenderServer(backend)
                .RunAsync(socketPath, token, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(socketPath)) File.Delete(socketPath); } catch { }
        }
    }

    private async Task StopServiceAsync(int startId, string? processIdPath)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopWorkerCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }

        StopForeground(StopForegroundFlags.Remove);
        StopSelfResult(startId);
        try { if (!string.IsNullOrWhiteSpace(processIdPath) && File.Exists(processIdPath)) File.Delete(processIdPath); } catch { }
    }

    private async Task StopWorkerCoreAsync()
    {
        var cancellation = _workerCancellation;
        var task = _workerTask;
        var socketPath = _socketPath;
        _workerCancellation = null;
        _workerTask = null;
        _socketPath = null;

        try { cancellation?.Cancel(); } catch { }
        if (task is not null)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (TimeoutException) { Log("Timed out stopping the Android render RPC worker.", "warn"); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log(ex, "Stop Android render RPC worker"); }
        }
        cancellation?.Dispose();
        try { if (!string.IsNullOrWhiteSpace(socketPath) && File.Exists(socketPath)) File.Delete(socketPath); } catch { }
    }

    private void ForceStopProcess()
    {
        try { _workerCancellation?.Cancel(); } catch { }
        StopForeground(StopForegroundFlags.Remove);
        StopSelf();
        global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
    }

    public override void OnDestroy()
    {
        try { _workerCancellation?.Cancel(); } catch { }
        _workerCancellation?.Dispose();
        _workerCancellation = null;
        _binder.Dispose();
        _lifecycleGate.Dispose();
        base.OnDestroy();
    }

    private static void WriteProcessId(string? processIdPath)
    {
        if (string.IsNullOrWhiteSpace(processIdPath)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(processIdPath)!);
            File.WriteAllText(processIdPath, global::Android.OS.Process.MyPid().ToString());
        }
        catch (Exception ex) { Log(ex, "Write Android render RPC process ID"); }
    }

    private void EnsureNotificationChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.CreateNotificationChannel(new NotificationChannel(
            NotificationChannelId,
            "Preview rendering",
            NotificationImportance.Low)
        {
            Description = "Keeps the projectFrameCut editor render RPC backend alive.",
        });
    }

    private Notification BuildNotification(string text)
    {
        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(this, NotificationChannelId)
            : new Notification.Builder(this);
        return builder
            .SetContentTitle("projectFrameCut")
            .SetContentText(text)
            .SetSmallIcon(ApplicationInfo?.Icon ?? global::Android.Resource.Drawable.StatSysDownload)
            .SetOngoing(true)
            .Build();
    }
}
#endif
