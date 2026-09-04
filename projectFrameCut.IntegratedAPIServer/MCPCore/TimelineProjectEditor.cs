using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Effect;
using projectFrameCut.Shared;
using System.Text.Json;

namespace projectFrameCut.McpCore;

public sealed class TimelineProjectEditor(TimelineProjectWorkspace workspace)
{
    public TimelineProjectWorkspace Workspace { get; } = workspace;

    public IReadOnlyList<ClipDraftDTO> ListClips()
        => Workspace.Draft.Clips.OrderBy(c => c.LayerIndex).ThenBy(c => c.StartFrame).ThenBy(c => c.SubLayerIndex).ToList();

    public ClipDraftDTO GetClip(string id)
        => FindClip(id) ?? throw new KeyNotFoundException($"Clip '{id}' not found.");

    public ClipDraftDTO UpsertClip(ClipDraftDTO clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        EffectBindingHelper.NormalizeClipProviders(clip);
        var clips = Workspace.Draft.Clips.ToList();
        int index = clips.FindIndex(c => c.Id == clip.Id);
        if (index >= 0)
        {
            clips[index] = clip;
        }
        else
        {
            clips.Add(clip);
        }

        Workspace.Draft.Clips = [.. clips];
        return clip;
    }

    public ClipDraftDTO MoveClip(string id, uint layerIndex, uint startFrame, uint? subLayerIndex = null)
    {
        var clip = GetClip(id);
        clip.LayerIndex = layerIndex;
        clip.StartFrame = startFrame;
        if (subLayerIndex.HasValue)
        {
            clip.SubLayerIndex = subLayerIndex.Value;
        }
        return clip;
    }

    public ClipDraftDTO PatchClip(string id, Dictionary<string, object?> patch)
    {
        var clip = GetClip(id);
        ApplyClipPatch(clip, patch);
        return clip;
    }

    public bool DeleteClip(string id)
    {
        var clips = Workspace.Draft.Clips.ToList();
        int before = clips.Count;
        clips.RemoveAll(c => c.Id == Guid.Parse(id));
        Workspace.Draft.Clips = [.. clips];
        return clips.Count != before;
    }

    public EffectAndMixtureJSONStructure AddOrReplaceEffect(string clipId, EffectAndMixtureJSONStructure effect)
    {
        var clip = GetClip(id: clipId);
        var effects = clip.Effects?.ToList() ?? [];
        if (string.IsNullOrWhiteSpace(effect.Name))
        {
            effect.Name = effect.TypeName;
        }

        var existingIndex = effects.FindIndex(e => string.Equals(e.Name, effect.Name, StringComparison.Ordinal));
        if (existingIndex >= 0)
        {
            effects[existingIndex] = effect;
        }
        else
        {
            effects.Add(effect);
        }

        clip.Effects = effects.ToArray();
        return effect;
    }

    public bool RemoveEffect(string clipId, string effectName)
    {
        var clip = GetClip(clipId);
        if (clip.Effects is null || clip.Effects.Length == 0)
        {
            return false;
        }

        int before = clip.Effects.Length;
        clip.Effects = clip.Effects.Where(e => !string.Equals(e.Name, effectName, StringComparison.Ordinal) && !string.Equals(e.Id, effectName, StringComparison.Ordinal)).ToArray();
        return clip.Effects.Length != before;
    }

    public EffectProviderJSONStructure AddOrReplaceEffectProvider(string clipId, EffectProviderJSONStructure provider)
    {
        var clip = GetClip(clipId);
        var providers = clip.EffectProviders?.ToList() ?? [];
        var existingIndex = providers.FindIndex(p => p.Id == provider.Id);
        if (existingIndex >= 0)
        {
            providers[existingIndex] = provider;
        }
        else
        {
            providers.Add(provider);
        }

        clip.EffectProviders = providers.ToArray();
        return provider;
    }

    public bool RemoveEffectProvider(string clipId, Guid providerId)
    {
        var clip = GetClip(clipId);
        if (clip.EffectProviders is null || clip.EffectProviders.Length == 0)
        {
            return false;
        }

        int before = clip.EffectProviders.Length;
        clip.EffectProviders = clip.EffectProviders.Where(p => p.Id != providerId).ToArray();
        return clip.EffectProviders.Length != before;
    }

    public ClipDraftDTO? FindClip(string id)
        => Workspace.Draft.Clips.FirstOrDefault(c => c.Id == Guid.Parse(id));

    public EffectInfo? GetEffectInfo(string typeName)
    {
        var effect = Workspace.Draft.Clips.SelectMany(c => c.Effects ?? [])
            .FirstOrDefault(e => string.Equals(e.TypeName, typeName, StringComparison.Ordinal));

        return effect is null
            ? null
            : new EffectInfo
            {
                FromPlugin = effect.FromPlugin,
                TypeName = effect.TypeName,
                Name = effect.Name,
                Description = "Effect metadata from project timeline.",
                Parameters = effect.Parameters?.ToDictionary(k => k.Key, k => new EffectParameterInfo
                {
                    Name = k.Key,
                    ParameterType = k.Value?.GetType().FullName ?? "unknown",
                    DefaultValue = k.Value
                }) ?? new Dictionary<string, EffectParameterInfo>(),
                EffectType = effect.IsContinuousEffect
                    ? EffectType.ContinuousEffect
                    : effect.IsVariableArgumentEffect
                        ? EffectType.BindableEffect
                        : EffectType.NormalEffect
            };
    }

    private static void ApplyClipPatch(ClipDraftDTO clip, Dictionary<string, object?> patch)
    {
        foreach (var (key, value) in patch)
        {
            object? normalizedValue = NormalizeJsonValue(value);
            switch (key.Trim().ToLowerInvariant())
            {
                case "name":
                case "displayname":
                    clip.Name = normalizedValue?.ToString() ?? string.Empty;
                    break;
                case "layerindex":
                    clip.LayerIndex = Convert.ToUInt32(normalizedValue);
                    break;
                case "startframe":
                    clip.StartFrame = Convert.ToUInt32(normalizedValue);
                    break;
                case "duration":
                    clip.Duration = Convert.ToUInt32(normalizedValue);
                    break;
                case "frametime":
                    clip.FrameTime = Convert.ToSingle(normalizedValue);
                    break;
                case "secondperfpreratio":
                case "secondperframeratio":
                    clip.SecondPerFrameRatio = Convert.ToSingle(normalizedValue);
                    break;
                case "filepath":
                    clip.FilePath = normalizedValue?.ToString();
                    break;
                case "sourceduration":
                    clip.SourceDuration = normalizedValue is null ? null : Convert.ToInt64(normalizedValue);
                    break;
                case "isinfinitelength":
                    clip.IsInfiniteLength = Convert.ToBoolean(normalizedValue);
                    break;
                case "shoulddisplayinui":
                    clip.ShouldDisplayInUI = Convert.ToBoolean(normalizedValue);
                    break;
                case "targetwidth":
                    clip.TargetWidth = Convert.ToInt32(normalizedValue);
                    break;
                case "targetheight":
                    clip.TargetHeight = Convert.ToInt32(normalizedValue);
                    break;
                case "targetx":
                    clip.TargetX = Convert.ToInt32(normalizedValue);
                    break;
                case "targety":
                    clip.TargetY = Convert.ToInt32(normalizedValue);
                    break;
                case "fromplugin":
                    clip.FromPlugin = normalizedValue?.ToString() ?? string.Empty;
                    break;
                case "typename":
                    clip.TypeName = normalizedValue?.ToString() ?? string.Empty;
                    break;
                case "cliptype":
                    clip.ClipType = Enum.TryParse<ClipMode>(normalizedValue?.ToString(), out var parsedMode)
                        ? parsedMode
                        : clip.ClipType;
                    break;
                case "metadata":
                case "extradata":
                    clip.MetaData = normalizedValue is null
                        ? null
                        : JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(normalizedValue), TimelineProjectWorkspace.JsonOptions);
                    break;
            }
        }
    }

    private static object? NormalizeJsonValue(object? value)
        => value is not JsonElement json
            ? value
            : json.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => json.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when json.TryGetInt64(out long integer) => integer,
                JsonValueKind.Number => json.GetDouble(),
                _ => json.Clone(),
            };
}
