using projectFrameCut.ApplicationAPIBase.Project;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.ProjectReferences;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Shared;
using System.Windows.Input;

namespace projectFrameCut;

public partial class DraftPage
{
    public ICommand ImportProjectReferenceCommand => new Command(async () => await ImportProjectReferenceAsync(), () => !IsReadonly);

    private async Task ImportProjectReferenceAsync()
    {
        if (IsReadonly) return;
        var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = Localized.ProjectReference_Import });
        if (file is null) return;
        string? mode = await DisplayActionSheetAsync(Localized.ProjectReference_Mode, Localized._Cancel, null,
            Localized.ProjectReference_Snapshot, Localized.ProjectReference_Linked);
        if (mode != Localized.ProjectReference_Snapshot && mode != Localized.ProjectReference_Linked) return;
        try
        {
            SetStateBusy();
            var reference = await Task.Run(() => ProjectReferenceStore.Capture(file.FullPath, WorkingPath,
                ProjectInfo.ProjectUniqueId, mode == Localized.ProjectReference_Linked ? ProjectReferenceMode.Linked : ProjectReferenceMode.Snapshot));
            uint layer = (uint)Math.Max(0, _selected?.origTrack ?? Tracks.Keys.Where(k => k < SubTrackOffset).DefaultIfEmpty(0).Min());
            var dto = ProjectReferenceEditing.Create(reference, ProjectInfo, WorkingPath, (uint)Math.Max(0, CurrentFrame), layer);
            await Task.Run(() => ProjectReferenceEditing.Validate(dto, WorkingPath));
            var imported = DraftImportAndExportHelper.ImportFromJSON(new() { Clips = [dto] }, ProjectInfo).Item1[dto.Id];
            AddReferenceTrack((int)layer);
            RegisterClip(imported, false);
            AddAClip(imported);
            ClearSelectionInternal();
            AddClipToSelection(imported);
            OnClipChanged?.Invoke(this, new ClipUpdateEventArgs { Reason = ClipUpdateReason.ClipAdded, DetailInfo = Localized.ProjectReference_Import });
            await RefreshSelectionUiAsync();
        }
        catch (Exception ex) { await ReportProjectReferenceErrorAsync(ex); }
        finally { SetStateOK(); }
    }

    private void AddReferenceTrack(int index)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        if (Tracks.ContainsKey(index)) return;
        if (index >= SubTrackOffset) AddASubTrack(index);
        else AddATrack(index);
    }

    public async Task RefreshProjectReferenceAsync(ClipElementUI clip, bool relocate = false)
    {
        if (IsReadonly || ProjectReference.Read(clip.ExtraData) is not { } reference) return;
        string source = reference.SourcePath;
        if (relocate)
        {
            var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = Localized.ProjectReference_Relocate });
            if (file is null) return;
            source = file.FullPath;
        }
        try
        {
            SetStateBusy();
            var refreshed = await Task.Run(() => ProjectReferenceStore.Capture(ProjectReferenceStore.Resolve(WorkingPath, source),
                WorkingPath, ProjectInfo.ProjectUniqueId, reference.Mode));
            var dto = ProjectReferenceEditing.Create(refreshed, ProjectInfo, WorkingPath, 0, 0);
            await Task.Run(() => ProjectReferenceEditing.Validate(dto, WorkingPath));
            clip.ExtraData[ProjectReference.MetadataKey] = refreshed;
            clip.ExtraData.Remove("ProjectReferenceError");
            ClipInitializationFailure.Clear(clip.ExtraData);
            clip.ClearInitializationFailureIndicator();
            clip.SourcePath = refreshed.SnapshotPath;
            clip.UpdateContent(null);
            clip.maxFrameCount = checked((uint)Math.Max(1, dto.SourceDuration ?? dto.Duration));
            clip.ApplyInitializationFailureIndicator();
            ApplyClipPreview(clip);
            OnClipChanged?.Invoke(this, new ClipUpdateEventArgs { Reason = ClipUpdateReason.PropertyChanged, DetailInfo = Localized.ProjectReference_Refresh });
            await RefreshSelectionUiAsync();
        }
        catch (Exception ex) { await ReportProjectReferenceErrorAsync(ex); }
        finally { SetStateOK(); }
    }

    public async Task UngroupProjectReferenceAsync(ClipElementUI clip, bool notify = true)
    {
        if (IsReadonly || clip.ClipType != ClipMode.ProjectReferenceClip) return;
        try
        {
            SetStateBusy();
            var dto = DraftImportAndExportHelper.ExportClipElementFromDraftPage(this, clip);
            var detached = await Task.Run(() => ProjectReferenceEditing.Detach(dto, ProjectInfo, WorkingPath));
            var imported = DraftImportAndExportHelper.ImportFromJSON(detached, ProjectInfo).Item1.Values.ToArray();
            if (imported.Any(c => Clips.ContainsKey(c.Id))) throw new InvalidOperationException("Detached clip ID collision.");
            foreach (var c in imported) AddReferenceTrack(c.origTrack ?? 0);
            DeleteAClip(clip, suppressClipChangedEvent: true);
            ClearSelectionInternal();
            foreach (var c in imported) RegisterClip(c, false);
            foreach (var c in imported) AddAClip(c, false);
            foreach (var c in imported) AddClipToSelection(c);
            await UpdateAdjacencyForTrack();
            if (notify) OnClipChanged?.Invoke(this, new ClipUpdateEventArgs { Reason = ClipUpdateReason.ClipUngrouped, DetailInfo = Localized.ProjectReference_Ungroup });
            Log($"Detached project reference {clip.Id} into {imported.Length} clips.");
            await RefreshSelectionUiAsync();
        }
        catch (Exception ex) when (notify) { await ReportProjectReferenceErrorAsync(ex); }
        finally { SetStateOK(); }
    }


    private async Task ReportProjectReferenceErrorAsync(Exception ex)
    {
        Log(ex, "Edit project reference", this);
        await DisplayAlertAsync(Localized._Warn, ex.Message, Localized._OK);
    }
}
