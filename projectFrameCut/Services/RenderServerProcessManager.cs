using System.Diagnostics;
using System.Reflection;
using FFmpeg.AutoGen;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RPCProtocol;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Shared;

namespace projectFrameCut.Services;

internal sealed class RenderServerProcessManager : IAsyncDisposable
{
    private const int DefaultHttpRpcPort = 39485;

    private readonly string _clientId;
    private readonly string _pipeName = $"projectFrameCut-render-{Guid.NewGuid():N}";
    private readonly string _token = Convert.ToHexString(Guid.NewGuid().ToByteArray());
    private string? _projectRoot;
    private Process? _process;
    private IRenderTransport? _transport;
    private IRenderClient? _client;
    private RenderServiceHost? _directHost;

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

    /// <param name="projectRoot">
    /// Project directory to hand to the server process. The optional HTTP RPC
    /// server preloads it so clients do not hit a "server has no project" error.
    /// </param>
    public void Start(string? projectRoot = null)
    {
        if (_client is not null) return;
        _projectRoot = string.IsNullOrWhiteSpace(projectRoot) ? null : Path.GetFullPath(projectRoot);

        if (!OperatingSystem.IsWindows() || SettingsManager.IsBoolSettingTrue("render_ForceDirectRenderTransport"))
        {
            _directHost = new RenderServiceHost(_clientId);
            _client = _directHost.Client;
            return;
        }
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
            startInfo.ArgumentList.Add($"--parentPid={Environment.ProcessId}");
            startInfo.ArgumentList.Add($"--dataRoot={GlobalPluginHelper.PluginsDataRootPath}");
            if (_projectRoot is not null)
            {
                startInfo.ArgumentList.Add($"--projectRoot={_projectRoot}");
            }
            startInfo.ArgumentList.Add($"--ffmpegRoot={ffmpeg.RootPath}");
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
    }

    private static void WaitForServer(IRenderClient client, Process serverProcess)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 20);
        Exception? lastError = null;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (serverProcess.HasExited)
                throw new InvalidOperationException($"The projectFrameCut Render RPC server exited before connecting (exit code {serverProcess.ExitCode}). See log.", lastError);

            try
            {
                _ = client.GetCapabilitiesAsync().AsTask().GetAwaiter().GetResult();
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
            {
                lastError = ex;
                Thread.Sleep(50);
            }
        }

        throw new TimeoutException("Timed out waiting for the projectFrameCut Render RPC server.", lastError);
    }

    public async ValueTask DisposeAsync()
    {
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

        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true);
                }
            }
            catch { }
            _process.Dispose();
            _process = null;
        }
        _projectRoot = null;
    }
}
