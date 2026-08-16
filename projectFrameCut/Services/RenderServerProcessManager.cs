using System.Diagnostics;
using System.Reflection;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RPCProtocol;

namespace projectFrameCut.Services;

internal sealed class RenderServerProcessManager : IAsyncDisposable
{
    private readonly string _clientId;
    private readonly string _pipeName = $"projectFrameCut-render-{Guid.NewGuid():N}";
    private readonly string _token = Convert.ToHexString(Guid.NewGuid().ToByteArray());
    private Process? _process;
    private IRenderTransport? _transport;
    private IRenderClient? _client;
    private RenderServiceHost? _directHost;

    public RenderServerProcessManager(string clientId)
    {
        _clientId = string.IsNullOrWhiteSpace(clientId) ? $"client-{Guid.NewGuid():N}" : clientId;
    }

    public IRenderClient Client => _client ?? throw new InvalidOperationException("Render RPC server has not been started.");

    public void Start()
    {
        if (_client is not null) return;

        if (!OperatingSystem.IsWindows())
        {
            _directHost = new RenderServiceHost(_clientId);
            _client = _directHost.Client;
            return;
        }
#if WINDOWS
        Exception? ex1 = null, ex2 = null;
        try
        {
            StartProcess(Path.Combine(AppContext.BaseDirectory, $"pjfc-cli.exe"));
            return;
        }
        catch (Exception ex)
        {
            ex1 = ex;
        }
        try
        {
            StartProcess($"projectFrameCutCLI_{AppInfo.PackageName}_{Assembly.GetExecutingAssembly().GetName().Version}.exe");
            return;
        }
        catch (Exception ex)
        {
            ex2 = ex;
        }

        throw new InvalidOperationException("Unable to start RPC server. Please ensure that the longer App alias is enabled in the Settings->Apps->Advanced->App execution alias.", new AggregateException(ex1, ex2));
#elif iDevices
        StartProcess(Path.Combine(Foundation.NSBundle.MainBundle.BundlePath, "Contents", "MacOS", "projectFrameCut_cli"));
#endif

        void StartProcess(string execPath)
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
            startInfo.ArgumentList.Add("--quiet");

            try
            {
                _process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Unable to start pjfc-cli.exe RPC server.");
                _process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        Debug.WriteLine($"[RenderRPC] {e.Data}");
                };
                _process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        Debug.WriteLine($"[RenderRPC:stdout] {e.Data}");
                };
                _process.BeginErrorReadLine();
                _process.BeginOutputReadLine();

                var transport = new NamedPipeRenderClientTransport(_pipeName, _token, _clientId);
                var client = new RenderClient(transport, _clientId);
                _transport = transport;
                _client = client;
                WaitForServer(client, _process);
            }
            catch
            {
                try { _client?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
                try { _process?.Kill(entireProcessTree: true); } catch { }
                _process?.Dispose();
                _process = null;
                _client = null;
                _transport = null;
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
    }
}
