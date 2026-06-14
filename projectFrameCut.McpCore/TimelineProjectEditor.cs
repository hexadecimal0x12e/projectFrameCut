using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Shared;
using System.Text.Json;

namespace projectFrameCut.McpCore;

public sealed class TimelineProjectEditor(TimelineProjectWorkspace workspace)
{
    public TimelineProjectWorkspace Workspace { get; } = workspace;

    public IReadOnlyList<ClipDraftDTO> ListClips()
        => Workspace.Draft.Clips.OfType<ClipDraftDTO>().OrderBy(c => c.LayerIndex).ThenBy(c => c.StartFrame).ThenBy(c => c.SubLayerIndex).ToList();

    public ClipDraftDTO GetClip(string id)
        => FindClip(id) ?? throw new KeyNotFoundException($"Clip '{id}' not found.");

    public ClipDraftDTO UpsertClip(ClipDraftDTO clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var clips = Workspace.Draft.Clips.OfType<ClipDraftDTO>().ToList();
        int index = clips.FindIndex(c => c.Id == clip.Id);
        if (index >= 0)
        {
            clips[index] = clip;
        }
        else
        {
            clips.Add(clip);
        }

        Workspace.Draft.Clips = clips.Cast<object>().ToArray();
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
        var clips = Workspace.Draft.Clips.OfType<ClipDraftDTO>().ToList();
        int before = clips.Count;
        clips.RemoveAll(c => c.Id == Guid.Parse(id));
        Workspace.Draft.Clips = clips.Cast<object>().ToArray();
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

    public EffectBundleJSONStructure AddOrReplaceEffectBundle(string clipId, EffectBundleJSONStructure bundle)
    {
        var clip = GetClip(clipId);
        var bundles = clip.EffectBundles?.ToList() ?? [];
        var existingIndex = bundles.FindIndex(b => b.Id == bundle.Id);
        if (existingIndex >= 0)
        {
            bundles[existingIndex] = bundle;
        }
        else
        {
            bundles.Add(bundle);
        }

        clip.EffectBundles = bundles.ToArray();
        return bundle;
    }

    public bool RemoveEffectBundle(string clipId, Guid bundleId)
    {
        var clip = GetClip(clipId);
        if (clip.EffectBundles is null || clip.EffectBundles.Length == 0)
        {
            return false;
        }

        int before = clip.EffectBundles.Length;
        clip.EffectBundles = clip.EffectBundles.Where(b => b.Id != bundleId).ToArray();
        return clip.EffectBundles.Length != before;
    }

    public ClipDraftDTO? FindClip(string id)
        => Workspace.Draft.Clips.OfType<ClipDraftDTO>().FirstOrDefault(c => c.Id == Guid.Parse(id));

    public EffectInfo? GetEffectInfo(string typeName)
    {
        var effect = Workspace.Draft.Clips.OfType<ClipDraftDTO>()
            .SelectMany(c => c.Effects ?? [])
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
            switch (key.Trim().ToLowerInvariant())
            {
                case "name":
                case "displayname":
                    clip.Name = value?.ToString() ?? string.Empty;
                    break;
                case "layerindex":
                    clip.LayerIndex = Convert.ToUInt32(value);
                    break;
                case "startframe":
                    clip.StartFrame = Convert.ToUInt32(value);
                    break;
                case "duration":
                    clip.Duration = Convert.ToUInt32(value);
                    break;
                case "frametime":
                    clip.FrameTime = Convert.ToSingle(value);
                    break;
                case "secondperfpreratio":
                case "secondperframeratio":
                    clip.SecondPerFrameRatio = Convert.ToSingle(value);
                    break;
                case "filepath":
                    clip.FilePath = value?.ToString();
                    break;
                case "sourceduration":
                    clip.SourceDuration = value is null ? null : Convert.ToInt64(value);
                    break;
                case "isinfinitelength":
                    clip.IsInfiniteLength = Convert.ToBoolean(value);
                    break;
                case "shoulddisplayinui":
                    clip.ShouldDisplayInUI = Convert.ToBoolean(value);
                    break;
                case "targetwidth":
                    clip.TargetWidth = Convert.ToInt32(value);
                    break;
                case "targetheight":
                    clip.TargetHeight = Convert.ToInt32(value);
                    break;
                case "targetx":
                    clip.TargetX = Convert.ToInt32(value);
                    break;
                case "targety":
                    clip.TargetY = Convert.ToInt32(value);
                    break;
                case "fromplugin":
                    clip.FromPlugin = value?.ToString() ?? string.Empty;
                    break;
                case "typename":
                    clip.TypeName = value?.ToString() ?? string.Empty;
                    break;
                case "cliptype":
                    clip.ClipType = value is JsonElement je && je.ValueKind == JsonValueKind.Number
                        ? (ClipMode)je.GetInt32()
                        : Enum.TryParse<ClipMode>(value?.ToString(), out var parsedMode) ? parsedMode : clip.ClipType;
                    break;
                case "metadata":
                case "extradata":
                    clip.MetaData = value is null
                        ? null
                        : JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(value), TimelineProjectWorkspace.JsonOptions);
                    break;
            }
        }
    }
}
