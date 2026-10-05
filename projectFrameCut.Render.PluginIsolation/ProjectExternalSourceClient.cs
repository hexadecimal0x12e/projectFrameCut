using projectFrameCut.Render.Contracts;

namespace projectFrameCut.Render.PluginIsolation;

public sealed class ProjectExternalSourceClient(IPluginIsolationSession session) : IExternalVideoSourceProvider, IAsyncDisposable
{
    public bool IsAvailable => !session.Control.Completion.IsCompleted;
    private string? frameError;
    private long frameRevision;
    public string? FrameError => Volatile.Read(ref frameError);
    public long FrameRevision => Volatile.Read(ref frameRevision);
    public IReadOnlyList<ExternalVideoSourceDescriptor> Sources { get; private set; } = [];
    public async Task LoadAsync(CancellationToken cancellationToken = default) => Sources =
        (await session.InvokeAsync<EmptyRequest, ExternalVideoSourceCatalog>(RenderOperation.IsolationListProjectExternalSources, new(), cancellationToken).ConfigureAwait(false)).Sources;
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
    public ValueTask DisposeAsync() => session.DisposeAsync();
}
