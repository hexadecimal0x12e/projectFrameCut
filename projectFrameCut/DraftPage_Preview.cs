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
    #region live preview
    SemaphoreSlim renderingLock = new(1, 1);
    private CancellationTokenSource? _renderOneFrameCts;
    private readonly object _renderCtsLock = new();
    private const int InitialStaticPreviewLongEdge = 240;
    private const int MaxConcurrentStaticPreviewRenders = 3;

    private CancellationToken ResetDynamicPreviewToken()
    {
        lock (_dynamicPreviewCtsLock)
        {
            _dynamicPreviewCts?.Cancel();
            // Intentionally not disposed here: an in-flight preparation task may still observe the
            // token. Disposing eagerly raced with that observer and surfaced as random
            // ObjectDisposedExceptions that left the preview stuck on a black frame. This CTS uses no
            // timer, so letting the GC reclaim it is safe.
            _dynamicPreviewCts = new CancellationTokenSource();
            return _dynamicPreviewCts.Token;
        }
    }

    private void OnClipInitializationFailed(Guid clipId, string description)
    {
        Dispatcher.Dispatch(() =>
        {
            if (!Clips.TryGetValue(clipId, out var clip)) return;
            if (ClipInitializationFailure.IsMarked(clip.ExtraData)) return;
            ClipInitializationFailure.Mark(clip.ExtraData, "Source or ResolveEffect", new InvalidOperationException(description));
            clip.ApplyInitializationFailureIndicator();
            DraftChanged(this, new ClipUpdateEventArgs
            {
                DetailInfo = $"Clip initialization failed: {clip.DisplayName}",
                ChangedClipID = clip.Id,
                SourceId = clip.Id,
                SourceName = clip.DisplayName,
                Reason = ClipUpdateReason.PropertyChanged
            });
        });
    }

    internal bool DeleteClipForService(Guid clipId)
    {
        if (!Clips.TryGetValue(clipId, out var clip))
        {
            return false;
        }

        DeleteAClip(clip);
        return true;
    }

    private void OnClipInitializationRecovered(Guid clipId)
    {
        Dispatcher.Dispatch(() =>
        {
            if (!Clips.TryGetValue(clipId, out var clip) || !ClipInitializationFailure.IsMarked(clip.ExtraData)) return;
            clip.ClearInitializationFailureIndicator();
            DraftChanged(this, new ClipUpdateEventArgs
            {
                DetailInfo = $"Clip initialization recovered: {clip.DisplayName}",
                ChangedClipID = clip.Id,
                SourceId = clip.Id,
                SourceName = clip.DisplayName,
                Reason = ClipUpdateReason.PropertyChanged
            });
        });
    }

    private void CancelDynamicPreview()
    {
        lock (_dynamicPreviewCtsLock)
        {
            _dynamicPreviewCts?.Cancel();
        }
    }

    internal async Task RefreshPreviewFromCurrentProviderAsync()
    {
#if DIAGHUB_ENABLE_TRACE_SYSTEM
        using var refreshMark = new UserMarkRange("Preview.RefreshProvider", $"frame={_currentFrame}, dynamic={UseDynamicPreview}");
#endif
        if (UseDynamicPreview)
        {
            await RefreshDynamicPreviewOverlay();
            return;
        }

        await RenderOneFrame((uint)_currentFrame);
    }

    private async Task<bool> RefreshDynamicPreviewOverlay()
    {
#if DIAGHUB_ENABLE_TRACE_SYSTEM
        using var overlayMark = new UserMarkRange("Preview.RefreshDynamicOverlay", $"frame={_currentFrame}");
#endif
        // 取消任何正在进行的预览准备操作。重建令牌的过程串行化，避免并发 Dispose 触发
        // ObjectDisposedException（这是预览高概率卡在黑屏的根因之一）。
        var token = ResetDynamicPreviewToken();

        try
        {
            var targetWidth = Math.Max(1, previewWidth);
            var targetHeight = Math.Max(1, previewHeight);
            var preparedPreviews = await DynamicPreviewProvider.PrepareFrameAsync((uint)_currentFrame, targetWidth, targetHeight, token);
            token.ThrowIfCancellationRequested();
            return await ActivePreviewEditor.ApplyPreparedPreviewsAsync(preparedPreviews);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log(ex, "Render one frame via DynamicPreview", this);
            SetStateFail(Localized._ExceptionTemplate(ex));
#if DEBUG
            if (await MainThread.InvokeOnMainThreadAsync(async () => await DisplayAlertAsync(Localized._Error, Localized.DraftPage_RenderFail((uint)_currentFrame, ex), "Throw", Localized._OK))) throw;
#else
            await MainThread.InvokeOnMainThreadAsync(async () => await DisplayAlertAsync(Localized._Error, Localized.DraftPage_RenderFail((uint)_currentFrame, ex), Localized._OK));
#endif
            return false;
        }
    }

    private async Task SwitchEditorLayoutAsync(string? mode)
    {
        bool fixedLayout = string.Equals(mode, "fixed", StringComparison.OrdinalIgnoreCase);
        if (fixedLayout == IsFixedLayout && _fixedLayoutInitialized) return;

        if (fixedLayout)
        {
            DetachViewFromParent(ClipEditorHost);
            DetachViewFromParent(LowerContent);
            DetachViewFromParent(AddClipView);

            FixedPreviewHost.Content = ClipEditorHost;
            FixedTimelineHost.Content = LowerContent;
            PlayingControlLayout.HorizontalOptions = LayoutOptions.Fill;
            PlayingControlLayout.Margin = new(4, 2, 0, 3);
            MainMultiWindowView.IsVisible = false;
            UpperContent.IsVisible = false;
            MainControlGrid.IsVisible = false;
            FixedLayoutRoot.IsVisible = true;
            IsFixedLayout = true;
            RebuildQuickCommandSlots();
            await RefreshFixedClipInfoAsync(_selectedClipIds.Count == 1 ? _selected : null);
        }
        else
        {
            DetachViewFromParent(AddClipView);
            FixedPreviewHost.Content = null;
            FixedTimelineHost.Content = null;
            FixedClipInfoHost.Content = null;
            if (AddClipSubwindow is not null && (_fixedLayoutInitialized || !_workspaceWindowHost.WasLayoutRestored))
            {
                _workspaceWindowHost?.OpenWindow("clips.add");
            }
            PreviewSubwindow.Content = ClipEditorHost;
            TimelineSubwindow.Content = LowerContent;
            AddClipHost.Content = AddClipView;
            PlayingControlLayout.HorizontalOptions = UseCompactLayout == true ? LayoutOptions.End : LayoutOptions.Fill;
            PlayingControlLayout.Margin = UseCompactLayout == true ? new(0, 0, 8, 0) : new(4, 2, 0, 3);
            FixedLayoutRoot.IsVisible = false;
            UpperContent.IsVisible = true;
            MainControlGrid.IsVisible = true;
            MainMultiWindowView.IsVisible = true;
            IsFixedLayout = false;
            _fixedClipInfoTabs = null;
            _fixedClipInfoPopupTabs = null;
        }

        _fixedLayoutInitialized = true;
        await Dispatcher.DispatchAsync(() => UpdatePlayheadPosition());
    }

    private static void DetachViewFromParent(View? view)
    {
        if (view?.Parent is ContentView contentView && contentView.Content == view)
        {
            contentView.Content = null;
        }
        else if (view?.Parent is Border border && border.Content == view)
        {
            border.Content = null;
        }
        else if (view?.Parent is Layout layout)
        {
            layout.Remove(view);
        }
    }

    private async Task RefreshFixedClipInfoAsync(ClipElementUI? clip)
    {
        if (!IsFixedLayout) return;
        if (popupShowingDirection != "none") await HidePopup(true);
        DetachViewFromParent(AddClipView);
        _fixedClipInfoPopupTabs = await infoBuilder.BuildFixed(clip, OnClipPropertiesChanged, AddClipView);
        _fixedClipInfoPopupTabs.HeaderRightContent = new Button
        {
            Text = MaterialIconClose,
            FontFamily = MaterialIconFontFamily,
            WidthRequest = 42,
            Command = new Command(async () => await HidePopup(true))
        };
        _fixedClipInfoTabs = new TabbedView { Background = Background };
        foreach (var item in _fixedClipInfoPopupTabs.TabItems)
        {
            _fixedClipInfoTabs.TabItems.Add(new TabbedViewItem
            {
                Header = item.Header,
                Tag = item.Tag,
                Content = new Grid { HeightRequest = 1 }
            });
        }
        _fixedClipInfoPopupTabs.OnTabSwitched += (_, selectedItem) =>
        {
            if (_syncingFixedClipInfoTabs) return;
            _syncingFixedClipInfoTabs = true;
            _fixedClipInfoTabs?.SelectByTag(selectedItem.Tag);
            _syncingFixedClipInfoTabs = false;
        };
        _fixedClipInfoTabs.OnTabSwitched += FixedClipInfoTabSwitched;
        FixedClipInfoHost.Content = _fixedClipInfoTabs;
        FixedClipInfoHost.IsVisible = true;
        FixedClipInfoHost.HeightRequest = 48;
    }

    private async void FixedClipInfoTabSwitched(object? sender, TabbedViewItem item)
    {
        if (_fixedClipInfoPopupTabs is null) return;
        _syncingFixedClipInfoTabs = true;
        _fixedClipInfoPopupTabs.SelectByTag(item.Tag);
        _syncingFixedClipInfoTabs = false;
        if (item.Tag == "add")
        {
            DetachViewFromParent(AddClipView);
            await ShowAPopup(content: AddClipView);
            return;
        }
        await ShowAPopup(content: _fixedClipInfoPopupTabs);
    }

    private async Task<ImageSource?> RenderOverviewImageSourceAsync()
    {
        if (AlreadyDisappeared || previewer.Clips is null) return null;

        var frame = (uint)Math.Clamp(_currentFrame, 0d, uint.MaxValue);
        var projectWidth = Math.Max(1, ProjectInfo.RelativeWidth);
        var projectHeight = Math.Max(1, ProjectInfo.RelativeHeight);
        var scale = (double)InitialStaticPreviewLongEdge / Math.Max(projectWidth, projectHeight);
        var width = Math.Max(1, (int)Math.Round(projectWidth * scale));
        var height = Math.Max(1, (int)Math.Round(projectHeight * scale));
        var content = await Task.Run(() => previewer.RenderFramePngBytes(frame, width, height));
        LogDiagnostic($"Rendered overview frame {frame} at {width}x{height}");
        return ImageSource.FromStream(() => new MemoryStream(content, writable: false));
    }

    private async Task RenderOneFrame(uint duration, int? width = null, int? height = null)
    {
#if DIAGHUB_ENABLE_TRACE_SYSTEM
        using var frameMark = new UserMarkRange("Preview.StaticFrame", $"frame={duration}");
#endif
        // Supersede any in-flight render BEFORE waiting on the lock so the current holder aborts and
        // releases it promptly, and surface feedback immediately so the status bar always shows that a
        // render was requested — even while we are still queued behind another render.
        var cts = new CancellationTokenSource();
        CancellationTokenSource? previousCts;
        lock (_renderCtsLock)
        {
            previousCts = _renderOneFrameCts;
            _renderOneFrameCts = cts;
        }
        previousCts?.Cancel();
        CancelDynamicPreview();

        SetStateBusy();
        SetStatusText(Localized.DraftPage_RenderOneFrame((int)duration, TimeSpan.FromSeconds(duration * SecondsPerFrame)));

#if DIAGHUB_ENABLE_TRACE_SYSTEM
        using (new UserMarkRange("Preview.WaitRenderLock"))
#endif
            await renderingLock.WaitAsync();
        try
        {
            // A newer render request arrived while we waited for the lock; drop this one quietly.
            if (cts.IsCancellationRequested) return;

            _currentFrame = duration;
            SyncClipEditorCurrentFrame();

            // Always keep a hard timeout (even in DEBUG) so a wedged render can never hold the lock
            // forever — otherwise every later playhead click would silently block on WaitAsync.
#if DEBUG
            cts.CancelAfter(60000);
#else
            cts.CancelAfter(10000);
#endif
            var targetWidth = width ?? previewWidth;
            var targetHeight = height ?? previewHeight;

            if (UseDynamicPreview)
            {
                if (DynamicPreviewProvider.Clips is null) return;
                await Dispatcher.DispatchAsync(() =>
                {
                    ClipEditor.SetRealtimePreviewContent(EnsureRealtimePreviewHost());
                    LivePreviewPlayer.IsVisible = false;
                    ClipEditor.SetStaticPreviewVisible(false);
                });

                if (!await RefreshDynamicPreviewOverlay())
                {
                    await RenderStaticPreviewProgressivelyAsync(duration, targetWidth, targetHeight, cts.Token);
                }
            }
            else
            {
                if (previewer.Clips is null) return;
                await Dispatcher.DispatchAsync(() =>
                {
                    ClipEditor.SetRealtimePreviewContent(EnsureRealtimePreviewHost());
                    LivePreviewPlayer.IsVisible = false;
                });

                await RenderStaticPreviewProgressivelyAsync(duration, targetWidth, targetHeight, cts.Token);
            }


            SetStateOK();
            SetStatusText(Localized.DraftPage_EverythingFine);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request → stay silent and let that request own the UI.
            // Only report a timeout when this render is still the active one.
            bool superseded;
            lock (_renderCtsLock)
            {
                superseded = !ReferenceEquals(_renderOneFrameCts, cts);
            }
            if (!superseded)
            {
                SetStateFail(Localized.DraftPage_RenderTimeout);
            }
        }
        catch (Exception ex)
        {
            Log(ex, "Render one frame", this);
            SetStateFail(Localized._ExceptionTemplate(ex));
#if DEBUG
            if (await MainThread.InvokeOnMainThreadAsync(async () => await DisplayAlertAsync(Localized._Error, Localized.DraftPage_RenderFail(duration, ex), "Throw", Localized._OK))) throw;
#else
            await MainThread.InvokeOnMainThreadAsync(async () => await DisplayAlertAsync(Localized._Error, Localized.DraftPage_RenderFail(duration, ex), Localized._OK));
#endif
        }
        finally
        {
            lock (_renderCtsLock)
            {
                if (ReferenceEquals(_renderOneFrameCts, cts))
                {
                    _renderOneFrameCts = null;
                }
            }
            cts.Dispose();
            renderingLock.Release();
        }
    }

    private async Task RenderStaticPreviewProgressivelyAsync(uint frameIndex, int targetWidth, int targetHeight, CancellationToken token)
    {
#if DIAGHUB_ENABLE_TRACE_SYSTEM
        using var progressiveMark = new UserMarkRange("Preview.StaticProgressive", $"frame={frameIndex}, size={targetWidth}x{targetHeight}");
#endif
        var sizes = BuildProgressiveStaticPreviewSizes(targetWidth, targetHeight);
        using var displayLock = new SemaphoreSlim(1, 1);
        using var renderThrottle = new SemaphoreSlim(MaxConcurrentStaticPreviewRenders, MaxConcurrentStaticPreviewRenders);
        var displayedTier = -1;

        async Task RenderTierAsync(int tier, bool isFinalTier)
        {
            var (width, height) = sizes[tier];
#if DIAGHUB_ENABLE_TRACE_SYSTEM
            using var tierMark = new UserMarkRange("Preview.StaticTier", $"frame={frameIndex}, tier={tier}, size={width}x{height}");
#endif
            try
            {
                await renderThrottle.WaitAsync(token);
                (PreviewFrameSource Frame, ImageSource? Source) content;
                try
                {
                    content = await Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        var rendered = previewer.RenderFrameForDisplay(frameIndex, width, height, token);
                        var source = rendered.TargetPixelFormat == PreviewPixelFormat.Rgba16FloatScRgb
                            ? null
                            : PreviewFrameMaterializer.CreateImageSource(rendered.VfdPath);
                        token.ThrowIfCancellationRequested();
                        return (rendered, source);
                    }, token);
                }
                finally
                {
                    renderThrottle.Release();
                }

                await displayLock.WaitAsync(token);
                try
                {
                    // Tiers complete out of order. Never let a late low-resolution render replace a
                    // sharper image that has already reached the preview surface.
                    if (tier <= displayedTier)
                    {
                        return;
                    }

                    token.ThrowIfCancellationRequested();
                    await Dispatcher.DispatchAsync(() =>
                    {
#if DIAGHUB_ENABLE_TRACE_SYSTEM
                        using var imageMark = new UserMarkRange("Preview.ApplyStaticImage", $"frame={frameIndex}, size={width}x{height}");
#endif
                        ClipEditor.SetStaticPreviewFrame(content.Frame, content.Source);
                    });
                    token.ThrowIfCancellationRequested();
                    displayedTier = tier;
                    await Dispatcher.DispatchAsync(() => ClipEditor.SetStaticPreviewVisible(true));
                }
                finally
                {
                    displayLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (!isFinalTier)
            {
                // A failed intermediate tier must not prevent the target-resolution render from
                // completing. The final tier still propagates its error to the normal UI handler.
                Log(ex, $"Render progressive static preview tier {width}x{height}", this);
            }
        }

        // Give the smallest render an uncontested head start. Once something is visible, render the
        // remaining quality levels in parallel; the display gate above keeps upgrades monotonic.
        await RenderTierAsync(0, sizes.Count == 1);
        if (sizes.Count == 1)
        {
            return;
        }

        var finalTier = sizes.Count - 1;
        var finalRender = RenderTierAsync(finalTier, true);
        var intermediateRenders = Enumerable.Range(1, Math.Max(0, finalTier - 1))
            .Select(tier => RenderTierAsync(tier, false));
        await Task.WhenAll(intermediateRenders.Append(finalRender));
    }

    private static List<(int Width, int Height)> BuildProgressiveStaticPreviewSizes(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var targetLongEdge = Math.Max(width, height);
        var sizes = new List<(int Width, int Height)>();

        for (var longEdge = Math.Min(InitialStaticPreviewLongEdge, targetLongEdge);
             longEdge < targetLongEdge;
             longEdge = Math.Min(targetLongEdge, longEdge * 2))
        {
            var scale = (double)longEdge / targetLongEdge;
            var tier = (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
            if (sizes.Count == 0 || sizes[^1] != tier)
            {
                sizes.Add(tier);
            }
        }

        var target = (width, height);
        if (sizes.Count == 0 || sizes[^1] != target)
        {
            sizes.Add(target);
        }

        return sizes;
    }

    CancellationTokenSource? _playbackCts;
    CancellationTokenSource? _movePlayheadDebounceCts;
    CancellationTokenSource? _nonRealtimePlayheadSyncCts;
    int _nonRealtimePlaybackChunkStartFrame = 0;
    bool isPlaying = false;
    bool playbackDone = false;
    private async void PlayPauseButton_Clicked(object sender, EventArgs e)
    {
        if ((UseCompactLayout ?? DeviceInfo.Idiom == DeviceIdiom.Phone) && _pendingClipPlacementFactory is not null)
        {
            CancelPendingClipPlacement();
            Dispatcher.Dispatch(() =>
            {
                SetPlayPauseIconToPlay();
                CurrentPlayheadLabel.IsVisible = true;
            });
            return;
        }

        isPlaying = !isPlaying;
        if (isPlaying)
        {
            SwitchPreviewEditorHost();
            SetPlayPauseIconToPause();
            LogDiagnostic("Start playing...");
            SetStateBusy();
            if (!_isLivePreviewPlayerEventsHooked)
            {
                LivePreviewPlayer.MediaEnded += (s, e) =>
                {
                    if (!isPlaying) return;
                    playbackDone = true;
                    try
                    {
                        if (_lastPlaybackPath is not null && File.Exists(_lastPlaybackPath))
                            File.Delete(_lastPlaybackPath);
                    }
                    catch { }
                };
                _isLivePreviewPlayerEventsHooked = true;
            }
            await Task.Run(PrepareLivePreview);

        }
        else
        {
            SetPlayPauseIconToPlay();
            LogDiagnostic("Pause playing.");
            await PauseLivePreview();
            SetStateOK();
        }

    }
    MediaElement LivePreviewPlayer = new();
    MediaElement RemotePreviewAudioPlayer = new();

    private uint GetContinuousAudioFrame()
    {
        if (_continuousAudioStopwatch == null)
            return (uint)_currentFrame;
        double fps = Math.Max(1d, ProjectInfo.TargetFrameRate);
        return (uint)_continuousAudioStartFrame + (uint)Math.Round(_continuousAudioStopwatch.Elapsed.TotalSeconds * fps);
    }

    private void StopNonRealtimePlayheadSyncLoop()
    {
        try
        {
            _nonRealtimePlayheadSyncCts?.Cancel();
            _nonRealtimePlayheadSyncCts?.Dispose();
        }
        catch { }
        finally
        {
            _nonRealtimePlayheadSyncCts = null;
        }
    }

    private void StartContinuousPlayheadSyncLoop(int chunkStartFrame)
    {
        _nonRealtimePlaybackChunkStartFrame = Math.Max(0, chunkStartFrame);
        if (_nonRealtimePlayheadSyncCts is not null)
            return;

        _nonRealtimePlayheadSyncCts = new CancellationTokenSource();
        var token = _nonRealtimePlayheadSyncCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (!isPlaying || UseDynamicPreview)
                    {
                        await Task.Delay(100, token);
                        continue;
                    }

                    int frame = (int)GetContinuousAudioFrame();

                    await Dispatcher.DispatchAsync(() =>
                    {
                        _currentFrame = frame;
                        SyncClipEditorCurrentFrame();
                        UpdatePlayheadPosition();
                        CurrentPlayheadLabel.Text = $"{TimeSpan.FromSeconds(_currentFrame * SecondsPerFrame):mm\\:ss\\.ff} / {TimeSpan.FromSeconds(ProjectDuration * SecondsPerFrame):mm\\:ss\\.ff}";
                    });

                    await Task.Delay(40, token);
                }
            }
            catch (OperationCanceledException)
            {
                // expected on pause/stop
            }
        }, token);
    }

    private Grid EnsureRealtimePreviewHost()
    {
        if (_livePreviewRealtimeHost is not null)
        {
            return _livePreviewRealtimeHost;
        }

        // The MediaElement is a child of the LivePreviewerHost (ContentView with Fill/Fill),
        // which itself fills the InteractableEditor. Without explicit Fill/Fill + AspectFit
        // here, the MediaElement falls back to its natural (video-source) size, which is
        // 1920x1080 or larger. The editor is only ~248px tall, so the unscaled MediaElement
        // overflows downward and visually dominates the editor — making every overlay
        // coordinate (renderRect, selection handles, debug overlay) appear to be misaligned
        // with the visible video, because the visible video is not at the editor's
        // coordinate scale. Constrain it to the host and use AspectFit so the live frame
        // letterboxes identically to the static PreviewOverlayImage.
        LivePreviewPlayer.Aspect = Aspect.AspectFit;
        LivePreviewPlayer.HorizontalOptions = LayoutOptions.Fill;
        LivePreviewPlayer.VerticalOptions = LayoutOptions.Fill;

        var host = new Grid();
        host.Children.Add(LivePreviewPlayer);
        _livePreviewRealtimeHost = host;
        return host;
    }

    private async Task PrepareLivePreview()
    {
        StopNonRealtimePlayheadSyncLoop();
        Dispatcher.Dispatch(() => ClearSelectionInternal());
        SetStateBusy(Localized._Processing);
        if (_playbackCts != null)
        {
            _playbackCts.Cancel();
            _playbackCts.Dispose();
        }
        _playbackCts = new CancellationTokenSource();
        var token = _playbackCts.Token;

        try
        {
            _playbackStartFrame = _currentFrame;
            _nextPlaybackPath = null;
            var continuousAudioReady = false;
            var useWorkerAudio = UseDynamicPreview && !IsRemoteProject;
            _continuousAudioStartFrame = _currentFrame;
            _correctedDynamicPlaybackFrame = -1;
            _previewAudioGeneration = 0;
            _previewAudioClockFailed = false;

            if (useWorkerAudio)
            {
                if (previewer.HasAudioSources())
                {
                    SetStatusText(Localized.DraftPage_LivePreview_Preparing);
                    try
                    {
                        var clock = await previewer.StartPreviewAudioAsync((uint)_currentFrame, (int)ProjectInfo.TargetFrameRate, token);
                        _previewAudioGeneration = clock.Generation;
                        continuousAudioReady = clock.HasAudio;
                        LogDiagnostic($"Started Worker preview audio generation {clock.Generation} at frame {_currentFrame}.");
                    }
                    catch (Exception ex)
                    {
                        Log(ex, "Start Worker preview audio; video preview will continue without audio", this);
                    }
                }
            }
            else
            {
                await previewer.ResetAudioPlaybackSources();

                int totalFrames = (int)Math.Max(previewer.TotalDuration, ProjectDuration);
                int remainingFrames = totalFrames - (int)_currentFrame;
                if (previewer.HasAudioSources() && remainingFrames > 0)
                {
                    SetStatusText(Localized.DraftPage_LivePreview_Preparing);
                    _fullContinuousAudioPath = await previewer.RenderSomeAudio(
                        (int)_currentFrame, remainingFrames,
                        (int)ProjectInfo.TargetFrameRate, token);

                    if (_fullContinuousAudioPath != null && File.Exists(_fullContinuousAudioPath))
                    {
                        var audioOpened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        await Dispatcher.DispatchAsync(() =>
                        {
                            try
                            {
                                RemotePreviewAudioPlayer.Stop();
                                RemotePreviewAudioPlayer.Source = null;
                                ComputeView.Children.Remove(RemotePreviewAudioPlayer);
                            }
                            catch { }

                            RemotePreviewAudioPlayer = new MediaElement
                            {
                                ShouldAutoPlay = false,
                                ShouldKeepScreenOn = false,
                                ShouldLoopPlayback = false,
                                ShouldShowPlaybackControls = false,
                                InputTransparent = true,
                                WidthRequest = 1,
                                HeightRequest = 1,
                                Opacity = 0.01,
                            };
                            RemotePreviewAudioPlayer.MediaOpened += (_, _) => audioOpened.TrySetResult(true);
                            RemotePreviewAudioPlayer.MediaFailed += (_, args) =>
                            {
                                Log($"Failed to open the live-preview audio source: {args.ErrorMessage}", "warn");
                                audioOpened.TrySetResult(false);
                            };
                            RemotePreviewAudioPlayer.MediaEnded += (s, e) =>
                            {
                                try { if (s is MediaElement endedPlayer) ComputeView.Children.Remove(endedPlayer); }
                                catch { }
                            };
                            ComputeView.Add(RemotePreviewAudioPlayer);
                            RemotePreviewAudioPlayer.Source = MediaSource.FromFile(_fullContinuousAudioPath);
                        });

                        try { continuousAudioReady = await audioOpened.Task.WaitAsync(TimeSpan.FromSeconds(15), token); }
                        catch (TimeoutException) { Log("Timed out waiting for the live-preview audio source to open.", "warn"); }

                        if (!continuousAudioReady)
                            Log("The live-preview audio source could not be opened; video preview will continue without audio.", "warn");
                    }
                }
            }

            if (UseDynamicPreview)
            {
                SetStateBusy();
                await Dispatcher.DispatchAsync(() =>
                {
                    var host = EnsureRealtimePreviewHost();
                    ClipEditor.SetRealtimePreviewContent(host);
                    ClipEditor.SetStaticPreviewVisible(false);
                    LivePreviewPlayer.IsVisible = false;
                });

                await Dispatcher.DispatchAsync(() =>
                {
                    if (continuousAudioReady && !useWorkerAudio)
                    {
                        RemotePreviewAudioPlayer.Play();
                    }
                    _continuousAudioStopwatch = Stopwatch.StartNew();
                });

                await RenderSomeFramesDynamicSynced((int)_currentFrame, token);
                return;
            }
            else
            {
                await Dispatcher.DispatchAsync(() =>
                {
                    ClipEditor.SetRealtimePreviewContent(EnsureRealtimePreviewHost());
                    LivePreviewPlayer.IsVisible = true;
                    LivePreviewPlayer.Opacity = 1;
                    LivePreviewPlayer.HeightRequest = -1;
                    LivePreviewPlayer.WidthRequest = -1;
                    LivePreviewPlayer.InputTransparent = false;
                    LivePreviewPlayer.ShouldShowPlaybackControls = false;
                    ClipEditor.SetStaticPreviewVisible(false);
                });


                var path = await RenderSomeFrames((int)_currentFrame, token, includeAudio: false);
                var currentStartFrame = (int)_currentFrame;
                Dispatcher.Dispatch(() =>
                {
                    _nonRealtimePlaybackChunkStartFrame = currentStartFrame;
                    LivePreviewPlayer.Source = MediaSource.FromFile(path);
                    LivePreviewPlayer.Play();
                    _continuousAudioStopwatch = Stopwatch.StartNew();
                    if (continuousAudioReady)
                        RemotePreviewAudioPlayer.Play();
                });
                StartContinuousPlayheadSyncLoop(currentStartFrame);

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var nextStart = currentStartFrame + LiveVideoPreviewBufferLength;
                        if (nextStart > Math.Max(previewer.TotalDuration, ProjectDuration))
                        {
                            _playbackCts.Cancel();
                            break;
                        }

                        LogDiagnostic($"Start continue Render from {nextStart}...");

                        _nextPlaybackPath = await RenderSomeFrames(nextStart, _playbackCts.Token, includeAudio: false);
                        LogDiagnostic($"Next preview is ready. Path:{_nextPlaybackPath}");
                        while (!playbackDone && !token.IsCancellationRequested) await Task.Delay(100, token);
                        LogDiagnostic("Previewer is ready!");
                        playbackDone = false;
                        Dispatcher.Dispatch(() =>
                        {
                            _nonRealtimePlaybackChunkStartFrame = nextStart;
                            LivePreviewPlayer.Stop();
                            LivePreviewPlayer.Source = null;
                            LivePreviewPlayer.Source = MediaSource.FromFile(_nextPlaybackPath);
                            _lastPlaybackPath = _nextPlaybackPath;
                            LivePreviewPlayer.ShouldAutoPlay = true;
                            LivePreviewPlayer.Play();
                        });
                        currentStartFrame += LiveVideoPreviewBufferLength;
                    }
                    catch (Exception ex)
                    {
                        Log(ex, "PreRender", this);
                    }
                    finally
                    {
                        _isPreRendering = false;

                    }
                }
            }

        }
        catch (OperationCanceledException)
        {
            // Stopped
        }
        catch (Exception ex)
        {
            Log(ex, "LivePreview", this);
            isPlaying = false;
            await PauseLivePreview();
            await MainThread.InvokeOnMainThreadAsync(() => SetPlayPauseIconToPlay());
#if DEBUG
            if (await MainThread.InvokeOnMainThreadAsync(async () => await DisplayAlertAsync(Localized._Error, Localized.DraftPage_RenderFail((uint)_currentFrame, ex), "Throw", Localized._OK))) throw;
#else
            await MainThread.InvokeOnMainThreadAsync(async () => await DisplayAlertAsync(Localized._Error, Localized.DraftPage_RenderFail((uint)_currentFrame, ex), Localized._OK));
#endif
        }
    }

    private async Task PauseLivePreview()
    {
        isPlaying = false;
        StopNonRealtimePlayheadSyncLoop();
        try
        {
            _playbackCts?.Cancel();
            _playbackCts?.Dispose();
        }
        catch { }
        finally
        {
            _playbackCts = null;
        }

        _continuousAudioStopwatch?.Stop();
        _continuousAudioStopwatch = null;

        if (_previewAudioGeneration != 0 && !IsRemoteProject)
        {
            try { await previewer.StopPreviewAudioAsync(); }
            catch (Exception ex) { Log(ex, "Stop Worker preview audio", this); }
            _previewAudioGeneration = 0;
            _correctedDynamicPlaybackFrame = -1;
            _previewAudioClockFailed = false;
        }

        await Dispatcher.DispatchAsync(() =>
        {
            SwitchPreviewEditorHost();
            try
            {
                LivePreviewPlayer.Stop();
                RemotePreviewAudioPlayer.Stop();
                LivePreviewPlayer.Source = null;
                RemotePreviewAudioPlayer.Source = null;
            }
            catch { }
            LivePreviewPlayer.IsVisible = false;
            // Keep preview elements inside a single host to avoid WinUI re-parent exceptions.
            ClipEditor.SetRealtimePreviewContent(EnsureRealtimePreviewHost());
            ClipEditor.SetStaticPreviewVisible(true);
            SetPlayPauseIconToPlay();
        });

        await RefreshPreviewFromCurrentProviderAsync();
        _nextPlaybackPath = null;
        _isPreRendering = false;

        try
        {
            if (!string.IsNullOrWhiteSpace(_fullContinuousAudioPath) && File.Exists(_fullContinuousAudioPath))
            {
                File.Delete(_fullContinuousAudioPath);
            }
        }
        catch { }
        _fullContinuousAudioPath = null;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                if (SettingsManager.IsBoolSettingTrueOrDefault("render_enableLivePreviewThreadAffinity", true))
                {
#if WINDOWS || LINUX || (Avalonia && !(ANDROID || IOS || MACOS))
                    Process.GetCurrentProcess().Refresh();
                    Process.GetCurrentProcess().ProcessorAffinity = (nint)(Math.Pow(2, Environment.ProcessorCount) - 1);
#else
                    ThreadAffinityHelper.SetCurrentThreadAffinity(Enumerable.Range(0, Environment.ProcessorCount).ToArray());
#endif
                }
            }
            catch { }
        });
    }

    private async Task<string> RenderSomeFrames(int startPoint, CancellationToken ct, bool includeAudio = true)
    {
        Stopwatch cd = Stopwatch.StartNew();
        void progChanged(double p, TimeSpan _)
        {
            if (cd.ElapsedMilliseconds < 500) return;
            cd.Restart();
            SetStatusText(Localized._ProcessingWithProg(p));
        }

        if (UseDynamicPreview)
        {
            throw new InvalidOperationException("Use RenderSomeFramesDynamicSynced instead.");
        }

        previewer.OnProgressChanged += progChanged;
        try
        {
            return await previewer.RenderSomeFrames(
                startPoint,
                LiveVideoPreviewBufferLength,
                (int)(previewWidth / LivePreviewResolutionFactor),
                (int)ProjectInfo.TargetFrameRate,
                (int)(previewHeight / LivePreviewResolutionFactor),
                ct,
                includeAudio);
        }
        finally
        {
            previewer.OnProgressChanged -= progChanged;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private async Task RenderSomeFramesDynamicSynced(int startPoint, CancellationToken ct)
    {
#if DIAGHUB_ENABLE_TRACE_SYSTEM
        using var playbackMark = new UserMarkRange("Preview.DynamicPlayback", $"startFrame={startPoint}");
#endif
        uint lastRenderedFrame = 0, targetFrame = (uint)startPoint;
        bool renderedAnyFrame = false;
        var totalDisplay = TimeSpan.FromSeconds(ProjectDuration * SecondsPerFrame).ToString("mm\\:ss");
        var developerMode = SettingsManager.IsBoolSettingTrue("DeveloperMode");
        int maxFrame = (int)Math.Max(previewer.TotalDuration, ProjectDuration);
        double minUiIntervalMs = ProjectInfo.TargetFrameRate > 0 ? 1000.0 / ProjectInfo.TargetFrameRate : 1000.0 / 30.0;
        DateTime now = DateTime.UtcNow;
        TimeSpan timeSinceLastUi = TimeSpan.MinValue;
        Queue<long> appliedFrameTimes = new();
        Task? pendingUiUpdate = null;
        IReadOnlyList<projectFrameCut.ApplicationAPIBase.Interaction.PreparedPreview>? preparedPreviews = null!;
        var projectWidth = Math.Max(1, ProjectInfo.RelativeWidth);
        var projectHeight = Math.Max(1, ProjectInfo.RelativeHeight);
        var (canvasWidth, canvasHeight) = DynamicPreviewProvider.ResolveDimensions(projectWidth, projectHeight, ClipEditor.Width, ClipEditor.Height);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                int[] CPUAffinityOverride = Array.Empty<int>();
                if (SettingsManager.IsBoolSettingTrueOrDefault("render_enableLivePreviewThreadAffinity", true))
                {
                    try
                    {
                        if (SettingsManager.IsSettingExists("render_coreAffinityOverride") && !string.IsNullOrWhiteSpace(SettingsManager.GetSetting("render_coreAffinityOverride", "")))
                        {
                            CPUAffinityOverride = SettingsManager.GetSetting("render_coreAffinityOverride", "0").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(c => uint.TryParse(c, out _)).Select(int.Parse).ToArray();
                        }
                        else
                        {
                            try
                            {
                                var group = ThreadAffinityHelper.GetCpuCoreGroups();
                                var bigGroup = group.OrderBy(c => c.MaxFrequencyKHz ?? 0 + c.Capacity ?? 0 + c.EfficiencyClass ?? 0).Last();
                                CPUAffinityOverride = bigGroup.CpuIndexes.ToArray();

                            }
                            catch { }
                        }

                        if (CPUAffinityOverride.Length > 0)
                        {
#if WINDOWS || LINUX || (Avalonia && !(ANDROID || IOS || MACOS))
                            Process.GetCurrentProcess().Refresh();
                            Process.GetCurrentProcess().ProcessorAffinity = (IntPtr)CPUAffinityOverride.Aggregate<int, nint>(0, (mask, c) => mask | (1 << c));
#else
                            ThreadAffinityHelper.SetCurrentThreadAffinity(CPUAffinityOverride);
#endif
                            LogDiagnostic($"Set live preview thread affinity to cores {string.Join(", ", CPUAffinityOverride)}");
                        }
                    }
                    catch { }
                }
            }
            catch { }
        });

        SetStateBusy(Localized._Processing);

        while (!ct.IsCancellationRequested)
        {
            preparedPreviews = null!;
            var clockStartedAt = Stopwatch.GetTimestamp();
            targetFrame = await GetDynamicPreviewFrameAsync(ct);
            var clockElapsedMs = Stopwatch.GetElapsedTime(clockStartedAt).TotalMilliseconds;
            if (targetFrame > maxFrame) break;
            if (renderedAnyFrame && targetFrame <= lastRenderedFrame)
            {
                await Task.Delay(1, ct);
                continue;
            }

            _currentFrame = targetFrame;

            using var frameCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            frameCts.CancelAfter(DynamicPreviewTimeout);

            try
            {
                var prepareStartedAt = Stopwatch.GetTimestamp();
                preparedPreviews = await DynamicPreviewProvider.PrepareRequestsAsync(
                    DynamicPreviewProvider.ResolveRequests(DynamicPreviewProvider.Clips, targetFrame, projectWidth, projectHeight),
                    canvasWidth, canvasHeight,
                    projectWidth, projectHeight,
                    targetFrame,
                    true, false,
                    0,
                    frameCts.Token).ConfigureAwait(false);
                var prepareElapsedMs = Stopwatch.GetElapsedTime(prepareStartedAt).TotalMilliseconds;
                if (preparedPreviews is null) continue;
                lastRenderedFrame = targetFrame;
                renderedAnyFrame = true;

                now = DateTime.UtcNow;

                // Skip UI update if no previews to render
                if (preparedPreviews.Count == 0)
                {
                    var sinceLast = now - _lastDynamicPreviewUIUpdate;
                    if (sinceLast.TotalMilliseconds < 30)
                    {
                        await Task.Delay(15, ct);
                        continue;
                    }
                }

                // Throttle UI updates based on target frame rate
                timeSinceLastUi = now - _lastDynamicPreviewUIUpdate;
                if (timeSinceLastUi.TotalMilliseconds < minUiIntervalMs)
                {
                    await Task.Delay((int)(minUiIntervalMs - timeSinceLastUi.TotalMilliseconds), ct);
                }

                // Backpressure: if the previous UI update hasn't been applied yet, skip this frame
                // to prevent the dispatcher queue from growing unboundedly.
                if (pendingUiUpdate is { IsCompleted: false })
                {
                    //LogDiagnostic($"Frame {targetFrame} skipped — UI still applying previous frame");
                    continue;
                }

                // Dispatch UI update and track it for backpressure
                var frameForThisUpdate = targetFrame;
                var previewsForThisFrame = preparedPreviews;
                var queuedAt = Stopwatch.GetTimestamp();
                pendingUiUpdate = Dispatcher.DispatchAsync([MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)] async () =>
                {
#if DIAGHUB_ENABLE_TRACE_SYSTEM
                    using var uiMark = new UserMarkRange("Preview.PlaybackUIUpdate", $"frame={frameForThisUpdate}");
#endif
                    var dispatchStartedAt = Stopwatch.GetTimestamp();
                    try
                    {
                        UpdatePlayheadPosition(TimelineScrollView.ScrollX);

                        // Auto-scroll timeline to keep playhead visible during playback
                        var timeX = FrameToPixel(frameForThisUpdate);
                        var scrollX = TimelineScrollView.ScrollX;
                        var viewportWidth = TimelineScrollView.Width;
                        if (viewportWidth > 0)
                        {
                            double margin = 50;
                            if (timeX < scrollX + margin)
                            {
                                await TimelineScrollView.ScrollToAsync(Math.Max(0, timeX - margin), 0, false);
                                UpdatePlayheadPosition(TimelineScrollView.ScrollX);
                            }
                            else if (timeX > scrollX + viewportWidth - margin)
                            {
                                await TimelineScrollView.ScrollToAsync(timeX - viewportWidth + margin, 0, false);
                                UpdatePlayheadPosition(TimelineScrollView.ScrollX);
                            }
                        }

                        CurrentPlayheadLabel.Text = $"{TimeSpan.FromSeconds(frameForThisUpdate * SecondsPerFrame):mm\\:ss\\.ff} / {totalDisplay}";
                        var applyStartedAt = Stopwatch.GetTimestamp();
                        FastPreviewEditor.SetCurrentFrame(frameForThisUpdate);
                        ActivePreviewEditor.ApplyPreparedPreviews(previewsForThisFrame);
                        var appliedAt = Stopwatch.GetTimestamp();
                        appliedFrameTimes.Enqueue(appliedAt);
                        var cutoff = appliedAt - Stopwatch.Frequency;
                        while (appliedFrameTimes.Count > 2 && appliedFrameTimes.Peek() < cutoff)
                            appliedFrameTimes.Dequeue();

                        double fps = 0;
                        if (appliedFrameTimes.Count > 1)
                        {
                            var elapsed = Stopwatch.GetElapsedTime(appliedFrameTimes.Peek(), appliedAt).TotalSeconds;
                            fps = (appliedFrameTimes.Count - 1) / Math.Max(0.001, elapsed);
                        }

                        var rate = fps <= 0
                            ? "-- FPS"
                            : fps < 1.5
                                ? Localized.DraftPage_LivePreview_SecondPerFrame(1 / fps)
                                : $"{fps:F1} FPS";
                        if (developerMode)
                        {
                            var queueElapsedMs = Stopwatch.GetElapsedTime(queuedAt, dispatchStartedAt).TotalMilliseconds;
                            var applyElapsedMs = Stopwatch.GetElapsedTime(applyStartedAt, appliedAt).TotalMilliseconds;
                            AlternativeStatusLabel.Text = $"{rate} ({clockElapsedMs:F1} ms clock + {prepareElapsedMs:F1} ms prep + {queueElapsedMs:F1} ms queue + {applyElapsedMs:F1} ms apply)";
                        }
                        else
                        {
                            AlternativeStatusLabel.Text = rate;
                        }

                    }
                    catch (Exception ex)
                    {
                        Log(ex, $"Apply prepared previews for frame {frameForThisUpdate}", this);
#if DEBUG
                        if (await MainThread.InvokeOnMainThreadAsync(async () => await DisplayAlertAsync(Localized._Error, Localized.DraftPage_RenderFail(frameForThisUpdate, ex), "Throw", Localized._OK))) throw;
#else
                        await MainThread.InvokeOnMainThreadAsync(async () => await DisplayAlertAsync(Localized._Error, Localized.DraftPage_RenderFail(frameForThisUpdate, ex), Localized._OK));
#endif
                        await PauseLivePreview();
                        await MainThread.InvokeOnMainThreadAsync(() => SetPlayPauseIconToPlay());
                    }
                    finally
                    {
                        _lastDynamicPreviewUIUpdate = DateTime.UtcNow;
                    }
                });
            }
            catch (TaskCanceledException)
            {
                LogDiagnostic($"Frame {targetFrame} cancelled");
            }
            catch (OperationCanceledException)
            {
                LogDiagnostic($"Frame {targetFrame} cancelled");
            }
            catch (Exception ex)
            {
                Log(ex, $"Prepare requests for frame {targetFrame}", this);
                throw;
            }

        }

        await PauseLivePreview();
        SetStateOK(Localized.DraftPage_EverythingFine);

    }

    private async ValueTask<uint> GetDynamicPreviewFrameAsync(CancellationToken token)
    {
        var fallback = GetContinuousAudioFrame();
        if (_previewAudioGeneration == 0 || IsRemoteProject) return fallback;
        try
        {
            var clock = await previewer.GetPreviewAudioClockAsync(_previewAudioGeneration, token);
            if (clock.Generation != _previewAudioGeneration || !clock.HasAudio) return fallback;
            if (!clock.IsRunning && clock.BufferedSamples == 0 && clock.PlayedSamples == 0) return clock.StartFrame;
            _previewAudioClockFailed = false;
            return DynamicPreviewProvider.ResolvePlaybackFrame(clock, ProjectInfo.TargetFrameRate, fallback, ref _correctedDynamicPlaybackFrame);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (!_previewAudioClockFailed)
            {
                _previewAudioClockFailed = true;
                Log(ex, "Read Worker preview audio clock; using the local playback clock", this);
            }
            return fallback;
        }
    }

    private static View CreatePropertiesPlaceholder(string text)
    => new Label
    {
        Text = text,
        TextColor = Colors.White,
        HorizontalOptions = LayoutOptions.Center,
        VerticalOptions = LayoutOptions.Center,
        Opacity = 0.85,
        Margin = new Thickness(12)
    };
    #endregion
}
