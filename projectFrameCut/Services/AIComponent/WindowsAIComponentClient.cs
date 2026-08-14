#if WINDOWS
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using projectFrameCut.AIComponentContracts;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Shared;
using IPicture = projectFrameCut.Drawing.Base.IPicture;

namespace projectFrameCut.Services.AIComponent;

public sealed class WindowsAIComponentClient : IAIComponentClient
{
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<AIReassembledMessage>> _pending = new();
    private readonly AIMessageWriter _writer = new();
    private AIMessageReader _reader = new();

    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private IReadOnlyList<AICapabilityDescriptor> _capabilities = Array.Empty<AICapabilityDescriptor>();

    public bool IsSupported => OperatingSystem.IsWindows();
    public bool IsConnected => _pipe?.IsConnected == true;
    public IReadOnlyList<AICapabilityDescriptor> Capabilities => _capabilities;

    public async Task<IReadOnlyList<AICapabilityDescriptor>> ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);

            string sessionId = Guid.NewGuid().ToString("N");
            string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            string pipeName = AIComponentProtocol.CreatePipeName(sessionId);
            var extensionUri = new Uri($"{AIComponentProtocol.ProtocolScheme}://connect/?session={sessionId}&nonce={nonce}&pipe={Uri.EscapeDataString(pipeName)}");

            Log($"Launching extension framework with link: {extensionUri}");

            bool launched = await Windows.System.Launcher.LaunchUriAsync(extensionUri);
            if (!launched)
                throw new AIComponentClientException("Windows could not activate the System AI extension.");

            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                pipe.Dispose();
                throw;
            }

            _pipe = pipe;
            _reader = new AIMessageReader();
            _receiveCts = new CancellationTokenSource();
            _receiveTask = ReceiveLoopAsync(_receiveCts.Token);

            var handshake = await RequestAsync(
                AIMessageKind.Hello,
                operation: "system.handshake",
                payloadKind: AIPayloadKind.None,
                metadata: new AIHandshakeMetadata
                {
                    SessionId = sessionId,
                    Nonce = nonce,
                    ClientName = "projectFrameCut",
                    ClientVersion = typeof(WindowsAIComponentClient).Assembly.GetName().Version?.ToString()
                },
                payload: ReadOnlyMemory<byte>.Empty,
                cancellationToken).ConfigureAwait(false);

            if (handshake.Envelope.Kind != AIMessageKind.Response)
                throw new AIComponentClientException("The System AI extension returned an invalid handshake response.");

            var response = DeserializeMetadata<AIHandshakeResponseMetadata>(handshake.Envelope.Metadata);
            if (response is null || response.SessionId != sessionId || !string.Equals(response.Nonce, nonce, StringComparison.Ordinal))
                throw new AIComponentClientException("The System AI extension handshake could not be authenticated.");

            _capabilities = response.Capabilities ?? Array.Empty<AICapabilityDescriptor>();
            return _capabilities;
        }
        catch
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async Task<string> ExecuteTextAsync(string operation, string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(text);

        var response = await RequestAsync(
            AIMessageKind.Request,
            operation,
            AIPayloadKind.TextUtf8,
            metadata: null,
            Encoding.UTF8.GetBytes(text),
            cancellationToken).ConfigureAwait(false);
        EnsurePayloadKind(response, AIPayloadKind.TextUtf8);
        return Encoding.UTF8.GetString(response.Payload);
    }

    public async Task<IPicture> ExecutePictureAsync(string operation, IPicture picture, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        byte[] payload = AIComponentPayloadCodec.EncodePicture(picture, out var descriptor);
        var response = await RequestAsync(
            AIMessageKind.Request,
            operation,
            AIPayloadKind.PicturePlanes,
            descriptor,
            payload,
            cancellationToken).ConfigureAwait(false);
        EnsurePayloadKind(response, AIPayloadKind.PicturePlanes);
        var responseDescriptor = DeserializeMetadata<AIPictureDescriptor>(response.Envelope.Metadata)
            ?? throw new AIComponentClientException("The extension returned no picture metadata.");
        return AIComponentPayloadCodec.DecodePicture(responseDescriptor, response.Payload);
    }

    public async Task<IPicture> ExecuteVideoSuperResolutionAsync(
        IPicture picture,
        int targetWidth,
        int targetHeight,
        int framesPerSecond,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(picture);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesPerSecond);

        byte[] payload = AIComponentPayloadCodec.EncodePicture(picture, out var descriptor);
        var requestDescriptor = new VideoSuperResolutionRequestDescriptor
        {
            Width = descriptor.Width,
            Height = descriptor.Height,
            BitDepth = descriptor.BitDepth,
            HasAlpha = descriptor.HasAlpha,
            TargetWidth = targetWidth,
            TargetHeight = targetHeight,
            FramesPerSecond = framesPerSecond
        };

        var response = await RequestAsync(
            AIMessageKind.Request,
            "video.super_resolution",
            AIPayloadKind.PicturePlanes,
            requestDescriptor,
            payload,
            cancellationToken).ConfigureAwait(false);
        EnsurePayloadKind(response, AIPayloadKind.PicturePlanes);
        var responseDescriptor = DeserializeMetadata<AIPictureDescriptor>(response.Envelope.Metadata)
            ?? throw new AIComponentClientException("The extension returned no picture metadata.");
        return AIComponentPayloadCodec.DecodePicture(responseDescriptor, response.Payload);
    }

    private sealed class VideoSuperResolutionRequestDescriptor
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public AIPictureBitDepth BitDepth { get; init; }
        public bool HasAlpha { get; init; }
        public int TargetWidth { get; init; }
        public int TargetHeight { get; init; }
        public int FramesPerSecond { get; init; }
    }

    public async Task<IAudioSamples<float>> ExecuteAudioAsync(string operation, IAudioSamples<float> audio, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        byte[] payload = AIComponentPayloadCodec.EncodeAudio(audio, out var descriptor);
        var response = await RequestAsync(
            AIMessageKind.Request,
            operation,
            AIPayloadKind.AudioPcm,
            descriptor,
            payload,
            cancellationToken).ConfigureAwait(false);
        EnsurePayloadKind(response, AIPayloadKind.AudioPcm);
        var responseDescriptor = DeserializeMetadata<AIAudioDescriptor>(response.Envelope.Metadata)
            ?? throw new AIComponentClientException("The extension returned no audio metadata.");
        return AIComponentPayloadCodec.DecodeAudio(responseDescriptor, response.Payload);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _writer.Dispose();
        _connectionLock.Dispose();
    }

    private async Task<AIReassembledMessage> RequestAsync(
        AIMessageKind messageKind,
        string operation,
        AIPayloadKind payloadKind,
        object? metadata,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var pipe = _pipe;
        if (pipe is null || !pipe.IsConnected)
            throw new AIComponentClientException("The System AI extension is not connected.");

        Guid requestId = Guid.NewGuid();
        var completion = new TaskCompletionSource<AIReassembledMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion))
            throw new AIComponentClientException("Could not allocate an AI component request id.");

        try
        {
            JsonElement metadataElement = metadata is null
                ? AIComponentProtocol.EmptyMetadata
                : JsonSerializer.SerializeToElement(metadata, AIComponentProtocol.JsonOptions);
            await _writer.WriteAsync(
                pipe,
                new AIMessageEnvelope
                {
                    Kind = messageKind,
                    RequestId = requestId,
                    Operation = operation,
                    PayloadKind = payloadKind,
                    Metadata = metadataElement
                },
                payload,
                cancellationToken).ConfigureAwait(false);

            using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(
                static state =>
                {
                    var tuple = ((WindowsAIComponentClient Client, Guid RequestId))state!;
                    _ = tuple.Client.SendCancelAsync(tuple.RequestId);
                },
                (this, requestId));
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AIProtocolException ex)
        {
            throw new AIComponentClientException("The System AI extension rejected the protocol message.", ex);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private async Task SendCancelAsync(Guid requestId)
    {
        try
        {
            var pipe = _pipe;
            if (pipe is null || !pipe.IsConnected)
                return;

            await _writer.WriteAsync(
                pipe,
                new AIMessageEnvelope
                {
                    Kind = AIMessageKind.Cancel,
                    RequestId = requestId,
                    Operation = "system.cancel",
                    PayloadKind = AIPayloadKind.None,
                    Metadata = AIComponentProtocol.EmptyMetadata
                },
                ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await _reader.ReadAsync(_pipe!, cancellationToken).ConfigureAwait(false);
                if (message is null)
                    break;

                if (_pending.TryGetValue(message.Envelope.RequestId, out var completion))
                    completion.TrySetResult(message);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            foreach (var completion in _pending.Values)
                completion.TrySetException(new AIComponentClientException("The System AI extension connection was lost.", ex));
        }
    }

    private async Task DisconnectCoreAsync()
    {
        _receiveCts?.Cancel();
        _pipe?.Dispose();

        if (_receiveTask is not null)
        {
            try { await _receiveTask.ConfigureAwait(false); }
            catch { }
        }

        foreach (var completion in _pending.Values)
            completion.TrySetException(new AIComponentClientException("The System AI extension connection was closed."));
        _pending.Clear();

        _receiveCts?.Dispose();
        _receiveCts = null;
        _receiveTask = null;
        _pipe = null;
        _capabilities = Array.Empty<AICapabilityDescriptor>();
    }

    private static void EnsurePayloadKind(AIReassembledMessage message, AIPayloadKind expected)
    {
        if (message.Envelope.Kind == AIMessageKind.Error || !message.Envelope.Success)
            throw new AIComponentClientException(message.Envelope.ErrorMessage ?? "The System AI extension returned an error.");
        if (message.Envelope.PayloadKind != expected)
            throw new AIComponentClientException($"The extension returned payload kind {message.Envelope.PayloadKind}, expected {expected}.");
    }

    private static T? DeserializeMetadata<T>(JsonElement metadata)
    {
        if (metadata.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return default;
        return JsonSerializer.Deserialize<T>(metadata.GetRawText(), AIComponentProtocol.JsonOptions);
    }
}
#endif
