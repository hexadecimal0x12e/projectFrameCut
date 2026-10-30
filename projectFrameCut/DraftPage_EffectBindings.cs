using System.Globalization;
using System.Text;
using System.Text.Json;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Services;
using projectFrameCut.Shared;

namespace projectFrameCut;

public partial class DraftPage
{
    private static StaticEffectArgumentField GuiStaticField(string id, IEffectArgumentField field, object? value)
    {
        return new StaticEffectArgumentField
        {
            Id = id,
            FieldType = field.FieldType,
            Value = value,
            DefaultValue = field.DefaultValue,
            MinValue = field.MinValue,
            MaxValue = field.MaxValue,
            PresetOptions = field.PresetOptions,
            Remarks = field.Remarks,
        };
    }

    private static object GuiEffectField(IEffectProvider provider, string id, IEffectArgumentField field,
        EffectFieldTypeResolution? types = null, bool input = true)
    {
        var fieldType = field.FieldType;
        if (types is not null)
        {
            if (input)
            {
                fieldType = types.GetInputType(provider, id);
            }
            else
            {
                fieldType = types.GetOutputType(provider, id);
            }
        }

        object? value = null;
        if (field is StaticEffectArgumentField staticField)
        {
            value = staticField.Value;
        }
        else if (field is DynamicEffectParamField dynamicField)
        {
            value = dynamicField.StaticFallbackValue;
        }

        return new
        {
            FieldId = id,
            FieldType = fieldType.ToString(),
            DeclaredFieldType = field.FieldType.ToString(),
            field.DefaultValue,
            field.MinValue,
            field.MaxValue,
            field.PresetOptions,
            field.Remarks,
            CanBind = input && !field.FieldType.HasFlag(EffectArgumentFieldType.CannotBeDynamic),
            SourceId = input ? provider.AnchorsBindingState.GetValueOrDefault(id) : null,
            Value = value,
        };
    }

    private ClipArgumentProvider CreateGuiClipArguments(GuiEffectClip clip, Guid id)
    {
        var provider = new ClipArgumentProvider { Id = id };
        IReadOnlyDictionary<string, ClipArgumentFieldDescriptor> fields = ClipArgumentHandler.DefaultFields;
        var values = new Dictionary<string, object>();
        try
        {
            var dto = clip.Dto;
            using var owner = PluginManager.CreateClip(JsonSerializer.SerializeToElement(dto));
            clip.SupportedStages = owner.SupportedStages.ToArray();
            if (clip.Live is not null) clip.Live.DeclaredStages = clip.SupportedStages;
            fields = owner.ArgumentFields;
            foreach (var field in fields)
            {
                values[field.Key] = field.Value.ReadValue(owner);
            }
        }
        catch (Exception ex)
        {
            Logger.Log(ex, $"Read argument fields for clip {clip.Id}", this);
        }
        provider.Attach(fields, key => key switch
        {
            nameof(IClip.TargetX) => clip.TargetX,
            nameof(IClip.TargetY) => clip.TargetY,
            nameof(IClip.TargetWidth) => clip.TargetWidth,
            nameof(IClip.TargetHeight) => clip.TargetHeight,
            nameof(IClip.Rotation) => clip.Rotation,
            _ => values.GetValueOrDefault(key) ?? fields[key].DefaultValue,
        });
        return provider;
    }

    private Dictionary<Guid, IEffectProvider> CopyGuiEffectProviders(GuiEffectClip clip)
    {
        var result = new Dictionary<Guid, IEffectProvider>();
        if (clip.EffectProviders is null)
        {
            return result;
        }

        var factories = EffectServices.GetAvailableEffectProviders();
        foreach (var original in clip.EffectProviders.Values)
        {
            var data = EffectBindingHelper.SerializeProvider(original);
            IEffectProvider copy;
            if (original is ClipArgumentProvider)
            {
                copy = CreateGuiClipArguments(clip, original.Id);
            }
            else
            {
                if (!factories.TryGetValue(original.TypeName, out var factory))
                {
                    throw new KeyNotFoundException($"Effect provider type '{original.TypeName}' is unavailable.");
                }
                copy = factory();
            }

            copy.Stage = original.Stage;
            copy.Id = original.Id;
            copy.Name = original.Name;
            copy.Enabled = original.Enabled;
            copy.MetaData = EffectGroupHelper.Copy(original.MetaData);
            if (copy is ClipArgumentProvider arguments)
            {
                arguments.RestoreStaticValues(data.StaticFields);
            }
            else
            {
                var fields = copy.Fields;
                foreach (var value in data.StaticFields ?? [])
                {
                    if (fields.TryGetValue(value.Key, out var field))
                    {
                        fields[value.Key] = GuiStaticField(value.Key, field, value.Value);
                    }
                }
                copy.Fields = fields;
            }
            copy.AnchorsBindingState = new Dictionary<string, string>(original.AnchorsBindingState);
            result.Add(copy.Id, copy);
        }
        EffectBindingHelper.MaterializeFields(result.Values);
        return result;
    }

    private static IReadOnlyList<EffectBindingHelper.BindingDiagnostic> GuiBindingErrors(
        Dictionary<Guid, IEffectProvider> providers, EffectFieldTypeResolution types)
    {
        return EffectBindingHelper.ValidateBindings(providers, types)
            .Concat(DynamicEffectBindings.Validate(providers, types))
            .Distinct()
            .ToArray();
    }

    private bool GuiClipPrecedes(GuiEffectClip source, GuiEffectClip target)
    {
        try
        {
            var s = source.Dto;
            var t = target.Dto;
            return ClipLayerOrder.Compare(s.LayerIndex, s.SubLayerIndex, t.LayerIndex, t.SubLayerIndex) < 0;
        }
        catch (KeyNotFoundException ex)
        {
            Logger.Log(ex, "Resolve endpoint clip order", this);
            return false;
        }
    }

    private string? GuiEndpointStatus(GuiEffectClip clip, IClipValueEndpointProvider endpoint,
        IReadOnlyDictionary<Guid, IEffectProvider> providers)
    {
        if (endpoint.IsExport)
        {
            return endpoint.GetMainInputSource() == IEffectProvider.NoConnectionGUID.ToString() ? "Endpoint output is disconnected." : null;
        }
        if (FindGuiEffectClip(endpoint.SourceClipId, clip.Live is null) is not { } source)
        {
            return "Source clip is missing.";
        }

        var sourceProviders = source.Id == clip.Id ? providers : source.EffectProviders;
        var output = sourceProviders?.GetValueOrDefault(endpoint.SourceEndpointId) as IClipValueEndpointProvider;
        if (output is null || !output.IsExport)
        {
            return "Source endpoint is missing.";
        }
        if (!output.Enabled)
        {
            return "Source endpoint is disabled.";
        }
        if (source.Id != clip.Id && !output.AllowCrossClip)
        {
            return "Source endpoint only allows pass-through within its own clip.";
        }
        if (output.ValueType != endpoint.ValueType || !output.CanPassTo(endpoint.Stage, source.Id == clip.Id, providers.Values))
        {
            return "Source endpoint type or pipeline has changed.";
        }
        if (source.Id != clip.Id && !GuiClipPrecedes(source, clip))
        {
            return "Source clip must precede the target clip in layer order.";
        }
        return output.GetMainInputSource() == IEffectProvider.NoConnectionGUID.ToString() ? "Source endpoint is disconnected." : null;
    }

    private object GuiEndpoint(GuiEffectClip clip, IClipValueEndpointProvider endpoint, IReadOnlyDictionary<Guid, IEffectProvider> providers)
    {
        return new
        {
            ClipId = clip.Id,
            EndpointId = endpoint.Id,
            ProviderId = endpoint.Id,
            endpoint.Name,
            endpoint.Enabled,
            endpoint.IsExport,
            endpoint.AllowCrossClip,
            ValueType = endpoint.ValueType.ToString(),
            Stage = endpoint.Stage.ToString(),
            Pipeline = endpoint.GetPipeline().ToString(),
            endpoint.SourceClipId,
            endpoint.SourceEndpointId,
            Status = GuiEndpointStatus(clip, endpoint, providers),
        };
    }

    private static object GuiEffectGroup(Guid clipId, EffectGroup group)
    {
        return new
        {
            ClipId = clipId,
            GroupId = group.Id,
            group.Name,
            group.ProviderIds,
            group.ChildGroupIds,
            group.Ports,
            group.Collapsed,
            group.X,
            group.Y,
        };
    }

    private static object[] GuiEffectBindings(Guid clipId, IReadOnlyDictionary<Guid, IEffectProvider> providers)
    {
        var bindings = new List<object>();
        foreach (var provider in providers.Values)
        {
            foreach (var binding in provider.AnchorsBindingState)
            {
                if (EffectProviderAnchorExtensions.IsOutputBindingKey(binding.Key) || binding.Value == IEffectProvider.NoConnectionGUID.ToString())
                {
                    continue;
                }

                bool parsed = EffectProviderOutputExtensions.TryParseOutputSourceId(binding.Value, out var sourceId, out var outputId);
                bindings.Add(new
                {
                    ClipId = clipId,
                    ProviderId = provider.Id,
                    FieldId = binding.Key,
                    SourceId = binding.Value,
                    SourceProviderId = parsed ? (Guid?)sourceId : null,
                    SourceOutputFieldId = parsed ? outputId : null,
                    Stage = provider.Stage.ToString(),
                    SupportedStages = provider.SupportedStages.Select(s => s.ToString()).ToArray(),
                    Pipeline = provider.GetPipeline().ToString(),
                });
            }
        }
        return bindings.ToArray();
    }

    private object GuiEffectGraph(GuiEffectClip clip, Dictionary<Guid, IEffectProvider> providers,
        List<EffectGroup> groups, EffectFieldTypeResolution types)
    {
        var outputs = new List<object>();
        var diagnostics = GuiBindingErrors(providers, types).ToList();
        foreach (var provider in providers.Values)
        {
            if (provider.IsFinalOutputSource())
            {
                outputs.Add(new
                {
                    ProviderId = provider.Id,
                    OutputFieldId = provider.GetFinalOutputFieldId(),
                    SourceId = provider.GetFinalOutputSourceId(),
                    Stage = provider.Stage.ToString(),
                    SupportedStages = provider.SupportedStages.Select(s => s.ToString()).ToArray(),
                    Pipeline = provider.GetPipeline().ToString(),
                });
            }
            if (provider is IClipValueEndpointProvider endpoint)
            {
                string? status = GuiEndpointStatus(clip, endpoint, providers);
                if (status is not null)
                {
                    diagnostics.Add(new EffectBindingHelper.BindingDiagnostic(endpoint.Id, "InvalidClipEndpoint", status));
                }
            }
        }

        return new
        {
            ClipId = clip.Id,
            InputSourceId = IEffectProvider.InputAnchorGUID,
            Providers = providers.Values.Select(provider => GuiProvider(provider, clip.Id, types: types)).ToArray(),
            Bindings = GuiEffectBindings(clip.Id, providers),
            Outputs = outputs.ToArray(),
            Groups = groups.Select(group => GuiEffectGroup(clip.Id, group)).ToArray(),
            Diagnostics = diagnostics.ToArray(),
        };
    }

    private static void ValidateGuiProviderTarget(GuiEffectClip clip, IEffectProvider provider)
    {
        if (provider is ClipArgumentProvider)
        {
            return;
        }
        EffectStages.Validate(provider);
        if (!clip.SupportedStages.Contains(provider.Stage))
            throw new ArgumentException($"Clip {clip.Id} does not support stage {provider.Stage}.");
        var target = clip.GetEffectSelectionTarget(provider.Stage);
        if (!EffectBindingHelper.AreTargetsCompatible(provider.Target, target))
        {
            throw new ArgumentException($"Effect provider '{provider.TypeName}' is incompatible with clip {clip.Id}.");
        }
    }

    private static string GuiBindingSource(JsonElement parameters, IReadOnlyDictionary<Guid, IEffectProvider> providers)
    {
        string source;
        if (parameters.TryGetProperty("BuiltIn", out var builtin))
        {
            switch (builtin.GetString()?.ToLowerInvariant())
            {
                case "frame":
                    source = ValueProviderFrameContext.BuiltInFrameProviderId;
                    break;
                case "progress":
                    source = ValueProviderFrameContext.BuiltInProgressProviderId;
                    break;
                default:
                    throw new ArgumentException("Unknown built-in binding source.");
            }
        }
        else if (parameters.TryGetProperty("ClipInput", out var input) && input.GetBoolean())
        {
            source = IEffectProvider.InputAnchorGUID.ToString();
        }
        else if (parameters.TryGetProperty("SourceProviderId", out var provider))
        {
            if (parameters.TryGetProperty("SourceOutputFieldId", out var output))
            {
                source = EffectProviderOutputExtensions.CreateOutputSourceId(provider.GetGuid(), output.GetString()!);
            }
            else
            {
                source = provider.GetGuid().ToString();
            }
        }
        else
        {
            if (!parameters.TryGetProperty("SourceId", out var id))
            {
                throw new ArgumentException("A binding source is required.");
            }
            source = id.GetString()!;
        }

        if (source == ValueProviderFrameContext.BuiltInFrameProviderId || source == ValueProviderFrameContext.BuiltInProgressProviderId)
        {
            return source;
        }
        if (!EffectProviderOutputExtensions.TryParseOutputSourceId(source, out var sourceId, out var outputId))
        {
            throw new ArgumentException($"Invalid binding source '{source}'.");
        }
        if (sourceId == IEffectProvider.InputAnchorGUID && outputId is null)
        {
            return sourceId.ToString();
        }
        if (!providers.TryGetValue(sourceId, out var from) || !DynamicEffectBindings.CanBindOutput(from)
            || !from.TryGetOutputField(outputId, out _))
        {
            throw new ArgumentException("Select an available provider output port.");
        }
        return outputId is null ? sourceId.ToString() : EffectProviderOutputExtensions.CreateOutputSourceId(sourceId, outputId);
    }

    private static void SetGuiBinding(IEffectProvider provider, string fieldId, string? source)
    {
        if (string.IsNullOrWhiteSpace(fieldId) || EffectProviderAnchorExtensions.IsOutputBindingKey(fieldId))
        {
            throw new ArgumentException("Select an input field.");
        }
        bool declared = DynamicEffectBindings.InputFields(provider).TryGetValue(fieldId, out var field);
        if (!declared && (source is not null || !provider.AnchorsBindingState.ContainsKey(fieldId)))
        {
            throw new ArgumentException($"Unknown input field '{fieldId}'.");
        }
        if (source is not null && field!.FieldType.HasFlag(EffectArgumentFieldType.CannotBeDynamic))
        {
            throw new ArgumentException($"Field '{fieldId}' cannot be bound.");
        }
        if (fieldId == EffectProviderAnchorExtensions.InputKey)
        {
            if (source is null)
            {
                provider.DisconnectMainInput();
            }
            else
            {
                provider.SetMainInputSource(source);
            }
        }
        else if (source is null)
        {
            provider.ClearFieldBinding(fieldId);
        }
        else
        {
            provider.SetFieldBinding(fieldId, source);
        }
    }

    private static void PruneGuiEffectGroups(List<EffectGroup> groups, IReadOnlyDictionary<Guid, IEffectProvider> providers)
    {
        foreach (var group in groups)
        {
            group.ProviderIds.RemoveAll(id => !providers.ContainsKey(id));
            group.Ports.RemoveAll(p => !providers.ContainsKey(p.ProviderId));
        }
        while (true)
        {
            var empty = groups.Where(g => g.ProviderIds.Count == 0 && g.ChildGroupIds.Count == 0)
                .Select(g => g.Id).ToHashSet();
            if (empty.Count == 0)
            {
                break;
            }
            groups.RemoveAll(g => empty.Contains(g.Id));
            foreach (var group in groups)
            {
                group.ChildGroupIds.RemoveAll(empty.Contains);
            }
        }
    }

    private async Task<object?> ExecuteGuiEffectOperationAsync(GuiProjectOperation operation, JsonElement parameters,
        CancellationToken ct, Action? deferRendering = null, GuiEffectClip? batchClip = null)
    {
        bool HasParameter(string key) => parameters.TryGetProperty(key, out _);
        string? GetString(string key) => parameters.TryGetProperty(key, out var value) ? value.GetString() : null;
        Guid GetGuid(string key) => parameters.TryGetProperty(key, out var value) ? value.GetGuid() : Guid.Empty;
        bool GetBoolean(string key) => parameters.TryGetProperty(key, out var value) && value.GetBoolean();
        double GetDouble(string key, double fallback = 0) => parameters.TryGetProperty(key, out var value) ? value.GetDouble() : fallback;

        List<Guid> GetIds(string key)
        {
            var ids = new List<Guid>();
            if (parameters.TryGetProperty(key, out var value))
            {
                foreach (var item in value.EnumerateArray())
                {
                    ids.Add(item.GetGuid());
                }
            }
            return ids;
        }

        EffectPipeline GetPipeline()
        {
            if (!HasParameter("Pipeline"))
            {
                return EffectPipeline.Picture;
            }
            if (!Enum.TryParse<EffectPipeline>(GetString("Pipeline"), true, out var pipeline) || !Enum.IsDefined(pipeline))
            {
                throw new ArgumentException("Invalid pipeline.");
            }
            return pipeline;
        }

        Stage GetStage()
        {
            if (!HasParameter("Stage")) return GetPipeline() == EffectPipeline.NativeContent ? Stage.VectorPicture : Stage.Picture;
            return Enum.TryParse<Stage>(GetString("Stage"), true, out var stage) && Enum.IsDefined(stage)
                ? stage : throw new ArgumentException("Invalid stage.");
        }

        Dictionary<string, object> GetFields()
        {
            var fields = new Dictionary<string, object>();
            if (parameters.TryGetProperty("Fields", out var value))
            {
                foreach (var field in value.EnumerateObject())
                {
                    fields.Add(field.Name, field.Value.Clone());
                }
            }
            return fields;
        }

        if (operation == GuiProjectOperation.GetEffectGroupTemplate)
        {
            return (await EffectGroupStore.LoadAsync()).Where(x => !HasParameter("Hash") || x.Hash.Equals(GetString("Hash"), StringComparison.OrdinalIgnoreCase))
                .Select(x => new { x.Hash, x.Name, x.Error, TemplateJson = Encoding.UTF8.GetString(x.Json) }).ToArray();
        }

        var clip = batchClip ?? FindGuiEffectClip(GetGuid("ClipId"), false);
        if (clip is null)
        {
            throw new KeyNotFoundException("Clip not found.");
        }

        if (operation is GuiProjectOperation.GetClipArgument or GuiProjectOperation.SetClipArgument
            && HasParameter("RequiredClipType") && clip.ClipType.ToString() != GetString("RequiredClipType"))
            throw new ArgumentException($"Clip is not a {GetString("RequiredClipType")}.");

        string GetGraphState()
        {
            return JsonSerializer.Serialize(new
            {
                Providers = (clip.Live?.EffectProviders ?? clip.EffectProviders)?.Values.Select(EffectBindingHelper.SerializeProvider).ToArray(),
                Groups = (clip.Live?.ExtraData ?? clip.ExtraData).GetValueOrDefault(EffectGroupHelper.MetadataKey),
            });
        }

        string initialState = GetGraphState();
        // Work on independent providers so rejected edits do not alter the live graph or its static fallbacks.
        var providers = CopyGuiEffectProviders(clip);
        var groups = EffectGroupHelper.Read(clip.ExtraData);
        IEffectProvider GetProvider(Guid id)
        {
            if (!providers.TryGetValue(id, out var provider) || provider is null)
            {
                throw new KeyNotFoundException($"Effect provider {id} not found.");
            }
            return provider;
        }

        ClipArgumentProvider GetClipArguments()
        {
            var arguments = providers.Values.OfType<ClipArgumentProvider>().SingleOrDefault();
            if (arguments is null)
            {
                arguments = CreateGuiClipArguments(clip, Guid.NewGuid());
                providers.Add(arguments.Id, arguments);
            }
            return arguments;
        }

        IEffectProvider GetTargetProvider()
        {
            if (HasParameter("ProviderId"))
            {
                return GetProvider(GetGuid("ProviderId"));
            }
            return GetClipArguments();
        }

        EffectGroup GetGroup()
        {
            var group = groups.SingleOrDefault(g => g.Id == GetGuid("GroupId"));
            if (group is null)
            {
                throw new KeyNotFoundException("Effect group not found.");
            }
            return group;
        }

        switch (operation)
        {
            case GuiProjectOperation.GetClipEffectStep:
                return GuiEffectSteps(clip.Id, GetProvider(GetGuid("ProviderId")) as IKeyFramedEffectProvider
                    ?? throw new ArgumentException("Provider does not support keyframes."),
                    HasParameter("StepIndex") ? parameters.GetProperty("StepIndex").GetInt32() : null);
            case GuiProjectOperation.GetClipEffectGraph:
                if (GetBoolean("AsMermaid"))
                {
                    return EffectBindingHelper.GenerateStoredMermaidDiagram(providers, groups);
                }
                return GuiEffectGraph(clip, providers, groups, await EffectFieldTypeResolution.ResolveAsync(providers, ct));
            case GuiProjectOperation.GetClipEffectBinding:
                {
                    var bindings = new List<object>();
                    foreach (var provider in providers.Values)
                    {
                        if (HasParameter("ProviderId") && provider.Id != GetGuid("ProviderId"))
                        {
                            continue;
                        }
                        foreach (var binding in provider.AnchorsBindingState)
                        {
                            if (EffectProviderAnchorExtensions.IsOutputBindingKey(binding.Key) || binding.Value == IEffectProvider.NoConnectionGUID.ToString())
                            {
                                continue;
                            }
                            if (HasParameter("FieldId") && binding.Key != GetString("FieldId"))
                            {
                                continue;
                            }
                            bindings.Add(new
                            {
                                ClipId = clip.Id,
                                ProviderId = provider.Id,
                                FieldId = binding.Key,
                                SourceId = binding.Value,
                                Stage = provider.Stage.ToString(),
                                SupportedStages = provider.SupportedStages.Select(s => s.ToString()).ToArray(),
                                Pipeline = provider.GetPipeline().ToString(),
                            });
                        }
                    }
                    return bindings.ToArray();
                }
            case GuiProjectOperation.GetClipEffectBindingSource:
                {
                    IEffectProvider? target = null;
                    if (HasParameter("FieldId"))
                    {
                        target = GetTargetProvider();
                    }
                    else if (HasParameter("ProviderId"))
                    {
                        target = GetProvider(GetGuid("ProviderId"));
                    }
                    return await GuiBindingSourcesAsync(clip, providers, target, GetString("FieldId"), GetStage(), HasParameter("Stage") || HasParameter("Pipeline"), ct);
                }
            case GuiProjectOperation.GetClipArgument:
                {
                    bool persisted = providers.Values.Any(x => x is ClipArgumentProvider);
                    var arguments = GetClipArguments();
                    return new
                    {
                        ClipId = clip.Id,
                        ProviderId = persisted ? (Guid?)arguments.Id : null,
                        Fields = arguments.Fields.Where(f => !HasParameter("FieldId") || f.Key == GetString("FieldId"))
                            .Select(f => GuiEffectField(arguments, f.Key, f.Value)).ToArray(),
                    };
                }
            case GuiProjectOperation.GetClipValueEndpoint:
                {
                    var endpoints = new List<object>();
                    if (GetBoolean("Available"))
                    {
                        foreach (var sourceClip in GetGuiEffectClips(batchClip is not null))
                        {
                            if (sourceClip.Id != clip.Id && !GuiClipPrecedes(sourceClip, clip))
                            {
                                continue;
                            }
                            var sourceProviders = sourceClip.Id == clip.Id ? providers : sourceClip.EffectProviders;
                            if (sourceProviders is null)
                            {
                                continue;
                            }
                            foreach (var endpoint in sourceProviders.Values.OfType<IClipValueEndpointProvider>())
                            {
                                if (!endpoint.IsExport || !endpoint.CanPassTo(GetStage(), sourceClip.Id == clip.Id, providers.Values))
                                {
                                    continue;
                                }
                                if (HasParameter("EndpointId") && endpoint.Id != GetGuid("EndpointId"))
                                {
                                    continue;
                                }
                                endpoints.Add(GuiEndpoint(sourceClip, endpoint, sourceProviders));
                            }
                        }
                    }
                    else
                    {
                        foreach (var endpoint in providers.Values.OfType<IClipValueEndpointProvider>())
                        {
                            if (HasParameter("EndpointId") && endpoint.Id != GetGuid("EndpointId"))
                            {
                                continue;
                            }
                            if (HasParameter("Pipeline") && endpoint.GetPipeline() != GetPipeline())
                            {
                                continue;
                            }
                            endpoints.Add(GuiEndpoint(clip, endpoint, providers));
                        }
                    }
                    return endpoints.ToArray();
                }
            case GuiProjectOperation.GetClipEffectGroup:
                return groups.Where(g => !HasParameter("GroupId") || g.Id == GetGuid("GroupId")).Select(g => GuiEffectGroup(clip.Id, g)).ToArray();
            case GuiProjectOperation.GetClipEffectGroupTemplate:
                {
                    var template = EffectGroupHelper.Export(GetGroup(), groups, providers);
                    return Encoding.UTF8.GetString(EffectGroupStore.Serialize(template));
                }
            case GuiProjectOperation.SaveClipEffectGroup:
                {
                    var template = EffectGroupHelper.Export(GetGroup(), groups, providers);
                    string hash = await EffectGroupStore.SaveAsync(EffectGroupStore.Serialize(template), GetString("ReplaceHash")?.ToLowerInvariant());
                    return new { ClipId = clip.Id, GroupId = GetGuid("GroupId"), Hash = hash };
                }
        }

        var before = GuiBindingErrors(providers, await EffectFieldTypeResolution.ResolveAsync(providers, ct)).ToHashSet();
        Guid resultProvider = Guid.Empty, resultGroup = Guid.Empty;
        bool endpointResult = false, stepResult = false;
        switch (operation)
        {
            case GuiProjectOperation.AddClipEffectStep:
            case GuiProjectOperation.SetClipEffectStep:
            case GuiProjectOperation.RemoveClipEffectStep:
                {
                    var provider = GetProvider(GetGuid("ProviderId")) as IKeyFramedEffectProvider
                        ?? throw new ArgumentException("Provider does not support keyframes.");
                    if (operation == GuiProjectOperation.RemoveClipEffectStep)
                        provider.RemoveStep(parameters.GetProperty("StepIndex").GetInt32());
                    else
                    {
                        bool add = operation == GuiProjectOperation.AddClipEffectStep;
                        int index = add ? 0 : parameters.GetProperty("StepIndex").GetInt32();
                        var step = add ? provider.CreateStep(provider.Steps.Count == 0 ? 0
                            : Math.Min(1, provider.Steps.Max(s => s.Progress) + 0.1)) : provider.Steps[index];
                        step.SetValues(GetFields());
                        step.ValidateStatic();
                        if (add) provider.AddStep(step);
                        else provider.UpdateStep(index, step);
                    }
                    resultProvider = provider.Id;
                    stepResult = true;
                    break;
                }
            case GuiProjectOperation.AddClipEffectProvider:
            case GuiProjectOperation.SetClipEffectProvider:
                {
                    bool add = operation == GuiProjectOperation.AddClipEffectProvider;
                    if (add && GetString("TypeName") == ClipArgumentProvider.ProviderTypeName && providers.Values.Any(x => x is ClipArgumentProvider))
                    {
                        throw new ArgumentException("Clip argument provider already exists.");
                    }
                    IEffectProvider provider;
                    if (!add)
                    {
                        provider = GetProvider(GetGuid("ProviderId"));
                    }
                    else if (GetString("TypeName") == ClipArgumentProvider.ProviderTypeName)
                    {
                        provider = GetClipArguments();
                    }
                    else
                    {
                        if (!EffectServices.GetAvailableEffectProviders().TryGetValue(GetString("TypeName") ?? "", out var factory))
                        {
                            throw new KeyNotFoundException("Effect provider type not found.");
                        }
                        provider = factory();
                    }
                    if (add && provider is not ClipArgumentProvider)
                    {
                        provider.Id = Guid.NewGuid();
                    }
                    if (HasParameter("Metadata"))
                    {
                        foreach (var value in parameters.GetProperty("Metadata").EnumerateObject())
                        {
                            switch (value.Value.ValueKind)
                            {
                                case JsonValueKind.Null:
                                    provider.MetaData.Remove(value.Name);
                                    break;
                                case JsonValueKind.Object:
                                case JsonValueKind.Array:
                                    provider.MetaData[value.Name] = value.Value.Clone();
                                    break;
                                default:
                                    provider.MetaData[value.Name] = EffectParamConvert.Normalize(value.Value.Clone())!;
                                    break;
                            }
                        }
                    }
                    if (HasParameter("Stage"))
                    {
                        if (!Enum.TryParse<Stage>(GetString("Stage"), true, out var stage) || !Enum.IsDefined(stage))
                            throw new ArgumentException("Invalid stage.");
                        provider.Stage = stage;
                    }
                    else if (HasParameter("Pipeline"))
                    {
                        var pipeline = GetPipeline();
                        provider.SetPipeline(pipeline);
                        if (provider.GetPipeline() != pipeline)
                        {
                            throw new ArgumentException("Provider has a fixed pipeline.");
                        }
                    }
                    ValidateGuiProviderTarget(clip, provider);
                    ApplyGuiProviderFields(provider, GetFields(), GetBoolean("ResetToDefaults"));
                    if (HasParameter("Name"))
                    {
                        provider.Name = GetString("Name")!;
                    }
                    if (HasParameter("Enabled"))
                    {
                        provider.Enabled = GetBoolean("Enabled");
                    }
                    if (add)
                    {
                        providers[provider.Id] = provider;
                        if (!GetBoolean("NoAutoConnect") && provider is not ClipArgumentProvider)
                        {
                            EffectBindingHelper.AutoConnectProviderToOutput(providers, provider, clip.GetEffectTarget());
                        }
                    }
                    if (HasParameter("InputProviderId"))
                    {
                        Guid inputProviderId = GetGuid("InputProviderId");
                        string? source = null;
                        if (inputProviderId != IEffectProvider.NoConnectionGUID)
                        {
                            source = GuiBindingSource(JsonSerializer.SerializeToElement(new { SourceProviderId = inputProviderId }), providers);
                        }
                        SetGuiBinding(provider, EffectProviderAnchorExtensions.InputKey, source);
                    }
                    if (HasParameter("IsFinalOutput"))
                    {
                        if (GetBoolean("IsFinalOutput"))
                        {
                            EffectBindingHelper.SetFinalOutput(providers, provider.Id, provider.Stage);
                        }
                        else
                        {
                            provider.SetFinalOutputSource(false);
                        }
                    }
                    resultProvider = provider.Id;
                    break;
                }
            case GuiProjectOperation.RemoveClipEffectProvider:
            case GuiProjectOperation.RemoveClipValueEndpoint:
                {
                    Guid id = GetGuid(operation == GuiProjectOperation.RemoveClipValueEndpoint ? "EndpointId" : "ProviderId");
                    if (operation == GuiProjectOperation.RemoveClipValueEndpoint && GetProvider(id) is not IClipValueEndpointProvider)
                    {
                        throw new ArgumentException("Provider is not a clip endpoint.");
                    }
                    if (!EffectBindingHelper.RemoveProvider(providers, id))
                    {
                        throw new KeyNotFoundException("Effect provider not found.");
                    }
                    PruneGuiEffectGroups(groups, providers);
                    break;
                }
            case GuiProjectOperation.SetClipArgument:
                {
                    var arguments = GetClipArguments();
                    var values = GetFields();
                    foreach (var id in values.Keys.ToArray())
                    {
                        if (!id.StartsWith(Render.ClipsAndTracks.SubtitleClip.ArgumentPrefix, StringComparison.Ordinal)) continue;
                        if (!arguments.InFields.TryGetValue(id, out var descriptor)) throw new ArgumentException($"Unknown clip argument '{id}'.");
                        if (!Render.RenderAPIBase.Text.TextStyleFieldHelper.TryNormalizeValue(descriptor, values[id], out var value, out var error))
                            throw new ArgumentException($"Invalid clip argument '{id}': {error}");
                        values[id] = value;
                    }
                    ApplyGuiProviderFields(arguments, values, GetBoolean("ResetToDefaults"));
                    resultProvider = arguments.Id;
                    break;
                }
            case GuiProjectOperation.SetClipEffectBinding:
            case GuiProjectOperation.RemoveClipEffectBinding:
                {
                    var target = GetTargetProvider();
                    string? source = null;
                    if (operation == GuiProjectOperation.SetClipEffectBinding)
                    {
                        source = GuiBindingSource(parameters, providers);
                    }
                    SetGuiBinding(target, GetString("FieldId") ?? "", source);
                    resultProvider = target.Id;
                    break;
                }
            case GuiProjectOperation.ClearClipEffectBinding:
                if (HasParameter("ProviderId"))
                {
                    var provider = GetProvider(GetGuid("ProviderId"));
                    if (HasParameter("Pipeline") && provider.GetPipeline() != GetPipeline())
                    {
                        throw new ArgumentException("Provider is in another pipeline.");
                    }
                    if (HasParameter("OutputFieldId") && !provider.TryGetOutputField(GetString("OutputFieldId"), out _))
                    {
                        throw new ArgumentException("Output port not found.");
                    }

                    bool MatchesSource(string source)
                    {
                        if (!EffectProviderOutputExtensions.TryParseOutputSourceId(source, out var id, out var output) || id != provider.Id)
                        {
                            return false;
                        }
                        if (!HasParameter("OutputFieldId") || output == GetString("OutputFieldId"))
                        {
                            return true;
                        }
                        return output is null && provider is not IMultipleOutputEffectProvider;
                    }

                    foreach (var target in providers.Values)
                    {
                        foreach (var binding in target.AnchorsBindingState.ToArray())
                        {
                            if (EffectProviderAnchorExtensions.IsOutputBindingKey(binding.Key))
                            {
                                continue;
                            }
                            if (MatchesSource(binding.Value) || !HasParameter("OutputFieldId") && target.Id == provider.Id)
                            {
                                SetGuiBinding(target, binding.Key, null);
                            }
                        }
                    }
                    if (provider.IsFinalOutputSource() && (!HasParameter("OutputFieldId") || provider.GetFinalOutputFieldId() == GetString("OutputFieldId")
                        || provider.GetFinalOutputFieldId() is null && provider is not IMultipleOutputEffectProvider))
                    {
                        provider.SetFinalOutputSource(false);
                    }
                }
                else
                {
                    if (HasParameter("OutputFieldId"))
                    {
                        throw new ArgumentException("OutputFieldId requires ProviderId.");
                    }
                    foreach (var provider in providers.Values)
                    {
                        if (!HasParameter("Pipeline") || provider.GetPipeline() == GetPipeline())
                        {
                            provider.AnchorsBindingState = new Dictionary<string, string>();
                        }
                    }
                }
                break;
            case GuiProjectOperation.SetClipEffectOutput:
                if (HasParameter("ProviderId"))
                {
                    var provider = GetProvider(GetGuid("ProviderId"));
                    if (HasParameter("Pipeline") && provider.GetPipeline() != GetPipeline())
                    {
                        throw new ArgumentException("Provider is in another pipeline.");
                    }
                    EffectBindingHelper.SetFinalOutput(providers, provider.Id, provider.Stage, GetString("OutputFieldId"),
                        await EffectFieldTypeResolution.ResolveAsync(providers, ct));
                }
                else
                {
                    if (HasParameter("OutputFieldId"))
                    {
                        throw new ArgumentException("OutputFieldId requires ProviderId.");
                    }
                    EffectBindingHelper.SetFinalOutput(providers, null, GetStage());
                }
                break;
            case GuiProjectOperation.AddClipValueEndpoint:
            case GuiProjectOperation.SetClipValueEndpoint:
                {
                    bool add = operation == GuiProjectOperation.AddClipValueEndpoint;
                    IClipValueEndpointProvider endpoint;
                    if (add)
                    {
                        if (HasParameter("SourceClipId"))
                        {
                            endpoint = new ClipValueImportProvider();
                        }
                        else
                        {
                            endpoint = new ClipValueExportProvider();
                        }
                    }
                    else
                    {
                        if (GetProvider(GetGuid("EndpointId")) is not IClipValueEndpointProvider existing)
                        {
                            throw new ArgumentException("Provider is not a clip endpoint.");
                        }
                        endpoint = existing;
                    }
                    if (HasParameter("Name"))
                    {
                        endpoint.Name = GetString("Name")!;
                    }
                    if (HasParameter("Enabled"))
                    {
                        endpoint.Enabled = GetBoolean("Enabled");
                    }
                    if (HasParameter("Pipeline") || add)
                    {
                        endpoint.Stage = GetStage();
                    }
                    if (endpoint.IsExport)
                    {
                        if (HasParameter("AllowCrossClip"))
                        {
                            endpoint.AllowCrossClip = GetBoolean("AllowCrossClip");
                        }
                        if (HasParameter("SourceClipId") || HasParameter("SourceEndpointId"))
                        {
                            throw new ArgumentException("Output endpoints do not have a source endpoint.");
                        }
                        if (HasParameter("ValueType"))
                        {
                            if (!Enum.TryParse<EffectArgumentFieldType>(GetString("ValueType"), true, out var valueType))
                            {
                                throw new ArgumentException("Invalid endpoint value type.");
                            }
                            switch (valueType)
                            {
                                case EffectArgumentFieldType.Numeric:
                                case EffectArgumentFieldType.Integer:
                                case EffectArgumentFieldType.UnsignedInteger:
                                case EffectArgumentFieldType.Long:
                                case EffectArgumentFieldType.UnsignedLong:
                                case EffectArgumentFieldType.Boolean:
                                case EffectArgumentFieldType.String:
                                case EffectArgumentFieldType.Color:
                                case EffectArgumentFieldType.Size:
                                case EffectArgumentFieldType.Position:
                                case EffectArgumentFieldType.SizeAndPosition:
                                case EffectArgumentFieldType.IPicture:
                                case EffectArgumentFieldType.VectorPicture:
                                    break;
                                default:
                                    throw new ArgumentException("Invalid endpoint value type.");
                            }
                            endpoint.ValueType = valueType;
                        }
                    }
                    else
                    {
                        if (HasParameter("ValueType"))
                        {
                            throw new ArgumentException("Input endpoint type is determined by its source.");
                        }
                        if (HasParameter("SourceClipId"))
                        {
                            endpoint.SourceClipId = GetGuid("SourceClipId");
                        }
                        if (HasParameter("SourceEndpointId"))
                        {
                            endpoint.SourceEndpointId = GetGuid("SourceEndpointId");
                        }
                        if (add || HasParameter("SourceClipId") || HasParameter("SourceEndpointId") || HasParameter("Pipeline"))
                        {
                            if (FindGuiEffectClip(endpoint.SourceClipId, clip.Live is null) is not { } source)
                            {
                                throw new KeyNotFoundException("Source clip not found.");
                            }
                            var sourceProviders = source.Id == clip.Id ? providers : source.EffectProviders;
                            var output = sourceProviders?.GetValueOrDefault(endpoint.SourceEndpointId) as IClipValueEndpointProvider;
                            if (output is null || !output.IsExport || !output.Enabled
                                || !output.CanPassTo(endpoint.Stage, source.Id == clip.Id, providers.Values)
                                || source.Id != clip.Id && !GuiClipPrecedes(source, clip))
                            {
                                throw new ArgumentException("Source endpoint is unavailable in this clip and pipeline.");
                            }
                            endpoint.ValueType = output.ValueType;
                        }
                    }
                    if (endpoint.ValueType.IsContent() && endpoint.ValueType != endpoint.Stage.GetContentType())
                    {
                        throw new ArgumentException("Endpoint content type belongs to another pipeline.");
                    }
                    ValidateGuiProviderTarget(clip, endpoint);
                    providers[endpoint.Id] = endpoint;
                    resultProvider = endpoint.Id;
                    endpointResult = true;
                    break;
                }
            case GuiProjectOperation.AddClipEffectGroup:
            case GuiProjectOperation.SetClipEffectGroup:
                {
                    bool add = operation == GuiProjectOperation.AddClipEffectGroup;
                    EffectGroup group;
                    if (add)
                    {
                        group = new EffectGroup
                        {
                            ProviderIds = GetIds("ProviderIds"),
                            ChildGroupIds = GetIds("ChildGroupIds"),
                        };
                    }
                    else
                    {
                        group = GetGroup();
                    }
                    if (HasParameter("Name"))
                    {
                        group.Name = GetString("Name")!.Trim();
                    }
                    if (string.IsNullOrWhiteSpace(group.Name))
                    {
                        throw new ArgumentException("Group name is required.");
                    }
                    if (add)
                    {
                        var parents = new HashSet<Guid?>();
                        foreach (var id in group.ProviderIds.Concat(group.ChildGroupIds))
                        {
                            var parent = groups.SingleOrDefault(g => g.ProviderIds.Contains(id) || g.ChildGroupIds.Contains(id));
                            parents.Add(parent?.Id);
                        }
                        if (parents.Count != 1)
                        {
                            throw new ArgumentException("Select providers and groups with the same parent.");
                        }
                        var parentId = parents.Single();
                        if (parentId.HasValue)
                        {
                            var parent = groups.Single(g => g.Id == parentId.Value);
                            parent.ProviderIds.RemoveAll(group.ProviderIds.Contains);
                            parent.ChildGroupIds.RemoveAll(group.ChildGroupIds.Contains);
                            parent.ChildGroupIds.Add(group.Id);
                        }
                        groups.Add(group);
                    }
                    double dx = GetDouble("X", group.X) - group.X;
                    double dy = GetDouble("Y", group.Y) - group.Y;
                    group.X += dx;
                    group.Y += dy;
                    if (!add && (dx != 0 || dy != 0))
                    {
                        MoveGuiEffectGroup(group, groups, providers, dx, dy);
                    }
                    if (HasParameter("Collapsed"))
                    {
                        group.Collapsed = GetBoolean("Collapsed");
                    }
                    if (HasParameter("Ports"))
                    {
                        group.Ports = JsonSerializer.Deserialize<List<EffectGroupPort>>(parameters.GetProperty("Ports").GetRawText(),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new ArgumentException("Ports must be an array.");
                    }
                    resultGroup = group.Id;
                    break;
                }
            case GuiProjectOperation.RemoveClipEffectGroup:
                {
                    var group = GetGroup();
                    if (GetBoolean("RemoveProviders"))
                    {
                        var members = EffectGroupHelper.Members(group, groups);
                        var removed = new HashSet<Guid>();
                        void CollectGroupIds(EffectGroup child)
                        {
                            removed.Add(child.Id);
                            foreach (var id in child.ChildGroupIds)
                            {
                                CollectGroupIds(groups.Single(g => g.Id == id));
                            }
                        }
                        CollectGroupIds(group);
                        foreach (var id in members)
                        {
                            EffectBindingHelper.RemoveProvider(providers, id);
                        }
                        groups.RemoveAll(g => removed.Contains(g.Id));
                        foreach (var remaining in groups)
                        {
                            remaining.ChildGroupIds.RemoveAll(removed.Contains);
                        }
                        PruneGuiEffectGroups(groups, providers);
                    }
                    else
                    {
                        foreach (var parent in groups.Where(g => g.ChildGroupIds.Contains(group.Id)))
                        {
                            parent.ChildGroupIds.Remove(group.Id);
                            parent.ChildGroupIds.AddRange(group.ChildGroupIds);
                            parent.ProviderIds.AddRange(group.ProviderIds);
                        }
                        groups.Remove(group);
                    }
                    break;
                }
            case GuiProjectOperation.ImportClipEffectGroup:
                {
                    byte[] json;
                    if (HasParameter("Hash"))
                    {
                        var item = (await EffectGroupStore.LoadAsync()).SingleOrDefault(x => x.Hash.Equals(GetString("Hash"), StringComparison.OrdinalIgnoreCase));
                        if (item is null)
                        {
                            throw new KeyNotFoundException("Effect group template not found.");
                        }
                        json = item.Json;
                    }
                    else
                    {
                        json = Encoding.UTF8.GetBytes(GetString("TemplateJson") ?? "");
                    }
                    var template = EffectGroupHelper.Instantiate(EffectGroupStore.Parse(json));
                    var restored = EffectGroupStore.Restore(template);
                    foreach (var provider in restored.Values)
                    {
                        ValidateGuiProviderTarget(clip, provider);
                        providers.Add(provider.Id, provider);
                    }
                    groups.AddRange(template.Groups);
                    var root = template.Groups.Single(g => g.Id == template.RootGroupId);
                    double dx = GetDouble("X", root.X) - root.X;
                    double dy = GetDouble("Y", root.Y) - root.Y;
                    root.X += dx;
                    root.Y += dy;
                    MoveGuiEffectGroup(root, groups, providers, dx, dy);
                    resultGroup = root.Id;
                    break;
                }
            default:
                throw new NotSupportedException("Unknown effect graph operation.");
        }

        ct.ThrowIfCancellationRequested();
        EffectGroupHelper.EnsureBoundaryPorts(groups, providers);
        EffectGroupHelper.Validate(groups, providers);
        EffectBindingHelper.MaterializeFields(providers.Values);
        var types = await EffectFieldTypeResolution.ResolveAsync(providers, ct);
        if (clip.Live is not null && (!ReferenceEquals(Clips.GetValueOrDefault(clip.Id), clip.Live) || initialState != GetGraphState()))
        {
            throw new InvalidOperationException("The clip effect graph changed during the request. Read it again before retrying.");
        }
        var errors = GuiBindingErrors(providers, types);
        var introduced = errors.Where(e => e.Code != "BrokenActivePath" && !before.Contains(e)).ToArray();
        if (introduced.Length != 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, introduced.Select(e => e.Message)));
        }
        if (clip.Live is { } live)
        {
            var effects = errors.Count == 0 ? EffectBindingHelper.RebuildAllEffects(providers, live.Effects) : null;
            live.EffectProviders = providers;
            ClipInfoBuilder.SaveTextStyleClipArguments(live);
            if (effects is not null) live.Effects = effects;
            EffectGroupHelper.Write(live.ExtraData, groups);
            EffectGroupBindingGuard.Capture(live);
        }
        else
        {
            SaveGuiBatchTextArguments(clip.Dto, providers);
            clip.Dto.EffectProviders = providers.Values.Select(EffectBindingHelper.SerializeProvider).ToArray();
            EffectGroupHelper.Write(clip.ExtraData, groups);
            _guiBatch!.Put(clip.Dto);
        }
        bool shouldDeferRendering = errors.Count > 0;
        if (!shouldDeferRendering)
        {
            foreach (var currentClip in GetGuiEffectClips(batchClip is not null))
            {
                var currentProviders = currentClip.EffectProviders;
                if (currentProviders is null)
                {
                    continue;
                }
                if (currentProviders.Values.OfType<IClipValueEndpointProvider>()
                    .Any(endpoint => GuiEndpointStatus(currentClip, endpoint, currentProviders) is not null))
                {
                    shouldDeferRendering = true;
                    break;
                }
            }
        }
        if (shouldDeferRendering)
        {
            deferRendering?.Invoke();
        }
        if (errors.Count > 0 && batchClip is null)
        {
            Logger.Log($"Effect graph on clip {clip.Id} is incomplete: {string.Join(" ", errors.Select(e => e.Message))}", "warning");
        }
        if (batchClip is null) Logger.Log($"GUI RPC {operation} updated clip {clip.Id}: {providers.Count} providers, {groups.Count} groups, {errors.Count} binding diagnostics.", "info");
        if (clip.Live is not null) await RefreshPropertyPanelAsync(clip.Live);
        if (resultProvider != Guid.Empty)
        {
            if (stepResult) return GuiEffectSteps(clip.Id, (IKeyFramedEffectProvider)providers[resultProvider]);
            if (endpointResult)
            {
                return GuiEndpoint(clip, (IClipValueEndpointProvider)providers[resultProvider], providers);
            }
            return GuiProvider(providers[resultProvider], clip.Id, types: types);
        }
        if (resultGroup != Guid.Empty)
        {
            return GuiEffectGroup(clip.Id, groups.Single(g => g.Id == resultGroup));
        }
        if (operation == GuiProjectOperation.ClearClipEffectBinding || operation == GuiProjectOperation.SetClipEffectOutput)
        {
            return GuiEffectGraph(clip, providers, groups, types);
        }
        return null;
    }

    private static void MoveGuiEffectGroup(EffectGroup root, List<EffectGroup> groups,
        Dictionary<Guid, IEffectProvider> providers, double dx, double dy)
    {
        const string xKey = "__DraftEffectBindingView_InteractiveEditorX__";
        const string yKey = "__DraftEffectBindingView_InteractiveEditorY__";

        foreach (var id in root.ChildGroupIds)
        {
            var child = groups.Single(g => g.Id == id);
            child.X += dx;
            child.Y += dy;
            MoveGuiEffectGroup(child, groups, providers, dx, dy);
        }
        foreach (var id in root.ProviderIds)
        {
            var provider = providers[id];
            provider.MetaData[xKey] = Convert.ToDouble(EffectParamConvert.Normalize(provider.MetaData.GetValueOrDefault(xKey)) ?? 0,
                CultureInfo.InvariantCulture) + dx;
            provider.MetaData[yKey] = Convert.ToDouble(EffectParamConvert.Normalize(provider.MetaData.GetValueOrDefault(yKey)) ?? 0,
                CultureInfo.InvariantCulture) + dy;
        }
    }

    private static async Task<object[]> GuiBindingSourcesAsync(GuiEffectClip clip, Dictionary<Guid, IEffectProvider> providers,
        IEffectProvider? target, string? fieldId, Stage pipeline, bool filterPipeline, CancellationToken ct)
    {
        if (filterPipeline && !clip.SupportedStages.Contains(pipeline))
        {
            throw new ArgumentException("Clip does not support the native content pipeline.");
        }
        if (fieldId is not null && (target is null || !DynamicEffectBindings.InputFields(target).ContainsKey(fieldId)))
        {
            throw new ArgumentException("Target input field not found.");
        }
        var types = await EffectFieldTypeResolution.ResolveAsync(providers, ct);
        var inputPipeline = target?.Stage ?? pipeline;
        var sources = new List<(string SourceId, Guid? ProviderId, string? OutputFieldId,
            string Name, EffectArgumentFieldType FieldType, Stage Pipeline)>
        {
            (ValueProviderFrameContext.BuiltInFrameProviderId, null, null, "Frame", EffectArgumentFieldType.Numeric, pipeline),
            (ValueProviderFrameContext.BuiltInProgressProviderId, null, null, "Progress", EffectArgumentFieldType.Numeric, pipeline),
            (IEffectProvider.InputAnchorGUID.ToString(), IEffectProvider.InputAnchorGUID, null, "Clip input", inputPipeline.GetContentType(), inputPipeline),
        };
        foreach (var provider in providers.Values)
        {
            if (provider.Id == target?.Id || !DynamicEffectBindings.CanBindOutput(provider))
            {
                continue;
            }
            if (provider.Stage != inputPipeline || filterPipeline && provider.Stage != pipeline)
            {
                continue;
            }
            foreach (var output in provider.GetOutputFields().Values)
            {
                string sourceId = provider.Id.ToString();
                if (provider is IMultipleOutputEffectProvider)
                {
                    sourceId = EffectProviderOutputExtensions.CreateOutputSourceId(provider.Id, output.Id);
                }
                sources.Add((sourceId, provider.Id, output.Id, provider.Name, types.GetOutputType(provider, output.Id), provider.Stage));
            }
        }

        return await Task.Run(() =>
        {
            var result = new List<object>();
            foreach (var source in sources)
            {
                if (fieldId is not null)
                {
                    if (DynamicEffectBindings.InputFields(target!)[fieldId].FieldType.HasFlag(EffectArgumentFieldType.CannotBeDynamic))
                    {
                        continue;
                    }
                    var candidate = types.ResolveBindingCandidate(target!, fieldId, source.SourceId);
                    if (candidate is null || candidate.Diagnostics.Count != 0 || !candidate.AreInputsCompatible(target!))
                    {
                        continue;
                    }
                    if (fieldId == EffectProviderAnchorExtensions.InputKey && source.ProviderId.HasValue
                        && providers.TryGetValue(source.ProviderId.Value, out var from) && !from.CanConnectContent(target!, source.OutputFieldId, candidate))
                    {
                        continue;
                    }
                }
                result.Add(new
                {
                    ClipId = clip.Id,
                    SourceId = source.SourceId,
                    SourceProviderId = source.ProviderId,
                    SourceOutputFieldId = source.OutputFieldId,
                    source.Name,
                    FieldType = source.FieldType.ToString(),
                    Stage = source.Pipeline.ToString(),
                    Pipeline = (source.Pipeline is Stage.Text or Stage.VectorComponent or Stage.VectorPicture ? EffectPipeline.NativeContent : EffectPipeline.Picture).ToString(),
                });
            }
            return result.ToArray();
        }, ct);
    }
}
