#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RPCProtocol;
using projectFrameCut.Services;
using System.Collections.Concurrent;
using System.Text.Json;

namespace projectFrameCut.Platforms.Android;

[Service(
    Name = "com.hexadecimal0x12e.projectFrameCut.RenderWorkerService",
    Exported = false,
    Process = ":renderworker")]
public sealed class RenderWorkerService : Service
{
    private const string NotificationChannelId = "projectframecut-render-worker";
    private const int ForegroundNotificationId = 4100;
    private readonly ConcurrentDictionary<string, WorkerHost> _workers = new(StringComparer.Ordinal);
    private readonly Binder _binder = new();

    public override void OnCreate()
    {
        base.OnCreate();
        EnsureNotificationChannel();
    }

    public override IBinder OnBind(Intent? intent)
    {
        if (intent is not null) StartFromIntent(intent);
        return _binder;
    }

    public override bool OnUnbind(Intent? intent)
    {
        foreach (var item in _workers.Where(static item => !item.Value.Independent).ToArray())
            StopWorker(item.Key);
        return base.OnUnbind(intent);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        StartForeground(ForegroundNotificationId, BuildNotification("Render worker is running"));
        if (intent is null) return StartCommandResult.RedeliverIntent;

        if (string.Equals(intent.Action, AndroidRenderWorkerController.ActionStopWorker, StringComparison.Ordinal))
        {
            StopWorker(intent.GetStringExtra(AndroidRenderWorkerController.ExtraSocketPath));
            return StartCommandResult.NotSticky;
        }

        StartFromIntent(intent);
        return intent.GetBooleanExtra(AndroidRenderWorkerController.ExtraIndependent, false)
            ? StartCommandResult.RedeliverIntent
            : StartCommandResult.NotSticky;
    }

    private void StartFromIntent(Intent intent)
    {
        var socketPath = intent.GetStringExtra(AndroidRenderWorkerController.ExtraSocketPath);
        var token = intent.GetStringExtra(AndroidRenderWorkerController.ExtraToken);
        if (string.IsNullOrWhiteSpace(socketPath) || string.IsNullOrWhiteSpace(token)) return;
        if (_workers.ContainsKey(socketPath)) return;

        var dataRoot = intent.GetStringExtra(AndroidRenderWorkerController.ExtraDataRoot);
        if (string.IsNullOrWhiteSpace(dataRoot)) dataRoot = MauiProgram.BasicDataPath;
        var ffmpegRoot = intent.GetStringExtra(AndroidRenderWorkerController.ExtraFfmpegRoot) ?? string.Empty;
        var independent = intent.GetBooleanExtra(AndroidRenderWorkerController.ExtraIndependent, false);
        var optionsJson = intent.GetStringExtra(AndroidRenderWorkerController.ExtraRenderOptions);

        var host = new WorkerHost(socketPath, independent);
        if (!_workers.TryAdd(socketPath, host)) return;
        host.Task = independent && !string.IsNullOrWhiteSpace(optionsJson)
            ? Task.Run(() => RunRenderTaskAsync(host, token, dataRoot, optionsJson))
            : Task.Run(() => RunEditorWorkerAsync(host, token, dataRoot, ffmpegRoot));
        _ = host.Task.ContinueWith(
            _ => WorkerFinished(socketPath),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task RunEditorWorkerAsync(WorkerHost host, string token, string dataRoot, string ffmpegRoot)
    {
        CLIProgram.InitializeRenderRuntime(dataRoot, ffmpegRoot);
        await using var backend = new RenderBackendService(
            stateRoot: dataRoot,
            completionSink: RenderCompletionNotifier.Notify);
        await new UnixSocketRenderServer(backend)
            .RunAsync(host.SocketPath, token, host.Cancellation.Token)
            .ConfigureAwait(false);
    }

    private async Task RunRenderTaskAsync(WorkerHost host, string token, string dataRoot, string optionsJson)
    {
        var options = JsonSerializer.Deserialize<CliRenderProcessOptions>(optionsJson)
            ?? throw new InvalidDataException("Android render worker options are invalid.");
        PersistRegistration(host.SocketPath, token, dataRoot, options);
        var args = new[]
        {
            "render",
            $"--project={options.ProjectRoot}",
            $"--output={options.OutputPath}",
            $"--output_options={options.Width},{options.Height},{options.FrameRate},{options.PixelFormat},{options.Encoder}",
            "--target=all",
            $"--assetDbFile={options.AssetDatabasePath}",
            $"--FFmpegLibraryPath={options.FFmpegLibraryPath}",
            $"--temp_path={options.TempPath}",
            $"--maxParallelThreads={Math.Max(1, options.MaxParallelThreads)}",
            $"--oneByOneRender={options.OneByOneRender}",
            $"--GCOptions={Math.Clamp(options.GcOption, 0, 2)}",
            $"--enableThreadAffinity={options.EnableThreadAffinity}",
            $"--prepareInWorker={options.PrepareInWorker}",
            $"--renderByLayer={options.RenderByLayer}",
            $"--rpcSocket={host.SocketPath}",
            $"--rpcToken={token}",
            $"--jobId={options.JobId:D}",
            $"--projectName={options.ProjectName}",
            $"--background={options.Background}",
            "--consoleLog",
        };
        var renderDataRoot = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(options.AssetDatabasePath) ?? dataRoot,
            "..",
            ".."));
        var statePath = Path.Combine(renderDataRoot, "RenderJobs", $"cli-{options.JobId:N}.json");
        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(host.Cancellation.Token);
        var monitorTask = MonitorProgressAsync(statePath, options.ProjectName, monitorCancellation.Token);
        try
        {
            _ = await Task.Run(() => CLIProgram.CLIMain(args), host.Cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            monitorCancellation.Cancel();
            try { await monitorTask.ConfigureAwait(false); } catch (System.OperationCanceledException) { }
        }
    }

    private async Task MonitorProgressAsync(string statePath, string projectName, CancellationToken cancellationToken)
    {
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(statePath))
                {
                    var job = JsonSerializer.Deserialize<RenderJob>(File.ReadAllText(statePath));
                    if (job is not null)
                    {
                        var text = job.State is RenderJobState.Queued or RenderJobState.Running
                            ? $"{projectName}: {job.Progress:P0}"
                            : $"{projectName}: {job.State}";
                        manager?.Notify(ForegroundNotificationId, BuildNotification(text));
                    }
                }
            }
            catch { }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private static void PersistRegistration(string socketPath, string token, string dataRoot, CliRenderProcessOptions options)
    {
        try
        {
            var path = Path.Combine(dataRoot, "RenderJobs", "worker.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                Kind = "android-render",
                PipeName = socketPath,
                Token = token,
                ProcessId = global::Android.OS.Process.MyPid(),
                ProtocolVersion = RenderProtocol.PipeProtocolVersion,
                UpdatedAtUtc = DateTime.UtcNow,
                options.JobId,
                options.ProjectRoot,
                options.OutputPath,
            }));
        }
        catch (Exception ex) { Log(ex, "Persist Android render worker registration"); }
    }

    private void StopWorker(string? socketPath)
    {
        if (string.IsNullOrWhiteSpace(socketPath)) return;
        if (_workers.TryRemove(socketPath, out var host)) host.Dispose();
        StopWhenIdle();
    }

    private void WorkerFinished(string socketPath)
    {
        if (_workers.TryRemove(socketPath, out var host)) host.Dispose();
        StopWhenIdle();
    }

    private void StopWhenIdle()
    {
        if (!_workers.IsEmpty) return;
        StopForeground(StopForegroundFlags.Remove);
        StopSelf();
    }

    public override void OnDestroy()
    {
        foreach (var host in _workers.Values) host.Dispose();
        _workers.Clear();
        _binder.Dispose();
        base.OnDestroy();
    }

    private void EnsureNotificationChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        manager?.CreateNotificationChannel(new NotificationChannel(
            NotificationChannelId,
            "Rendering",
            NotificationImportance.Low)
        {
            Description = "Keeps projectFrameCut render workers alive while rendering.",
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

    private sealed class WorkerHost(string socketPath, bool independent) : IDisposable
    {
        public string SocketPath { get; } = socketPath;
        public bool Independent { get; } = independent;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? Task { get; set; }

        public void Dispose()
        {
            try { Cancellation.Cancel(); } catch { }
            Cancellation.Dispose();
        }
    }
}
#endif
