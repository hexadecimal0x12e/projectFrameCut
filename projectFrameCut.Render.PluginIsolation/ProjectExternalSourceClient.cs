using projectFrameCut.Render.Contracts;

namespace projectFrameCut.Render.PluginIsolation;

public sealed class ProjectExternalSourceClient(IPluginIsolationSession session) : IExternalVideoSourceProvider, IExternalAudioSourceProvider, IExternalMaskSourceProvider, IAsyncDisposable
{
    public bool IsAvailable => !session.Control.Completion.IsCompleted;
    private string? frameError;
    private long frameRevision;
    public string? FrameError => Volatile.Read(ref frameError);
    public long FrameRevision => Volatile.Read(ref frameRevision);
    public IReadOnlyList<ExternalVideoSourceDescriptor> Sources { get; private set; } = [];
    public IReadOnlyList<ExternalAudioSourceDescriptor> AudioSources { get; private set; } = [];
    public IReadOnlyList<ExternalMaskSourceDescriptor> MaskSources { get; private set; } = [];
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Sources = (await session.InvokeAsync<EmptyRequest, ExternalVideoSourceCatalog>(RenderOperation.IsolationListProjectExternalSources, new(), cancellationToken).ConfigureAwait(false)).Sources;
        AudioSources = (await session.InvokeAsync<EmptyRequest, ExternalAudioSourceCatalog>(RenderOperation.IsolationListProjectExternalAudioSources, new(), cancellationToken).ConfigureAwait(false)).Sources;
        MaskSources = (await session.InvokeAsync<EmptyRequest, ExternalMaskSourceCatalog>(RenderOperation.IsolationListProjectExternalMaskSources, new(), cancellationToken).ConfigureAwait(false)).Sources;
    }
    public ValueTask<ExternalVideoSourceInstance> CreateAsync(ExternalVideoSourceCreateRequest request, CancellationToken cancellationToken = default) =>
        session.InvokeAsync<ExternalVideoSourceCreateRequest, ExternalVideoSourceInstance>(RenderOperation.ExternalVideoSourceCreate, request, cancellationToken);
    public ValueTask<ExternalVideoSourceInstance> InitializeAsync(ExternalVideoSourceStateRequest request, CancellationToken cancellationToken = default) =>
        session.InvokeAsync<ExternalVideoSourceStateRequest, ExternalVideoSourceInstance>(RenderOperation.ExternalVideoSourceInitialize, request, cancellationToken);
    public async ValueTask<ExternalVideoFrame> ReadFrameAsync(ExternalVideoSourceReadRequest request, CancellationToken cancellationToken = default)
    {
        ProjectExternalSourceFrame? response = null;
        try
        {
            response = await session.InvokeAsync<ExternalVideoSourceReadRequest, ProjectExternalSourceFrame>(RenderOperation.ExternalVideoSourceReadFrame, request, cancellationToken).ConfigureAwait(false);
            await using var stream = await session.Payloads.OpenReadAsync(response.Payload, cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            var frame = RenderRpcSerializer.Deserialize<ExternalVideoFrame>(buffer.ToArray());
            frame.Validate();
            if (Interlocked.Exchange(ref frameError, null) is not null) Interlocked.Increment(ref frameRevision);
            return frame;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref frameError, ex.Message);
            Interlocked.Increment(ref frameRevision);
            Log(ex, "Read project external source frame.");
            throw;
        }
        finally { if (response is not null) await session.Payloads.ReleaseAsync(response.Payload).ConfigureAwait(false); }
    }
    public async ValueTask ReleaseAsync(ExternalVideoSourceStateRequest request, CancellationToken cancellationToken = default) =>
        _ = await session.InvokeAsync<ExternalVideoSourceStateRequest, EmptyResponse>(RenderOperation.ExternalVideoSourceRelease, request, cancellationToken).ConfigureAwait(false);
    public ValueTask<ExternalAudioSourceInstance> CreateAudioAsync(ExternalAudioSourceCreateRequest request, CancellationToken cancellationToken = default) =>
        session.InvokeAsync<ExternalAudioSourceCreateRequest, ExternalAudioSourceInstance>(RenderOperation.ExternalAudioSourceCreate, request, cancellationToken);
    public ValueTask<ExternalAudioSourceInstance> InitializeAudioAsync(ExternalAudioSourceStateRequest request, CancellationToken cancellationToken = default) =>
        session.InvokeAsync<ExternalAudioSourceStateRequest, ExternalAudioSourceInstance>(RenderOperation.ExternalAudioSourceInitialize, request, cancellationToken);
    public async ValueTask<ExternalAudioSamples> ReadSamplesAsync(ExternalAudioSourceReadRequest request, CancellationToken cancellationToken = default)
    {
        IsolationAudioSamplesResponse? response = null;
        try
        {
            request.Validate();
            response = await session.InvokeAsync<ExternalAudioSourceReadRequest, IsolationAudioSamplesResponse>(RenderOperation.ExternalAudioSourceReadSamples, request, cancellationToken).ConfigureAwait(false);
            if (response.ChannelCount <= 0 || response.ChannelCount > 256 || response.SamplePerSecond <= 0
                || response.SampleCount < 0 || response.SampleCount > request.Count
                || (long)response.ChannelCount * response.SampleCount * sizeof(float) != response.Samples.Length)
                throw new InvalidDataException("External audio payload descriptor is invalid.");
            await using var stream = await session.Payloads.OpenReadAsync(response.Samples, cancellationToken).ConfigureAwait(false);
            var samples = new ExternalAudioSamples { ChannelCount = response.ChannelCount, SamplePerSecond = response.SamplePerSecond,
                SampleCount = response.SampleCount, Samples = new byte[checked((int)response.Samples.Length)] };
            await stream.ReadExactlyAsync(samples.Samples, cancellationToken).ConfigureAwait(false);
            if (Interlocked.Exchange(ref frameError, null) is not null) Interlocked.Increment(ref frameRevision);
            return samples;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref frameError, ex.Message);
            Interlocked.Increment(ref frameRevision);
            Log(ex, "Read project external source audio.");
            throw;
        }
        finally { if (response is not null) await session.Payloads.ReleaseAsync(response.Samples).ConfigureAwait(false); }
    }
    public async ValueTask ReleaseAudioAsync(ExternalAudioSourceStateRequest request, CancellationToken cancellationToken = default) =>
        _ = await session.InvokeAsync<ExternalAudioSourceStateRequest, EmptyResponse>(RenderOperation.ExternalAudioSourceRelease, request, cancellationToken).ConfigureAwait(false);
    public ValueTask DisposeAsync() => session.DisposeAsync();

    public async ValueTask<ExternalVideoFrame> ProcessMaskAsync(ExternalMaskSourceProcessRequest request, CancellationToken cancellationToken = default)
    {
        ProjectExternalSourceFrame? response = null;
        try
        {
            request.Validate();
            await using var backdrop = await session.Payloads.PublishAsync(RenderRpcSerializer.Serialize(request.Backdrop), session.PreferredPayloadKind, cancellationToken).ConfigureAwait(false);
            response = await session.InvokeAsync<ProjectExternalMaskSourceProcessRequest, ProjectExternalSourceFrame>(RenderOperation.ExternalMaskSourceProcess, new()
            {
                Request = new() { Source = request.Source, Arguments = request.Arguments, Scale = request.Scale },
                Backdrop = backdrop.Reference,
            }, cancellationToken).ConfigureAwait(false);
            await using var stream = await session.Payloads.OpenReadAsync(response.Payload, cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            var frame = RenderRpcSerializer.Deserialize<ExternalVideoFrame>(buffer.ToArray());
            request.ValidateResult(frame);
            if (Interlocked.Exchange(ref frameError, null) is not null) Interlocked.Increment(ref frameRevision);
            return frame;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref frameError, ex.Message);
            Interlocked.Increment(ref frameRevision);
            Log(ex, $"Process project external mask source {request.Source.SourceId}.");
            throw;
        }
        finally { if (response is not null) await session.Payloads.ReleaseAsync(response.Payload).ConfigureAwait(false); }
    }
}
