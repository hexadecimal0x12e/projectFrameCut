using Microsoft.UI.Xaml.Media;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Shared;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.Platforms.Windows;

public sealed class RpcClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Process? _proc;
    private string _pipeName = "";

    public Action<JsonElement>? ErrorCallback = null;

    public static string BootRPCServer(out Process rpcProc, out string backendAccessToken, out int backendPort, string tmpDir = "", string options = "1280,720,42,AV_PIX_FMT_NONE,nope", bool VerboseBackendLog = false, Action<string>? stdoutCallback = null, Action<string>? stderrCallback = null)
    {
        var pipeId = "pjfc_rpc_" + Guid.NewGuid().ToString();
        var pluginPipeId = "pjfc_plugin_" + Guid.NewGuid().ToString("N");
        backendAccessToken = Guid.NewGuid().ToString().Replace("-", "");
        backendPort = GenerateBackendPort();
        Directory.CreateDirectory(tmpDir);
        rpcProc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(AppContext.BaseDirectory, "projectFrameCut.Render.WindowsRender.exe"),
                WorkingDirectory = Path.Combine(AppContext.BaseDirectory),
                Arguments =
                $"""
                 rpc_backend
                  "-pipe={pipeId}"
                                    "-pluginConnectionPipe={pluginPipeId}"
                  "-output_options={options}"
                  "-tempFolder={tmpDir}"
                  "-accessToken={backendAccessToken}"
                  "-port={backendPort}"
                  "-UseCheckerboardBackgroundForNoContent=true"
                  "-ParentPID={Process.GetCurrentProcess().Id}"
                """.Replace(Environment.NewLine, " "),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            }
        };
        if (VerboseBackendLog)
        {
            rpcProc.StartInfo.RedirectStandardError = false;
            rpcProc.StartInfo.RedirectStandardOutput = false;
            rpcProc.StartInfo.CreateNoWindow = false;
            rpcProc.StartInfo.UseShellExecute = true;
        }

        Log($"Starting RPC backend with pipe ID: {pipeId}, args:{rpcProc.StartInfo.Arguments}");

        rpcProc.OutputDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                Log(e.Data, "Backend_STDOUT");
                stdoutCallback?.Invoke(e.Data);
            }
        };

        rpcProc.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                Log(e.Data, "Backend_STDERR");
                stderrCallback?.Invoke(e.Data);
            }
        };

        _ = Task.Run(async () =>
        {
            try
            {
                await PluginPipeTransport.SendEnabledPluginsAsync(pluginPipeId);
            }
            catch (Exception ex)
            {
                Log(ex, "send plugins via pipe", nameof(RpcClient));
            }
        });

        rpcProc.EnableRaisingEvents = true;
        rpcProc.Start();
        if (!VerboseBackendLog)
        {
            rpcProc.BeginErrorReadLine();
            rpcProc.BeginOutputReadLine();
        }

        return pipeId;
    }

    private static int GenerateBackendPort()
    {
        bool IsPortAvailable(int port)
        {
            try
            {
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch
            {
                return false;
            }
        }
        var random = new Random();
        int minPort = 23457;
        int maxPort = 65000;
        int port;
        bool isAvailable = false;

        for (int i = 0; i < 100; i++)
        {
            port = random.Next(minPort, maxPort + 1);
            if (IsPortAvailable(port))
            {
                return port;
            }
        }
        return -1;
    }

    public static async Task<string> UpdateDraft(DraftStructureJSON draft, RpcClient rpcClient, CancellationToken ct = default)
    {
        var element = JsonSerializer.SerializeToElement(draft);
        var result = await rpcClient.SendAsync("UpdateClips", element, ct);
        if (result.HasValue && result.Value.TryGetProperty("status", out var status))
        {
            Log("Draft updated.");
            return status.GetString();
        }
        else
        {
            Log("Failed to update draft.");
            return "failed by unknown reason";
        }
    }

    public static async Task<string> RenderOneFrame(uint frameId, RpcClient rpcClient, CancellationToken ct = default)
    {
        if (rpcClient is null) return "";
        var result = await rpcClient.SendAsync("RenderOne", JsonSerializer.SerializeToElement(frameId), ct);
        if (result is null)
        {
            throw new InvalidOperationException("RPC doesn't return anything.");
        }
        if (result.HasValue && result.Value.TryGetProperty("status", out var status))
        {
            if (status.GetString() == "ok")
            {

                if (result.Value.TryGetProperty("path", out var path))
                {
                    return path.GetString() ?? "";

                }
            }
            else
            {
                throw new Exception(result.Value.GetProperty("error").GetString());
            }
        }
        else
        {
            Log("Failed to update draft.");

            throw new Exception(result.Value.GetProperty("error").GetString());
        }
        return "";
    }

    public async Task StartAsync(string pipeName, CancellationToken ct = default) => await StartAsync(new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous), pipeName, ct);

    public async Task StartAsync(NamedPipeClientStream client, string pipeName, CancellationToken ct = default)
    {
        _pipe = client;
        _pipeName = pipeName;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await _pipe.ConnectAsync(cts.Token);

        }
        catch (OperationCanceledException)
        {
            Log("Connect to RPC timeout.", "error");
        }
        catch (Exception ex)
        {
            Log(ex, "Connect to RPC", this);

        }

        try
        {
            _reader = new StreamReader(_pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false), bufferSize: 8192, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
        }
        catch (Exception ex)
        {
            Log(ex, "Connect to RPC", this);
        }


    }

    public async Task<JsonElement?> SendAsync(string type, JsonElement msg, CancellationToken ct = default)
    {
        var req = new RpcProtocol.RpcMessage
        {
            Type = type,
            Payload = msg,
            RequestId = (ulong)Math.Pow(2, Random.Shared.Next(4, 63)),
        };

        var resp = await SendReceiveAsync(req, ct);
        if (resp?.RequestId == req.RequestId && resp.Payload is { } p)
        {
            try
            {
                if (resp.Payload.Value.TryGetProperty("status", out var stat) && stat.GetString() is not null && !(stat.GetString() ?? "").Equals("ok", StringComparison.InvariantCultureIgnoreCase))
                {
                    ErrorCallback?.Invoke(p);
                }
            }
            catch
            {
                //ignore
            }
            return p;
        }
        return null;
    }

    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        var req = new RpcProtocol.RpcMessage { Type = "ShutDown", Payload = null };
        try
        {
            await SendAsync(req, ct);
        }
        catch { /* ignore */ }

        try
        {
            if (_proc is { HasExited: false })
            {
                // 给后端一点时间正常退出
                if (!await WaitForExitAsync(_proc, TimeSpan.FromSeconds(2)))
                    _proc.Kill(entireProcessTree: true);
            }
        }
        catch { /* ignore */ }
    }

    private async Task SendAsync(RpcProtocol.RpcMessage req, CancellationToken ct)
    {
        if (_writer is null) throw new InvalidOperationException("RPC 未连接。");
        var json = JsonSerializer.Serialize(req, RpcProtocol.JsonOptions);

        await _ioLock.WaitAsync(ct);
        try
        {
            await _writer.WriteLineAsync(json);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private async Task<RpcProtocol.RpcMessage?> SendReceiveAsync(RpcProtocol.RpcMessage req, CancellationToken ct)
    {
        try
        {
            if (_writer is null || _reader is null) throw new InvalidOperationException("RPC 未连接。");
            await _ioLock.WaitAsync(ct);
            var json = JsonSerializer.Serialize(req, RpcProtocol.JsonOptions);
            await _writer.WriteLineAsync(json);
            var line = await _reader.ReadLineAsync(ct);
            if (line is null) return null;
            return JsonSerializer.Deserialize<RpcProtocol.RpcMessage>(line, RpcProtocol.JsonOptions);
        }
        catch (Exception ex)
        {
            Log(ex, "SendReceive", this);
            return null;
        }
        finally
        {
            try
            {
                _ioLock.Release();
            }
            catch (Exception ex)
            {
                Log(ex, "SendReceive", this);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync();
        _writer?.Dispose();
        _reader?.Dispose();
        _pipe?.Dispose();
        _ioLock.Dispose();
        _proc?.Dispose();
    }

    private static Task<bool> WaitForExitAsync(Process p, TimeSpan timeout)
    {
        return Task.Run(() => p.WaitForExit((int)timeout.TotalMilliseconds));
    }

    public override string ToString() => $"RpcClient {_pipeName} (Connected: {_pipe?.IsConnected ?? false})";
}