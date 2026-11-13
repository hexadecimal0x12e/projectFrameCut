using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices;
using projectFrameCut.ApplicationAPIBase.Project;
using projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;
using static projectFrameCut.Shared.Logger;

#if WINDOWS
using Microsoft.UI.Windowing;
#endif

namespace projectFrameCut;

public partial class DraftPage
{
    #region multiform
    private readonly SemaphoreSlim _formSwitchLock = new(1, 1);
    private readonly List<MenuFlyoutItem> _workspaceWindowMenuItems = [];
    private DraftPageForm? _currentForm;
    private bool _formInitialized;
    private MultiWindowItem? _mobilePanelWindow;
    private View? _mobilePanelContent;

    public static DraftPageForm DefaultForm => DeviceInfo.Idiom == DeviceIdiom.Phone
        ? DraftPageForm.Mobile : DraftPageForm.Workspace;

    public DraftPageForm CurrentForm => _currentForm ?? (UseCompactLayout switch
    {
        true => DraftPageForm.Mobile,
        false => DraftPageForm.Workspace,
        _ => DefaultForm
    });

    public bool IsWorkspaceForm => CurrentForm == DraftPageForm.Workspace;
    public bool IsMobileForm => CurrentForm == DraftPageForm.Mobile;
    public bool IsPreviewForm => CurrentForm == DraftPageForm.Preview;
    public event EventHandler? FormChanged;

    public Task SwitchFormAsync(DraftPageForm form)
    {
        if (!Enum.IsDefined(form)) throw new ArgumentOutOfRangeException(nameof(form), form, null);
        return MainThread.InvokeOnMainThreadAsync(async () =>
        {
            await _formSwitchLock.WaitAsync();
            try
            {
                if (AlreadyDisappeared) throw new InvalidOperationException("The draft page has already been closed.");
                if (form != DraftPageForm.Preview && IsPreviewForm) form = GetAdaptiveForm();
#if !ANDROID && !IOS
                if (form != DraftPageForm.Preview) form = DraftPageForm.Workspace;
#endif
                if (_formInitialized && CurrentForm == form) return;
                await ApplyFormAsync(form);
            }
            catch (Exception ex)
            {
                Log(ex, $"Switch draft page form to {form}", this);
                throw;
            }
            finally
            {
                _formSwitchLock.Release();
            }
        });
    }

    private async Task ApplyFormAsync(DraftPageForm form)
    {
        var previous = CurrentForm;
        if (IsPreviewForm && form != DraftPageForm.Preview && _previewFormFullScreen)
            SetPreviewFormFullScreen(false);
        if (IsPreviewForm || form == DraftPageForm.Preview)
        {
            _previewFormSeekCts?.Cancel();
            _movePlayheadDebounceCts?.Cancel();
            _previewFormDragging = false;
            _resumePreviewFormPlayback = false;
            lock (_renderCtsLock) _renderOneFrameCts?.Cancel();
            CancelDynamicPreview();
            await _previewFormSeekLock.WaitAsync();
            _previewFormSeekLock.Release();
            if (isPlaying || previewer.HasDynamicPreview) await PauseLivePreview(refreshPreview: false);
        }
        if (_workspaceWindowHost is not null)
        {
            await HidePopup(true);
            if (_pendingClipPlacementFactory is not null || IsClipMoving) CancelPendingClipPlacement();
            if (form != DraftPageForm.Workspace)
            {
                foreach (var window in MainMultiWindowView.StandaloneWindows) window.Dock();
                if (_formInitialized && IsWorkspaceForm) _workspaceWindowHost.SaveLayout();
            }
        }

        Interlocked.Increment(ref _selectionUiVersion);
        _fixedClipInfoVersion++;
        _currentForm = form;
        UseCompactLayout = IsMobileForm;
        if (_workspaceWindowHost is not null)
        {
            DetachViewFromParent(AddClipView);
            if (IsMobileForm)
            {
                PreviewSubwindow.Content = null;
                TimelineSubwindow.Content = null;
                DetachViewFromParent(ClipEditorHost);
                DetachViewFromParent(LowerContent);
                FixedPreviewHost.Content = ClipEditorHost;
                FixedTimelineHost.Content = LowerContent;
                if (!ToolbarItems.Contains(CommandPanelToolbarItem)) ToolbarItems.Add(CommandPanelToolbarItem);
            }
            else
            {
                FixedPreviewHost.Content = null;
                FixedTimelineHost.Content = null;
                FixedClipInfoHost.Content = null;
                FixedClipInfoHost.IsVisible = false;
                _fixedClipInfoTabs = null;
                _fixedClipInfoPopupTabs = null;
                if (IsWorkspaceForm)
                {
                    PreviewSubwindow.Content = ClipEditorHost;
                    TimelineSubwindow.Content = LowerContent;
                    AddClipHost.Content = AddClipView;
                    AddClipView.IsVisible = true;
                }
                else
                {
                    PreviewSubwindow.Content = null;
                    TimelineSubwindow.Content = null;
                }
                ToolbarItems.Remove(CommandPanelToolbarItem);
            }
            PlayingControlLayout.HorizontalOptions = LayoutOptions.Fill;
            PlayingControlLayout.Margin = new(4, 2, 0, 3);
            MainMultiWindowView.IsVisible = IsWorkspaceForm;
            _formInitialized = true;
        }

        SwitchPreviewEditorHost();
        ApplyClipEditorPreviewOverlayMode();
        UpdatePreviewFormPlaybackControls();
        if (IsPreviewForm) UpdatePreviewFormFullScreenButton();
        foreach (var property in new[] { nameof(CurrentForm), nameof(IsWorkspaceForm), nameof(IsMobileForm), nameof(IsPreviewForm), nameof(IsFixedLayout),
            nameof(UseCompactLayout), nameof(UnNullUseCompactLayout), nameof(_ShouldShowClipMoveControlInCenterInfoBar), nameof(_ShouldShowCenterCompactControlGrid) })
            OnPropertyChanged(property);

        if (_formInitialized)
        {
            RefreshWorkspaceWindowMenu();
            UpdateTrackHeaderLayoutForViewport();
            ApplyDefaultMainMultiWindowLayout();
            await RefreshSelectionUiAsync(refreshPreview: false);
            await Dispatcher.DispatchAsync(() =>
            {
                SyncPreviewSurfaceSize();
                UpdateTimelineWidth();
                UpdatePlayheadPosition();
            });
            if (IsPreviewForm || previous == DraftPageForm.Preview)
                await RefreshPreviewFromCurrentProviderAsync();
        }
        LogDiagnostic($"Applied draft page form: {form}.");
        if (previous != form) FormChanged?.Invoke(this, EventArgs.Empty);
    }

    private void WorkspaceWindowsChanged(object? sender, MultiWindowItem window) => RefreshWorkspaceWindowMenu();

    private void RefreshWorkspaceWindowMenu()
    {
        foreach (var item in _workspaceWindowMenuItems) ViewMenuBarItem.Remove(item);
        _workspaceWindowMenuItems.Clear();
        if (IsWorkspaceForm)
        {
            foreach (var window in MainMultiWindowView.Windows)
            {
                var item = new MenuFlyoutItem
                {
                    Text = window.Title,
                    IconImageSource = new FontImageSource { FontFamily = MaterialIconFontFamily, Glyph = "\uf71b" },
                    Command = ManageWindowCommand,
                    CommandParameter = window.WindowID.ToString()
                };
                _workspaceWindowMenuItems.Add(item);
                ViewMenuBarItem.Insert(0, item);
            }
        }
        RefreshQuickCommands();
    }

    private async Task OpenFormWindowAsync(MultiWindowItem window)
    {
        if (IsWorkspaceForm)
        {
            ActivateMultiWindowItem(window);
            return;
        }
        if (window == PreviewSubwindow || window == TimelineSubwindow) return;
        await HidePopup(true);
        if (window.Content is not View content) return;
        _mobilePanelWindow = window;
        _mobilePanelContent = content;
        window.Content = null;
        DetachViewFromParent(content);
        await ShowAPopup(new ContentView { Content = content }, disableScrollWrapping: true);
    }

    private void RestoreMobilePanelContent()
    {
        if (_mobilePanelWindow is null || _mobilePanelContent is null) return;
        DetachViewFromParent(_mobilePanelContent);
        _mobilePanelWindow.Content = _mobilePanelContent;
        _mobilePanelWindow = null;
        _mobilePanelContent = null;
    }

    private async Task SelectFormAsync()
    {
        var selected = await DisplayActionSheetAsync($"Form: {CurrentForm}", Localized._Cancel, null, Enum.GetNames<DraftPageForm>());
        if (Enum.TryParse<DraftPageForm>(selected, out var form)) await SwitchFormAsync(form);
    }
    #endregion

    #region preview form
    private readonly SemaphoreSlim _previewFormSeekLock = new(1, 1);
    private CancellationTokenSource? _previewFormSeekCts;
    private bool _updatingPreviewFormProgress;
    private bool _previewFormDragging;
    private bool _resumePreviewFormPlayback;
    private bool _previewFormFullScreen;
#if WINDOWS
    private AppWindowPresenter? _previewFormWindowPresenter;
#elif LINUX
    private Gtk.EventControllerKey? _previewFormFullScreenKeys;
#endif

    public bool SupportsPreviewFullScreen => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    private bool UseDynamicPreviewForCurrentForm => IsPreviewForm || UseDynamicPreview;

    private async void PreviewFormExitButton_Clicked(object? sender, EventArgs e)
    {
        if (!IsPreviewForm) return;
        try
        {
            await SwitchFormAsync(GetAdaptiveForm());
        }
        catch (Exception ex)
        {
            Log(ex, "Exit preview form", this);
        }
    }

    private void PreviewFormFullScreenButton_Clicked(object? sender, EventArgs e)
    {
        if (IsPreviewForm) SetPreviewFormFullScreen(!_previewFormFullScreen);
    }

    private void SetPreviewFormFullScreen(bool fullScreen)
    {
#if WINDOWS || LINUX
        try
        {
#if WINDOWS
            if (Window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window window) return;
            if (fullScreen)
            {
                _previewFormWindowPresenter = window.AppWindow.Presenter;
                window.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            }
            else if (_previewFormWindowPresenter is { } presenter)
            {
                window.AppWindow.SetPresenter(presenter);
                _previewFormWindowPresenter = null;
            }
            else
            {
                window.AppWindow.SetPresenter(AppWindowPresenterKind.Default);
            }
#elif LINUX
            if (Window?.Handler?.PlatformView is not Gtk.Window window) return;
            if (fullScreen && _previewFormFullScreenKeys is null)
            {
                _previewFormFullScreenKeys = Gtk.EventControllerKey.New();
                _previewFormFullScreenKeys.SetPropagationPhase(Gtk.PropagationPhase.Capture);
                _previewFormFullScreenKeys.OnKeyPressed += PreviewFormFullScreenKeyPressed;
                window.AddController(_previewFormFullScreenKeys);
            }
            if (fullScreen) window.Fullscreen();
            else window.Unfullscreen();
#endif
            UpdatePreviewFormFullScreenButton(fullScreen);
            LogDiagnostic($"Preview window full screen: {fullScreen}.");
        }
        catch (Exception ex)
        {
            Log(ex, "Set preview window full screen", this);
        }
#endif
    }

    private void UpdatePreviewFormFullScreenButton(bool? fullScreen = null)
    {
#if WINDOWS
        _previewFormFullScreen = fullScreen ?? (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window window
            && window.AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen);
#elif LINUX
        _previewFormFullScreen = fullScreen ?? (Window?.Handler?.PlatformView is Gtk.Window window && window.IsFullscreen());
#else
        _previewFormFullScreen = false;
#endif
        PreviewFormFullScreenButton.Text = _previewFormFullScreen
            ? Localized.DraftPage_Preview_ExitFullScreen : Localized.DraftPage_Preview_FullScreen;
#if LINUX
        if (!_previewFormFullScreen && _previewFormFullScreenKeys is { } keys)
        {
            _previewFormFullScreenKeys = null;
            keys.OnKeyPressed -= PreviewFormFullScreenKeyPressed;
            keys.GetWidget()?.RemoveController(keys);
            keys.Dispose();
        }
#endif
    }

#if LINUX
    private bool PreviewFormFullScreenKeyPressed(Gtk.EventControllerKey sender, Gtk.EventControllerKey.KeyPressedSignalArgs e)
    {
        if (e.Keyval != Gdk.Constants.KEY_Escape || !IsPreviewForm || !_previewFormFullScreen) return false;
        SetPreviewFormFullScreen(false);
        return true;
    }
#endif

    private void UpdatePreviewFormPlaybackControls()
    {
        if (!IsPreviewForm) return;
        PreviewFormPlayPauseButton.IsEnabled = ProjectDuration > 0;
        if (_previewFormDragging) return;
        _updatingPreviewFormProgress = true;
        try
        {
            PreviewFormProgress.Maximum = Math.Max(1, ProjectDuration - 1d);
            PreviewFormProgress.IsEnabled = ProjectDuration > 1;
            PreviewFormProgress.Value = Math.Clamp(_currentFrame, 0, Math.Max(0, ProjectDuration - 1d));
            UpdatePreviewFormTimeLabel(PreviewFormProgress.Value);
        }
        finally
        {
            _updatingPreviewFormProgress = false;
        }
    }

    private void UpdatePreviewFormTimeLabel(double frame)
        => PreviewFormTimeLabel.Text = $"{TimeSpan.FromSeconds(frame * SecondsPerFrame):mm\\:ss\\.ff} / {TimeSpan.FromSeconds(ProjectDuration * SecondsPerFrame):mm\\:ss\\.ff}";

    private void PreviewFormProgress_DragStarted(object? sender, EventArgs e)
    {
        _previewFormDragging = true;
        _resumePreviewFormPlayback |= isPlaying;
    }

    private async void PreviewFormProgress_DragCompleted(object? sender, EventArgs e)
    {
        _previewFormDragging = false;
        if (IsPreviewForm) await SeekPreviewFormAsync(PreviewFormProgress.Value, debounce: false);
    }

    private async void PreviewFormProgress_ValueChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_updatingPreviewFormProgress || !IsPreviewForm) return;
        UpdatePreviewFormTimeLabel(e.NewValue);
        await SeekPreviewFormAsync(e.NewValue);
    }

    private async Task SeekPreviewFormAsync(double frame, bool debounce = true)
    {
        _resumePreviewFormPlayback |= isPlaying;
        _previewFormSeekCts?.Cancel();
        CancelDynamicPreview();
        using var cts = new CancellationTokenSource();
        _previewFormSeekCts = cts;
        try
        {
            if (debounce) await Task.Delay(120, cts.Token);
            await _previewFormSeekLock.WaitAsync(cts.Token);
            try
            {
                cts.Token.ThrowIfCancellationRequested();
                if (!IsPreviewForm || AlreadyDisappeared) return;
                if (isPlaying || previewer.HasDynamicPreview) await PauseLivePreview(refreshPreview: false);
                cts.Token.ThrowIfCancellationRequested();
                _currentFrame = Math.Clamp(Math.Round(frame), 0, Math.Max(0, ProjectDuration - 1d));
                SyncClipEditorCurrentFrame();
                UpdatePlayheadPosition();
                await RefreshPreviewFromCurrentProviderAsync();
                cts.Token.ThrowIfCancellationRequested();
                if (_resumePreviewFormPlayback && !_previewFormDragging && IsPreviewForm && !AlreadyDisappeared)
                {
                    _resumePreviewFormPlayback = false;
                    if (_currentFrame < ProjectDuration - 1d) PlayPauseButton_Clicked(this, EventArgs.Empty);
                }
            }
            finally
            {
                _previewFormSeekLock.Release();
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _resumePreviewFormPlayback = false;
            Log(ex, "Seek preview form", this);
        }
        finally
        {
            if (ReferenceEquals(_previewFormSeekCts, cts)) _previewFormSeekCts = null;
        }
    }
    #endregion

    #region device adapt
    private Window? _adaptiveFormWindow;
    private bool _adaptiveFormUpdateQueued;
    private int _adaptiveFormGeneration;
#if ANDROID || IOS
    private Services.DraftFormMonitor? _draftFormMonitor;
#endif

    private DraftPageForm GetAdaptiveForm()
    {
#if ANDROID || IOS
        double width = Width > 0 ? Width : Window?.Width ?? 0;
        if (_draftFormMonitor is not null) return _draftFormMonitor.GetForm(width);
        if (DeviceInfo.Idiom == DeviceIdiom.Tablet)
            return width > 0 && width < 840 ? DraftPageForm.Mobile : DraftPageForm.Workspace;
        return DeviceDisplay.MainDisplayInfo.Orientation == DisplayOrientation.Landscape
            ? DraftPageForm.Workspace : DraftPageForm.Mobile;
#else
        return DraftPageForm.Workspace;
#endif
    }

    private void StartAdaptiveFormMonitoring()
    {
        if (_adaptiveFormWindow is not null || Window is null) return;
        _adaptiveFormWindow = Window;
        SizeChanged += AdaptiveFormEnvironmentChanged;
        HandlerChanged += AdaptiveFormHandlerChanged;
        _adaptiveFormWindow.HandlerChanged += AdaptiveFormHandlerChanged;
        _adaptiveFormWindow.SizeChanged += AdaptiveFormEnvironmentChanged;
        _adaptiveFormWindow.Activated += AdaptiveFormEnvironmentChanged;
        _adaptiveFormWindow.Resumed += AdaptiveFormEnvironmentChanged;
        RecreateAdaptiveFormMonitor();
    }

    private void AdaptiveFormHandlerChanged(object? sender, EventArgs e)
    {
        RecreateAdaptiveFormMonitor();
        AdaptiveFormEnvironmentChanged(sender, e);
    }

    private void RecreateAdaptiveFormMonitor()
    {
#if ANDROID || IOS
        var monitor = _draftFormMonitor;
        _draftFormMonitor = null;
        try
        {
            monitor?.Dispose();
            if (Window?.Handler?.PlatformView is null || Handler?.PlatformView is null) return;
            _draftFormMonitor = new Services.DraftFormMonitor(this);
            _draftFormMonitor.Changed += AdaptiveFormEnvironmentChanged;
        }
        catch (Exception ex)
        {
            Log(ex, "Observe draft device form", this);
        }
#endif
    }

    private void AdaptiveFormEnvironmentChanged(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_adaptiveFormWindow is null || AlreadyDisappeared || _adaptiveFormUpdateQueued) return;
            _adaptiveFormUpdateQueued = true;
            int generation = _adaptiveFormGeneration;
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(100), async () =>
            {
                if (generation != _adaptiveFormGeneration) return;
                _adaptiveFormUpdateQueued = false;
                await _formSwitchLock.WaitAsync();
                try
                {
                    if (generation != _adaptiveFormGeneration || AlreadyDisappeared || IsPreviewForm) return;
                    var form = GetAdaptiveForm();
                    if (!_formInitialized || CurrentForm != form) await ApplyFormAsync(form);
                }
                catch (Exception ex)
                {
                    Log(ex, "Apply draft device form", this);
                }
                finally
                {
                    _formSwitchLock.Release();
                }
            });
        });
    }

    private void StopAdaptiveFormMonitoring()
    {
        _adaptiveFormGeneration++;
        _adaptiveFormUpdateQueued = false;
        SizeChanged -= AdaptiveFormEnvironmentChanged;
        HandlerChanged -= AdaptiveFormHandlerChanged;
        if (_adaptiveFormWindow is not null)
        {
            _adaptiveFormWindow.HandlerChanged -= AdaptiveFormHandlerChanged;
            _adaptiveFormWindow.SizeChanged -= AdaptiveFormEnvironmentChanged;
            _adaptiveFormWindow.Activated -= AdaptiveFormEnvironmentChanged;
            _adaptiveFormWindow.Resumed -= AdaptiveFormEnvironmentChanged;
            _adaptiveFormWindow = null;
        }
#if ANDROID || IOS
        try
        {
            _draftFormMonitor?.Dispose();
        }
        catch (Exception ex)
        {
            Log(ex, "Stop observing draft device form", this);
        }
        _draftFormMonitor = null;
#endif
    }
    #endregion
}
