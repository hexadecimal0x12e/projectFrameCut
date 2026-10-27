using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationAPIBase.Project;
using projectFrameCut.Asset;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.RPCProtocol;
using projectFrameCut.Render.Sources;
using projectFrameCut.Render.Subtitles;
using projectFrameCut.Render.VectorContent;
using projectFrameCut.Render.VectorContent.Components;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace projectFrameCut;

public partial class DraftPage
{
    #region Protocol and connect
    private readonly SemaphoreSlim _guiRegistrationGate = new(1, 1);
    private Guid _guiSessionId;
    private IRenderClient? _guiClient;

    #region external RPC
    private bool? externalRpcUiAllowed;
    private int externalRpcMenuIndex = -1;

    private void UpdateExternalRpcUi(bool allowed)
    {
        if (externalRpcUiAllowed == allowed) return;
        externalRpcUiAllowed = allowed;
        if (externalRpcMenuIndex < 0) externalRpcMenuIndex = ProjectNameMenuBarItem.IndexOf(CreateRpcPipeMenuItem);
        if (allowed)
        {
            if (!ProjectNameMenuBarItem.Contains(CreateRpcPipeMenuItem))
                ProjectNameMenuBarItem.Insert(Math.Clamp(externalRpcMenuIndex, 0, ProjectNameMenuBarItem.Count), CreateRpcPipeMenuItem);
        }
        else ProjectNameMenuBarItem.Remove(CreateRpcPipeMenuItem);
        if (QuickCommands.Count > 0) RefreshQuickCommands();
    }

    private async Task<Guid> EnsureGuiRpcSessionAsync(IRenderClient client, CancellationToken ct)
    {
        await _guiRegistrationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (AlreadyDisappeared) throw new InvalidOperationException("Project is closed.");
            if (ReferenceEquals(_guiClient, client) && _guiSessionId != Guid.Empty) return _guiSessionId;
            var id = Guid.NewGuid();
            await client.RegisterGuiProjectAsync(new() { SessionId = id }, ct).ConfigureAwait(false);
            _guiClient = client;
            _guiSessionId = id;
            Log($"Registered GUI RPC session {id}.");
            return id;
        }
        finally { _guiRegistrationGate.Release(); }
    }

    private async Task MonitorGuiRpcAsync(CancellationToken ct)
    {
        if (!RenderRpcBootstrap.SupportsCliRenderProcess) return;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!RenderRpcBootstrap.TryGetClient(out var client) || client is null)
                    { await Task.Delay(1000, ct); continue; }
                    var id = await EnsureGuiRpcSessionAsync(client, ct).ConfigureAwait(false);
                    var work = await client.GetGuiProjectWorkAsync(new() { SessionId = id }, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(work.ConnectedClientName))
                    {
                        SetStatusText($"External RPC client connected: {work.ConnectedClientName}");
                        continue;
                    }
                    if (work.Request is null) continue;
                    var result = new GuiProjectResult { SessionId = id, RequestId = work.Request.RequestId };
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        result.Json = await MainThread.InvokeOnMainThreadAsync(async () =>
                        {
                            if (AlreadyDisappeared || id != _guiSessionId || ct.IsCancellationRequested)
                                throw new InvalidOperationException("Project session is closed.");
                            Log($"GUI RPC {work.Request.Operation}, request {work.Request.RequestId}.");
                            return JsonSerializer.Serialize(await ExecuteGuiProjectAsync(work.Request, ct));
                        });
                    }
                    catch (Exception ex)
                    {
                        Log(ex, $"GUI RPC {work.Request.Operation}", this);
                        result.Error = new RemoteError(ex);
                    }
                    await client.CompleteGuiProjectWorkAsync(result, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Log(ex, "GUI RPC request loop", this);
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            if (_guiClient is not null && _guiSessionId != Guid.Empty)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await _guiClient.UnregisterGuiProjectAsync(new() { SessionId = _guiSessionId }, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) { Log(ex, "Unregister GUI RPC session", this); }
                _guiSessionId = Guid.Empty;
                _guiClient = null;
            }
        }
    }

    private object GuiClipSnapshot(ClipElementUI clip)
    {
        var dto = DraftImportAndExportHelper.ExportClipElementFromDraftPage(this, clip, false);
        return new
        {
            ClipId = dto.Id,
            dto.Name,
            Type = dto.ClipType.ToString(),
            TrackId = dto.LayerIndex,
            dto.StartFrame,
            DurationFrames = dto.Duration,
            SourceStartFrame = dto.RelativeStartFrame,
            dto.FilePath,
            WidthPixels = dto.TargetWidth,
            HeightPixels = dto.TargetHeight,
            XPixels = dto.TargetX,
            YPixels = dto.TargetY
        };
    }

    private static object GuiField(IEffectArgumentField field) => new
    {
        FieldId = field.Id,
        FieldType = field.FieldType.ToString(),
        field.DefaultValue,
        field.MinValue,
        field.MaxValue,
        field.PresetOptions,
        field.Remarks,
        Value = field switch { StaticEffectArgumentField f => f.Value, DynamicEffectParamField f => f.StaticFallbackValue, _ => null },
    };

    private static object GuiProvider(IEffectProvider provider, Guid clipId, string? registrationKey = null, EffectFieldTypeResolution? types = null) => new
    {
        ClipId = clipId,
        ProviderId = provider.Id,
        provider.Name,
        TypeName = registrationKey ?? provider.TypeName,
        provider.FromPlugin,
        provider.Enabled,
        Target = provider.Target.ToString(),
        EffectType = provider.TypeOfEffect.ToString(),
        Pipeline = provider.GetPipeline().ToString(),
        InputProviderId = provider.InFields.ContainsKey(EffectProviderAnchorExtensions.InputKey) ? provider.GetMainInputSource() : null,
        IsFinalOutput = provider.IsFinalOutputSource(),
        OutputFieldId = provider.GetFinalOutputFieldId(),
        Fields = provider.Fields.Select(f => GuiEffectField(provider, f.Key, f.Value, types)).ToArray(),
        InFields = provider.InFields.Select(f => GuiEffectField(provider, f.Key, f.Value, types)).ToArray(),
        OutFields = provider.GetOutputFields().Select(f => GuiEffectField(provider, f.Key, f.Value, types, false)).ToArray(),
        provider.AnchorsBindingState,
        provider.MetaData,
        SupportsImplementTypes = provider.SupportsImplementTypes.Select(t => t.ToString()).ToArray(),
        SupportsKeyFrames = provider is IKeyFramedEffectProvider,
    };

    private async Task<object?> ExecuteGuiProjectAsync(GuiProjectRequest request, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(request.ParametersJson);
        var p = document.RootElement;
        if (p.ValueKind != JsonValueKind.Object) throw new ArgumentException("Parameters must be an object.");
        bool Has(string key) => p.TryGetProperty(key, out _);
        string? S(string key) => Has(key) ? p.GetProperty(key).GetString() : null;
        int I(string key, int fallback = 0) => Has(key) ? p.GetProperty(key).GetInt32() : fallback;
        uint U(string key, uint fallback = 0) => Has(key) ? p.GetProperty(key).GetUInt32() : fallback;
        Guid G(string key) => Has(key) ? p.GetProperty(key).GetGuid() : Guid.Empty;
        Dictionary<string, object> Fields() => Has("Fields")
            ? p.GetProperty("Fields").EnumerateObject().ToDictionary(x => x.Name, x => EffectParamConvert.Normalize(x.Value.Clone())!) : new();
        ClipElementUI Clip() => Clips.TryGetValue(G("ClipId"), out var c) ? c : throw new KeyNotFoundException("Clip not found.");
        IEffectProvider Factory() => EffectServices.GetAvailableEffectProviders().TryGetValue(S("TypeName") ?? "", out var f)
            ? f() : throw new KeyNotFoundException("Effect provider type not found.");
        object Info() => new
        {
            SessionId = _guiSessionId,
            Name = ProjectInfo.ProjectName,
            WidthPixels = ProjectInfo.RelativeWidth,
            HeightPixels = ProjectInfo.RelativeHeight,
            FrameRate = ProjectInfo.TargetFrameRate,
            DurationFrames = ProjectDuration,
            ClipCount = Clips.Count,
            TrackCount = Tracks.Count,
            AssetCount = Assets.Count,
            WorkingPath,
            CurrentFrame,
            CurrentSnapshotId = CurrentSnapshotID,
        };
        foreach (var key in new[] { "StartFrame", "SourceStartFrame", "DurationFrames", "TrackId" })
            if (Has(key) && (U(key) > int.MaxValue || (key == "DurationFrames" && U(key) == 0)))
                throw new ArgumentException($"Invalid {key}.");
        foreach (var key in new[] { "WidthPixels", "HeightPixels" })
            if (Has(key) && I(key) <= 0) throw new ArgumentException($"Invalid {key}.");
        var operation = request.Operation;
        bool write = !operation.ToString().StartsWith("Get", StringComparison.Ordinal);
        if (write && IsReadonly) throw new InvalidOperationException("Project is read-only.");
        object? result;
        bool deferEffectRendering = false;
        switch (operation)
        {
            case GuiProjectOperation.GetProjectHistory:
                return GetProjectHistory();
            case GuiProjectOperation.GetClipProperties:
            case GuiProjectOperation.SetClipProperties:
            case GuiProjectOperation.GetClipGeometry:
            case GuiProjectOperation.SetClipGeometry:
            case GuiProjectOperation.GetMaskSourceType:
                result = await ExecuteGuiClipConfigurationAsync(operation, new(p));
                if (!write) return result;
                break;
            case GuiProjectOperation.AddSolidColorClip:
            case GuiProjectOperation.AddMaskClip:
            case GuiProjectOperation.AddEmptyVideoClip:
                result = ExecuteGuiSourceClipOperation(operation, new(p));
                break;
            case GuiProjectOperation.GetSubtitleClip:
            case GuiProjectOperation.AddSubtitleClip:
            case GuiProjectOperation.SetSubtitleClip:
            case GuiProjectOperation.GetSubtitleCue:
            case GuiProjectOperation.AddSubtitleCue:
            case GuiProjectOperation.SetSubtitleCue:
            case GuiProjectOperation.RemoveSubtitleCue:
                result = ExecuteGuiSubtitleOperation(operation, new(p));
                if (!write) return result;
                break;
            case GuiProjectOperation.SplitClip:
            case GuiProjectOperation.JoinClip:
            case GuiProjectOperation.SplitClipGroup:
            case GuiProjectOperation.RemoveTrack:
                result = await ExecuteGuiTimelineOperationAsync(operation, new(p));
                break;
            case GuiProjectOperation.GetExternalSource:
            case GuiProjectOperation.AddExternalSource:
            case GuiProjectOperation.SetExternalSource:
            case GuiProjectOperation.RemoveExternalSource:
            case GuiProjectOperation.AddExternalSourceClip:
                result = await ExecuteGuiExternalSourceOperationAsync(operation, new(p), cancellationToken);
                if (!write) return result;
                break;
            case GuiProjectOperation.GetTransformType:
            case GuiProjectOperation.GetClipTransform:
            case GuiProjectOperation.SetClipTransform:
            case GuiProjectOperation.RemoveClipTransform:
            case GuiProjectOperation.GetClipTransformPreview:
                result = await ExecuteGuiTransformOperationAsync(operation, new(p), cancellationToken);
                if (!write) return result;
                break;
            case GuiProjectOperation.GetSettings:
            case GuiProjectOperation.SetSettings:
                result = ExecuteGuiSettingsOperation(operation, new(p));
                if (!write) return result;
                break;
            case GuiProjectOperation.UndoProjectHistory:
                return await ApplyProjectHistorySnapshotAsync(GetPreviousSlot()?.SnapshotID
                    ?? throw new InvalidOperationException("No earlier project history snapshot is available."), true);
            case GuiProjectOperation.RedoProjectHistory:
                {
                    var next = GetNextSlots().Where(x => FindSlotDirectory(x.SnapshotID) is not null)
                        .OrderByDescending(x => x.SavedAtUtc).FirstOrDefault()
                        ?? throw new InvalidOperationException("No later project history snapshot is available.");
                    return await ApplyProjectHistorySnapshotAsync(next.SnapshotID, true);
                }
            case GuiProjectOperation.RestoreProjectHistory:
                return await ApplyProjectHistorySnapshotAsync(G("SnapshotId"), false);
            case GuiProjectOperation.GetInfo: return Info();
            case GuiProjectOperation.GetFramePreview:
            case GuiProjectOperation.GetClipFramePreview:
                return await GetGuiPreviewAsync(request, p, cancellationToken);
            case GuiProjectOperation.Save:
                await Save(args: CreateGuiProjectChange(request), throwOnFailure: true);
                return Info();
            case GuiProjectOperation.GetClip:
                return Clips.Values.Where(c => (!Has("ClipId") || c.Id == G("ClipId"))
                    && (!Has("TrackId") || c.origTrack == I("TrackId"))
                    && (!Has("Name") || c.DisplayName.Contains(S("Name")!, StringComparison.OrdinalIgnoreCase)))
                    .Select(GuiClipSnapshot).ToArray();
            case GuiProjectOperation.AddClip:
                {
                    if (!Tracks.ContainsKey(I("TrackId"))) throw new ArgumentException("Track not found.");
                    if (Has("FilePath") && Has("AssetId")) throw new ArgumentException("Choose FilePath or AssetId.");
                    ClipElementUI c;
                    if (Has("AssetId"))
                    {
                        if (!Assets.TryGetValue(S("AssetId")!, out var asset)) throw new KeyNotFoundException("Asset not found.");
                        c = CreateFromAsset(asset, I("TrackId"), FrameToPixel(U("StartFrame")));
                    }
                    else
                    {
                        var path = S("FilePath");
                        if (path is not null && !File.Exists(path)) throw new FileNotFoundException("Source file not found.", path);
                        c = CreateAndAddClip(FrameToPixel(U("StartFrame")), FrameToPixel(U("DurationFrames", 300)), I("TrackId"),
                            labelText: S("Name") ?? "New Clip", relativeStart: U("SourceStartFrame"));
                        if (path is not null)
                        {
                            c.SourcePath = path;
                            c.ClipType = ClipElementUI.DetermineClipMode(path);
                            if (c.ClipType is ClipMode.VideoClip or ClipMode.AudioClip) c.UpdateSourceDuration();
                            if (c.ClipType == ClipMode.VideoClip) EnsureGeneratedSoundTrack(c);
                        }
                    }
                    result = GuiClipSnapshot(ApplyGuiClipParameters(c, p));
                    break;
                }
            case GuiProjectOperation.SetClip: result = GuiClipSnapshot(ApplyGuiClipParameters(Clip(), p)); break;
            case GuiProjectOperation.CopyClip:
                {
                    var dto = DraftImportAndExportHelper.ExportClipElementFromDraftPage(this, Clip(), false);
                    dto.Id = Guid.NewGuid();
                    dto.BindedSoundTrack = string.Empty;
                    dto.EffectProviders = TransformProcessing.CopySingleInputs(dto.EffectProviders);
                    dto.MetaData?.Remove(SoundTrackMetadata.ProbeSourceKey);
                    dto.MetaData?.Remove(SoundTrackMetadata.ProbeHasStreamKey);
                    dto.Name = S("Name") ?? $"Copy of {dto.Name}";
                    dto.StartFrame = U("StartFrame", checked(dto.StartFrame + dto.Duration));
                    dto.LayerIndex = U("TrackId", dto.LayerIndex);
                    result = GuiClipSnapshot(TimelineMcpLiveService.ReplaceClip(this, dto));
                    break;
                }
            case GuiProjectOperation.RemoveClip:
                if (!TimelineMcpLiveService.DeleteClip(this, Clip().Id)) throw new InvalidOperationException("Clip could not be removed.");
                result = null; break;
            case GuiProjectOperation.GetTrack:
                return Tracks.Keys.OrderBy(x => x).Where(x => !Has("TrackId") || x == I("TrackId"))
                    .Select(x => new { TrackId = x, ClipCount = Clips.Values.Count(c => c.origTrack == x) }).ToArray();
            case GuiProjectOperation.AddTrack:
            case GuiProjectOperation.AddSubTrack:
                {
                    bool useSubTrack = operation == GuiProjectOperation.AddSubTrack;
                    int id = I("TrackId", Tracks.Keys.Where(k => (k >= SubTrackOffset) == useSubTrack)
                        .DefaultIfEmpty(useSubTrack ? SubTrackOffset - 1 : -1).Max() + 1);
                    if (id < 0 || (id >= SubTrackOffset) != useSubTrack)
                        throw new ArgumentOutOfRangeException("TrackId", useSubTrack
                            ? "Auxiliary track IDs must be 10000 or greater."
                            : "Main track IDs must be between 0 and 9999.");
                    if (Tracks.ContainsKey(id)) throw new ArgumentException("Track already exists.", "TrackId");
                    AddATrack(id); result = new { TrackId = id }; break;
                }
            case GuiProjectOperation.GetAsset:
                return Assets.Where(a => (!Has("AssetId") || a.Key == S("AssetId")) && (!Has("Name") || (a.Value.Name ?? "").Contains(S("Name")!, StringComparison.OrdinalIgnoreCase)))
                    .Select(a => new { AssetId = a.Key, a.Value.Name, a.Value.Path, Type = a.Value.AssetType.ToString(), a.Value.Width, a.Value.Height, a.Value.Duration }).ToArray();
            case GuiProjectOperation.AddAsset:
                {
                    var source = S("FilePath") ?? throw new ArgumentException("FilePath is required.");
                    if (!File.Exists(source)) throw new FileNotFoundException("Asset file not found.", source);
                    var asset = await AddAsset(source, copyToProject: Has("CopyToProject") ? p.GetProperty("CopyToProject").GetBoolean() : null, name: S("Name"))
                        ?? throw new InvalidOperationException("Asset import was cancelled or failed.");
                    result = new { asset.AssetId, asset.Name, asset.Path }; break;
                }
            case GuiProjectOperation.RemoveAsset:
                if (!Assets.TryRemove(S("AssetId") ?? "", out _)) throw new KeyNotFoundException("Asset not found.");
                result = null; break;
            case GuiProjectOperation.GetTextStyle:
                return PluginManager.LoadedPlugins.Values.OfType<IApplicationPluginBase>().SelectMany(x => x.TextClipStyleProvider)
                    .Select(x => new { StyleId = x.Key, FromPlugin = x.Value().FromPlugin }).ToArray();
            case GuiProjectOperation.GetTextStyleField:
                return (TimelineMcpLiveService.ResolveTextStyleProvider(S("StyleId") ?? "") ?? throw new KeyNotFoundException("Text style not found."))
                    .SettableFields.Values.Select(f => new { FieldId = f.Id, FieldType = f.FieldType.ToString(), f.DefaultValue, f.MinValue, f.MaxValue, f.PresetOptions, f.Remarks }).ToArray();
            case GuiProjectOperation.AddTextClip:
                {
                    if (!Tracks.ContainsKey(I("TrackId"))) throw new ArgumentException("Track not found.");
                    var c = TimelineMcpLiveService.AddTextClipToPage(this, S("StyleId") ?? "", S("Text") ?? "",
                        0, I("TrackId"), Fields(), strictFields: true);
                    result = GuiClipSnapshot(ApplyGuiClipParameters(c, p)); break;
                }
            case GuiProjectOperation.SetTextClipStyle:
                TimelineMcpLiveService.SetTextClipStyleFields(this, Clip().Id, Fields(), strictFields: true);
                result = GuiClipSnapshot(Clip()); break;
            case GuiProjectOperation.GetVectorComponentType:
            case GuiProjectOperation.GetVectorClip:
            case GuiProjectOperation.GetVectorComponent:
                return ExecuteGuiVectorOperation(operation, p, cancellationToken);
            case GuiProjectOperation.AddVectorCanvasClip:
            case GuiProjectOperation.AddVectorComponentClip:
            case GuiProjectOperation.SetVectorClip:
            case GuiProjectOperation.AddVectorComponent:
            case GuiProjectOperation.SetVectorComponent:
            case GuiProjectOperation.RemoveVectorComponent:
                result = ExecuteGuiVectorOperation(operation, p, cancellationToken);
                break;
            case GuiProjectOperation.GetEffectProviderType:
                return EffectServices.GetAvailableEffectProviders().Select(x => (Key: x.Key, Provider: x.Value()))
                    .Where(x => !Has("Name") || x.Key.Contains(S("Name")!, StringComparison.OrdinalIgnoreCase)
                        || x.Provider.Name.Contains(S("Name")!, StringComparison.OrdinalIgnoreCase))
                    .Select(x => GuiProvider(x.Provider, Guid.Empty, x.Key)).ToArray();
            case GuiProjectOperation.GetEffectProviderField: return Factory().Fields.Values.Select(GuiField).ToArray();
            case GuiProjectOperation.GetClipEffectGraph:
            case GuiProjectOperation.GetClipEffectStep:
            case GuiProjectOperation.GetClipEffectBinding:
            case GuiProjectOperation.GetClipEffectBindingSource:
            case GuiProjectOperation.GetClipArgument:
            case GuiProjectOperation.GetClipValueEndpoint:
            case GuiProjectOperation.GetClipEffectGroup:
            case GuiProjectOperation.GetClipEffectGroupTemplate:
            case GuiProjectOperation.GetEffectGroupTemplate:
                return await ExecuteGuiEffectOperationAsync(operation, p, cancellationToken);
            case GuiProjectOperation.GetClipEffectProvider:
                return (Clip().EffectProviders?.Values.AsEnumerable() ?? Enumerable.Empty<IEffectProvider>())
                    .Where(x => (!Has("ProviderId") || x.Id == G("ProviderId")) && (!Has("TypeName") || x.TypeName == S("TypeName")))
                    .Select(x => GuiProvider(x, G("ClipId"))).ToArray();
            case GuiProjectOperation.AddClipEffectProvider:
            case GuiProjectOperation.AddClipEffectStep:
            case GuiProjectOperation.SetClipEffectStep:
            case GuiProjectOperation.RemoveClipEffectStep:
            case GuiProjectOperation.SetClipEffectProvider:
            case GuiProjectOperation.RemoveClipEffectProvider:
            case GuiProjectOperation.SetClipEffectBinding:
            case GuiProjectOperation.RemoveClipEffectBinding:
            case GuiProjectOperation.ClearClipEffectBinding:
            case GuiProjectOperation.SetClipEffectOutput:
            case GuiProjectOperation.SetClipArgument:
            case GuiProjectOperation.AddClipValueEndpoint:
            case GuiProjectOperation.SetClipValueEndpoint:
            case GuiProjectOperation.RemoveClipValueEndpoint:
            case GuiProjectOperation.AddClipEffectGroup:
            case GuiProjectOperation.SetClipEffectGroup:
            case GuiProjectOperation.RemoveClipEffectGroup:
            case GuiProjectOperation.ImportClipEffectGroup:
            case GuiProjectOperation.SaveClipEffectGroup:
                result = await ExecuteGuiEffectOperationAsync(operation, p, cancellationToken, () => deferEffectRendering = true);
                break;
            default: throw new NotSupportedException("Unknown GUI project operation.");
        }
        var change = CreateGuiProjectChange(request);
        if (deferEffectRendering) change.DetailInfo = "__EFFECT_BINDING_CHANGED__";
        ForwardClipChangeToWorkspace(this, change);
        await DraftChangedAsync(this, change);
        return result;
    }

    private static ClipUpdateEventArgs CreateGuiProjectChange(GuiProjectRequest request) => new()
    {
        ChangeReason = string.IsNullOrWhiteSpace(request.ChangeReason) ? $"RPC {request.Operation}" : request.ChangeReason.Trim(),
        DetailInfo = $"RPC {request.Operation}",
        Operator = ClipChangeOperatorKind.ExternalRPC,
        OperatorDetailName = string.IsNullOrWhiteSpace(request.ClientName) ? "RPC client" : request.ClientName.Trim(),
    };

    private ClipElementUI ApplyGuiClipParameters(ClipElementUI clip, JsonElement parameters)
    {
        var node = JsonSerializer.SerializeToNode(DraftImportAndExportHelper.ExportClipElementFromDraftPage(this, clip, false))!;
        foreach (var (source, target) in new[] { ("Name", "Name"), ("StartFrame", "StartFrame"), ("DurationFrames", "Duration"),
            ("SourceStartFrame", "RelativeStartFrame"), ("TrackId", "LayerIndex"), ("FilePath", "FilePath"),
            ("WidthPixels", "TargetWidth"), ("HeightPixels", "TargetHeight"), ("XPixels", "TargetX"), ("YPixels", "TargetY") })
            if (parameters.TryGetProperty(source, out var value)) node[target] = JsonNode.Parse(value.GetRawText());
        var dto = node.Deserialize<ClipDraftDTO>()!;
        if (dto.Duration == 0 || dto.LayerIndex > int.MaxValue || !Tracks.ContainsKey((int)dto.LayerIndex)) throw new ArgumentException("Invalid duration or track.");
        return TimelineMcpLiveService.ReplaceClip(this, dto);
    }

    private static void ApplyGuiProviderFields(IEffectProvider provider, Dictionary<string, object> values, bool reset)
    {
        var bindings = reset ? provider.EnumerateFieldBindings().Select(x => x.Key).ToArray() : values.Keys.ToArray();
        if (reset && provider is ClipArgumentProvider arguments) arguments.RestoreStaticValues(null);
        var fields = provider.Fields;
        if (reset && provider is not ClipArgumentProvider)
        {
            var defaults = EffectServices.GetAvailableEffectProviders()[provider.TypeName]();
            defaults.MetaData = new(provider.MetaData);
            fields = defaults.Fields;
        }
        foreach (var (id, raw) in values)
        {
            if (!fields.TryGetValue(id, out var f)) throw new ArgumentException($"Unknown effect field '{id}'.");
            var value = f.FieldType.HasFlag(EffectArgumentFieldType.Any) ? raw : EffectParamConvert.Normalize(raw);
            if (f.FieldType.HasFlag(EffectArgumentFieldType.Any) && value is JsonElement json) value = json.Clone();
            object? converted = f.FieldType.BaseType() switch
            {
                EffectArgumentFieldType.Integer => Convert.ToInt32(value, CultureInfo.InvariantCulture),
                EffectArgumentFieldType.UnsignedInteger => Convert.ToUInt16(value, CultureInfo.InvariantCulture),
                EffectArgumentFieldType.Numeric => Convert.ToSingle(value, CultureInfo.InvariantCulture),
                EffectArgumentFieldType.Boolean => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
                EffectArgumentFieldType.Long => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                EffectArgumentFieldType.UnsignedLong => Convert.ToUInt64(value, CultureInfo.InvariantCulture),
                EffectArgumentFieldType.String => value?.ToString(),
                _ => value,
            };
            if (f.PresetOptions is { Length: > 0 } && converted is string text && !f.PresetOptions.Contains(text)) throw new ArgumentException($"Invalid option for '{id}'.");
            fields[id] = GuiStaticField(id, f, converted);
        }
        provider.Fields = provider is ClipArgumentProvider ? fields.Where(f => values.ContainsKey(f.Key)).ToDictionary() : fields;
        foreach (var id in bindings) provider.ClearFieldBinding(id);
    }

    private async Task MonitorExternalRpcRequestsAsync(CancellationToken ct)
    {
        IRenderClient? checkedClient = null;
        var supported = false;
        bool? lastAllowed = null;
        bool? lastRpcSourcesAllowed = null;
        bool? lastAssemblySourcesAllowed = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                var sources = ExternalSourceSettings.Load(MauiProgram.BasicDataPath);
                if (lastRpcSourcesAllowed != sources.AllowRpcSources || lastAssemblySourcesAllowed != sources.AllowAssemblySources)
                {
                    var changed = lastRpcSourcesAllowed.HasValue;
                    var assemblyChanged = lastAssemblySourcesAllowed.HasValue && lastAssemblySourcesAllowed != sources.AllowAssemblySources;
                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        if (AlreadyDisappeared || AddClipView is null) return;
                        if (assemblyChanged) await ProjectExternalSourceService.ApplyAsync(WorkingPath);
                        await AddClipView.RefreshExternalSourcesAsync();
                        if (changed) await RefreshPreviewFromCurrentProviderAsync();
                    });
                    lastRpcSourcesAllowed = sources.AllowRpcSources;
                    lastAssemblySourcesAllowed = sources.AllowAssemblySources;
                }
                var allowed = ExternalRpcPipeSettings.Load(MauiProgram.BasicDataPath).AllowExternalRPC;
                if (lastAllowed != allowed)
                {
                    lastAllowed = allowed;
                    checkedClient = null;
                    supported = false;
                }
                if (externalRpcUiAllowed != allowed)
                {
                    await MainThread.InvokeOnMainThreadAsync(() => UpdateExternalRpcUi(allowed));
                }
                if (!allowed) continue;
                if (AlreadyDisappeared || !RenderRpcBootstrap.SupportsCliRenderProcess ||
                    !RenderRpcBootstrap.TryGetClient(out var client) || client is null) continue;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                if (!ReferenceEquals(checkedClient, client))
                {
                    supported = (await client.GetCapabilitiesAsync(timeout.Token).ConfigureAwait(false))
                        .Operations.Contains(nameof(RenderOperation.GetExternalRpcRequest));
                    checkedClient = client;
                }
                if (!supported) continue;
                var sessionId = await EnsureGuiRpcSessionAsync(client, timeout.Token).ConfigureAwait(false);
                var pending = await client.GetExternalRpcRequestAsync(timeout.Token).ConfigureAwait(false);
                if (pending.ClaimId == Guid.Empty) continue;
                var request = JsonSerializer.Deserialize<ExternalRpcRequest>(pending.Json)!;
                var approved = request.IsPersistent || await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    if (AlreadyDisappeared || ct.IsCancellationRequested) return false;
                    if (!ExternalRpcPipeSettings.Load(MauiProgram.BasicDataPath).AllowExternalRPC) return false;
                    return await DisplayAlertAsync(Localized.DraftPage_ExternalRpcAuthorization_Title(request.AppName),
                        Localized.DraftPage_ExternalRpcAuthorization(request.AppName, request.Author, request.Purpose),
                        Localized._OK, Localized._Cancel);
                }).WaitAsync(ct).ConfigureAwait(false);
                if (AlreadyDisappeared || ct.IsCancellationRequested) return;
                ExternalRpcPipeSettings.EnsureAllowed(MauiProgram.BasicDataPath);
                using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                responseTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                await client.ResolveExternalRpcRequestAsync(new() { ClaimId = pending.ClaimId, Approved = approved, GuiSessionId = sessionId }, responseTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Log(ex, "Authorize external RPC request", this);
                try { await Task.Delay(5000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
    #endregion
    #endregion

    #region Common
    private sealed class GuiEditArgs(JsonElement json)
    {
        public JsonElement Json => json;
        public bool Has(string key) => json.TryGetProperty(key, out _);
        public string? String(string key, string? fallback = null) => Has(key) ? json.GetProperty(key).GetString() : fallback;
        public int Int(string key, int fallback = 0) => Has(key) ? json.GetProperty(key).GetInt32() : fallback;
        public uint UInt(string key, uint fallback = 0) => Has(key) ? json.GetProperty(key).GetUInt32() : fallback;
        public long Long(string key, long fallback = 0) => Has(key) ? json.GetProperty(key).GetInt64() : fallback;
        public bool Bool(string key, bool fallback = false) => Has(key) ? json.GetProperty(key).GetBoolean() : fallback;
        public Guid Guid(string key) => json.GetProperty(key).GetGuid();
        public double Number(string key, double fallback = 0)
        {
            double value = Has(key) ? json.GetProperty(key).GetDouble() : fallback;
            return double.IsFinite(value) ? value : throw new ArgumentException($"{key} must be finite.");
        }
        public Dictionary<string, object> Fields(string key = "Fields") => Has(key)
            ? json.GetProperty(key).EnumerateObject().ToDictionary(x => x.Name, x => EffectParamConvert.Normalize(x.Value.Clone())!) : new();
        public T Enum<T>(string key, T fallback = default) where T : struct, System.Enum => !Has(key) ? fallback
            : System.Enum.TryParse<T>(String(key), true, out var value) && System.Enum.IsDefined(value) ? value
            : throw new ArgumentException($"Invalid {key}.");
    }

    private ClipElementUI GuiEditableClip(Guid id) => Clips.TryGetValue(id, out var clip) && !clip.IsGhost && !clip.IsShadow
        ? clip : throw new KeyNotFoundException("Clip not found.");

    private ClipDraftDTO GuiEditableDto(ClipElementUI clip) => JsonSerializer.Deserialize<ClipDraftDTO>(
        JsonSerializer.Serialize(DraftImportAndExportHelper.ExportClipElementFromDraftPage(this, clip, false, false)))!;

    private static double GuiMetadataNumber(ClipElementUI clip, string key, double fallback = 0) =>
        clip.ExtraData.TryGetValue(key, out var value) && double.TryParse(value?.ToString(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) ? number : fallback;

    private object GuiClipProperties(ClipElementUI clip, Guid? detachedAudioClipId = null)
    {
        var audio = clip.ClipType == ClipMode.VideoClip ? GetBoundSoundTrack(clip) : clip.ClipType == ClipMode.AudioClip ? clip : null;
        return new
        {
            ClipId = clip.Id,
            Name = clip.DisplayName,
            Type = clip.ClipType.ToString(),
            clip.ClipColor,
            FilePath = clip.SourcePath,
            IsInfiniteLength = clip.isInfiniteLength,
            SourceDurationFrames = clip.isInfiniteLength ? (uint?)null : clip.maxFrameCount,
            AllowFreeScaleResize = clip.AllowFreeScaleResize || clip.IsExtraDataOptionIsTrue("AllowFreeScaleResize"),
            ExtendToWholeDraft = clip.IsExtraDataOptionIsTrue("ExtendToWholeDraft"),
            TargetDecoder = clip.ClipType == ClipMode.VideoClip ? (clip.ExtraData.GetValueOrDefault("TargetDecoder")
                ?? clip.ExtraData.GetValueOrDefault("targetdecoder"))?.ToString() ?? "auto" : null,
            ResolvedDecoder = (GetLoadedClipInstance(clip.Id) as VideoClip)?.DecoderName,
            VideoDecoderOptions = clip.ClipType == ClipMode.VideoClip ? new[] { "auto", "DecoderContextFP16", "DecoderContextHWFP16", "HDRDecoderContextFP16" } : null,
            HDRBrightnessOffset = clip.ClipType == ClipMode.VideoClip ? (double?)GuiMetadataNumber(clip, "HDRBrightnessOffset") : null,
            AudioClipId = audio?.Id,
            AudioEnabled = audio is null ? (bool?)null : SoundTrackMetadata.ReadBool(audio.ExtraData, SoundTrackMetadata.EnabledKey, true),
            AudioVolume = audio is null ? (float?)null : SoundTrackMetadata.ReadVolume(audio.ExtraData),
            AudioDetached = clip.IsExtraDataOptionIsTrue(SoundTrackMetadata.DetachedKey),
            DetachedAudioClipId = detachedAudioClipId,
            Red = clip.ClipType == ClipMode.SolidColorClip ? (double?)GuiMetadataNumber(clip, "R", 65535) : null,
            Green = clip.ClipType == ClipMode.SolidColorClip ? (double?)GuiMetadataNumber(clip, "G", 65535) : null,
            Blue = clip.ClipType == ClipMode.SolidColorClip ? (double?)GuiMetadataNumber(clip, "B", 65535) : null,
            Alpha = clip.ClipType == ClipMode.SolidColorClip ? (double?)GuiMetadataNumber(clip, "A", 1) : null,
            MaskSourceType = clip.ClipType == ClipMode.MaskClip ? clip.ExtraData.GetValueOrDefault(MaskClip.SourceTypeKey)?.ToString() ?? "Blur" : null,
            SourceArguments = clip.ClipType == ClipMode.MaskClip ? (CopyGuiEffectProviders(clip).Values.OfType<ClipArgumentProvider>().SingleOrDefault()
                ?? CreateGuiClipArguments(clip, Guid.NewGuid())).Fields.Values
                .Where(f => f.Id.StartsWith(MaskClip.ArgumentPrefix, StringComparison.Ordinal)).Select(GuiField).ToArray() : null,
            AntiAliasMode = clip.SupportsNativeEffects ? clip.ExtraData.GetValueOrDefault("VectorAntiAliasMode")?.ToString() is { Length: > 0 } aa ? aa : "Default" : null,
        };
    }

    private object GuiClipGeometry(ClipElementUI clip)
    {
        int width = clip.TargetWidth > 0 ? clip.TargetWidth : ProjectInfo.RelativeWidth;
        int height = clip.TargetHeight > 0 ? clip.TargetHeight : ProjectInfo.RelativeHeight;
        var crop = ClipInfoBuilder.GetCropGeometry(clip, width, height);
        return new
        {
            ClipId = clip.Id,
            WidthPixels = width,
            HeightPixels = height,
            AutomaticWidth = clip.TargetWidth <= 0,
            AutomaticHeight = clip.TargetHeight <= 0,
            XPixels = clip.TargetX,
            YPixels = clip.TargetY,
            RotationDegrees = clip.Rotation,
            CropEnabled = crop.Enabled,
            CropXPixels = crop.X,
            CropYPixels = crop.Y,
            CropWidthPixels = crop.Width,
            CropHeightPixels = crop.Height,
            CropRotationDegrees = crop.Rotation,
        };
    }

    private void ApplyGuiSourceArguments(ClipElementUI clip, GuiEditArgs a)
    {
        if (a.Has("MaskSourceType"))
        {
            _ = MaskSources.Create(a.String("MaskSourceType")!);
            clip.ExtraData[MaskClip.SourceTypeKey] = a.String("MaskSourceType")!;
            if (clip.EffectProviders?.Values.OfType<ClipArgumentProvider>().SingleOrDefault() is { } existing)
            {
                existing.RestoreStaticValues(existing.StaticValues.Where(x => !x.Key.StartsWith(MaskClip.ArgumentPrefix, StringComparison.Ordinal)).ToDictionary());
                foreach (var key in existing.AnchorsBindingState.Keys.Where(k => k.StartsWith(MaskClip.ArgumentPrefix, StringComparison.Ordinal)).ToArray())
                    existing.ClearFieldBinding(key);
            }
        }
        var provider = ClipInfoBuilder.EnsureClipArgumentProvider(clip, this);
        if (a.Has("SourceArguments")) ApplyGuiProviderFields(provider,
            a.Fields("SourceArguments").ToDictionary(x => MaskClip.ArgumentPrefix + x.Key, x => x.Value), false);
        ClipInfoBuilder.RebuildAllEffects(clip);
    }

    private async Task<object?> ExecuteGuiClipConfigurationAsync(GuiProjectOperation operation, GuiEditArgs a)
    {
        if (operation == GuiProjectOperation.GetMaskSourceType)
            return MaskSources.GetAvailable().Where(s => !a.Has("TypeName") || s.TypeName.Equals(a.String("TypeName"), StringComparison.OrdinalIgnoreCase))
                .Select(s => new { s.TypeName, s.Fields }).ToArray();
        var current = GuiEditableClip(a.Guid("ClipId"));
        if (operation == GuiProjectOperation.GetClipProperties) return GuiClipProperties(current);
        if ((operation is GuiProjectOperation.GetClipGeometry or GuiProjectOperation.SetClipGeometry)
            && (current.ClipType is ClipMode.AudioClip or ClipMode.MarkingClip)) throw new ArgumentException("Clip has no picture geometry.");
        if (operation == GuiProjectOperation.GetClipGeometry) return GuiClipGeometry(current);
        var clip = DraftImportAndExportHelper.ConvertToElement(GuiEditableDto(current));
        if (operation == GuiProjectOperation.SetClipGeometry)
        {
            var crop = ClipInfoBuilder.GetCropGeometry(clip, clip.TargetWidth > 0 ? clip.TargetWidth : ProjectInfo.RelativeWidth,
                clip.TargetHeight > 0 ? clip.TargetHeight : ProjectInfo.RelativeHeight);
            if (new[] { "CropEnabled", "CropXPixels", "CropYPixels", "CropWidthPixels", "CropHeightPixels", "CropRotationDegrees" }.Any(a.Has))
                ClipInfoBuilder.SetCropGeometry(clip, new(a.Bool("CropEnabled", crop.Enabled),
                    a.Int("CropXPixels", crop.X), a.Int("CropYPixels", crop.Y), a.Int("CropWidthPixels", crop.Width),
                    a.Int("CropHeightPixels", crop.Height), (float)a.Number("CropRotationDegrees", crop.Rotation)));
            if (a.Has("WidthPixels")) clip.TargetWidth = a.Int("WidthPixels");
            if (a.Has("HeightPixels")) clip.TargetHeight = a.Int("HeightPixels");
            if (a.Has("XPixels")) clip.TargetX = a.Int("XPixels");
            if (a.Has("YPixels")) clip.TargetY = a.Int("YPixels");
            if (a.Has("RotationDegrees"))
            {
                float angle = (float)a.Number("RotationDegrees");
                if (!float.IsFinite(angle)) throw new ArgumentException("Invalid rotation angle.");
                clip.Rotation = VideoClipRotation.Normalize(angle);
            }
            if (a.Has("WidthPixels") && clip.TargetWidth <= 0 || a.Has("HeightPixels") && clip.TargetHeight <= 0 || !float.IsFinite(clip.Rotation))
                throw new ArgumentException("Invalid clip geometry.");
            if (clip.ClipType == ClipMode.SolidColorClip)
            {
                clip.ExtraData["SolidColorOutputWidth"] = clip.TargetWidth;
                clip.ExtraData["SolidColorOutputHeight"] = clip.TargetHeight;
                clip.ExtraData["SolidColorUseFixedOutputSize"] = true;
            }
            if (clip.EffectProviders?.Values.OfType<ClipArgumentProvider>().SingleOrDefault() is { } arguments)
            {
                var keys = new Dictionary<string, string>
                {
                    ["WidthPixels"] = "TargetWidth",
                    ["HeightPixels"] = "TargetHeight",
                    ["XPixels"] = "TargetX",
                    ["YPixels"] = "TargetY",
                    ["RotationDegrees"] = "Rotation"
                }
                    .Where(x => a.Has(x.Key)).Select(x => x.Value).ToHashSet();
                if (new[] { "CropEnabled", "CropXPixels", "CropYPixels", "CropWidthPixels", "CropHeightPixels", "CropRotationDegrees" }.Any(a.Has)
                    && a.Bool("CropEnabled", crop.Enabled))
                {
                    keys.Add("TargetWidth");
                    keys.Add("TargetHeight");
                }
                arguments.RestoreStaticValues(arguments.StaticValues.Where(x => !keys.Contains(x.Key)).ToDictionary());
                foreach (var key in keys) arguments.ClearFieldBinding(key);
            }
            clip = TimelineMcpLiveService.ReplaceClip(this, GuiEditableDto(clip));
            Logger.Log($"GUI RPC updated geometry of clip {clip.Id}.", "info");
            return GuiClipGeometry(clip);
        }

        if (new[] { "TargetDecoder", "HDRBrightnessOffset" }.Any(a.Has) && clip.ClipType != ClipMode.VideoClip)
            throw new ArgumentException("Video source properties require a video clip.");
        if (new[] { "Red", "Green", "Blue", "Alpha" }.Any(a.Has) && clip.ClipType != ClipMode.SolidColorClip)
            throw new ArgumentException("Color channels require a solid color clip.");
        if (new[] { "MaskSourceType", "SourceArguments" }.Any(a.Has) && clip.ClipType != ClipMode.MaskClip)
            throw new ArgumentException("Mask source properties require a mask clip.");
        var audio = current.ClipType == ClipMode.VideoClip ? GetBoundSoundTrack(current) : current.ClipType == ClipMode.AudioClip ? current : null;
        if (new[] { "AudioEnabled", "AudioVolume", "DetachAudio" }.Any(a.Has) && audio is null)
            throw new ArgumentException("Clip has no audio track.");
        if (a.Bool("DetachAudio") && (clip.ClipType != ClipMode.VideoClip || !SoundTrackMetadata.ReadBool(audio?.ExtraData, SoundTrackMetadata.GeneratedFromVideoKey)))
            throw new ArgumentException("Only a generated video audio track can be detached.");
        if (a.Has("Name")) clip.DisplayName = a.String("Name") ?? throw new ArgumentException("Name cannot be null.");
        if (a.Has("ClipColor"))
        {
            string? color = a.String("ClipColor");
            clip.ClipColor = string.IsNullOrWhiteSpace(color) ? null : Color.FromArgb(color).ToArgbHex();
            clip.ApplyClipColor();
        }
        if (a.Has("AllowFreeScaleResize"))
        {
            clip.AllowFreeScaleResize = a.Bool("AllowFreeScaleResize");
            clip.ExtraData["AllowFreeScaleResize"] = clip.AllowFreeScaleResize;
        }
        if (a.Has("ExtendToWholeDraft"))
        {
            if (a.Bool("ExtendToWholeDraft") && (!clip.isInfiniteLength || clip.origTrack < SubTrackOffset ||
                Clips.Values.Any(c => c.Id != clip.Id && c.origTrack == clip.origTrack && c.ShouldDisplayInUI && !c.IsGhost && !c.IsShadow)))
                throw new ArgumentException("Extending to the whole draft requires an infinite clip alone on a subtrack.");
            clip.ExtraData["ExtendToWholeDraft"] = a.Bool("ExtendToWholeDraft");
        }
        if (a.Has("TargetDecoder"))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(a.String("TargetDecoder"));
            if (clip.SourcePath?.StartsWith("#", StringComparison.Ordinal) == true)
                throw new ArgumentException("External and generated video sources select their own decoder.");
            clip.ExtraData["TargetDecoder"] = a.String("TargetDecoder")!;
            clip.ExtraData.Remove("targetdecoder");
        }
        if (a.Has("HDRBrightnessOffset"))
        {
            double offset = a.Number("HDRBrightnessOffset");
            if (offset is < -1 or > 1) throw new ArgumentException("HDRBrightnessOffset must be between -1 and 1.");
            clip.ExtraData["HDRBrightnessOffset"] = offset;
        }
        foreach (var (key, channel) in new[] { ("Red", "R"), ("Green", "G"), ("Blue", "B") })
            if (a.Has(key)) clip.ExtraData[channel] = checked((ushort)a.Int(key));
        if (a.Has("Alpha"))
        {
            double alpha = a.Number("Alpha");
            if (alpha is < 0 or > 1) throw new ArgumentException("Alpha must be between 0 and 1.");
            clip.ExtraData["A"] = (float)alpha;
        }
        if (a.Has("AntiAliasMode"))
        {
            if (!clip.SupportsNativeEffects) throw new ArgumentException("Clip does not support vector antialiasing.");
            string mode = a.String("AntiAliasMode")!;
            string canonical = new[] { "Default", "None", "SSAA2x", "SSAA4x", "SSAA8x" }
                .FirstOrDefault(x => x.Equals(mode, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("Invalid AntiAliasMode.");
            clip.ExtraData["VectorAntiAliasMode"] = canonical == "Default" ? "" : canonical;
        }
        if (a.Has("MaskSourceType") || a.Has("SourceArguments")) ApplyGuiSourceArguments(clip, a);
        double volume = a.Number("AudioVolume", 1);
        if (volume is < 0 or > 1) throw new ArgumentException("AudioVolume must be between 0 and 1.");
        clip = TimelineMcpLiveService.ReplaceClip(this, GuiEditableDto(clip));
        if (a.Has("AudioEnabled")) SetClipAudioEnabled(clip, a.Bool("AudioEnabled"));
        if (a.Has("AudioVolume")) SetClipAudioVolume(clip, volume);
        Guid? detachedId = null;
        if (a.Bool("DetachAudio"))
        {
            detachedId = GetBoundSoundTrack(clip)?.Id;
            await UnbindClipAudioAsync(clip, notify: false);
        }
        Logger.Log($"GUI RPC updated properties of clip {clip.Id}.", "info");
        return GuiClipProperties(clip, detachedId);
    }

    private ClipDraftDTO NewGuiSourceClip(GuiEditArgs a, ClipMode type, string typeName, string name)
    {
        int track = a.Int("TrackId");
        uint start = a.UInt("StartFrame"), duration = a.UInt("DurationFrames", 300);
        int width = a.Int("WidthPixels", ProjectInfo.RelativeWidth), height = a.Int("HeightPixels", ProjectInfo.RelativeHeight);
        if (!Tracks.ContainsKey(track) || duration == 0 || (ulong)start + duration > int.MaxValue || width <= 0 || height <= 0 || ProjectInfo.TargetFrameRate == 0)
            throw new ArgumentException("Invalid clip track, timing or dimensions.");
        return new()
        {
            Id = Guid.NewGuid(),
            Name = a.String("Name", name)!,
            ClipType = type,
            TypeName = typeName,
            FromPlugin = InternalPluginBase.InternalPluginBaseID,
            LayerIndex = (uint)track,
            SubLayerIndex = (uint)track,
            StartFrame = start,
            Duration = duration,
            RelativeStartFrame = a.UInt("SourceStartFrame"),
            FrameTime = 1f / ProjectInfo.TargetFrameRate,
            IsInfiniteLength = true,
            TargetWidth = width,
            TargetHeight = height,
            MetaData = new()
            {
                [ClipDraftDTO.ProjectFrameRateMetaKey] = ProjectInfo.TargetFrameRate,
                [ClipDraftDTO.FrameSemanticVersionMetaKey] = ClipDraftDTO.CurrentFrameSemanticVersion
            },
        };
    }

    private object ExecuteGuiSourceClipOperation(GuiProjectOperation operation, GuiEditArgs a)
    {
        var dto = NewGuiSourceClip(a, operation == GuiProjectOperation.AddSolidColorClip ? ClipMode.SolidColorClip
            : operation == GuiProjectOperation.AddMaskClip ? ClipMode.MaskClip : ClipMode.VideoClip,
            operation == GuiProjectOperation.AddSolidColorClip ? "SolidColorClip" : operation == GuiProjectOperation.AddMaskClip ? "MaskClip" : "VideoClip", "New Clip");
        if (operation == GuiProjectOperation.AddSolidColorClip)
        {
            dto.MetaData!["R"] = checked((ushort)a.Int("Red", ushort.MaxValue));
            dto.MetaData["G"] = checked((ushort)a.Int("Green", ushort.MaxValue));
            dto.MetaData["B"] = checked((ushort)a.Int("Blue", ushort.MaxValue));
            double alpha = a.Number("Alpha", 1);
            if (alpha is < 0 or > 1) throw new ArgumentException("Alpha must be between 0 and 1.");
            dto.MetaData["A"] = (float)alpha;
            dto.MetaData["SolidColorOutputWidth"] = dto.TargetWidth;
            dto.MetaData["SolidColorOutputHeight"] = dto.TargetHeight;
            dto.MetaData["SolidColorUseFixedOutputSize"] = true;
        }
        else if (operation == GuiProjectOperation.AddMaskClip)
        {
            _ = MaskSources.Create(a.String("SourceType")!);
            dto.MetaData![MaskClip.SourceTypeKey] = a.String("SourceType")!;
            dto.MetaData["AllowFreeScaleResize"] = true;
        }
        else dto.FilePath = EmptyVideoSource.CreatePath(dto.TargetWidth, dto.TargetHeight);
        var clip = DraftImportAndExportHelper.ConvertToElement(dto);
        if (clip.ClipType == ClipMode.MaskClip) ApplyGuiSourceArguments(clip, a);
        else if (operation == GuiProjectOperation.AddEmptyVideoClip) ClipInfoBuilder.EnsureClipArgumentProvider(clip, this);
        clip = TimelineMcpLiveService.ReplaceClip(this, GuiEditableDto(clip));
        Logger.Log($"GUI RPC created {clip.ClipType} {clip.Id}.", "info");
        return GuiClipSnapshot(clip);
    }

    #endregion

    #region External source
    private async Task<object[]> GuiExternalSourcesAsync(GuiEditArgs a, CancellationToken ct)
    {
        if (a.Has("ImportId") && a.Has("ClientId")) throw new ArgumentException("Choose ImportId or ClientId.");
        var result = new List<object>();
        if (!a.Has("ClientId") && ProjectExternalSourceService.AllowAssemblySources)
        {
            var local = await ProjectExternalSourceRuntime.ListAsync(WorkingPath, ct);
            ProjectExternalSourceCatalog? remote = null;
            if (RenderRpcBootstrap.TryGetClient(out var client) && client is not null)
                remote = await client.ListProjectExternalSourcesAsync(new() { ProjectRoot = WorkingPath }, ct);
            foreach (var source in local.Sources.Where(s => !a.Has("ImportId") || s.ImportId == a.Guid("ImportId")))
            {
                var backend = remote?.Sources.FirstOrDefault(s => s.ImportId == source.ImportId);
                result.Add(new
                {
                    Kind = "Project",
                    source.ImportId,
                    source.Name,
                    source.ManifestId,
                    source.Version,
                    source.Author,
                    source.Description,
                    Loaded = source.Loaded && (backend?.Loaded ?? true),
                    Error = string.IsNullOrWhiteSpace(backend?.Error) ? source.Error : backend.Error,
                    VideoSources = source.Sources,
                    source.AudioSources,
                    source.MaskSources,
                });
            }
        }
        if (!a.Has("ImportId") && ProjectExternalSourceService.AllowRpcSources && RenderRpcBootstrap.TryGetClient(out var rpc) && rpc is not null)
        {
            var clients = await rpc.ListExternalVideoSourceClientsAsync(ct);
            foreach (var client in clients.Clients.Where(c => !a.Has("ClientId") || c.ClientId == a.Guid("ClientId")))
                result.Add(new
                {
                    Kind = "Rpc",
                    client.ClientId,
                    Name = client.ClientName,
                    client.Loaded,
                    VideoSources = client.Sources,
                    client.AudioSources,
                    client.MaskSources
                });
        }
        return result.ToArray();
    }

    private async Task<object?> ExecuteGuiExternalSourceOperationAsync(GuiProjectOperation operation, GuiEditArgs a, CancellationToken ct)
    {
        if (operation == GuiProjectOperation.GetExternalSource) return await GuiExternalSourcesAsync(a, ct);
        if (operation == GuiProjectOperation.AddExternalSourceClip) return await AddGuiExternalSourceClipAsync(a, ct);
        if (a.Has("ImportId") && a.Has("ClientId")) throw new ArgumentException("Choose ImportId or ClientId.");
        if (operation == GuiProjectOperation.AddExternalSource)
        {
            ExternalSourceSettings.EnsureAssemblySourcesAllowed(MauiProgram.BasicDataPath);
            var copyToProject = a.Has("CopyToProject") ? a.Bool("CopyToProject") : await AssetDatabase.PickImportModeAsync(this);
            if (copyToProject is null) throw new OperationCanceledException("External source import was cancelled.");
            var asset = await ProjectExternalSourceService.ImportAsync(this, a.String("FilePath")!,
                a.Has("ReplaceImportId") ? a.Guid("ReplaceImportId") : null, a.Bool("Loaded", true), ct, copyToProject.Value);
            await AddClipView.RefreshExternalSourcesAsync();
            var query = new GuiEditArgs(System.Text.Json.JsonSerializer.SerializeToElement(new { asset.ImportId }));
            return (await GuiExternalSourcesAsync(query, ct)).Single();
        }
        if (a.Has("ClientId"))
        {
            await ProjectExternalSourceService.ManageRpcClientAsync(a.Guid("ClientId"),
                operation == GuiProjectOperation.RemoveExternalSource ? ExternalVideoSourceClientAction.Remove
                : a.Bool("Loaded") ? ExternalVideoSourceClientAction.Load : ExternalVideoSourceClientAction.Unload);
        }
        else if (operation == GuiProjectOperation.RemoveExternalSource)
            await ProjectExternalSourceService.RemoveAsync(this, a.Guid("ImportId"), save: false);
        else await ProjectExternalSourceService.SetLoadedAsync(WorkingPath, a.Guid("ImportId"), a.Bool("Loaded"));
        await AddClipView.RefreshExternalSourcesAsync();
        if (operation == GuiProjectOperation.RemoveExternalSource) return null;
        return (await GuiExternalSourcesAsync(a, ct)).Single();
    }

    private async Task<object> AddGuiExternalSourceClipAsync(GuiEditArgs a, CancellationToken ct)
    {
        if (a.Has("ImportId") == a.Has("ClientId")) throw new ArgumentException("Choose ImportId or ClientId.");
        if (a.Has("ImportId")) ExternalSourceSettings.EnsureAssemblySourcesAllowed(MauiProgram.BasicDataPath);
        else ExternalSourceSettings.EnsureRpcSourcesAllowed(MauiProgram.BasicDataPath);
        if (!Tracks.ContainsKey(a.Int("TrackId"))) throw new KeyNotFoundException("Track not found.");
        List<ExternalVideoSourceDescriptor> videos;
        List<ExternalAudioSourceDescriptor> audios;
        List<ExternalMaskSourceDescriptor> masks;
        Guid? importId = a.Has("ImportId") ? a.Guid("ImportId") : null;
        if (importId.HasValue)
        {
            var source = (await ProjectExternalSourceRuntime.ListAsync(WorkingPath, ct)).Sources.SingleOrDefault(s => s.ImportId == importId)
                ?? throw new KeyNotFoundException("External source not found.");
            if (!source.Loaded || !string.IsNullOrEmpty(source.Error)) throw new InvalidOperationException("External source is not loaded successfully.");
            videos = source.Sources; audios = source.AudioSources; masks = source.MaskSources;
        }
        else
        {
            var client = (await RenderRpcBootstrap.Client.ListExternalVideoSourceClientsAsync(ct)).Clients.SingleOrDefault(c => c.ClientId == a.Guid("ClientId"))
                ?? throw new KeyNotFoundException("External source client not found.");
            if (!client.Loaded) throw new InvalidOperationException("External source client is not loaded.");
            videos = client.Sources; audios = client.AudioSources; masks = client.MaskSources;
        }
        string id = a.String("SourceId")!;
        string kind = a.String("SourceKind")!;
        string path;
        if (kind.Equals("Mask", StringComparison.OrdinalIgnoreCase))
        {
            var source = masks.SingleOrDefault(s => s.SourceId == id) ?? throw new KeyNotFoundException("Mask source not found.");
            path = importId.HasValue ? ProjectExternalMaskSource.CreatePath(importId.Value, source) : RemoteRpcMaskSource.CreatePath(source);
            var dto = NewGuiSourceClip(a, ClipMode.MaskClip, "MaskClip", source.Name);
            dto.MetaData!["AllowFreeScaleResize"] = true;
            dto.MetaData[MaskClip.SourceTypeKey] = path;
            var mask = DraftImportAndExportHelper.ConvertToElement(dto);
            ApplyGuiSourceArguments(mask, a);
            return GuiClipSnapshot(TimelineMcpLiveService.ReplaceClip(this, GuiEditableDto(mask)));
        }
        if (a.Has("SourceArguments")) throw new ArgumentException("SourceArguments requires a mask source.");
        AssetItem asset;
        if (kind.Equals("Audio", StringComparison.OrdinalIgnoreCase))
        {
            var source = audios.SingleOrDefault(s => s.SourceId == id) ?? throw new KeyNotFoundException("Audio source not found.");
            path = importId.HasValue ? ProjectExternalAudioSource.CreatePath(importId.Value, source) : RemoteRpcAudioSource.CreatePath(source);
            asset = new()
            {
                Name = source.Name,
                AssetType = AssetType.Audio,
                ClipType = ClipMode.AudioClip,
                Path = path,
                Duration = source.Duration,
                SecondPerFrame = 1
            };
        }
        else if (kind.Equals("Video", StringComparison.OrdinalIgnoreCase))
        {
            var source = videos.SingleOrDefault(s => s.SourceId == id) ?? throw new KeyNotFoundException("Video source not found.");
            path = importId.HasValue ? ProjectExternalVideoSource.CreatePath(importId.Value, source) : RemoteRpcVideoSource.CreatePath(source);
            asset = new()
            {
                Name = source.Name,
                AssetType = AssetType.Video,
                ClipType = ClipMode.VideoClip,
                Path = path,
                Duration = source.TotalFrames,
                SecondPerFrame = source.Fps > 0 ? (float)(1d / source.Fps) : -1,
                Width = source.Width,
                Height = source.Height,
                BitPerPixel = source.HasKnownResultBitsPerPixel ? source.ResultBitsPerPixel : 0
            };
        }
        else throw new ArgumentException("SourceKind must be Video, Audio or Mask.");
        var clip = CreateFromAsset(asset, a.Int("TrackId"), InternalPluginBase.InternalPluginBaseID, path, FrameToPixel(a.UInt("StartFrame")));
        uint sourceStart = a.UInt("SourceStartFrame");
        if (!clip.isInfiniteLength && clip.maxFrameCount > 0)
        {
            if (sourceStart >= clip.maxFrameCount) throw new ArgumentException("SourceStartFrame is outside the source.");
            if (!a.Has("DurationFrames")) clip.lengthInFrame = clip.maxFrameCount - sourceStart;
            if (a.UInt("DurationFrames", clip.lengthInFrame) > clip.maxFrameCount - sourceStart)
                throw new ArgumentException("Clip duration exceeds the available source frames.");
        }
        if ((ulong)a.UInt("StartFrame") + a.UInt("DurationFrames", clip.lengthInFrame) > int.MaxValue)
            throw new ArgumentException("Clip exceeds the timeline range.");
        return GuiClipSnapshot(ApplyGuiClipParameters(clip, a.Json));
    }
    #endregion

    #region Setting
    private uint GuiSettingNumber(string key, uint fallback) => ProjectInfo.Properties.TryGetValue(key, out var text)
       && uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint value) ? value : fallback;

    private object GuiSettingsSnapshot() => new
    {
        FrameRate = ProjectInfo.TargetFrameRate,
        WidthPixels = ProjectInfo.RelativeWidth,
        HeightPixels = ProjectInfo.RelativeHeight,
        EnableHDR = ProjectInfo.Properties.TryGetValue("EnableHDR", out var hdr) && bool.TryParse(hdr, out var enabled) && enabled,
        HdrMaximumBrightness = GuiSettingNumber("HdrMaximumBrightness", 1000),
        SdrClipBrightness = GuiSettingNumber("SdrClipBrightness", GuiSettingNumber("sdrClipBrightness", 203)),
        CoverFilePath = ProjectInfo.ThumbPath,
        WriteCoverAsFirstFrame = ProjectInfo.Properties.TryGetValue("WriteCoverAsFirstFrame", out var cover) && bool.TryParse(cover, out var first) && first,
        ProjectInfo.UserDefinedProperties,
    };

    private object ExecuteGuiSettingsOperation(GuiProjectOperation operation, GuiEditArgs a)
    {
        if (operation == GuiProjectOperation.GetSettings) return GuiSettingsSnapshot();
        uint fps = a.UInt("FrameRate", ProjectInfo.TargetFrameRate);
        int width = a.Int("WidthPixels", ProjectInfo.RelativeWidth), height = a.Int("HeightPixels", ProjectInfo.RelativeHeight);
        if (fps == 0 || fps > int.MaxValue || width <= 0 || height <= 0) throw new ArgumentException("Invalid project frame rate or resolution.");
        if (a.Has("FrameRate"))
            foreach (var clip in Clips.Values.Where(c => c.ClipType == ClipMode.SubtitleClip && !c.IsGhost && !c.IsShadow))
                foreach (var cue in GuiSubtitleData(clip).Cues) _ = SubtitleData.FrameAt(cue.EndMilliseconds, fps);
        if (a.Has("CoverFilePath") && a.Bool("ClearCover")) throw new ArgumentException("Choose CoverFilePath or ClearCover.");
        string? coverPath = a.Bool("ClearCover") ? null : a.String("CoverFilePath", ProjectInfo.ThumbPath);
        if (a.Has("CoverFilePath") && !File.Exists(coverPath)) throw new FileNotFoundException("Project cover not found.", coverPath);
        var properties = new Dictionary<string, string>(ProjectInfo.Properties);
        foreach (string key in new[] { "EnableHDR", "WriteCoverAsFirstFrame" })
            if (a.Has(key)) properties[key] = a.Bool(key).ToString();
        foreach (string key in new[] { "HdrMaximumBrightness", "SdrClipBrightness" })
        {
            if (!a.Has(key)) continue;
            uint value = a.UInt(key);
            if (value > int.MaxValue || key == "HdrMaximumBrightness" && value == 0) throw new ArgumentException($"Invalid {key}.");
            properties[key] = value.ToString(CultureInfo.InvariantCulture);
        }
        if (a.Bool("ClearCover")) properties["WriteCoverAsFirstFrame"] = bool.FalseString;
        if (a.Bool("WriteCoverAsFirstFrame") && !File.Exists(coverPath)) throw new ArgumentException("Writing a cover as the first frame requires a cover file.");
        var custom = new Dictionary<string, string>(ProjectInfo.UserDefinedProperties);
        if (a.Has("UserDefinedProperties"))
            foreach (var item in a.Json.GetProperty("UserDefinedProperties").EnumerateObject())
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(item.Name);
                if (item.Value.ValueKind == JsonValueKind.Null) custom.Remove(item.Name);
                else custom[item.Name] = item.Value.ValueKind == JsonValueKind.String ? item.Value.GetString()! : item.Value.ToString();
            }
        if (a.Has("CoverFilePath"))
        {
            string directory = Path.Combine(WorkingPath, "assets", "covers");
            Directory.CreateDirectory(directory);
            string destination = Path.Combine(directory, Guid.NewGuid().ToString("N") + Path.GetExtension(coverPath));
            File.Copy(coverPath!, destination);
            coverPath = destination;
        }
        ProjectInfo.TargetFrameRate = fps;
        ProjectInfo.RelativeWidth = width;
        ProjectInfo.RelativeHeight = height;
        ProjectInfo.Properties = properties;
        ProjectInfo.UserDefinedProperties = custom;
        ProjectInfo.ThumbPath = coverPath;
        Logger.Log($"GUI RPC project settings: {width}x{height}, {fps} fps.", "info");
        return GuiSettingsSnapshot();
    }
    #endregion

    #region Subtitle payload
    private static SubtitleData GuiSubtitleData(ClipElementUI clip)
    {
        if (clip.ClipType != ClipMode.SubtitleClip) throw new ArgumentException("Clip is not a subtitle clip.");
        return clip.ExtraData.TryGetValue(SubtitleData.MetadataKey, out var data) ? SubtitleData.Restore(data).Clone() : new();
    }

    private object GuiSubtitleSnapshot(ClipElementUI clip)
    {
        var data = GuiSubtitleData(clip);
        return new
        {
            ClipId = clip.Id,
            Name = clip.DisplayName,
            TrackId = clip.origTrack,
            StartFrame = GetClipFrameRange(clip).Start,
            DurationFrames = clip.lengthInFrame,
            SourceStartFrame = clip.relativeStartFrame,
            CueCount = data.Cues.Count,
            Cues = data.Cues.OrderBy(c => c.StartMilliseconds).Select(c => GuiSubtitleCue(clip.Id, c)).ToArray(),
        };
    }

    private static object GuiSubtitleCue(Guid clipId, SubtitleCue cue) => new
    {
        ClipId = clipId,
        CueId = cue.Id,
        cue.StartMilliseconds,
        cue.EndMilliseconds,
        cue.Markdown,
        cue.SourceLine,
    };

    private SubtitleData ReadGuiSubtitles(GuiEditArgs a, SubtitleData data)
    {
        if (a.Has("FilePath") && a.Has("Cues")) throw new ArgumentException("Choose FilePath or Cues.");
        if (a.Has("EncodingName") && !a.Has("FilePath")) throw new ArgumentException("EncodingName requires FilePath.");
        if (a.Has("FilePath"))
        {
            string path = a.String("FilePath")!;
            if (!SubtitleImporter.Supports(path)) throw new NotSupportedException("Supported subtitle formats: SRT, VTT, ASS, SSA.");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var imported = SubtitleImporter.Read(path, a.Has("EncodingName")
                ? Encoding.GetEncoding(a.String("EncodingName")!, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback) : null);
            data.Cues = imported.Data.Cues;
            foreach (var warning in imported.Warnings) Logger.Log(warning, "warn");
        }
        if (a.Has("Cues"))
        {
            data.Cues = a.Json.GetProperty("Cues").EnumerateArray().Select(value =>
            {
                foreach (var field in value.EnumerateObject())
                    if (field.Name is not ("CueId" or "StartMilliseconds" or "EndMilliseconds" or "Markdown"))
                        throw new ArgumentException($"Unknown subtitle cue field '{field.Name}'.");
                var cue = new GuiEditArgs(value);
                return new SubtitleCue
                {
                    Id = cue.Has("CueId") ? cue.Guid("CueId") : Guid.NewGuid(),
                    StartMilliseconds = cue.Long("StartMilliseconds"),
                    EndMilliseconds = cue.Long("EndMilliseconds"),
                    Markdown = cue.String("Markdown", "")!,
                };
            }).ToList();
        }
        if (a.Has("OffsetMilliseconds"))
            foreach (var cue in data.Cues)
            {
                cue.StartMilliseconds = checked(cue.StartMilliseconds + a.Long("OffsetMilliseconds"));
                cue.EndMilliseconds = checked(cue.EndMilliseconds + a.Long("OffsetMilliseconds"));
            }
        ValidateGuiSubtitles(data);
        return data;
    }

    private void ValidateGuiSubtitles(SubtitleData data)
    {
        data.Validate();
        if (data.Cues.Any(c => c.Id == Guid.Empty) || data.Cues.Select(c => c.Id).Distinct().Count() != data.Cues.Count)
            throw new ArgumentException("Subtitle cue IDs must be nonempty and unique.");
        foreach (var cue in data.Cues) _ = SubtitleData.FrameAt(cue.EndMilliseconds, ProjectInfo.TargetFrameRate);
    }

    private object? ExecuteGuiSubtitleOperation(GuiProjectOperation operation, GuiEditArgs a)
    {
        if (operation == GuiProjectOperation.GetSubtitleClip)
            return Clips.Values.Where(c => c.ClipType == ClipMode.SubtitleClip && !c.IsGhost && !c.IsShadow
                && (!a.Has("ClipId") || c.Id == a.Guid("ClipId")) && (!a.Has("TrackId") || c.origTrack == a.Int("TrackId")))
                .Select(GuiSubtitleSnapshot).ToArray();
        if (operation == GuiProjectOperation.AddSubtitleClip)
        {
            var data = ReadGuiSubtitles(a, new());
            var dto = NewGuiSourceClip(a, ClipMode.SubtitleClip, "SubtitleClip", a.Has("FilePath") ? Path.GetFileNameWithoutExtension(a.String("FilePath")) : "Subtitles");
            data.Style.FontSize = Math.Max(12, ProjectInfo.RelativeHeight * 48f / 1080);
            data.Style.StrokeThickness = Math.Max(1, ProjectInfo.RelativeHeight * 2f / 1080);
            data.BottomMargin = Math.Max(12, ProjectInfo.RelativeHeight * 60f / 1080);
            if (!a.Has("DurationFrames") && data.Cues.Count > 0)
                dto.Duration = Math.Max(1, SubtitleData.FrameAt(data.Cues.Max(c => c.EndMilliseconds), ProjectInfo.TargetFrameRate));
            if ((ulong)dto.StartFrame + dto.Duration > int.MaxValue) throw new ArgumentException("Subtitle clip exceeds the timeline range.");
            dto.MetaData![SubtitleData.MetadataKey] = data;
            return GuiSubtitleSnapshot(TimelineMcpLiveService.ReplaceClip(this, dto));
        }
        var clip = GuiEditableClip(a.Guid("ClipId"));
        var subtitles = GuiSubtitleData(clip);
        if (operation == GuiProjectOperation.GetSubtitleCue)
            return subtitles.Cues.Where(c => !a.Has("CueId") || c.Id == a.Guid("CueId"))
                .OrderBy(c => c.StartMilliseconds).Select(c => GuiSubtitleCue(clip.Id, c)).ToArray();
        SubtitleCue? edited = null;
        if (operation == GuiProjectOperation.SetSubtitleClip) subtitles = ReadGuiSubtitles(a, subtitles);
        else if (operation == GuiProjectOperation.AddSubtitleCue)
        {
            edited = new() { StartMilliseconds = a.Long("StartMilliseconds"), EndMilliseconds = a.Long("EndMilliseconds"), Markdown = a.String("Markdown", "")! };
            subtitles.Cues.Add(edited);
        }
        else
        {
            edited = subtitles.Cues.SingleOrDefault(c => c.Id == a.Guid("CueId")) ?? throw new KeyNotFoundException("Subtitle cue not found.");
            if (operation == GuiProjectOperation.RemoveSubtitleCue) subtitles.Cues.Remove(edited);
            else
            {
                if (a.Has("StartMilliseconds")) edited.StartMilliseconds = a.Long("StartMilliseconds");
                if (a.Has("EndMilliseconds")) edited.EndMilliseconds = a.Long("EndMilliseconds");
                if (a.Has("Markdown")) edited.Markdown = a.String("Markdown") ?? "";
            }
        }
        ValidateGuiSubtitles(subtitles);
        var updated = GuiEditableDto(clip);
        if (a.Bool("FitLength") && subtitles.Cues.Count > 0)
        {
            uint end = SubtitleData.FrameAt(subtitles.Cues.Max(c => c.EndMilliseconds), ProjectInfo.TargetFrameRate);
            if (end <= clip.relativeStartFrame) throw new ArgumentException("Subtitles end before the clip's source start.");
            uint length = end - clip.relativeStartFrame;
            if ((ulong)GetClipFrameRange(clip).Start + ClipTiming.EffectiveDuration(length, clip.SpeedProvider) > int.MaxValue)
                throw new ArgumentException("Subtitle clip exceeds the timeline range.");
            updated.Duration = length;
        }
        updated.MetaData![SubtitleData.MetadataKey] = subtitles;
        clip = TimelineMcpLiveService.ReplaceClip(this, updated);
        Logger.Log($"GUI RPC {operation}: subtitle clip {clip.Id}, {subtitles.Cues.Count} cues.", "info");
        return edited is not null ? GuiSubtitleCue(clip.Id, edited) : GuiSubtitleSnapshot(clip);
    }
    #endregion

    #region Timeline
    private ClipElementUI SplitClipAtFrame(ClipElementUI clip, uint frame)
    {
        var range = GetClipFrameRange(clip);
        if (clip.ClipType == ClipMode.MarkingClip || clip.IsExtraDataOptionIsTrue("ExtendToWholeDraft") ||
            frame <= range.Start || (ulong)frame >= (ulong)range.Start + range.Duration || clip.lengthInFrame < 2)
            throw new ArgumentException("Split frame must be inside a non-group clip with at least two source frames.");
        uint length = clip.lengthInFrame;
        uint consumed = Math.Clamp(ClipTiming.SourceOffset(frame - range.Start, length, clip.SpeedProvider), 1u, length - 1);
        clip.lengthInFrame = consumed;
        clip.ProjectTiming();
        ClipElementUI right;
        try
        {
            right = CreateAndAddClip(FrameToPixel((uint)clip.TimelineEnd), FrameToPixel(length - consumed), clip.origTrack ?? 0,
                labelText: $"{clip.DisplayName} (2)", background: clip.Clip.Background, resolveOverlap: false,
                maxFrames: clip.maxFrameCount, relativeStart: checked(clip.relativeStartFrame + consumed), sourceElement: clip);
        }
        catch
        {
            clip.lengthInFrame = length;
            clip.ProjectTiming();
            throw;
        }
        right.SubLayerIndex = clip.SubLayerIndex;
        TransferSplitTransforms(clip, right);
        ApplyClipPreview(clip);
        UpdateTimelineWidth();
        Logger.Log($"Split clip {clip.Id} into {consumed}+{length - consumed} source frames at {clip.TimelineEnd}; right clip {right.Id}.", "info");
        return right;
    }

    private async Task<object?> ExecuteGuiTimelineOperationAsync(GuiProjectOperation operation, GuiEditArgs a)
    {
        if (operation == GuiProjectOperation.RemoveTrack)
        {
            int track = a.Int("TrackId");
            if (!Tracks.ContainsKey(track)) throw new KeyNotFoundException("Track not found.");
            var ids = Clips.Values.Where(c => c.origTrack == track && !c.IsGhost && !c.IsShadow).Select(c => c.Id).ToArray();
            RemoveTrackAndReindex(track);
            await UpdateAdjacencyForTrack();
            Logger.Log($"GUI RPC removed track {track}, {ids.Length} clips; tracks reindexed.", "info");
            return new { TrackId = track, RemovedClipIds = ids, Tracks = Tracks.Keys.Order().ToArray() };
        }
        if (operation == GuiProjectOperation.JoinClip)
        {
            var ids = a.Json.GetProperty("ClipIds").Deserialize<Guid[]>()!.Distinct().ToArray();
            if (ids.Length < 2) throw new ArgumentException("At least two distinct clips are required.");
            var clips = ids.Select(GuiEditableClip).ToArray();
            if (clips.Any(c => !c.ShouldDisplayInUI || c.ClipType == ClipMode.MarkingClip))
                throw new ArgumentException("Only visible, ungrouped clips can be joined.");
            if (clips.Any(c => c.origTrack < SubTrackOffset) && clips.Any(c => c.origTrack >= SubTrackOffset))
                throw new ArgumentException("Main-track and subtrack clips cannot be joined in the same group.");
            ClearSelectionInternal();
            foreach (var clip in clips) AddClipToSelection(clip);
            await CombineSelection(notify: false);
            if (_selectedClipIds.Count != 1) throw new InvalidOperationException("No clip group was created.");
            var group = GuiEditableClip(_selectedClipIds.Single());
            if (ids.Contains(group.Id)) throw new InvalidOperationException("No clip group was created.");
            if (a.Has("Name")) group.DisplayName = a.String("Name")!;
            Logger.Log($"GUI RPC joined {ids.Length} clips into {group.Id}.", "info");
            return GuiClipSnapshot(group);
        }
        var current = GuiEditableClip(a.Guid("ClipId"));
        if (operation == GuiProjectOperation.SplitClip)
        {
            var right = SplitClipAtFrame(current, a.UInt("FrameIndex"));
            await UpdateAdjacencyForTrack();
            return new[] { GuiClipSnapshot(current), GuiClipSnapshot(right) };
        }
        var before = Clips.Keys.ToHashSet();
        if (current.ClipType == ClipMode.MarkingClip && ExtractGroupedClipIds(current).Count > 0)
        {
            var ids = ExtractGroupedClipIds(current).Select(Guid.Parse).ToArray();
            await UnbindGroupingMarkerAsync(current, notify: false);
            Logger.Log($"GUI RPC split clip group {current.Id}, {ids.Length} members.", "info");
            return ids.Where(Clips.ContainsKey).Select(id => GuiClipSnapshot(Clips[id])).ToArray();
        }
        if (current.ClipType != ClipMode.VectorComponentClip || !current.ExtraData.ContainsKey(VectorComponentClip.ChildrenKey))
            throw new ArgumentException("Clip is not a timeline clip group.");
        await UngroupVectorClipAsync(current, notify: false);
        return Clips.Values.Where(c => !before.Contains(c.Id) && !c.IsGhost && !c.IsShadow).Select(GuiClipSnapshot).ToArray();
    }
    #endregion

    #region Transform
    private object? GuiClipTransform(ClipElementUI clip, TransformSide side)
    {
        var infos = GetTransformClipInfos(clip.ClipType == ClipMode.AudioClip);
        if (TransformProcessing.Find(infos, clip.Id, side) is not { } found) return null;
        var provider = Clips[found.Owner.Id].EffectProviders[found.Provider.Id];
        var mode = TransformProcessing.ReadEnum<TransformInputMode>(provider.MetaData, TransformProcessing.ModeKey);
        Guid? neighbor = mode == TransformInputMode.TwoInput
            ? found.Owner.Id == clip.Id ? TransformProcessing.ReadNextClip(provider.MetaData) : found.Owner.Id : null;
        return new
        {
            ClipId = clip.Id,
            OwnerClipId = found.Owner.Id,
            ProviderId = provider.Id,
            Side = side.ToString(),
            provider.TypeName,
            provider.Name,
            provider.Enabled,
            NeighborClipId = neighbor,
            InputMode = mode.ToString(),
            DurationFrames = TransformProcessing.ReadDuration(provider.MetaData),
            RenderOrder = TransformProcessing.ReadEnum<TransformRenderOrder>(provider.MetaData, TransformProcessing.OrderKey).ToString(),
            ResolvedDurationFrames = TransformProcessing.Resolve(infos, clip.Id, side)?.Duration ?? 0,
            Provider = GuiProvider(provider, found.Owner.Id),
        };
    }

    private async Task<object?> ExecuteGuiTransformOperationAsync(GuiProjectOperation operation, GuiEditArgs a, CancellationToken ct)
    {
        if (operation == GuiProjectOperation.GetTransformType)
        {
            return TransformServices.GetAvailableTransforms(a.Bool("Audio"))
                .Where(x => !a.Has("TypeName") || x.Key.Equals(a.String("TypeName"), StringComparison.OrdinalIgnoreCase))
                .Select(x =>
                {
                    var p = x.Value();
                    var transform = TransformServices.Create(p);
                    try
                    {
                        return new
                        {
                            TypeName = x.Key,
                            p.Name,
                            Definition = transform.Definition.ToString(),
                            SupportsOneInput = transform.Definition.HasFlag(TransformDefinition.SupportOneInput),
                            SupportsTwoInput = transform.Definition.HasFlag(TransformDefinition.SupportTwoInput),
                            Fields = p.Fields.Values.Select(GuiField).ToArray()
                        };
                    }
                    finally { (transform as IDisposable)?.Dispose(); }
                }).ToArray();
        }
        var clip = GuiEditableClip(a.Guid("ClipId"));
        if (!SupportsPictureTransform(clip) && !SupportsAudioTransform(clip)) throw new ArgumentException("Clip does not support transforms.");
        var side = a.Enum<TransformSide>("Side");
        if (operation == GuiProjectOperation.GetClipTransform)
            return (a.Has("Side") ? new[] { side } : Enum.GetValues<TransformSide>())
                .Select(s => GuiClipTransform(clip, s)).Where(x => x is not null).ToArray();
        bool audio = clip.ClipType == ClipMode.AudioClip;
        var existing = TransformProcessing.Find(GetTransformClipInfos(audio), clip.Id, side);
        if (operation == GuiProjectOperation.RemoveClipTransform)
        {
            if (existing is null) throw new KeyNotFoundException("Clip transform not found.");
            ClearClipTransform(clip, side);
            RefreshTransformShadows();
            return null;
        }
        IEffectProvider provider;
        if (existing is { } saved && (!a.Has("TypeName") || saved.Provider.TypeName == a.String("TypeName")))
            provider = CopyGuiEffectProviders(Clips[saved.Owner.Id])[saved.Provider.Id];
        else if (TransformServices.GetAvailableTransforms(audio).TryGetValue(a.String("TypeName") ?? "", out var factory))
        {
            provider = factory();
            provider.Id = Guid.NewGuid();
        }
        else throw new ArgumentException("Specify an available transform TypeName when creating or replacing a transform.");
        ApplyGuiProviderFields(provider, a.Fields(), a.Bool("ResetToDefaults"));
        TransformInputMode mode;
        if (existing is { } current)
            mode = a.Enum("InputMode", TransformProcessing.ReadEnum<TransformInputMode>(current.Provider.MetaData, TransformProcessing.ModeKey));
        else
        {
            var neighbors = FindTransformNeighbors(clip, audio);
            var transform = TransformServices.Create(provider);
            try
            {
                mode = a.Enum("InputMode", (side == TransformSide.Left ? neighbors.left : neighbors.right) is not null
                    && transform.Definition.HasFlag(TransformDefinition.SupportTwoInput) ? TransformInputMode.TwoInput : TransformInputMode.OneInput);
            }
            finally { (transform as IDisposable)?.Dispose(); }
        }
        if (operation == GuiProjectOperation.GetClipTransformPreview)
        {
            if (audio) throw new NotSupportedException("Transform previews require a picture clip.");
            string path = await RenderTransformPreviewAsync(clip, side, mode, provider, ct)
                ?? throw new InvalidOperationException("Transform cannot be previewed at this clip edge.");
            return new { ClipId = clip.Id, Side = side.ToString(), Path = path };
        }
        uint duration = a.UInt("DurationFrames", existing is { } previous
            ? TransformProcessing.ReadDuration(previous.Provider.MetaData) : Math.Max(1u, ProjectInfo.TargetFrameRate / 2));
        if (duration == 0 || duration > int.MaxValue) throw new ArgumentException("Invalid transform duration.");
        var order = a.Enum("RenderOrder", existing is { } previousOrder
            ? TransformProcessing.ReadEnum<TransformRenderOrder>(previousOrder.Provider.MetaData, TransformProcessing.OrderKey) : TransformServices.DefaultRenderOrder);
        provider.Enabled = a.Bool("Enabled", provider.Enabled);
        if (!SetClipTransform(clip, side, mode, provider, duration, TransformProcessing.IsAI(provider.MetaData), order, notify: false))
            throw new InvalidOperationException("Transform input mode or neighboring clips are incompatible.");
        Logger.Log($"GUI RPC transform {provider.TypeName} on clip {clip.Id}/{side}: {mode}, {duration} frames.", "info");
        return GuiClipTransform(clip, side);
    }
    #endregion

    #region VectorContent
    private static bool IsGuiVectorClip(ClipElementUI clip)
    => clip.ClipType is ClipMode.VectorCanvasClip or ClipMode.VectorComponentClip;

    private static Dictionary<string, Func<IVectorComponent>> GetGuiVectorComponentFactories()
    {
        var factories = VectorComponentHandlerServices.GetAvailableHandlers()
            .ToDictionary(x => x.Key, x => (Func<IVectorComponent>)(() => x.Value().Create()), StringComparer.OrdinalIgnoreCase);
        factories.TryAdd("ComponentGroup", () => new ComponentGroup());
        return factories;
    }

    private static void ApplyGuiVectorParameters(IVectorComponent component, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) throw new ArgumentException("Parameters must be an object.");
        foreach (var field in parameters.EnumerateObject())
        {
            var key = component.Parameters.Keys.FirstOrDefault(k => k.Equals(field.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Unknown vector parameter '{field.Name}' for {component.TypeName}.");
            if (key == "Children") throw new ArgumentException("Use component commands to edit group children.");
            if (field.Value.ValueKind == JsonValueKind.Null) throw new ArgumentException($"Vector parameter '{key}' cannot be null.");
            var expected = JsonSerializer.SerializeToElement(component.Parameters[key]);
            if (expected.ValueKind == JsonValueKind.Number
                && (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetSingle(out var number) || !float.IsFinite(number)))
                throw new ArgumentException($"Vector parameter '{key}' must be a finite number.");
            if ((expected.ValueKind is JsonValueKind.String or JsonValueKind.Array or JsonValueKind.Object)
                && field.Value.ValueKind != expected.ValueKind)
                throw new ArgumentException($"Invalid value type for vector parameter '{key}'.");
            if ((expected.ValueKind is JsonValueKind.True or JsonValueKind.False)
                && field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException($"Vector parameter '{key}' must be a Boolean.");
            component.Parameters[key] = field.Value.Clone();
        }
    }

    private static IVectorComponent CreateGuiVectorComponent(JsonElement definition)
    {
        if (definition.ValueKind != JsonValueKind.Object || !definition.TryGetProperty("TypeName", out var type))
            throw new ArgumentException("A component definition requires TypeName.");
        if (!GetGuiVectorComponentFactories().TryGetValue(type.GetString() ?? "", out var factory))
            throw new KeyNotFoundException("Vector component type not found.");
        var component = factory();
        if (definition.TryGetProperty("Name", out var name)) component.Name = name.GetString() ?? "";
        if (definition.TryGetProperty("Parameters", out var parameters)) ApplyGuiVectorParameters(component, parameters);
        if (definition.TryGetProperty("Children", out var children))
        {
            if (component is not ComponentGroup group || children.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Children must be an array on a ComponentGroup.");
            var list = children.EnumerateArray().Select(CreateGuiVectorComponent).ToList();
            ReindexGuiVectorComponents(list);
            group.SetChildren(list);
        }
        return component;
    }

    private static IEnumerable<(IVectorComponent Component, ComponentGroup? Parent)> EnumerateGuiVectorComponents(
        IEnumerable<IVectorComponent> components, ComponentGroup? parent = null)
    {
        foreach (var component in components)
        {
            yield return (component, parent);
            if (component is ComponentGroup group)
                foreach (var child in EnumerateGuiVectorComponents(group.Children, group)) yield return child;
        }
    }

    private static void ReindexGuiVectorComponents(List<IVectorComponent> components)
    {
        for (int i = 0; i < components.Count; i++)
        {
            components[i].Index = i;
            if (components[i].Parameters.ContainsKey("LayerIndex")) components[i].Parameters["LayerIndex"] = i;
        }
    }

    private static object GuiVectorComponentSnapshot(Guid clipId, IVectorComponent component, ComponentGroup? parent) => new
    {
        ClipId = clipId,
        ComponentId = component.Id,
        ParentComponentId = parent?.Id,
        component.TypeName,
        component.FromPlugin,
        component.Name,
        component.Index,
        Parameters = component.Parameters.Where(p => p.Key != "Children").ToDictionary(p => p.Key, p => p.Value),
    };

    private object GuiVectorClipSnapshot(ClipElementUI clip)
    {
        var dto = DraftImportAndExportHelper.ExportClipElementFromDraftPage(this, clip, false);
        return new
        {
            ClipId = dto.Id,
            dto.Name,
            Type = dto.ClipType.ToString(),
            TrackId = dto.LayerIndex,
            dto.StartFrame,
            DurationFrames = dto.Duration,
            SourceStartFrame = dto.RelativeStartFrame,
            WidthPixels = dto.TargetWidth,
            HeightPixels = dto.TargetHeight,
            XPixels = dto.TargetX,
            YPixels = dto.TargetY,
            RotationDegrees = dto.Rotation,
            CanvasWidthPixels = VectorComponentClip.ReadNumber(clip.ExtraData, VectorComponentClip.CanvasWidthKey, dto.TargetWidth),
            CanvasHeightPixels = VectorComponentClip.ReadNumber(clip.ExtraData, VectorComponentClip.CanvasHeightKey, dto.TargetHeight),
            Viewport = VectorComponentClip.ReadViewport(clip.ExtraData, dto.TargetWidth, dto.TargetHeight),
            SourceLengthFrames = VectorComponentClip.ReadNumber(clip.ExtraData, VectorComponentClip.SourceLengthKey, dto.Duration),
            AntiAliasMode = clip.ExtraData.GetValueOrDefault("VectorAntiAliasMode")?.ToString() is { Length: > 0 } aa ? aa : "Default",
            Components = EnumerateGuiVectorComponents(VectorComponentSerializer.Read(clip.ExtraData))
                .Select(c => GuiVectorComponentSnapshot(dto.Id, c.Component, c.Parent)).ToArray(),
        };
    }

    private ClipDraftDTO ApplyGuiVectorClipParameters(ClipDraftDTO dto, JsonElement parameters)
    {
        var metadata = dto.MetaData;
        var node = JsonSerializer.SerializeToNode(dto)!;
        foreach (var (source, target) in new[] { ("Name", "Name"), ("StartFrame", "StartFrame"), ("DurationFrames", "Duration"),
            ("SourceStartFrame", "RelativeStartFrame"), ("TrackId", "LayerIndex"), ("WidthPixels", "TargetWidth"),
            ("HeightPixels", "TargetHeight"), ("XPixels", "TargetX"), ("YPixels", "TargetY"), ("RotationDegrees", "Rotation") })
            if (parameters.TryGetProperty(source, out var value)) node[target] = JsonNode.Parse(value.GetRawText());
        dto = node.Deserialize<ClipDraftDTO>()!;
        if (dto.Duration == 0 || dto.LayerIndex > int.MaxValue || !Tracks.ContainsKey((int)dto.LayerIndex)
            || (ulong)dto.StartFrame + dto.Duration > int.MaxValue || !float.IsFinite(dto.Rotation))
            throw new ArgumentException("Invalid vector clip timing, track or rotation.");
        if (dto.TargetWidth <= 0 || dto.TargetHeight <= 0) throw new ArgumentException("Vector clip dimensions must be positive.");
        dto.MetaData = metadata is null ? new() : new(metadata);
        foreach (var (parameter, key) in new[] { ("CanvasWidthPixels", VectorComponentClip.CanvasWidthKey),
            ("CanvasHeightPixels", VectorComponentClip.CanvasHeightKey), ("SourceLengthFrames", VectorComponentClip.SourceLengthKey) })
        {
            if (!parameters.TryGetProperty(parameter, out var value)) continue;
            if (dto.ClipType != ClipMode.VectorComponentClip) throw new ArgumentException($"{parameter} requires a VectorComponentClip.");
            if (value.GetInt32() <= 0) throw new ArgumentException($"{parameter} must be positive.");
            dto.MetaData[key] = value.GetInt32();
            if (parameter == "SourceLengthFrames") dto.SourceDuration = value.GetInt32();
        }
        if (parameters.TryGetProperty("Viewport", out var viewport))
        {
            if (dto.ClipType != ClipMode.VectorComponentClip) throw new ArgumentException("Viewport requires a VectorComponentClip.");
            var rect = viewport.Deserialize<int[]>();
            if (rect is not { Length: 4 } || rect[2] <= 0 || rect[3] <= 0)
                throw new ArgumentException("Viewport must be [x, y, width, height] with positive dimensions.");
            dto.MetaData[VectorComponentClip.ViewportKey] = rect;
        }
        if (parameters.TryGetProperty("AntiAliasMode", out var aa))
        {
            string mode = aa.GetString() ?? "";
            string? canonical = new[] { "Default", "None", "SSAA2x", "SSAA4x", "SSAA8x" }
                .FirstOrDefault(x => x.Equals(mode, StringComparison.OrdinalIgnoreCase));
            if (canonical is null) throw new ArgumentException("Unknown vector anti-alias mode.");
            dto.MetaData["VectorAntiAliasMode"] = canonical == "Default" ? "" : canonical;
        }
        return dto;
    }

    private ClipDraftDTO SetGuiVectorDefinition(ClipDraftDTO dto, List<IVectorComponent> components, bool updateBounds)
    {
        foreach (var component in components) _ = component.ComputeAll().ToArray();
        // Work on a detached element so invalid definitions cannot partially update the timeline.
        var element = DraftImportAndExportHelper.ConvertToElement(dto);
        VectorClipServices.SetDefinition(this, element, components, updateBounds && dto.ClipType == ClipMode.VectorComponentClip);
        dto.MetaData = element.ExtraData;
        dto.TargetWidth = element.TargetWidth;
        dto.TargetHeight = element.TargetHeight;
        dto.TargetX = element.TargetX;
        dto.TargetY = element.TargetY;
        return dto;
    }

    private ClipElementUI ReplaceGuiVectorClip(ClipDraftDTO dto, List<IVectorComponent> components, CancellationToken cancellationToken)
    {
        bool showDefaultHandles = components.Count != 1 || VectorClipServices.GetHandler(components[0])?.HasDefaultHandles != false;
        cancellationToken.ThrowIfCancellationRequested();
        var clip = TimelineMcpLiveService.ReplaceClip(this, dto);
        clip.ShowDefaultHandles = showDefaultHandles;
        return clip;
    }

    private object? ExecuteGuiVectorOperation(GuiProjectOperation operation, JsonElement parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool Has(string key) => parameters.TryGetProperty(key, out _);
        string? S(string key) => Has(key) ? parameters.GetProperty(key).GetString() : null;
        Guid G(string key) => Has(key) ? parameters.GetProperty(key).GetGuid() : Guid.Empty;
        bool B(string key, bool fallback = false) => Has(key) ? parameters.GetProperty(key).GetBoolean() : fallback;
        ClipElementUI Clip()
        {
            if (!Clips.TryGetValue(G("ClipId"), out var clip)) throw new KeyNotFoundException("Clip not found.");
            return IsGuiVectorClip(clip) ? clip : throw new ArgumentException("Clip must be a VectorCanvasClip or VectorComponentClip.");
        }

        if (operation == GuiProjectOperation.GetVectorComponentType)
            return GetGuiVectorComponentFactories().Select(x => (TypeName: x.Key, Component: x.Value()))
                .Where(x => (!Has("TypeName") || x.TypeName.Equals(S("TypeName"), StringComparison.OrdinalIgnoreCase))
                    && (!Has("Name") || x.TypeName.Contains(S("Name")!, StringComparison.OrdinalIgnoreCase)
                        || x.Component.Name.Contains(S("Name")!, StringComparison.OrdinalIgnoreCase)))
                .Select(x => new
                {
                    x.TypeName,
                    x.Component.Name,
                    x.Component.FromPlugin,
                    DefaultParameters = x.Component.Parameters.Where(p => p.Key != "Children").ToDictionary(p => p.Key, p => p.Value)
                }).ToArray();

        if (operation == GuiProjectOperation.GetVectorClip)
            return Clips.Values.Where(c => IsGuiVectorClip(c) && (!Has("ClipId") || c.Id == G("ClipId"))
                && (!Has("TrackId") || c.origTrack == parameters.GetProperty("TrackId").GetInt32())
                && (!Has("Name") || c.DisplayName.Contains(S("Name")!, StringComparison.OrdinalIgnoreCase)))
                .Select(GuiVectorClipSnapshot).ToArray();

        if (operation == GuiProjectOperation.GetVectorComponent)
        {
            var clip = Clip();
            return EnumerateGuiVectorComponents(VectorComponentSerializer.Read(clip.ExtraData))
                .Where(c => (!Has("ComponentId") || c.Component.Id == G("ComponentId"))
                    && (!Has("ParentComponentId") || (c.Parent?.Id ?? Guid.Empty) == G("ParentComponentId"))
                    && (!Has("TypeName") || c.Component.TypeName.Equals(S("TypeName"), StringComparison.OrdinalIgnoreCase)))
                .Select(c => GuiVectorComponentSnapshot(clip.Id, c.Component, c.Parent)).ToArray();
        }

        ClipDraftDTO dto;
        object? result;
        if (operation is GuiProjectOperation.AddVectorCanvasClip or GuiProjectOperation.AddVectorComponentClip)
        {
            bool canvas = operation == GuiProjectOperation.AddVectorCanvasClip;
            var components = new List<IVectorComponent>();
            if (canvas)
            {
                if (Has("Components"))
                {
                    if (parameters.GetProperty("Components").ValueKind != JsonValueKind.Array) throw new ArgumentException("Components must be an array.");
                    components.AddRange(parameters.GetProperty("Components").EnumerateArray().Select(CreateGuiVectorComponent));
                }
            }
            else if (Has("FilePath"))
            {
                if (Has("TypeName") || Has("Parameters")) throw new ArgumentException("Choose FilePath or TypeName and Parameters.");
                var component = VectorClipServices.Import(S("FilePath")!);
                if (component.Parameters.ContainsKey(VectorClipServices.AssetClipKey))
                    throw new ArgumentException("Import component JSON or SVG; saved timeline clips are not component definitions.");
                components.Add(component);
            }
            else components.Add(CreateGuiVectorComponent(parameters));
            ReindexGuiVectorComponents(components);
            uint duration = Has("DurationFrames") ? parameters.GetProperty("DurationFrames").GetUInt32() : 300;
            dto = new ClipDraftDTO
            {
                Id = Guid.NewGuid(),
                FromPlugin = InternalPluginBase.InternalPluginBaseID,
                ClipType = canvas ? ClipMode.VectorCanvasClip : ClipMode.VectorComponentClip,
                TypeName = canvas ? nameof(VectorCanvasClip) : nameof(VectorComponentClip),
                Name = S("Name") ?? (canvas ? "Vector Canvas" : components[0].Name),
                Duration = duration,
                SourceDuration = duration,
                FrameTime = 1f / Math.Max(1u, ProjectInfo.TargetFrameRate),
                SecondPerFrameRatio = 1f,
                TargetWidth = ProjectInfo.RelativeWidth,
                TargetHeight = ProjectInfo.RelativeHeight,
                MetaData = new(),
            };
            if (!canvas)
            {
                dto.MetaData[VectorComponentClip.CanvasWidthKey] = ProjectInfo.RelativeWidth;
                dto.MetaData[VectorComponentClip.CanvasHeightKey] = ProjectInfo.RelativeHeight;
                dto.MetaData[VectorComponentClip.SourceLengthKey] = duration;
            }
            dto = ApplyGuiVectorClipParameters(dto, parameters);
            dto = SetGuiVectorDefinition(dto, components, !canvas && !Has("Viewport"));
            dto = ApplyGuiVectorClipParameters(dto, parameters);
            result = GuiVectorClipSnapshot(ReplaceGuiVectorClip(dto, components, cancellationToken));
        }
        else
        {
            var clip = Clip();
            dto = DraftImportAndExportHelper.ExportClipElementFromDraftPage(this, clip, false);
            dto.MetaData ??= new();
            var components = VectorComponentSerializer.Read(dto.MetaData!);
            if (operation == GuiProjectOperation.SetVectorClip)
            {
                if (B("UpdateBounds") && Has("Viewport")) throw new ArgumentException("Choose UpdateBounds or Viewport.");
                dto = ApplyGuiVectorClipParameters(dto, parameters);
                dto = SetGuiVectorDefinition(dto, components, B("UpdateBounds"));
                dto = ApplyGuiVectorClipParameters(dto, parameters);
                result = GuiVectorClipSnapshot(ReplaceGuiVectorClip(dto, components, cancellationToken));
            }
            else
            {
                if (dto.MetaData!.ContainsKey(VectorComponentClip.ChildrenKey) && operation != GuiProjectOperation.SetVectorComponent)
                    throw new InvalidOperationException("Cannot add or remove components in a timed vector clip group.");
                IVectorComponent component;
                ComponentGroup? parent;
                List<IVectorComponent> siblings;
                if (operation == GuiProjectOperation.AddVectorComponent)
                {
                    parent = Has("ParentComponentId") && G("ParentComponentId") != Guid.Empty
                        ? EnumerateGuiVectorComponents(components).Select(c => c.Component).OfType<ComponentGroup>()
                            .FirstOrDefault(c => c.Id == G("ParentComponentId")) ?? throw new KeyNotFoundException("Parent component group not found.")
                        : null;
                    siblings = parent?.Children ?? components;
                    component = CreateGuiVectorComponent(parameters);
                    siblings.Add(component);
                }
                else
                {
                    var found = EnumerateGuiVectorComponents(components).FirstOrDefault(c => c.Component.Id == G("ComponentId"));
                    component = found.Component ?? throw new KeyNotFoundException("Vector component not found.");
                    parent = found.Parent;
                    siblings = parent?.Children ?? components;
                    if (operation == GuiProjectOperation.SetVectorComponent)
                    {
                        if (B("ResetToDefaults"))
                        {
                            IVectorComponent defaults = component is ComponentGroup ? new ComponentGroup()
                                : VectorClipServices.GetHandler(component)?.Create()
                                    ?? throw new InvalidOperationException("No defaults are available for this vector component.");
                            var children = (component as ComponentGroup)?.Children.ToArray();
                            component.Parameters.Clear();
                            foreach (var field in defaults.Parameters) component.Parameters[field.Key] = field.Value;
                            if (component is ComponentGroup group) group.SetChildren(children!);
                            if (component.Parameters.ContainsKey("LayerIndex")) component.Parameters["LayerIndex"] = component.Index;
                        }
                        if (Has("Name")) component.Name = S("Name") ?? "";
                        if (Has("Parameters")) ApplyGuiVectorParameters(component, parameters.GetProperty("Parameters"));
                    }
                    else if (operation == GuiProjectOperation.RemoveVectorComponent) siblings.Remove(component);
                    else throw new NotSupportedException("Unknown GUI vector operation.");
                }
                if (Has("Index"))
                {
                    int index = parameters.GetProperty("Index").GetInt32();
                    if (index < 0 || index >= siblings.Count) throw new ArgumentException("Index is outside the component list.");
                    siblings.Remove(component);
                    siblings.Insert(index, component);
                }
                if (operation != GuiProjectOperation.SetVectorComponent || Has("Index")) ReindexGuiVectorComponents(siblings);
                parent?.SetChildren(siblings);
                dto = SetGuiVectorDefinition(dto, components, B("UpdateBounds", dto.ClipType == ClipMode.VectorComponentClip));
                ReplaceGuiVectorClip(dto, components, cancellationToken);
                result = operation == GuiProjectOperation.RemoveVectorComponent ? null : GuiVectorComponentSnapshot(dto.Id, component, parent);
            }
        }
        Log($"GUI RPC {operation}: vector clip {dto.Id}.");
        return result;
    }
    #endregion
}
