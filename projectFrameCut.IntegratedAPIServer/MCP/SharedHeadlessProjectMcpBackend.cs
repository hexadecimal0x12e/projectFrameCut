using System.Text.Json;
using System.Text.Json.Serialization;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.Project;

namespace projectFrameCut.IntegratedAPIServer.MCP;

/// <summary>
/// Runs MCP operations through the same headless session that is published to
/// remote project clients. This keeps MCP and GUI edits under one revision and
/// optimistic-concurrency model.
/// </summary>
internal sealed class SharedHeadlessProjectMcpBackend : IIntegratedApiBackend, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly IRenderClient _client;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly bool _ownsSession;
    private HeadlessProjectSnapshot _snapshot;

    private SharedHeadlessProjectMcpBackend(IRenderClient client, HeadlessProjectSnapshot snapshot, bool ownsSession)
    {
        _client = client;
        _snapshot = snapshot;
        _ownsSession = ownsSession;
    }

    public static async Task<SharedHeadlessProjectMcpBackend> CreateAsync(
        IRenderService service,
        string? projectRoot = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        var client = new RenderClient(new DirectRenderTransport(service), "headless-mcp");
        try
        {
            bool ownsSession = !string.IsNullOrWhiteSpace(projectRoot);
            HeadlessProjectSnapshot snapshot = ownsSession
                ? await client.OpenHeadlessProjectAsync(
                    new OpenHeadlessProjectRequest { ProjectRoot = projectRoot! },
                    cancellationToken).ConfigureAwait(false)
                : await client.GetHeadlessProjectSnapshotAsync(
                    Guid.Empty,
                    cancellationToken).ConfigureAwait(false);
            return new SharedHeadlessProjectMcpBackend(client, snapshot, ownsSession);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<JsonElement> ExecuteAsync(
        IntegratedApiOperation operation,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A remote GUI may have saved since the preceding MCP call. Always
            // start from the current server revision before creating a mutation
            // precondition or answering a query.
            _snapshot = await _client.GetHeadlessProjectSnapshotAsync(
                _snapshot.SessionId,
                cancellationToken).ConfigureAwait(false);

            return operation switch
            {
                IntegratedApiOperation.GetTimelineInfo => GetTimelineInfo(),
                IntegratedApiOperation.ListLayers => GetLayers(),
                IntegratedApiOperation.ListAvailableEffects => ToElement(ProjectModeEditingService.GetAvailableEffects()),
                IntegratedApiOperation.GetEffectInfo => ToElement(ProjectModeEditingService.GetEffectInfo(RequiredString(arguments, "effectType"))),
                IntegratedApiOperation.GetProjectMetadata => GetProjectMetadata(),
                IntegratedApiOperation.ListClips => await ListClipsAsync(cancellationToken).ConfigureAwait(false),
                IntegratedApiOperation.GetClip => await GetClipAsync(
                    RequiredString(arguments, "clipId"),
                    cancellationToken).ConfigureAwait(false),
                IntegratedApiOperation.UpsertClip => await MutateClipAsync(
                    _client.UpsertHeadlessClipAsync,
                    clipId: string.Empty,
                    json: RequiredProperty(arguments, "clip").GetRawText(),
                    cancellationToken).ConfigureAwait(false),
                IntegratedApiOperation.MoveClip => await MoveClipAsync(arguments, cancellationToken).ConfigureAwait(false),
                IntegratedApiOperation.PatchClip => await MutateClipAsync(
                    _client.PatchHeadlessClipAsync,
                    RequiredString(arguments, "clipId"),
                    RequiredProperty(arguments, "patch").GetRawText(),
                    cancellationToken).ConfigureAwait(false),
                IntegratedApiOperation.DeleteClip => Wrap(
                    "deleted",
                    await MutateClipAsync(
                        _client.DeleteHeadlessClipAsync,
                        RequiredString(arguments, "clipId"),
                        "null",
                        cancellationToken).ConfigureAwait(false)),
                IntegratedApiOperation.AddEffect => await MutateClipAsync(
                    _client.AddOrReplaceHeadlessEffectAsync,
                    RequiredString(arguments, "clipId"),
                    RequiredProperty(arguments, "effect").GetRawText(),
                    cancellationToken).ConfigureAwait(false),
                IntegratedApiOperation.RemoveEffect => Wrap(
                    "removed",
                    await RemoveEffectAsync(arguments, cancellationToken).ConfigureAwait(false)),
                IntegratedApiOperation.AddEffectBundle => await MutateClipAsync(
                    _client.AddOrReplaceHeadlessEffectBundleAsync,
                    RequiredString(arguments, "clipId"),
                    RequiredProperty(arguments, "bundle").GetRawText(),
                    cancellationToken).ConfigureAwait(false),
                IntegratedApiOperation.RemoveEffectBundle => Wrap(
                    "removed",
                    await RemoveEffectBundleAsync(arguments, cancellationToken).ConfigureAwait(false)),
                IntegratedApiOperation.SaveProject => await SaveProjectAsync(
                    OptionalString(arguments, "changeReason"),
                    cancellationToken).ConfigureAwait(false),
                IntegratedApiOperation.ProjectModeQuery => QueryProjectMode(arguments),
                IntegratedApiOperation.ProjectModeEdit => await ApplyProjectModeEditAsync(arguments, cancellationToken).ConfigureAwait(false),
                IntegratedApiOperation.ListConnectedClients or
                IntegratedApiOperation.GetClientEnvironment or
                IntegratedApiOperation.RenderClientPreview or
                IntegratedApiOperation.ApplyClientPatch or
                IntegratedApiOperation.MoveClientClip => throw new InvalidOperationException(
                    "Integrated editor client tools are not exposed by the headless MCP server."),
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown integrated API operation."),
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public ValueTask<bool> RequestAuthorizationAsync(
        IntegratedApiAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(true);
    }

    private JsonElement GetTimelineInfo()
    {
        ProjectJSONStructure project = Deserialize<ProjectJSONStructure>(_snapshot.ProjectJson);
        DraftStructureJSON draft = Deserialize<DraftStructureJSON>(_snapshot.TimelineJson);
        uint totalFrames = draft.Clips.Length == 0
            ? 0
            : draft.Clips.Max(static clip => clip.StartFrame + clip.Duration);
        uint layerCount = draft.Clips.Length == 0
            ? 0
            : draft.Clips.Max(static clip => clip.LayerIndex) + 1;

        return ToElement(new
        {
            projectName = project.ProjectName,
            width = project.RelativeWidth,
            height = project.RelativeHeight,
            frameRate = project.TargetFrameRate,
            totalFrames,
            layerCount,
            clipCount = draft.Clips.Length,
            lastChanged = project.LastChanged,
            savedAt = draft.SavedAt,
            revision = _snapshot.Revision,
            snapshotHash = _snapshot.SnapshotHash,
        });
    }

    private JsonElement GetLayers()
    {
        DraftStructureJSON draft = Deserialize<DraftStructureJSON>(_snapshot.TimelineJson);
        return ToElement(new
        {
            layers = draft.Clips
                .GroupBy(static clip => clip.LayerIndex)
                .OrderBy(static group => group.Key)
                .Select(static group => new
                {
                    layerIndex = group.Key,
                    clipCount = group.Count(),
                    clips = group
                        .OrderBy(static clip => clip.StartFrame)
                        .ThenBy(static clip => clip.SubLayerIndex)
                        .Select(static clip => new
                        {
                            id = clip.Id,
                            name = clip.Name,
                            startFrame = clip.StartFrame,
                            duration = clip.Duration,
                            subLayerIndex = clip.SubLayerIndex,
                        })
                        .ToArray(),
                })
                .ToArray(),
        });
    }

    private JsonElement GetProjectMetadata()
    {
        ProjectJSONStructure project = Deserialize<ProjectJSONStructure>(_snapshot.ProjectJson);
        long fileSize = 0;
        foreach (string fileName in new[] { "project.pjfc", "project.json", "timeline.json", "assets.json" })
        {
            string path = Path.Combine(_snapshot.ProjectRoot, fileName);
            if (File.Exists(path)) fileSize += new FileInfo(path).Length;
        }

        return ToElement(new
        {
            projectName = project.ProjectName,
            projectPath = _snapshot.ProjectRoot,
            fileSize,
            createdOrModified = project.LastChanged,
            lastSnapshotId = project.LastSnapshotID,
            pluginsUsed = project.PluginUsed,
            normallyExited = project.NormallyExited,
            revision = _snapshot.Revision,
            snapshotHash = _snapshot.SnapshotHash,
        });
    }

    private async ValueTask<JsonElement> ListClipsAsync(CancellationToken cancellationToken)
    {
        HeadlessJsonResponse response = await _client.ListHeadlessClipsAsync(
            _snapshot.SessionId,
            cancellationToken).ConfigureAwait(false);
        _snapshot = response.Snapshot;
        return ToElement(new { clips = ParseElement(response.Json) });
    }

    private async ValueTask<JsonElement> GetClipAsync(string clipId, CancellationToken cancellationToken)
    {
        HeadlessJsonResponse response = await _client.GetHeadlessClipAsync(
            new HeadlessClipRequest { SessionId = _snapshot.SessionId, ClipId = clipId },
            cancellationToken).ConfigureAwait(false);
        _snapshot = response.Snapshot;
        return ParseElement(response.Json);
    }

    private async ValueTask<JsonElement> MutateClipAsync(
        Func<HeadlessClipMutationRequest, CancellationToken, ValueTask<HeadlessJsonResponse>> mutation,
        string clipId,
        string json,
        CancellationToken cancellationToken)
    {
        HeadlessJsonResponse response = await mutation(new HeadlessClipMutationRequest
        {
            Precondition = CreatePrecondition(),
            ClipId = clipId,
            Json = json,
        }, cancellationToken).ConfigureAwait(false);
        _snapshot = response.Snapshot;
        return ParseElement(response.Json);
    }

    private async ValueTask<JsonElement> MoveClipAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        uint? subLayerIndex = OptionalUInt32(arguments, "subLayerIndex");
        HeadlessJsonResponse response = await _client.MoveHeadlessClipAsync(new MoveHeadlessClipRequest
        {
            Precondition = CreatePrecondition(),
            ClipId = RequiredString(arguments, "clipId"),
            LayerIndex = RequiredUInt32(arguments, "layerIndex"),
            StartFrame = RequiredUInt32(arguments, "startFrame"),
            HasSubLayerIndex = subLayerIndex.HasValue,
            SubLayerIndex = subLayerIndex.GetValueOrDefault(),
        }, cancellationToken).ConfigureAwait(false);
        _snapshot = response.Snapshot;
        return ParseElement(response.Json);
    }

    private async ValueTask<JsonElement> RemoveEffectAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        HeadlessJsonResponse response = await _client.RemoveHeadlessEffectAsync(new RemoveHeadlessEffectRequest
        {
            Precondition = CreatePrecondition(),
            ClipId = RequiredString(arguments, "clipId"),
            EffectKey = RequiredString(arguments, "effectKey"),
        }, cancellationToken).ConfigureAwait(false);
        _snapshot = response.Snapshot;
        return ParseElement(response.Json);
    }

    private async ValueTask<JsonElement> RemoveEffectBundleAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        HeadlessJsonResponse response = await _client.RemoveHeadlessEffectBundleAsync(new RemoveHeadlessEffectBundleRequest
        {
            Precondition = CreatePrecondition(),
            ClipId = RequiredString(arguments, "clipId"),
            BundleId = Guid.Parse(RequiredString(arguments, "bundleId")),
        }, cancellationToken).ConfigureAwait(false);
        _snapshot = response.Snapshot;
        return ParseElement(response.Json);
    }

    private async ValueTask<JsonElement> SaveProjectAsync(string? changeReason, CancellationToken cancellationToken)
    {
        _snapshot = await _client.SaveHeadlessProjectAsync(new HeadlessSaveProjectRequest
        {
            Precondition = CreatePrecondition(),
            ChangeReason = string.IsNullOrWhiteSpace(changeReason) ? "MCP save" : changeReason,
        }, cancellationToken).ConfigureAwait(false);
        DraftStructureJSON draft = Deserialize<DraftStructureJSON>(_snapshot.TimelineJson);
        return ToElement(new
        {
            saved = true,
            projectRoot = _snapshot.ProjectRoot,
            clipCount = draft.Clips.Length,
            revision = _snapshot.Revision,
            snapshotHash = _snapshot.SnapshotHash,
        });
    }

    private JsonElement QueryProjectMode(JsonElement arguments)
    {
        ProjectJSONStructure project = Deserialize<ProjectJSONStructure>(_snapshot.ProjectJson);
        DraftStructureJSON draft = Deserialize<DraftStructureJSON>(_snapshot.TimelineJson);
        List<AssetItem> assets = Deserialize<List<AssetItem>>(_snapshot.AssetsJson);
        return ToElement(ProjectModeEditingService.Query(project, draft, assets, arguments));
    }

    private async ValueTask<JsonElement> ApplyProjectModeEditAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        HeadlessJsonResponse response = await _client.ApplyHeadlessProjectEditAsync(new HeadlessProjectEditRequest
        {
            Precondition = CreatePrecondition(),
            Json = arguments.GetRawText(),
        }, cancellationToken).ConfigureAwait(false);
        _snapshot = response.Snapshot;
        return ParseElement(response.Json);
    }

    private HeadlessMutationPrecondition CreatePrecondition() => new()
    {
        SessionId = _snapshot.SessionId,
        BaseRevision = _snapshot.Revision,
        BaseSnapshotHash = _snapshot.SnapshotHash,
    };

    private static JsonElement Wrap(string propertyName, JsonElement value)
    {
        using var document = JsonDocument.Parse($"{{{JsonSerializer.Serialize(propertyName)}:{value.GetRawText()}}}");
        return document.RootElement.Clone();
    }

    private static JsonElement RequiredProperty(JsonElement arguments, string name)
        => arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out JsonElement value)
            ? value
            : throw new ArgumentException($"Missing '{name}'.", nameof(arguments));

    private static string RequiredString(JsonElement arguments, string name)
        => OptionalString(arguments, name)
            ?? throw new ArgumentException($"Missing or invalid '{name}'.", nameof(arguments));

    private static string? OptionalString(JsonElement arguments, string name)
        => arguments.ValueKind == JsonValueKind.Object &&
           arguments.TryGetProperty(name, out JsonElement value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static uint RequiredUInt32(JsonElement arguments, string name)
        => OptionalUInt32(arguments, name)
            ?? throw new ArgumentException($"Missing or invalid '{name}'.", nameof(arguments));

    private static uint? OptionalUInt32(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out uint number)) return number;
        return value.ValueKind == JsonValueKind.String && uint.TryParse(value.GetString(), out number)
            ? number
            : null;
    }

    private static T Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new JsonException($"Could not deserialize {typeof(T).Name}.");

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement ToElement<T>(T value)
        => JsonSerializer.SerializeToElement(value, JsonOptions);

    public async ValueTask DisposeAsync()
    {
        _operationLock.Dispose();
        if (_ownsSession)
        {
            try
            {
                await _client.CloseProjectAsync(_snapshot.SessionId).ConfigureAwait(false);
            }
            catch
            {
            }
        }
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
