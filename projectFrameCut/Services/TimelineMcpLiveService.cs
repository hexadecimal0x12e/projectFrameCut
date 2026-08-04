using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationAPIBase.Project;
using projectFrameCut.ApplicationAPIBase.Text;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationAPIBase.Views.TabbedView;
using projectFrameCut.Asset;
using projectFrameCut.DraftStuff;
using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Services;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Shared;
using System.Collections;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace projectFrameCut.Services;

public static class TimelineMcpLiveService
{
    public static DraftStructureJSON ToDraftDTO(DraftPage page)
        => DraftImportAndExportHelper.ExportFromDraftPage(page, false);

    public static IEnumerable ListClips(DraftPage page)
        => DraftImportAndExportHelper.ExportFromDraftPage(page, false).Clips.OrderBy(c => c.LayerIndex).ThenBy(c => c.StartFrame).Select(c => new { id = c.Id, displayName = c.Name, type = c.ClipType.ToString() });

    public static ClipDraftDTO? GetClip(DraftPage page, Guid? id)
        => id.HasValue && page.Clips.TryGetValue(id.Value, out var clip) ? DraftImportAndExportHelper.ExportClipElementFromDraftPage(page, clip, false) : null;

    public static ClipElementUI ReplaceClip(DraftPage page, ClipDraftDTO dto)
    {
        var element = DraftImportAndExportHelper.ConvertToElement(dto);
        UpsertClipElement(page, element);
        return element;
    }

    public static IEnumerable GetAllAvailableEffects()
    {
        var effects = EffectServices.GetAvailableEffectProviders();
        var locNames = EffectServices.GetLocalizedEffectNames();
        return effects.Select(c => c.Value()).Select((e) => new { type = e.TypeName, localizedDisplayName = locNames.TryGetValue(e.TypeName, out var name) ? name : e.TypeName, effectTarget = e.Target, typeOfEffect = e.TypeOfEffect, @params = e.Fields.Keys.ToArray(), fromPlugin = e.FromPlugin });
    }

    public static IEnumerable GetAllAvailablePlugins()
    {
        return PluginManager.LoadedPlugins.Values.Select(p => new { id = p.PluginID, name = p.Name, displayName = p.ReadLocalizationItem("_PluginBase_Name_", p.Name), provides = p is IApplicationPluginBase ab ? IApplicationPluginBase.GetWhatProvided(ab) : PluginMetadata.GetWhatProvided(p) });
    }

    public static IEnumerable GetAllAvailableTextStyles()
    {
        return PluginManager.LoadedPlugins.Values.OfType<IApplicationPluginBase>().SelectMany(c => c.TextClipStyleProvider).Select(c => c.Value()).Select(c => new { id = c.TypeName, fromPlugin = c.FromPlugin, typeName = c.TypeName, displayName = PluginManager.GetLocalizationItem("DisplayName_TextStyle_" + c.TypeName, c.TypeName), settableFields = c.SettableFields });    
    }

    public static IEnumerable<AssetItem> GetAllAvailableAssets(DraftPage? page, bool includeDraftWide, bool includeGlobal, bool includeRemote, bool searchWithRegex = false, string filter = "")
    {
        List<AssetItem> assets = new();
        if (includeDraftWide && page is not null) assets.AddRange(page.Assets.Values);
        if (includeGlobal) assets.AddRange(Asset.AssetDatabase.Assets.Values);
        if (!string.IsNullOrEmpty(filter))
        {
            if (searchWithRegex)
            {
                var rgx = new Regex(filter);
                assets = assets.Where(a => rgx.IsMatch(a.Name ?? "")).ToList();
            }
            else
            {
                assets = assets.Where(a => a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            }
        }
        return assets;
    }

    public static IEnumerable GetPropertyPanelViewTabs(DraftPage? page)
    {
        if (page?.infoBuilder?.CurrentContent is not TabbedView tv) throw new InvalidOperationException("No Tab view in property panel.");
        return tv.TabItems.Select(t => new { tag = t.Tag, header = t.Header, isSelected = t.IsSelected });
    }

    public static bool SetPropertyPanelViewTabs(DraftPage? page, string tag)
    {
        if (page?.infoBuilder?.CurrentContent is not TabbedView tv) throw new InvalidOperationException("No Tab view in property panel.");
        tv.SelectByTag(tag);
        return tv.SelectedItem.Tag == tag;
    }

    public static string GetPropertyPanelViewTree(DraftPage? page)
    {
        if (page?.infoBuilder?.CurrentContent is null) throw new InvalidOperationException("No content in property panel.");
        return new ApplicationAPIBase.Helpers.ControlTreeHelper(page.infoBuilder.CurrentContent).DumpTree();
    }


    public static IEnumerable GetPropertyPanelProperties(DraftPage? page)
    {
        if (page?.infoBuilder?.CurrentContent is not TabbedView tv) throw new InvalidOperationException("No content in property panel.");
        if (tv?.DisplayingContent.BindingContext is not PropertyPanelBuilder ppb) throw new InvalidOperationException("No PropertyPanelBuilder available in the view.");
        return ppb.Properties;
    }

    internal static IEnumerable SetPropertyPanelProperties(DraftPage page, string keyToModify, object value)
    {
        if (page?.infoBuilder?.CurrentContent is not TabbedView tv) throw new InvalidOperationException("No content in property panel.");
        if (tv?.DisplayingContent.BindingContext is not PropertyPanelBuilder ppb) throw new InvalidOperationException("No PropertyPanelBuilder available in the view.");
        if (!ppb.Properties.TryGetValue(keyToModify, out var currentValue) || currentValue is null)
            throw new KeyNotFoundException($"Property '{keyToModify}' not found.");
        var oldType = currentValue.GetType();
        try
        {
            var old = ppb.Properties.ToDictionary(C => C.Key, c => c.Value);
            old[keyToModify] = ConvertPropertyValue(value, oldType) ?? throw new InvalidOperationException($"Failed to convert value to type '{oldType}'.");
            return ppb.WithProperties(old).Properties;

        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to set property '{keyToModify}' of type {oldType}: {Environment.NewLine}{ex}{Environment.NewLine}Try again with a proper value.", ex);
        }
    }

    private static object? ConvertPropertyValue(object value, Type targetType)
    {
        var nonNullableTargetType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (value is JsonElement jsonElement)
        {
            return jsonElement.ValueKind == JsonValueKind.Null
                ? null
                : JsonSerializer.Deserialize(jsonElement.GetRawText(), nonNullableTargetType);
        }

        if (nonNullableTargetType.IsInstanceOfType(value))
        {
            return value;
        }

        return Convert.ChangeType(value, nonNullableTargetType);
    }

    internal static IEnumerable RemovePropertyPanelProperties(DraftPage page, string keyToModify)
    {
        if (page?.infoBuilder?.CurrentContent is not TabbedView tv) throw new InvalidOperationException("No content in property panel.");
        if (tv?.DisplayingContent.BindingContext is not PropertyPanelBuilder ppb) throw new InvalidOperationException("No content in property panel.");
        return ppb.WithProperties(ppb.Properties.Where(c => c.Key != keyToModify).ToDictionary(c => c.Key, c => c.Value)).Properties;
    }


    internal static async Task SelectAClip(DraftPage page, Guid id)
    {
        await page.SelectAClip(id);
    }

    internal static async Task AddFromAsset(DraftPage page, string assetId, int startPosition, int track)
    {
        if (page.Assets.TryGetValue(assetId, out var value) || AssetDatabase.Assets.TryGetValue(assetId, out value))
        {
            var elem = page.CreateFromAsset(value, track, assetId, null, startPosition);
            await page.Dispatcher.DispatchAsync(() =>
            {
                page.RegisterClip(elem, true);
                page.AddAClip(elem);
            });
        }
        else
        {
            throw new KeyNotFoundException($"Cannot find asset with id {assetId} either in draft wide, or in global wide.");
        }
    }

    internal static ITextClipStyleProvider? ResolveTextStyleProvider(string styleId)
    {
        var pvd = PluginManager.LoadedPlugins.Values
            .OfType<IApplicationPluginBase>()
            .SelectMany(c => c.TextClipStyleProvider)
            .FirstOrDefault(C => C.Key == styleId);
        return pvd.Value?.Invoke();
    }

    internal static void ApplyTextStyleFields(ITextClipStyleProvider provider, Dictionary<string, object>? fields, List<string>? resultLog = null)
    {
        if (fields is null || fields.Count == 0) return;
        if (provider.SettableFields is null || provider.SettableFields.Count == 0) return;

        foreach (var kv in fields)
        {
            if (string.IsNullOrWhiteSpace(kv.Key)) continue;
            if (!provider.SettableFields.TryGetValue(kv.Key, out var fieldDef))
            {
                resultLog?.Add($"Warning: Field '{kv.Key}' not found on text style '{provider.TypeName}'. Available: {string.Join(", ", provider.SettableFields.Keys)}");
                continue;
            }

            if (provider.HandleSettableFieldsChange(fieldDef, kv.Value, out var feedback))
            {
                resultLog?.Add($"{kv.Key} = {kv.Value}");
            }
            else
            {
                resultLog?.Add($"Warning: Failed to set field '{kv.Key}' on text style '{provider.TypeName}': {feedback}");
            }
        }
    }

    internal static TextEntry[] BuildTextEntriesWithFontFallback(ITextClipStyleProvider provider)
    {
        var entries = provider.BuildEntries();
        var textLang = TextHelper.DetectTextLanguage(provider.BasicText);
        if (textLang == TextLanguage.English) return entries;

        var fontOverride = textLang switch
        {
            TextLanguage.Chinese => Localized._LocaleId_ == "zh-TW" ? "Noto Sans TC Regular" : "Noto Sans SC Regular",
            TextLanguage.Japanese => "Noto Sans JP Regular",
            TextLanguage.Korean => "Noto Sans KR Regular",
            TextLanguage.Arabic => "HarmonyOS Sans Naskh Arabic Medium",
            _ => "Noto Sans"
        };
        return entries
            .Select(e => e.FontName == "Arial" ? e with { FontName = fontOverride } : e)
            .ToArray();
    }

    internal static async Task AddAText(DraftPage page, string styleId, string text, int startPosition, int track)
    {
        await AddAText(page, styleId, text, startPosition, track, null);
    }

    internal static async Task AddAText(DraftPage page, string styleId, string text, int startPosition, int track, Dictionary<string, object>? fields)
    {
        var providerItem = ResolveTextStyleProvider(styleId);
        if (providerItem is null)
        {
            throw new KeyNotFoundException($"Cannot find text style provider with id {styleId}.");
        }

        var provider = TextStyleServices.RestoreTextStyleProvider(providerItem.FromPlugin, providerItem.TypeName, providerItem.Parameters) ?? providerItem;
        provider.Parameters = new Dictionary<string, string>(providerItem.Parameters);
        provider.BasicText = text;

        ApplyTextStyleFields(provider, fields);

        var entries = BuildTextEntriesWithFontFallback(provider);

        // 必须在 UI 线程上创建和添加 Clip，CreateAndAddClip 内部已调用 RegisterClip + AddAClip
        ClipElementUI? element = null;
        await page.Dispatcher.DispatchAsync(() =>
        {
            element = page.CreateAndAddClip(
                startX: startPosition,
                width: page.FrameToPixel(SettingsManager.GetSettingAs<uint>("Edit_DefaultInfLengthClipLength", 300, 300)),
                trackIndex: track,
                id: null,
                labelText: text,
                background: new SolidColorBrush(Colors.MediumPurple),
                resolveOverlap: true,
                relativeStart: 0,
                maxFrames: 0
            );

            element.ClipType = ClipMode.TextClip;
            element.FromPlugin = "projectFrameCut.Render.Plugins.InternalPluginBase";
            element.isInfiniteLength = true;
            element.maxFrameCount = 0;
            element.ExtraData = new();
            element.ExtraData["TextEntries"] = entries.ToList();
            element.ExtraData["TextStyleProvider_FromPlugin"] = provider.FromPlugin;
            element.ExtraData["TextStyleProvider_TypeName"] = provider.TypeName;
            element.ExtraData["TextStyleProvider_Parameters"] = new Dictionary<string, string>(provider.Parameters);
        });
    }

    /// <summary>
    /// 同步创建并添加文本 Clip。调用方必须已在 UI 线程上。
    /// </summary>
    internal static ClipElementUI AddTextClipToPage(DraftPage page, string styleId, string text, int startPosition, int track, Dictionary<string, object>? fields = null)
    {
        var providerItem = ResolveTextStyleProvider(styleId);
        if (providerItem is null)
        {
            throw new KeyNotFoundException($"Cannot find text style provider with id {styleId}.");
        }

        var provider = TextStyleServices.RestoreTextStyleProvider(providerItem.FromPlugin, providerItem.TypeName, providerItem.Parameters) ?? providerItem;
        provider.Parameters = new Dictionary<string, string>(providerItem.Parameters);
        provider.BasicText = text;

        ApplyTextStyleFields(provider, fields);

        var entries = BuildTextEntriesWithFontFallback(provider);

        var element = page.CreateAndAddClip(
            startX: startPosition,
            width: page.FrameToPixel(SettingsManager.GetSettingAs<uint>("Edit_DefaultInfLengthClipLength", 300, 300)),
            trackIndex: track,
            id: null,
            labelText: text,
            background: new SolidColorBrush(Colors.MediumPurple),
            resolveOverlap: true,
            relativeStart: 0,
            maxFrames: 0
        );

        element.ClipType = ClipMode.TextClip;
        element.FromPlugin = "projectFrameCut.Render.Plugins.InternalPluginBase";
        element.isInfiniteLength = true;
        element.maxFrameCount = 0;
        element.ExtraData = new();
        element.ExtraData["TextEntries"] = entries.ToList();
        element.ExtraData["TextStyleProvider_FromPlugin"] = provider.FromPlugin;
        element.ExtraData["TextStyleProvider_TypeName"] = provider.TypeName;
        element.ExtraData["TextStyleProvider_Parameters"] = new Dictionary<string, string>(provider.Parameters);

        return element;
    }

    internal static ITextClipStyleProvider? RestoreTextStyleProviderFromClip(ClipElementUI clip)
    {
        if (clip.ExtraData is null) return null;

        string? ReadStringValue(object? raw)
        {
            if (raw is string s) return s;
            if (raw is JsonElement elem && elem.ValueKind == JsonValueKind.String) return elem.GetString();
            return raw?.ToString();
        }

        Dictionary<string, string>? ReadParameters(object? raw)
        {
            if (raw is Dictionary<string, string> dict) return new Dictionary<string, string>(dict);
            if (raw is Dictionary<string, object> objDict)
                return objDict.ToDictionary(k => k.Key, v => v.Value?.ToString() ?? string.Empty);
            if (raw is JsonElement elem)
            {
                try { return JsonSerializer.Deserialize<Dictionary<string, string>>(elem); }
                catch { return null; }
            }
            if (raw is string json && !string.IsNullOrWhiteSpace(json))
            {
                try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
                catch { return null; }
            }
            return null;
        }

        var fromPlugin = clip.ExtraData.TryGetValue("TextStyleProvider_FromPlugin", out var fromObj) ? ReadStringValue(fromObj) : null;
        var typeName = clip.ExtraData.TryGetValue("TextStyleProvider_TypeName", out var typeObj) ? ReadStringValue(typeObj) : null;
        var parameters = clip.ExtraData.TryGetValue("TextStyleProvider_Parameters", out var paramsObj) ? ReadParameters(paramsObj) : null;

        if (string.IsNullOrWhiteSpace(fromPlugin) || string.IsNullOrWhiteSpace(typeName))
            return null;

        var provider = TextStyleServices.RestoreTextStyleProvider(fromPlugin, typeName, parameters);
        if (provider is null) return null;
        if (parameters is not null)
            provider.Parameters = new Dictionary<string, string>(parameters);

        return provider;
    }

    internal static List<string> SetTextClipStyleFields(DraftPage page, Guid clipId, Dictionary<string, object> fields)
    {
        if (!page.Clips.TryGetValue(clipId, out var clip))
        {
            throw new KeyNotFoundException($"Clip '{clipId}' not found.");
        }

        if (clip.ClipType != ClipMode.TextClip)
        {
            throw new InvalidOperationException($"Clip '{clipId}' is not a text clip.");
        }

        var provider = RestoreTextStyleProviderFromClip(clip);
        if (provider is null)
        {
            throw new InvalidOperationException($"Clip '{clipId}' does not have a restorable text style provider.");
        }

        var resultLog = new List<string>();
        ApplyTextStyleFields(provider, fields, resultLog);

        var entries = BuildTextEntriesWithFontFallback(provider);

        clip.ExtraData ??= new Dictionary<string, object>();
        clip.ExtraData["TextEntries"] = entries.ToList();
        clip.ExtraData["TextStyleProvider_Parameters"] = new Dictionary<string, string>(provider.Parameters);
        clip.IsMoveable = true;
        clip.IsHorizontalResizable = provider.IsHorizontalResizable;
        clip.IsVerticalResizable = provider.IsVerticalResizable;
        clip.CanSnapWhileResizing = provider.CanSnapWhileResizing;

        return resultLog;
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
        page.Dispatcher.Dispatch(() =>
        {
            page.RegisterClip(clip, true);
            page.AddAClip(clip);
        });
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
            created = EffectHelper.EffectsProviderEnum.TryGetValue(effect.TypeName, out var creator) ? creator().RestoreInstanceWithDefaultType() : null;
        }
        else
        {
            created = PluginManager.LoadedPlugins.TryGetValue(effect.FromPlugin, out var plugin)
                ? plugin.EffectProviderProvider.TryGetValue(effect.TypeName, out var creator) ? creator().RestoreInstanceWithDefaultType() : null
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

    public static IEffectProvider AddEffectBundle(DraftPage page, string clipId, IEffectProvider bundle)
    {
        if (!Guid.TryParse(clipId, out var clipGuid) || !page.Clips.TryGetValue(clipGuid, out var clip))
        {
            throw new KeyNotFoundException($"Clip '{clipId}' not found.");
        }

        clip.EffectProviders ??= new Dictionary<Guid, IEffectProvider>();
        clip.EffectProviders[bundle.Id] = bundle;
        ClipInfoBuilder.RebuildAllEffects(clip);
        page.RefreshPropertyPanel(clip);
        return bundle;
    }

    public static bool RemoveEffectBundle(DraftPage page, string clipId, Guid bundleId)
    {
        if (!Guid.TryParse(clipId, out var clipGuid) || !page.Clips.TryGetValue(clipGuid, out var clip) || clip.EffectProviders is null)
        {
            return false;
        }

        bool removed = clip.EffectProviders.Remove(bundleId);
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
