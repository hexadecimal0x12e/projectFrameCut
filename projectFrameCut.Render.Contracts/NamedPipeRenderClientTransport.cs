using System.Collections.Concurrent;
using System.IO.Pipes;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.Render.RPCProtocol;

public sealed class NamedPipeRenderClientTransport : IRenderDuplexTransport
{
    private readonly string _pipeName;
    private readonly string _token;
    private readonly string _clientId;
    private readonly IReadOnlyList<ExternalRpcClientAuthorization> _persistentAuthorizations;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<RenderResponseEnvelope>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private NamedPipeClientStream? _pipe;
    private Task? _readerTask;
    private Exception? _connectionError;
    private int _disposed;
    private readonly ConcurrentDictionary<Guid, Task> _callbacks = new();

    public IRenderService? CallbackService { get; set; }

    public NamedPipeRenderClientTransport(string pipeName, string token, string clientId, IReadOnlyList<ExternalRpcClientAuthorization>? persistentAuthorizations = null)
    {
        if (string.IsNullOrWhiteSpace(pipeName)) throw new ArgumentException("Pipe name is required.", nameof(pipeName));
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Pipe token is required.", nameof(token));
        if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("Client ID is required.", nameof(clientId));
        _pipeName = pipeName;
        _token = token;
        _clientId = clientId;
        _persistentAuthorizations = persistentAuthorizations ?? [];
    }

    public NamedPipeRenderClientTransport(string pipeName, string clientId)
        : this(pipeName, GetAdditionalPipeToken(pipeName), clientId) { }

    private static string GetAdditionalPipeToken(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName) ||
            !pipeName.StartsWith(RenderProtocol.AdditionalPipePrefix, StringComparison.Ordinal))
            throw new ArgumentException("An additional render pipe name is required.", nameof(pipeName));
        var token = pipeName[RenderProtocol.AdditionalPipePrefix.Length..];
        if (token.Length != 64 || !token.All(Uri.IsHexDigit))
            throw new ArgumentException("The additional render pipe name has an invalid identifier.", nameof(pipeName));
        return token;
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
            var payload = RenderRpcSerializer.Serialize(request);
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Cancellation is safe while waiting for the write gate, but not after a framed
                // message has started. Interrupting between the length prefix and payload leaves
                // the shared byte stream misaligned for every subsequent request.
                cancellationToken.ThrowIfCancellationRequested();
                var pipe = _pipe ?? throw new RenderPipeException("Render server pipe is not connected.");
                await RenderPipeFrame.WriteAsync(pipe, payload, CancellationToken.None).ConfigureAwait(false);
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
                var handshake = new RenderPipeHandshake
                {
                    ClientId = _clientId,
                    Token = _token,
                    PersistentAuthorizations = _persistentAuthorizations.ToList(),
                };
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
                if (response.CallbackRequest is not null)
                {
                    var request = response.CallbackRequest;
                    var task = HandleCallbackAsync(pipe, request);
                    _callbacks[request.RequestId] = task;
                    _ = task.ContinueWith(_ => _callbacks.TryRemove(request.RequestId, out var ignored), TaskScheduler.Default);
                    continue;
                }
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

    private async Task HandleCallbackAsync(NamedPipeClientStream pipe, RenderRequestEnvelope request)
    {
        RenderResponseEnvelope response;
        try
        {
            response = CallbackService is null
                ? new() { RequestId = request.RequestId, Error = new() { Code = RenderErrorCode.Unsupported, Message = "This client does not provide RPC video sources." } }
                : await CallbackService.DispatchAsync(request, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            response = new() { RequestId = request.RequestId, Error = new(ex) };
        }

        var completion = new RenderRequestEnvelope
        {
            RequestId = Guid.NewGuid(),
            ClientId = _clientId,
            Operation = RenderOperation.CompleteExternalVideoSourceCallback,
            Payload = RenderRpcSerializer.Serialize(response),
        };
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try { await RenderPipeFrame.WriteAsync(pipe, RenderRpcSerializer.Serialize(completion), CancellationToken.None).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        try { _pipe?.Dispose(); } catch { }
        if (_readerTask is not null)
        {
            try { await _readerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        }
        try { await Task.WhenAll(_callbacks.Values).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        _connectGate.Dispose();
        _writeGate.Dispose();
        _lifetime.Dispose();
    }
}
