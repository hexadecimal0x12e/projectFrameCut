using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

using Path = System.IO.Path;
using Grid = Microsoft.Maui.Controls.Grid;
using Image = Microsoft.Maui.Controls.Image;
using Application = Microsoft.Maui.Controls.Application;

using projectFrameCut.Render;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Shared;
using projectFrameCut.DraftStuff;

using projectFrameCut.Setting.SettingManager;
using projectFrameCut.LivePreview;
using CommunityToolkit.Maui.Views;
using CommunityToolkit.Maui.Core;
using projectFrameCut.Services;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Asset;
using projectFrameCut.ViewModels;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.Effect;
using System.Runtime;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;
using CommunityToolkit.Maui.Alerts;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Effect;
using ITransform = projectFrameCut.Render.RenderAPIBase.ClipAndTrack.ITransform;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using System.Reflection;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Contracts;
using System.Runtime.InteropServices;
using projectFrameCut.ApplicationAPIBase.Project;
using projectFrameCut.ApplicationAPIBase.Views.TabbedView;
using projectFrameCut.ApplicationAPIBase.Workspace;
using projectFrameCut.ApplicationAPIBase.Workspace.Modules;
using projectFrameCut.InteractableEditor;
using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Drawing.Base;
using projectFrameCut.ApplicationPluginBase.Effect;
using CommunityToolkit.Maui.Extensions;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationAPIBase.Interaction;
using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json.Nodes;

#if WINDOWS
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using System.Reflection.Metadata.Ecma335;

#endif

#if iDevices
using Foundation;
using UIKit;
using MobileCoreServices;
#if IOS
using projectFrameCut.Render.HwAccelEngine.Platforms.iOS;
#endif
#endif

#if ANDROID
using projectFrameCut.Render.HwAccelEngine.Platforms.Android;
using projectFrameCut.Render.HwAccelEngine;
using Microsoft.Maui.Platform;
#endif

namespace projectFrameCut;

public partial class DraftPage : ContentPage, IDraftPage
{
    #region handle changes
    private void TryMoveToInitialPreviewFrame(DraftStructureJSON draft)
    {
        if (_hasResolvedInitialPreviewFrame)
        {
            return;
        }

        _hasResolvedInitialPreviewFrame = true;

        if (_selected is not null || _selectedClipIds.Count > 0)
        {
            return;
        }

        if (_currentFrame > 0 || draft.Clips is null || draft.Clips.Length == 0)
        {
            return;
        }

        var firstVisualStartFrame = draft.Clips
            .Where(c => c.ShouldDisplayInUI && c.ClipType != ClipMode.AudioClip && c.ClipType != ClipMode.MarkingClip)
            .Select(c => (double)c.StartFrame)
            .DefaultIfEmpty(0)
            .Min();

        if (firstVisualStartFrame <= 0)
        {
            return;
        }

        _currentFrame = firstVisualStartFrame;
        SyncClipEditorCurrentFrame();
        UpdatePlayheadPosition();
        CurrentPlayheadLabel.Text = $"{TimeSpan.FromSeconds(_currentFrame * SecondsPerFrame):mm\\:ss\\.ff} / {TimeSpan.FromSeconds(ProjectDuration * SecondsPerFrame):mm\\:ss\\.ff}";
    }

    private void StartRenderBackendWatchdog()
    {
        if (IsRemoteProject || _renderBackendWatchdogCts is not null) return;

        var cts = new CancellationTokenSource();
        _renderBackendWatchdogCts = cts;
        _renderBackendDisconnectedSinceUtc = DateTime.UtcNow;
        _ = Task.Run(() => MonitorRenderBackendAsync(cts.Token));
        _ = Task.Run(() => MonitorExternalRpcRequestsAsync(cts.Token));
        _ = Task.Run(() => MonitorGuiRpcAsync(cts.Token));
    }

    private void StopRenderBackendWatchdog()
    {
        var cts = Interlocked.Exchange(ref _renderBackendWatchdogCts, null);
        if (cts is null) return;
        try { cts.Cancel(); } catch { }
        cts.Dispose();
    }

    private async Task MonitorRenderBackendAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RenderBackendHealthCheckIntervalMs, cancellationToken).ConfigureAwait(false);
                if (AlreadyDisappeared || _renderBackendRestartLock.CurrentCount == 0) continue;

                bool connected = false;
                if (RenderRpcBootstrap.TryGetClient(out var client) && client is not null)
                {
                    using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    probeCts.CancelAfter(TimeSpan.FromSeconds(5));
                    try
                    {
                        _ = await client.GetCapabilitiesAsync(probeCts.Token).ConfigureAwait(false);
                        connected = true;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        Log(ex, "Probe render RPC backend connection", this);
                    }
                }

                if (connected)
                {
                    _renderBackendDisconnectedSinceUtc = null;
                    continue;
                }

                var now = DateTime.UtcNow;
                _renderBackendDisconnectedSinceUtc ??= now;
                if (now - _renderBackendDisconnectedSinceUtc < RenderBackendReconnectTimeout) continue;

                _renderBackendDisconnectedSinceUtc = now;
                await RestartRenderBackendAsync(showErrorDialog: false).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log(ex, "Monitor render RPC backend", this);
            }
        }
    }

    private async Task RestartRenderBackendAsync(bool showErrorDialog)
    {
        if (IsRemoteProject)
        {
            if (showErrorDialog)
                await DisplayAlertAsync(Localized._Info, Localized.DraftPage_RestartBackend_RemoteUnavailable, Localized._OK);
            return;
        }

        await _renderBackendRestartLock.WaitAsync();
        try
        {
            if (AlreadyDisappeared) return;
            if (isPlaying || _previewAudioGeneration != 0) await PauseLivePreview();

            await Dispatcher.DispatchAsync(() =>
            {
                SetStateBusy();
                SetStatusText(Localized.DraftPage_RestartBackend_Restarting);
            });

            var draft = await MainThread.InvokeOnMainThreadAsync(() =>
                DraftImportAndExportHelper.ExportFromDraftPage(this, includeUiOnlyClips: false));

            await Task.Run(() => RenderRpcBootstrap.Restart(WorkingPath, ProjectName));
            previewer.CleanupMaterializedPreviews();
            previewer.RenderSessionId = Guid.NewGuid();
            await previewer.UpdateDraft(draft).ConfigureAwait(false);
            _renderBackendDisconnectedSinceUtc = null;

            if (!AlreadyDisappeared)
            {
                await Dispatcher.DispatchAsync(async () =>
                {
                    DynamicPreviewProvider.SetClips(previewer.Clips);
                    await RefreshPreviewFromCurrentProviderAsync();
                    SetStateOK();
                    SetStatusText(Localized.DraftPage_RestartBackend_Succeeded);
                });
            }
        }
        catch (Exception ex)
        {
            _renderBackendDisconnectedSinceUtc = DateTime.UtcNow;
            Log(ex, "Restart render RPC backend", this);
            if (!AlreadyDisappeared)
            {
                await Dispatcher.DispatchAsync(() =>
                {
                    SetStateFail($"{Localized.DraftPage_RestartBackend_Failed} {Localized._ExceptionTemplate(ex)}");
                });
                if (showErrorDialog)
                    await DisplayAlertAsync(Localized._Error, $"{Localized.DraftPage_RestartBackend_Failed} {Localized._ExceptionTemplate(ex)}", Localized._OK);
            }
        }
        finally
        {
            _renderBackendRestartLock.Release();
        }
    }

    private async void DraftChanged(object? sender, ClipUpdateEventArgs e)
        => await DraftChangedAsync(sender, e);

    private async Task DraftChangedAsync(object? sender, ClipUpdateEventArgs e)
    {
        CancelGeneratedSoundTrackSync();
        if (AlreadyDisappeared) return;
        if (isPlaying || _previewAudioGeneration != 0) await PauseLivePreview();

        if (string.IsNullOrEmpty(WorkingPath))
        {
            SetStateFail(Localized.DraftPage_CannotSave_NoPath);
        }
        if (IsReadonly)
        {
            SetStateFail(Localized.DraftPage_CannotSave_Readonly);
        }

        foreach (var item in Clips)
        {
            if (!item.Value.isInfiniteLength && item.Value.lengthInFrame > item.Value.maxFrameCount)
            {
                SetStateFail($"Clip {item.Key} has a invalid length {item.Value.lengthInFrame} frames, larger than it's source {item.Value.maxFrameCount}.");
            }
        }
        if (!IsReadonly && (!e?.NoSave ?? false))
        {
            ScheduleSnapshotSave(e);
        }

        UpdatePlayheadHeight();
        DraftStructureJSON d = null!;
        SetStateBusy();
        SetStatusText(Localized.DraftPage_ApplyingChanges);

        try
        {
            bool usePreparedRenderProject = Interlocked.Exchange(ref _renderProjectPrepared, 0) == 1;
            await Dispatcher.DispatchAsync(async () =>
            {
                d = DraftImportAndExportHelper.ExportFromDraftPage(this, includeUiOnlyClips: false);
                if (!usePreparedRenderProject) await previewer.UpdateDraft(d);
            });
            ProjectDuration = Math.Max(d.Duration, d.AudioDuration);
            TryMoveToInitialPreviewFrame(d);
            await ClipEditor.UpdateClips(Clips);
            ClipEditor.SetCurrentFrame((uint)Math.Max(0, _currentFrame));
            FastPreviewEditor.SetCurrentFrame((uint)Math.Max(0, _currentFrame));
            DynamicPreviewProvider.SetClips(previewer.Clips);
            await RefreshPreviewFromCurrentProviderAsync();
            if (e.Reason == ClipUpdateReason.PropertyChanged && e.SourceId is Guid clipId && ShouldWarmClipPreview(e.DetailInfo))
                StartClipPreviewWarmup(clipId);
            SetStatusText(Localized.DraftPage_ChangesApplied);
            SetStateOK();
        }
        catch (Exception ex)
        {
            Log(ex, "apply change", this);
            SetStateFail(Localized._ExceptionTemplate(ex));
#if DEBUG
            if (await DisplayAlertAsync(Localized._Error, Localized.DraftPage_ApplyChangesFail(ex), "Throw", Localized._OK)) throw;
#else
            await DisplayAlertAsync(Localized._Error, Localized.DraftPage_ApplyChangesFail(ex), Localized._OK);
#endif

        }

    }

    private async Task OnClipEditorUpdate()
    {
        if (AlreadyDisappeared) return;

        var d = DraftImportAndExportHelper.ExportFromDraftPage(this, includeUiOnlyClips: false);
        await previewer.UpdateDraft(d);
        DynamicPreviewProvider.SetClips(previewer.Clips);

        var currentX = PlayheadLine.TranslationX - TrackHeadLayout.Width + TimelineScrollView.ScrollX;
        if (currentX < 0) currentX = 0;
        var duration = PixelToFrame(currentX);
        _currentFrame = duration;
        SyncClipEditorCurrentFrame();

        if (UseDynamicPreview)
        {
            await RefreshDynamicPreviewOverlay();
        }
        else
        {
            await RenderOneFrame(duration);
        }

        ScheduleAutoSave();
    }

    private void ScheduleAutoSave()
    {
        _autoSaveCts?.Cancel();
        _autoSaveCts?.Dispose();
        _autoSaveCts = new CancellationTokenSource();
        var cts = _autoSaveCts;

        _ = Task.Delay(AutoSaveDelayMs, cts.Token).ContinueWith(async _ =>
        {
            if (cts.Token.IsCancellationRequested) return;
            try
            {
                await Save(noSlot: true);
            }
            catch (Exception ex)
            {
                Log(ex, "auto-save failed", this);
            }
        }, TaskContinuationOptions.RunContinuationsAsynchronously);
    }

    private void ScheduleSnapshotSave(ClipUpdateEventArgs e)
    {
        if (AlreadyDisappeared || IsReadonly || string.IsNullOrWhiteSpace(WorkingPath))
        {
            return;
        }

        lock (_snapshotSaveGate)
        {
            _pendingSnapshotSaveArgs = e;
            _snapshotSaveCts?.Cancel();
            _snapshotSaveCts?.Dispose();
            _snapshotSaveCts = new CancellationTokenSource();
            var cts = _snapshotSaveCts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(SnapshotSaveDebounceMs, cts.Token);
                    if (cts.Token.IsCancellationRequested || AlreadyDisappeared || IsReadonly || string.IsNullOrWhiteSpace(WorkingPath))
                    {
                        return;
                    }

                    ClipUpdateEventArgs? pendingArgs;
                    lock (_snapshotSaveGate)
                    {
                        pendingArgs = _pendingSnapshotSaveArgs;
                        _pendingSnapshotSaveArgs = null;
                    }

                    await Save(false, pendingArgs);
                    MarkHistoryPanelDirty();
                }
                catch (TaskCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Log(ex, "snapshot save failed", this);
                }
            }, cts.Token);
        }
    }

    private void MarkHistoryPanelDirty()
    {
        _historyPanelDirty = true;
        if (MainMultiWindowView.Children.Contains(HistorySubWindow))
        {
            RefreshHistorySubWindowContent();
        }
    }

    private void RefreshHistorySubWindowContent()
    {
        try
        {
            HistorySubWindow.Content = new DraftSettingPage(this).HistoryTabContent;
            _historyPanelDirty = false;
        }
        catch (Exception ex)
        {
            Log(ex, "refresh history panel", this);
        }
    }


    private async void PlayheadTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            // RulerLayout and PlayheadLine are both children of LowerContent, so the ruler's
            // local X is also the playhead's viewport X. Keep this conversion entirely in the
            // timeline window's coordinate space: a floating MultiWindowItem may be translated
            // relative to the page, and including that page offset would turn window movement
            // into an apparent timeline offset.
            var p = e.GetPosition(RulerLayout);
            if (p is null) return;

            double timelineX = p.Value.X - TrackHeadLayout.Width + TimelineScrollView.ScrollX;
            if (timelineX >= 0)
            {
                // Clip edges and the snap grid are expressed in timeline-content coordinates,
                // not viewport/page coordinates.
                double snappedTimelineX = SnapPixels(timelineX);
                var duration = PixelToFrame(snappedTimelineX);
                _currentFrame = duration;
                SyncClipEditorCurrentFrame();
                UpdatePlayheadPosition();
                CurrentPlayheadLabel.Text = $"{TimeSpan.FromSeconds(duration * SecondsPerFrame):mm\\:ss\\.ff} / {TimeSpan.FromSeconds(ProjectDuration * SecondsPerFrame):mm\\:ss\\.ff}";

                // Debounce overlapping clicks so only the latest playhead position is rendered. The
                // playhead/label above already moved synchronously; RenderOneFrame additionally
                // supersedes any in-flight render so a burst of clicks can never wedge the preview.
                _movePlayheadDebounceCts?.Cancel();
                _movePlayheadDebounceCts = new CancellationTokenSource();
                var renderToken = _movePlayheadDebounceCts.Token;
                try
                {
                    await Task.Delay(120, renderToken);
                    if (!renderToken.IsCancellationRequested)
                    {
                        await RenderOneFrame(duration);
                    }
                }
                catch (TaskCanceledException) { }
            }
        }
        catch (Exception ex)
        {
            Log(ex, "playhead tap", this);
        }
    }

    private void TimelineScrollView_Scrolled(object sender, ScrolledEventArgs e)
    {
        if (sender == TimelineScrollView)
        {
            if (Math.Abs(SubTimelineScrollView.ScrollX - e.ScrollX) > 0.1)
                SubTimelineScrollView.ScrollToAsync(e.ScrollX, 0, false);
        }
        else if (sender == SubTimelineScrollView)
        {
            if (Math.Abs(TimelineScrollView.ScrollX - e.ScrollX) > 0.1)
                TimelineScrollView.ScrollToAsync(e.ScrollX, 0, false);
        }
        UpdatePlayheadPosition(e.ScrollX);

        // Notify all active clip previews so they update their visible frame range
        NotifyClipPreviewsScrollChanged(e.ScrollX);
    }

    private void NotifyClipPreviewsScrollChanged(double scrollX)
    {
        double viewportWidth = TimelineScrollView.Width;
        List<Guid>? toRemove = null;
        foreach (var kvp in _activeClipPreviews)
        {
            if (!kvp.Value.NotifyScrollChanged(scrollX, viewportWidth))
            {
                toRemove ??= new();
                toRemove.Add(kvp.Key);
            }
        }
        if (toRemove is not null)
        {
            foreach (var id in toRemove)
            {
                if (_activeClipPreviews.TryGetValue(id, out var detached))
                {
                    detached.Dispose();
                    _activeClipPreviews.Remove(id);
                }
            }
        }
    }

    /// <summary>
    /// Returns the current horizontal scroll position and viewport width of the
    /// main timeline ScrollView. Used by <see cref="OnClipUIPreview"/> to perform
    /// viewport-culled frame rendering without direct access to the private field.
    /// </summary>
    internal (double scrollX, double viewportWidth) GetTimelineScrollState()
        => (TimelineScrollView.ScrollX, TimelineScrollView.Width);

    private async Task MovePlayhead(int deltaFrames)
    {
        var targetFrame = _currentFrame + deltaFrames;
        if (targetFrame < 0) targetFrame = 0;
        _currentFrame = targetFrame;
        SyncClipEditorCurrentFrame();

        UpdatePlayheadPosition();
        CurrentPlayheadLabel.Text = $"{TimeSpan.FromSeconds(_currentFrame * SecondsPerFrame):mm\\:ss\\.ff} / {TimeSpan.FromSeconds(ProjectDuration * SecondsPerFrame):mm\\:ss\\.ff}";

        var timeX = FrameToPixel((uint)_currentFrame);

        // Auto Scroll
        var scrollX = TimelineScrollView.ScrollX;
        var viewportWidth = TimelineScrollView.Width;

        if (viewportWidth > 0)
        {
            double margin = 50;

            if (timeX < scrollX + margin)
            {
                await TimelineScrollView.ScrollToAsync(Math.Max(0, timeX - margin), 0, true);
                // After scrolling, the playhead position (overlay) needs update? 
                // The ScrollView.Scrolled event usually handles this, but calling it manually ensures sync.
                UpdatePlayheadPosition();
            }
            else if (timeX > scrollX + viewportWidth - margin)
            {
                await TimelineScrollView.ScrollToAsync(timeX - viewportWidth + margin, 0, true);
                UpdatePlayheadPosition();
            }
        }

        // Render Logic
        _movePlayheadDebounceCts?.Cancel();

        if (previewer.IsFrameRendered((uint)_currentFrame))
        {
            await RenderOneFrame((uint)_currentFrame);
        }
        else
        {
            _movePlayheadDebounceCts = new CancellationTokenSource();
            var token = _movePlayheadDebounceCts.Token;
            try
            {
                await Task.Delay(200, token);
                if (!token.IsCancellationRequested)
                {
                    await RenderOneFrame((uint)_currentFrame);
                }
            }
            catch (TaskCanceledException) { }
        }
    }

    private void UpdatePlayheadPosition() => UpdatePlayheadPosition(TimelineScrollView.ScrollX);

    private void UpdatePlayheadPosition(double scrollX)
    {
        double timeX = FrameToPixel((uint)_currentFrame);
        double screenX = timeX + TrackHeadLayout.Width - scrollX;
        PlayheadLine.TranslationX = screenX;
    }

    private void UpdatePlayheadHeight()
    {
        PlayheadLine.HeightRequest = (TrackContentLayout.Children.Count + SubTrackContentLayout.Children.Count) * ClipHeight;
    }

    private void UpdateTimelineWidth()
    {
        double maxPixel = 0;
        foreach (var clip in Clips.Values)
        {
            double end = clip.Clip.TranslationX + clip.Clip.WidthRequest;
            if (end > maxPixel) maxPixel = end;
        }

        maxPixel += 50;

        double minWidth = Math.Max(1000d, Window?.Width ?? 2000 + 200);
        if (maxPixel < minWidth) maxPixel = minWidth;

        TrackContentLayout.WidthRequest = maxPixel;
        SubTrackContentLayout.WidthRequest = maxPixel;

        // 更新所有ext endToWholeDraft的clips，使其能延伸到整个项目
        UpdateAllExtendToWholeDraftClips();
    }

    [DebuggerNonUserCode()] //too annoying in step-through debugging
    internal IClip? GetLoadedClipInstance(Guid clipId)
    {
        return previewer?.Clips?.FirstOrDefault(c => c.Id == clipId);
    }

    internal IClip? GetOrCreateClipInstance(ClipElementUI element)
    {
        if (GetLoadedClipInstance(element.Id) is IClip c) return c;
        try
        {
            return PluginManager.CreateClip(JsonSerializer.SerializeToElement(DraftImportAndExportHelper.ExportClipElementFromDraftPage(this, element)));
        }
        catch (Exception ex)
        {
            Log(ex, $"Create clip instance for {element.Id} failed", this);
            return null;
        }
    }

    [DebuggerNonUserCode()]
    private IClip? OnGetClipInstanceCallback(ClipElementUI element) => GetOrCreateClipInstance(element);

    #endregion

    #region save, undo and redo
    public async Task Save(bool noSlot = false, ClipUpdateEventArgs? args = null, bool throwOnFailure = false)
    {
        if (string.IsNullOrEmpty(WorkingPath))
        {
            if (throwOnFailure) throw new InvalidOperationException("Project working path is empty.");
            Log("saving failed: working path is empty", "warn");
            SetStateFail(Localized.DraftPage_CannotSave_NoPath);
            return;
        }
        if (IsReadonly)
        {
            if (throwOnFailure) throw new InvalidOperationException("Project is read-only.");
            Log("saving failed: project is read-only", "warn");
            SetStateFail(Localized.DraftPage_CannotSave_Readonly);
            return;
        }
        if (!await saveLocker.WaitAsync(1500))
        {
            if (throwOnFailure) throw new TimeoutException("Project save is busy.");
            Log("Cannot save because of failed to acquire save lock, skipping this save.", "warn");
            return;
        }
        try
        {
            var draft = DraftImportAndExportHelper.ExportFromDraftPage(this, includeUiOnlyClips: true);
            var assets = Assets.Values.ToList();
            var timelineModule = _workspace.GetModule<TimelineModule>();
            var assetModule = _workspace.GetModule<AssetModule>();
            timelineModule.Reset(draft.Clips);
            assetModule.Reset(assets);

            if (_remoteProjectSession is not null)
            {
                ProjectInfo.LastChanged = DateTime.Now;
                ProjectInfo.LastOpenAPIBaseVersion = IPluginBase.CurrentPluginAPIVersion;
                ProjectInfo.LastOpenAppVersion = Assembly.GetExecutingAssembly()?.GetName()?.Version?.ToString() ?? "0.0.0.0";
                ProjectInfo.LastOpenAppName = MauiProgram.AssemblyName;
                ProjectInfo.LastOpenAppIdentifier = MauiProgram.AppIdentifier;
                ProjectInfo.ProjectName ??= ProjectName;
                ProjectDuration = draft.Duration;
                try
                {
                    await _remoteProjectSession.ApplyAndSaveAsync(
                        ProjectInfo,
                        draft,
                        assets,
                        args?.ToString() ?? (noSlot ? "Auto-save" : "Save"));
                }
                catch (RemoteRenderException ex) when (ex.ErrorCode == RenderErrorCode.VersionConflict)
                {
                    Log("Remote project was modified by another client; local changes conflict with the server.", "warn");
                    SetStateFail(Localized.DraftPage_RemoteProject_ModifiedOnServer);
                    if (!noSlot)
                    {
                        bool sync = await DisplayAlertAsync(
                            Localized._Info,
                            Localized.DraftPage_RemoteProject_ModifiedOnServer,
                            Localized.DraftPage_RemoteProject_SyncFromServer,
                            Localized._Cancel);
                        if (sync) await SyncRemoteProjectFromServerAsync();
                    }
                    return;
                }
                _workspace.GetModule<ProjectModule>().MarkClean();
                SetStateOK(Localized.DraftPage_EverythingFine);
                return;
            }

            var snapshotId = Guid.NewGuid();
            string slot = ".";
            if (noSlot)
            {
                ProjectInfo.NormallyExited = true;
                await Task.Run(SaveProjectThumbnailBeforeExit);
                await timelineModule.SaveAsync(draft);
                await assetModule.SaveAsync();
            }
            else //avoid worst condition (crashes while saving)
            {
                slot = $"slot_{snapshotId}";
                ProjectInfo.SnapshotIDMapping[snapshotId] = new ProjectJSONStructure.SnapshotIDMappingStructure
                {
                    Previous = PreviousSnapshotID
                };
                if (PreviousSnapshotID != Guid.Empty && ProjectInfo.SnapshotIDMapping.TryGetValue(PreviousSnapshotID, out var prevEntry))
                {
                    if (!prevEntry.Next.Contains(snapshotId))
                        prevEntry.Next.Add(snapshotId);
                }
                ProjectInfo.LastSnapshotID = snapshotId;
                CurrentSnapshotID = snapshotId;
                LogDiagnostic($"Switching slot to {snapshotId}...");

                if (MaximumSaveSlot >= 0)
                {
                    PruneOldestSaveSlots();
                }

                if (args is not null)
                {
                    draft.ChangeReason = args.ToString();
                    draft.DetailedChangeReason = args.DetailInfo ?? string.Empty;
                }
                draft.Operator = args?.Operator ?? ClipChangeOperatorKind.User;
                draft.OperatorDetailName = args?.OperatorDetailName ?? string.Empty;
                draft.ChangedByUserDisplayName = SettingsManager.GetSetting("UserName", "User");
                draft.ChangedByUser = SettingsManager.GetSettingAs("UserID", Guid.Empty, Guid.Empty);
                draft.PreviousSnapshot = PreviousSnapshotID;
                draft.SnapshotID = snapshotId;
                Directory.CreateDirectory(Path.Combine(WorkingPath, "saveSlots", slot));
                await _workspace.GetModule<HistoryModule>().SaveSnapshotAsync(draft, slot);
                await _workspace.Context.Storage.WriteTextAsync(Path.Combine("saveSlots", slot, "assets.json"), JsonSerializer.Serialize(assets, savingOpts));
                PreviousSnapshotID = draft.SnapshotID;
                _historyNavigatedByUndoRedo = false;
            }

            ProjectDuration = draft.Duration;
            ProjectInfo.LastChanged = DateTime.Now;
            ProjectInfo.LastOpenAPIBaseVersion = IPluginBase.CurrentPluginAPIVersion;
            ProjectInfo.LastOpenAppVersion = Assembly.GetExecutingAssembly()?.GetName()?.Version?.ToString() ?? "0.0.0.0";
            ProjectInfo.LastOpenAppName = MauiProgram.AssemblyName;
            ProjectInfo.LastOpenAppIdentifier = MauiProgram.AppIdentifier;
            ProjectInfo.PluginUsed =
                draft.Clips
                           .Select(c => c.FromPlugin)
                           .Concat(draft.Clips.SelectMany(c => c.Effects?.Select(eff => eff.FromPlugin) ?? []))
                           .Concat(draft.Clips.SelectMany(c => c.EffectProviders?.Select(eff => eff.FromPlugin) ?? []))
                           .Where(c => !c.StartsWith("projectFrameCut.Render."))
                           .Distinct().ToList();

            if (ProjectInfo.ProjectUniqueId == Guid.Empty)
            {
                ProjectInfo.ProjectUniqueId = Guid.NewGuid();
            }

            if (ClipEditor != null)
            {
                ProjectInfo.Properties["InteractableEditor_LockLayout"] = ClipEditor.LockLayout.ToString();
                ProjectInfo.Properties["InteractableEditor_EnableSnapping"] = ClipEditor.EnableSnapping.ToString();
                ProjectInfo.Properties["InteractableEditor_DisallowClipOutOfBounds"] = ClipEditor.DisallowClipOutOfBounds.ToString();
                ProjectInfo.Properties["InteractableEditor_ShowReferenceLines"] = ClipEditor.ShowReferenceLines.ToString();
                ProjectInfo.Properties["InteractableEditor_EnableKeyframeRecording"] = ClipEditor.EnableKeyframeRecording.ToString();
            }

            if (!AlreadyDisappeared) _workspaceWindowHost.SaveLayout();
            await _workspace.GetModule<ProjectModule>().SaveProjectAsync();
            DraftImportAndExportHelper.EnsureProjectDirectoryShellIntegration(WorkingPath);
        }
        catch (Exception ex)
        {
            Log(ex, "saving draft failed", this);
            if (throwOnFailure) throw;
            SetStateFail(Localized.DraftPage_CannotSave_Exception(ex));
        }
        finally
        {
            saveLocker.Release();
        }

    }

    private List<SaveSlotMeta> GetSaveSlotsSortedByTime()
    {
        List<SaveSlotMeta> slots = [];
        if (string.IsNullOrWhiteSpace(WorkingPath))
        {
            return slots;
        }

        var saveRoot = Path.Combine(WorkingPath, "saveSlots");
        if (!Directory.Exists(saveRoot))
        {
            return slots;
        }

        foreach (var dir in Directory.GetDirectories(saveRoot, "slot_*"))
        {
            var folderName = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(folderName) || !folderName.StartsWith("slot_"))
            {
                continue;
            }

            var timelinePath = Path.Combine(dir, "timeline.json");
            if (!File.Exists(timelinePath))
            {
                continue;
            }

            try
            {
                var tml = File.ReadAllText(timelinePath);
                var draft = JsonSerializer.Deserialize<DraftStructureJSON>(tml, savingOpts);
                if (draft is null)
                {
                    continue;
                }

                var snapshotId = draft.SnapshotID;
                if (snapshotId == Guid.Empty)
                {
                    continue;
                }

                DateTime savedAtUtc;
                if (draft.SavedAt == default)
                {
                    savedAtUtc = File.GetLastWriteTimeUtc(timelinePath);
                }
                else
                {
                    savedAtUtc = draft.SavedAt.Kind switch
                    {
                        DateTimeKind.Utc => draft.SavedAt,
                        DateTimeKind.Local => draft.SavedAt.ToUniversalTime(),
                        _ => DateTime.SpecifyKind(draft.SavedAt, DateTimeKind.Local).ToUniversalTime()
                    };
                }

                slots.Add(new SaveSlotMeta
                {
                    SnapshotID = snapshotId,
                    SavedAtUtc = savedAtUtc
                });
            }
            catch
            {
                // Ignore broken slot data to keep undo/redo usable.
            }
        }

        return slots
            .OrderBy(x => x.SavedAtUtc)
            .ThenBy(x => x.SnapshotID)
            .ToList();
    }

    private SaveSlotMeta? GetPreviousSlot()
    {
        if (CurrentSnapshotID == Guid.Empty
            || !ProjectInfo.SnapshotIDMapping.TryGetValue(CurrentSnapshotID, out var entry)
            || entry.Previous == Guid.Empty)
        {
            return null;
        }

        return new SaveSlotMeta { SnapshotID = entry.Previous, SavedAtUtc = DateTime.MinValue };
    }

    private List<SaveSlotMeta> GetNextSlots()
    {
        if (CurrentSnapshotID == Guid.Empty
            || !ProjectInfo.SnapshotIDMapping.TryGetValue(CurrentSnapshotID, out var entry)
            || entry.Next.Count == 0)
        {
            return new List<SaveSlotMeta>();
        }

        return entry.Next.Select(id =>
        {
            var savedAt = GetSlotSavedAt(id);
            return new SaveSlotMeta { SnapshotID = id, SavedAtUtc = savedAt };
        }).ToList();
    }

    private DateTime GetSlotSavedAt(Guid snapshotId)
    {
        var slotPath = FindSlotDirectory(snapshotId);
        if (slotPath is null) return DateTime.MinValue;
        var timelinePath = Path.Combine(slotPath, "timeline.json");
        if (!File.Exists(timelinePath)) return DateTime.MinValue;
        try
        {
            var tml = File.ReadAllText(timelinePath);
            var draft = JsonSerializer.Deserialize<DraftStructureJSON>(tml, savingOpts);
            return draft?.SavedAt ?? DateTime.MinValue;
        }
        catch { return DateTime.MinValue; }
    }

    private void PruneNewerSaveSlotsFromCurrent()
    {
        if (CurrentSnapshotID == Guid.Empty
            || !ProjectInfo.SnapshotIDMapping.TryGetValue(CurrentSnapshotID, out var curEntry))
            return;

        foreach (var nextId in curEntry.Next.ToList())
        {
            PruneRecursive(nextId);
        }
        curEntry.Next.Clear();
        ProjectInfo.SnapshotIDMapping[CurrentSnapshotID] = curEntry;
    }

    private void PruneRecursive(Guid id)
    {
        if (!ProjectInfo.SnapshotIDMapping.TryGetValue(id, out var entry)) return;
        foreach (var nextId in entry.Next.ToList())
        {
            PruneRecursive(nextId);
        }
        DeleteSlotDirectory(id);
        ProjectInfo.SnapshotIDMapping.Remove(id);
    }

    private void PruneOldestSaveSlots()
    {
        while (ProjectInfo.SnapshotIDMapping.Count > MaximumSaveSlot && MaximumSaveSlot >= 0)
        {
            var leafToRemove = ProjectInfo.SnapshotIDMapping
                .Where(kv => kv.Value.Next.Count == 0 && kv.Key != CurrentSnapshotID)
                .Select(kv => new { kv.Key, SavedAt = GetSlotSavedAt(kv.Key) })
                .OrderBy(x => x.SavedAt)
                .FirstOrDefault();

            if (leafToRemove is null || leafToRemove.Key == Guid.Empty) break;

            if (ProjectInfo.SnapshotIDMapping.TryGetValue(leafToRemove.Key, out var leafEntry)
                && leafEntry.Previous != Guid.Empty
                && ProjectInfo.SnapshotIDMapping.TryGetValue(leafEntry.Previous, out var prevEntry))
            {
                prevEntry.Next.Remove(leafToRemove.Key);
            }

            DeleteSlotDirectory(leafToRemove.Key);
            ProjectInfo.SnapshotIDMapping.Remove(leafToRemove.Key);
        }
    }

    private void DeleteSlotDirectory(Guid snapshotId)
    {
        try
        {
            var slotPath = Path.Combine(WorkingPath, "saveSlots", $"slot_{snapshotId}");
            if (Directory.Exists(slotPath))
            {
                Directory.Delete(slotPath, true);
                LogDiagnostic($"Pruned save slot: slot_{snapshotId}");
            }
        }
        catch (Exception ex)
        {
            Log(ex, $"prune save slot slot_{snapshotId}", this);
        }
    }

    private async Task RedoChanges()
    {
        var nextSlots = GetNextSlots();
        if (nextSlots is null || nextSlots.Count == 0)
        {
            SetStateOK(Localized.DraftPage_RedoAndUndo_NoMoreSlots);
            return;
        }
        if (IsSyncCooldown()) return;
        SetSyncCooldown();
        var oldSlot = CurrentSnapshotID;
        // If multiple next slots exist (branch), pick the most recently saved one
        var nextSlot = nextSlots.Count == 1
            ? nextSlots[0]
            : nextSlots.OrderByDescending(s => s.SavedAtUtc).First();
        await ApplySlot(nextSlot.SnapshotID);
        if (CurrentSnapshotID != oldSlot)
        {
            _historyNavigatedByUndoRedo = true;
        }
    }

    private async Task UndoChanges()
    {
        var nextSlot = GetPreviousSlot();
        if (nextSlot is null)
        {
            SetStateOK(Localized.DraftPage_RedoAndUndo_NoMoreSlots);
            return;
        }
        if (IsSyncCooldown()) return;
        SetSyncCooldown();
        var oldSlot = CurrentSnapshotID;
        await ApplySlot(nextSlot.SnapshotID);
        if (CurrentSnapshotID != oldSlot)
        {
            _historyNavigatedByUndoRedo = true;
        }
    }

    private ProjectHistoryState GetProjectHistoryState()
    {
        var previous = GetPreviousSlot();
        return new()
        {
            CurrentSnapshotId = CurrentSnapshotID,
            CanUndo = previous is not null && FindSlotDirectory(previous.SnapshotID) is not null,
            CanRedo = GetNextSlots().Any(x => FindSlotDirectory(x.SnapshotID) is not null),
        };
    }

    private ProjectHistory GetProjectHistory()
    {
        var (nodes, _) = HistoryGraphDataBuilder.BuildFromDraftPage(this);
        return new()
        {
            State = GetProjectHistoryState(),
            Nodes = nodes.Select(x => new ProjectHistoryNode
            {
                SnapshotId = x.SnapshotID,
                PreviousSnapshotId = x.PreviousSnapshotID,
                NextSnapshotIds = x.NextSnapshotIDs.ToList(),
                SavedAtUtc = x.SavedAt == default ? DateTime.MinValue : x.SavedAt.Kind switch
                {
                    DateTimeKind.Utc => x.SavedAt,
                    DateTimeKind.Local => x.SavedAt.ToUniversalTime(),
                    _ => DateTime.SpecifyKind(x.SavedAt, DateTimeKind.Local).ToUniversalTime(),
                },
                ChangeReason = x.ChangeReason,
                ChangedBy = x.ChangedByUserDisplayName,
                ChangedByUserId = x.ChangedByUser,
                IsCurrentSnapshot = x.IsCurrentSnapshot,
            }).ToList(),
        };
    }

    private async Task<ProjectHistoryState> ApplyProjectHistorySnapshotAsync(Guid snapshotId, bool navigatedByUndoRedo)
    {
        if (snapshotId == Guid.Empty) throw new ArgumentException("SnapshotId is required.");
        if (snapshotId == CurrentSnapshotID) return GetProjectHistoryState();
        if (FindSlotDirectory(snapshotId) is null) throw new KeyNotFoundException("Project history snapshot not found.");
        await ApplySlot(snapshotId);
        if (CurrentSnapshotID != snapshotId) throw new InvalidOperationException("Project history snapshot could not be restored.");
        if (navigatedByUndoRedo) _historyNavigatedByUndoRedo = true;
        return GetProjectHistoryState();
    }

    /// <summary>
    /// Registers an <see cref="IHistoryGraphProvider"/> for this DraftPage session.
    /// The provider's <see cref="IHistoryGraphProvider.CurrentSnapshotChanged"/> event
    /// is raised when <see cref="ApplySlot"/> applies a new snapshot.
    /// </summary>
    public void RegisterHistoryProvider(IHistoryGraphProvider provider)
    {
        _activeHistoryProvider = provider;
    }

    public async Task ApplySlot(Guid snapshotId)
    {
        try
        {
            LogDiagnostic($"Switching slot from {CurrentSnapshotID} to {snapshotId}...");
            var slotPath = FindSlotDirectory(snapshotId);
            if (slotPath is null)
            {
                SetStateOK(Localized.DraftPage_RedoAndUndo_Failed);
                return;
            }

            var tml = File.ReadAllText(Path.Combine(slotPath, "timeline.json"));
            var assets = JsonSerializer.Deserialize<List<AssetItem>>(File.ReadAllText(Path.Combine(slotPath, "assets.json")), savingOpts) ?? new();
            var draftJson = JsonSerializer.Deserialize<DraftStructureJSON>(tml, savingOpts);
            if (draftJson is null)
            {
                SetStateOK(Localized.DraftPage_RedoAndUndo_Failed);
                return;
            }
            (var clips, var tracks) = DraftImportAndExportHelper.ImportFromJSON(draftJson, ProjectInfo);
            Clips = new ConcurrentDictionary<Guid, ClipElementUI>(clips);
            Assets = new ConcurrentDictionary<string, AssetItem>(assets.ToDictionary((a) => a.AssetId ?? $"unknown+{Random.Shared.Next()}", (a) => a));
            ClipEditor.SetAssets(Assets);

            foreach (var item in Tracks)
            {
                var t = item.Value;
                while (t.Children.Count > 0)
                {
                    t.Children.RemoveAt(0);
                }
            }

            foreach (var kv in Clips.OrderBy(kv => kv.Value.origTrack ?? 0).ThenBy(kv => kv.Value.origX))
            {
                var item = kv.Value;
                int t = item.origTrack ?? 0;
                if (!Tracks.ContainsKey(t)) AddATrack(t);
                AddAClip(item);
                RegisterClip(item, true);
            }

            EnsureContinuousTrackIndices();
            await ClipEditor.UpdateClips(Clips);
            CurrentSnapshotID = snapshotId;
            _activeHistoryProvider?.NotifyExternalSnapshotChanged();
            PreviousSnapshotID = draftJson.PreviousSnapshot;
            RefreshHistorySubWindowContent();
            await DraftChangedAsync(this, new() { DetailInfo = "Sync changes", NoSave = true });
            SetStateOK(Localized.DraftPage_RedoAndUndo_Success(draftJson.SavedAt));

        }
        catch (Exception ex)
        {
            if (MyLoggerExtensions.LoggingDiagnosticInfo)
            {
                Log(ex, "apply changes", this);
            }
            SetStateOK(Localized.DraftPage_RedoAndUndo_Failed);
        }
    }

    private string? FindSlotDirectory(Guid snapshotId)
    {
        var saveRoot = Path.Combine(WorkingPath, "saveSlots");
        if (!Directory.Exists(saveRoot))
        {
            return null;
        }

        // Try new format first: slot_<guid>
        var directPath = Path.Combine(saveRoot, $"slot_{snapshotId}");
        if (Directory.Exists(directPath) && File.Exists(Path.Combine(directPath, "timeline.json")))
        {
            return directPath;
        }

        // Fallback: scan directories for matching SnapshotID in timeline.json
        foreach (var dir in Directory.GetDirectories(saveRoot, "slot_*"))
        {
            var timelinePath = Path.Combine(dir, "timeline.json");
            if (!File.Exists(timelinePath))
            {
                continue;
            }

            try
            {
                var tml = File.ReadAllText(timelinePath);
                var draft = JsonSerializer.Deserialize<DraftStructureJSON>(tml, savingOpts);
                if (draft?.SnapshotID == snapshotId)
                {
                    return dir;
                }
            }
            catch
            {
                // Skip unreadable slots
            }
        }

        return null;
    }

    private bool IsSyncCooldown() => DateTime.Now - lastSyncTime < SyncCooldown;
    private void SetSyncCooldown() => lastSyncTime = DateTime.Now;


    #endregion
}