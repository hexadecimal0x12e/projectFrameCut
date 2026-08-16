using Microsoft.Maui.ApplicationModel;
using projectFrameCut.DraftStuff;
using projectFrameCut.IntegratedAPIServer;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.Services;

internal sealed class IntegratedApiBackend(DraftPage page) : IIntegratedApiBackend, IAsyncDisposable
{
    public const string IntegratedClientId = "projectFrameCut-integrated-editor";

    private readonly DraftPage _page = page;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public async ValueTask<JsonElement> ExecuteAsync(
        IntegratedApiOperation operation,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            return await MainThread.InvokeOnMainThreadAsync(
                () => ExecuteOnMainThreadAsync(operation, arguments, cancellationToken));
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async ValueTask<bool> RequestAuthorizationAsync(
        IntegratedApiAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            string message =
                $"An MCP client is requesting access to the current project.\n\n" +
                $"Client: {request.ClientName} {request.ClientVersion}\n" +
                $"Address: {request.RemoteAddress}\n" +
                $"Reason: {request.Reason}\n\n" +
                "Allow this client to read and modify the project for this connection?";
            return await _page.DisplayAlertAsync("MCP access request", message, "Allow", "Deny");
        });
    }

    private async Task<JsonElement> ExecuteOnMainThreadAsync(
        IntegratedApiOperation operation,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool saveAfterOperation = IsMutation(operation);
        object? result;

        switch (operation)
        {
            case IntegratedApiOperation.GetTimelineInfo:
                result = GetTimelineInfo();
                break;
            case IntegratedApiOperation.ListLayers:
                result = GetLayers();
                break;
            case IntegratedApiOperation.ListAvailableEffects:
                result = new { effects = TimelineMcpLiveService.GetAllAvailableEffects().Cast<object>().ToList() };
                break;
            case IntegratedApiOperation.GetEffectInfo:
                result = GetEffectInfo(GetRequiredString(arguments, "effectType"));
                break;
            case IntegratedApiOperation.GetProjectMetadata:
                result = GetProjectMetadata();
                break;
            case IntegratedApiOperation.ListConnectedClients:
                result = new
                {
                    count = 1,
                    clients = new[]
                    {
                        new { clientId = IntegratedClientId, projectName = _page.ProjectName, integrated = true }
                    }
                };
                break;
            case IntegratedApiOperation.GetClientEnvironment:
                EnsureIntegratedClient(arguments);
                result = new
                {
                    clientId = IntegratedClientId,
                    effects = TimelineMcpLiveService.GetAllAvailableEffects().Cast<object>().ToList(),
                    plugins = TimelineMcpLiveService.GetAllAvailablePlugins().Cast<object>().ToList(),
                    textStyles = TimelineMcpLiveService.GetAllAvailableTextStyles().Cast<object>().ToList(),
                };
                break;
            case IntegratedApiOperation.RenderClientPreview:
                EnsureIntegratedClient(arguments);
                result = RenderPreview(arguments);
                break;
            case IntegratedApiOperation.ApplyClientPatch:
                EnsureIntegratedClient(arguments);
                result = ExportClip(TimelineMcpLiveService.ApplyClipPatch(
                    _page,
                    GetRequiredString(arguments, "clipId"),
                    DeserializeRequired<Dictionary<string, object?>>(arguments, "patch")));
                break;
            case IntegratedApiOperation.MoveClientClip:
                EnsureIntegratedClient(arguments);
                result = ExportClip(TimelineMcpLiveService.MoveClip(
                    _page,
                    GetRequiredString(arguments, "clipId"),
                    GetRequiredUInt(arguments, "layerIndex"),
                    GetRequiredUInt(arguments, "startFrame")));
                break;
            case IntegratedApiOperation.ListClips:
                result = new { clips = DraftImportAndExportHelper.ExportFromDraftPage(_page, false).Clips };
                break;
            case IntegratedApiOperation.GetClip:
                result = TimelineMcpLiveService.GetClip(_page, GetRequiredGuid(arguments, "clipId"))
                    ?? throw new KeyNotFoundException($"Clip '{GetRequiredString(arguments, "clipId")}' was not found.");
                break;
            case IntegratedApiOperation.UpsertClip:
                result = ExportClip(TimelineMcpLiveService.ReplaceClip(
                    _page,
                    DeserializeRequired<ClipDraftDTO>(arguments, "clip")));
                break;
            case IntegratedApiOperation.MoveClip:
                result = ExportClip(TimelineMcpLiveService.MoveClip(
                    _page,
                    GetRequiredString(arguments, "clipId"),
                    GetRequiredUInt(arguments, "layerIndex"),
                    GetRequiredUInt(arguments, "startFrame"),
                    TryGetUInt(arguments, "subLayerIndex")));
                break;
            case IntegratedApiOperation.PatchClip:
                result = ExportClip(TimelineMcpLiveService.ApplyClipPatch(
                    _page,
                    GetRequiredString(arguments, "clipId"),
                    DeserializeRequired<Dictionary<string, object?>>(arguments, "patch")));
                break;
            case IntegratedApiOperation.DeleteClip:
                result = new { deleted = TimelineMcpLiveService.DeleteClip(_page, GetRequiredGuid(arguments, "clipId")) };
                break;
            case IntegratedApiOperation.AddEffect:
            {
                var effect = DeserializeRequired<EffectAndMixtureJSONStructure>(arguments, "effect");
                var created = TimelineMcpLiveService.AddEffect(_page, GetRequiredString(arguments, "clipId"), effect);
                result = new
                {
                    created.Id,
                    created.Name,
                    created.TypeName,
                    created.FromPlugin,
                    created.Enabled,
                    created.Index,
                    created.Parameters,
                };
                break;
            }
            case IntegratedApiOperation.RemoveEffect:
                result = new
                {
                    removed = TimelineMcpLiveService.RemoveEffect(
                        _page,
                        GetRequiredString(arguments, "clipId"),
                        GetRequiredString(arguments, "effectKey"))
                };
                break;
            case IntegratedApiOperation.AddEffectBundle:
            {
#pragma warning disable CS0618
                var bundle = DeserializeRequired<EffectBundleJSONStructure>(arguments, "bundle");
                var providers = EffectBindingHelper.MigrateToEffectProviders(null, [bundle]);
#pragma warning restore CS0618
                if (!providers.TryGetValue(bundle.Id, out var provider))
                {
                    throw new InvalidOperationException($"Effect bundle '{bundle.BundleTypeName}' could not be restored.");
                }

                TimelineMcpLiveService.AddEffectBundle(_page, GetRequiredString(arguments, "clipId"), provider);
                result = bundle;
                break;
            }
            case IntegratedApiOperation.RemoveEffectBundle:
                result = new
                {
                    removed = TimelineMcpLiveService.RemoveEffectBundle(
                        _page,
                        GetRequiredString(arguments, "clipId"),
                        GetRequiredGuid(arguments, "bundleId"))
                };
                break;
            case IntegratedApiOperation.SaveProject:
                await _page.Save(false);
                saveAfterOperation = false;
                result = new
                {
                    saved = true,
                    projectRoot = _page.WorkingPath,
                    clipCount = _page.Clips.Count,
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported integrated API operation.");
        }

        if (saveAfterOperation)
        {
            await _page.Save(true);
        }

        return Serialize(result);
    }

    private object GetTimelineInfo()
    {
        var draft = DraftImportAndExportHelper.ExportFromDraftPage(_page, false);
        uint maxFrame = draft.Clips.Max(static clip => clip.StartFrame + clip.Duration);
        uint maxLayer = draft.Clips.Max(static clip => clip.LayerIndex);
        return new
        {
            projectName = _page.ProjectInfo.ProjectName,
            width = _page.ProjectInfo.RelativeWidth,
            height = _page.ProjectInfo.RelativeHeight,
            frameRate = _page.ProjectInfo.TargetFrameRate,
            totalFrames = maxFrame,
            layerCount = draft.Clips.Length == 0 ? 0 : maxLayer + 1,
            clipCount = draft.Clips.Length,
            lastChanged = _page.ProjectInfo.LastChanged,
            savedAt = draft.SavedAt,
        };
    }

    private object GetLayers()
    {
        var clips = DraftImportAndExportHelper.ExportFromDraftPage(_page, false).Clips;
        var layers = clips
            .GroupBy(static clip => clip.LayerIndex)
            .OrderBy(static group => group.Key)
            .Select(static group => new
            {
                layerIndex = group.Key,
                clipCount = group.Count(),
                clips = group.OrderBy(static clip => clip.StartFrame)
                    .Select(static clip => new { id = clip.Id, name = clip.Name, startFrame = clip.StartFrame, duration = clip.Duration })
                    .ToList(),
            })
            .ToList();
        return new { layers };
    }

    private static object GetEffectInfo(string effectType)
    {
        if (!EffectHelper.EffectsProviderEnum.TryGetValue(effectType, out var creator))
        {
            throw new KeyNotFoundException($"Effect type '{effectType}' was not found.");
        }

        var effect = creator().RestoreInstanceWithDefaultType();
        var info = effect.GetInfo();
        return new
        {
            typeName = effectType,
            effect.Name,
            effect.FromPlugin,
            effectType = effect.TypeOfEffect.ToString(),
            description = info.Description,
            isEnabled = effect.Enabled,
            effect.Index,
            info.Parameters,
        };
    }

    private object GetProjectMetadata()
    {
        long fileSize = 0;
        foreach (string fileName in new[] { "project.pjfc", "timeline.json", "assets.json" })
        {
            string path = Path.Combine(_page.WorkingPath, fileName);
            if (File.Exists(path))
            {
                fileSize += new FileInfo(path).Length;
            }
        }

        return new
        {
            projectName = _page.ProjectInfo.ProjectName,
            projectPath = _page.WorkingPath,
            fileSize,
            createdOrModified = _page.ProjectInfo.LastChanged,
            lastSnapshotId = _page.ProjectInfo.LastSnapshotID,
            pluginsUsed = _page.ProjectInfo.PluginUsed,
            normallyExited = _page.ProjectInfo.NormallyExited,
        };
    }

    private object RenderPreview(JsonElement arguments)
    {
        uint frame = GetRequiredUInt(arguments, "frame");
        int width = TryGetInt(arguments, "width") ?? _page.ProjectInfo.RelativeWidth;
        int height = TryGetInt(arguments, "height") ?? _page.ProjectInfo.RelativeHeight;
        string imagePath = _page.previewer.RenderFrame(frame, width, height);
        return new
        {
            frame,
            width,
            height,
            mimeType = "image/png",
            imageBase64 = Convert.ToBase64String(File.ReadAllBytes(imagePath)),
        };
    }

    private ClipDraftDTO ExportClip(ClipElementUI clip)
        => DraftImportAndExportHelper.ExportClipElementFromDraftPage(_page, clip, false);

    private void EnsureIntegratedClient(JsonElement arguments)
    {
        string clientId = GetRequiredString(arguments, "clientId");
        if (!string.Equals(clientId, IntegratedClientId, StringComparison.Ordinal))
        {
            throw new KeyNotFoundException($"Client '{clientId}' is not connected.");
        }
    }

    private JsonElement Serialize(object? value)
        => value is null
            ? JsonDocument.Parse("null").RootElement.Clone()
            : JsonSerializer.SerializeToElement(value, value.GetType(), _jsonOptions);

    private T DeserializeRequired<T>(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var value))
        {
            throw new ArgumentException($"Missing '{propertyName}'.");
        }

        return value.Deserialize<T>(_jsonOptions)
            ?? throw new ArgumentException($"Invalid '{propertyName}'.");
    }

    private static string GetRequiredString(JsonElement arguments, string propertyName)
        => arguments.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? throw new ArgumentException($"Missing '{propertyName}'.")
            : throw new ArgumentException($"Missing '{propertyName}'.");

    private static Guid GetRequiredGuid(JsonElement arguments, string propertyName)
        => Guid.TryParse(GetRequiredString(arguments, propertyName), out var value)
            ? value
            : throw new ArgumentException($"Invalid '{propertyName}'.");

    private static uint GetRequiredUInt(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var value))
        {
            throw new ArgumentException($"Missing '{propertyName}'.");
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out uint number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String && uint.TryParse(value.GetString(), out number))
        {
            return number;
        }

        throw new ArgumentException($"Invalid '{propertyName}'.");
    }

    private static int? TryGetInt(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
            ? number
            : value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)
                ? number
                : null;
    }

    private static uint? TryGetUInt(JsonElement arguments, string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out uint number)
            ? number
            : value.ValueKind == JsonValueKind.String && uint.TryParse(value.GetString(), out number)
                ? number
                : null;
    }

    private static bool IsMutation(IntegratedApiOperation operation)
        => operation is IntegratedApiOperation.ApplyClientPatch
            or IntegratedApiOperation.MoveClientClip
            or IntegratedApiOperation.UpsertClip
            or IntegratedApiOperation.MoveClip
            or IntegratedApiOperation.PatchClip
            or IntegratedApiOperation.DeleteClip
            or IntegratedApiOperation.AddEffect
            or IntegratedApiOperation.RemoveEffect
            or IntegratedApiOperation.AddEffectBundle
            or IntegratedApiOperation.RemoveEffectBundle;

    public ValueTask DisposeAsync()
    {
        _operationLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
