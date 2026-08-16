using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.Render.RPCProtocol;

public sealed class NamedPipeRenderClientTransport : IRenderTransport
{
    private readonly string _pipeName;
    private readonly string _token;
    private readonly string _clientId;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<RenderResponseEnvelope>> _pending = new();
    private NamedPipeClientStream? _pipe;
    private Task? _readerTask;
    private Exception? _connectionError;
    private int _disposed;

    public NamedPipeRenderClientTransport(string pipeName, string token, string clientId)
    {
        if (string.IsNullOrWhiteSpace(pipeName)) throw new ArgumentException("Pipe name is required.", nameof(pipeName));
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Pipe token is required.", nameof(token));
        if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("Client ID is required.", nameof(clientId));
        _pipeName = pipeName;
        _token = token;
        _clientId = clientId;
    }

    public async ValueTask<RenderResponseEnvelope> SendAsync(RenderRequestEnvelope request, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource<RenderResponseEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(request.RequestId, completion))
            throw new RenderPipeException($"Duplicate render request ID '{request.RequestId}'.");

        try
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var pipe = _pipe ?? throw new RenderPipeException("Render server pipe is not connected.");
                await RenderPipeFrame.WriteAsync(pipe, RenderRpcSerializer.Serialize(request), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }

            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(request.RequestId, out _);
            throw;
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_pipe?.IsConnected == true) return;
        if (_connectionError is not null) throw new RenderPipeException("Render server connection failed.", _connectionError);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pipe?.IsConnected == true) return;
            var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                await pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
                var handshake = new RenderPipeHandshake { ClientId = _clientId, Token = _token };
                await RenderPipeFrame.WriteAsync(pipe, RenderRpcSerializer.Serialize(handshake), cancellationToken).ConfigureAwait(false);
                var responseBytes = await RenderPipeFrame.ReadAsync(pipe, cancellationToken).ConfigureAwait(false)
                    ?? throw new RenderPipeException("Render server closed the pipe during handshake.");
                var response = RenderRpcSerializer.Deserialize<RenderPipeHandshake>(responseBytes);
                if (!response.Accepted)
                    throw new RenderPipeException(string.IsNullOrWhiteSpace(response.Error) ? "Render server rejected the handshake." : response.Error);
                if (response.ProtocolVersion != RenderProtocol.PipeProtocolVersion)
                    throw new RenderPipeException($"Render pipe protocol mismatch: server={response.ProtocolVersion}, client={RenderProtocol.PipeProtocolVersion}.");

                _pipe = pipe;
                _readerTask = Task.Run(() => ReadResponsesAsync(pipe));
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task ReadResponsesAsync(NamedPipeClientStream pipe)
    {
        Exception? failure = null;
        try
        {
            while (pipe.IsConnected)
            {
                var bytes = await RenderPipeFrame.ReadAsync(pipe, CancellationToken.None).ConfigureAwait(false);
                if (bytes is null) break;
                var response = RenderRpcSerializer.Deserialize<RenderResponseEnvelope>(bytes);
                if (_pending.TryRemove(response.RequestId, out var completion)) completion.TrySetResult(response);
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            _connectionError = failure ?? new EndOfStreamException("Render server closed the pipe.");
            foreach (var pending in _pending.Values)
                pending.TrySetException(new RenderPipeException("Render server disconnected.", _connectionError));
            _pending.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _pipe?.Dispose(); } catch { }
        if (_readerTask is not null)
        {
            try { await _readerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        }
        _connectGate.Dispose();
        _writeGate.Dispose();
    }
}

public sealed class NamedPipeRenderServer(IRenderService service)
{
    private readonly IRenderService _service = service ?? throw new ArgumentNullException(nameof(service));

    public async Task RunAsync(string pipeName, string token, string? expectedParentPid = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pipeName)) throw new ArgumentException("Pipe name is required.", nameof(pipeName));
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Pipe token is required.", nameof(token));

        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

        var handshakeBytes = await RenderPipeFrame.ReadAsync(pipe, cancellationToken).ConfigureAwait(false)
            ?? throw new RenderPipeException("Render client closed the pipe during handshake.");
        var handshake = RenderRpcSerializer.Deserialize<RenderPipeHandshake>(handshakeBytes);
        var accepted = handshake.ProtocolVersion == RenderProtocol.PipeProtocolVersion
            && string.Equals(handshake.Token, token, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(handshake.ClientId);
        var handshakeResponse = new RenderPipeHandshake
        {
            ProtocolVersion = RenderProtocol.PipeProtocolVersion,
            ClientId = handshake.ClientId,
            Accepted = accepted,
            Error = accepted ? string.Empty : "Render pipe handshake was rejected.",
            Capabilities = accepted ? new RenderCapabilities
            {
                ProtocolVersion = RenderProtocol.CurrentVersion,
                MinimumProtocolVersion = RenderProtocol.MinimumSupportedVersion,
            } : null,
        };
        await RenderPipeFrame.WriteAsync(pipe, RenderRpcSerializer.Serialize(handshakeResponse), cancellationToken).ConfigureAwait(false);
        if (!accepted) return;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var parentMonitor = StartParentMonitor(expectedParentPid, linked);
        var writeGate = new SemaphoreSlim(1, 1);
        try
        {
            var requests = new List<Task>();
            while (pipe.IsConnected && !linked.IsCancellationRequested)
            {
                var bytes = await RenderPipeFrame.ReadAsync(pipe, linked.Token).ConfigureAwait(false);
                if (bytes is null) break;
                var request = RenderRpcSerializer.Deserialize<RenderRequestEnvelope>(bytes);
                requests.Add(DispatchAndWriteAsync(pipe, writeGate, request, linked.Token));
                requests.RemoveAll(static task => task.IsCompleted);
            }
            linked.Cancel();
            try { await Task.WhenAll(requests).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        finally
        {
            linked.Cancel();
            try { await parentMonitor.ConfigureAwait(false); } catch { }
            writeGate.Dispose();
        }
    }

    private async Task DispatchAndWriteAsync(NamedPipeServerStream pipe, SemaphoreSlim writeGate, RenderRequestEnvelope request, CancellationToken cancellationToken)
    {
        RenderResponseEnvelope response;
        try
        {
            response = await _service.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            response = new RenderResponseEnvelope
            {
                RequestId = request.RequestId,
                Error = new RenderError { Code = RenderErrorCode.Canceled, Message = "Render request was canceled." },
            };
        }
        catch (Exception ex)
        {
            response = new RenderResponseEnvelope
            {
                RequestId = request.RequestId,
                Error = new RenderError { Code = RenderErrorCode.BackendFailure, Message = ex.Message, Details = ex.ToString() },
            };
        }

        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await RenderPipeFrame.WriteAsync(pipe, RenderRpcSerializer.Serialize(response), cancellationToken).ConfigureAwait(false); }
        finally { writeGate.Release(); }
    }

    private static Task StartParentMonitor(string? parentPid, CancellationTokenSource cancellation)
    {
        if (!int.TryParse(parentPid, out var pid) || pid <= 0) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            try
            {
                using var parent = System.Diagnostics.Process.GetProcessById(pid);
                await parent.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch { }
            if (!cancellation.IsCancellationRequested) cancellation.Cancel();
        });
    }
}

internal static class RenderPipeFrame
{
    public static async Task WriteAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length > RenderProtocol.MaxPipeFrameBytes) throw new InvalidDataException("Render pipe frame is too large.");
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        if (!await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false)) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > RenderProtocol.MaxPipeFrameBytes) throw new InvalidDataException($"Invalid render pipe frame length: {length}.");
        var payload = new byte[length];
        if (!await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false)) throw new EndOfStreamException("Render pipe frame was truncated.");
        return payload;
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }
}
