using projectFrameCut.Shared;
using System.Diagnostics;
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
    private uint _requestId = 0;

    public async Task StartAsync(string pipeName, CancellationToken ct = default)
    {
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await _pipe.ConnectAsync(cts.Token);

        }
        catch(OperationCanceledException)
        {
            Debug.WriteLine("连接 RPC 后端超时。");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("连接 RPC 后端失败。", ex);
        }
        // 连接命名管道


        _reader = new StreamReader(_pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192, leaveOpen: true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), bufferSize: 8192, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
    }

    public async Task<JsonElement?> SendAsync(string type, JsonElement msg, CancellationToken ct = default)
    {
        var req = new RpcProtocol.RpcMessage
        {
            Type = type,
            Payload = msg,
            RequestId = (ulong)Math.Pow(2, Random.Shared.Next(4,63)),
        };

        var resp = await SendReceiveAsync(req, ct);
        if (resp?.RequestId == req.RequestId && resp.Payload is { } p)
        {
            return p;
            //return p.Deserialize<RpcProtocol.RpcMessage>(RpcProtocol.JsonOptions);
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
        if (_writer is null || _reader is null) throw new InvalidOperationException("RPC 未连接。");

        await _ioLock.WaitAsync(ct);
        try
        {
            var json = JsonSerializer.Serialize(req, RpcProtocol.JsonOptions);
            await _writer.WriteLineAsync(json);
            var line = await _reader.ReadLineAsync();
            if (line is null) return null;
            return JsonSerializer.Deserialize<RpcProtocol.RpcMessage>(line, RpcProtocol.JsonOptions);
        }
        finally
        {
            _ioLock.Release();
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
}