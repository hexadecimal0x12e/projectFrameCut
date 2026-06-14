using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Project;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;

namespace projectFrameCut.Services;

public static class TimelineMcpLiveService
{
    public static IReadOnlyList<ClipDraftDTO> ListClips(DraftPage page)
        => DraftImportAndExportHelper.ExportFromDraftPage(page, false).Clips.OfType<ClipDraftDTO>().OrderBy(c => c.LayerIndex).ThenBy(c => c.StartFrame).ToList();

    public static ClipDraftDTO? GetClip(DraftPage page, string id)
        => Guid.TryParse(id, out var guid) && page.Clips.TryGetValue(guid, out var clip) ? DraftImportAndExportHelper.ExportClipElementFromDraftPage(page, clip, false) : null;

    public static ClipElementUI ReplaceClip(DraftPage page, ClipDraftDTO dto)
    {
        var element = DraftImportAndExportHelper.ConvertToElement(dto);
        UpsertClipElement(page, element);
        return element;
    }

    public static ClipElementUI MoveClip(DraftPage page, string clipId, uint layerIndex, uint startFrame)
    {
        if (!Guid.TryParse(clipId, out var clipGuid) || !page.Clips.TryGetValue(clipGuid, out var clip))
        {
            throw new KeyNotFoundException($"Clip '{clipId}' not found.");
        }

        var targetTrack = (int)layerIndex;
        if (!page.Tracks.ContainsKey(targetTrack))
        {
            page.AddATrack(targetTrack);
        }

        if (clip.origTrack.HasValue && page.Tracks.TryGetValue(clip.origTrack.Value, out var oldTrack))
        {
            oldTrack.Children.Remove(clip.Clip);
        }

        clip.origTrack = targetTrack;
        clip.SubLayerIndex = targetTrack;
        clip.Clip.TranslationX = page.FrameToPixel(startFrame);
        clip.origX = clip.Clip.TranslationX;
        page.AddAClip(clip);
        page.RegisterClip(clip, true);
        return clip;
    }

    public static ClipElementUI ApplyClipPatch(DraftPage page, string clipId, Dictionary<string, object?> patch)
    {
        if (!Guid.TryParse(clipId, out var clipGuid) || !page.Clips.TryGetValue(clipGuid, out var clip))
        {
            throw new KeyNotFoundException($"Clip '{clipId}' not found.");
        }

        var dto = DraftImportAndExportHelper.ExportClipElementFromDraftPage(page, clip, false);
        foreach (var kv in patch)
        {
            switch (kv.Key.Trim().ToLowerInvariant())
            {
                case "name":
                case "displayname":
                    dto.Name = kv.Value?.ToString() ?? dto.Name;
                    break;
                case "layerindex":
                    dto.LayerIndex = Convert.ToUInt32(kv.Value);
                    break;
                case "startframe":
                    dto.StartFrame = Convert.ToUInt32(kv.Value);
                    break;
                case "duration":
                    dto.Duration = Convert.ToUInt32(kv.Value);
                    break;
                case "filepath":
                    dto.FilePath = kv.Value?.ToString();
                    break;
                case "shoulddisplayinui":
                    dto.ShouldDisplayInUI = Convert.ToBoolean(kv.Value);
                    break;
                case "targetwidth":
                    dto.TargetWidth = Convert.ToInt32(kv.Value);
                    break;
                case "targetheight":
                    dto.TargetHeight = Convert.ToInt32(kv.Value);
                    break;
                case "targetx":
                    dto.TargetX = Convert.ToInt32(kv.Value);
                    break;
                case "targety":
                    dto.TargetY = Convert.ToInt32(kv.Value);
                    break;
                case "extradata":
                case "metadata":
                    dto.MetaData = kv.Value as Dictionary<string, object> ?? dto.MetaData;
                    break;
            }
        }

        return ReplaceClip(page, dto);
    }

    public static IEffect AddEffect(DraftPage page, string clipId, EffectAndMixtureJSONStructure effect)
    {
        if (!Guid.TryParse(clipId, out var clipGuid) || !page.Clips.TryGetValue(clipGuid, out var clip))
        {
            throw new KeyNotFoundException($"Clip '{clipId}' not found.");
        }

        clip.Effects ??= new Dictionary<string, IEffect>();

        IEffect? created = null;
        if (effect.FromPlugin == InternalPluginBase.InternalPluginBaseID)
        {
            created = EffectHelper.EffectsEnum.TryGetValue(effect.TypeName, out var creator) ? creator() : null;
        }
        else
        {
            created = PluginManager.LoadedPlugins.TryGetValue(effect.FromPlugin, out var plugin)
                ? plugin.EffectProvider.TryGetValue(effect.TypeName, out var creator) ? creator() : null
                : null;
        }

        if (created is null)
        {
            throw new InvalidOperationException($"Failed to create effect '{effect.TypeName}'.");
        }

        created.Name = string.IsNullOrWhiteSpace(effect.Name) ? effect.TypeName : effect.Name;
        created.Enabled = effect.Enabled;
        created.Index = effect.Index;

        if (clip.Effects.ContainsKey(created.Name))
        {
            clip.Effects[created.Name] = created;
        }
        else
        {
            clip.Effects.Add(created.Name, created);
        }

        page.RefreshPropertyPanel(clip);
        return created;
    }

    public static bool RemoveEffect(DraftPage page, string clipId, string effectKey)
    {
        if (!Guid.TryParse(clipId, out var clipGuid) || !page.Clips.TryGetValue(clipGuid, out var clip) || clip.Effects is null)
        {
            return false;
        }

        bool removed = clip.Effects.Remove(effectKey);
        if (removed)
        {
            page.RefreshPropertyPanel(clip);
        }

        return removed;
    }

    public static IEffectBundle AddEffectBundle(DraftPage page, string clipId, IEffectBundle bundle)
    {
        if (!Guid.TryParse(clipId, out var clipGuid) || !page.Clips.TryGetValue(clipGuid, out var clip))
        {
            throw new KeyNotFoundException($"Clip '{clipId}' not found.");
        }

        clip.EffectBundles ??= new Dictionary<Guid, IEffectBundle>();
        clip.EffectBundles[bundle.Id] = bundle;
        ClipInfoBuilder.RebuildAllEffects(clip);
        page.RefreshPropertyPanel(clip);
        return bundle;
    }

    public static bool RemoveEffectBundle(DraftPage page, string clipId, Guid bundleId)
    {
        if (!Guid.TryParse(clipId, out var clipGuid) || !page.Clips.TryGetValue(clipGuid, out var clip) || clip.EffectBundles is null)
        {
            return false;
        }

        bool removed = clip.EffectBundles.Remove(bundleId);
        if (removed)
        {
            ClipInfoBuilder.RebuildAllEffects(clip);
            page.RefreshPropertyPanel(clip);
        }

        return removed;
    }

    private static void UpsertClipElement(DraftPage page, ClipElementUI element)
    {
        if (element.origTrack is null)
        {
            throw new InvalidOperationException("Clip must have a target track.");
        }

        if (!page.Tracks.ContainsKey(element.origTrack.Value))
        {
            page.AddATrack(element.origTrack.Value);
        }

        if (page.Clips.TryGetValue(element.Id, out var existing) && existing.origTrack is not null && page.Tracks.TryGetValue(existing.origTrack.Value, out var oldTrack))
        {
            oldTrack.Children.Remove(existing.Clip);
        }

        page.Clips[element.Id] = element;
        page.RegisterClip(element, true);
        page.AddAClip(element);
    }
}
