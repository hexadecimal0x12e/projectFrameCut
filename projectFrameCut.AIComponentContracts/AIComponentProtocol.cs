using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.AIComponentContracts;

public static class AIComponentProtocol
{
    public const int CurrentVersion = 1;
    public const int FrameHeaderSize = 64;
    public const int MaxMetadataBytes = 1024 * 1024;
    public const int MaxChunkBytes = 4 * 1024 * 1024;
    public const long MaxPayloadBytes = 512L * 1024 * 1024;
    public const string PipePrefix = @"LOCAL\projectFrameCut.SystemAI.";
    public const string ProtocolScheme = "pjfc-systemai";
    public const string Magic = "PJAI";
    public static readonly byte[] MagicAsByte = "PJAI"u8.ToArray();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static readonly JsonElement EmptyMetadata = JsonSerializer.SerializeToElement(new { }, JsonOptions);

    public static string CreatePipeName(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Any(c => !char.IsLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("The session id contains invalid characters.", nameof(sessionId));

        return PipePrefix + sessionId;
    }
}

public enum AIMessageKind : byte
{
    Hello = 1,
    Request = 2,
    Response = 3,
    Error = 4,
    Cancel = 5,
    Heartbeat = 6
}

[Flags]
public enum AIFrameFlags : byte
{
    None = 0,
    Start = 1,
    End = 2,
    Compressed = 4
}

public enum AIPayloadKind : byte
{
    None = 0,
    Json = 1,
    TextUtf8 = 2,
    PicturePlanes = 3,
    AudioPcm = 4,
    Binary = 5
}

public enum AIPictureBitDepth : byte
{
    Byte = 8,
    UShort = 16
}

public enum AIAudioSampleFormat : byte
{
    Float32 = 1
}

public sealed class AIMessageEnvelope
{
    public int ProtocolVersion { get; set; } = AIComponentProtocol.CurrentVersion;
    public AIMessageKind Kind { get; set; }
    public Guid RequestId { get; set; }
    public string? Operation { get; set; }
    public AIPayloadKind PayloadKind { get; set; }
    public long PayloadLength { get; set; }
    public bool Success { get; set; } = true;
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public JsonElement Metadata { get; set; }
}

public sealed class AIHandshakeMetadata
{
    public string SessionId { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string? ClientVersion { get; set; }
}

public sealed class AIHandshakeResponseMetadata
{
    public string SessionId { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public string? ServerVersion { get; set; }
    public IReadOnlyList<AICapabilityDescriptor> Capabilities { get; set; } = Array.Empty<AICapabilityDescriptor>();
}

public sealed class AICapabilityDescriptor
{
    public string Operation { get; set; } = string.Empty;
    public AIPayloadKind Input { get; set; }
    public AIPayloadKind Output { get; set; }
    public string? Description { get; set; }
    public bool Streaming { get; set; }
}

public sealed class AIPictureDescriptor
{
    public int Width { get; set; }
    public int Height { get; set; }
    public AIPictureBitDepth BitDepth { get; set; }
    public bool HasAlpha { get; set; }
}

public sealed class AIAudioDescriptor
{
    public int SampleRate { get; set; }
    public int ChannelCount { get; set; }
    public int SampleCount { get; set; }
    public AIAudioSampleFormat SampleFormat { get; set; } = AIAudioSampleFormat.Float32;
    public string Layout { get; set; } = "planar";
}

public sealed class AIWireFrame
{
    public required AIFrameHeader Header { get; init; }
    public byte[] Metadata { get; init; } = Array.Empty<byte>();
    public byte[] Payload { get; init; } = Array.Empty<byte>();
}

public sealed class AIFrameHeader
{
    public AIMessageKind MessageKind { get; init; }
    public AIFrameFlags Flags { get; init; }
    public AIPayloadKind PayloadKind { get; init; }
    public Guid RequestId { get; init; }
    public int MetadataLength { get; init; }
    public int PayloadLength { get; init; }
    public long Sequence { get; init; }
    public long PayloadOffset { get; init; }
    public long TotalPayloadLength { get; init; }
}

public sealed class AIReassembledMessage
{
    public required AIMessageEnvelope Envelope { get; init; }
    public byte[] Payload { get; init; } = Array.Empty<byte>();
}

public sealed class AIProtocolException : IOException
{
    public AIProtocolException(string message) : base(message) { }
}

public sealed class AIMessageWriter : IDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task WriteAsync(Stream stream, AIMessageEnvelope envelope, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.RequestId == Guid.Empty)
            throw new AIProtocolException("Every message must have a request id.");
        if (payload.Length > AIComponentProtocol.MaxPayloadBytes)
            throw new AIProtocolException($"Payload is larger than {AIComponentProtocol.MaxPayloadBytes} bytes.");

        envelope.ProtocolVersion = AIComponentProtocol.CurrentVersion;
        envelope.PayloadLength = payload.Length;
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(envelope, AIComponentProtocol.JsonOptions);
        if (metadata.Length > AIComponentProtocol.MaxMetadataBytes)
            throw new AIProtocolException("Message metadata is too large.");

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (payload.Length == 0)
            {
                await AIFrameCodec.WriteFrameAsync(
                    stream,
                    new AIFrameHeader
                    {
                        MessageKind = envelope.Kind,
                        Flags = AIFrameFlags.Start | AIFrameFlags.End,
                        PayloadKind = envelope.PayloadKind,
                        RequestId = envelope.RequestId,
                        MetadataLength = metadata.Length,
                        PayloadLength = 0,
                        Sequence = 0,
                        PayloadOffset = 0,
                        TotalPayloadLength = 0
                    },
                    metadata,
                    ReadOnlyMemory<byte>.Empty,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            long offset = 0;
            long sequence = 0;
            while (offset < payload.Length)
            {
                int length = (int)Math.Min(AIComponentProtocol.MaxChunkBytes, payload.Length - offset);
                var flags = AIFrameFlags.None;
                if (offset == 0) flags |= AIFrameFlags.Start;
                if (offset + length == payload.Length) flags |= AIFrameFlags.End;

                await AIFrameCodec.WriteFrameAsync(
                    stream,
                    new AIFrameHeader
                    {
                        MessageKind = envelope.Kind,
                        Flags = flags,
                        PayloadKind = envelope.PayloadKind,
                        RequestId = envelope.RequestId,
                        MetadataLength = offset == 0 ? metadata.Length : 0,
                        PayloadLength = length,
                        Sequence = sequence++,
                        PayloadOffset = offset,
                        TotalPayloadLength = payload.Length
                    },
                    offset == 0 ? metadata : ReadOnlyMemory<byte>.Empty,
                    payload.Slice((int)offset, length),
                    cancellationToken).ConfigureAwait(false);

                offset += length;
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose() => _writeLock.Dispose();
}

public sealed class AIMessageReader
{
    private readonly Dictionary<Guid, PendingMessage> _pending = new();

    public async Task<AIReassembledMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            AIWireFrame? frame = await AIFrameCodec.ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
            if (frame is null)
                return null;

            var header = frame.Header;
            bool starts = header.Flags.HasFlag(AIFrameFlags.Start);
            bool ends = header.Flags.HasFlag(AIFrameFlags.End);

            if (starts)
            {
                if (header.PayloadOffset != 0 || _pending.ContainsKey(header.RequestId))
                    throw new AIProtocolException("A message was started more than once.");
                if (header.TotalPayloadLength > AIComponentProtocol.MaxPayloadBytes)
                    throw new AIProtocolException("The logical payload is too large.");

                var envelope = DeserializeEnvelope(frame.Metadata);
                if (envelope.RequestId != header.RequestId || envelope.Kind != header.MessageKind ||
                    envelope.PayloadKind != header.PayloadKind || envelope.PayloadLength != header.TotalPayloadLength)
                    throw new AIProtocolException("Message metadata does not match the frame header.");

                var pending = new PendingMessage(envelope, header.TotalPayloadLength);
                pending.Append(header.PayloadOffset, frame.Payload);
                _pending.Add(header.RequestId, pending);

                if (ends)
                    return Complete(header.RequestId);
            }
            else
            {
                if (!_pending.TryGetValue(header.RequestId, out var pending))
                    throw new AIProtocolException("A continuation frame has no active message.");
                pending.Append(header.PayloadOffset, frame.Payload);

                if (ends)
                    return Complete(header.RequestId);
            }
        }
    }

    private AIReassembledMessage Complete(Guid requestId)
    {
        var pending = _pending[requestId];
        _pending.Remove(requestId);
        return pending.Complete();
    }

    private static AIMessageEnvelope DeserializeEnvelope(byte[] metadata)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<AIMessageEnvelope>(metadata, AIComponentProtocol.JsonOptions);
            if (envelope is null)
                throw new AIProtocolException("Message metadata is empty.");
            if (envelope.ProtocolVersion != AIComponentProtocol.CurrentVersion)
                throw new AIProtocolException($"Unsupported protocol version {envelope.ProtocolVersion}.");
            envelope.Metadata = envelope.Metadata.ValueKind == JsonValueKind.Undefined
                ? AIComponentProtocol.EmptyMetadata
                : envelope.Metadata.Clone();
            return envelope;
        }
        catch (JsonException ex)
        {
            throw new AIProtocolException($"Invalid message metadata: {ex.Message}");
        }
    }

    private sealed class PendingMessage
    {
        private readonly MemoryStream _payload;
        private long _expectedOffset;

        public PendingMessage(AIMessageEnvelope envelope, long totalPayloadLength)
        {
            Envelope = envelope;
            if (totalPayloadLength > int.MaxValue)
                throw new AIProtocolException("A single reassembled message exceeds the supported in-memory size.");
            _payload = new MemoryStream((int)totalPayloadLength);
        }

        public AIMessageEnvelope Envelope { get; }

        public void Append(long offset, byte[] bytes)
        {
            if (offset != _expectedOffset)
                throw new AIProtocolException("Message frames are out of order.");
            if (bytes.Length > AIComponentProtocol.MaxChunkBytes)
                throw new AIProtocolException("A frame payload is too large.");

            _payload.Write(bytes, 0, bytes.Length);
            _expectedOffset += bytes.Length;
        }

        public AIReassembledMessage Complete()
        {
            if (_expectedOffset != Envelope.PayloadLength)
                throw new AIProtocolException("The reassembled payload length does not match its metadata.");

            return new AIReassembledMessage
            {
                Envelope = Envelope,
                Payload = _payload.ToArray()
            };
        }
    }
}

public static class AIFrameCodec
{
    public static async ValueTask WriteFrameAsync(
        Stream stream,
        AIFrameHeader header,
        ReadOnlyMemory<byte> metadata,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (metadata.Length != header.MetadataLength || payload.Length != header.PayloadLength)
            throw new AIProtocolException("Frame lengths do not match the frame header.");
        if (metadata.Length > AIComponentProtocol.MaxMetadataBytes || payload.Length > AIComponentProtocol.MaxChunkBytes)
            throw new AIProtocolException("Frame exceeds the configured size limit.");

        byte[] rawHeader = new byte[AIComponentProtocol.FrameHeaderSize];
        AIComponentProtocol.MagicAsByte.AsSpan().CopyTo(rawHeader.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(rawHeader.AsSpan(4, 2), AIComponentProtocol.CurrentVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(rawHeader.AsSpan(6, 2), AIComponentProtocol.FrameHeaderSize);
        rawHeader[8] = (byte)header.MessageKind;
        rawHeader[9] = (byte)header.Flags;
        rawHeader[10] = (byte)header.PayloadKind;
        header.RequestId.TryWriteBytes(rawHeader.AsSpan(12, 16));
        BinaryPrimitives.WriteInt32LittleEndian(rawHeader.AsSpan(28, 4), metadata.Length);
        BinaryPrimitives.WriteInt32LittleEndian(rawHeader.AsSpan(32, 4), payload.Length);
        BinaryPrimitives.WriteInt64LittleEndian(rawHeader.AsSpan(36, 8), header.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(rawHeader.AsSpan(44, 8), header.PayloadOffset);
        BinaryPrimitives.WriteInt64LittleEndian(rawHeader.AsSpan(52, 8), header.TotalPayloadLength);

        await stream.WriteAsync(rawHeader, cancellationToken).ConfigureAwait(false);
        if (!metadata.IsEmpty)
            await stream.WriteAsync(metadata, cancellationToken).ConfigureAwait(false);
        if (!payload.IsEmpty)
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<AIWireFrame?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] rawHeader = ArrayPool<byte>.Shared.Rent(AIComponentProtocol.FrameHeaderSize);
        try
        {
            int first = await stream.ReadAsync(rawHeader.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (first == 0)
                return null;

            await stream.ReadExactlyAsync(rawHeader.AsMemory(1, AIComponentProtocol.FrameHeaderSize - 1), cancellationToken).ConfigureAwait(false);
            if (!rawHeader.AsSpan(0, 4).SequenceEqual("PJAI"u8))
                throw new AIProtocolException("Invalid AI component frame magic.");

            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(rawHeader.AsSpan(4, 2));
            ushort headerSize = BinaryPrimitives.ReadUInt16LittleEndian(rawHeader.AsSpan(6, 2));
            if (version != AIComponentProtocol.CurrentVersion || headerSize != AIComponentProtocol.FrameHeaderSize)
                throw new AIProtocolException("Unsupported AI component frame version.");

            int metadataLength = BinaryPrimitives.ReadInt32LittleEndian(rawHeader.AsSpan(28, 4));
            int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(rawHeader.AsSpan(32, 4));
            long payloadOffset = BinaryPrimitives.ReadInt64LittleEndian(rawHeader.AsSpan(44, 8));
            long totalPayloadLength = BinaryPrimitives.ReadInt64LittleEndian(rawHeader.AsSpan(52, 8));

            if (metadataLength < 0 || metadataLength > AIComponentProtocol.MaxMetadataBytes ||
                payloadLength < 0 || payloadLength > AIComponentProtocol.MaxChunkBytes ||
                totalPayloadLength < 0 || totalPayloadLength > AIComponentProtocol.MaxPayloadBytes ||
                payloadOffset < 0 || payloadOffset > totalPayloadLength || payloadLength > totalPayloadLength - payloadOffset)
            {
                throw new AIProtocolException("Invalid AI component frame lengths.");
            }

            byte[] metadata = new byte[metadataLength];
            byte[] payload = new byte[payloadLength];
            if (metadataLength > 0)
                await stream.ReadExactlyAsync(metadata, cancellationToken).ConfigureAwait(false);
            if (payloadLength > 0)
                await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);

            return new AIWireFrame
            {
                Header = new AIFrameHeader
                {
                    MessageKind = (AIMessageKind)rawHeader[8],
                    Flags = (AIFrameFlags)rawHeader[9],
                    PayloadKind = (AIPayloadKind)rawHeader[10],
                    RequestId = new Guid(rawHeader.AsSpan(12, 16)),
                    MetadataLength = metadataLength,
                    PayloadLength = payloadLength,
                    Sequence = BinaryPrimitives.ReadInt64LittleEndian(rawHeader.AsSpan(36, 8)),
                    PayloadOffset = payloadOffset,
                    TotalPayloadLength = totalPayloadLength
                },
                Metadata = metadata,
                Payload = payload
            };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rawHeader);
        }
    }
}
