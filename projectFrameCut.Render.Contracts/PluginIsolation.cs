using ProtoBuf;
using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace projectFrameCut.Render.Contracts;

public static class PluginIsolationProtocol
{
    public const int CurrentVersion = 1;
    public const long DefaultMaximumPayloadBytes = 2L * 1024 * 1024 * 1024;
}

[ProtoContract]
public enum IsolationControlMode
{
    [ProtoEnum] Auto = 0,
    [ProtoEnum] Direct = 1,
    [ProtoEnum] NamedPipe = 2,
    [ProtoEnum] UnixSocket = 3,
    [ProtoEnum] Http = 4,
}

[ProtoContract]
public enum IsolationPayloadKind
{
    [ProtoEnum] Inline = 0,
    [ProtoEnum] SharedMemory = 1,
    [ProtoEnum] LocalFile = 2,
    [ProtoEnum] TransferredResource = 3,
}

[ProtoContract]
public enum IsolationPayloadAccess
{
    [ProtoEnum] ReadOnly = 0,
    [ProtoEnum] WriteOnly = 1,
    [ProtoEnum] ReadWrite = 2,
}

[ProtoContract]
public sealed class IsolationMediaDescriptor
{
    [ProtoMember(1)] public int LayoutVersion { get; set; } = 1;
    [ProtoMember(2)] public int Width { get; set; }
    [ProtoMember(3)] public int Height { get; set; }
    [ProtoMember(4)] public int BitsPerChannel { get; set; }
    [ProtoMember(5)] public bool HasAlpha { get; set; }
    [ProtoMember(6)] public bool HasHdrBrightness { get; set; }
    [ProtoMember(7)] public float MaximumBrightness { get; set; }
    [ProtoMember(8)] public long RedLength { get; set; }
    [ProtoMember(9)] public long GreenLength { get; set; }
    [ProtoMember(10)] public long BlueLength { get; set; }
    [ProtoMember(11)] public long AlphaLength { get; set; }
    [ProtoMember(12)] public long BrightnessLength { get; set; }
}

[ProtoContract]
public sealed class IsolationPayloadReference
{
    [ProtoMember(1)] public IsolationPayloadKind Kind { get; set; }
    [ProtoMember(2)] public string Locator { get; set; } = string.Empty;
    [ProtoMember(3)] public long Offset { get; set; }
    [ProtoMember(4)] public long Length { get; set; }
    [ProtoMember(5)] public IsolationPayloadAccess Access { get; set; }
    [ProtoMember(6)] public string ContentHash { get; set; } = string.Empty;
    [ProtoMember(7)] public IsolationMediaDescriptor? Media { get; set; }
    [ProtoMember(8)] public byte[] InlineData { get; set; } = [];
}

[ProtoContract]
public sealed class IsolationChannelCapabilities
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; } = PluginIsolationProtocol.CurrentVersion;
    [ProtoMember(2)] public List<IsolationPayloadKind> PayloadKinds { get; set; } = [];
    [ProtoMember(3)] public long MaximumInlineBytes { get; set; } = 64 * 1024;
    [ProtoMember(4)] public long MaximumSharedMemoryBytes { get; set; } = PluginIsolationProtocol.DefaultMaximumPayloadBytes;
    [ProtoMember(5)] public bool SupportsResourceTransfer { get; set; }
    [ProtoMember(6)] public string Platform { get; set; } = string.Empty;
}

public sealed class PluginIsolationTransportOptions
{
    public IsolationControlMode ControlMode { get; init; } = IsolationControlMode.Auto;
    public IsolationPayloadKind? PayloadMode { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaximumInlineBytes { get; init; } = 64 * 1024;
    public long MaximumPayloadBytes { get; init; } = PluginIsolationProtocol.DefaultMaximumPayloadBytes;
    public bool TerminateOnRemoteError { get; init; }
}

public sealed class PluginIsolationLaunchContext
{
    public required string PluginId { get; init; }
    public required string PluginRoot { get; init; }
    public required string SessionRoot { get; init; }
    public required string AuthenticationToken { get; init; }
    public required string PluginEncryptionKey { get; init; }
    public required string InstancePackageName { get; init; }
    public PluginIsolationTransportOptions Transport { get; init; } = new();
}

public interface IIsolationControlChannel : IRenderTransport
{
    IsolationControlMode Mode { get; }
    Task Completion { get; }
}

public interface IIsolationPayloadResolver
{
    ValueTask<Stream> OpenReadAsync(IsolationPayloadReference reference, CancellationToken cancellationToken = default);
    ValueTask<Stream> OpenWriteAsync(IsolationPayloadReference reference, CancellationToken cancellationToken = default);
}

public interface IIsolationPayloadExchange : IIsolationPayloadResolver, IAsyncDisposable
{
    ValueTask<IsolationPayloadLease> PublishAsync(ReadOnlyMemory<byte> data, IsolationPayloadKind preferredKind, CancellationToken cancellationToken = default);
    ValueTask<IsolationPayloadLease> AllocateAsync(long length, IsolationPayloadKind preferredKind, CancellationToken cancellationToken = default);
    ValueTask ReleaseAsync(IsolationPayloadReference reference);
}

public interface IIsolationResourceResolver
{
    string ResolveReadOnlyFile(IsolationPayloadReference reference);
}

public interface IIsolationResourceBroker : IIsolationResourceResolver, IAsyncDisposable
{
    ValueTask<IsolationPayloadLease> BrokerReadOnlyFileAsync(string path, CancellationToken cancellationToken = default);
    ValueTask<IsolationPayloadLease> BrokerReadOnlyStreamAsync(Stream source, long length, CancellationToken cancellationToken = default);
}

public interface IPluginIsolationSession : IAsyncDisposable
{
    string PluginId { get; }
    IIsolationControlChannel Control { get; }
    IIsolationPayloadExchange Payloads { get; }
    IIsolationResourceBroker Resources { get; }
    IsolationChannelCapabilities Capabilities { get; }
    IsolationPayloadKind PreferredPayloadKind { get; }
    ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(RenderOperation operation, TRequest request, CancellationToken cancellationToken = default);
    ValueTask TerminateAsync(string reason);
}

public sealed class DirectIsolationControlChannel(IRenderService service) : IIsolationControlChannel
{
    private readonly DirectRenderTransport _transport = new(service);
    public IsolationControlMode Mode => IsolationControlMode.Direct;
    public Task Completion => Task.CompletedTask;
    public ValueTask<RenderResponseEnvelope> SendAsync(RenderRequestEnvelope request, CancellationToken cancellationToken = default)
        => _transport.SendAsync(request, cancellationToken);
    public ValueTask DisposeAsync() => _transport.DisposeAsync();
}

public interface IPluginIsolationPlatform
{
    ValueTask<IPluginIsolationSession> StartAsync(PluginIsolationLaunchContext context, CancellationToken cancellationToken = default);
}

public sealed class IsolationPayloadLease : IAsyncDisposable
{
    private readonly Func<IsolationPayloadReference, ValueTask>? _release;
    private int _disposed;

    public IsolationPayloadLease(IsolationPayloadReference reference, Func<IsolationPayloadReference, ValueTask>? release = null)
    {
        Reference = reference ?? throw new ArgumentNullException(nameof(reference));
        _release = release;
    }

    public IsolationPayloadReference Reference { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _release is not null)
            await _release(Reference).ConfigureAwait(false);
    }
}

public sealed class StreamIsolationControlChannel : IIsolationControlChannel
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<RenderResponseEnvelope>> _pending = new();
    private readonly Task _reader;
    private int _disposed;

    public StreamIsolationControlChannel(Stream stream, IsolationControlMode mode)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        Mode = mode;
        _reader = Task.Run(ReadResponsesAsync);
    }

    public IsolationControlMode Mode { get; }
    public Task Completion => _reader;

    public async ValueTask<RenderResponseEnvelope> SendAsync(RenderRequestEnvelope request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var completion = new TaskCompletionSource<RenderResponseEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(request.RequestId, completion))
            throw new RenderPipeException($"Duplicate isolation request ID '{request.RequestId}'.");

        try
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await IsolationFrame.WriteAsync(_stream, RenderRpcSerializer.Serialize(request), CancellationToken.None).ConfigureAwait(false);
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

    private async Task ReadResponsesAsync()
    {
        Exception failure;
        try
        {
            while (true)
            {
                var data = await IsolationFrame.ReadAsync(_stream, CancellationToken.None).ConfigureAwait(false);
                if (data is null) break;
                var response = RenderRpcSerializer.Deserialize<RenderResponseEnvelope>(data);
                if (!_pending.TryRemove(response.RequestId, out var completion))
                    throw new InvalidDataException($"The isolation runtime returned an unknown request ID '{response.RequestId}'.");
                completion.TrySetResult(response);
            }
            failure = new EndOfStreamException("The isolation control channel was closed.");
        }
        catch (Exception ex) { failure = ex; }

        foreach (var completion in _pending.Values)
            completion.TrySetException(new RenderPipeException("The isolation runtime disconnected.", failure));
        _pending.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await _stream.DisposeAsync().ConfigureAwait(false); } catch { }
        try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        _writeGate.Dispose();
    }
}

public static class StreamIsolationRequestDispatcher
{
    public static async Task RunAsync(Stream stream, IRenderService service, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(service);
        using var writeGate = new SemaphoreSlim(1, 1);
        var requests = new List<Task>();
        while (!cancellationToken.IsCancellationRequested)
        {
            var data = await IsolationFrame.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            if (data is null) break;
            var request = RenderRpcSerializer.Deserialize<RenderRequestEnvelope>(data);
            requests.Add(DispatchAsync(stream, writeGate, service, request, cancellationToken));
            requests.RemoveAll(static x => x.IsCompleted);
        }
        try { await Task.WhenAll(requests).ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    private static async Task DispatchAsync(Stream stream, SemaphoreSlim writeGate, IRenderService service, RenderRequestEnvelope request, CancellationToken cancellationToken)
    {
        RenderResponseEnvelope response;
        try { response = await service.DispatchAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException ex)
        {
            response = new() { RequestId = request.RequestId, Error = new(ex, RenderErrorCode.Canceled) };
        }
        catch (Exception ex)
        {
            response = new() { RequestId = request.RequestId, Error = new(ex) };
        }

        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await IsolationFrame.WriteAsync(stream, RenderRpcSerializer.Serialize(response), CancellationToken.None).ConfigureAwait(false); }
        finally { writeGate.Release(); }
    }
}

public static class IsolationFrame
{
    public static async Task WriteAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length <= 0 || payload.Length > RenderProtocol.MaxPipeFrameBytes)
            throw new InvalidDataException($"Invalid isolation frame length: {payload.Length}.");
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
        if (length <= 0 || length > RenderProtocol.MaxPipeFrameBytes)
            throw new InvalidDataException($"Invalid isolation frame length: {length}.");
        var payload = new byte[length];
        if (!await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false))
            throw new EndOfStreamException("The isolation frame was truncated.");
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
