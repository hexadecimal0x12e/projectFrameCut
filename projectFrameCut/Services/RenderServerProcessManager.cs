using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using FFmpeg.AutoGen;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RPCProtocol;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Shared;
#if ANDROID
using projectFrameCut.Platforms.Android;
#endif

namespace projectFrameCut.Services;

internal sealed class RenderServerProcessManager : IAsyncDisposable
{
    private const int DefaultHttpRpcPort = 39485;
    private static readonly TimeSpan WorkerShutdownTimeout = TimeSpan.FromSeconds(30);

    private readonly string _clientId;
    private string _pipeName = $"projectFrameCut-render-{Guid.NewGuid():N}";
    private string _token = Convert.ToHexString(Guid.NewGuid().ToByteArray());
    private string? _projectRoot;
    private bool _independentWorker;
    private string _projectName = "UnnamedProject";
    private Process? _process;
    private IRenderTransport? _transport;
    private IRenderClient? _client;
    private RenderServiceHost? _directHost;
    private Guid? _jobId;
#if ANDROID
    private AndroidRenderWorkerController? _androidWorker;
#endif

    public RenderServerProcessManager(string clientId)
    {
        _clientId = string.IsNullOrWhiteSpace(clientId) ? $"client-{Guid.NewGuid():N}" : clientId;
    }

    public IRenderClient Client => _client ?? throw new InvalidOperationException("Render RPC server has not been started.");

    /// <summary>
    /// The project directory this backend was started for, or null when started
    /// without one. Used to keep each project bound to its own backend process.
    /// </summary>
    public string? ProjectRoot => _projectRoot;
    public bool IsIndependentWorker => _independentWorker;
    public Guid? JobId => _jobId;
    public string? CliPreviewPath { get; private set; }

    /// <summary>
    /// Requests a CLI render worker to stop and gives it a bounded grace period
    /// to release encoders, GPU resources and temporary files. The owner process
    /// remains responsible for the final cleanup when the worker is stuck.
    /// </summary>
    public async Task CancelCliRenderAsync(Guid jobId)
    {
        if (_client is null || _jobId != jobId) return;
        var deadline = DateTime.UtcNow + WorkerShutdownTimeout;

        try
        {
            await _client.CancelJobAsync(jobId).AsTask().WaitAsync(RemainingShutdownTime(deadline, TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }
        catch { }

        if (_process is null) return;

        try
        {
            // Closing the RPC session tells the worker to stop its server loop
            // once the render pipeline has observed cancellation.
            await _client.CloseProjectAsync(Guid.Empty).AsTask().WaitAsync(RemainingShutdownTime(deadline, TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }
        catch { }

        await WaitForWorkerExitOrKillAsync(_process, RemainingShutdownTime(deadline, TimeSpan.Zero)).ConfigureAwait(false);
    }

    public static bool SupportsCliRenderProcess
    {
        get
        {
#if WINDOWS || LINUX || MACOS || ANDROID
            return !SettingsManager.IsBoolSettingTrue("render_ForceDirectRenderTransport");
#else
            return false;
#endif
        }
    }

    /// <param name="projectRoot">
    /// Project directory to hand to the server process. The optional HTTP RPC
    /// server preloads it so clients do not hit a "server has no project" error.
    /// </param>
    /// <param name="projectName">
    /// The name of the project. This is used to identify the project in the UI.
    /// </param>
    public void Start(string? projectRoot = null, bool independentWorker = false, string? projectName = null)
    {
        if (_client is not null) return;
        _projectRoot = string.IsNullOrWhiteSpace(projectRoot) ? null : Path.GetFullPath(projectRoot);
        _independentWorker = independentWorker;
        _projectName = string.IsNullOrWhiteSpace(projectName) ? Localized._Unknown : projectName;

        if (!SupportsCliRenderProcess)
        {
            _directHost = new RenderServiceHost(_clientId);
            _client = _directHost.Client;
            return;
        }

#if ANDROID
        StartAndroidEditorWorker();
#else
        if (_independentWorker && TryConnectRegisteredWorker()) return;
        var enableHttp = SettingsManager.IsBoolSettingTrue("render_RpcServerEnableHttp");
#if WINDOWS
        Exception? ex1 = null, ex2 = null;
        try
        {
            StartProcess(Path.Combine(AppContext.BaseDirectory, $"pjfc-cli.exe"), enableHttp);
            return;
        }
        catch (Exception ex)
        {
            ex1 = ex;
        }
        try
        {
            StartProcess($"projectFrameCutCompatible_{AppInfo.PackageName}_{Assembly.GetExecutingAssembly().GetName().Version}.exe", enableHttp);
            return;
        }
        catch (Exception ex)
        {
            ex2 = ex;
        }

        throw new InvalidOperationException("Unable to start RPC server. Please ensure that the longer App alias is enabled in the Settings->Apps->Advanced->App execution alias.", new AggregateException(ex1, ex2));
#elif MACOS
        StartProcess(Path.Combine(Foundation.NSBundle.MainBundle.BundlePath, "Contents", "MacOS", "projectFrameCut_cli"), enableHttp);
#elif LINUX
        StartProcess(Path.Combine(AppContext.BaseDirectory, "projectFrameCut"), enableHttp);
#else
        throw new PlatformNotSupportedException("The projectFrameCut Render RPC server is not supported on this platform. Use In-Process RPC Backend mode.");
#endif

        void StartProcess(string execPath, bool withHttp)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = execPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            startInfo.ArgumentList.Add("rpc_server");
            startInfo.ArgumentList.Add($"--pipe={_pipeName}");
            startInfo.ArgumentList.Add($"--token={_token}");
            if (!_independentWorker)
            {
                startInfo.ArgumentList.Add($"--parentPid={Environment.ProcessId}");
            }
            startInfo.ArgumentList.Add($"--dataRoot={GlobalPluginHelper.PluginsDataRootPath}");
            if (_projectRoot is not null)
            {
                startInfo.ArgumentList.Add($"--projectRoot={_projectRoot}");
            }
            startInfo.ArgumentList.Add($"--ffmpegRoot={ffmpeg.RootPath}");
            startInfo.ArgumentList.Add($"--locale={Localized._LocaleId_}");
            startInfo.ArgumentList.Add($"--preferHwAccelDecoder={SettingsManager.IsBoolSettingTrueOrDefault("codec_PreferredHWAccelDecoding", true)}");
            startInfo.ArgumentList.Add($"--preferHwAccelEncoder={SettingsManager.IsBoolSettingTrueOrDefault("codec_PreferredHWAccelEncoding", true)}");
            startInfo.ArgumentList.Add("--quiet");

            if (withHttp)
            {
                var portSetting = SettingsManager.GetSetting("render_RpcServerHttpPort", "");
                var port = int.TryParse(portSetting, out var parsedPort) && parsedPort > 0 && parsedPort < 65536
                    ? parsedPort
                    : DefaultHttpRpcPort;
                var httpToken = Convert.ToHexString(Guid.NewGuid().ToByteArray());
                startInfo.ArgumentList.Add($"--http=http://127.0.0.1:{port}");
                startInfo.ArgumentList.Add($"--httpToken={httpToken}");
                Log($"Starting RPC server with HTTP endpoint on port {port} (token: {httpToken})");
            }

            startInfo.ArgumentList.Add("--consoleLog");
            if (MyLoggerExtensions.LoggingDiagnosticInfo)
            {
                startInfo.ArgumentList.Add("--logDiagnostic");
            }

            try
            {
                _process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Unable to start pjfc-cli.exe RPC server.");
                _process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        Log(e.Data, "RPCWorker Stderr");
                };
                _process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        Log(e.Data, "RPCWorker Stdout");
                };
                _process.BeginErrorReadLine();
                _process.BeginOutputReadLine();

                var transport = new NamedPipeRenderClientTransport(_pipeName, _token, _clientId);
                var client = new RenderClient(transport, _clientId);
                _transport = transport;
                _client = client;
                WaitForServer(client, _process);
                if (_independentWorker) SaveRegistration();
            }
            catch (Exception ex)
            {
                try { _client?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
                try { _process?.Kill(entireProcessTree: true); } catch { }
                _process?.Dispose();
                _process = null;
                _client = null;
                _transport = null;
                if (withHttp)
                {
                    // A failing optional HTTP endpoint (for example the port is
                    // already taken) must not take down the named-pipe backend.
                    Debug.WriteLine($"[RenderRPC] Starting the RPC server with the HTTP endpoint failed ({ex.Message}); retrying without --http.");
                    StartProcess(execPath, false);
                    return;
                }
                throw;
            }
        }
#endif
    }

    public void StartCliRender(CliRenderProcessOptions options)
    {
        if (_client is not null) return;
        if (!SupportsCliRenderProcess)
            throw new PlatformNotSupportedException("An external CLI render process is not available on this platform or has been disabled in settings.");

        _projectRoot = Path.GetFullPath(options.ProjectRoot);
        _independentWorker = true;
        _jobId = options.JobId == Guid.Empty ? Guid.NewGuid() : options.JobId;
        CliPreviewPath = options.PreviewPath;
#if ANDROID
        StartAndroidRenderTask(options with { JobId = _jobId.Value });
#else
        StartCliProcess(options with { JobId = _jobId.Value });
#endif
    }

    public bool TryReconnectCliRender(string projectRoot)
    {
        if (_client is not null || !SupportsCliRenderProcess) return false;
#if ANDROID
        return TryReconnectAndroidRenderTask(projectRoot);
#else
        try
        {
            if (!File.Exists(RegistrationPath)) return false;
            var registration = System.Text.Json.JsonSerializer.Deserialize<WorkerRegistration>(File.ReadAllText(RegistrationPath));
            if (registration is null || !string.Equals(registration.Kind, "cli-render", StringComparison.Ordinal)) return false;
            if (registration.JobId == Guid.Empty || string.IsNullOrWhiteSpace(registration.ProjectRoot)) return false;
            if (!SamePath(registration.ProjectRoot, projectRoot)) return false;
            using var process = Process.GetProcessById(registration.ProcessId);
            if (process.HasExited) return false;

            _projectRoot = Path.GetFullPath(projectRoot);
            _independentWorker = true;
            _jobId = registration.JobId;
            CliPreviewPath = registration.PreviewPath;
            _pipeName = registration.PipeName;
            _token = registration.Token;
            var transport = new NamedPipeRenderClientTransport(_pipeName, _token, _clientId);
            var client = new RenderClient(transport, _clientId);
            _transport = transport;
            _client = client;
            WaitForServer(client, null);
            return true;
        }
        catch
        {
            try { _client?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            _client = null;
            _transport = null;
            _jobId = null;
            return false;
        }
#endif
    }

#if ANDROID
    private void StartAndroidEditorWorker()
    {
        _pipeName = AndroidRenderWorkerController.CreateSocketPath();
        var dataRoot = GlobalPluginHelper.PluginsDataRootPath ?? MauiProgram.BasicDataPath;
        try
        {
            _androidWorker = AndroidRenderWorkerController.StartEditorWorker(
                _pipeName,
                _token,
                dataRoot,
                _projectRoot,
                _projectName,
                ffmpeg.RootPath);
            ConnectAndroidTransport();
        }
        catch
        {
            try { _client?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            try { _androidWorker?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            _androidWorker = null;
            _client = null;
            _transport = null;
            throw;
        }
    }

    private void StartAndroidRenderTask(CliRenderProcessOptions options)
    {
        _pipeName = AndroidRenderWorkerController.CreateSocketPath();
        var dataRoot = GlobalPluginHelper.PluginsDataRootPath ?? MauiProgram.BasicDataPath;
        try
        {
            _androidWorker = AndroidRenderWorkerController.StartRenderTask(
                _pipeName,
                _token,
                dataRoot,
                _projectName,
                options);
            ConnectAndroidTransport();
        }
        catch
        {
            try { _client?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            _androidWorker = null;
            _client = null;
            _transport = null;
            _jobId = null;
            throw;
        }
    }

    private bool TryReconnectAndroidRenderTask(string projectRoot)
    {
        try
        {
            if (!File.Exists(RegistrationPath)) return false;
            var registration = System.Text.Json.JsonSerializer.Deserialize<WorkerRegistration>(File.ReadAllText(RegistrationPath));
            if (registration is null || !string.Equals(registration.Kind, "android-render", StringComparison.Ordinal)) return false;
            if (registration.JobId == Guid.Empty || string.IsNullOrWhiteSpace(registration.ProjectRoot)) return false;
            if (string.IsNullOrWhiteSpace(registration.PipeName) || string.IsNullOrWhiteSpace(registration.Token)) return false;
            if (!File.Exists(registration.PipeName)) return false;
            if (!SamePath(registration.ProjectRoot, projectRoot)) return false;

            _projectRoot = Path.GetFullPath(projectRoot);
            _independentWorker = true;
            _jobId = registration.JobId;
            _pipeName = registration.PipeName;
            _token = registration.Token;
            ConnectAndroidTransport();
            return true;
        }
        catch
        {
            try { _client?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            _client = null;
            _transport = null;
            _jobId = null;
            return false;
        }
    }

    private void ConnectAndroidTransport()
    {
        var transport = new UnixSocketRenderClientTransport(_pipeName, _token, _clientId);
        var client = new RenderClient(transport, _clientId);
        _transport = transport;
        _client = client;
        WaitForServer(client, null);
    }

#endif

    private void StartCliProcess(CliRenderProcessOptions options)
    {
        Exception? firstError = null;
        foreach (var executable in GetCliExecutableCandidates())
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };
                startInfo.ArgumentList.Add("render");
                Add("project", options.ProjectRoot);
                Add("output", options.OutputPath);
                Add("output_options", $"{options.Width},{options.Height},{options.FrameRate},{options.PixelFormat},{options.Encoder}");
                Add("target", options.WriteToVoid ? "void" : "all");
                Add("assetDbFile", options.AssetDatabasePath);
                Add("FFmpegLibraryPath", options.FFmpegLibraryPath);
                Add("maxParallelThreads", Math.Max(1, options.MaxParallelThreads).ToString());
                Add("oneByOneRender", options.OneByOneRender.ToString());
                Add("GCOptions", Math.Clamp(options.GcOption, 0, 2).ToString());
                Add("enableThreadAffinity", options.EnableThreadAffinity.ToString());
                Add("prepareInWorker", options.PrepareInWorker.ToString());
                Add("renderByLayer", options.RenderByLayer.ToString());
                Add("rpcPipe", _pipeName);
                Add("rpcToken", _token);
                Add("jobId", options.JobId.ToString("D"));
                Add("projectName", options.ProjectName);
                Add("background", options.Background.ToString());
                Add("temp_path", options.TempPath.ToString());
                if (!string.IsNullOrWhiteSpace(options.PreviewPath)) Add("preview_path", options.PreviewPath);
                startInfo.ArgumentList.Add("--consoleLog");
                if (MyLoggerExtensions.LoggingDiagnosticInfo) startInfo.ArgumentList.Add("--logDiagnostic");

                _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start the CLI renderer.");
                AttachProcessLogging(_process);
                var transport = new NamedPipeRenderClientTransport(_pipeName, _token, _clientId);
                var client = new RenderClient(transport, _clientId);
                _transport = transport;
                _client = client;
                WaitForServer(client, _process);
                SaveRegistration("cli-render", options);
                return;

                void Add(string name, string value) => startInfo.ArgumentList.Add($"--{name}={value}");
            }
            catch (Exception ex)
            {
                firstError ??= ex;
                try { _client?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
                try { _process?.Kill(entireProcessTree: true); } catch { }
                _process?.Dispose();
                _process = null;
                _client = null;
                _transport = null;
            }
        }
        throw new InvalidOperationException("Unable to start pjfc-cli in render mode.", firstError);
    }

    private static IEnumerable<string> GetCliExecutableCandidates()
    {
#if WINDOWS
        yield return Path.Combine(AppContext.BaseDirectory, "pjfc-cli.exe");
        yield return $"projectFrameCutCompatible_{AppInfo.PackageName}_{Assembly.GetExecutingAssembly().GetName().Version}.exe";
#elif MACOS
        yield return Path.Combine(Foundation.NSBundle.MainBundle.BundlePath, "Contents", "MacOS", "projectFrameCut_cli");
#elif LINUX
        yield return Path.Combine(AppContext.BaseDirectory, "projectFrameCut");
#else
        yield break;
#endif
    }

    private static void AttachProcessLogging(Process process)
    {
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) Log(e.Data, "CLI Renderer Stderr");
        };
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) Log(e.Data, "CLI Renderer Stdout");
        };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
    }

    private string RegistrationPath => Path.Combine(GlobalPluginHelper.PluginsDataRootPath ?? AppContext.BaseDirectory, "RenderJobs", "worker.json");

    private bool TryConnectRegisteredWorker()
    {
        try
        {
            if (!File.Exists(RegistrationPath)) return false;
            var registration = System.Text.Json.JsonSerializer.Deserialize<WorkerRegistration>(File.ReadAllText(RegistrationPath));
            if (registration is null || string.IsNullOrWhiteSpace(registration.PipeName) || string.IsNullOrWhiteSpace(registration.Token)) return false;
            if (!string.IsNullOrWhiteSpace(registration.Kind) && !string.Equals(registration.Kind, "rpc-server", StringComparison.Ordinal)) return false;
            // An independent backend is project-scoped. Never attach to a
            // live worker that belongs to another project: doing so makes
            // preview requests resolve against the wrong timeline/assets.
            if (_projectRoot is not null
                && (string.IsNullOrWhiteSpace(registration.ProjectRoot)
                    || !SamePath(registration.ProjectRoot, _projectRoot))) return false;
            if (registration.ProcessId > 0)
            {
                using var process = Process.GetProcessById(registration.ProcessId);
                if (process.HasExited) return false;
            }
            _pipeName = registration.PipeName;
            _token = registration.Token;
            var transport = new NamedPipeRenderClientTransport(_pipeName, _token, _clientId);
            var client = new RenderClient(transport, _clientId);
            _transport = transport;
            _client = client;
            WaitForServer(client, null);
            return true;
        }
        catch
        {
            try { _client?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            _client = null;
            _transport = null;
            return false;
        }
    }

    private void SaveRegistration()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RegistrationPath)!);
            var registration = new WorkerRegistration
            {
                Kind = "rpc-server",
                PipeName = _pipeName,
                Token = _token,
                ProcessId = _process?.Id ?? 0,
                ProtocolVersion = RenderProtocol.PipeProtocolVersion,
                UpdatedAtUtc = DateTime.UtcNow,
                ProjectRoot = _projectRoot ?? string.Empty,
            };
            File.WriteAllText(RegistrationPath, System.Text.Json.JsonSerializer.Serialize(registration));
        }
        catch (Exception ex) { Log(ex, "Save render worker registration"); }
    }

    private void SaveRegistration(string kind, CliRenderProcessOptions options)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RegistrationPath)!);
            var registration = new WorkerRegistration
            {
                Kind = kind,
                PipeName = _pipeName,
                Token = _token,
                ProcessId = _process?.Id ?? 0,
                ProtocolVersion = RenderProtocol.PipeProtocolVersion,
                UpdatedAtUtc = DateTime.UtcNow,
                JobId = options.JobId,
                ProjectRoot = Path.GetFullPath(options.ProjectRoot),
                OutputPath = options.OutputPath,
                PreviewPath = options.PreviewPath ?? string.Empty,
            };
            File.WriteAllText(RegistrationPath, System.Text.Json.JsonSerializer.Serialize(registration));
        }
        catch (Exception ex) { Log(ex, "Save CLI render worker registration"); }
    }

    private static bool SamePath(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            comparison);
    }

    private static void WaitForServer(IRenderClient client, Process? serverProcess)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 20);
        Exception? lastError = null;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (serverProcess?.HasExited == true)
                throw new InvalidOperationException($"The projectFrameCut Render RPC server exited before connecting (exit code {serverProcess.ExitCode}). See log.", lastError);

            try
            {
                _ = client.GetCapabilitiesAsync().AsTask().GetAwaiter().GetResult();
                return;
            }
            catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or OperationCanceledException)
            {
                lastError = ex;
                Thread.Sleep(50);
            }
        }

        throw new TimeoutException("Timed out waiting for the projectFrameCut Render RPC server.", lastError);
    }

    public async ValueTask DisposeAsync()
    {
        if (_independentWorker && _jobId is Guid jobId && _process is not null)
        {
            try { await CancelCliRenderAsync(jobId).ConfigureAwait(false); } catch { }
        }

        if (_client is not null)
        {
            try { await _client.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _client = null;
        _transport = null;

        if (_directHost is not null)
        {
            await _directHost.DisposeAsync().ConfigureAwait(false);
            _directHost = null;
        }

#if ANDROID
        if (_androidWorker is not null)
        {
            await _androidWorker.DisposeAsync().ConfigureAwait(false);
            _androidWorker = null;
        }
#endif

        if (_process is not null)
        {
            if (!_independentWorker)
            {
                try
                {
                    if (!_process.HasExited && !_process.WaitForExit(2000))
                        _process.Kill(entireProcessTree: true);
                }
                catch { }
            }
            _process.Dispose();
            _process = null;
        }
        _projectRoot = null;
        _independentWorker = false;
        _jobId = null;
    }

    private static async Task WaitForWorkerExitOrKillAsync(Process process, TimeSpan timeout)
    {
        try
        {
            if (process.HasExited) return;
            await process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
        }
        catch { }
    }

    private sealed class WorkerRegistration
    {
        public string Kind { get; set; } = string.Empty;
        public string PipeName { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public int ProcessId { get; set; }
        public int ProtocolVersion { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public Guid JobId { get; set; }
        public string ProjectRoot { get; set; } = string.Empty;
        public string OutputPath { get; set; } = string.Empty;
        public string PreviewPath { get; set; } = string.Empty;
    }

    private static TimeSpan RemainingShutdownTime(DateTime deadline, TimeSpan cap)
    {
        var remaining = deadline - DateTime.UtcNow;
        var bounded = cap == TimeSpan.Zero ? remaining : (remaining < cap ? remaining : cap);
        return bounded > TimeSpan.Zero ? bounded : TimeSpan.FromMilliseconds(1);
    }
}

internal sealed record CliRenderProcessOptions
{
    public Guid JobId { get; init; } = Guid.NewGuid();
    public required string ProjectRoot { get; init; }
    public required string ProjectName { get; init; }
    public required string OutputPath { get; init; }
    public required string AssetDatabasePath { get; init; }
    public required string FFmpegLibraryPath { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int FrameRate { get; init; }
    public required string PixelFormat { get; init; }
    public required string Encoder { get; init; }
    public int MaxParallelThreads { get; init; } = Environment.ProcessorCount;
    public bool OneByOneRender { get; init; }
    public int GcOption { get; init; }
    public bool EnableThreadAffinity { get; init; } = true;
    public bool PrepareInWorker { get; init; } = true;
    public bool RenderByLayer { get; init; } = true;
    public bool Background { get; init; }
    public required string TempPath { get; init; }
    public string? PreviewPath { get; init; }
    public bool UseHwAccelDecoder { get; init; } = true;
    public bool UseHwAccelEncoder { get; init; } = true;
    public bool WriteToVoid { get; init; }
}
