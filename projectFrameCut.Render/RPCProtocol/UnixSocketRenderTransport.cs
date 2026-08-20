using System.Collections.Concurrent;
using System.Net.Sockets;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.Render.RPCProtocol;

/// <summary>
/// Render RPC transport over a filesystem-backed Unix domain socket. The wire
/// format intentionally matches the named-pipe transport so Android workers do
/// not need a second RPC protocol.
/// </summary>
public sealed class UnixSocketRenderClientTransport : IRenderTransport
{
    private readonly string _socketPath;
    private readonly string _token;
    private readonly string _clientId;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<RenderResponseEnvelope>> _pending = new();
    private Socket? _socket;
    private NetworkStream? _stream;
    private Task? _readerTask;
    private Exception? _connectionError;
    private int _disposed;

    public UnixSocketRenderClientTransport(string socketPath, string token, string clientId)
    {
        if (string.IsNullOrWhiteSpace(socketPath)) throw new ArgumentException("Socket path is required.", nameof(socketPath));
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Socket token is required.", nameof(token));
        if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("Client ID is required.", nameof(clientId));
        _socketPath = Path.GetFullPath(socketPath);
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
                var stream = _stream ?? throw new RenderPipeException("Render worker socket is not connected.");
                await RenderPipeFrame.WriteAsync(stream, RenderRpcSerializer.Serialize(request), cancellationToken).ConfigureAwait(false);
            }
            finally { _writeGate.Release(); }

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
        if (_socket?.Connected == true && _stream is not null) return;
        if (_connectionError is not null) throw new RenderPipeException("Render worker connection failed.", _connectionError);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_socket?.Connected == true && _stream is not null) return;
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), connectTimeout.Token).ConfigureAwait(false);
                var stream = new NetworkStream(socket, ownsSocket: true);
                var handshake = new RenderPipeHandshake { ClientId = _clientId, Token = _token };
                await RenderPipeFrame.WriteAsync(stream, RenderRpcSerializer.Serialize(handshake), cancellationToken).ConfigureAwait(false);
                var responseBytes = await RenderPipeFrame.ReadAsync(stream, cancellationToken).ConfigureAwait(false)
                    ?? throw new RenderPipeException("Render worker closed the socket during handshake.");
                var response = RenderRpcSerializer.Deserialize<RenderPipeHandshake>(responseBytes);
                if (!response.Accepted)
                    throw new RenderPipeException(string.IsNullOrWhiteSpace(response.Error) ? "Render worker rejected the handshake." : response.Error);
                if (response.ProtocolVersion != RenderProtocol.PipeProtocolVersion)
                    throw new RenderPipeException($"Render socket protocol mismatch: server={response.ProtocolVersion}, client={RenderProtocol.PipeProtocolVersion}.");

                _socket = socket;
                _stream = stream;
                _readerTask = Task.Run(() => ReadResponsesAsync(stream));
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        finally { _connectGate.Release(); }
    }

    private async Task ReadResponsesAsync(NetworkStream stream)
    {
        Exception? failure = null;
        try
        {
            while (_socket?.Connected == true)
            {
                var bytes = await RenderPipeFrame.ReadAsync(stream, CancellationToken.None).ConfigureAwait(false);
                if (bytes is null) break;
                var response = RenderRpcSerializer.Deserialize<RenderResponseEnvelope>(bytes);
                if (_pending.TryRemove(response.RequestId, out var completion)) completion.TrySetResult(response);
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            _connectionError = failure ?? new EndOfStreamException("Render worker closed the socket.");
            foreach (var pending in _pending.Values)
                pending.TrySetException(new RenderPipeException("Render worker disconnected.", _connectionError));
            _pending.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _stream?.Dispose(); } catch { }
        try { _socket?.Dispose(); } catch { }
        if (_readerTask is not null)
        {
            try { await _readerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        }
        _connectGate.Dispose();
        _writeGate.Dispose();
    }
}

public sealed class UnixSocketRenderServer(IRenderService service)
{
    private readonly IRenderService _service = service ?? throw new ArgumentNullException(nameof(service));

    public async Task RunAsync(string socketPath, string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(socketPath)) throw new ArgumentException("Socket path is required.", nameof(socketPath));
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Socket token is required.", nameof(token));
        socketPath = Path.GetFullPath(socketPath);
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
        TryDeleteSocket(socketPath);

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(4);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var socket = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                await using var stream = new NetworkStream(socket, ownsSocket: true);
                try { await RunConnectionAsync(stream, token, cancellationToken).ConfigureAwait(false); }
                catch (EndOfStreamException) { }
                catch (IOException) { }
                catch (SocketException) when (!cancellationToken.IsCancellationRequested) { }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            }
        }
        finally { TryDeleteSocket(socketPath); }
    }

    private async Task RunConnectionAsync(Stream stream, string token, CancellationToken cancellationToken)
    {
        var handshakeBytes = await RenderPipeFrame.ReadAsync(stream, cancellationToken).ConfigureAwait(false)
            ?? throw new RenderPipeException("Render client closed the socket during handshake.");
        var handshake = RenderRpcSerializer.Deserialize<RenderPipeHandshake>(handshakeBytes);
        var accepted = handshake.ProtocolVersion == RenderProtocol.PipeProtocolVersion
            && string.Equals(handshake.Token, token, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(handshake.ClientId);
        var response = new RenderPipeHandshake
        {
            ProtocolVersion = RenderProtocol.PipeProtocolVersion,
            ClientId = handshake.ClientId,
            Accepted = accepted,
            Error = accepted ? string.Empty : "Render socket handshake was rejected.",
            Capabilities = accepted ? new RenderCapabilities
            {
                ProtocolVersion = RenderProtocol.CurrentVersion,
                MinimumProtocolVersion = RenderProtocol.MinimumSupportedVersion,
            } : null,
        };
        await RenderPipeFrame.WriteAsync(stream, RenderRpcSerializer.Serialize(response), cancellationToken).ConfigureAwait(false);
        if (!accepted) return;

        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var writeGate = new SemaphoreSlim(1, 1);
        var requests = new List<Task>();
        try
        {
            while (!connectionCancellation.IsCancellationRequested)
            {
                var bytes = await RenderPipeFrame.ReadAsync(stream, connectionCancellation.Token).ConfigureAwait(false);
                if (bytes is null) break;
                var request = RenderRpcSerializer.Deserialize<RenderRequestEnvelope>(bytes);
                requests.Add(DispatchAndWriteAsync(stream, writeGate, request, connectionCancellation.Token));
                requests.RemoveAll(static task => task.IsCompleted);
            }
        }
        finally
        {
            connectionCancellation.Cancel();
            try { await Task.WhenAll(requests).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private async Task DispatchAndWriteAsync(Stream stream, SemaphoreSlim writeGate, RenderRequestEnvelope request, CancellationToken cancellationToken)
    {
        RenderResponseEnvelope response;
        try { response = await _service.DispatchAsync(request, cancellationToken).ConfigureAwait(false); }
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
        try { await RenderPipeFrame.WriteAsync(stream, RenderRpcSerializer.Serialize(response), cancellationToken).ConfigureAwait(false); }
        finally { writeGate.Release(); }
    }

    private static void TryDeleteSocket(string socketPath)
    {
        try { if (File.Exists(socketPath)) File.Delete(socketPath); } catch { }
    }
}
