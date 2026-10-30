using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationAPIBase.Workspace.Modules;
using projectFrameCut.Asset;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.RenderAPIBase.Text;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.Sources;
using projectFrameCut.Render.Text;
using projectFrameCut.Render.VectorContent;
using projectFrameCut.Render.VectorContent.Components;
using projectFrameCut.Services;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Shared;

namespace projectFrameCut;

public partial class DraftPage
{
    #region Batch lifecycle

    private sealed class GuiBatchState
    {
        public required Guid Id { get; init; }
        public required Guid Owner { get; init; }
        public required ProjectJSONStructure Project { get; set; }
        public required DraftStructureJSON Draft { get; init; }
        public required Dictionary<Guid, ClipDraftDTO> Clips { get; init; }
        public required Dictionary<string, AssetItem> Assets { get; init; }
        public required HashSet<int> Tracks { get; init; }
        public required ProjectJSONStructure OriginalProject { get; init; }
        public required Dictionary<string, AssetItem> OriginalAssets { get; init; }
        public required HashSet<int> OriginalTracks { get; init; }
        public bool OriginalSnapshotDirty { get; init; }
        public bool OriginalProjectDirty { get; init; }
        public bool OriginalHistoryNavigatedByUndoRedo { get; init; }
        public int OperationCount { get; set; }
        public bool Applied { get; set; }
        public bool Saved { get; set; }
        public bool Notified { get; set; }
        public Dictionary<string, string> PendingFiles { get; } = new();
        public HashSet<string> CopiedFiles { get; } = new();
        private readonly Dictionary<Guid, ClipDraftDTO?> _originals = new();
        private HashSet<int>? _tracks;
        private Dictionary<string, AssetItem>? _assets;
        private Dictionary<string, string>? _files;
        private ProjectJSONStructure? _project;

        public void BeginRequest(GuiProjectOperation operation)
        {
            if (operation is GuiProjectOperation.AddTrack or GuiProjectOperation.AddSubTrack or GuiProjectOperation.RemoveTrack
                or GuiProjectOperation.SplitClipGroup or GuiProjectOperation.SetClipProperties)
                _tracks = new(Tracks);
            if (operation is GuiProjectOperation.AddAsset or GuiProjectOperation.RemoveAsset) _assets = new(Assets);
            if (operation is GuiProjectOperation.AddAsset or GuiProjectOperation.SetSettings) _files = new(PendingFiles);
            if (operation == GuiProjectOperation.SetSettings) _project = Project;
        }

        public ClipDraftDTO Read(Guid id) => Clips.GetValueOrDefault(id) ?? throw new KeyNotFoundException("Clip not found.");
        public ClipDraftDTO Edit(Guid id)
        {
            if (!_originals.ContainsKey(id))
            {
                _originals[id] = Read(id);
                Clips[id] = CloneGuiData(Read(id));
            }
            return Read(id);
        }
        public void Put(ClipDraftDTO clip)
        {
            _originals.TryAdd(clip.Id, Clips.GetValueOrDefault(clip.Id));
            Clips[clip.Id] = clip;
        }
        public void Remove(Guid id)
        {
            _originals.TryAdd(id, Read(id));
            Clips.Remove(id);
        }
        public void FinishRequest(bool rollback)
        {
            if (rollback)
            {
                foreach (var (id, clip) in _originals)
                    if (clip is null) Clips.Remove(id);
                    else Clips[id] = clip;
                if (_tracks is not null) { Tracks.Clear(); Tracks.UnionWith(_tracks); }
                if (_assets is not null) { Assets.Clear(); foreach (var item in _assets) Assets.Add(item.Key, item.Value); }
                if (_files is not null) { PendingFiles.Clear(); foreach (var item in _files) PendingFiles.Add(item.Key, item.Value); }
                if (_project is not null) Project = _project;
            }
            _originals.Clear();
            _tracks = null; _assets = null; _files = null; _project = null;
        }
    }

    private GuiBatchState? _guiBatch;
    private Guid _lastGuiBatchId, _lastGuiBatchOwner;
    private object? _lastGuiBatchResult;
    private readonly SemaphoreSlim _guiEditGate = new(1, 1);
    private CancellationTokenSource? _guiBatchCancellation;
    private bool _guiBatchCancelPending;
    private readonly List<(Window Window, INavigation Navigation, GuiBatchPage Page, Microsoft.Maui.ITitleBar? TitleBar, string Title)> _guiBatchPages = [];
    private bool IsGuiBatchOverlayVisible => _guiBatchCancellation is not null || _guiBatchPages.Count > 0;

    private static T CloneGuiData<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, savingOpts), savingOpts)!;

    private async Task SetGuiBatchOverlayAsync(bool visible)
    {
        if (visible)
        {
            foreach (var window in MainMultiWindowView.StandaloneWindows.Select(w => w.Window).Prepend(Window).OfType<Window>().Distinct().ToArray())
            {
                var navigation = ReferenceEquals(window, Window) ? Navigation : window.Page!.Navigation;
                var page = new GuiBatchPage(CancelGuiBatchFromUiAsync);
                Microsoft.Maui.ITitleBar? titleBar = null;
#if WINDOWS || MACCATALYST
                titleBar = window.TitleBar;
                window.TitleBar = null;
#endif
                _guiBatchPages.Add((window, navigation, page, titleBar, window.Title));
                await navigation.PushModalAsync(page, false);
            }
        }
        else
        {
            try
            {
                foreach (var (window, navigation, page, titleBar, title) in _guiBatchPages.ToArray().Reverse())
                {
                    page.DisableCancellation();
                    if (Application.Current?.Windows.Contains(window) == true)
                    {
                        if (navigation.ModalStack.Contains(page))
                        {
                            if (navigation.ModalStack.LastOrDefault() != page)
                                throw new InvalidOperationException("Close the active modal before leaving batch mode.");
                            await navigation.PopModalAsync(false);
                        }
#if WINDOWS || MACCATALYST
                        window.TitleBar = titleBar;
#endif
                        window.Title = title;
                    }
                    _guiBatchPages.RemoveAll(p => p.Page == page);
                }
                _guiBatchCancellation?.Dispose();
                _guiBatchCancellation = null;
            }
            catch (Exception ex)
            {
                foreach (var item in _guiBatchPages) item.Page.ShowError(ex);
                throw;
            }
        }
    }

    private async Task CancelGuiBatchFromUiAsync()
    {
        if (_guiBatchCancellation is not { } cts || _guiBatchCancelPending) return;
        _guiBatchCancelPending = true;
        foreach (var item in _guiBatchPages) item.Page.SetCancelling();
        try
        {
            Log("GUI RPC batch cancellation requested by the user.");
            cts.Cancel();
            CancelDynamicPreview();
            _renderOneFrameCts?.Cancel();
            await _guiEditGate.WaitAsync();
            try
            {
                if (ReferenceEquals(_guiBatchCancellation, cts)) await DiscardGuiBatchAsync();
            }
            finally { _guiEditGate.Release(); }
        }
        catch (Exception ex)
        {
            Log(ex, "Cancel GUI RPC batch", this);
            foreach (var item in _guiBatchPages) item.Page.ShowError(ex);
        }
        finally { _guiBatchCancelPending = false; }
    }

    private async Task DiscardGuiBatchAsync()
    {
        if (_guiBatch is not { } batch)
        {
            await SetGuiBatchOverlayAsync(false);
            return;
        }
        if (batch.Applied)
        {
            await ApplyGuiBatchDataToUiAsync(batch.Draft, batch.OriginalProject, batch.OriginalAssets, batch.OriginalTracks);
            ProjectInfo.SnapshotIDMapping = CloneGuiData(batch.OriginalProject.SnapshotIDMapping);
            if (batch.OriginalProjectDirty) _workspace.GetModule<ProjectModule>().MarkDirty();
            else _workspace.GetModule<ProjectModule>().MarkClean();
            ProjectInfo.LastChanged = batch.OriginalProject.LastChanged;
            ProjectInfo.PluginUsed = batch.OriginalProject.PluginUsed;
            if (CurrentSnapshotID == batch.Id)
                await WriteCurrentSnapshotAsync(batch.Draft, $"slot_{batch.Draft.SnapshotID}", batch.OriginalSnapshotDirty);
            ProjectInfo.NormallyExited = batch.OriginalProject.NormallyExited;
            _historyNavigatedByUndoRedo = batch.OriginalHistoryNavigatedByUndoRedo;
            await WriteSnapshotJsonAsync(Path.Combine(WorkingPath, ProjectJSONStructure.SnapshotMappingFileName), ProjectInfo.SnapshotIDMapping);
            await WriteSnapshotJsonAsync(Path.Combine(WorkingPath, "project.pjfc"), ProjectInfo);
            await DraftChangedAsync(this, new() { NoSave = true, DetailInfo = "RPC batch" }, throwOnFailure: true);
            ForwardClipChangeToWorkspace(this, new() { NoSave = true, DetailInfo = "RPC batch" });
            RefreshHistorySubWindowContent();
            _activeHistoryProvider?.NotifyExternalSnapshotChanged();
        }
        foreach (string path in batch.CopiedFiles) File.Delete(path);
        string slot = Path.Combine(WorkingPath, "saveSlots", $"slot_{batch.Id}");
        if (Directory.Exists(slot))
        {
            File.Delete(Path.Combine(slot, "timeline.json"));
            File.Delete(Path.Combine(slot, "assets.json"));
            File.Delete(Path.Combine(slot, "timeline.json.tmp"));
            File.Delete(Path.Combine(slot, "assets.json.tmp"));
            if (!Directory.EnumerateFileSystemEntries(slot).Any()) Directory.Delete(slot);
        }
        batch.Saved = false;
        Log($"GUI RPC batch {batch.Id} cancelled ({batch.OperationCount} operations).");
        _lastGuiBatchId = batch.Id;
        _lastGuiBatchOwner = batch.Owner;
        _lastGuiBatchResult = null;
        _guiBatch = null;
        await SetGuiBatchOverlayAsync(false);
    }

    private async Task<object?> ExecuteGuiRequestAsync(GuiProjectRequest request, Guid connectionId, CancellationToken ct)
    {
        await _guiEditGate.WaitAsync(ct);
        bool wasGuiRpcRequest = _guiRpcRequest.Value;
        _guiRpcRequest.Value = true;
        try
        {
            if (request.Operation == GuiProjectOperation.StartBatch)
            {
                if (IsReadonly || IsRemoteProject) throw new InvalidOperationException("Batch editing requires a writable local GUI project.");
                if (connectionId == Guid.Empty || request.BatchId == Guid.Empty) throw new ArgumentException("Batch and connection IDs are required.");
                if (ProjectInfo.SnapshotIDMapping.ContainsKey(request.BatchId) || request.BatchId == _lastGuiBatchId
                    || Directory.Exists(Path.Combine(WorkingPath, "saveSlots", $"slot_{request.BatchId}")))
                    throw new ArgumentException("Use a new batch ID.");
                if (IsGuiBatchOverlayVisible)
                    throw new InvalidOperationException("A project batch is already active.");
                _guiBatchCancellation = new();
                using var batchCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _guiBatchCancellation.Token);
                ct = batchCts.Token;
                try
                {
                    await SetGuiBatchOverlayAsync(true);
                    ct.ThrowIfCancellationRequested();
                    if (isPlaying || previewer.HasDynamicPreview) await PauseLivePreview(refreshPreview: false);
                    CancelDynamicPreview();
                    DynamicPreviewProvider.CancelClipWarmups();
                    _renderOneFrameCts?.Cancel();
                    foreach (var cts in _perClipThumbCts.Values) cts.Cancel();
                    CancelGeneratedSoundTrackSync();
                    lock (_snapshotSaveGate)
                    {
                        _snapshotSaveCts?.Cancel();
                    }
                    await saveLocker.WaitAsync(ct);
                    saveLocker.Release();
                    // Flush only a change that was already pending before entering batch mode.
                    if (_pendingSnapshotSaveArgs is not null || CurrentSnapshotID == Guid.Empty) await Save(args: _pendingSnapshotSaveArgs, throwOnFailure: true);
                    ct.ThrowIfCancellationRequested();
                    var draft = new DraftStructureJSON
                    {
                        Clips = Clips.Values.Where(c => !c.IsGhost && !c.IsShadow)
                            .Select(c => CloneGuiData(DraftImportAndExportHelper.ExportClipElementFromDraftPage(this, c, true, false))).ToArray(),
                        Duration = ProjectDuration,
                        ProjectSettings = ProjectSettingsSnapshot.Capture(ProjectInfo),
                    };
                    _currentSnapshot?.CopyTo(draft);
                    var originalProject = CloneGuiData(ProjectInfo);
                    originalProject.SnapshotIDMapping = CloneGuiData(ProjectInfo.SnapshotIDMapping);
                    _guiBatch = new()
                    {
                        Id = request.BatchId, Owner = connectionId, Project = CloneGuiData(ProjectInfo), Draft = draft,
                        Clips = draft.Clips.ToDictionary(c => c.Id),
                        Assets = CloneGuiData(Assets.Values.ToList()).ToDictionary(a => a.AssetId!), Tracks = Tracks.Keys.ToHashSet(),
                        OriginalProject = originalProject, OriginalAssets = Assets.ToDictionary(a => a.Key, a => a.Value), OriginalTracks = Tracks.Keys.ToHashSet(),
                        OriginalSnapshotDirty = _currentSnapshot?.IsDirty ?? true,
                        OriginalProjectDirty = _workspace.GetModule<ProjectModule>().IsDirty,
                        OriginalHistoryNavigatedByUndoRedo = _historyNavigatedByUndoRedo,
                    };
                    Log($"GUI RPC batch {request.BatchId} started.");
                    return new { BatchId = request.BatchId, OperationCount = 0 };
                }
                catch { await SetGuiBatchOverlayAsync(false); throw; }
            }
            if (request.Operation == GuiProjectOperation.CompleteBatch && _guiBatch is null
                && request.BatchId == _lastGuiBatchId && connectionId == _lastGuiBatchOwner && _lastGuiBatchResult is not null)
                return _lastGuiBatchResult;
            if (request.Operation == GuiProjectOperation.StopBatch && _guiBatch is null
                && request.BatchId != Guid.Empty && request.BatchId == _lastGuiBatchId
                && connectionId == _lastGuiBatchOwner && _lastGuiBatchResult is null)
                return new { BatchId = request.BatchId, Cancelled = true, Saved = false };
            if (_guiBatch is not { } batch)
            {
                if (request.BatchId != Guid.Empty || request.Operation is GuiProjectOperation.CompleteBatch or GuiProjectOperation.StopBatch)
                    throw new InvalidOperationException("No matching project batch is active.");
                return await ExecuteGuiProjectAsync(request, ct);
            }
            if (request.Operation is GuiProjectOperation.ExportFrameSequence or GuiProjectOperation.ExportAudioSamples
                or GuiProjectOperation.GetFramePreview or GuiProjectOperation.GetClipFramePreview or GuiProjectOperation.GetClipTransformPreview)
                throw new InvalidOperationException("Complete or cancel the batch before requesting a preview.");
            if (connectionId != batch.Owner)
            {
                if (!request.Operation.ToString().StartsWith("Get", StringComparison.Ordinal))
                    throw new InvalidOperationException("Another RPC connection owns the active project batch.");
                return await ExecuteGuiProjectAsync(request, ct);
            }
            if (request.BatchId != batch.Id) throw new InvalidOperationException("The request does not identify the active project batch.");
            if (request.Operation == GuiProjectOperation.StopBatch)
            {
                await DiscardGuiBatchAsync();
                return new { BatchId = batch.Id, Cancelled = !batch.Saved, batch.Saved };
            }
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _guiBatchCancellation!.Token);
            ct = requestCts.Token;
            ct.ThrowIfCancellationRequested();
            if (request.Operation == GuiProjectOperation.CompleteBatch) return await CompleteGuiBatchAsync(request, batch, ct);
            if (batch.Applied) throw new InvalidOperationException("The batch is being committed. Retry Complete-ProjectBatch.");
            if (request.Operation is GuiProjectOperation.Save or GuiProjectOperation.UndoProjectHistory or GuiProjectOperation.RedoProjectHistory
                or GuiProjectOperation.RestoreProjectHistory or GuiProjectOperation.GetFramePreview or GuiProjectOperation.GetClipFramePreview
                or GuiProjectOperation.GetClipTransformPreview or GuiProjectOperation.SaveClipEffectGroup
                or GuiProjectOperation.AddExternalSource or GuiProjectOperation.SetExternalSource or GuiProjectOperation.RemoveExternalSource)
                throw new InvalidOperationException("Complete or cancel the batch before running this operation.");
            try
            {
                batch.BeginRequest(request.Operation);
                var result = await ExecuteGuiBatchDataAsync(request, batch, ct);
                ct.ThrowIfCancellationRequested();
                batch.FinishRequest(false);
                if (!request.Operation.ToString().StartsWith("Get", StringComparison.Ordinal)) batch.OperationCount++;
                return result;
            }
            catch { batch.FinishRequest(true); throw; }
        }
        finally
        {
            _guiRpcRequest.Value = wasGuiRpcRequest;
            _guiEditGate.Release();
        }
    }

    private async Task<object> CompleteGuiBatchAsync(GuiProjectRequest request, GuiBatchState batch, CancellationToken ct)
    {
        if (batch.OperationCount > 0)
        {
            if (!batch.Applied)
            {
                if (FindSlotDirectory(batch.Draft.SnapshotID) is { } previousSlot)
                {
                    string path = Path.Combine(previousSlot, "timeline.json");
                    var previous = JsonSerializer.Deserialize<DraftStructureJSON>(await File.ReadAllTextAsync(path, ct), savingOpts)!;
                    if (previous.ProjectSettings is null)
                    {
                        previous.ProjectSettings = batch.Draft.ProjectSettings;
                        await WriteSnapshotJsonAsync(path, previous);
                    }
                }
                var files = batch.Assets.Values.Select(a => a.Path).Append(batch.Project.ThumbPath).ToHashSet();
                foreach (var (destination, source) in batch.PendingFiles.Where(p => files.Contains(p.Key)))
                {
                    ct.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    bool overwrite = batch.CopiedFiles.Contains(destination);
                    await using var input = File.OpenRead(source);
                    await using var output = new FileStream(destination, overwrite ? FileMode.Create : FileMode.CreateNew,
                        FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                    batch.CopiedFiles.Add(destination);
                    await input.CopyToAsync(output, ct);
                }
                ct.ThrowIfCancellationRequested();
                try
                {
                    var draft = CloneGuiData(batch.Draft);
                    draft.Clips = batch.Clips.Values.ToArray();
                    batch.Applied = true;
                    await ApplyGuiBatchDataToUiAsync(draft, batch.Project, batch.Assets, batch.Tracks);
                }
                catch
                {
                    await ApplyGuiBatchDataToUiAsync(batch.Draft, batch.OriginalProject, batch.OriginalAssets, batch.OriginalTracks);
                    batch.Applied = false;
                    throw;
                }
            }
            ct.ThrowIfCancellationRequested();
            if (!batch.Saved)
            {
                await SaveCoreAsync(false, CreateGuiProjectChange(request), true,
                    requestedSnapshotId: batch.Id, previousSnapshotId: batch.Draft.SnapshotID);
                batch.Saved = true;
            }
            ct.ThrowIfCancellationRequested();
            await DraftChangedAsync(this, new() { NoSave = true, DetailInfo = "RPC batch" }, throwOnFailure: true, cancellationToken: ct);
            ct.ThrowIfCancellationRequested();
            if (!batch.Notified)
            {
                var change = CreateGuiProjectChange(request);
                change.DetailInfo = "RPC batch";
                ForwardClipChangeToWorkspace(this, change);
                batch.Notified = true;
            }
            RefreshHistorySubWindowContent();
            _activeHistoryProvider?.NotifyExternalSnapshotChanged();
        }
        var result = new { BatchId = batch.Id, batch.OperationCount, CurrentSnapshotId = CurrentSnapshotID };
        _lastGuiBatchId = batch.Id;
        _lastGuiBatchOwner = batch.Owner;
        _lastGuiBatchResult = result;
        Log($"GUI RPC batch {batch.Id} committed ({batch.OperationCount} operations).");
        foreach (var item in _guiBatchPages) item.Page.DisableCancellation();
        if (batch.Saved && MaximumSaveSlot >= 0)
        {
            try
            {
                await PruneOldestSaveSlotsAsync();
                await WriteSnapshotJsonAsync(Path.Combine(WorkingPath, ProjectJSONStructure.SnapshotMappingFileName), ProjectInfo.SnapshotIDMapping);
            }
            catch (Exception ex) { Log(ex, "Prune GUI RPC batch history", this); }
        }
        _guiBatch = null;
        await SetGuiBatchOverlayAsync(false);
        return result;
    }

    private async Task ApplyGuiBatchDataToUiAsync(DraftStructureJSON draft, ProjectJSONStructure project,
        Dictionary<string, AssetItem> assets, HashSet<int> tracks)
    {
        var imported = DraftImportAndExportHelper.ImportFromJSON(draft, project);
        ProjectSettingsSnapshot.Capture(project).ApplyTo(ProjectInfo);
        foreach (var track in Tracks.Values) track.Children.Clear();
        Clips = new ConcurrentDictionary<Guid, ClipElementUI>(imported.Item1);
        Assets = new ConcurrentDictionary<string, AssetItem>(assets);
        ClipEditor.SetAssets(Assets);
        _workspace.GetModule<AssetModule>().Reset(Assets.Values);
        foreach (int track in Tracks.Keys.Except(tracks).ToArray()) RemoveEmptyGuiBatchTrack(track);
        foreach (int track in tracks.Order()) if (!Tracks.ContainsKey(track)) AddATrack(track);
        foreach (var clip in Clips.Values.OrderBy(c => c.origTrack).ThenBy(c => c.origX))
        {
            RegisterClip(clip, false);
            AddAClip(clip, false);
        }
        NormalizeLoadedClipFrameSemantics();
        await UpdateAdjacencyForTrack();
        UpdateTimelineWidth();
        previewer.ProjectJson = JsonSerializer.Serialize(ProjectInfo, savingOpts);
        await RefreshRestoredSelectionAsync(refreshPreview: false);
    }

    private async Task CloseGuiBatchAsync()
    {
        _guiBatchCancellation?.Cancel();
        await _guiEditGate.WaitAsync();
        try { await DiscardGuiBatchAsync(); }
        finally { _guiEditGate.Release(); }
    }

    private void RemoveEmptyGuiBatchTrack(int id)
    {
        var heads = id >= SubTrackOffset ? SubTrackHeadLayout : TrackHeadLayout;
        var contents = id >= SubTrackOffset ? SubTrackContentLayout : TrackContentLayout;
        var head = heads.Children.OfType<Border>().FirstOrDefault(b => GetTrackIdFromHeaderBorder(b) == id);
        var content = contents.Children.OfType<Border>().FirstOrDefault(b => b.BindingContext is int track && track == id);
        if (head is not null) heads.Children.Remove(head);
        if (content is not null) contents.Children.Remove(content);
        Tracks.TryRemove(id, out _);
    }

    #endregion

    #region Data editing

    private static object GuiClipSnapshot(ClipDraftDTO dto) => new
    {
        ClipId = dto.Id, dto.Name, Type = dto.ClipType.ToString(), TrackId = dto.LayerIndex, dto.StartFrame,
        DurationFrames = dto.Duration, SourceStartFrame = dto.RelativeStartFrame, dto.FilePath,
        WidthPixels = dto.TargetWidth, HeightPixels = dto.TargetHeight, XPixels = dto.TargetX, YPixels = dto.TargetY,
    };

    private IEnumerable<ClipDraftDTO> GetGuiVectorDtos(GuiBatchState? batch) => batch is null
        ? Clips.Values.Where(IsGuiVectorClip).Select(GuiEditableDto) : batch.Clips.Values;

    private ClipDraftDTO SaveGuiVectorClip(ClipDraftDTO dto, List<IVectorComponent> components, CancellationToken ct, GuiBatchState? batch)
    {
        ct.ThrowIfCancellationRequested();
        if (batch is null) return GuiEditableDto(ReplaceGuiVectorClip(dto, components, ct));
        batch.Put(dto);
        return dto;
    }

    private static ClipDraftDTO NewGuiBatchClip(GuiBatchState batch, GuiEditArgs a, ClipMode type, string name)
    {
        var dto = new ClipDraftDTO
        {
            Id = Guid.NewGuid(), Name = a.String("Name", name)!, ClipType = type,
            TypeName = type == ClipMode.AudioClip ? nameof(SoundTrackToClipWrapper) : type.ToString(),
            FromPlugin = InternalPluginBase.InternalPluginBaseID, LayerIndex = a.UInt("TrackId"), SubLayerIndex = a.UInt("TrackId"),
            StartFrame = a.UInt("StartFrame"), Duration = a.UInt("DurationFrames", 300), RelativeStartFrame = a.UInt("SourceStartFrame"),
            FrameTime = 1f / batch.Project.TargetFrameRate, SecondPerFrameRatio = 1,
            IsInfiniteLength = true, TargetWidth = a.Int("WidthPixels", batch.Project.RelativeWidth),
            TargetHeight = a.Int("HeightPixels", batch.Project.RelativeHeight),
            MetaData = new()
            {
                [ClipDraftDTO.ProjectFrameRateMetaKey] = batch.Project.TargetFrameRate,
                [ClipDraftDTO.FrameSemanticVersionMetaKey] = ClipDraftDTO.CurrentFrameSemanticVersion,
            },
        };
        return ApplyGuiBatchParameters(batch, dto, a.Json);
    }

    private static ClipDraftDTO ApplyGuiBatchParameters(GuiBatchState batch, ClipDraftDTO dto, JsonElement parameters)
    {
        var node = JsonSerializer.SerializeToNode(dto, savingOpts)!;
        foreach (var (source, target) in new[] { ("Name", "Name"), ("TrackId", "LayerIndex"), ("StartFrame", "StartFrame"),
            ("DurationFrames", "Duration"), ("SourceStartFrame", "RelativeStartFrame"), ("WidthPixels", "TargetWidth"),
            ("HeightPixels", "TargetHeight"), ("XPixels", "TargetX"), ("YPixels", "TargetY"), ("FilePath", "FilePath") })
            if (parameters.TryGetProperty(source, out var value)) node[target] = JsonNode.Parse(value.GetRawText());
        dto = node.Deserialize<ClipDraftDTO>(savingOpts)!;
        if (dto.Duration == 0 || dto.LayerIndex > int.MaxValue || !batch.Tracks.Contains((int)dto.LayerIndex)
            || (ulong)dto.StartFrame + dto.Duration > int.MaxValue || dto.RelativeStartFrame > int.MaxValue)
            throw new ArgumentException("Invalid clip track or timing.");
        foreach (string key in new[] { "WidthPixels", "HeightPixels" })
            if (parameters.TryGetProperty(key, out var value) && value.GetInt32() <= 0) throw new ArgumentException($"Invalid {key}.");
        return dto;
    }

    private async Task<object?> ExecuteGuiBatchDataAsync(GuiProjectRequest request, GuiBatchState batch, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(request.ParametersJson);
        var a = new GuiEditArgs(document.RootElement);
        var operation = request.Operation;
        ct.ThrowIfCancellationRequested();
        switch (operation)
        {
            case GuiProjectOperation.GetInfo:
                return new
                {
                    SessionId = _guiSessionId, Name = batch.Project.ProjectName, WidthPixels = batch.Project.RelativeWidth,
                    HeightPixels = batch.Project.RelativeHeight, FrameRate = batch.Project.TargetFrameRate,
                    DurationFrames = batch.Clips.Values.Select(c => checked(c.StartFrame + GuiBatchDuration(c))).DefaultIfEmpty().Max(),
                    ClipCount = batch.Clips.Count, TrackCount = batch.Tracks.Count, AssetCount = batch.Assets.Count,
                    WorkingPath, CurrentFrame, CurrentSnapshotId = CurrentSnapshotID, BatchId = batch.Id,
                };
            case GuiProjectOperation.GetClip:
                return batch.Clips.Values.Where(c => (!a.Has("ClipId") || c.Id == a.Guid("ClipId"))
                    && (!a.Has("TrackId") || c.LayerIndex == a.UInt("TrackId"))
                    && (!a.Has("Name") || c.Name.Contains(a.String("Name")!, StringComparison.OrdinalIgnoreCase))).Select(GuiClipSnapshot).ToArray();
            case GuiProjectOperation.AddClip:
                {
                    if (a.Has("FilePath") && a.Has("AssetId")) throw new ArgumentException("Choose FilePath or AssetId.");
                    var asset = a.Has("AssetId") ? batch.Assets.GetValueOrDefault(a.String("AssetId")!)
                        ?? throw new KeyNotFoundException("Asset not found.") : a.Has("FilePath") ? ProbeGuiBatchAsset(a.String("FilePath")!, a.String("Name")) : null;
                    if (asset?.AssetType == AssetType.Subtitle && a.Has("AssetId"))
                    {
                        var parameters = JsonNode.Parse(a.Json.GetRawText())!.AsObject();
                        parameters.Remove("AssetId");
                        parameters["FilePath"] = batch.PendingFiles.GetValueOrDefault(asset.Path!, asset.Path!);
                        return ExecuteGuiBatchSubtitle(GuiProjectOperation.AddSubtitleClip,
                            new GuiEditArgs(JsonSerializer.SerializeToElement(parameters)), batch);
                    }
                    if (asset?.AssetType == AssetType.VectorComposition)
                    {
                        var component = VectorClipServices.Import(batch.PendingFiles.GetValueOrDefault(asset.Path!, asset.Path!));
                        var dto = NewGuiBatchClip(batch, a, ClipMode.VectorComponentClip, asset.Name);
                        dto.IsInfiniteLength = false;
                        dto.SourceDuration = dto.Duration;
                        VectorClipServices.SetDefinition(batch.Project, dto, [component], true);
                        batch.Put(dto);
                        return GuiClipSnapshot(dto);
                    }
                    var clip = NewGuiBatchClip(batch, a, asset?.GetClipMode() ?? ClipMode.Special, asset?.Name ?? "New Clip");
                    if (asset is not null)
                    {
                        clip.FilePath = a.Has("AssetId") ? "$" + asset.AssetId : asset.Path;
                        clip.IsInfiniteLength = asset.isInfiniteLength;
                        clip.SourceDuration = asset.Duration;
                        clip.FrameTime = asset.SecondPerFrame > 0 ? asset.SecondPerFrame : clip.FrameTime;
                        if (a.Has("AssetId") && !a.Has("DurationFrames")) clip.Duration = asset.isInfiniteLength
                            ? SettingsManager.GetSettingAs<uint>("Edit_DefaultInfLengthClipLength", 300, 300)
                            : Math.Max(1u, AssetDatabase.DetermineLengthInFrame(asset, batch.Project.TargetFrameRate));
                        if ((ulong)clip.StartFrame + clip.Duration > int.MaxValue) throw new ArgumentException("Clip exceeds the timeline range.");
                        if (!a.Has("WidthPixels") && asset.Width > 0) clip.TargetWidth = asset.Width;
                        if (!a.Has("HeightPixels") && asset.Height > 0) clip.TargetHeight = asset.Height;
                    }
                    batch.Put(clip);
                    SyncGuiBatchAudio(batch, clip);
                    return GuiClipSnapshot(clip);
                }
            case GuiProjectOperation.SetClip:
                {
                    var clip = ApplyGuiBatchParameters(batch, batch.Read(a.Guid("ClipId")), a.Json);
                    batch.Put(clip);
                    SyncGuiBatchAudio(batch, clip);
                    return GuiClipSnapshot(clip);
                }
            case GuiProjectOperation.CopyClip:
                {
                    var clip = CloneGuiData(batch.Read(a.Guid("ClipId")));
                    clip.Id = Guid.NewGuid();
                    clip.Name = a.String("Name", "Copy of " + clip.Name)!;
                    clip.BindedSoundTrack = "";
                    clip.StartFrame = a.UInt("StartFrame", checked(clip.StartFrame + clip.Duration));
                    clip.LayerIndex = a.UInt("TrackId", clip.LayerIndex);
                    clip.EffectProviders = TransformProcessing.CopySingleInputs(clip.EffectProviders);
                    clip.MetaData?.Remove(SoundTrackMetadata.ProbeSourceKey);
                    clip.MetaData?.Remove(SoundTrackMetadata.ProbeHasStreamKey);
                    clip = ApplyGuiBatchParameters(batch, clip, a.Json);
                    batch.Put(clip);
                    SyncGuiBatchAudio(batch, clip);
                    return GuiClipSnapshot(clip);
                }
            case GuiProjectOperation.RemoveClip:
                RemoveGuiBatchClip(batch, a.Guid("ClipId"));
                return null;
            case GuiProjectOperation.GetTrack:
                return batch.Tracks.Order().Where(t => !a.Has("TrackId") || t == a.Int("TrackId"))
                    .Select(t => new { TrackId = t, ClipCount = batch.Clips.Values.Count(c => c.LayerIndex == t) }).ToArray();
            case GuiProjectOperation.AddTrack:
            case GuiProjectOperation.AddSubTrack:
                {
                    bool sub = operation == GuiProjectOperation.AddSubTrack;
                    int id = a.Int("TrackId", batch.Tracks.Where(t => (t >= SubTrackOffset) == sub).DefaultIfEmpty(sub ? SubTrackOffset - 1 : -1).Max() + 1);
                    if (id < 0 || (id >= SubTrackOffset) != sub || batch.Tracks.Contains(id)) throw new ArgumentException("Invalid or duplicate track ID.");
                    for (int i = sub ? SubTrackOffset : 0; i <= id; i++) batch.Tracks.Add(i);
                    return new { TrackId = id };
                }
            case GuiProjectOperation.RemoveTrack:
                {
                    int id = a.Int("TrackId");
                    if (!batch.Tracks.Contains(id)) throw new KeyNotFoundException("Track not found.");
                    var removed = batch.Clips.Values.Where(c => c.LayerIndex == id).Select(c => c.Id).ToArray();
                    foreach (var clipId in removed) if (batch.Clips.ContainsKey(clipId)) RemoveGuiBatchClip(batch, clipId);
                    foreach (var clip in batch.Clips.Values.Where(c => c.LayerIndex > id && (c.LayerIndex >= SubTrackOffset) == (id >= SubTrackOffset)).ToArray())
                    {
                        var shifted = batch.Edit(clip.Id);
                        shifted.LayerIndex--;
                        if (shifted.SubLayerIndex > id && (shifted.SubLayerIndex >= SubTrackOffset) == (id >= SubTrackOffset)) shifted.SubLayerIndex--;
                    }
                    var tracks = batch.Tracks.Where(t => t != id).Select(t => t > id && (t >= SubTrackOffset) == (id >= SubTrackOffset) ? t - 1 : t).ToArray();
                    batch.Tracks.Clear();
                    batch.Tracks.UnionWith(tracks);
                    return new { TrackId = id, RemovedClipIds = removed, Tracks = batch.Tracks.Order().ToArray() };
                }
            case GuiProjectOperation.GetAsset:
                return batch.Assets.Values.Where(v => (!a.Has("AssetId") || v.AssetId == a.String("AssetId"))
                    && (!a.Has("Name") || v.Name.Contains(a.String("Name")!, StringComparison.OrdinalIgnoreCase)))
                    .Select(v => new { v.AssetId, v.Name, v.Path, Type = v.AssetType.ToString(), v.Width, v.Height, v.Duration }).ToArray();
            case GuiProjectOperation.AddAsset:
                {
                    var copyToProject = a.Has("CopyToProject") ? a.Bool("CopyToProject") : false;
                    var asset = ProbeGuiBatchAsset(a.String("FilePath")!, a.String("Name"));
                    asset.IsReference = !copyToProject;
                    if (!asset.IsReference)
                    {
                        string destination = Path.Combine(WorkingPath, "asset", asset.AssetId + Path.GetExtension(asset.Path));
                        batch.PendingFiles.Add(destination, asset.Path!);
                        asset.Path = destination;
                    }
                    batch.Assets.Add(asset.AssetId!, asset);
                    return new { asset.AssetId, asset.Name, asset.Path };
                }
            case GuiProjectOperation.RemoveAsset:
                if (!batch.Assets.Remove(a.String("AssetId")!)) throw new KeyNotFoundException("Asset not found.");
                return null;
            case GuiProjectOperation.AddTextClip:
            case GuiProjectOperation.SetTextClipStyle:
                {
                    var clip = operation == GuiProjectOperation.AddTextClip ? NewGuiBatchClip(batch, a, ClipMode.TextClip, a.String("Text", "Text")!) : batch.Edit(a.Guid("ClipId"));
                    if (clip.ClipType != ClipMode.TextClip) throw new ArgumentException("Clip is not a text clip.");
                    var style = operation == GuiProjectOperation.AddTextClip ? TimelineMcpLiveService.ResolveTextStyleProvider(a.String("StyleId") ?? "")
                        : TextClipStyles.Restore(clip.MetaData!);
                    if (style is null) throw new KeyNotFoundException("Text style not found.");
                    if (operation == GuiProjectOperation.AddTextClip) style.BasicText = a.String("Text", "")!;
                    TimelineMcpLiveService.ApplyTextStyleFields(style, a.Fields(), strict: true);
                    clip.MetaData ??= new();
                    clip.MetaData["TextEntries"] = TimelineMcpLiveService.BuildTextEntriesWithFontFallback(style).ToList();
                    TextClipStyles.Save(style, clip.MetaData);
                    SyncGuiBatchTextArguments(clip, style);
                    batch.Put(clip);
                    return GuiClipSnapshot(clip);
                }
            case GuiProjectOperation.GetVectorClip: case GuiProjectOperation.AddVectorCanvasClip: case GuiProjectOperation.AddVectorComponentClip:
            case GuiProjectOperation.SetVectorClip: case GuiProjectOperation.GetVectorComponent: case GuiProjectOperation.AddVectorComponent:
            case GuiProjectOperation.SetVectorComponent: case GuiProjectOperation.RemoveVectorComponent:
                return ExecuteGuiVectorOperation(operation, a.Json, ct, batch);
            case GuiProjectOperation.GetSubtitleClip: case GuiProjectOperation.AddSubtitleClip: case GuiProjectOperation.SetSubtitleClip:
            case GuiProjectOperation.GetSubtitleCue: case GuiProjectOperation.AddSubtitleCue: case GuiProjectOperation.SetSubtitleCue: case GuiProjectOperation.RemoveSubtitleCue:
                return ExecuteGuiBatchSubtitle(operation, a, batch);
            case GuiProjectOperation.GetSettings: case GuiProjectOperation.SetSettings:
                return ExecuteGuiBatchSettings(operation, a, batch);
            case GuiProjectOperation.GetClipProperties: case GuiProjectOperation.SetClipProperties:
            case GuiProjectOperation.GetClipGeometry: case GuiProjectOperation.SetClipGeometry:
                return ExecuteGuiBatchProperties(operation, a, batch);
            case GuiProjectOperation.AddSolidColorClip: case GuiProjectOperation.AddEmptyVideoClip: case GuiProjectOperation.AddMaskClip:
                return AddGuiBatchSourceClip(operation, a, batch);
            case GuiProjectOperation.AddExternalSourceClip:
                return await AddGuiExternalSourceClipAsync(a, ct, batch);
            case GuiProjectOperation.GetClipEffectProvider:
                return CopyGuiEffectProviders(new GuiEffectClip(batch.Read(a.Guid("ClipId")))).Values
                    .Where(p => (!a.Has("ProviderId") || p.Id == a.Guid("ProviderId")) && (!a.Has("TypeName") || p.TypeName == a.String("TypeName")))
                    .Select(p => GuiProvider(p, a.Guid("ClipId"))).ToArray();
            case GuiProjectOperation.GetClipTransform: case GuiProjectOperation.SetClipTransform: case GuiProjectOperation.RemoveClipTransform:
                return ExecuteGuiBatchTransform(operation, a, batch);
            case GuiProjectOperation.SplitClip: case GuiProjectOperation.JoinClip: case GuiProjectOperation.SplitClipGroup:
                return ExecuteGuiBatchTimeline(operation, a, batch);
            case GuiProjectOperation.GetEffectProviderType: case GuiProjectOperation.GetEffectProviderField:
            case GuiProjectOperation.GetTextStyle: case GuiProjectOperation.GetTextStyleField: case GuiProjectOperation.GetVectorComponentType:
            case GuiProjectOperation.GetMaskSourceType: case GuiProjectOperation.GetTransformType: case GuiProjectOperation.GetEffectGroupTemplate:
            case GuiProjectOperation.GetProjectHistory: case GuiProjectOperation.GetExternalSource:
                return await ExecuteGuiProjectAsync(request, ct);
            default:
                return await ExecuteGuiEffectOperationAsync(operation, a.Json, ct, batchClip: new GuiEffectClip(batch.Edit(a.Guid("ClipId"))));
        }
    }

    private static AssetItem ProbeGuiBatchAsset(string path, string? name)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Source file not found.", path);
        var asset = new AssetItem { AssetId = Guid.NewGuid().ToString(), Name = name ?? Path.GetFileNameWithoutExtension(path), Path = path,
            AssetType = AssetItem.GetAssetType(path), CreatedAt = DateTime.Now, IsReference = true };
        asset.ClipType = asset.GetClipMode();
        if (asset.AssetType == AssetType.Video)
        {
            using var source = PluginManager.CreateVideoSource(path);
            asset.Duration = source.TotalFrames; asset.SecondPerFrame = (float)(1 / source.Fps);
            asset.Width = source.Width; asset.Height = source.Height;
        }
        else if (asset.AssetType == AssetType.Audio)
        {
            using var source = PluginManager.CreateAudioSource(path);
            asset.Duration = source.Duration; asset.SecondPerFrame = 1;
        }
        return asset;
    }

    private static void RemoveGuiBatchClip(GuiBatchState batch, Guid id)
    {
        var clip = batch.Read(id);
        if (clip.ClipType == ClipMode.AudioClip && SoundTrackMetadata.ReadSourceClipId(clip.MetaData) is { } source && batch.Clips.ContainsKey(source))
        {
            var video = batch.Edit(source);
            video.BindedSoundTrack = "";
            (video.MetaData ??= new())[SoundTrackMetadata.DetachedKey] = true;
        }
        if (Guid.TryParse(clip.BindedSoundTrack, out var audio) && batch.Clips.ContainsKey(audio)) batch.Remove(audio);
        batch.Remove(id);
        foreach (var current in batch.Clips.Values.Where(c => c.EffectProviders?.Any(p => TransformProcessing.ReadNextClip(p.MetaData) == id) == true).ToArray())
            batch.Edit(current.Id).EffectProviders = current.EffectProviders!.Where(p => TransformProcessing.ReadNextClip(p.MetaData) != id).ToArray();
    }

    #endregion

    #region Effect providers

    // The effect editor only needs clip data; live controls are optional.
    private sealed class GuiEffectClip
    {
        public ClipElementUI? Live { get; }
        public ClipDraftDTO Dto { get; }
        public Guid Id => Dto.Id;
        public ClipMode ClipType => Dto.ClipType;
        public Dictionary<string, object> ExtraData => Dto.MetaData ??= new();
        public IReadOnlyList<Stage> SupportedStages { get; set; }
        public Dictionary<Guid, IEffectProvider>? EffectProviders { get; set; }
        public int TargetX => Live?.TargetX ?? Dto.TargetX;
        public int TargetY => Live?.TargetY ?? Dto.TargetY;
        public int TargetWidth => Live?.TargetWidth ?? Dto.TargetWidth;
        public int TargetHeight => Live?.TargetHeight ?? Dto.TargetHeight;
        public float Rotation => Live?.Rotation ?? Dto.Rotation;

        public GuiEffectClip(DraftPage page, ClipElementUI clip)
        {
            Live = clip;
            Dto = DraftImportAndExportHelper.ExportClipElementFromDraftPage(page, clip, rebuildEffects: false);
            Dto.MetaData = clip.ExtraData;
            EffectProviders = clip.EffectProviders;
            SupportedStages = clip.SupportedStages;
        }

        public GuiEffectClip(ClipDraftDTO dto)
        {
            Dto = dto;
            EffectProviders = EffectBindingHelper.MigrateToEffectProviders(CloneGuiData(dto.EffectProviders), null);
            if (EffectProviders.Count != (dto.EffectProviders?.Length ?? 0)) throw new InvalidOperationException("A clip effect provider is unavailable.");
            SupportedStages = dto.ClipType == ClipMode.AudioClip ? EffectStages.Audio
                : dto.ClipType is ClipMode.TextClip or ClipMode.SubtitleClip ? EffectStages.Text
                : dto.ClipType is ClipMode.VectorCanvasClip or ClipMode.VectorComponentClip ? EffectStages.Vector
                : dto.TypeName == nameof(projectFrameCut.Render.ClipsAndTracks.VectorPhotoClip) || string.Equals(Path.GetExtension(dto.FilePath), ".svg", StringComparison.OrdinalIgnoreCase)
                    ? EffectStages.VectorImage : EffectStages.Picture;
        }

        public EffectTarget GetEffectSelectionTarget(Stage stage) => Live?.GetEffectSelectionTarget(stage) ?? (stage switch
        {
            Stage.ClipArguments => EffectTarget.ValueProvider,
            Stage.Text => EffectTarget.Text | EffectTarget.ValueProvider,
            Stage.VectorComponent => EffectTarget.VectorComponent | EffectTarget.ValueProvider,
            Stage.VectorPicture => EffectTarget.VectorPicture | EffectTarget.ValueProvider,
            Stage.SpeedVariance => EffectTarget.SpeedVariance | EffectTarget.ValueProvider,
            Stage.SourceReplacement => EffectTarget.SourceReplacement | EffectTarget.ValueProvider,
            Stage.Audio => EffectTarget.Audio | EffectTarget.ValueProvider,
            Stage.Transform => EffectTarget.Transform | EffectTarget.ValueProvider,
            Stage.Mixture => EffectTarget.Mixture | EffectTarget.ValueProvider,
            _ => EffectTarget.Video | EffectTarget.ColorAdjustment | EffectTarget.ValueProvider,
        });

        public EffectTarget GetEffectTarget() => Live?.GetEffectTarget() ?? (ClipType switch
        {
            ClipMode.Special or ClipMode.MarkingClip => EffectTarget.NotSpecified,
            ClipMode.AudioClip => EffectTarget.Audio,
            ClipMode.TextClip or ClipMode.SubtitleClip => EffectTarget.Text | EffectTarget.Video | EffectTarget.VectorPicture,
            ClipMode.VectorCanvasClip or ClipMode.VectorComponentClip => EffectTarget.Video | EffectTarget.VectorComponent | EffectTarget.VectorPicture,
            _ => EffectTarget.Video | (SupportedStages.Contains(Stage.VectorPicture) ? EffectTarget.VectorPicture : 0),
        });
    }

    private GuiEffectClip? FindGuiEffectClip(Guid id, bool batch)
        => batch ? _guiBatch!.Clips.TryGetValue(id, out var dto) ? new(dto) : null
            : Clips.TryGetValue(id, out var clip) ? new(this, clip) : null;

    private IEnumerable<GuiEffectClip> GetGuiEffectClips(bool batch)
        => batch ? _guiBatch!.Clips.Values.Select(c => new GuiEffectClip(c))
            : Clips.Values.Where(c => !c.IsGhost && !c.IsShadow).Select(c => new GuiEffectClip(this, c));

    private ClipArgumentProvider CreateGuiClipArguments(ClipElementUI clip, Guid id) => CreateGuiClipArguments(new GuiEffectClip(this, clip), id);
    private Dictionary<Guid, IEffectProvider> CopyGuiEffectProviders(ClipElementUI clip) => CopyGuiEffectProviders(new GuiEffectClip(this, clip));

    private static void SaveGuiBatchTextArguments(ClipDraftDTO clip, Dictionary<Guid, IEffectProvider> providers)
    {
        if (clip.ClipType != ClipMode.TextClip || TextClipStyles.Restore(clip.MetaData) is not { } style) return;
        var arguments = providers.Values.OfType<ClipArgumentProvider>().SingleOrDefault();
        if (arguments is null) return;
        var values = new Dictionary<string, object>(arguments.StaticValues);
        foreach (var (key, value) in arguments.StaticValues)
        {
            if (!key.StartsWith(TextClipStyles.ArgumentPrefix, StringComparison.Ordinal)) continue;
            string id = key[TextClipStyles.ArgumentPrefix.Length..];
            if (!style.SettableFields.TryGetValue(id, out var descriptor)) continue;
            if (!style.HandleSettableFieldsChange(descriptor, value, out var feedback)) throw new ArgumentException($"Invalid text style field '{id}': {feedback}");
            values[key] = TextClipStyles.ReadValue(style, id);
        }
        arguments.RestoreStaticValues(values);
        TextClipStyles.Save(style, clip.MetaData!);
        clip.MetaData!["TextEntries"] = style.BuildEntries().ToList();
    }

    private static void SyncGuiBatchTextArguments(ClipDraftDTO clip, ITextClipStyleProvider style)
    {
        var providers = EffectBindingHelper.MigrateToEffectProviders(clip.EffectProviders, null);
        foreach (var arguments in providers.Values.OfType<ClipArgumentProvider>())
        {
            bool Valid(string key) => !key.StartsWith(TextClipStyles.ArgumentPrefix, StringComparison.Ordinal)
                || style.SettableFields.ContainsKey(key[TextClipStyles.ArgumentPrefix.Length..]);
            arguments.RestoreStaticValues(arguments.StaticValues.Where(p => Valid(p.Key)).ToDictionary(p => p.Key,
                p => p.Key.StartsWith(TextClipStyles.ArgumentPrefix, StringComparison.Ordinal)
                    ? TextClipStyles.ReadValue(style, p.Key[TextClipStyles.ArgumentPrefix.Length..]) : p.Value));
            arguments.AnchorsBindingState = arguments.AnchorsBindingState.Where(p => Valid(p.Key)).ToDictionary();
            arguments.MetaData[TextClipStyles.FromKey] = style.FromPlugin;
            arguments.MetaData[TextClipStyles.TypeKey] = style.TypeName;
        }
        clip.EffectProviders = providers.Values.Select(EffectBindingHelper.SerializeProvider).ToArray();
    }

    #endregion

    #region Clip properties and sources

    private static readonly Guid GuiBatchCropId = new("a3a744cc-53b7-4d5e-8dd5-4c66077d9401");
    private static double GuiBatchNumber(Dictionary<string, object>? data, string key, double fallback = 0)
        => data?.TryGetValue(key, out var value) == true && double.TryParse(value?.ToString(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) ? number : fallback;

    private static object GuiBatchSettingsSnapshot(ProjectJSONStructure project)
    {
        uint Number(string key, uint fallback) => project.Properties.TryGetValue(key, out var value) && uint.TryParse(value, out var n) ? n : fallback;
        bool Flag(string key) => project.Properties.TryGetValue(key, out var value) && bool.TryParse(value, out var b) && b;
        return new { FrameRate = project.TargetFrameRate, WidthPixels = project.RelativeWidth, HeightPixels = project.RelativeHeight,
            EnableHDR = Flag("EnableHDR"), HdrMaximumBrightness = Number("HdrMaximumBrightness", 1000),
            SdrClipBrightness = Number("SdrClipBrightness", Number("sdrClipBrightness", 203)), CoverFilePath = project.ThumbPath,
            WriteCoverAsFirstFrame = Flag("WriteCoverAsFirstFrame"), project.UserDefinedProperties };
    }

    private object AddGuiBatchSourceClip(GuiProjectOperation operation, GuiEditArgs a, GuiBatchState batch)
    {
        var dto = NewGuiBatchClip(batch, a, operation == GuiProjectOperation.AddSolidColorClip ? ClipMode.SolidColorClip
            : operation == GuiProjectOperation.AddMaskClip ? ClipMode.MaskClip : ClipMode.VideoClip, "New Clip");
        if (operation == GuiProjectOperation.AddSolidColorClip)
        {
            foreach (var (key, channel) in new[] { ("Red", "R"), ("Green", "G"), ("Blue", "B") }) dto.MetaData![channel] = checked((ushort)a.Int(key, ushort.MaxValue));
            double alpha = a.Number("Alpha", 1);
            if (alpha is < 0 or > 1) throw new ArgumentException("Alpha must be between 0 and 1.");
            dto.MetaData!["A"] = (float)alpha;
            dto.MetaData["SolidColorOutputWidth"] = dto.TargetWidth;
            dto.MetaData["SolidColorOutputHeight"] = dto.TargetHeight;
            dto.MetaData["SolidColorUseFixedOutputSize"] = true;
        }
        else if (operation == GuiProjectOperation.AddMaskClip)
        {
            _ = MaskSources.Create(a.String("SourceType")!);
            dto.MetaData![MaskClip.SourceTypeKey] = a.String("SourceType")!;
            dto.MetaData["AllowFreeScaleResize"] = true;
            ApplyGuiBatchSourceArguments(dto, a);
        }
        else dto.FilePath = EmptyVideoSource.CreatePath(dto.TargetWidth, dto.TargetHeight);
        batch.Put(dto);
        return GuiClipSnapshot(dto);
    }

    private void ApplyGuiBatchSourceArguments(ClipDraftDTO dto, GuiEditArgs a)
    {
        var clip = new GuiEffectClip(dto);
        var providers = CopyGuiEffectProviders(clip);
        if (a.Has("MaskSourceType"))
        {
            _ = MaskSources.Create(a.String("MaskSourceType")!);
            dto.MetaData![MaskClip.SourceTypeKey] = a.String("MaskSourceType")!;
            foreach (var existing in providers.Values.OfType<ClipArgumentProvider>())
            {
                existing.RestoreStaticValues(existing.StaticValues.Where(x => !x.Key.StartsWith(MaskClip.ArgumentPrefix, StringComparison.Ordinal)).ToDictionary());
                foreach (string key in existing.AnchorsBindingState.Keys.Where(k => k.StartsWith(MaskClip.ArgumentPrefix, StringComparison.Ordinal)).ToArray()) existing.ClearFieldBinding(key);
            }
        }
        var provider = providers.Values.OfType<ClipArgumentProvider>().SingleOrDefault() ?? CreateGuiClipArguments(new GuiEffectClip(dto), Guid.NewGuid());
        if (a.Has("SourceArguments")) ApplyGuiProviderFields(provider,
            a.Fields("SourceArguments").ToDictionary(x => MaskClip.ArgumentPrefix + x.Key, x => x.Value), false);
        providers[provider.Id] = provider;
        dto.EffectProviders = providers.Values.Select(EffectBindingHelper.SerializeProvider).ToArray();
    }

    private object ExecuteGuiBatchProperties(GuiProjectOperation operation, GuiEditArgs a, GuiBatchState batch)
    {
        var clip = batch.Edit(a.Guid("ClipId"));
        clip.MetaData ??= new();
        if (operation is GuiProjectOperation.GetClipGeometry or GuiProjectOperation.SetClipGeometry)
        {
            if (clip.ClipType is ClipMode.AudioClip or ClipMode.MarkingClip) throw new ArgumentException("Clip has no picture geometry.");
            var providers = CopyGuiEffectProviders(new GuiEffectClip(clip));
            providers.TryGetValue(GuiBatchCropId, out var crop);
            int width = clip.TargetWidth > 0 ? clip.TargetWidth : batch.Project.RelativeWidth;
            int height = clip.TargetHeight > 0 ? clip.TargetHeight : batch.Project.RelativeHeight;
            double Field(string key, double fallback) => crop?.Fields.TryGetValue(key, out var field) == true
                ? Convert.ToDouble(EffectParamConvert.Normalize(field is StaticEffectArgumentField s ? s.Value
                    : field is DynamicEffectParamField d ? d.StaticFallbackValue : field.DefaultValue), CultureInfo.InvariantCulture) : fallback;
            bool direct = SoundTrackMetadata.ReadBool(clip.MetaData, "__Internal_DirectCropEnabled__");
            bool enabled = direct || crop?.Enabled == true;
            int x = direct ? clip.StartingX : (int)Field("StartX", 0), y = direct ? clip.StartingY : (int)Field("StartY", 0);
            int cw = direct ? (int)GuiBatchNumber(clip.MetaData, "__Internal_DirectCropWidth__", width) : (int)Field("Width", width);
            int ch = direct ? (int)GuiBatchNumber(clip.MetaData, "__Internal_DirectCropHeight__", height) : (int)Field("Height", height);
            float angle = direct ? 0 : (float)Field("Angle", 0);
            if (operation == GuiProjectOperation.SetClipGeometry)
            {
                if (new[] { "CropEnabled", "CropXPixels", "CropYPixels", "CropWidthPixels", "CropHeightPixels", "CropRotationDegrees" }.Any(a.Has))
                {
                    enabled = a.Bool("CropEnabled", enabled); x = a.Int("CropXPixels", x); y = a.Int("CropYPixels", y);
                    cw = a.Int("CropWidthPixels", cw); ch = a.Int("CropHeightPixels", ch); angle = (float)a.Number("CropRotationDegrees", angle);
                    if (x < 0 || y < 0 || cw <= 0 || ch <= 0 || !float.IsFinite(angle)) throw new ArgumentException("Invalid crop geometry.");
                    bool add = crop is null;
                    crop ??= EffectServices.GetAvailableEffectProviders()["Crop"]();
                    crop.Id = GuiBatchCropId; crop.Enabled = enabled;
                    ApplyGuiProviderFields(crop, new() { ["StartX"] = x, ["StartY"] = y, ["Width"] = cw, ["Height"] = ch, ["Angle"] = angle }, false);
                    foreach (string key in new[] { "StartX", "StartY", "Width", "Height", "Angle" }) crop.ClearFieldBinding(key);
                    providers[crop.Id] = crop;
                    if (add) EffectBindingHelper.AutoConnectProviderToInput(providers, crop);
                    clip.StartingX = clip.StartingY = 0;
                    foreach (string key in new[] { "__Internal_DirectCropEnabled__", "__Internal_DirectCropWidth__", "__Internal_DirectCropHeight__" }) clip.MetaData.Remove(key);
                    if (enabled) { clip.TargetWidth = cw; clip.TargetHeight = ch; }
                }
                if (a.Has("WidthPixels")) clip.TargetWidth = a.Int("WidthPixels");
                if (a.Has("HeightPixels")) clip.TargetHeight = a.Int("HeightPixels");
                if (a.Has("XPixels")) clip.TargetX = a.Int("XPixels");
                if (a.Has("YPixels")) clip.TargetY = a.Int("YPixels");
                if (a.Has("RotationDegrees")) clip.Rotation = VideoClipRotation.Normalize((float)a.Number("RotationDegrees"));
                if (a.Has("WidthPixels") && clip.TargetWidth <= 0 || a.Has("HeightPixels") && clip.TargetHeight <= 0 || !float.IsFinite(clip.Rotation)) throw new ArgumentException("Invalid clip geometry.");
                foreach (var arguments in providers.Values.OfType<ClipArgumentProvider>())
                {
                    var fields = new[] { ("XPixels", "TargetX"), ("YPixels", "TargetY"), ("WidthPixels", "TargetWidth"), ("HeightPixels", "TargetHeight"), ("RotationDegrees", "Rotation") }
                        .Where(p => a.Has(p.Item1)).Select(p => p.Item2).ToHashSet();
                    arguments.RestoreStaticValues(arguments.StaticValues.Where(p => !fields.Contains(p.Key)).ToDictionary());
                    foreach (string key in fields) arguments.ClearFieldBinding(key);
                }
                clip.EffectProviders = providers.Values.Select(EffectBindingHelper.SerializeProvider).ToArray();
                if (a.Has("CropEnabled") || a.Has("CropXPixels") || a.Has("CropYPixels") || a.Has("CropWidthPixels") || a.Has("CropHeightPixels") || a.Has("CropRotationDegrees"))
                    clip.Effects = clip.Effects?.Where(e => e.Name != "__Internal_Crop__" && e.BindedEffectGroupID != GuiBatchCropId.ToString()).ToArray();
            }
            return new { ClipId = clip.Id, WidthPixels = clip.TargetWidth > 0 ? clip.TargetWidth : batch.Project.RelativeWidth,
                HeightPixels = clip.TargetHeight > 0 ? clip.TargetHeight : batch.Project.RelativeHeight, AutomaticWidth = clip.TargetWidth <= 0,
                AutomaticHeight = clip.TargetHeight <= 0, XPixels = clip.TargetX, YPixels = clip.TargetY, RotationDegrees = clip.Rotation,
                CropEnabled = enabled, CropXPixels = x, CropYPixels = y, CropWidthPixels = cw, CropHeightPixels = ch, CropRotationDegrees = angle };
        }
        var audio = clip.ClipType == ClipMode.AudioClip ? clip : Guid.TryParse(clip.BindedSoundTrack, out var audioId) ? batch.Clips.GetValueOrDefault(audioId) : null;
        Guid? detached = null;
        if (operation == GuiProjectOperation.SetClipProperties)
        {
            if (new[] { "TargetDecoder", "HDRBrightnessOffset" }.Any(a.Has) && clip.ClipType != ClipMode.VideoClip) throw new ArgumentException("Video properties require a video clip.");
            if (new[] { "Red", "Green", "Blue", "Alpha" }.Any(a.Has) && clip.ClipType != ClipMode.SolidColorClip) throw new ArgumentException("Color channels require a solid color clip.");
            if (new[] { "MaskSourceType", "SourceArguments" }.Any(a.Has) && clip.ClipType != ClipMode.MaskClip) throw new ArgumentException("Mask properties require a mask clip.");
            if (new[] { "AudioEnabled", "AudioVolume", "DetachAudio" }.Any(a.Has) && audio is null) throw new ArgumentException("Clip has no audio track.");
            if (a.Has("Name")) clip.Name = a.String("Name") ?? throw new ArgumentException("Name cannot be null.");
            if (a.Has("ClipColor")) clip.MetaData["ClipColor"] = string.IsNullOrWhiteSpace(a.String("ClipColor")) ? "" : Color.FromArgb(a.String("ClipColor")!).ToArgbHex();
            foreach (string key in new[] { "AllowFreeScaleResize", "ExtendToWholeDraft" }) if (a.Has(key)) clip.MetaData[key] = a.Bool(key);
            if (a.Bool("ExtendToWholeDraft") && (!clip.IsInfiniteLength || clip.LayerIndex < SubTrackOffset || batch.Clips.Values.Any(c => c.Id != clip.Id && c.LayerIndex == clip.LayerIndex && c.ShouldDisplayInUI)))
                throw new ArgumentException("Extending to the whole draft requires an infinite clip alone on a subtrack.");
            if (a.Has("TargetDecoder"))
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(a.String("TargetDecoder"));
                if (clip.FilePath?.StartsWith('#') == true) throw new ArgumentException("Generated sources select their own decoder.");
                clip.MetaData["TargetDecoder"] = a.String("TargetDecoder")!; clip.MetaData.Remove("targetdecoder");
            }
            if (a.Has("HDRBrightnessOffset"))
            {
                double value = a.Number("HDRBrightnessOffset");
                if (value is < -1 or > 1) throw new ArgumentException("HDRBrightnessOffset must be between -1 and 1.");
                clip.MetaData["HDRBrightnessOffset"] = value;
            }
            foreach (var (key, channel) in new[] { ("Red", "R"), ("Green", "G"), ("Blue", "B") }) if (a.Has(key)) clip.MetaData[channel] = checked((ushort)a.Int(key));
            if (a.Has("Alpha"))
            {
                double value = a.Number("Alpha");
                if (value is < 0 or > 1) throw new ArgumentException("Alpha must be between 0 and 1.");
                clip.MetaData["A"] = (float)value;
            }
            if (a.Has("AntiAliasMode"))
            {
                if (clip.ClipType is not (ClipMode.TextClip or ClipMode.SubtitleClip or ClipMode.VectorCanvasClip or ClipMode.VectorComponentClip)) throw new ArgumentException("Clip does not support vector antialiasing.");
                string mode = new[] { "Default", "None", "SSAA2x", "SSAA4x", "SSAA8x" }.FirstOrDefault(s => s.Equals(a.String("AntiAliasMode"), StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("Invalid AntiAliasMode.");
                clip.MetaData["VectorAntiAliasMode"] = mode == "Default" ? "" : mode;
            }
            if (a.Has("MaskSourceType") || a.Has("SourceArguments")) ApplyGuiBatchSourceArguments(clip, a);
            if (audio is not null && new[] { "AudioEnabled", "AudioVolume", "DetachAudio" }.Any(a.Has))
            {
                audio = batch.Edit(audio.Id); audio.MetaData ??= new();
                if (a.Has("AudioEnabled")) audio.MetaData[SoundTrackMetadata.EnabledKey] = a.Bool("AudioEnabled");
                if (a.Has("AudioVolume"))
                {
                    double volume = a.Number("AudioVolume");
                    if (volume is < 0 or > 1) throw new ArgumentException("AudioVolume must be between 0 and 1.");
                    audio.MetaData[SoundTrackMetadata.VolumeKey] = (float)volume;
                }
                if (a.Bool("DetachAudio"))
                {
                    if (clip.ClipType != ClipMode.VideoClip || !SoundTrackMetadata.ReadBool(audio.MetaData, SoundTrackMetadata.GeneratedFromVideoKey)) throw new ArgumentException("Only generated video audio can be detached.");
                    detached = audio.Id; clip.BindedSoundTrack = ""; clip.MetaData[SoundTrackMetadata.DetachedKey] = true;
                    audio.ShouldDisplayInUI = true; audio.MetaData.Remove(SoundTrackMetadata.GeneratedFromVideoKey);
                    audio.MetaData.Remove(SoundTrackMetadata.SourceClipIdKey);
                    int track = batch.Tracks.Where(t => t < SubTrackOffset).DefaultIfEmpty(-1).Max() + 1;
                    if (track >= SubTrackOffset) throw new InvalidOperationException("No main track is available for detached audio.");
                    batch.Tracks.Add(track);
                    audio.LayerIndex = audio.SubLayerIndex = (uint)track;
                }
            }
        }
        bool video = clip.ClipType == ClipMode.VideoClip, solid = clip.ClipType == ClipMode.SolidColorClip;
        return new { ClipId = clip.Id, clip.Name, Type = clip.ClipType.ToString(), ClipColor = clip.MetaData.GetValueOrDefault("ClipColor"),
            clip.FilePath, clip.IsInfiniteLength, SourceDurationFrames = clip.IsInfiniteLength ? null : clip.SourceDuration,
            AllowFreeScaleResize = SoundTrackMetadata.ReadBool(clip.MetaData, "AllowFreeScaleResize"), ExtendToWholeDraft = SoundTrackMetadata.ReadBool(clip.MetaData, "ExtendToWholeDraft"),
            TargetDecoder = video ? clip.MetaData.GetValueOrDefault("TargetDecoder")?.ToString() ?? "auto" : null, ResolvedDecoder = (string?)null,
            VideoDecoderOptions = video ? new[] { "auto", "DecoderContextFP16", "DecoderContextHWFP16", "HDRDecoderContextFP16" } : null,
            HDRBrightnessOffset = video ? (double?)GuiBatchNumber(clip.MetaData, "HDRBrightnessOffset") : null,
            AudioClipId = audio?.Id, AudioEnabled = audio is null ? (bool?)null : SoundTrackMetadata.ReadBool(audio.MetaData, SoundTrackMetadata.EnabledKey, true),
            AudioVolume = audio is null ? (float?)null : SoundTrackMetadata.ReadVolume(audio.MetaData), AudioDetached = SoundTrackMetadata.ReadBool(clip.MetaData, SoundTrackMetadata.DetachedKey),
            DetachedAudioClipId = detached, Red = solid ? (double?)GuiBatchNumber(clip.MetaData, "R", 65535) : null,
            Green = solid ? (double?)GuiBatchNumber(clip.MetaData, "G", 65535) : null, Blue = solid ? (double?)GuiBatchNumber(clip.MetaData, "B", 65535) : null,
            Alpha = solid ? (double?)GuiBatchNumber(clip.MetaData, "A", 1) : null,
            MaskSourceType = clip.ClipType == ClipMode.MaskClip ? clip.MetaData.GetValueOrDefault(MaskClip.SourceTypeKey)?.ToString() ?? "Blur" : null,
            SourceArguments = clip.ClipType == ClipMode.MaskClip ? (CopyGuiEffectProviders(new GuiEffectClip(clip)).Values.OfType<ClipArgumentProvider>().SingleOrDefault()
                ?? CreateGuiClipArguments(new GuiEffectClip(clip), Guid.NewGuid())).Fields.Values.Where(f => f.Id.StartsWith(MaskClip.ArgumentPrefix, StringComparison.Ordinal)).Select(GuiField).ToArray() : null,
            AntiAliasMode = clip.MetaData.GetValueOrDefault("VectorAntiAliasMode")?.ToString() is { Length: > 0 } aa ? aa : "Default" };
    }

    #endregion

    #region Subtitles and settings

    private static SubtitleData GuiSubtitleData(ClipDraftDTO clip)
    {
        if (clip.ClipType != ClipMode.SubtitleClip) throw new ArgumentException("Clip is not a subtitle clip.");
        return clip.MetaData?.TryGetValue(SubtitleData.MetadataKey, out var data) == true ? SubtitleData.Restore(data).Clone() : new();
    }

    private static object GuiSubtitleSnapshot(ClipDraftDTO clip)
    {
        var data = GuiSubtitleData(clip);
        return new { ClipId = clip.Id, clip.Name, TrackId = clip.LayerIndex, clip.StartFrame,
            DurationFrames = clip.Duration, SourceStartFrame = clip.RelativeStartFrame, CueCount = data.Cues.Count,
            Cues = data.Cues.OrderBy(c => c.StartMilliseconds).Select(c => GuiSubtitleCue(clip.Id, c)).ToArray() };
    }
    private object? ExecuteGuiBatchSubtitle(GuiProjectOperation operation, GuiEditArgs a, GuiBatchState batch)
    {
        if (operation == GuiProjectOperation.GetSubtitleClip)
            return batch.Clips.Values.Where(c => c.ClipType == ClipMode.SubtitleClip
                && (!a.Has("ClipId") || c.Id == a.Guid("ClipId")) && (!a.Has("TrackId") || c.LayerIndex == a.Int("TrackId")))
                .Select(GuiSubtitleSnapshot).ToArray();
        if (operation == GuiProjectOperation.AddSubtitleClip)
        {
            var data = ReadGuiSubtitles(a, new());
            var dto = NewGuiBatchClip(batch, a, ClipMode.SubtitleClip, a.Has("FilePath") ? Path.GetFileNameWithoutExtension(a.String("FilePath")) : "Subtitles");
            data.Style.FontSize = Math.Max(12, batch.Project.RelativeHeight * 48f / 1080);
            data.Style.StrokeThickness = Math.Max(1, batch.Project.RelativeHeight * 2f / 1080);
            data.BottomMargin = Math.Max(12, batch.Project.RelativeHeight * 60f / 1080);
            if (!a.Has("DurationFrames") && data.Cues.Count > 0)
                dto.Duration = Math.Max(1, SubtitleData.FrameAt(data.Cues.Max(c => c.EndMilliseconds), batch.Project.TargetFrameRate));
            if ((ulong)dto.StartFrame + dto.Duration > int.MaxValue) throw new ArgumentException("Subtitle clip exceeds the timeline range.");
            dto.MetaData![SubtitleData.MetadataKey] = data;
            batch.Put(dto);
            return GuiSubtitleSnapshot(dto);
        }
        var clip = batch.Edit(a.Guid("ClipId"));
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
        var updated = clip;
        if (a.Bool("FitLength") && subtitles.Cues.Count > 0)
        {
            uint end = SubtitleData.FrameAt(subtitles.Cues.Max(c => c.EndMilliseconds), batch.Project.TargetFrameRate);
            if (end <= clip.RelativeStartFrame) throw new ArgumentException("Subtitles end before the clip's source start.");
            uint length = end - clip.RelativeStartFrame;
            if ((ulong)clip.StartFrame + length > int.MaxValue)
                throw new ArgumentException("Subtitle clip exceeds the timeline range.");
            updated.Duration = length;
        }
        updated.MetaData![SubtitleData.MetadataKey] = subtitles;
        batch.Put(updated);
        return edited is not null ? GuiSubtitleCue(clip.Id, edited) : GuiSubtitleSnapshot(clip);
    }
    private object ExecuteGuiBatchSettings(GuiProjectOperation operation, GuiEditArgs a, GuiBatchState batch)
    {
        var project = CloneGuiData(batch.Project);
        if (operation == GuiProjectOperation.GetSettings) return GuiBatchSettingsSnapshot(batch.Project);
        uint fps = a.UInt("FrameRate", project.TargetFrameRate);
        int width = a.Int("WidthPixels", project.RelativeWidth), height = a.Int("HeightPixels", project.RelativeHeight);
        if (fps == 0 || fps > int.MaxValue || width <= 0 || height <= 0) throw new ArgumentException("Invalid project frame rate or resolution.");
        if (a.Has("FrameRate"))
            foreach (var clip in batch.Clips.Values.Where(c => c.ClipType == ClipMode.SubtitleClip))
                foreach (var cue in GuiSubtitleData(clip).Cues) _ = SubtitleData.FrameAt(cue.EndMilliseconds, fps);
        if (a.Has("CoverFilePath") && a.Bool("ClearCover")) throw new ArgumentException("Choose CoverFilePath or ClearCover.");
        string? coverPath = a.Bool("ClearCover") ? null : a.String("CoverFilePath", project.ThumbPath);
        bool CoverExists() => coverPath is not null && File.Exists(batch.PendingFiles.GetValueOrDefault(coverPath, coverPath));
        if (a.Has("CoverFilePath") && !CoverExists()) throw new FileNotFoundException("Project cover not found.", coverPath);
        var properties = new Dictionary<string, string>(project.Properties);
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
        if (a.Bool("WriteCoverAsFirstFrame") && !CoverExists()) throw new ArgumentException("Writing a cover as the first frame requires a cover file.");
        var custom = new Dictionary<string, string>(project.UserDefinedProperties);
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
            string destination = Path.Combine(directory, Guid.NewGuid().ToString("N") + Path.GetExtension(coverPath));
            batch.PendingFiles[destination] = coverPath!;
            coverPath = destination;
        }
        project.TargetFrameRate = fps;
        project.RelativeWidth = width;
        project.RelativeHeight = height;
        project.Properties = properties;
        project.UserDefinedProperties = custom;
        project.ThumbPath = coverPath;
        batch.Project = project;
        return GuiBatchSettingsSnapshot(batch.Project);
    }

    #endregion

    #region Timeline and transforms

    private static ISpeedVarianceProvider? GuiBatchSpeed(ClipDraftDTO clip)
    {
        if (clip.ClipType == ClipMode.ProjectReferenceClip || clip.MetaData?.ContainsKey(ProjectReferencePart.MetadataKey) == true) return null;
        if (clip.EffectProviders?.Any(p => p.Stage == Stage.SpeedVariance) != true) return null;
        return EffectBindingHelper.RebuildAllEffects(EffectBindingHelper.MigrateToEffectProviders(clip.EffectProviders, null), null)
            ?.Values.OfType<ISpeedVarianceProvider>().FirstOrDefault();
    }
    private static uint GuiBatchDuration(ClipDraftDTO clip) => ClipTiming.EffectiveDuration(clip.Duration, GuiBatchSpeed(clip));
    private static TransformClipInfo[] GuiBatchTransformInfos(GuiBatchState batch, bool audio) => batch.Clips.Values
        .Where(c => audio ? c.ClipType == ClipMode.AudioClip : c.ClipType is not (ClipMode.AudioClip or ClipMode.MarkingClip or ClipMode.TransformClip))
        .Select(c => new TransformClipInfo(c.Id, c.StartFrame, GuiBatchDuration(c), c.LayerIndex, c.SubLayerIndex, c.EffectProviders ?? [])).ToArray();

    private static object? GuiBatchTransformSnapshot(GuiBatchState batch, ClipDraftDTO clip, TransformSide side)
    {
        var infos = GuiBatchTransformInfos(batch, clip.ClipType == ClipMode.AudioClip);
        if (TransformProcessing.Find(infos, clip.Id, side) is not { } found) return null;
        var provider = EffectBindingHelper.MigrateToEffectProviders([found.Provider], null)[found.Provider.Id];
        var mode = TransformProcessing.ReadEnum<TransformInputMode>(provider.MetaData, TransformProcessing.ModeKey);
        return new { ClipId = clip.Id, OwnerClipId = found.Owner.Id, ProviderId = provider.Id, Side = side.ToString(), provider.TypeName,
            provider.Name, provider.Enabled, NeighborClipId = mode == TransformInputMode.TwoInput
                ? (Guid?)(found.Owner.Id == clip.Id ? TransformProcessing.ReadNextClip(provider.MetaData) : found.Owner.Id) : null,
            InputMode = mode.ToString(), DurationFrames = TransformProcessing.ReadDuration(provider.MetaData),
            RenderOrder = TransformProcessing.ReadEnum<TransformRenderOrder>(provider.MetaData, TransformProcessing.OrderKey).ToString(),
            ResolvedDurationFrames = TransformProcessing.Resolve(infos, clip.Id, side)?.Duration ?? 0, Provider = GuiProvider(provider, found.Owner.Id) };
    }

    private object? ExecuteGuiBatchTransform(GuiProjectOperation operation, GuiEditArgs a, GuiBatchState batch)
    {
        var clip = batch.Read(a.Guid("ClipId"));
        if (clip.ClipType is ClipMode.MarkingClip or ClipMode.TransformClip) throw new ArgumentException("Clip does not support transforms.");
        var side = a.Enum<TransformSide>("Side");
        if (operation == GuiProjectOperation.GetClipTransform)
            return (a.Has("Side") ? new[] { side } : Enum.GetValues<TransformSide>()).Select(s => GuiBatchTransformSnapshot(batch, clip, s)).Where(s => s is not null).ToArray();
        var infos = GuiBatchTransformInfos(batch, clip.ClipType == ClipMode.AudioClip);
        var existing = TransformProcessing.Find(infos, clip.Id, side);
        if (operation == GuiProjectOperation.RemoveClipTransform)
        {
            if (existing is not { } old) throw new KeyNotFoundException("Clip transform not found.");
            batch.Edit(old.Owner.Id).EffectProviders = batch.Read(old.Owner.Id).EffectProviders!.Where(p => p.Id != old.Provider.Id).ToArray();
            return null;
        }
        IEffectProvider provider;
        if (existing is { } saved && (!a.Has("TypeName") || saved.Provider.TypeName == a.String("TypeName")))
            provider = EffectBindingHelper.MigrateToEffectProviders([CloneGuiData(saved.Provider)], null)[saved.Provider.Id];
        else if (TransformServices.GetAvailableTransforms(clip.ClipType == ClipMode.AudioClip).TryGetValue(a.String("TypeName") ?? "", out var factory))
        { provider = factory(); provider.Id = Guid.NewGuid(); }
        else throw new ArgumentException("Specify an available transform TypeName.");
        ApplyGuiProviderFields(provider, a.Fields(), a.Bool("ResetToDefaults"));
        var own = infos.Single(c => c.Id == clip.Id);
        var neighbor = infos.Where(c => c.Id != clip.Id && c.Layer == own.Layer && c.SubLayer == own.SubLayer
            && (side == TransformSide.Left ? c.End == own.Start : c.Start == own.End)).OrderBy(c => c.Id).Select(c => (TransformClipInfo?)c).FirstOrDefault();
        var transform = TransformServices.Create(provider);
        TransformInputMode mode;
        try
        {
            mode = a.Enum("InputMode", existing is { } previous ? TransformProcessing.ReadEnum<TransformInputMode>(previous.Provider.MetaData, TransformProcessing.ModeKey)
                : neighbor is not null && transform.Definition.HasFlag(TransformDefinition.SupportTwoInput) ? TransformInputMode.TwoInput : TransformInputMode.OneInput);
            if (!transform.Definition.HasFlag(clip.ClipType == ClipMode.AudioClip ? TransformDefinition.Audio : TransformDefinition.Clip)
                || !transform.Definition.HasFlag(mode == TransformInputMode.TwoInput ? TransformDefinition.SupportTwoInput : TransformDefinition.SupportOneInput)
                || mode == TransformInputMode.TwoInput && neighbor is null) throw new ArgumentException("Transform input mode or neighboring clips are incompatible.");
        }
        finally { (transform as IDisposable)?.Dispose(); }
        uint duration = a.UInt("DurationFrames", existing is { } previousDuration ? TransformProcessing.ReadDuration(previousDuration.Provider.MetaData) : Math.Max(1u, batch.Project.TargetFrameRate / 2));
        if (duration == 0 || duration > int.MaxValue) throw new ArgumentException("Invalid transform duration.");
        var order = a.Enum("RenderOrder", existing is { } previousOrder ? TransformProcessing.ReadEnum<TransformRenderOrder>(previousOrder.Provider.MetaData, TransformProcessing.OrderKey) : TransformServices.DefaultRenderOrder);
        provider.Enabled = a.Bool("Enabled", provider.Enabled);
        Guid ownerId = mode == TransformInputMode.TwoInput && side == TransformSide.Left ? neighbor!.Value.Id : clip.Id;
        TransformProcessing.Configure(provider, mode == TransformInputMode.TwoInput ? TransformSide.Right : side, mode, duration,
            mode == TransformInputMode.TwoInput ? side == TransformSide.Left ? clip.Id : neighbor!.Value.Id : Guid.Empty, order);
        var replaced = new HashSet<Guid>();
        if (existing is { } replacedEdge) replaced.Add(replacedEdge.Provider.Id);
        if (mode == TransformInputMode.TwoInput && TransformProcessing.Find(infos, neighbor!.Value.Id, side == TransformSide.Left ? TransformSide.Right : TransformSide.Left) is { } other) replaced.Add(other.Provider.Id);
        var candidate = EffectBindingHelper.SerializeProvider(provider);
        var proposed = infos.Select(c => c with { Providers = c.Providers.Where(p => !replaced.Contains(p.Id)).Concat(c.Id == ownerId ? new[] { candidate } : []).ToArray() }).ToArray();
        if (TransformProcessing.Resolve(proposed, clip.Id, side) is not { Duration: > 0 } resolved) throw new ArgumentException("Transform cannot be placed on this edge.");
        provider.MetaData[TransformProcessing.DurationKey] = resolved.Duration;
        foreach (var info in infos.Where(c => c.Id == ownerId || c.Providers.Any(p => replaced.Contains(p.Id))))
            batch.Edit(info.Id).EffectProviders = info.Providers.Where(p => !replaced.Contains(p.Id))
                .Concat(info.Id == ownerId ? new[] { EffectBindingHelper.SerializeProvider(provider) } : []).ToArray();
        return GuiBatchTransformSnapshot(batch, clip, side);
    }

    private object? ExecuteGuiBatchTimeline(GuiProjectOperation operation, GuiEditArgs a, GuiBatchState batch)
    {
        if (operation == GuiProjectOperation.SplitClip)
        {
            var left = batch.Edit(a.Guid("ClipId"));
            uint frame = a.UInt("FrameIndex"), duration = GuiBatchDuration(left);
            if (left.ClipType == ClipMode.MarkingClip || SoundTrackMetadata.ReadBool(left.MetaData, "ExtendToWholeDraft")
                || frame <= left.StartFrame || (ulong)frame >= (ulong)left.StartFrame + duration || left.Duration < 2)
                throw new ArgumentException("Split frame must be inside a non-group clip with at least two source frames.");
            uint consumed = Math.Clamp(ClipTiming.SourceOffset(frame - left.StartFrame, left.Duration, GuiBatchSpeed(left)), 1u, left.Duration - 1);
            var node = JsonSerializer.SerializeToNode(left, savingOpts)!;
            node["Id"] = Guid.NewGuid(); node["Name"] = left.Name + " (2)";
            node["StartFrame"] = checked(left.StartFrame + ClipTiming.EffectiveDuration(consumed, GuiBatchSpeed(left)));
            node["RelativeStartFrame"] = checked(left.RelativeStartFrame + consumed); node["Duration"] = left.Duration - consumed;
            node["BindedSoundTrack"] = "";
            var right = node.Deserialize<ClipDraftDTO>(savingOpts)!;
            left.Duration = consumed;
            right.EffectProviders = right.EffectProviders?.Where(p => p.MetaData?.ContainsKey(TransformProcessing.SideKey) != true
                || TransformProcessing.ReadEnum<TransformSide>(p.MetaData, TransformProcessing.SideKey) == TransformSide.Right).ToArray();
            left.EffectProviders = left.EffectProviders?.Where(p => p.MetaData?.ContainsKey(TransformProcessing.SideKey) != true
                || TransformProcessing.ReadEnum<TransformSide>(p.MetaData, TransformProcessing.SideKey) == TransformSide.Left).ToArray();
            batch.Put(right);
            SyncGuiBatchAudio(batch, left);
            SyncGuiBatchAudio(batch, right);
            return new[] { GuiClipSnapshot(left), GuiClipSnapshot(right) };
        }
        if (operation == GuiProjectOperation.JoinClip)
        {
            var children = a.Json.GetProperty("ClipIds").Deserialize<Guid[]>()!.Distinct().Select(batch.Read).ToArray();
            if (children.Length < 2 || children.Any(c => !c.ShouldDisplayInUI || c.ClipType == ClipMode.MarkingClip)
                || children.Any(c => c.LayerIndex < SubTrackOffset) && children.Any(c => c.LayerIndex >= SubTrackOffset))
                throw new ArgumentException("Select at least two visible clips on compatible tracks.");
            uint start = children.Min(c => c.StartFrame), end = checked((uint)children.Max(c => (ulong)c.StartFrame + GuiBatchDuration(c)));
            bool vector = children.All(c => c.ClipType == ClipMode.VectorComponentClip);
            var group = NewGuiBatchClip(batch, new GuiEditArgs(JsonSerializer.SerializeToElement(new
            { TrackId = vector ? children.Max(c => c.LayerIndex) : children.Min(c => c.LayerIndex), StartFrame = start, DurationFrames = end - start,
                Name = a.String("Name", "Group") })), vector ? ClipMode.VectorComponentClip : ClipMode.MarkingClip, "Group");
            if (vector)
            {
                var copies = children.OrderBy(c => c.LayerIndex).ThenBy(c => c.SubLayerIndex).Select(CloneGuiData).ToArray();
                foreach (var child in copies) child.StartFrame -= start;
                group.MetaData![VectorComponentClip.ChildrenKey] = JsonSerializer.Serialize(copies, savingOpts);
                int x = children.Min(c => c.TargetX), y = children.Min(c => c.TargetY);
                int width = Math.Max(1, children.Max(c => c.TargetX + c.TargetWidth) - x), height = Math.Max(1, children.Max(c => c.TargetY + c.TargetHeight) - y);
                group.TargetX = x; group.TargetY = y; group.TargetWidth = width; group.TargetHeight = height;
                group.MetaData[VectorClipServices.ViewportKey] = new[] { x, y, width, height };
                group.MetaData[VectorClipServices.CanvasWidthKey] = batch.Project.RelativeWidth;
                group.MetaData[VectorClipServices.CanvasHeightKey] = batch.Project.RelativeHeight;
                group.MetaData[VectorComponentClip.SourceLengthKey] = end - start;
                var component = new ComponentGroup { Name = group.Name };
                component.SetInitialBounds((x + width / 2f) / batch.Project.RelativeWidth, (y + height / 2f) / batch.Project.RelativeHeight,
                    width / (float)batch.Project.RelativeWidth, height / (float)batch.Project.RelativeHeight);
                component.Parameters["RelativeX"] = component.InitialRelativeX; component.Parameters["RelativeY"] = component.InitialRelativeY;
                component.Parameters["Width"] = component.InitialWidth; component.Parameters["Height"] = component.InitialHeight;
                VectorClipServices.SetDefinition(batch.Project, group, [component], false);
                foreach (var child in children) batch.Remove(child.Id);
            }
            else
            {
                group.MetaData!["GroupedClipIds"] = children.Select(c => c.Id).ToArray();
                foreach (var child in children)
                {
                    var hidden = batch.Edit(child.Id); hidden.SubLayerIndex = hidden.LayerIndex; hidden.LayerIndex = group.LayerIndex; hidden.ShouldDisplayInUI = false;
                    SyncGuiBatchAudio(batch, hidden);
                }
            }
            batch.Put(group);
            return GuiClipSnapshot(group);
        }
        var parent = batch.Read(a.Guid("ClipId"));
        if (parent.ClipType == ClipMode.ProjectReferenceClip)
        {
            var detached = projectFrameCut.Render.ProjectReferences.ProjectReferenceEditing.Detach(parent, batch.Project, WorkingPath);
            var children = detached.Clips.ToList();
            foreach (var track in detached.SoundTracks)
            {
                var node = JsonSerializer.SerializeToNode(track, savingOpts)!;
                node["ClipType"] = (int)ClipMode.AudioClip;
                node["TypeName"] = nameof(SoundTrackToClipWrapper);
                node["SourceDuration"] = (ulong)track.RelativeStartFrame + track.Duration;
                node["BindedSoundTrack"] = track.Id;
                children.Add(node.Deserialize<ClipDraftDTO>(savingOpts)!);
            }
            if (children.Any(c => batch.Clips.ContainsKey(c.Id))) throw new InvalidOperationException("Detached clip ID collision.");
            foreach (var child in children) { batch.Put(child); batch.Tracks.Add((int)child.LayerIndex); }
            batch.Remove(parent.Id);
            return children.Select(GuiClipSnapshot).ToArray();
        }
        if (parent.ClipType == ClipMode.MarkingClip && parent.MetaData?.GetValueOrDefault("GroupedClipIds") is { } ids)
        {
            var children = JsonSerializer.Deserialize<Guid[]>(JsonSerializer.Serialize(ids))!.Where(batch.Clips.ContainsKey).Select(batch.Edit).ToArray();
            foreach (var child in children)
            { child.LayerIndex = batch.Tracks.Contains((int)child.SubLayerIndex) ? child.SubLayerIndex : parent.LayerIndex; child.ShouldDisplayInUI = true; }
            batch.Remove(parent.Id);
            return children.Select(GuiClipSnapshot).ToArray();
        }
        return UngroupGuiBatchVector(parent, batch);
    }

    #endregion

    #region Vector groups

    private static object UngroupGuiBatchVector(ClipDraftDTO parent, GuiBatchState batch)
    {
        if (parent.ClipType != projectFrameCut.Shared.ClipMode.VectorComponentClip) throw new ArgumentException("Clip is not a vector group.");
        var group = VectorComponentSerializer.Read(parent.MetaData!).OfType<ComponentGroup>().FirstOrDefault();
        if (group is null) throw new ArgumentException("Clip is not a vector group.");
        List<ClipDraftDTO> children = [];
        bool timedGroup = parent.MetaData!.TryGetValue(VectorComponentClip.ChildrenKey, out var raw);
        if (timedGroup)
            children = JsonSerializer.Deserialize<List<ClipDraftDTO>>(raw!.ToString()!) ?? [];
        if (timedGroup && (parent.RelativeStartFrame > 0 || parent.Duration < VectorComponentClip.ReadNumber(parent.MetaData!, VectorComponentClip.SourceLengthKey, parent.Duration)))
        {
            var visible = new List<ClipDraftDTO>();
            foreach (var child in children)
            {
                uint first = Math.Max(child.StartFrame, parent.RelativeStartFrame);
                uint end = checked((uint)Math.Min((ulong)child.StartFrame + GuiBatchDuration(child), (ulong)parent.RelativeStartFrame + parent.Duration));
                if (end <= first) continue;
                uint sourceStart = checked(child.RelativeStartFrame + ClipTiming.SourceOffset(first - child.StartFrame, child.Duration, GuiBatchSpeed(child)));
                uint sourceEnd = (checked(child.RelativeStartFrame + ClipTiming.SourceOffset(end - 1 - child.StartFrame, child.Duration, GuiBatchSpeed(child)))) + 1;
                var node = JsonSerializer.SerializeToNode(child)!;
                node["StartFrame"] = first - parent.RelativeStartFrame;
                node["RelativeStartFrame"] = sourceStart;
                node["Duration"] = Math.Max(1u, sourceEnd - sourceStart);
                visible.Add(node.Deserialize<ClipDraftDTO>()!);
            }
            children = visible;
        }
        var viewport = parent.MetaData!.TryGetValue(VectorClipServices.ViewportKey, out var bounds)
            ? JsonSerializer.Deserialize<int[]>(JsonSerializer.Serialize(bounds))! : new[] { 0, 0, batch.Project.RelativeWidth, batch.Project.RelativeHeight };
        float sx = parent.TargetWidth / (float)Math.Max(1, viewport[2]);
        float sy = parent.TargetHeight / (float)Math.Max(1, viewport[3]);
        if (!timedGroup)
        {
            foreach (var child in group.Children)
            {
                var source = VectorComponentSerializer.Clone(child);
                var wrapper = new VectorComponentWrapperClip(source) { ParentCanvasWidth = batch.Project.RelativeWidth, ParentCanvasHeight = batch.Project.RelativeHeight };
                wrapper.SyncFromDefinition();
                children.Add(new ClipDraftDTO
                {
                    Id = Guid.NewGuid(), FromPlugin = parent.FromPlugin, ClipType = ClipMode.VectorComponentClip,
                    TypeName = nameof(VectorComponentClip), Name = source.Name, LayerIndex = parent.LayerIndex,
                    RelativeStartFrame = parent.RelativeStartFrame, Duration = parent.Duration, FrameTime = parent.FrameTime,
                    TargetX = wrapper.TargetX, TargetY = wrapper.TargetY, TargetWidth = wrapper.TargetWidth, TargetHeight = wrapper.TargetHeight,
                    MetaData = new Dictionary<string, object>
                    {
                        [VectorComponentSerializer.ComponentsKey] = VectorComponentSerializer.Serialize([source]),
                        [VectorClipServices.ViewportKey] = new[] { wrapper.TargetX, wrapper.TargetY, wrapper.TargetWidth, wrapper.TargetHeight },
                        [VectorClipServices.CanvasWidthKey] = batch.Project.RelativeWidth,
                        [VectorClipServices.CanvasHeightKey] = batch.Project.RelativeHeight,
                        [VectorComponentClip.SourceLengthKey] = parent.MetaData!.GetValueOrDefault(VectorComponentClip.SourceLengthKey, parent.Duration)
                    }
                });
            }
        }
        if (!timedGroup)
        {
            var transform = (ComponentGroup)VectorComponentSerializer.Clone(group);
            transform.SetChildren([]);
            foreach (var child in children)
            {
                var data = child.MetaData!;
                var transforms = data.TryGetValue(VectorComponentClip.TransformsKey, out var existing)
                    ? VectorComponentSerializer.Read(new Dictionary<string, object> { [VectorComponentSerializer.ComponentsKey] = existing }) : [];
                transforms.Add(transform);
                data[VectorComponentClip.TransformsKey] = VectorComponentSerializer.Serialize(transforms);
                VectorClipServices.SetDefinition(batch.Project, child, VectorComponentSerializer.Read(data), true);
            }
        }
        foreach (var child in children)
        {
            child.StartFrame = checked(parent.StartFrame + child.StartFrame);
            float x = (child.TargetX + child.TargetWidth / 2f - viewport[0] - viewport[2] / 2f) * sx;
            float y = (child.TargetY + child.TargetHeight / 2f - viewport[1] - viewport[3] / 2f) * sy;
            float rotation = timedGroup ? group.Parameters.GetFloat("Rotation", 0) + VectorComponentClip.ReadNumber(parent.MetaData!, VectorComponentClip.RotationKey, 0) : VectorComponentClip.ReadNumber(parent.MetaData!, VectorComponentClip.RotationKey, 0);
            child.TargetWidth = Math.Max(1, (int)Math.Round(child.TargetWidth * sx));
            child.TargetHeight = Math.Max(1, (int)Math.Round(child.TargetHeight * sy));
            child.TargetX = (int)Math.Round(parent.TargetX + parent.TargetWidth / 2f + x * MathF.Cos(rotation) - y * MathF.Sin(rotation) - child.TargetWidth / 2f);
            child.TargetY = (int)Math.Round(parent.TargetY + parent.TargetHeight / 2f + x * MathF.Sin(rotation) + y * MathF.Cos(rotation) - child.TargetHeight / 2f);
            child.MetaData ??= new();
            if (rotation != 0) child.MetaData[VectorComponentClip.RotationKey] = VectorComponentClip.ReadNumber(child.MetaData, VectorComponentClip.RotationKey, 0) + rotation;
        }
        if (children.Count == 0) throw new InvalidOperationException("Vector group has no visible children.");
        foreach (var child in children)
        {
            if (batch.Clips.ContainsKey(child.Id)) child.Id = Guid.NewGuid();
            batch.Tracks.Add((int)child.LayerIndex);
            batch.Put(child);
        }
        batch.Remove(parent.Id);
        return children.Select(GuiClipSnapshot).ToArray();
    }

    #endregion

    #region Audio

    private static void SyncGuiBatchAudio(GuiBatchState batch, ClipDraftDTO video)
    {
        if (video.ClipType != ClipMode.VideoClip || SoundTrackMetadata.ReadBool(video.MetaData, SoundTrackMetadata.DetachedKey)) return;
        ClipDraftDTO? audio = Guid.TryParse(video.BindedSoundTrack, out var id) ? batch.Clips.GetValueOrDefault(id) : null;
        if (audio is not null && !SoundTrackMetadata.ReadBool(audio.MetaData, SoundTrackMetadata.GeneratedFromVideoKey)) return;
        video.MetaData ??= new();
        if (audio is null)
        {
            string? path = video.FilePath;
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith('#')) return;
            if (path.StartsWith('$')) path = batch.Assets.GetValueOrDefault(path[1..])?.Path;
            if (path is not null) path = batch.PendingFiles.GetValueOrDefault(path, path);
            if (!File.Exists(path)) return;
            bool hasAudio = false;
            try
            {
                using var source = PluginManager.CreateAudioSource(path!);
                hasAudio = source.SamplePerSecond > 0;
            }
            catch (Exception ex) { LogDiagnostic($"No audio stream was found for batch clip {video.Id}: {ex.Message}"); }
            video.MetaData[SoundTrackMetadata.ProbeSourceKey] = video.FilePath!;
            video.MetaData[SoundTrackMetadata.ProbeHasStreamKey] = hasAudio;
            if (!hasAudio) return;
            audio = new()
            {
                Id = Guid.NewGuid(), Name = video.Name + "'s Audio", ClipType = ClipMode.AudioClip,
                TypeName = nameof(SoundTrackToClipWrapper), FromPlugin = video.FromPlugin, MetaData = new()
                {
                    [SoundTrackMetadata.GeneratedFromVideoKey] = true,
                    [SoundTrackMetadata.SourceClipIdKey] = video.Id.ToString("D"),
                    [SoundTrackMetadata.EnabledKey] = true, [SoundTrackMetadata.VolumeKey] = 1f,
                },
            };
        }
        var node = JsonSerializer.SerializeToNode(audio, savingOpts)!;
        node["RelativeStartFrame"] = video.RelativeStartFrame;
        audio = node.Deserialize<ClipDraftDTO>(savingOpts)!;
        audio.FilePath = video.FilePath; audio.StartFrame = video.StartFrame; audio.Duration = video.Duration;
        audio.LayerIndex = video.LayerIndex; audio.SubLayerIndex = video.SubLayerIndex;
        audio.FrameTime = video.FrameTime; audio.SourceDuration = video.SourceDuration;
        audio.IsInfiniteLength = video.IsInfiniteLength; audio.ShouldDisplayInUI = false;
        video.BindedSoundTrack = audio.Id.ToString("D");
        batch.Put(audio);
    }

    #endregion
}
