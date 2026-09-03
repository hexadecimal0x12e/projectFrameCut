using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.McpCore;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Shared;
using BindingDiagnostic = projectFrameCut.Render.Effect.EffectBindingHelper.BindingDiagnostic;

namespace projectFrameCut.IntegratedAPIServer.MCP;

internal static class ProjectModeEditingService
{
    internal const string InternalPluginId = "projectFrameCut.Render.Plugins.InternalPluginBase";
    private const string VectorComponentsKey = "VectorCanvas.Components";
    private const string TextEntriesKey = "TextEntries";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };
    private static readonly JsonSerializerOptions ComponentJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = null,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static readonly string[] BuiltInVectorTypes =
    [
        "Rectangle", "RoundedRectangle", "Ellipse", "Line", "CubicBezier",
        "QuadraticBezier", "Arc", "Polygon", "Polyline", "Text", "ComponentGroup",
    ];

    public static object Query(
        ProjectJSONStructure project,
        DraftStructureJSON draft,
        IReadOnlyList<AssetItem> assets,
        JsonElement arguments)
    {
        string kind = RequiredString(arguments, "kind");
        return kind switch
        {
            "projectAssets" => QueryAssets(assets, arguments),
            "vectorComponentTypes" => new { types = BuiltInVectorTypes, count = BuiltInVectorTypes.Length },
            "vectorComponents" => QueryVectorComponents(FindClip(draft, arguments), arguments),
            "clipEffectProviders" => QueryProviders(FindClip(draft, arguments)),
            "validateEffectProviderGraph" => ValidateProviderGraph(FindClip(draft, arguments)),
            _ => throw new ArgumentException($"Unknown ProjectMode query '{kind}'.", nameof(arguments)),
        };
    }

    public static object Edit(TimelineProjectWorkspace workspace, JsonElement arguments)
    {
        string kind = RequiredString(arguments, "kind");
        return kind switch
        {
            "addClipFromAsset" => AddClipFromAsset(workspace, arguments),
            "addTextClip" => AddTextClip(workspace, arguments),
            "setTextEntries" => SetTextEntries(workspace, arguments),
            "addSolidColorClip" => AddSolidColorClip(workspace, arguments),
            "setSolidColor" => SetSolidColor(workspace, arguments),
            "addVectorCanvasClip" => AddVectorCanvasClip(workspace, arguments),
            "addVectorComponent" => AddVectorComponent(workspace, arguments),
            "updateVectorComponent" => UpdateVectorComponent(workspace, arguments),
            "removeVectorComponent" => RemoveVectorComponent(workspace, arguments),
            "replaceVectorComponents" => ReplaceVectorComponents(workspace, arguments),
            "setVectorComponentKeyframes" => SetVectorComponentKeyframes(workspace, arguments),
            "addEffectProvider" => AddEffectProvider(workspace, arguments),
            "updateEffectProvider" => UpdateEffectProvider(workspace, arguments),
            "removeEffectProvider" => RemoveEffectProvider(workspace, arguments),
            "connectEffectProviderInput" => ConnectEffectProviderInput(workspace, arguments),
            "setEffectProviderOutput" => SetEffectProviderOutput(workspace, arguments),
            "bindEffectProviderField" => BindEffectProviderField(workspace, arguments),
            "unbindEffectProviderField" => UnbindEffectProviderField(workspace, arguments),
            "replaceEffectProviderGraph" => ReplaceEffectProviderGraph(workspace, arguments),
            "setColorAdjustment" => SetColorAdjustment(workspace, arguments),
            "setClipSpeed" => SetClipSpeed(workspace, arguments),
            "setLinearEffectAnimation" => SetLinearEffectAnimation(workspace, arguments),
            "setPositionKeyframes" => SetPositionKeyframes(workspace, arguments),
            "setCropKeyframes" => SetCropKeyframes(workspace, arguments),
            _ => throw new ArgumentException($"Unknown ProjectMode edit '{kind}'.", nameof(arguments)),
        };
    }

    public static object GetAvailableEffects()
    {
        var effects = EnumerateProviderFactories()
            .Select(static item =>
            {
                try { return DescribeProvider(item.TypeName, item.Factory()); }
                catch (Exception ex)
                {
                    return (object)new
                    {
                        typeName = item.TypeName,
                        available = false,
                        error = ex.Message,
                    };
                }
            })
            .ToArray();
        return new { effects, count = effects.Length };
    }

    public static object GetEffectInfo(string typeName)
    {
        var factory = ResolveProviderFactory(typeName);
        return DescribeProvider(typeName, factory());
    }

    private static object QueryAssets(IReadOnlyList<AssetItem> assets, JsonElement arguments)
    {
        string? filter = OptionalString(arguments, "filter");
        string? type = OptionalString(arguments, "assetType");
        IEnumerable<AssetItem> result = assets;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            result = result.Where(asset =>
                (asset.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (asset.AssetId?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (asset.Path?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        if (!string.IsNullOrWhiteSpace(type) && Enum.TryParse(type, true, out AssetType assetType))
            result = result.Where(asset => asset.AssetType == assetType);
        AssetItem[] values = result.ToArray();
        return new { assets = values, count = values.Length };
    }

    private static object QueryVectorComponents(ClipDraftDTO clip, JsonElement arguments)
    {
        EnsureClipType(clip, ClipMode.VectorCanvasClip);
        JsonElement[] components = ReadVectorComponents(clip).Select(static value => value.Clone()).ToArray();
        return new { clipId = clip.Id, components, count = components.Length };
    }

    private static object QueryProviders(ClipDraftDTO clip)
    {
        Dictionary<Guid, IEffectProvider> providers = RestoreProviders(clip, out var restoreDiagnostics);
        return new
        {
            clipId = clip.Id,
            providers = providers.Values.Select(provider => DescribeProvider(provider.TypeName, provider)).ToArray(),
            diagnostics = restoreDiagnostics,
        };
    }

    private static object ValidateProviderGraph(ClipDraftDTO clip)
    {
        Dictionary<Guid, IEffectProvider> providers = RestoreProviders(clip, out var restoreDiagnostics);
        var diagnostics = restoreDiagnostics.Concat(ValidateProviderPorts(providers)).Distinct().ToArray();
        return new { valid = diagnostics.Length == 0, diagnostics };
    }

    private static object AddClipFromAsset(TimelineProjectWorkspace workspace, JsonElement arguments)
    {
        AssetItem asset = Required<AssetItem>(arguments, "asset");
        uint duration = OptionalUInt32(arguments, "duration") ?? DetermineAssetDuration(asset, workspace.ProjectInfo.TargetFrameRate);
        bool infinite = IsInfiniteAsset(asset);
        if (duration == 0) duration = 300;
        var clip = NewClip(workspace, arguments, asset.Name, asset.GetClipMode(), duration);
        clip.FilePath = "$" + (asset.AssetId ?? throw new ArgumentException("The asset has no assetId."));
        clip.FrameTime = asset.SecondPerFrame > 0 ? asset.SecondPerFrame : 1f / Math.Max(1u, workspace.ProjectInfo.TargetFrameRate);
        clip.SourceDuration = infinite ? null : asset.Duration;
        clip.IsInfiniteLength = infinite;
        clip.TargetWidth = Math.Max(0, asset.Width);
        clip.TargetHeight = Math.Max(0, asset.Height);
        if (asset.IsAIGenerated) clip.MetaData!["IsAI"] = true;
        return AddClip(workspace, clip);
    }

    private static object AddTextClip(TimelineProjectWorkspace workspace, JsonElement arguments)
    {
        string text = RequiredString(arguments, "text");
        uint duration = OptionalUInt32(arguments, "duration") ?? 300;
        var clip = NewClip(workspace, arguments, OptionalString(arguments, "name") ?? text, ClipMode.TextClip, duration);
        clip.IsInfiniteLength = true;
        clip.TargetWidth = OptionalInt32(arguments, "targetWidth") ?? workspace.ProjectInfo.RelativeWidth;
        clip.TargetHeight = OptionalInt32(arguments, "targetHeight") ?? workspace.ProjectInfo.RelativeHeight;
        var entry = new TextEntry
        {
            Text = text,
            FontName = OptionalString(arguments, "fontName") ?? "Arial",
            FontStyle = OptionalString(arguments, "fontStyle") ?? "Regular",
            FontSize = OptionalSingle(arguments, "fontSize") ?? Math.Max(1f, workspace.ProjectInfo.RelativeHeight * 0.08f),
            X = OptionalSingle(arguments, "x") ?? 0f,
            Y = OptionalSingle(arguments, "y") ?? 0f,
            FillR = OptionalUInt16(arguments, "fillR") ?? ushort.MaxValue,
            FillG = OptionalUInt16(arguments, "fillG") ?? ushort.MaxValue,
            FillB = OptionalUInt16(arguments, "fillB") ?? ushort.MaxValue,
            FillA = OptionalSingle(arguments, "fillA") ?? 1f,
        };
        clip.MetaData![TextEntriesKey] = new List<TextEntry> { entry };
        return AddClip(workspace, clip);
    }

    private static object SetTextEntries(TimelineProjectWorkspace workspace, JsonElement arguments)
    {
        ClipDraftDTO clip = FindClip(workspace.Draft, arguments);
        EnsureClipType(clip, ClipMode.TextClip);
        List<TextEntry> entries = Required<List<TextEntry>>(arguments, "entries");
        clip.MetaData ??= new();
        clip.MetaData[TextEntriesKey] = entries;
        return clip;
    }

    private static object AddSolidColorClip(TimelineProjectWorkspace workspace, JsonElement arguments)
    {
        uint duration = OptionalUInt32(arguments, "duration") ?? 300;
        string color = OptionalString(arguments, "color") ?? "#FFFFFFFF";
        var clip = NewClip(workspace, arguments, OptionalString(arguments, "name") ?? color, ClipMode.SolidColorClip, duration);
        clip.IsInfiniteLength = true;
        clip.TargetWidth = workspace.ProjectInfo.RelativeWidth;
        clip.TargetHeight = workspace.ProjectInfo.RelativeHeight;
        clip.MetaData!["UseFixedOutputSize"] = true;
        clip.MetaData["OutputWidth"] = workspace.ProjectInfo.RelativeWidth;
        clip.MetaData["OutputHeight"] = workspace.ProjectInfo.RelativeHeight;
        ApplySolidColor(clip, color);
        return AddClip(workspace, clip);
    }

    private static object SetSolidColor(TimelineProjectWorkspace workspace, JsonElement arguments)
    {
        ClipDraftDTO clip = FindClip(workspace.Draft, arguments);
        EnsureClipType(clip, ClipMode.SolidColorClip);
        ApplySolidColor(clip, RequiredString(arguments, "color"));
        return clip;
    }

    private static object AddVectorCanvasClip(TimelineProjectWorkspace workspace, JsonElement arguments)
    {
        uint duration = OptionalUInt32(arguments, "duration") ?? 1000;
        var clip = NewClip(workspace, arguments, OptionalString(arguments, "name") ?? "Vector Composition", ClipMode.VectorCanvasClip, duration);
        clip.IsInfiniteLength = true;
        clip.TargetWidth = OptionalInt32(arguments, "targetWidth") ?? workspace.ProjectInfo.RelativeWidth;
        clip.TargetHeight = OptionalInt32(arguments, "targetHeight") ?? workspace.ProjectInfo.RelativeHeight;
        clip.MetaData![VectorComponentsKey] = "[]";
        return AddClip(workspace, clip);
    }

    private static object AddVectorComponent(TimelineProjectWorkspace workspace, JsonElement arguments)
    {
        ClipDraftDTO clip = FindClip(workspace.Draft, arguments);
        EnsureClipType(clip, ClipMode.VectorCanvasClip);
        var components = ReadVectorComponents(clip);
        JsonElement component = NormalizeComponent(RequiredProperty(arguments, "component"), requireExistingId: false);
        components.Add(component);
        WriteVectorComponents(clip, components);
        return new { clipId = clip.Id, component };
    }

    private static object UpdateVectorComponent(TimelineProjectWorkspace workspace, JsonElement arguments)
    {
        ClipDraftDTO clip = FindClip(workspace.Draft, arguments);
        EnsureClipType(clip, ClipMode.VectorCanvasClip);
        Guid componentId = RequiredGuid(arguments, "componentId");
        var components = ReadVectorComponents(clip);
        int index = FindComponentIndex(components, componentId);
        JsonElement replacement = NormalizeComponent(RequiredProperty(arguments, "component"), requireExistingId: false, componentId);
        components[index] = replacement;
        WriteVectorComponents(clip, components);
        return new { clipId = clip.Id, component = replacement };
    }

    private static object RemoveVectorComponent(TimelineProjectWorkspace workspace, JsonElement arguments)
    {
        ClipDraftDTO clip = FindClip(workspace.Draft, arguments);
        EnsureClipType(clip, ClipMode.VectorCanvasClip);
        Guid componentId = RequiredGuid(arguments, "componentId");
        var components = ReadVectorComponents(clip);
        int index = FindComponentIndex(components, componentId);
        components.RemoveAt(index);
        WriteVectorComponents(clip, components);
        return new { removed = true, clipId = clip.Id, componentId };
    }

    private static object ReplaceVectorComponents(TimelineProjectWorkspace workspace, JsonElement arguments)
    {
        ClipDraftDTO clip = FindClip(workspace.Draft, arguments);
        EnsureClipType(clip, ClipMode.VectorCanvasClip);
        JsonElement source = RequiredProperty(arguments, "components");
        if (source.ValueKind != JsonValueKind.Array) throw new ArgumentException("'components' must be an array.");
        var components = source.EnumerateArray().Select(item => NormalizeComponent(item, requireExistingId: false)).ToList();
        EnsureUniqueComponentIds(components);
        WriteVectorComponents(clip, components);
        return new { clipId = clip.Id, components, count = components.Count };
    }

    private static object SetVectorComponentKeyframes(TimelineProjectWorkspace workspace, JsonElement arguments)
    {
        ClipDraftDTO clip = FindClip(workspace.Draft, arguments);
        EnsureClipType(clip, ClipMode.VectorCanvasClip);
        Guid componentId = RequiredGuid(arguments, "componentId");
        string fieldId = RequiredString(arguments, "fieldId");
        List<VectorAnimationKeyFrame> frames = Required<List<VectorAnimationKeyFrame>>(arguments, "keyframes");
        ValidateKeyframes(frames, fieldId);
        var components = ReadVectorComponents(clip);
        int index = FindComponentIndex(components, componentId);
        JsonElement componentJson = components[index];
        var component = CreateComponent(componentJson);
        if (!component.AnimatableFields.ContainsKey(fieldId))
            throw new ArgumentException($"Vector component '{component.TypeName}' does not expose animatable field '{fieldId}'.");
        component.AnimationFrames.RemoveAll(frame => string.Equals(frame.TargetFieldId, fieldId, StringComparison.Ordinal));
        component.AnimationFrames.AddRange(frames.OrderBy(frame => frame.Time));
        components[index] = SerializeComponent(component);
        WriteVectorComponents(clip, components);
        return new { clipId = clip.Id, componentId, fieldId, keyframes = frames };
    }

    private static object AddEffectProvider(TimelineProjectWorkspace workspace, JsonElement arguments)
    {
        ClipDraftDTO clip = FindClip(workspace.Draft, arguments);
        Dictionary<Guid, IEffectProvider> providers = RestoreProviders(clip, out _);
        string typeName = RequiredString(arguments, "typeName");
        var provider = ResolveProviderFactory(typeName)();
        provider.Id = OptionalGuid(arguments, "providerId") ?? Guid.NewGuid();
        provider.Name = OptionalString(arguments, "name") ?? provider.Name;
        provider.Enabled = OptionalBoolean(arguments, "enabled") ?? true;
        ApplyProviderFields(provider, OptionalObject(arguments, "fields"));
        ApplyProviderMetadata(provider, OptionalObject(arguments, "metadata"));
        ApplyProviderImplementType(provider, arguments);
        if (!providers.TryAdd(provider.Id, provider)) throw new ArgumentException($"Effect provider '{provider.Id}' already exists.");
        string autoConnect = OptionalString(arguments, "autoConnect") ?? "output";
        if (string.Equals(autoConnect, "output", StringComparison.OrdinalIgnoreCase))
            EffectBindingHelper.AutoConnectProviderToOutput(providers, provider, ClipTarget(clip));
        else if (string.Equals(autoConnect, "input", StringComparison.OrdinalIgnoreCase))
            EffectBindingHelper.AutoConnectProviderToInput(providers, provider);
        else if (!string.Equals(autoConnect, "none", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("'autoConnect' must be output, input, or none.");
        CommitProviders(clip, providers, allowIncompletePicturePath: string.Equals(autoConnect, "none", StringComparison.OrdinalIgnoreCase));
        return DescribeProvider(provider.TypeName, provider);
    }

    private static object UpdateEffectProvider(TimelineProjectWorkspace workspace, JsonElement arguments)
    {
        ClipDraftDTO clip = FindClip(workspace.Draft, arguments);
        Dictionary<Guid, IEffectProvider> providers = RestoreProviders(clip, out _);
        IEffectProvider provider = FindProvider(providers, RequiredGuid(arguments, "providerId"));
        if (arguments.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String) provider.Name = name.GetString()!;
        if (arguments.TryGetProperty("enabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False) provider.Enabled = enabled.GetBoolean();
        ApplyProviderFields(provider, OptionalObject(arguments, "fields"));
        ApplyProviderMetadata(provider, OptionalObject(arguments, "metadata"));
        ApplyProviderImplementType(provider, arguments);
        CommitProviders(clip, providers);
        return DescribeProvider(provider.TypeName, provider);
    }

    private static object RemoveEffectProvider(TimelineProjectWorkspace workspace, JsonElement arguments)
    {
        ClipDraftDTO clip = FindClip(workspace.Draft, arguments);
        Dictionary<Guid, IEffectProvider> providers = RestoreProviders(clip, out _);
        Guid providerId = RequiredGuid(arguments, "providerId");
        IEffectProvider removedProvider = FindProvider(providers, providerId);
        string upstream = removedProvider.GetMainInputSource();
        bool wasFinalOutput = removedProvider.IsFinalOutputSource();
        foreach (IEffectProvider provider in providers.Values.Where(provider => provider.Id != providerId))
        {
            if (provider.GetMainInputSource() == providerId.ToString()) provider.SetMainInputSource(upstream);
            foreach (var binding in provider.EnumerateFieldBindings().Where(binding => binding.Value == providerId.ToString()).ToArray())
                provider.ClearFieldBinding(binding.Key);
        }
        providers.Remove(providerId);
        if (wasFinalOutput)
        {
            Guid? replacementOutput = Guid.TryParse(upstream, out Guid upstreamId) && providers.ContainsKey(upstreamId)
                ? upstreamId
                : null;
            EffectBindingHelper.SetFinalOutput(providers, replacementOutput);
        }
        CommitProviders(clip, providers, allowIncompletePicturePath: true);
        return new { removed = true, providerId, diagnostics = ValidateProviderPorts(providers) };
    }

    private static object ConnectEffectProviderInput(TimelineProjectWorkspace workspace, JsonElement arguments)
    {
        ClipDraftDTO clip = FindClip(workspace.Draft, arguments);
        Dictionary<Guid, IEffectProvider> providers = RestoreProviders(clip, out _);
        IEffectProvider target = FindProvider(providers, RequiredGuid(arguments, "providerId"));
        string source = RequiredString(arguments, "source");
        if (string.Equals(source, "clip-input", StringComparison.OrdinalIgnoreCase)) source = IEffectProvider.InputAnchorGUID.ToString();
        if (string.Equals(source, "none", StringComparison.OrdinalIgnoreCase)) source = IEffectProvider.NoConnectionGUID.ToString();
        if (source != IEffectProvider.InputAnchorGUID.ToString() && source != IEffectProvider.NoConnectionGUID.ToString())
        {
            if (!Guid.TryParse(source, out Guid sourceId)) throw new ArgumentException("Picture source must be clip-input, none, or a provider UUID.");
            IEffectProvider sourceProvider = FindProvider(providers, sourceId);
            if (!sourceProvider.OutField.FieldType.HasFlag(EffectArgumentFieldType.IPicture))
                throw new ArgumentException($"Provider '{sourceId}' does not output a picture.");
        }
        target.SetMainInputSource(source);
        CommitProviders(clip, providers, allowIncompletePicturePath: true);
        return DescribeProvider(target.TypeName, target);
    }

    private static object SetEffectProviderOutput(TimelineProjectWorkspace workspace, JsonElement arguments)
    {
        ClipDraftDTO clip = FindClip(workspace.Draft, arguments);
        Dictionary<Guid, IEffectProvider> providers = RestoreProviders(clip, out _);
        Guid? providerId = OptionalGuid(arguments, "providerId");
        EffectBindingHelper.SetFinalOutput(providers, providerId);
        CommitProviders(clip, providers, allowIncompletePicturePath: true);
        return new { clipId = clip.Id, providerId };
    }

    private static object BindEffectProviderField(TimelineProjectWorkspace workspace, JsonElement arguments)
    {
        ClipDraftDTO clip = FindClip(workspace.Draft, arguments);
        Dictionary<Guid, IEffectProvider> providers = RestoreProviders(clip, out _);
        IEffectProvider target = FindProvider(providers, RequiredGuid(arguments, "providerId"));
        string fieldId = RequiredString(arguments, "fieldId");
        string source = RequiredString(arguments, "source");
        if (!target.Fields.TryGetValue(fieldId, out IEffectArgumentField? targetField))
            throw new KeyNotFoundException($"Field '{fieldId}' was not found on provider '{target.Id}'.");
        if (targetField.FieldType.HasFlag(EffectArgumentFieldType.CannotBeDynamic))
            throw new ArgumentException($"Field '{fieldId}' does not support dynamic bindings.");
        EffectArgumentFieldType sourceType;
        if (source is ValueProviderFrameContext.BuiltInFrameProviderId or ValueProviderFrameContext.BuiltInProgressProviderId)
            sourceType = EffectArgumentFieldType.Numeric;
        else
        {
            if (!Guid.TryParse(source, out Guid sourceId)) throw new ArgumentException("Value source must be builtin://frame, builtin://progress, or a provider UUID.");
            if (sourceId == target.Id) throw new ArgumentException("An effect provider field cannot bind to its own output.");
            IEffectProvider sourceProvider = FindProvider(providers, sourceId);
            if (!sourceProvider.Target.HasFlag(EffectTarget.ValueProvider))
                throw new ArgumentException($"Provider '{sourceId}' is not a value provider.");
            sourceType = sourceProvider.OutField.FieldType;
        }
        if (!ArePortTypesCompatible(sourceType, targetField.FieldType))
            throw new ArgumentException($"Port type '{sourceType}' cannot be bound to '{targetField.FieldType}'.");
        target.SetFieldBinding(fieldId, source);
        CommitProviders(clip, providers);
        return DescribeProvider(target.TypeName, target);
    }

    private static object UnbindEffectProviderField(TimelineProjectWorkspace workspace, JsonElement arguments)
    {
        ClipDraftDTO clip = FindClip(workspace.Draft, arguments);
        Dictionary<Guid, IEffectProvider> providers = RestoreProviders(clip, out _);
        IEffectProvider provider = FindProvider(providers, RequiredGuid(arguments, "providerId"));
        string fieldId = RequiredString(arguments, "fieldId");
        provider.ClearFieldBinding(fieldId);
        EffectBindingHelper.MaterializeFields(providers.Values);
        CommitProviders(clip, providers);
        return DescribeProvider(provider.TypeName, provider);
    }

    private static object ReplaceEffectProviderGraph(TimelineProjectWorkspace workspace, JsonElement arguments)
    {
        ClipDraftDTO clip = FindClip(workspace.Draft, arguments);
        EffectProviderJSONStructure[] structures = Required<EffectProviderJSONStructure[]>(arguments, "providers");
        Dictionary<Guid, IEffectProvider> providers = EffectBindingHelper.MigrateToEffectProviders(structures, null, out var restoreDiagnostics);
        if (providers.Count != structures.Length)
            throw new ArgumentException("One or more effect providers could not be restored: " + string.Join(" ", restoreDiagnostics.Select(item => item.Message)));
        CommitProviders(clip, providers);
        return new { clipId = clip.Id, providers = providers.Values.Select(provider => DescribeProvider(provider.TypeName, provider)).ToArray() };
    }

    private static object SetColorAdjustment(TimelineProjectWorkspace workspace, JsonElement arguments)
        => UpsertConvenienceProvider(workspace, arguments, "ColorAdjustment", new Dictionary<string, object?>
        {
            ["Brightness"] = OptionalSingle(arguments, "brightness"), ["Contrast"] = OptionalSingle(arguments, "contrast"),
            ["Saturation"] = OptionalSingle(arguments, "saturation"), ["Hue"] = OptionalSingle(arguments, "hue"),
            ["Gamma"] = OptionalSingle(arguments, "gamma"), ["Vibrance"] = OptionalSingle(arguments, "vibrance"),
            ["Temperature"] = OptionalSingle(arguments, "temperature"), ["Invert"] = OptionalBoolean(arguments, "invert"),
            ["Grayscale"] = OptionalSingle(arguments, "grayscale"), ["Opacity"] = OptionalSingle(arguments, "opacity"),
        });

    private static object SetClipSpeed(TimelineProjectWorkspace workspace, JsonElement arguments)
    {
        float ratio = RequiredSingle(arguments, "ratio");
        if (ratio is < 0.05f or > 8f) throw new ArgumentOutOfRangeException("ratio", "Speed ratio must be between 0.05 and 8.");
        return UpsertConvenienceProvider(workspace, arguments, "ClassicSpeedVarianceProvider", new Dictionary<string, object?> { ["Ratio"] = ratio }, autoConnect: false);
    }

    private static object SetLinearEffectAnimation(TimelineProjectWorkspace workspace, JsonElement arguments)
    {
        ClipDraftDTO clip = FindClip(workspace.Draft, arguments);
        Dictionary<Guid, IEffectProvider> providers = RestoreProviders(clip, out _);
        IEffectProvider target = FindProvider(providers, RequiredGuid(arguments, "targetProviderId"));
        string fieldId = RequiredString(arguments, "fieldId");
        if (!target.Fields.TryGetValue(fieldId, out var targetField)) throw new KeyNotFoundException($"Field '{fieldId}' was not found.");
        if (targetField.FieldType.HasFlag(EffectArgumentFieldType.CannotBeDynamic))
            throw new ArgumentException($"Field '{fieldId}' does not support dynamic bindings.");
        Guid? requestedAnimationId = OptionalGuid(arguments, "animationProviderId");
        Guid animationId = requestedAnimationId
            ?? (target.TryGetFieldBinding(fieldId, out string boundSource)
                && Guid.TryParse(boundSource, out Guid boundId)
                && providers.TryGetValue(boundId, out IEffectProvider? boundProvider)
                && string.Equals(boundProvider.TypeName, "LinearAnimationValueProvider", StringComparison.Ordinal)
                    ? boundId
                    : Guid.NewGuid());
        IEffectProvider animation;
        if (providers.TryGetValue(animationId, out IEffectProvider? existingAnimation))
        {
            if (!string.Equals(existingAnimation.TypeName, "LinearAnimationValueProvider", StringComparison.Ordinal))
                throw new ArgumentException($"Provider '{animationId}' is not a LinearAnimationValueProvider.");
            animation = existingAnimation;
        }
        else
        {
            animation = ResolveProviderFactory("LinearAnimationValueProvider")();
            animation.Id = animationId;
            providers.Add(animation.Id, animation);
        }
        animation.Name = OptionalString(arguments, "name") ?? $"{target.Name}.{fieldId} animation";
        ApplyProviderFields(animation, new Dictionary<string, JsonElement>
        {
            ["FromValue"] = JsonSerializer.SerializeToElement(RequiredSingle(arguments, "fromValue")),
            ["ToValue"] = JsonSerializer.SerializeToElement(RequiredSingle(arguments, "toValue")),
        });
        if (!ArePortTypesCompatible(animation.OutField.FieldType, targetField.FieldType)) throw new ArgumentException("The animation output is incompatible with the target field.");
        target.SetFieldBinding(fieldId, animation.Id.ToString());
        CommitProviders(clip, providers);
        return new { target = DescribeProvider(target.TypeName, target), animation = DescribeProvider(animation.TypeName, animation) };
    }

    private static object SetPositionKeyframes(TimelineProjectWorkspace workspace, JsonElement arguments)
    {
        JsonElement keyframes = RequiredProperty(arguments, "keyframes");
        ValidateNormalizedIndexes(keyframes);
        List<ProgressData> normalized = keyframes.Deserialize<List<ProgressData>>(JsonOptions)
            ?? throw new ArgumentException("Invalid position keyframes.");
        return UpsertConvenienceProvider(workspace, arguments, "ProgressPlacer", new Dictionary<string, object?>
        {
            ["ProgressList"] = JsonSerializer.Serialize(normalized),
        });
    }

    private static object SetCropKeyframes(TimelineProjectWorkspace workspace, JsonElement arguments)
    {
        JsonElement keyframes = RequiredProperty(arguments, "keyframes");
        ValidateNormalizedIndexes(keyframes);
        List<CropData> normalized = keyframes.Deserialize<List<CropData>>(JsonOptions)
            ?? throw new ArgumentException("Invalid crop keyframes.");
        return UpsertConvenienceProvider(workspace, arguments, "ProgressCrop", new Dictionary<string, object?>
        {
            ["StartX"] = OptionalInt32(arguments, "startX") ?? 0,
            ["StartY"] = OptionalInt32(arguments, "startY") ?? 0,
            ["Width"] = OptionalInt32(arguments, "width") ?? workspace.ProjectInfo.RelativeWidth,
            ["Height"] = OptionalInt32(arguments, "height") ?? workspace.ProjectInfo.RelativeHeight,
            ["Angle"] = OptionalSingle(arguments, "angle") ?? 0f,
            ["CropList"] = JsonSerializer.Serialize(normalized),
        });
    }

    private static object UpsertConvenienceProvider(
        TimelineProjectWorkspace workspace,
        JsonElement arguments,
        string typeName,
        IReadOnlyDictionary<string, object?> fields,
        bool autoConnect = true)
    {
        ClipDraftDTO clip = FindClip(workspace.Draft, arguments);
        Dictionary<Guid, IEffectProvider> providers = RestoreProviders(clip, out _);
        Guid? requestedId = OptionalGuid(arguments, "providerId");
        IEffectProvider? provider;
        if (requestedId.HasValue)
        {
            provider = providers.TryGetValue(requestedId.Value, out IEffectProvider? byId) ? byId : null;
        }
        else
        {
            IEffectProvider[] matches = providers.Values
                .Where(item => string.Equals(item.TypeName, typeName, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length > 1)
                throw new InvalidOperationException($"Multiple {typeName} providers exist; specify providerId.");
            provider = matches.SingleOrDefault();
        }
        bool isNew = provider is null;
        provider ??= ResolveProviderFactory(typeName)();
        if (!string.Equals(provider.TypeName, typeName, StringComparison.Ordinal))
            throw new ArgumentException($"Provider '{provider.Id}' is not a {typeName} provider.");
        if (isNew)
        {
            provider.Id = requestedId ?? Guid.NewGuid();
            providers.Add(provider.Id, provider);
        }
        ApplyProviderFields(provider, fields.Where(item => item.Value is not null).ToDictionary(item => item.Key, item => JsonSerializer.SerializeToElement(item.Value, JsonOptions)));
        if (isNew && autoConnect) EffectBindingHelper.AutoConnectProviderToOutput(providers, provider, ClipTarget(clip));
        CommitProviders(clip, providers);
        return DescribeProvider(provider.TypeName, provider);
    }

    private static ClipDraftDTO NewClip(TimelineProjectWorkspace workspace, JsonElement arguments, string name, ClipMode mode, uint duration)
    {
        uint frameRate = Math.Max(1u, workspace.ProjectInfo.TargetFrameRate);
        return new ClipDraftDTO
        {
            Id = Guid.NewGuid(), Name = name, FromPlugin = InternalPluginId, ClipType = mode,
            LayerIndex = RequiredUInt32(arguments, "layerIndex"), SubLayerIndex = OptionalUInt32(arguments, "subLayerIndex") ?? 0,
            StartFrame = RequiredUInt32(arguments, "startFrame"), Duration = Math.Max(1u, duration),
            FrameTime = 1f / frameRate, SecondPerFrameRatio = 1f, ShouldDisplayInUI = true,
            MetaData = new()
            {
                [ClipDraftDTO.ProjectFrameRateMetaKey] = frameRate,
                [ClipDraftDTO.FrameSemanticVersionMetaKey] = ClipDraftDTO.CurrentFrameSemanticVersion,
            },
        };
    }

    private static ClipDraftDTO AddClip(TimelineProjectWorkspace workspace, ClipDraftDTO clip)
    {
        var editor = new TimelineProjectEditor(workspace);
        editor.UpsertClip(clip);
        uint endFrame = clip.StartFrame > uint.MaxValue - clip.Duration ? uint.MaxValue : clip.StartFrame + clip.Duration;
        workspace.Draft.Duration = Math.Max(workspace.Draft.Duration, endFrame);
        if (clip.ClipType == ClipMode.AudioClip)
            workspace.Draft.AudioDuration = Math.Max(workspace.Draft.AudioDuration, endFrame);
        return clip;
    }

    private static uint DetermineAssetDuration(AssetItem asset, uint targetFrameRate)
    {
        if (IsInfiniteAsset(asset)) return 300;
        double frames = asset.AssetType switch
        {
            AssetType.Video when asset.SecondPerFrame > 0 => (asset.Duration ?? 0) * asset.SecondPerFrame * targetFrameRate,
            AssetType.Audio => (asset.Duration ?? 0) * targetFrameRate,
            _ => asset.Duration ?? 0,
        };
        return (uint)Math.Clamp(Math.Round(frames), 1d, uint.MaxValue);
    }

    private static bool IsInfiniteAsset(AssetItem asset)
        => asset.AssetType switch
        {
            AssetType.Audio => asset.Duration is null or <= 0,
            AssetType.Video => asset.Duration is null or <= 0 || asset.SecondPerFrame <= 0,
            _ => true,
        };

    private static void ApplySolidColor(ClipDraftDTO clip, string value)
    {
        string color = value.Trim().TrimStart('#');
        if (color.Length == 6) color += "FF";
        if (color.Length != 8 || !uint.TryParse(color, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgba))
            throw new ArgumentException("Color must use #RRGGBB or #RRGGBBAA.");
        byte r = (byte)(rgba >> 24), g = (byte)(rgba >> 16), b = (byte)(rgba >> 8), a = (byte)rgba;
        clip.MetaData ??= new();
        clip.MetaData["R"] = (ushort)(r * 257);
        clip.MetaData["G"] = (ushort)(g * 257);
        clip.MetaData["B"] = (ushort)(b * 257);
        clip.MetaData["A"] = a / 255f;
        clip.Name = "#" + color.ToUpperInvariant();
    }

    private static List<JsonElement> ReadVectorComponents(ClipDraftDTO clip)
    {
        if (clip.MetaData is null || !clip.MetaData.TryGetValue(VectorComponentsKey, out object? raw) || raw is null) return [];
        JsonElement value = raw switch
        {
            JsonElement element => element,
            string json => Parse(json),
            _ => JsonSerializer.SerializeToElement(raw, JsonOptions),
        };
        if (value.ValueKind == JsonValueKind.String) value = Parse(value.GetString() ?? "[]");
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"'{VectorComponentsKey}' must contain an array.");
        return value.EnumerateArray().Select(static item => item.Clone()).ToList();
    }

    private static void WriteVectorComponents(ClipDraftDTO clip, IReadOnlyList<JsonElement> components)
    {
        EnsureUniqueComponentIds(components);
        clip.MetaData ??= new();
        clip.MetaData[VectorComponentsKey] = JsonSerializer.Serialize(components, JsonOptions);
    }

    private static JsonElement NormalizeComponent(JsonElement source, bool requireExistingId, Guid? forcedId = null)
    {
        if (source.ValueKind != JsonValueKind.Object) throw new ArgumentException("A vector component must be an object.");
        JsonObject normalized = JsonNode.Parse(source.GetRawText()) as JsonObject
            ?? throw new ArgumentException("A vector component must be an object.");
        var values = normalized.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        string typeName = values.TryGetValue("typeName", out JsonNode? type) && type is System.Text.Json.Nodes.JsonValue typeValue && typeValue.TryGetValue(out string? parsedType)
            ? parsedType!
            : throw new ArgumentException("A vector component requires typeName.");
        string fromPlugin = values.TryGetValue("fromPlugin", out JsonNode? plugin) && plugin is System.Text.Json.Nodes.JsonValue pluginValue && pluginValue.TryGetValue(out string? parsedPlugin)
            ? parsedPlugin!
            : InternalPluginId;
        Guid id = forcedId ?? (values.TryGetValue("id", out JsonNode? idNode) && Guid.TryParse(idNode?.GetValue<string>(), out Guid parsed)
            ? parsed
            : requireExistingId ? throw new ArgumentException("A vector component requires id.") : Guid.NewGuid());

        SetCanonicalProperty(normalized, values, "FromPlugin", System.Text.Json.Nodes.JsonValue.Create(fromPlugin));
        SetCanonicalProperty(normalized, values, "TypeName", System.Text.Json.Nodes.JsonValue.Create(typeName));
        SetCanonicalProperty(normalized, values, "Id", System.Text.Json.Nodes.JsonValue.Create(id));
        SetCanonicalProperty(normalized, values, "Name", values.TryGetValue("name", out JsonNode? name) ? name?.DeepClone() : System.Text.Json.Nodes.JsonValue.Create(typeName));
        SetCanonicalProperty(normalized, values, "Index", values.TryGetValue("index", out JsonNode? index) ? index?.DeepClone() : System.Text.Json.Nodes.JsonValue.Create(0));
        SetCanonicalProperty(normalized, values, "Parameters", values.TryGetValue("parameters", out JsonNode? parameters) ? parameters?.DeepClone() : new JsonObject());
        SetCanonicalProperty(normalized, values, "AnimationFrames", values.TryGetValue("animationFrames", out JsonNode? frames) ? frames?.DeepClone() : new JsonArray());
        return SerializeComponent(CreateComponent(JsonSerializer.SerializeToElement(normalized, ComponentJsonOptions)));
    }

    private static void SetCanonicalProperty(JsonObject target, IReadOnlyDictionary<string, JsonNode?> values, string name, JsonNode? value)
    {
        foreach (string existing in values.Keys.Where(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase) && !string.Equals(key, name, StringComparison.Ordinal)).ToArray())
            target.Remove(existing);
        target[name] = value;
    }

    private static projectFrameCut.Render.RenderAPIBase.VectorContent.IVectorComponent CreateComponent(JsonElement element)
    {
        string fromPlugin = element.TryGetProperty("fromPlugin", out var lowerPlugin)
            ? lowerPlugin.GetString() ?? InternalPluginId
            : element.TryGetProperty("FromPlugin", out var plugin) ? plugin.GetString() ?? InternalPluginId : InternalPluginId;
        if (!PluginManager.LoadedPlugins.TryGetValue(fromPlugin, out var loaded))
            throw new KeyNotFoundException($"Plugin '{fromPlugin}' is not loaded.");
        return loaded.VectComponentCreator(element);
    }

    private static JsonElement SerializeComponent(projectFrameCut.Render.RenderAPIBase.VectorContent.IVectorComponent component)
        => JsonSerializer.SerializeToElement(component, component.GetType(), ComponentJsonOptions);

    private static int FindComponentIndex(IReadOnlyList<JsonElement> components, Guid id)
    {
        for (int i = 0; i < components.Count; i++)
        {
            JsonElement item = components[i];
            if ((item.TryGetProperty("Id", out var upper) || item.TryGetProperty("id", out upper)) && upper.TryGetGuid(out Guid found) && found == id) return i;
        }
        throw new KeyNotFoundException($"Vector component '{id}' was not found.");
    }

    private static void EnsureUniqueComponentIds(IEnumerable<JsonElement> components)
    {
        var ids = new HashSet<Guid>();
        foreach (JsonElement item in components)
        {
            if (!(item.TryGetProperty("Id", out var idValue) || item.TryGetProperty("id", out idValue)) || !idValue.TryGetGuid(out Guid id))
                throw new ArgumentException("Every vector component requires a UUID id.");
            if (!ids.Add(id)) throw new ArgumentException($"Duplicate vector component id '{id}'.");
        }
    }

    private static void ValidateKeyframes(IEnumerable<VectorAnimationKeyFrame> frames, string fieldId)
    {
        var times = new HashSet<float>();
        foreach (var frame in frames)
        {
            if (frame.Time is < 0f or > 1f) throw new ArgumentOutOfRangeException("keyframes", "Keyframe time must be between 0 and 1.");
            if (!times.Add(frame.Time)) throw new ArgumentException($"Duplicate keyframe time '{frame.Time}'.");
            frame.TargetFieldId = fieldId;
        }
    }

    private static Dictionary<Guid, IEffectProvider> RestoreProviders(ClipDraftDTO clip, out IReadOnlyList<BindingDiagnostic> diagnostics)
        => EffectBindingHelper.MigrateToEffectProviders(clip.EffectProviders, null, out diagnostics);

    private static void CommitProviders(ClipDraftDTO clip, Dictionary<Guid, IEffectProvider> providers, bool allowIncompletePicturePath = false)
    {
        EffectBindingHelper.MaterializeFields(providers.Values);
        BindingDiagnostic[] diagnostics = ValidateProviderPorts(providers).ToArray();
        if (!allowIncompletePicturePath && diagnostics.Length != 0)
            throw new InvalidOperationException("Invalid effect provider graph: " + string.Join(" ", diagnostics.Select(item => item.Message)));
        if (allowIncompletePicturePath && diagnostics.Any(item => item.Code != "MissingFinalOutput"))
            throw new InvalidOperationException("Invalid effect provider graph: " + string.Join(" ", diagnostics.Select(item => item.Message)));
        clip.EffectProviders = providers.Values.Select(SerializeProvider).ToArray();
    }

    private static EffectProviderJSONStructure SerializeProvider(IEffectProvider provider)
        => new()
        {
            Id = provider.Id, FromPlugin = provider.FromPlugin, TypeName = provider.TypeName,
            Name = provider.Name, Enabled = provider.Enabled,
            AnchorsBindingState = new(provider.AnchorsBindingState ?? []),
            StaticFields = provider.Fields
                .Where(item => item.Value is StaticEffectArgumentField or DynamicEffectParamField)
                .ToDictionary(item => item.Key, item => EffectParamConvert.Normalize(GetFieldValue(item.Value)) ?? new object()),
            MetaData = provider.MetaData is { Count: > 0 } ? new(provider.MetaData) : null,
        };

    private static IEnumerable<BindingDiagnostic> ValidateProviderPorts(IReadOnlyDictionary<Guid, IEffectProvider> providers)
    {
        foreach (BindingDiagnostic diagnostic in EffectBindingHelper.ValidateBindings(providers)) yield return diagnostic;
        IEffectProvider[] pictureProviders = providers.Values
            .Where(provider => provider.OutField.FieldType.HasFlag(EffectArgumentFieldType.IPicture))
            .ToArray();
        if (pictureProviders.Length > 0 && !pictureProviders.Any(provider => provider.IsFinalOutputSource()))
            yield return new BindingDiagnostic(null, "MissingFinalOutput", "The picture provider graph has no final output provider.");
        foreach (IEffectProvider target in providers.Values)
        {
            foreach (var binding in target.EnumerateFieldBindings())
            {
                if (!target.Fields.TryGetValue(binding.Key, out var targetField)) continue;
                EffectArgumentFieldType sourceType;
                if (binding.Value is ValueProviderFrameContext.BuiltInFrameProviderId or ValueProviderFrameContext.BuiltInProgressProviderId)
                    sourceType = EffectArgumentFieldType.Numeric;
                else if (binding.Value.StartsWith("builtin://", StringComparison.Ordinal))
                {
                    yield return new BindingDiagnostic(target.Id, "UnknownBuiltinSource", $"Field '{binding.Key}' references unknown built-in source '{binding.Value}'.");
                    continue;
                }
                else if (Guid.TryParse(binding.Value, out Guid sourceId) && providers.TryGetValue(sourceId, out var source))
                {
                    if (sourceId == target.Id)
                        yield return new BindingDiagnostic(target.Id, "SelfFieldBinding", $"Field '{binding.Key}' is bound to its own provider output.");
                    sourceType = source.OutField.FieldType;
                }
                else continue;
                if (targetField.FieldType.HasFlag(EffectArgumentFieldType.CannotBeDynamic))
                    yield return new BindingDiagnostic(target.Id, "FieldCannotBeDynamic", $"Field '{binding.Key}' does not support dynamic bindings.");
                if (!ArePortTypesCompatible(sourceType, targetField.FieldType))
                    yield return new BindingDiagnostic(target.Id, "IncompatibleFieldPort", $"Field '{binding.Key}' cannot accept source type '{sourceType}'.");
            }

            string pictureSource = target.GetMainInputSource();
            if (Guid.TryParse(pictureSource, out Guid pictureSourceId)
                && providers.TryGetValue(pictureSourceId, out IEffectProvider? pictureProvider)
                && !pictureProvider.OutField.FieldType.HasFlag(EffectArgumentFieldType.IPicture))
            {
                yield return new BindingDiagnostic(target.Id, "IncompatiblePicturePort", $"Picture input cannot accept output type '{pictureProvider.OutField.FieldType}' from provider {pictureSourceId}.");
            }
        }
        foreach (BindingDiagnostic diagnostic in ValidateValueBindingCycles(providers)) yield return diagnostic;
    }

    private static IReadOnlyList<BindingDiagnostic> ValidateValueBindingCycles(IReadOnlyDictionary<Guid, IEffectProvider> providers)
    {
        var diagnostics = new List<BindingDiagnostic>();
        var state = new Dictionary<Guid, byte>();
        var path = new List<Guid>();
        var reported = new HashSet<string>(StringComparer.Ordinal);

        foreach (Guid id in providers.Keys) Visit(id);
        return diagnostics;

        void Visit(Guid id)
        {
            if (state.GetValueOrDefault(id) == 2) return;
            state[id] = 1;
            path.Add(id);
            foreach (Guid dependency in providers[id].EnumerateFieldBindings()
                         .Select(binding => Guid.TryParse(binding.Value, out Guid parsed) ? parsed : Guid.Empty)
                         .Where(dependency => dependency != Guid.Empty && providers.ContainsKey(dependency)))
            {
                if (state.GetValueOrDefault(dependency) == 1)
                {
                    int start = path.IndexOf(dependency);
                    Guid[] cycle = [.. path.Skip(Math.Max(0, start)), dependency];
                    string signature = string.Join("|", cycle.Take(cycle.Length - 1).OrderBy(value => value));
                    if (reported.Add(signature))
                        diagnostics.Add(new(id, "ValueBindingCycle", $"Value binding cycle detected: {string.Join(" -> ", cycle)}."));
                }
                else
                {
                    Visit(dependency);
                }
            }
            path.RemoveAt(path.Count - 1);
            state[id] = 2;
        }
    }

    private static bool ArePortTypesCompatible(EffectArgumentFieldType source, EffectArgumentFieldType target)
    {
        EffectArgumentFieldType src = source & (EffectArgumentFieldType)0x1FFF;
        EffectArgumentFieldType dst = target & (EffectArgumentFieldType)0x1FFF;
        if (src == EffectArgumentFieldType.Unknown || dst == EffectArgumentFieldType.Unknown || src == dst) return true;
        bool Numeric(EffectArgumentFieldType value) => value is EffectArgumentFieldType.Numeric or EffectArgumentFieldType.Integer or EffectArgumentFieldType.UnsignedInteger or EffectArgumentFieldType.Long or EffectArgumentFieldType.UnsignedLong;
        if (Numeric(src) && Numeric(dst)) return true;
        return src == EffectArgumentFieldType.SizeAndPosition && dst is EffectArgumentFieldType.Size or EffectArgumentFieldType.Position
            || dst == EffectArgumentFieldType.SizeAndPosition && src is EffectArgumentFieldType.Size or EffectArgumentFieldType.Position;
    }

    private static object DescribeProvider(string typeName, IEffectProvider provider)
        => new
        {
            available = true,
            provider.Id, typeName, provider.Name, provider.FromPlugin, provider.Enabled,
            effectType = provider.TypeOfEffect.ToString(), target = provider.Target.ToString(),
            implementationType = provider.GetType().FullName,
            supportedImplementTypes = provider.SupportsImplementTypes.Select(item => item.ToString()).ToArray(),
            defaultImplementType = provider.DefaultImplementType.ToString(),
            configuredImplementType = provider.MetaData is not null && provider.MetaData.TryGetValue(EffectProviderBase.ImplementTypeParameterKey, out object? configuredImplementType)
                ? configuredImplementType?.ToString()
                : null,
            inputPorts = provider.InFields.Values.Select(DescribeDescriptor).ToArray(),
            outputPort = DescribeDescriptor(provider.OutField),
            fields = provider.Fields.Values.Select(field => new
            {
                field.Id, fieldType = field.FieldType.ToString(), value = GetFieldValue(field),
                bindingSource = provider.TryGetFieldBinding(field.Id, out string source) ? source : null,
                field.DefaultValue, field.MinValue, field.MaxValue, field.PresetOptions, field.Remarks,
            }).ToArray(),
            anchorsBindingState = provider.AnchorsBindingState,
            metadata = provider.MetaData,
        };

    private static object DescribeDescriptor(EffectArgumentFieldDescriptor descriptor)
        => new { descriptor.Id, fieldType = descriptor.FieldType.ToString(), descriptor.TypeName, descriptor.DefaultValue, descriptor.MinValue, descriptor.MaxValue, descriptor.PresetOptions, descriptor.Remarks };

    private static object? GetFieldValue(IEffectArgumentField field) => field switch
    {
        StaticEffectArgumentField value => value.Value,
        DynamicEffectParamField value => value.StaticFallbackValue,
        _ => field.DefaultValue,
    };

    private static void ApplyProviderFields(IEffectProvider provider, IReadOnlyDictionary<string, JsonElement>? values)
    {
        if (values is null) return;
        Dictionary<string, IEffectArgumentField> fields = provider.Fields;
        foreach ((string fieldId, JsonElement raw) in values)
        {
            if (!fields.TryGetValue(fieldId, out IEffectArgumentField? field)) throw new KeyNotFoundException($"Field '{fieldId}' was not found on provider '{provider.TypeName}'.");
            if (field.FieldType.HasFlag(EffectArgumentFieldType.CanNotBeStatic))
                throw new ArgumentException($"Field '{fieldId}' requires a dynamic binding.");
            object converted = ConvertFieldValue(field, raw);
            ValidateFieldRange(field, converted);
            fields[fieldId] = new StaticEffectArgumentField
            {
                Id = fieldId, FieldType = field.FieldType, Value = converted,
                DefaultValue = field.DefaultValue, MinValue = field.MinValue, MaxValue = field.MaxValue,
                PresetOptions = field.PresetOptions, Remarks = field.Remarks,
            };
            provider.ClearFieldBinding(fieldId);
        }
        provider.Fields = fields;
    }

    private static void ApplyProviderMetadata(IEffectProvider provider, IReadOnlyDictionary<string, JsonElement>? metadata)
    {
        if (metadata is null) return;
        provider.MetaData ??= new();
        foreach ((string key, JsonElement value) in metadata)
        {
            if (value.ValueKind == JsonValueKind.Null) provider.MetaData.Remove(key);
            else provider.MetaData[key] = JsonValue(value) ?? new object();
        }
    }

    private static void ApplyProviderImplementType(IEffectProvider provider, JsonElement arguments)
    {
        string? value = OptionalString(arguments, "implementType");
        if (value is null) return;
        if (!Enum.TryParse(value, ignoreCase: true, out EffectImplementType implementType))
            throw new ArgumentException($"Unknown effect implementation type '{value}'.");
        if (implementType != EffectImplementType.NotSpecified && !provider.SupportsImplementTypes.Contains(implementType))
            throw new ArgumentException($"Provider '{provider.TypeName}' does not support implementation type '{implementType}'.");
        provider.MetaData ??= new();
        if (implementType == EffectImplementType.NotSpecified)
            provider.MetaData.Remove(EffectProviderBase.ImplementTypeParameterKey);
        else
            provider.MetaData[EffectProviderBase.ImplementTypeParameterKey] = implementType.ToString();
    }

    private static object ConvertFieldValue(IEffectArgumentField field, JsonElement raw)
    {
        object? value = JsonValue(raw);
        EffectArgumentFieldType type = field.FieldType & (EffectArgumentFieldType)0x3FF;
        return type switch
        {
            EffectArgumentFieldType.Integer when EffectParamConvert.TryConvertToInt(value, out int result) => result,
            EffectArgumentFieldType.UnsignedInteger when EffectParamConvert.TryConvertToUShort(value, out ushort result) => result,
            EffectArgumentFieldType.Numeric when EffectParamConvert.TryConvertToFloat(value, out float result) => result,
            EffectArgumentFieldType.Boolean when EffectParamConvert.TryConvertToBool(value, out bool result) => result,
            EffectArgumentFieldType.Long => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            EffectArgumentFieldType.UnsignedLong => Convert.ToUInt64(value, CultureInfo.InvariantCulture),
            EffectArgumentFieldType.String => value?.ToString() ?? string.Empty,
            _ when value is not null => value,
            _ => throw new ArgumentException($"Field '{field.Id}' cannot be null."),
        };
    }

    private static void ValidateFieldRange(IEffectArgumentField field, object value)
    {
        if (field.PresetOptions is { Length: > 0 } && value is string text && !field.PresetOptions.Contains(text, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Field '{field.Id}' must be one of: {string.Join(", ", field.PresetOptions)}.");
        if (!double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) return;
        if (double.TryParse(field.MinValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double min) && number < min) throw new ArgumentOutOfRangeException(field.Id, $"Value must be at least {min}.");
        if (double.TryParse(field.MaxValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double max) && number > max) throw new ArgumentOutOfRangeException(field.Id, $"Value must be at most {max}.");
    }

    private static IEnumerable<(string TypeName, Func<IEffectProvider> Factory)> EnumerateProviderFactories()
        => (EffectHelper.EffectsProviderEnum ?? throw new InvalidOperationException("The render runtime has not initialized effect providers."))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => (item.Key, item.Value));

    private static Func<IEffectProvider> ResolveProviderFactory(string typeName)
    {
        var factories = EffectHelper.EffectsProviderEnum ?? throw new InvalidOperationException("The render runtime has not initialized effect providers.");
        return factories.TryGetValue(typeName, out var factory) ? factory : throw new KeyNotFoundException($"Effect provider type '{typeName}' was not found.");
    }

    private static IEffectProvider FindProvider(IReadOnlyDictionary<Guid, IEffectProvider> providers, Guid id)
        => providers.TryGetValue(id, out var provider) ? provider : throw new KeyNotFoundException($"Effect provider '{id}' was not found.");

    private static EffectTarget ClipTarget(ClipDraftDTO clip)
        => clip.ClipType == ClipMode.AudioClip ? EffectTarget.Audio : clip.ClipType == ClipMode.TextClip ? EffectTarget.Video | EffectTarget.Text : EffectTarget.Video | EffectTarget.ColorAdjustment;

    private static ClipDraftDTO FindClip(DraftStructureJSON draft, JsonElement arguments)
    {
        Guid id = RequiredGuid(arguments, "clipId");
        return draft.Clips.FirstOrDefault(clip => clip.Id == id) ?? throw new KeyNotFoundException($"Clip '{id}' was not found.");
    }

    private static void EnsureClipType(ClipDraftDTO clip, ClipMode mode)
    {
        if (clip.ClipType != mode) throw new InvalidOperationException($"Clip '{clip.Id}' is '{clip.ClipType}', not '{mode}'.");
    }

    private static void ValidateNormalizedIndexes(JsonElement keyframes)
    {
        if (keyframes.ValueKind != JsonValueKind.Array) throw new ArgumentException("'keyframes' must be an array.");
        double previous = -1;
        foreach (JsonElement item in keyframes.EnumerateArray())
        {
            if (!item.TryGetProperty("index", out JsonElement index) || !index.TryGetDouble(out double value) || value is < 0 or > 1)
                throw new ArgumentOutOfRangeException("keyframes", "Every keyframe index must be between 0 and 1.");
            if (value <= previous) throw new ArgumentException("Keyframe indexes must be strictly increasing.");
            previous = value;
        }
    }

    private static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static object? JsonValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt64(out long integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        _ => value.Clone(),
    };

    private static JsonElement RequiredProperty(JsonElement arguments, string name)
        => arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out JsonElement value)
            ? value : throw new ArgumentException($"Missing '{name}'.", nameof(arguments));

    private static string RequiredString(JsonElement arguments, string name)
        => OptionalString(arguments, name) ?? throw new ArgumentException($"Missing or invalid '{name}'.", nameof(arguments));

    private static string? OptionalString(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;

    private static Guid RequiredGuid(JsonElement arguments, string name)
        => OptionalGuid(arguments, name) ?? throw new ArgumentException($"Missing or invalid '{name}'.", nameof(arguments));

    private static Guid? OptionalGuid(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out Guid result) ? result : null;

    private static uint RequiredUInt32(JsonElement arguments, string name)
        => OptionalUInt32(arguments, name) ?? throw new ArgumentException($"Missing or invalid '{name}'.", nameof(arguments));

    private static uint? OptionalUInt32(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out JsonElement value) && value.TryGetUInt32(out uint result) ? result : null;

    private static int? OptionalInt32(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : null;

    private static ushort? OptionalUInt16(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out JsonElement value) && value.TryGetUInt16(out ushort result) ? result : null;

    private static float RequiredSingle(JsonElement arguments, string name)
        => OptionalSingle(arguments, name) ?? throw new ArgumentException($"Missing or invalid '{name}'.", nameof(arguments));

    private static float? OptionalSingle(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out JsonElement value) && value.TryGetSingle(out float result) ? result : null;

    private static bool? OptionalBoolean(JsonElement arguments, string name)
        => arguments.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static T Required<T>(JsonElement arguments, string name)
        => RequiredProperty(arguments, name).Deserialize<T>(JsonOptions) ?? throw new ArgumentException($"Invalid '{name}'.", nameof(arguments));

    private static Dictionary<string, JsonElement>? OptionalObject(JsonElement arguments, string name)
    {
        if (!arguments.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException($"'{name}' must be an object.");
        return value.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal);
    }
}
