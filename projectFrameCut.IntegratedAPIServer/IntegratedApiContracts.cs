using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography.X509Certificates;

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
    AddEffectProvider,
    RemoveEffectProvider,
    SaveProject,
    ProjectModeQuery,
    ProjectModeEdit,
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

    public string? RpcToken { get; init; }

    /// <summary>
    /// Project to load before a headless RPC server starts accepting requests.
    /// </summary>
    public string? ProjectRoot { get; init; }

    /// <summary>
    /// Optional global asset database used to resolve timeline references that
    /// are not stored in the project's own assets.json file.
    /// </summary>
    public string? GlobalAssetsDatabasePath { get; init; }

    public bool EnableMcp { get; init; } = true;

    public bool RequireMcpAuthorization { get; init; } = true;

    public bool IncludeIntegratedClientMcpTools { get; init; } = true;

    public Action<string>? WarningSink { get; init; }

    /// <summary>Optional certificate used by the lightweight HTTPS listener.</summary>
    public X509Certificate2? SslCertificate { get; init; }

    /// <summary>Optional PFX file used when <see cref="SslCertificate"/> is not supplied.</summary>
    public string? SslCertificateFile { get; init; }

    /// <summary>Password for <see cref="SslCertificateFile"/>.</summary>
    public string? SslCertificatePassword { get; init; }
}


internal sealed record HealthResponse(string Status, bool ProjectReady);

internal sealed record EchoResponse(string Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(EchoResponse))]
internal sealed partial class IntegratedApiJsonContext : JsonSerializerContext;
