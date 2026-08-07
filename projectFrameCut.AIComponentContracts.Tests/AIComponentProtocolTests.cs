using System.Text;
using System.Text.Json;
using projectFrameCut.AIComponentContracts;

namespace projectFrameCut.AIComponentContracts.Tests;

[TestClass]
public sealed class AIComponentProtocolTests
{
    [TestMethod]
    public async Task WriterAndReaderRoundTripChunkedPayload()
    {
        byte[] payload = Enumerable.Range(0, AIComponentProtocol.MaxChunkBytes + 257)
            .Select(index => (byte)(index % 251))
            .ToArray();
        Guid requestId = Guid.NewGuid();

        using var stream = new MemoryStream();
        using var writer = new AIMessageWriter();
        await writer.WriteAsync(
            stream,
            new AIMessageEnvelope
            {
                Kind = AIMessageKind.Request,
                RequestId = requestId,
                Operation = "picture.echo",
                PayloadKind = AIPayloadKind.PicturePlanes,
                Metadata = JsonSerializer.SerializeToElement(new AIPictureDescriptor
                {
                    Width = 1,
                    Height = 1,
                    BitDepth = AIPictureBitDepth.Byte
                }, AIComponentProtocol.JsonOptions)
            },
            payload);

        stream.Position = 0;
        var result = await new AIMessageReader().ReadAsync(stream);

        Assert.IsNotNull(result);
        Assert.AreEqual(requestId, result.Envelope.RequestId);
        Assert.AreEqual(AIPayloadKind.PicturePlanes, result.Envelope.PayloadKind);
        CollectionAssert.AreEqual(payload, result.Payload);
    }

    [TestMethod]
    public async Task EmptyMetadataIsAValidJsonObject()
    {
        using var stream = new MemoryStream();
        using var writer = new AIMessageWriter();
        await writer.WriteAsync(
            stream,
            new AIMessageEnvelope
            {
                Kind = AIMessageKind.Request,
                RequestId = Guid.NewGuid(),
                Operation = "text.echo",
                PayloadKind = AIPayloadKind.TextUtf8
            },
            Encoding.UTF8.GetBytes("你好，System AI"));

        stream.Position = 0;
        var result = await new AIMessageReader().ReadAsync(stream);

        Assert.IsNotNull(result);
        Assert.AreEqual(JsonValueKind.Object, result.Envelope.Metadata.ValueKind);
        Assert.AreEqual("你好，System AI", Encoding.UTF8.GetString(result.Payload));
    }

    [TestMethod]
    public async Task ReaderRejectsEnvelopeLengthMismatch()
    {
        using var stream = new MemoryStream();
        Guid requestId = Guid.NewGuid();
        var envelope = new AIMessageEnvelope
        {
            Kind = AIMessageKind.Request,
            RequestId = requestId,
            Operation = "audio.echo",
            PayloadKind = AIPayloadKind.AudioPcm,
            PayloadLength = 1,
            Metadata = AIComponentProtocol.EmptyMetadata
        };
        byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(envelope, AIComponentProtocol.JsonOptions);

        await AIFrameCodec.WriteFrameAsync(
            stream,
            new AIFrameHeader
            {
                MessageKind = AIMessageKind.Request,
                Flags = AIFrameFlags.Start | AIFrameFlags.End,
                PayloadKind = AIPayloadKind.AudioPcm,
                RequestId = requestId,
                MetadataLength = metadata.Length,
                PayloadLength = 0,
                PayloadOffset = 0,
                TotalPayloadLength = 0
            },
            metadata,
            ReadOnlyMemory<byte>.Empty);

        stream.Position = 0;
        await Assert.ThrowsExceptionAsync<AIProtocolException>(() => new AIMessageReader().ReadAsync(stream));
    }
}
