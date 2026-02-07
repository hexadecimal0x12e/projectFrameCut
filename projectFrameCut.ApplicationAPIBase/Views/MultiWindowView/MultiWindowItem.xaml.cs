using System;
using System.Runtime.Versioning;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using projectFrameCut.ApplicationAPIBase.Helpers;

namespace projectFrameCut.ApplicationAPIBase.Views.MultiWindowView
{
    /// <summary>
    /// Represents a MultiWindowItem, which is a customizable window-like container that can be used within a parent layout to create a multi-window interface.
    /// </summary>
    public partial class MultiWindowItem : ContentView
    {
        #region Bindable Properties

        public static readonly BindableProperty TitleProperty =
            BindableProperty.Create(nameof(Title), typeof(string), typeof(MultiWindowItem), "Window");

        public static readonly BindableProperty IsDraggableProperty =
            BindableProperty.Create(nameof(IsDraggable), typeof(bool), typeof(MultiWindowItem), true);

        public static readonly BindableProperty IsResizableProperty =
            BindableProperty.Create(nameof(IsResizable), typeof(bool), typeof(MultiWindowItem), true);

        public static readonly BindableProperty IsMaximizableProperty =
            BindableProperty.Create(nameof(IsMaximizable), typeof(bool), typeof(MultiWindowItem), true, propertyChanged: OnButtonVisibilityChanged);

        public static readonly BindableProperty IsMinimizableProperty =
            BindableProperty.Create(nameof(IsMinimizable), typeof(bool), typeof(MultiWindowItem), true, propertyChanged: OnButtonVisibilityChanged);

        public static readonly BindableProperty IsClosableProperty =
            BindableProperty.Create(nameof(IsClosable), typeof(bool), typeof(MultiWindowItem), true, propertyChanged: OnButtonVisibilityChanged);
        
        public static readonly BindableProperty IsPopOutVisibleProperty =
            BindableProperty.Create(nameof(IsPopOutVisible), typeof(bool), typeof(MultiWindowItem), false, propertyChanged: OnButtonVisibilityChanged);

        public static readonly BindableProperty IsNavigationVisibleProperty =
            BindableProperty.Create(nameof(IsNavigationVisible), typeof(bool), typeof(MultiWindowItem), true);

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public bool IsPopOutVisible
        {
            get => (bool)GetValue(IsPopOutVisibleProperty);
            set => SetValue(IsPopOutVisibleProperty, value);
        }

        public bool IsDraggable
        {
            get => (bool)GetValue(IsDraggableProperty);
            set => SetValue(IsDraggableProperty, value);
        }

        public bool IsResizable
        {
            get => (bool)GetValue(IsResizableProperty);
            set => SetValue(IsResizableProperty, value);
        }

        public bool IsMaximizable
        {
            get => (bool)GetValue(IsMaximizableProperty);
            set => SetValue(IsMaximizableProperty, value);
        }

        public bool IsMinimizable
        {
            get => (bool)GetValue(IsMinimizableProperty);
            set => SetValue(IsMinimizableProperty, value);
        }

        public bool IsClosable
        {
            get => (bool)GetValue(IsClosableProperty);
            set => SetValue(IsClosableProperty, value);
        }

        public bool IsNavigationVisible
        {
            get => (bool)GetValue(IsNavigationVisibleProperty);
            set => SetValue(IsNavigationVisibleProperty, value);
        }

        private static readonly BindablePropertyKey CanGoBackPropertyKey =
            BindableProperty.CreateReadOnly(nameof(CanGoBack), typeof(bool), typeof(MultiWindowItem), false);

        public static readonly BindableProperty CanGoBackProperty = CanGoBackPropertyKey.BindableProperty;

        public bool CanGoBack
        {
            get => (bool)GetValue(CanGoBackProperty);
            private set => SetValue(CanGoBackPropertyKey, value);
        }

        private static readonly BindablePropertyKey CanGoForwardPropertyKey =
            BindableProperty.CreateReadOnly(nameof(CanGoForward), typeof(bool), typeof(MultiWindowItem), false);

        public static readonly BindableProperty CanGoForwardProperty = CanGoForwardPropertyKey.BindableProperty;

        public bool CanGoForward
        {
            get => (bool)GetValue(CanGoForwardProperty);
            private set => SetValue(CanGoForwardPropertyKey, value);
        }

        /// <summary>
        /// Provide a <see cref="IContextMenuBuilder"/> to help build the ContextMenu when user right-clicks on the title bar. The context menu will be displayed with the options provided by the builder.
        /// </summary>
        public static Func<IContextMenuBuilder>? ContextMenuProviderGetter { get; set; }

        #endregion

        #region Events

        public event EventHandler<CloseEventArgs> CloseClicked;
        public event EventHandler MinimizeClicked;
        public event EventHandler MaximizeClicked;
        public event EventHandler Activated;

        #endregion

        #region Fields

        private double _startX, _startY;
        private double _startWidth, _startHeight;

        // For window state restoration
        private double _preMaxWidth, _preMaxHeight, _preMaxX, _preMaxY;
        private int _preCol, _preRow, _preColSpan, _preRowSpan;
        private bool _isMaximized = false;

        private bool _isMinimized = false;
        private double _preMinHeight;

        // Template Parts
        private Grid _titleBarGrid;
        private Border _visualRoot;
        private Grid _resizeGrid;
        private Border _minimizeBtn;
        private Border _maximizeBtn;
        private Border _closeBtn;
        private Border _dockBtn;
        private Border _popOutBtn;

        // Window Mode State
        private bool _isInWindowMode = false;
        private object _originalParent; // Can be Layout, ContentPage, ContentView
        private Grid _originalTitleBarParent; // The Grid in VisualRoot
        private Window _hostWindow;

        // Snapshot before window mode
        private double _preWindowX, _preWindowY, _preWindowWidth, _preWindowHeight;

        // Dialog Parts
        private Grid _dialogOverlay;
        private Label _dialogTitle;
        private Label _dialogMessage;
        private Entry _dialogInput;
        private ScrollView _actionSheetScroll;
        private VerticalStackLayout _actionSheetContainer;
        private Grid _dialogButtonGrid;
        private Button _dialogOkBtn;
        private Button _dialogCancelBtn;
        private Button _actionSheetCancelBtn;

        // Dialog State
        private TaskCompletionSource<bool> _alertTcs;
        private TaskCompletionSource<string> _promptTcs;
        private TaskCompletionSource<string> _actionSheetTcs;

        private readonly System.Collections.Generic.Stack<View> _backStack = new();
        private readonly System.Collections.Generic.Stack<View> _forwardStack = new();

        #endregion

        #region Init

        public MultiWindowItem()
        {
            InitializeComponent();

            // Ensure the window floats and doesn't stretch by default
            HorizontalOptions = LayoutOptions.Start;
            VerticalOptions = LayoutOptions.Start;
        }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            _titleBarGrid = GetTemplateChild("TitleBarGrid") as Grid;
            _visualRoot = GetTemplateChild("VisualRoot") as Border;
            _resizeGrid = GetTemplateChild("ResizeGrid") as Grid;
            _minimizeBtn = GetTemplateChild("MinimizeBtn") as Border;
            _maximizeBtn = GetTemplateChild("MaximizeBtn") as Border;
            _closeBtn = GetTemplateChild("CloseBtn") as Border;
            _dockBtn = GetTemplateChild("DockBtn") as Border;
            _popOutBtn = GetTemplateChild("PopOutBtn") as Border;

            if (_titleBarGrid != null)
            {
                // Remove existing gestures to avoid duplicates if re-applied
                _titleBarGrid.GestureRecognizers.Clear();
                var panGesture = new PanGestureRecognizer();
                panGesture.PanUpdated += OnTitleBarPanUpdated;
                _titleBarGrid.GestureRecognizers.Add(panGesture);
            }

            if (_visualRoot != null)
            {
                _visualRoot.GestureRecognizers.Clear();
                var tapGesture = new TapGestureRecognizer();
                tapGesture.Tapped += (s, e) => Activated?.Invoke(this, EventArgs.Empty);
                _visualRoot.GestureRecognizers.Add(tapGesture);
            }

            // Wire up Buttons
            if (_minimizeBtn != null)
            {
                _minimizeBtn.GestureRecognizers.Clear();
                var tap = new TapGestureRecognizer();
                tap.Tapped += OnMinimizeTapped;
                _minimizeBtn.GestureRecognizers.Add(tap);
            }
            if (_maximizeBtn != null)
            {
                _maximizeBtn.GestureRecognizers.Clear();
                var tap = new TapGestureRecognizer();
                tap.Tapped += OnMaximizeTapped;
                _maximizeBtn.GestureRecognizers.Add(tap);
            }
            if (_closeBtn != null)
            {
                _closeBtn.GestureRecognizers.Clear();
                var tap = new TapGestureRecognizer();
                tap.Tapped += OnCloseTapped;
                _closeBtn.GestureRecognizers.Add(tap);
            }
            if (_dockBtn != null)
            {
                _dockBtn.GestureRecognizers.Clear();
                var tap = new TapGestureRecognizer();
                tap.Tapped += OnDockTapped;
                _dockBtn.GestureRecognizers.Add(tap);
                // Ensure visibility is sync with window mode, default hidden
                _dockBtn.IsVisible = _isInWindowMode;
            }
            if (_popOutBtn != null)
            {
                _popOutBtn.GestureRecognizers.Clear();
                var tap = new TapGestureRecognizer();
                tap.Tapped += OnPopOutTapped;
                _popOutBtn.GestureRecognizers.Add(tap);
            }

            // Dialog Parts
            _dialogOverlay = GetTemplateChild("DialogOverlay") as Grid;
            _dialogTitle = GetTemplateChild("DialogTitle") as Label;
            _dialogMessage = GetTemplateChild("DialogMessage") as Label;
            _dialogInput = GetTemplateChild("DialogInput") as Entry;
            _actionSheetScroll = GetTemplateChild("ActionSheetScroll") as ScrollView;
            _actionSheetContainer = GetTemplateChild("ActionSheetContainer") as VerticalStackLayout;
            _dialogButtonGrid = GetTemplateChild("DialogButtonGrid") as Grid;
            _dialogOkBtn = GetTemplateChild("DialogOkBtn") as Button;
            _dialogCancelBtn = GetTemplateChild("DialogCancelBtn") as Button;
            _actionSheetCancelBtn = GetTemplateChild("ActionSheetCancelBtn") as Button;

            if (_dialogOkBtn != null) _dialogOkBtn.Clicked += OnDialogOkClicked;
            if (_dialogCancelBtn != null) _dialogCancelBtn.Clicked += OnDialogCancelClicked;
            if (_actionSheetCancelBtn != null) _actionSheetCancelBtn.Clicked += OnActionSheetCancelClicked;

            // Wire up Resize Handles
            SetupResizeHandle("ResizeTopLeft", OnResizeTopLeft);
            SetupResizeHandle("ResizeTop", OnResizeTop);
            SetupResizeHandle("ResizeTopRight", OnResizeTopRight);
            SetupResizeHandle("ResizeLeft", OnResizeLeft);
            SetupResizeHandle("ResizeRight", OnResizeRight);
            SetupResizeHandle("ResizeBottomLeft", OnResizeBottomLeft);
            SetupResizeHandle("ResizeBottom", OnResizeBottom);
            SetupResizeHandle("ResizeBottomRight", OnResizeBottomRight);

            UpdatedButtonVisibility();

            if(ContextMenuProviderGetter is not null && _titleBarGrid is not null)
            {
                var ContextMenuProvider = ContextMenuProviderGetter();

                ContextMenuProvider.AddCommand("Recover", () =>
                    {
                        if(_isMaximized) Maximize();
                        if(_isMinimized) Minimize();
                    })
                    .AddCommand("Close", () => OnCloseTapped(this, EventArgs.Empty))
                    .AddCommand("Maximize", Maximize)
                    .AddCommand("Minimize", Minimize);
#if WINDOWS || MACCATALYST
                ContextMenuProvider.AddCommand("To standalone window", () => OpenInNewWindow());
#endif

                var gesture = new TapGestureRecognizer
                {
                    NumberOfTapsRequired = 1,
                    Buttons = ButtonsMask.Secondary
                };
                gesture.Tapped += (s, e) => ContextMenuProvider.TryShow(_titleBarGrid);
                _titleBarGrid.GestureRecognizers.Add(gesture);   

            }

        }

        private void SetupResizeHandle(string name, EventHandler<PanUpdatedEventArgs> handler)
        {
            if (GetTemplateChild(name) is View handle)
            {
                handle.GestureRecognizers.Clear();
                var pan = new PanGestureRecognizer();
                pan.PanUpdated += handler;
                handle.GestureRecognizers.Add(pan);
            }
        }

        private static void OnButtonVisibilityChanged(BindableObject bindable, object oldValue, object newValue)
        {
            if (bindable is MultiWindowItem item)
            {
                item.UpdatedButtonVisibility();
            }
        }

        private void UpdatedButtonVisibility()
        {
            if (_isInWindowMode)
            {
                if (_minimizeBtn != null) _minimizeBtn.IsVisible = false;
                if (_maximizeBtn != null) _maximizeBtn.IsVisible = false;
                if (_closeBtn != null) _closeBtn.IsVisible = false;
                if (_popOutBtn != null) _popOutBtn.IsVisible = false; // Hide popout button in window mode
                return;
            }

            if (_minimizeBtn != null) _minimizeBtn.IsVisible = IsMinimizable;
            if (_maximizeBtn != null) _maximizeBtn.IsVisible = IsMaximizable;
            if (_closeBtn != null) _closeBtn.IsVisible = IsClosable;
            if (_popOutBtn != null) _popOutBtn.IsVisible = IsPopOutVisible; // Show based on property
        }

#endregion

        #region Dialogs

        /// <summary>
        /// Displays an alert dialog with the specified title and message, allowing the user to dismiss it with a cancel
        /// button.
        /// </summary>
        /// <remarks>This method provides a simplified interface for displaying an alert with a single
        /// cancel option. No additional buttons are included.</remarks>
        /// <param name="title">The title of the alert dialog, displayed at the top of the dialog.</param>
        /// <param name="message">The message content shown in the alert dialog, providing information or context to the user.</param>
        /// <param name="cancel">The text for the cancel button, which the user can tap to close the alert dialog.</param>
        /// <returns>A task that represents the asynchronous operation of displaying the alert dialog.</returns>
        public Task DisplayAlertAsync(string title, string message, string cancel)
        {
            return DisplayAlertAsync(title, message, null, cancel);
        }

        /// <summary>
        /// Displays an alert dialog with the specified title and message, allowing the user to dismiss it with a cancel
        /// button.
        /// </summary>
        /// <remarks>This method provides a simplified interface for displaying an alert with a single
        /// cancel option. No additional buttons are included.</remarks>
        /// <param name="title">The title of the alert dialog, displayed at the top of the dialog.</param>
        /// <param name="message">The message content shown in the alert dialog, providing information or context to the user.</param>
        /// <param name="accept">The text for the accept button, which the user can tap to confirm the alert dialog.</param>
        /// <param name="cancel">The text for the cancel button, which the user can tap to close the alert dialog.</param>
        /// <returns>True for accept, false for cancel.</returns>
        public async Task<bool> DisplayAlertAsync(string title, string message, string accept, string cancel)
        {
            if (_hostWindow?.Page is not null) return await _hostWindow.Page.DisplayAlertAsync(title, message, accept, cancel);
            if (_dialogOverlay == null) return false;

            ResetDialogUI();
            _dialogTitle.Text = title;
            _dialogMessage.Text = message;
            _dialogCancelBtn.Text = cancel;

            if (string.IsNullOrEmpty(accept))
            {
                _dialogOkBtn.IsVisible = false;
                if (_dialogButtonGrid != null) Grid.SetColumnSpan(_dialogCancelBtn, 2);
            }
            else
            {
                _dialogOkBtn.Text = accept;
                _dialogOkBtn.IsVisible = true;
                if (_dialogButtonGrid != null) Grid.SetColumnSpan(_dialogCancelBtn, 1);
            }

            if (_dialogButtonGrid != null) _dialogButtonGrid.IsVisible = true;
            _dialogOverlay.IsVisible = true;

            _alertTcs = new TaskCompletionSource<bool>();
            return await _alertTcs.Task;
        }

        /// <summary>
        /// Provide an input field for user to enter text. User can confirm with "accept" button or cancel with "cancel" button.
        /// </summary>
        /// <param name="title">The title of the prompt dialog, displayed at the top of the dialog.</param>
        /// <param name="message">The message content shown in the prompt dialog, providing information or context to the user.</param>
        /// <param name="accept">The text for the accept button, which the user can tap to confirm the prompt dialog.</param>
        /// <param name="cancel">The text for the cancel button, which the user can tap to close the prompt dialog.</param>
        /// <param name="placeholder">The placeholder text displayed in the input field when it is empty.</param>
        /// <param name="maxLength">The maximum number of characters allowed in the input field.</param>
        /// <param name="keyboard">The type of keyboard to display for the input field.</param>
        /// <param name="initialValue">The initial text value displayed in the input field.</param>
        /// <returns>The text entered by the user if accepted; otherwise, null.</returns>
        public async Task<string> DisplayPromptAsync(string title, string message, string accept = "OK", string cancel = "Cancel", string placeholder = null, int maxLength = -1, Keyboard keyboard = null, string initialValue = "")
        {
            if (_hostWindow?.Page is not null) return await _hostWindow.Page.DisplayPromptAsync(title, message, accept, cancel,placeholder,maxLength,keyboard,initialValue);

            if (_dialogOverlay == null) return null;

            ResetDialogUI();
            _dialogTitle.Text = title;
            _dialogMessage.Text = message;
            _dialogOkBtn.Text = accept;
            _dialogCancelBtn.Text = cancel;

            if (_dialogInput != null)
            {
                _dialogInput.IsVisible = true;
                _dialogInput.Placeholder = placeholder;
                _dialogInput.MaxLength = maxLength > 0 ? maxLength : int.MaxValue;
                _dialogInput.Keyboard = keyboard ?? Keyboard.Default;
                _dialogInput.Text = initialValue;
                _dialogInput.Focus();
            }

            if (_dialogButtonGrid != null) _dialogButtonGrid.IsVisible = true;
            _dialogOverlay.IsVisible = true;

            _promptTcs = new TaskCompletionSource<string>();
            return await _promptTcs.Task;
        }

        /// <summary>
        /// Shows an action sheet with a list of options. User can select one option, or cancel. This is typically used for presenting a set of choices related to an action the user is taking.
        /// </summary>
        /// <param name="title">The title of the action sheet, displayed at the top.</param>
        /// <param name="cancel">The text for the cancel button, which the user can tap to close the action sheet without making a selection.</param>
        /// <param name="destruction">The text for the destructive button, which represents a destructive action the user can take.</param>
        /// <param name="buttons">An array of other button texts representing different options the user can select.</param>
        /// <returns>The user's option selection as a string, or null if cancelled.</returns>
        public async Task<string> DisplayActionSheetAsync(string title, string cancel, string destruction, params string[] buttons)
        {
            if (_hostWindow?.Page is not null) return await _hostWindow.Page.DisplayActionSheetAsync(title, cancel, destruction, buttons);
            if (_dialogOverlay == null) return null;

            ResetDialogUI();
            _dialogTitle.Text = title;
            _dialogMessage.IsVisible = false; // Usually ActionSheet has only title

            if (_actionSheetScroll != null) _actionSheetScroll.IsVisible = true;
            if (_actionSheetContainer != null)
            {
                _actionSheetContainer.Children.Clear();

                if (!string.IsNullOrEmpty(destruction))
                {
                    var btn = CreateActionSheetButton(destruction, true);
                    btn.Clicked += (s, e) => OnActionSheetButtonClicked(destruction);
                    _actionSheetContainer.Children.Add(btn);
                }

                if (buttons != null)
                {
                    foreach (var b in buttons)
                    {
                        var btn = CreateActionSheetButton(b, false);
                        btn.Clicked += (s, e) => OnActionSheetButtonClicked(b);
                        _actionSheetContainer.Children.Add(btn);
                    }
                }
            }

            if (!string.IsNullOrEmpty(cancel) && _actionSheetCancelBtn != null)
            {
                _actionSheetCancelBtn.Text = cancel;
                _actionSheetCancelBtn.IsVisible = true;
            }

            _dialogOverlay.IsVisible = true;

            _actionSheetTcs = new TaskCompletionSource<string>();
            return await _actionSheetTcs.Task;
        }

        private Button CreateActionSheetButton(string text, bool isDestructive)
        {
            return new Button
            {
                Text = text,
                BackgroundColor = isDestructive ? Colors.Red : Color.FromArgb("#333333"),
                TextColor = Colors.White,
                CornerRadius = 4
            };
        }

        private void ResetDialogUI()
        {
            if (_dialogTitle != null) _dialogTitle.Text = "";
            if (_dialogMessage != null)
            {
                _dialogMessage.Text = "";
                _dialogMessage.IsVisible = true;
            }
            if (_dialogInput != null)
            {
                _dialogInput.IsVisible = false;
                _dialogInput.Text = "";
            }
            if (_actionSheetScroll != null) _actionSheetScroll.IsVisible = false;
            if (_dialogButtonGrid != null) _dialogButtonGrid.IsVisible = false;
            if (_actionSheetCancelBtn != null) _actionSheetCancelBtn.IsVisible = false;

            // Reset Grid layout for buttons
            if (_dialogCancelBtn != null) Grid.SetColumnSpan(_dialogCancelBtn, 1);
            if (_dialogOkBtn != null) Grid.SetColumn(_dialogOkBtn, 1);
        }

        private void OnDialogOkClicked(object sender, EventArgs e)
        {
            CloseDialog();
            if (_alertTcs != null)
            {
                _alertTcs.TrySetResult(true);
                _alertTcs = null;
            }
            if (_promptTcs != null)
            {
                _promptTcs.TrySetResult(_dialogInput?.Text);
                _promptTcs = null;
            }
        }

        private void OnDialogCancelClicked(object sender, EventArgs e)
        {
            CloseDialog();
            if (_alertTcs != null)
            {
                _alertTcs.TrySetResult(false);
                _alertTcs = null;
            }
            if (_promptTcs != null)
            {
                _promptTcs.TrySetResult(null);
                _promptTcs = null;
            }
        }

        private void OnActionSheetCancelClicked(object sender, EventArgs e)
        {
            CloseDialog();
            if (_actionSheetTcs != null)
            {
                _actionSheetTcs.TrySetResult(_actionSheetCancelBtn?.Text ?? "Cancel");
                _actionSheetTcs = null;
            }
        }

        private void OnActionSheetButtonClicked(string result)
        {
            CloseDialog();
            if (_actionSheetTcs != null)
            {
                _actionSheetTcs.TrySetResult(result);
                _actionSheetTcs = null;
            }
        }

        private void CloseDialog()
        {
            if (_dialogOverlay != null) _dialogOverlay.IsVisible = false;
        }

        #endregion

        #region Event Handlers

        private void OnCloseTapped(object sender, EventArgs e)
        {
            if (_isInWindowMode && _hostWindow != null)
            {
                 // In Window Mode, simply close the host window.
                 // The 'CloseClicked' event is subscribed in OpenInNewWindow to handle cleanup if needed,
                 // but typically we can just close the window.
                 // However, to keep consistency with 'CloseClicked' allowing cancel, we invoke it.
                 var a = new CloseEventArgs { Cancel = false };
                 CloseClicked?.Invoke(this, a);
                 if (a.Cancel) return; // Allow user to cancel closing
                 
                 Application.Current?.CloseWindow(_hostWindow);
                 return;
            }

            CloseDialog();
            var a1 = new CloseEventArgs { Cancel = false };
            CloseClicked?.Invoke(this, a1);
            if (a1.Cancel) return;
            // Internal MDI Close
            if (Parent is Layout layout)
            {
                layout.Children.Remove(this);
            }
        }

        private void OnMinimizeTapped(object sender, EventArgs e)
        {
            if (_isInWindowMode && _hostWindow != null)
            {
                 // Minimize Host Window
                 // MAUI 8/9 doesn't have a direct 'Minimize' on Window cross-platform easily accessible 
                 // without lifecycle hooks, but on Windows/MacCatalyst we might be able to.
                 // Actually, standard MAUI Window doesn't expose WindowState.
                 // We will skip explicit Minimize implementation for now or rely on native titlebar if present.
                 // But since we are "replacing" the titlebar, we should implement it.
#if WINDOWS
                 // Check if we can access platform specific window
                 var platformWindow = _hostWindow.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
                 if (platformWindow != null)
                 {
                      var handle = WinRT.Interop.WindowNative.GetWindowHandle(platformWindow);
                      var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
                      var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
                      if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
                      {
                           p.Minimize();
                      }
                 }
#elif MACCATALYST
                 // MacCatalyst specific minimize
                 var uiWindow = _hostWindow.Handler?.PlatformView as UIKit.UIWindow;
                 uiWindow?.WindowScene?.SizeRestrictions?.MinimumSize.Deconstruct(out var w, out var h);
                 // Minimizing programmatically in UIKit is not standard for iPad apps, 
                 // but for MacCatalyst we can use NSWindow via dynamic lookup if needed.
                 // Getting simple: just do nothing or maybe Hide? No.
#endif
                 return;
            }
            MinimizeClicked?.Invoke(this, EventArgs.Empty);
            Minimize();
        }

        private void OnMaximizeTapped(object sender, EventArgs e)
        {
            if (_isInWindowMode && _hostWindow != null)
            {
#if WINDOWS
                 var platformWindow = _hostWindow.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
                 if (platformWindow != null)
                 {
                      var handle = WinRT.Interop.WindowNative.GetWindowHandle(platformWindow);
                      var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
                      var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
                      if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
                      {
                          if (p.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized)
                              p.Restore();
                          else
                              p.Maximize();
                      }
                 }
#elif MACCATALYST
                 var uiWindow = _hostWindow.Handler?.PlatformView as UIKit.UIWindow;
                 if (uiWindow?.WindowScene != null)
                 {
                      // MacCatalyst: Zoom toggle
                      // We can't easily toggle zoom from shared code without binding
                 }
#endif
                 return;
            }

            MaximizeClicked?.Invoke(this, EventArgs.Empty);
            Maximize();
        }

        private void OnDockTapped(object sender, EventArgs e)
        {
            PerformDock(true);
        }

        private void PerformDock(bool closeWindow)
        {
            if (!_isInWindowMode || _hostWindow == null) return;

            // Close the host window - but do not destroy the content interaction
            // actually, 'CloseWindow' closes the window. We need to detach THIS content first?
            // If we detach content, the window becomes empty.
            // 1. Detach content from Host Page
            if (_hostWindow.Page is ContentPage cp)
            {
                cp.Content = null;
            }

            // 2. Close Window
            var win = _hostWindow;
            _hostWindow = null;
            _isInWindowMode = false;
            
            if (closeWindow)
            {
                Application.Current?.CloseWindow(win);
            }

            // 3. Restore to Original Parent
            if (_originalParent is Layout layout)
            {
                layout.Add(this);
            }
            else if (_originalParent is ContentView cv)
            {
                cv.Content = this;
            }
            else if (_originalParent is ContentPage page)
            {
                page.Content = this;
            }

            // 4. Restore Visual State
            if (_dockBtn != null) _dockBtn.IsVisible = false;
            
            // Re-enable internal MDI
            IsDraggable = true;
            IsResizable = true;
            IsMaximizable = true;
            IsMinimizable = true;
            // Update buttons visibility (restore them)
            UpdatedButtonVisibility();
            
            if (_resizeGrid != null) _resizeGrid.IsVisible = true;
            if (_visualRoot != null)
            {
                _visualRoot.StrokeThickness = 1; // Default
                _visualRoot.StrokeShape = new RoundRectangle { CornerRadius = 10 }; // Default
            }

            if (_titleBarGrid != null)
            {
                // Restore TitleBar to internal visual tree
                if (_originalTitleBarParent != null && _titleBarGrid.Parent != _originalTitleBarParent)
                {
                    // It was moved to Window.TitleBar, move it back
                    // TitleBar control might need to release it first? 
                    // Usually removing from one parent and adding to another is fine, 
                    // but since Window is closing, just adding it back should work.
                    // Check if it's already removed?
                    if (_titleBarGrid.Parent is Layout oldP) oldP.Remove(_titleBarGrid); 
                    else if (_titleBarGrid.Parent is ContentView oldCV) oldCV.Content = null;
                    
                    if (!_originalTitleBarParent.Children.Contains(_titleBarGrid))
                    {
                        _originalTitleBarParent.Children.Add(_titleBarGrid);
                        Grid.SetRow(_titleBarGrid, 0); // Assuming row 0
                    }
                }
                _titleBarGrid.IsVisible = true;
                if (_originalTitleBarParent != null && _originalTitleBarParent.RowDefinitions.Count > 0)
                {
                    _originalTitleBarParent.RowDefinitions[0].Height = new GridLength(32);
                }



            }

            // Restore size/pos
            this.HorizontalOptions = LayoutOptions.Start;
            this.VerticalOptions = LayoutOptions.Start;
            this.TranslationX = _preWindowX;
            this.TranslationY = _preWindowY;
            this.WidthRequest = _preWindowWidth > 0 ? _preWindowWidth : 400; // Fallback
            this.HeightRequest = _preWindowHeight > 0 ? _preWindowHeight : 300; // Fallback
            
            // Reset margin just in case
            this.Margin = new Thickness(0);
        }

        private void OnBackTapped(object sender, EventArgs e)
        {
            if (CanGoBack) GoBack();
        }

        private void OnForwardTapped(object sender, EventArgs e)
        {
            if (CanGoForward) GoForward();
        }

        private void OnPopOutTapped(object sender, EventArgs e)
        {
            OpenInNewWindow();
        }


        #endregion

        #region Actions

        /// <summary>
        /// Make the window minimized
        /// </summary>
        public void Minimize()
        {
            if (_isMinimized)
            {
                // Restore
                this.HeightRequest = _preMinHeight;
                _isMinimized = false;
                // Re-enable resize handles
                if (_resizeGrid != null) _resizeGrid.IsVisible = true;
            }
            else
            {
                _preMinHeight = this.HeightRequest > 0 ? this.HeightRequest : this.Height;
                // Minimize to TitleHeight approx 32 + borders
                this.HeightRequest = 35;
                _isMinimized = true;
                // Disable resize handles
                if (_resizeGrid != null) _resizeGrid.IsVisible = false;
            }
        }

        /// <summary>
        /// Make this window to use all space in the parent container.
        /// </summary>
        public void Maximize()
        {
            if (Parent is not VisualElement parentContainer) return;

            if (_isMaximized)
            {
                // Restore
                this.HorizontalOptions = LayoutOptions.Start;
                this.VerticalOptions = LayoutOptions.Start;
                this.TranslationX = _preMaxX;
                this.TranslationY = _preMaxY;
                this.WidthRequest = _preMaxWidth;
                this.HeightRequest = _preMaxHeight;

                Grid.SetColumn(this, _preCol);
                Grid.SetRow(this, _preRow);
                Grid.SetColumnSpan(this, _preColSpan);
                Grid.SetRowSpan(this, _preRowSpan);

                _isMaximized = false;

                // Re-enable resize handles
                if (_resizeGrid != null) _resizeGrid.IsVisible = true;
                if (_visualRoot != null) _visualRoot.StrokeShape = new RoundRectangle { CornerRadius = 10 };
            }
            else
            {
                // Snapshot
                _preMaxX = this.TranslationX;
                _preMaxY = this.TranslationY;
                _preMaxWidth = this.Width;
                _preMaxHeight = this.Height;
                _preCol = Grid.GetColumn(this);
                _preRow = Grid.GetRow(this);
                _preColSpan = Grid.GetColumnSpan(this);
                _preRowSpan = Grid.GetRowSpan(this);

                // Maximize
                // We use Fill options to let the Grid layout engine handle the size, 
                // ensuring it stays maximized even if parent resizes.
                parentContainer.InvalidateMeasure(); // Force layout update just in case

                this.TranslationX = 0;
                this.TranslationY = 0;
                this.HorizontalOptions = LayoutOptions.Fill;
                this.VerticalOptions = LayoutOptions.Fill;

                // Span all columns/rows if parent is Grid
                if (Parent is Grid parentGrid)
                {
                    Grid.SetColumn(this, 0);
                    Grid.SetRow(this, 0);
                    Grid.SetColumnSpan(this, parentGrid.ColumnDefinitions.Count > 0 ? parentGrid.ColumnDefinitions.Count : 1);
                    Grid.SetRowSpan(this, parentGrid.RowDefinitions.Count > 0 ? parentGrid.RowDefinitions.Count : 1);
                }

                // Clear explicit requests so Fill works
                this.WidthRequest = -1;
                this.HeightRequest = -1;

                _isMaximized = true;

                // Disable resize handles
                if (_resizeGrid != null) _resizeGrid.IsVisible = false;
                if (_visualRoot != null) _visualRoot.StrokeShape = new RoundRectangle { CornerRadius = 0 };
            }
        }

        public void Close(bool force = false)
        {
            if (!force)
            {
                var a = new CloseEventArgs { Cancel = false };
                CloseClicked?.Invoke(this, a);
                if (a.Cancel) return;
            }
            if (Parent is Layout layout)
            {
                layout.Children.Remove(this);
            }
        }

        /// <summary>
        /// Moves this MultiWindowItem into a new independent OS window.
        /// Supported on Windows and MacCatalyst.
        /// </summary>
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("maccatalyst")]
        public void OpenInNewWindow()
        {
#if WINDOWS || MACCATALYST
            // 0. Save state
            _preWindowX = this.TranslationX;
            _preWindowY = this.TranslationY;
            _preWindowWidth = this.Width;
            _preWindowHeight = this.Height;

            _originalParent = Parent;
            _isInWindowMode = true;
            _hostWindow = null;

            // 1. Detach from current parent
            if (Parent is Layout layout)
            {
                layout.Children.Remove(this);
            }
            else if (Parent is ContentView contentView)
            {
                contentView.Content = null;
            }
            else if (Parent is ContentPage contentPage)
            {
                contentPage.Content = null;
            }

            // 2. Disable MDI-style interactions (Internal gestures)
            IsDraggable = false;
            IsResizable = false;
            // Note: We keep IsMaximizable/IsMinimizable/IsClosable true so buttons appear, 
            // but handlers delegate to Window (via _isInWindowMode check).
            
            // 2.1 Update internal chrome for Window Mode
            if (_resizeGrid != null) _resizeGrid.IsVisible = false; // OS handles resizing

            if (_visualRoot != null)
            {
                 // Remove rounded corners/border to look like a full window
                _visualRoot.StrokeThickness = 0;
                _visualRoot.StrokeShape = new RoundRectangle { CornerRadius = 0 };
            }

            // Move TitleBarGrid out of visual tree so it can be used in TitleBar control
            if (_titleBarGrid != null)
            {
                if (_titleBarGrid.Parent is Grid parentGrid)
                {
                    _originalTitleBarParent = parentGrid;
                    parentGrid.Children.Remove(_titleBarGrid);
                    if (parentGrid.RowDefinitions.Count > 0)
                    {
                        parentGrid.RowDefinitions[0].Height = new GridLength(0); // Collapse space
                    }
                }
                _titleBarGrid.IsVisible = true; // Ensure it's visible for the TitleBar
            }
            
            // Show Dock Button
            if (_dockBtn != null) _dockBtn.IsVisible = true;

            // 3. Reset transform and layout properties to fill the new window
            TranslationX = 0;
            TranslationY = 0;
            WidthRequest = -1;
            HeightRequest = -1;
            HorizontalOptions = LayoutOptions.Fill;
            VerticalOptions = LayoutOptions.Fill;
            Margin = new Thickness(0);

            // 4. Create a hosting ContentPage
            var hostingPage = new ContentPage
            {
                Content = this,
                Title = this.Title ?? "Window"
            };
            NavigationPage.SetHasNavigationBar(hostingPage, false); // Hide default MAUI Navigation Bar

            // 5. Create a new Window
            var newWindow = new Window(hostingPage)
            {
                Title = this.Title ?? "Window"
            };
            _hostWindow = newWindow;

            // Update visibility of internal buttons (hide them in window mode)
            UpdatedButtonVisibility();


            // 7. Cleanup when the OS window is closed physically
            newWindow.Destroying += (s, e) =>
            {
                PerformDock(false);

            };

            // 8. Open the window
            Application.Current?.OpenWindow(newWindow);
#endif
        }

        #endregion

        #region Navigation Methods

        /// <summary>
        /// Push a specific 'page' to the front. The current content will be pushed to back stack. 
        /// </summary>
        /// <param name="view"></param>
        public void NavigateTo(View view)
        {
            if (view == null) return;

            // If we have current content, push it to back stack
            if (Content != null)
            {
                _backStack.Push((View)Content);
            }

            // Clear forward stack on new navigation
            _forwardStack.Clear();

            // Set new content
            Content = view;

            UpdateNavigationState();
        }
        /// <summary>
        /// Go back to the prior content if any in the back stack. 
        /// </summary>
        public void GoBack()
        {
            if (_backStack.Count > 0)
            {
                if (Content != null)
                {
                    _forwardStack.Push((View)Content);
                }

                var view = _backStack.Pop();
                Content = view;
                UpdateNavigationState();
            }
        }

        /// <summary>
        /// Go forward to the next content if any in the forward stack. 
        /// </summary>
        public void GoForward()
        {
            if (_forwardStack.Count > 0)
            {
                if (Content != null)
                {
                    _backStack.Push((View)Content);
                }

                var view = _forwardStack.Pop();
                Content = view;
                UpdateNavigationState();
            }
        }

        private void UpdateNavigationState()
        {
            CanGoBack = _backStack.Count > 0;
            CanGoForward = _forwardStack.Count > 0;
        }

        #endregion

        #region Moving
        private void OnTitleBarPanUpdated(object sender, PanUpdatedEventArgs e)
        {
            if (_isMaximized || !IsDraggable) return;

            if (Parent is not VisualElement parent) return;

            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    _startX = this.TranslationX;
                    _startY = this.TranslationY;
                    break;
                case GestureStatus.Running:
                    double proposedX = _startX + e.TotalX;
                    double proposedY = _startY + e.TotalY;

                    // Calculate bounds
                    // Assuming the element is aligned Top/Left (LayoutOptions.Start),
                    // Translation corresponds to the position relative to the parent's generic Top/Left.
                    double maxX = Math.Max(0, parent.Width - this.Width);
                    double maxY = Math.Max(0, parent.Height - this.Height);

                    this.TranslationX = Math.Clamp(proposedX, 0, maxX);
                    this.TranslationY = Math.Clamp(proposedY, 0, maxY);
                    break;
                case GestureStatus.Completed:
                    break;
            }
        }
        #endregion

        #region Resizing

        private void PrepareResize()
        {
            _startX = this.TranslationX;
            _startY = this.TranslationY;
            _startWidth = this.Width;
            _startHeight = this.Height;
        }

        private void OnResizeRight(object sender, PanUpdatedEventArgs e)
        {
            if (!IsResizable) return;
            if (e.StatusType == GestureStatus.Started) PrepareResize();
            if (e.StatusType == GestureStatus.Running)
            {
                this.WidthRequest = Math.Max(100, _startWidth + e.TotalX);
            }
        }

        private void OnResizeBottom(object sender, PanUpdatedEventArgs e)
        {
            if (!IsResizable) return;
            if (e.StatusType == GestureStatus.Started) PrepareResize();
            if (e.StatusType == GestureStatus.Running)
            {
                this.HeightRequest = Math.Max(100, _startHeight + e.TotalY);
            }
        }

        private void OnResizeBottomRight(object sender, PanUpdatedEventArgs e)
        {
            if (!IsResizable) return;
            if (e.StatusType == GestureStatus.Started) PrepareResize();
            if (e.StatusType == GestureStatus.Running)
            {
                this.WidthRequest = Math.Max(100, _startWidth + e.TotalX);
                this.HeightRequest = Math.Max(100, _startHeight + e.TotalY);
            }
        }

        private void OnResizeLeft(object sender, PanUpdatedEventArgs e)
        {
            if (!IsResizable) return;
            if (e.StatusType == GestureStatus.Started) PrepareResize();
            if (e.StatusType == GestureStatus.Running)
            {
                // Moving left edge means changing X and Width
                double newWidth = Math.Max(100, _startWidth - e.TotalX);
                // If we hit min width, don't move X anymore
                if (newWidth > 100)
                {
                    this.TranslationX = _startX + e.TotalX;
                    this.WidthRequest = newWidth;
                }
            }
        }

        private void OnResizeTop(object sender, PanUpdatedEventArgs e)
        {
            if (!IsResizable) return;
            if (e.StatusType == GestureStatus.Started) PrepareResize();
            if (e.StatusType == GestureStatus.Running)
            {
                double newHeight = Math.Max(100, _startHeight - e.TotalY);
                if (newHeight > 100)
                {
                    this.TranslationY = _startY + e.TotalY;
                    this.HeightRequest = newHeight;
                }
            }
        }

        // Combinations
        private void OnResizeTopLeft(object sender, PanUpdatedEventArgs e)
        {
            if (!IsResizable) return;
            if (e.StatusType == GestureStatus.Started) PrepareResize();
            if (e.StatusType == GestureStatus.Running)
            {
                // Top Logic
                double newHeight = Math.Max(100, _startHeight - e.TotalY);
                if (newHeight > 100)
                {
                    this.TranslationY = _startY + e.TotalY;
                    this.HeightRequest = newHeight;
                }
                // Left Logic
                double newWidth = Math.Max(100, _startWidth - e.TotalX);
                if (newWidth > 100)
                {
                    this.TranslationX = _startX + e.TotalX;
                    this.WidthRequest = newWidth;
                }
            }
        }

        private void OnResizeTopRight(object sender, PanUpdatedEventArgs e)
        {
            if (!IsResizable) return;
            if (e.StatusType == GestureStatus.Started) PrepareResize();
            if (e.StatusType == GestureStatus.Running)
            {
                // Top Logic
                double newHeight = Math.Max(100, _startHeight - e.TotalY);
                if (newHeight > 100)
                {
                    this.TranslationY = _startY + e.TotalY;
                    this.HeightRequest = newHeight;
                }
                // Right Logic
                this.WidthRequest = Math.Max(100, _startWidth + e.TotalX);
            }
        }

        private void OnResizeBottomLeft(object sender, PanUpdatedEventArgs e)
        {
            if (!IsResizable) return;
            if (e.StatusType == GestureStatus.Started) PrepareResize();
            if (e.StatusType == GestureStatus.Running)
            {
                // Bottom Logic
                this.HeightRequest = Math.Max(100, _startHeight + e.TotalY);
                // Left Logic
                double newWidth = Math.Max(100, _startWidth - e.TotalX);
                if (newWidth > 100)
                {
                    this.TranslationX = _startX + e.TotalX;
                    this.WidthRequest = newWidth;
                }
            }
        }
        #endregion
    }

    public class CloseEventArgs : EventArgs
    {
        public bool Cancel { get; set; }
    }
}
