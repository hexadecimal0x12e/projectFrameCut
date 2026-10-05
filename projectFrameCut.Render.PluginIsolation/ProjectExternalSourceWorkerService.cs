using projectFrameCut.Render.Contracts;

namespace projectFrameCut.Render.PluginIsolation;

public sealed class ProjectExternalSourceWorkerService(IExternalVideoSourceProvider provider, string token, int parentPid, SessionPayloadExchange payloads, CancellationTokenSource lifetime) : IRenderService, IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HashSet<Guid> instances = [];
    private bool authenticated;
    private IsolationPayloadKind payloadKind = IsolationPayloadKind.SharedMemory;

    public async ValueTask<RenderResponseEnvelope> DispatchAsync(RenderRequestEnvelope request, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (request.ProtocolVersion != RenderProtocol.CurrentVersion) throw new InvalidDataException("Unsupported external source protocol version.");
            if (!authenticated)
            {
                if (request.Operation != RenderOperation.IsolationNegotiate) throw new UnauthorizedAccessException("External source session is not authenticated.");
                var negotiation = RenderRpcSerializer.Deserialize<IsolationNegotiateRequest>(request.Payload);
                if (negotiation.AuthenticationToken != token || negotiation.HostProcessId != parentPid) throw new UnauthorizedAccessException("Invalid external source session.");
                payloadKind = negotiation.PreferredPayloadKind;
                if (payloadKind is not (IsolationPayloadKind.Inline or IsolationPayloadKind.SharedMemory or IsolationPayloadKind.LocalFile)) throw new NotSupportedException("Unsupported external source frame payload mode.");
                authenticated = true;
                return Success(new IsolationChannelCapabilities { Platform = Environment.OSVersion.Platform.ToString(), PayloadKinds = [IsolationPayloadKind.Inline, IsolationPayloadKind.SharedMemory, IsolationPayloadKind.LocalFile] });
            }
            switch (request.Operation)
            {
                case RenderOperation.IsolationListProjectExternalSources:
                    return Success(new ExternalVideoSourceCatalog { Sources = provider.Sources.Select(x => RenderRpcSerializer.Clone(x)).ToList() });
                case RenderOperation.ExternalVideoSourceCreate:
                    var create = RenderRpcSerializer.Deserialize<ExternalVideoSourceCreateRequest>(request.Payload);
                    if (!provider.Sources.Any(x => x.SourceId == create.Source.SourceId && x.DecoderName == create.Source.DecoderName))
                        throw new UnauthorizedAccessException("Requested source is not in this provider's catalog.");
                    var created = await provider.CreateAsync(create, cancellationToken).ConfigureAwait(false);
                    if (created.InstanceId == Guid.Empty || !instances.Add(created.InstanceId)) throw new InvalidDataException("External source returned an invalid or duplicate instance ID.");
                    return Success(created);
                case RenderOperation.ExternalVideoSourceInitialize:
                    var initialize = RenderRpcSerializer.Deserialize<ExternalVideoSourceStateRequest>(request.Payload);
                    RequireInstance(initialize.InstanceId);
                    var initialized = await provider.InitializeAsync(initialize, cancellationToken).ConfigureAwait(false);
                    if (initialized.InstanceId != initialize.InstanceId) throw new InvalidDataException("External source initialization changed its instance ID.");
                    return Success(initialized);
                case RenderOperation.ExternalVideoSourceReadFrame:
                    var read = RenderRpcSerializer.Deserialize<ExternalVideoSourceReadRequest>(request.Payload);
                    RequireInstance(read.State.InstanceId);
                    var frame = await provider.ReadFrameAsync(read, cancellationToken).ConfigureAwait(false);
                    frame.Validate();
                    var lease = await payloads.PublishAsync(RenderRpcSerializer.Serialize(frame), payloadKind, cancellationToken).ConfigureAwait(false);
                    return Success(new ProjectExternalSourceFrame { Payload = lease.Reference });
                case RenderOperation.ExternalVideoSourceRelease:
                    var release = RenderRpcSerializer.Deserialize<ExternalVideoSourceStateRequest>(request.Payload);
                    RequireInstance(release.InstanceId);
                    try { await provider.ReleaseAsync(release, cancellationToken).ConfigureAwait(false); }
                    finally { instances.Remove(release.InstanceId); }
                    return Success(new EmptyResponse());
                case RenderOperation.IsolationShutdown:
                    await ReleaseInstancesAsync().ConfigureAwait(false);
                    lifetime.CancelAfter(100);
                    return Success(new EmptyResponse());
                default: throw new NotSupportedException($"Unsupported external source operation {request.Operation}.");
            }
        }
        catch (Exception ex) { Log(ex, $"External source operation {request.Operation}."); return new() { RequestId = request.RequestId, Error = new(ex) }; }
        finally { gate.Release(); }

        RenderResponseEnvelope Success<T>(T value) => new() { RequestId = request.RequestId, Payload = RenderRpcSerializer.Serialize(value) };
    }

    private void RequireInstance(Guid id)
    {
        if (!instances.Contains(id)) throw new UnauthorizedAccessException("External source instance does not belong to this worker.");
    }
    private async Task ReleaseInstancesAsync()
    {
        foreach (var id in instances.ToArray())
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await provider.ReleaseAsync(new() { InstanceId = id }, timeout.Token).AsTask().WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (Exception ex) { Log(ex, $"Release external source instance {id}."); }
        }
        instances.Clear();
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { await ReleaseInstancesAsync().ConfigureAwait(false); }
        finally { gate.Release(); gate.Dispose(); }
    }
}
