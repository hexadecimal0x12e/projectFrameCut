using System.Buffers.Binary;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.EncodeAndDecode;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class ExternalAudioSourceTests
{
    [TestMethod]
    public void AudioReferencesAndPcmRoundTrip()
    {
        var id = Guid.NewGuid();
        var descriptor = new ExternalAudioSourceDescriptor { ClientId = id, SourceId = "audio", DecoderName = "ExampleAudio",
            Name = "Audio", ChannelCount = 2, SamplePerSecond = 48000, Duration = 10, Metadata = new() { ["tone"] = "440" } };
        var rpcPath = RemoteRpcAudioSource.CreatePath(descriptor);
        Assert.IsTrue(RemoteRpcAudioSource.TryGetDescriptor(rpcPath, out var rpc));
        Assert.AreEqual(id, rpc.ClientId);
        Assert.AreEqual(48000, rpc.SamplePerSecond);
        var projectPath = ProjectExternalAudioSource.CreatePath(id, descriptor);
        Assert.IsTrue(ProjectExternalAudioSource.TryGetDescriptor(projectPath, out var project));
        Assert.AreEqual("440", project.Metadata["tone"]);
        Assert.AreEqual(10u, project.Duration);
        Assert.IsFalse(RemoteRpcAudioSource.TryGetDescriptor("#RemoteRpcAudioSource:invalid", out _));
        var samples = RenderRpcSerializer.Clone(new ExternalAudioSamples { ChannelCount = 2, SamplePerSecond = 48000,
            SampleCount = 1, Samples = new byte[8] });
        samples.Validate();
        samples.SampleCount = 2;
        Assert.Throws<InvalidDataException>(samples.Validate);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExternalAudioSourceReadRequest { Count = -1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExternalAudioSourceReadRequest { Count = (long)int.MaxValue + 1 }.Validate());
    }
}

internal sealed class TestAudioSourceProvider : IExternalAudioSourceProvider
{
    private readonly HashSet<Guid> instances = [];
    public int Released { get; private set; }
    public ExternalAudioSourceReadRequest? LastRead { get; private set; }
    public IReadOnlyList<ExternalAudioSourceDescriptor> AudioSources { get; } = [new() { SourceId = "audio",
        Name = "Test audio", DecoderName = "ExampleAudio", Duration = 10, ChannelCount = 2, SamplePerSecond = 48000 }];

    public ValueTask<ExternalAudioSourceInstance> CreateAudioAsync(ExternalAudioSourceCreateRequest request, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        instances.Add(id);
        return ValueTask.FromResult(new ExternalAudioSourceInstance { InstanceId = id, Descriptor = RenderRpcSerializer.Clone(AudioSources.Single(x => x.SourceId == request.Source.SourceId)) });
    }

    public ValueTask<ExternalAudioSourceInstance> InitializeAudioAsync(ExternalAudioSourceStateRequest request, CancellationToken cancellationToken = default)
    {
        if (!instances.Contains(request.InstanceId)) throw new InvalidOperationException("Unknown audio instance.");
        return ValueTask.FromResult(new ExternalAudioSourceInstance { InstanceId = request.InstanceId, Descriptor = RenderRpcSerializer.Clone(AudioSources[0]) });
    }

    public ValueTask<ExternalAudioSamples> ReadSamplesAsync(ExternalAudioSourceReadRequest request, CancellationToken cancellationToken = default)
    {
        if (!instances.Contains(request.State.InstanceId)) throw new InvalidOperationException("Unknown audio instance.");
        LastRead = RenderRpcSerializer.Clone(request);
        var count = (int)Math.Min(request.Count, Math.Max(0L, 480000L - request.StartIndex));
        var samples = new ExternalAudioSamples { ChannelCount = 2, SamplePerSecond = 48000, SampleCount = count, Samples = new byte[checked(count * 8)] };
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(samples.Samples.AsSpan(i * 4), (request.StartIndex + i) / 1000f);
            BinaryPrimitives.WriteSingleLittleEndian(samples.Samples.AsSpan((count + i) * 4), -(request.StartIndex + i) / 1000f);
        }
        return ValueTask.FromResult(samples);
    }

    public ValueTask ReleaseAudioAsync(ExternalAudioSourceStateRequest request, CancellationToken cancellationToken = default)
    {
        if (instances.Remove(request.InstanceId)) Released++;
        return ValueTask.CompletedTask;
    }
}
