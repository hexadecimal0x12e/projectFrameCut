using System.Text.Json;
using System.Text.Json.Serialization;
using projectFrameCut.McpCore;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;

namespace projectFrameCut.IntegratedAPIServer.MCP;

/// <summary>
/// File-backed implementation used by headless MCP transports. Changes remain
/// in memory until save_project is called.
/// </summary>
internal sealed class HeadlessProjectMcpBackend : IIntegratedApiBackend, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly TimelineProjectWorkspace _workspace;
    private readonly TimelineProjectEditor _editor;

    public HeadlessProjectMcpBackend(string projectRoot)
    {
        _workspace = TimelineProjectWorkspace.Load(projectRoot);
        _editor = new TimelineProjectEditor(_workspace);
    }

    public async ValueTask<JsonElement> ExecuteAsync(
        IntegratedApiOperation operation,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            object result = operation switch
            {
                IntegratedApiOperation.GetTimelineInfo => GetTimelineInfo(),
                IntegratedApiOperation.ListLayers => GetLayers(),
                IntegratedApiOperation.ListAvailableEffects => ProjectModeEditingService.GetAvailableEffects(),
                IntegratedApiOperation.GetEffectInfo => ProjectModeEditingService.GetEffectInfo(RequiredString(arguments, "effectType")),
                IntegratedApiOperation.GetProjectMetadata => GetProjectMetadata(),
                IntegratedApiOperation.ListConnectedClients => new { clients = Array.Empty<object>(), count = 0 },
                IntegratedApiOperation.GetClientEnvironment or
                IntegratedApiOperation.RenderClientPreview or
                IntegratedApiOperation.ApplyClientPatch or
                IntegratedApiOperation.MoveClientClip => throw new InvalidOperationException(
                    "Integrated editor client operations are unavailable in headless MCP mode."),
                IntegratedApiOperation.ListClips => new { clips = _editor.ListClips() },
                IntegratedApiOperation.GetClip => _editor.GetClip(RequiredString(arguments, "clipId")),
                IntegratedApiOperation.UpsertClip => _editor.UpsertClip(
                    RequiredValue<ClipDraftDTO>(arguments, "clip")),
                IntegratedApiOperation.MoveClip => _editor.MoveClip(
                    RequiredString(arguments, "clipId"),
                    RequiredUInt32(arguments, "layerIndex"),
                    RequiredUInt32(arguments, "startFrame"),
                    OptionalUInt32(arguments, "subLayerIndex")),
                IntegratedApiOperation.PatchClip => _editor.PatchClip(
                    RequiredString(arguments, "clipId"),
                    RequiredPatch(arguments, "patch")),
                IntegratedApiOperation.DeleteClip => new
                {
                    deleted = _editor.DeleteClip(RequiredString(arguments, "clipId")),
                },
                IntegratedApiOperation.AddEffect => _editor.AddOrReplaceEffect(
                    RequiredString(arguments, "clipId"),
                    RequiredValue<EffectAndMixtureJSONStructure>(arguments, "effect")),
                IntegratedApiOperation.RemoveEffect => new
                {
                    removed = _editor.RemoveEffect(
                        RequiredString(arguments, "clipId"),
                        RequiredString(arguments, "effectKey")),
                },
                IntegratedApiOperation.AddEffectProvider => _editor.AddOrReplaceEffectProvider(
                    RequiredString(arguments, "clipId"),
                    RequiredValue<EffectProviderJSONStructure>(arguments, "provider")),
                IntegratedApiOperation.RemoveEffectProvider => new
                {
                    removed = _editor.RemoveEffectProvider(
                        RequiredString(arguments, "clipId"),
                        Guid.Parse(RequiredString(arguments, "providerId"))),
                },
                IntegratedApiOperation.SaveProject => SaveProject(OptionalString(arguments, "changeReason")),
                IntegratedApiOperation.ProjectModeQuery => ProjectModeEditingService.Query(
                    _workspace.ProjectInfo,
                    _workspace.Draft,
                    _workspace.Assets,
                    arguments),
                IntegratedApiOperation.ProjectModeEdit => ApplyProjectModeEdit(arguments),
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown integrated API operation."),
            };

            return JsonSerializer.SerializeToElement(result, JsonOptions);
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

    private object ApplyProjectModeEdit(JsonElement arguments)
    {
        string projectJson = JsonSerializer.Serialize(_workspace.ProjectInfo, TimelineProjectWorkspace.JsonOptions);
        string draftJson = JsonSerializer.Serialize(_workspace.Draft, TimelineProjectWorkspace.JsonOptions);
        string assetsJson = JsonSerializer.Serialize(_workspace.Assets, TimelineProjectWorkspace.JsonOptions);
        try
        {
            return ProjectModeEditingService.Edit(_workspace, arguments);
        }
        catch
        {
            _workspace.ReplaceProjectInfo(JsonSerializer.Deserialize<ProjectJSONStructure>(projectJson, TimelineProjectWorkspace.JsonOptions)!);
            _workspace.ReplaceDraft(JsonSerializer.Deserialize<DraftStructureJSON>(draftJson, TimelineProjectWorkspace.JsonOptions)!);
            _workspace.ReplaceAssets(JsonSerializer.Deserialize<List<AssetItem>>(assetsJson, TimelineProjectWorkspace.JsonOptions)!);
            throw;
        }
    }

    private object GetTimelineInfo()
    {
        var clips = _workspace.Draft.Clips;
        uint totalFrames = clips.Length == 0 ? 0 : clips.Max(static clip => clip.StartFrame + clip.Duration);
        uint layerCount = clips.Length == 0 ? 0 : clips.Max(static clip => clip.LayerIndex) + 1;

        return new
        {
            projectName = _workspace.ProjectInfo.ProjectName,
            width = _workspace.ProjectInfo.RelativeWidth,
            height = _workspace.ProjectInfo.RelativeHeight,
            frameRate = _workspace.ProjectInfo.TargetFrameRate,
            totalFrames,
            layerCount,
            clipCount = clips.Length,
            lastChanged = _workspace.ProjectInfo.LastChanged,
            savedAt = _workspace.Draft.SavedAt,
        };
    }

    private object GetLayers()
        => new
        {
            layers = _workspace.Draft.Clips
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
        };

    private object GetProjectMetadata()
    {
        long fileSize = 0;
        foreach (string fileName in new[] { "project.pjfc", "project.json", "timeline.json", "assets.json" })
        {
            string path = Path.Combine(_workspace.ProjectRoot, fileName);
            if (File.Exists(path))
            {
                fileSize += new FileInfo(path).Length;
            }
        }

        return new
        {
            projectName = _workspace.ProjectInfo.ProjectName,
            projectPath = _workspace.ProjectRoot,
            fileSize,
            createdOrModified = _workspace.ProjectInfo.LastChanged,
            lastSnapshotId = _workspace.ProjectInfo.LastSnapshotID,
            pluginsUsed = _workspace.ProjectInfo.PluginUsed ?? [],
            normallyExited = _workspace.ProjectInfo.NormallyExited,
        };
    }

    private object SaveProject(string? changeReason)
    {
        _workspace.Save(string.IsNullOrWhiteSpace(changeReason) ? "MCP save" : changeReason);
        return new
        {
            saved = true,
            projectRoot = _workspace.ProjectRoot,
            clipCount = _editor.ListClips().Count,
        };
    }

    private static string RequiredString(JsonElement arguments, string name)
        => OptionalString(arguments, name)
            ?? throw new ArgumentException($"Missing or invalid '{name}'.", nameof(arguments));

    private static string? OptionalString(JsonElement arguments, string name)
        => arguments.ValueKind == JsonValueKind.Object &&
           arguments.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static uint RequiredUInt32(JsonElement arguments, string name)
        => OptionalUInt32(arguments, name)
            ?? throw new ArgumentException($"Missing or invalid '{name}'.", nameof(arguments));

    private static uint? OptionalUInt32(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out uint number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && uint.TryParse(value.GetString(), out number)
            ? number
            : null;
    }

    private static T RequiredValue<T>(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value))
        {
            throw new ArgumentException($"Missing '{name}'.", nameof(arguments));
        }

        return value.Deserialize<T>(JsonOptions)
            ?? throw new ArgumentException($"Invalid '{name}'.", nameof(arguments));
    }

    private static Dictionary<string, object?> RequiredPatch(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty(name, out var patch) ||
            patch.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"Missing or invalid '{name}'.", nameof(arguments));
        }

        return patch.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => NormalizeValue(property.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    private static object? NormalizeValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt64(out long integer) => integer,
            JsonValueKind.Number => value.GetDouble(),
            _ => value.Clone(),
        };

    public void Dispose() => _operationLock.Dispose();
}
