using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.IntegratedAPIServer;

public enum IntegratedApiOperation
{
    GetTimelineInfo,
    ListLayers,
    ListAvailableEffects,
    GetEffectInfo,
    GetProjectMetadata,
    ListConnectedClients,
    GetClientEnvironment,
    RenderClientPreview,
    ApplyClientPatch,
    MoveClientClip,
    ListClips,
    GetClip,
    UpsertClip,
    MoveClip,
    PatchClip,
    DeleteClip,
    AddEffect,
    RemoveEffect,
    AddEffectBundle,
    RemoveEffectBundle,
    SaveProject,
}

public sealed record IntegratedApiAuthorizationRequest(
    string ClientName,
    string ClientVersion,
    string RemoteAddress,
    string Reason);

public interface IIntegratedApiBackend
{
    ValueTask<JsonElement> ExecuteAsync(
        IntegratedApiOperation operation,
        JsonElement arguments,
        CancellationToken cancellationToken = default);

    ValueTask<bool> RequestAuthorizationAsync(
        IntegratedApiAuthorizationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class IntegratedApiServerOptions
{
    public required Uri ListenUri { get; init; }

    public Action<string>? WarningSink { get; init; }
}


internal sealed record HealthResponse(string Status, bool ProjectReady);

internal sealed record EchoResponse(string Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(EchoResponse))]
internal sealed partial class IntegratedApiJsonContext : JsonSerializerContext;