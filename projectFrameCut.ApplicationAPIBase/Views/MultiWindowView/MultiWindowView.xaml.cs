using System;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Xaml.Internals;
using Microsoft.Maui.Graphics;
#pragma warning disable CS0108 // avoid hiding members cause XAML compatibility issues

namespace projectFrameCut.ApplicationAPIBase.Views.MultiWindowView
{
    public enum WindowSnapZone
    {
        None,
        LeftHalf,
        RightHalf,
        TopHalf,
        BottomHalf,
        TopLeftQuarter,
        TopRightQuarter,
        BottomLeftQuarter,
        BottomRightQuarter,
        Full
    }

    public enum WindowArrangeMode
    {
        Grid,
        Cascade,
        Horizontal,
        Vertical
    }

    public enum TaskbarVisibilityMode
    {
        /// <summary>Taskbar is never shown.</summary>
        AlwaysHidden,
        /// <summary>Taskbar is shown only when at least one window is minimized.</summary>
        Auto,
        /// <summary>Taskbar is always shown, even when empty.</summary>
        AlwaysVisible
    }

    /// <summary>
    /// A container for all <see cref="MultiWindowItem"/>.
    /// </summary>
    public partial class MultiWindowView : Grid
    {
        public event EventHandler<MultiWindowItem>? WindowAdded;
        public event EventHandler<MultiWindowItem>? WindowClosed;
        public event EventHandler<MultiWindowItem>? WindowFocused;

        private bool Initialized { get; set; } = false;

        /// <summary>
        /// Allow automatic add the window to the window collection when the window provided to <see cref="ActiveWindow"/> is not in the collection.
        /// </summary>
        public static bool StrictMode { get; set; } = false;

        public static readonly BindableProperty IsWindowSnappingEnabledProperty =
            BindableProperty.Create(nameof(IsWindowSnappingEnabled), typeof(bool), typeof(MultiWindowView), true);

        public static readonly BindableProperty IsSnapPreviewEnabledProperty =
            BindableProperty.Create(nameof(IsSnapPreviewEnabled), typeof(bool), typeof(MultiWindowView), true);

        public static readonly BindableProperty IsAutoArrangeEnabledProperty =
            BindableProperty.Create(nameof(IsAutoArrangeEnabled), typeof(bool), typeof(MultiWindowView), true);

        public static readonly BindableProperty AutoArrangeOnWindowAddedProperty =
            BindableProperty.Create(nameof(AutoArrangeOnWindowAdded), typeof(bool), typeof(MultiWindowView), false);

        public static readonly BindableProperty SnapThresholdProperty =
            BindableProperty.Create(nameof(SnapThreshold), typeof(double), typeof(MultiWindowView), 8d);

        public static readonly BindableProperty AutoArrangeModeProperty =
            BindableProperty.Create(nameof(AutoArrangeMode), typeof(WindowArrangeMode), typeof(MultiWindowView), WindowArrangeMode.Grid);

        public static readonly BindableProperty TaskbarVisibilityProperty =
            BindableProperty.Create(nameof(TaskbarVisibility), typeof(TaskbarVisibilityMode), typeof(MultiWindowView), TaskbarVisibilityMode.Auto, propertyChanged: OnTaskbarVisibilityChanged);

        public static readonly BindableProperty TaskbarHeightProperty =
            BindableProperty.Create(nameof(TaskbarHeight), typeof(double), typeof(MultiWindowView), 40d, propertyChanged: OnTaskbarLayoutChanged);

        /// <summary>
        /// Enable or disable edge/corner snapping while dragging windows.
        /// </summary>
        public bool IsWindowSnappingEnabled
        {
            get => (bool)GetValue(IsWindowSnappingEnabledProperty);
            set => SetValue(IsWindowSnappingEnabledProperty, value);
        }

        /// <summary>
        /// Show or hide visual snap preview overlay while dragging.
        /// </summary>
        public bool IsSnapPreviewEnabled
        {
            get => (bool)GetValue(IsSnapPreviewEnabledProperty);
            set => SetValue(IsSnapPreviewEnabledProperty, value);
        }

        /// <summary>
        /// Enable auto arrangement when overlap is detected or after a snap operation.
        /// </summary>
        public bool IsAutoArrangeEnabled
        {
            get => (bool)GetValue(IsAutoArrangeEnabledProperty);
            set => SetValue(IsAutoArrangeEnabledProperty, value);
        }

        /// <summary>
        /// Auto arrange all windows when a new window is added.
        /// </summary>
        public bool AutoArrangeOnWindowAdded
        {
            get => (bool)GetValue(AutoArrangeOnWindowAddedProperty);
            set => SetValue(AutoArrangeOnWindowAddedProperty, value);
        }

        /// <summary>
        /// Pixel threshold used to detect when a window should snap to a screen edge.
        /// </summary>
        public double SnapThreshold
        {
            get => (double)GetValue(SnapThresholdProperty);
            set => SetValue(SnapThresholdProperty, value);
        }

        /// <summary>
        /// Preferred mode used by automatic arrangement routines.
        /// </summary>
        public WindowArrangeMode AutoArrangeMode
        {
            get => (WindowArrangeMode)GetValue(AutoArrangeModeProperty);
            set => SetValue(AutoArrangeModeProperty, value);
        }

        /// <summary>
        /// Controls when the taskbar is visible: always hidden, only when minimized windows exist (Auto), or always visible.
        /// </summary>
        public TaskbarVisibilityMode TaskbarVisibility
        {
            get => (TaskbarVisibilityMode)GetValue(TaskbarVisibilityProperty);
            set => SetValue(TaskbarVisibilityProperty, value);
        }

        /// <summary>
        /// Convenience property. Returns true when <see cref="TaskbarVisibility"/> is not <see cref="TaskbarVisibilityMode.AlwaysHidden"/>.
        /// </summary>
        public bool IsTaskbarEnabled => TaskbarVisibility != TaskbarVisibilityMode.AlwaysHidden;

        /// <summary>
        /// Height of the taskbar in pixels.
        /// </summary>
        public double TaskbarHeight
        {
            get => (double)GetValue(TaskbarHeightProperty);
            set => SetValue(TaskbarHeightProperty, value);
        }

        private MultiWindowItem? _activeWindow;

        /// <summary>
        /// Represents the currently active window.
        /// When set, the specified window will be brought to the front and receive focus.
        /// </summary>
        public MultiWindowItem ActiveWindow
        {
            get => _activeWindow ?? Windows.FirstOrDefault() ?? throw new InvalidOperationException("No active window is available.");
            set
            {
                if (!Windows.Contains(value, new MultiWindowItemComparer()))
                {
                    if (StrictMode)
                    {
                        throw new InvalidOperationException($"Window {value.Title} ({value.WindowID}) is not part of the collection.");
                    }
                    else
                    {
                        AddWindow(value);
                    }
                }

                if (_activeWindow is not null && MultiWindowItem.ReferenceEquals(_activeWindow, value))
                {
                    return;
                }

                BringToFront(value);
            }
        }

        /// <summary>
        /// Collection of all <see cref="MultiWindowItem"/>.
        /// </summary>
        /// <remarks>
        /// To open a window, use <see cref="AddWindow(MultiWindowItem)"/> to the collection. 
        /// To close a window, use <see cref="CloseWindow(MultiWindowItem,bool)"/>.
        /// </remarks>
        public IReadOnlyList<MultiWindowItem> Windows => base.Children.OfType<MultiWindowItem>().ToList();

        /// <summary>
        /// <b>DO NOT manipulate this collection directly.</b>
        /// </summary>
        ///<remarks>
        /// To open a window, use <see cref="AddWindow(MultiWindowItem)"/> to the collection. 
        /// To close a window, use <see cref="CloseWindow(MultiWindowItem,bool)"/>.
        /// </remarks>
        public IList<IView> Children => base.Children;

        private readonly HashSet<MultiWindowItem> _managedWindows = new();
        private readonly Dictionary<MultiWindowItem, WindowSnapZone> _snapStates = new();
        private readonly Dictionary<MultiWindowItem, Border> _taskbarItems = new();
        private MultiWindowItem? _snapTarget;
        private WindowSnapZone _pendingSnapZone = WindowSnapZone.None;
        private Border? _taskbar;
        private HorizontalStackLayout? _taskbarItemsContainer;

        public MultiWindowView()
        {
            Initialized = false;
            InitializeComponent();
            this.ChildAdded += OnChildAdded;
            this.ChildRemoved += OnChildRemoved;
            this.Unloaded += OnUnloaded;
            this.SizeChanged += OnContainerSizeChanged;

            if (SnapPreviewOverlay != null)
            {
                SnapPreviewOverlay.ZIndex = 10000;
            }

            _taskbar = Taskbar;
            _taskbarItemsContainer = TaskbarItemsContainer;
            ApplyTaskbarVisibility();

            Initialized = true;
        }

        private void OnUnloaded(object? sender, EventArgs e)
        {
            foreach (var item in _managedWindows.ToArray())
            {
                item.PropertyChanged -= OnTaskbarWindowPropertyChanged;
                item.PropertyChanged -= OnItemPropertyChanged;
                item.Close(true);
            }
            _managedWindows.Clear();
            _snapStates.Clear();
            _taskbarItems.Clear();
            _taskbarItemsContainer?.Children.Clear();
            _snapTarget = null;
            _pendingSnapZone = WindowSnapZone.None;
            HideSnapPreview();
        }

        private void OnContainerSizeChanged(object? sender, EventArgs e)
        {
            if (Width <= 0 || Height <= 0) return;

            foreach (var pair in _snapStates.ToArray())
            {
                if (!Children.Contains(pair.Key))
                {
                    _snapStates.Remove(pair.Key);
                    continue;
                }

                ApplySnap(pair.Key, pair.Value, rememberState: true, bringToFront: false);
            }
        }

        private void OnItemCloseClicked(object? sender, CloseEventArgs e)
        {
            if (!e.Cancel && sender is MultiWindowItem item)
            {
                _managedWindows.Remove(item);
                _snapStates.Remove(item);
                RemoveTaskbarItem(item);
                item.CloseClicked -= OnItemCloseClicked;
                item.MinimizeClicked -= OnItemMinimizeClicked;
                item.PropertyChanged -= OnItemPropertyChanged;
            }
        }

        private void OnItemActivated(object? sender, EventArgs e)
        {
            if (sender is not MultiWindowItem item) return;

            BringToFront(item);
            WindowFocused?.Invoke(this, item);
        }

        private void OnItemDragStarted(object? sender, WindowBoundsChangedEventArgs e)
        {
            if (sender is not MultiWindowItem item) return;

            _snapStates.Remove(item);
            if (_snapTarget is not null && MultiWindowItem.ReferenceEquals(_snapTarget, item))
            {
                _snapTarget = null;
                _pendingSnapZone = WindowSnapZone.None;
            }
            HideSnapPreview();
        }

        private void OnItemDragging(object? sender, WindowBoundsChangedEventArgs e)
        {
            if (sender is not MultiWindowItem item) return;

            if (!IsWindowSnappingEnabled || Width <= 0 || Height <= 0)
            {
                HideSnapPreview();
                return;
            }

            var zone = DetectSnapZone(e);
            _snapTarget = item;
            _pendingSnapZone = zone;
            UpdateSnapPreview(zone);
        }

        private void OnItemDragCompleted(object? sender, WindowBoundsChangedEventArgs e)
        {
            if (sender is not MultiWindowItem item) return;

            var zone = (_snapTarget is not null && MultiWindowItem.ReferenceEquals(_snapTarget, item)) ? _pendingSnapZone : WindowSnapZone.None;

            _snapTarget = null;
            _pendingSnapZone = WindowSnapZone.None;
            HideSnapPreview();

            if (!IsWindowSnappingEnabled || zone == WindowSnapZone.None)
            {
                if (IsAutoArrangeEnabled && ShouldAutoArrange(item))
                {
                    ArrangeWindows(AutoArrangeMode);
                }
                return;
            }

            ApplySnap(item, zone, rememberState: true, bringToFront: true);

            if (IsAutoArrangeEnabled)
            {
                AutoArrangeRemainingWindows(item, zone);
            }
        }

        private bool _isHandlingMinimize;

        private void OnItemMinimizeClicked(object? sender, EventArgs e)
        {
            // The actual taskbar logic is handled by OnItemPropertyChanged
            // listening for IsMinimized changes. But for the button click,
            // Minimize() is called AFTER this event. OnItemPropertyChanged
            // will catch the IsMinimized change and handle taskbar logic.
        }

        private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is not MultiWindowItem item) return;
            if (!IsTaskbarEnabled) return;
            if (e.PropertyName != nameof(MultiWindowItem.IsMinimized)) return;
            if (_isHandlingMinimize) return;

            _isHandlingMinimize = true;
            try
            {
                if (item.IsMinimized)
                {
                    MinimizeWindowToTaskbar(item);
                }
                else if (_taskbarItems.ContainsKey(item))
                {
                    // Restore: the Minimize() call already toggled the state.
                    // Only act if the window is actually tracked in the taskbar.
                    item.IsVisible = true;
                    RemoveTaskbarItem(item);
                    BringToFront(item);
                }
            }
            finally
            {
                _isHandlingMinimize = false;
            }
        }

        private void MinimizeWindowToTaskbar(MultiWindowItem item)
        {
            item.IsVisible = false;
            AddTaskbarItem(item);
        }

        private void RestoreWindowFromTaskbar(MultiWindowItem item)
        {
            item.IsVisible = true;
            if (item.IsMinimized)
            {
                _isHandlingMinimize = true;
                item.Minimize();
                _isHandlingMinimize = false;
            }
            RemoveTaskbarItem(item);
            BringToFront(item);
        }

        private void AddTaskbarItem(MultiWindowItem item)
        {
            if (_taskbarItemsContainer is null) return;
            if (_taskbarItems.ContainsKey(item)) return;

            var label = new Label
            {
                Text = item.Title,
                TextColor = Colors.White,
                FontSize = 12,
                VerticalOptions = LayoutOptions.Center,
                Margin = new Thickness(8, 0),
                LineBreakMode = LineBreakMode.TailTruncation,
                MaxLines = 1
            };

            var border = new Border
            {
                Content = label,
                BackgroundColor = Color.FromArgb("#3C3C40"),
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 4 },
                Stroke = Colors.Transparent,
                HeightRequest = 32,
                MinimumWidthRequest = 120,
                MaximumWidthRequest = 200,
                VerticalOptions = LayoutOptions.Center,
                Margin = new Thickness(2, 0),
            };

            var tap = new TapGestureRecognizer();
            tap.Tapped += (s, e) => OnTaskbarItemTapped(item);
            border.GestureRecognizers.Add(tap);

            _taskbarItems[item] = border;
            _taskbarItemsContainer.Children.Add(border);

            // Track title changes
            item.PropertyChanged += OnTaskbarWindowPropertyChanged;

            UpdateTaskbarHighlight();
            UpdateTaskbarDynamicVisibility();
        }

        private void RemoveTaskbarItem(MultiWindowItem item)
        {
            item.PropertyChanged -= OnTaskbarWindowPropertyChanged;

            if (_taskbarItems.TryGetValue(item, out var border))
            {
                _taskbarItems.Remove(item);
                if (_taskbarItemsContainer is not null)
                {
                    _taskbarItemsContainer.Children.Remove(border);
                }
                UpdateTaskbarDynamicVisibility();
            }
        }

        private void OnTaskbarWindowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is not MultiWindowItem item) return;
            if (e.PropertyName == nameof(MultiWindowItem.Title))
            {
                if (_taskbarItems.TryGetValue(item, out var border) && border.Content is Label label)
                {
                    label.Text = item.Title;
                }
            }
        }

        private void OnTaskbarItemTapped(MultiWindowItem item)
        {
            RestoreWindowFromTaskbar(item);
            ActiveWindow = item;
        }

        private void UpdateTaskbarHighlight()
        {
            if (_taskbar is null) return;

            foreach (var (window, border) in _taskbarItems)
            {
                var isActive = _activeWindow is not null && MultiWindowItem.ReferenceEquals(_activeWindow, window);
                border.BackgroundColor = isActive
                    ? Color.FromArgb("#505055")
                    : Color.FromArgb("#3C3C40");
            }
        }

        private void ApplyTaskbarVisibility()
        {
            if (_taskbar is null) return;

            switch (TaskbarVisibility)
            {
                case TaskbarVisibilityMode.AlwaysHidden:
                    RestoreAllMinimizedWindows();
                    SetTaskbarVisible(false);
                    break;
                case TaskbarVisibilityMode.AlwaysVisible:
                    SetTaskbarVisible(true);
                    break;
                case TaskbarVisibilityMode.Auto:
                    SetTaskbarVisible(_taskbarItems.Count > 0);
                    break;
            }

            // Re-apply snaps to adjust for taskbar height change
            foreach (var pair in _snapStates.ToArray())
            {
                if (!Children.Contains(pair.Key))
                {
                    _snapStates.Remove(pair.Key);
                    continue;
                }
                ApplySnap(pair.Key, pair.Value, rememberState: true, bringToFront: false);
            }
        }

        private void UpdateTaskbarDynamicVisibility()
        {
            if (_taskbar is null) return;
            if (TaskbarVisibility != TaskbarVisibilityMode.Auto) return;

            var shouldShow = _taskbarItems.Count > 0;
            if (_taskbar.IsVisible != shouldShow)
            {
                SetTaskbarVisible(shouldShow);

                // Re-apply snaps when taskbar toggles in Auto mode
                foreach (var pair in _snapStates.ToArray())
                {
                    if (!Children.Contains(pair.Key))
                    {
                        _snapStates.Remove(pair.Key);
                        continue;
                    }
                    ApplySnap(pair.Key, pair.Value, rememberState: true, bringToFront: false);
                }
            }
        }

        private void SetTaskbarVisible(bool visible)
        {
            if (_taskbar is null) return;
            _taskbar.IsVisible = visible;
            _taskbar.HeightRequest = visible ? TaskbarHeight : 0;
        }

        private void RestoreAllMinimizedWindows()
        {
            foreach (var item in _taskbarItems.Keys.ToArray())
            {
                RestoreWindowFromTaskbar(item);
                if (item.IsMinimized) item.Minimize();
            }
            _taskbarItems.Clear();
            if (_taskbarItemsContainer is not null)
                _taskbarItemsContainer.Children.Clear();
        }

        private static void OnTaskbarVisibilityChanged(BindableObject bindable, object oldValue, object newValue)
        {
            if (bindable is MultiWindowView view)
            {
                view.ApplyTaskbarVisibility();
            }
        }

        private static void OnTaskbarLayoutChanged(BindableObject bindable, object oldValue, object newValue)
        {
            if (bindable is MultiWindowView view)
            {
                view.ApplyTaskbarVisibility();
            }
        }

        private double GetEffectiveMdiHeight()
        {
            var h = Math.Max(0, Height);
            if (_taskbar?.IsVisible == true)
            {
                h = Math.Max(0, h - TaskbarHeight);
            }
            return h;
        }

        private Rect GetMdiArea()
        {
            return new Rect(0, 0, Math.Max(0, Width), GetEffectiveMdiHeight());
        }

        private void OnChildAdded(object? sender, ElementEventArgs e)
        {
            if (e.Element is MultiWindowItem item)
            {
                if (_managedWindows.Add(item))
                {
                    item.CloseClicked += OnItemCloseClicked;
                    item.Activated += OnItemActivated;
                    item.MinimizeClicked += OnItemMinimizeClicked;
                    item.PropertyChanged += OnItemPropertyChanged;
                    item.DragStarted += OnItemDragStarted;
                    item.Dragging += OnItemDragging;
                    item.DragCompleted += OnItemDragCompleted;
                }

                WindowAdded?.Invoke(this, item);

                if (AutoArrangeOnWindowAdded && Windows.Count > 1)
                {
                    Dispatcher.Dispatch(() => ArrangeWindows(AutoArrangeMode));
                }
            }
        }

        private void OnChildRemoved(object? sender, ElementEventArgs e)
        {
            if (e.Element is MultiWindowItem item)
            {
                item.CloseClicked -= OnItemCloseClicked;
                item.Activated -= OnItemActivated;
                item.MinimizeClicked -= OnItemMinimizeClicked;
                item.PropertyChanged -= OnItemPropertyChanged;
                item.DragStarted -= OnItemDragStarted;
                item.Dragging -= OnItemDragging;
                item.DragCompleted -= OnItemDragCompleted;
                _managedWindows.Remove(item);
                _snapStates.Remove(item);
                RemoveTaskbarItem(item);

                if (_activeWindow is not null && MultiWindowItem.ReferenceEquals(_activeWindow, item))
                {
                    _activeWindow = Windows.LastOrDefault();
                }

                WindowClosed?.Invoke(this, item);
            }
        }

        /// <summary>
        /// Bring the specific window to the front. The window will be displayed on top of the existing windows.
        /// </summary>
        /// <param name="item"></param>
        public void BringToFront(MultiWindowItem item)
        {
            int maxZ = 0;
            foreach (var child in Windows)
            {
                if (!MultiWindowItem.ReferenceEquals(child, item) && child.ZIndex > maxZ)
                {
                    maxZ = child.ZIndex;
                }
            }

            item.ZIndex = maxZ + 1;
            _activeWindow = item;

            if (SnapPreviewOverlay != null)
            {
                SnapPreviewOverlay.ZIndex = item.ZIndex + 1;
            }

            UpdateTaskbarHighlight();
        }

        private static Rect CreateRectFromWindow(MultiWindowItem item)
        {
            var width = item.Width > 0 ? item.Width : Math.Max(0, item.WidthRequest);
            var height = item.Height > 0 ? item.Height : Math.Max(0, item.HeightRequest);
            return new Rect(item.TranslationX, item.TranslationY, width, height);
        }

        private static double GetIntersectionArea(Rect a, Rect b)
        {
            var left = Math.Max(a.Left, b.Left);
            var top = Math.Max(a.Top, b.Top);
            var right = Math.Min(a.Right, b.Right);
            var bottom = Math.Min(a.Bottom, b.Bottom);

            var w = right - left;
            var h = bottom - top;
            if (w <= 0 || h <= 0) return 0;
            return w * h;
        }

        private bool ShouldAutoArrange(MultiWindowItem movedItem)
        {
            var movedBounds = CreateRectFromWindow(movedItem);
            var movedArea = movedBounds.Width * movedBounds.Height;
            if (movedArea <= 0) return false;

            foreach (var other in Windows)
            {
                if (MultiWindowItem.ReferenceEquals(other, movedItem)) continue;
                if (!other.IsVisible) continue;

                var intersection = GetIntersectionArea(movedBounds, CreateRectFromWindow(other));
                if (intersection <= 0) continue;

                // Trigger auto arrange only when overlap is meaningful to avoid aggressive relayout.
                if (intersection / movedArea >= 0.2)
                {
                    return true;
                }
            }

            return false;
        }

        private WindowSnapZone DetectSnapZone(WindowBoundsChangedEventArgs e)
        {
            var threshold = Math.Max(8, SnapThreshold);
            var mdiHeight = GetEffectiveMdiHeight();
            var nearLeft = e.X <= threshold;
            var nearRight = e.X + e.Width >= Width - threshold;
            var nearTop = e.Y <= threshold;
            var nearBottom = e.Y + e.Height >= mdiHeight - threshold;

            if (nearTop && nearLeft) return WindowSnapZone.TopLeftQuarter;
            if (nearTop && nearRight) return WindowSnapZone.TopRightQuarter;
            if (nearBottom && nearLeft) return WindowSnapZone.BottomLeftQuarter;
            if (nearBottom && nearRight) return WindowSnapZone.BottomRightQuarter;
            if (nearTop) return WindowSnapZone.Full;
            if (nearLeft) return WindowSnapZone.LeftHalf;
            if (nearRight) return WindowSnapZone.RightHalf;
            if (nearBottom) return WindowSnapZone.BottomHalf;

            return WindowSnapZone.None;
        }

        private Rect GetSnapBounds(WindowSnapZone zone)
        {
            var area = GetMdiArea();
            var w = area.Width;
            var h = area.Height;
            var halfW = w / 2;
            var halfH = h / 2;

            return zone switch
            {
                WindowSnapZone.LeftHalf => new Rect(0, 0, halfW, h),
                WindowSnapZone.RightHalf => new Rect(halfW, 0, halfW, h),
                WindowSnapZone.TopHalf => new Rect(0, 0, w, halfH),
                WindowSnapZone.BottomHalf => new Rect(0, halfH, w, halfH),
                WindowSnapZone.TopLeftQuarter => new Rect(0, 0, halfW, halfH),
                WindowSnapZone.TopRightQuarter => new Rect(halfW, 0, halfW, halfH),
                WindowSnapZone.BottomLeftQuarter => new Rect(0, halfH, halfW, halfH),
                WindowSnapZone.BottomRightQuarter => new Rect(halfW, halfH, halfW, halfH),
                WindowSnapZone.Full => new Rect(0, 0, w, h),
                _ => Rect.Zero,
            };
        }

        private Rect GetAssistBoundsForOtherWindows(WindowSnapZone snappedZone)
        {
            var area = GetMdiArea();
            var w = area.Width;
            var h = area.Height;
            var halfW = w / 2;
            var halfH = h / 2;

            return snappedZone switch
            {
                WindowSnapZone.LeftHalf => new Rect(halfW, 0, halfW, h),
                WindowSnapZone.RightHalf => new Rect(0, 0, halfW, h),
                WindowSnapZone.TopHalf => new Rect(0, halfH, w, halfH),
                WindowSnapZone.BottomHalf => new Rect(0, 0, w, halfH),
                WindowSnapZone.TopLeftQuarter => new Rect(halfW, 0, halfW, h),
                WindowSnapZone.TopRightQuarter => new Rect(0, 0, halfW, h),
                WindowSnapZone.BottomLeftQuarter => new Rect(halfW, 0, halfW, h),
                WindowSnapZone.BottomRightQuarter => new Rect(0, 0, halfW, h),
                _ => Rect.Zero,
            };
        }

        private void SetWindowBounds(MultiWindowItem item, Rect bounds, bool clearSnapState)
        {
            item.HorizontalOptions = LayoutOptions.Start;
            item.VerticalOptions = LayoutOptions.Start;
            item.Margin = new Thickness(0);
            item.TranslationX = bounds.X;
            item.TranslationY = bounds.Y;
            item.WidthRequest = bounds.Width;
            item.HeightRequest = bounds.Height;

            if (clearSnapState)
            {
                _snapStates.Remove(item);
            }
        }

        private void ApplySnap(MultiWindowItem item, WindowSnapZone zone, bool rememberState, bool bringToFront)
        {
            if (zone == WindowSnapZone.None) return;

            var bounds = GetSnapBounds(zone);
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            SetWindowBounds(item, bounds, clearSnapState: false);

            if (rememberState)
            {
                _snapStates[item] = zone;
            }

            if (bringToFront)
            {
                BringToFront(item);
            }
        }

        private void UpdateSnapPreview(WindowSnapZone zone)
        {
            if (SnapPreviewOverlay == null) return;

            if (!IsSnapPreviewEnabled || zone == WindowSnapZone.None)
            {
                HideSnapPreview();
                return;
            }

            var bounds = GetSnapBounds(zone);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                HideSnapPreview();
                return;
            }

            SnapPreviewOverlay.WidthRequest = bounds.Width;
            SnapPreviewOverlay.HeightRequest = bounds.Height;
            SnapPreviewOverlay.TranslationX = bounds.X;
            SnapPreviewOverlay.TranslationY = bounds.Y;
            SnapPreviewOverlay.ZIndex = Windows.Select(w => w.ZIndex).DefaultIfEmpty(0).Max() + 1;
            SnapPreviewOverlay.IsVisible = true;
        }

        private void HideSnapPreview()
        {
            if (SnapPreviewOverlay == null) return;

            SnapPreviewOverlay.IsVisible = false;
            SnapPreviewOverlay.WidthRequest = 0;
            SnapPreviewOverlay.HeightRequest = 0;
            SnapPreviewOverlay.TranslationX = 0;
            SnapPreviewOverlay.TranslationY = 0;
        }

        private void AutoArrangeRemainingWindows(MultiWindowItem snappedItem, WindowSnapZone snappedZone)
        {
            var remaining = Windows.Where(w => !MultiWindowItem.ReferenceEquals(w, snappedItem) && w.IsVisible).ToList();
            if (remaining.Count == 0) return;

            var assistBounds = GetAssistBoundsForOtherWindows(snappedZone);
            if (assistBounds.Width <= 0 || assistBounds.Height <= 0) return;

            ArrangeWindowsInternal(remaining, assistBounds, AutoArrangeMode);
        }

        /// <summary>
        /// Add a new window to the multi-window view. The window will be displayed on top of the existing windows.
        /// </summary>
        /// <param name="window"></param>
        public void AddWindow(MultiWindowItem window)
        {
            this.Children.Add(window);
        }

        /// <summary>
        /// Snap a window to one of the built-in snap zones.
        /// </summary>
        /// <param name="window">The window to snap.</param>
        /// <param name="zone">The snap zone to apply.</param>
        /// <param name="bringToFront">Whether the snapped window should become active.</param>
        /// <returns>True when the snap was applied.</returns>
        public bool SnapWindow(MultiWindowItem window, WindowSnapZone zone, bool bringToFront = true)
        {
            if (window is null || zone == WindowSnapZone.None || Width <= 0 || Height <= 0)
            {
                return false;
            }

            if (!Windows.Any(w => MultiWindowItem.ReferenceEquals(w, window)))
            {
                return false;
            }

            ApplySnap(window, zone, rememberState: true, bringToFront: bringToFront);
            return true;
        }

        /// <summary>
        /// Arrange all windows using <see cref="AutoArrangeMode"/>.
        /// </summary>
        public void ArrangeWindows()
        {
            ArrangeWindows(AutoArrangeMode);
        }

        /// <summary>
        /// Arrange all windows in the specified mode.
        /// </summary>
        /// <param name="mode">The arrangement mode.</param>
        public void ArrangeWindows(WindowArrangeMode mode)
        {
            var windows = Windows.Where(w => w.IsVisible).ToList();
            if (windows.Count == 0 || Width <= 0 || Height <= 0) return;

            ArrangeWindowsInternal(windows, GetMdiArea(), mode);
        }

        private void ArrangeWindowsInternal(IReadOnlyList<MultiWindowItem> windows, Rect area, WindowArrangeMode mode)
        {
            if (windows.Count == 0 || area.Width <= 0 || area.Height <= 0) return;

            const double gap = 8;

            switch (mode)
            {
                case WindowArrangeMode.Cascade:
                {
                    var windowWidth = Math.Max(220, area.Width * 0.68);
                    var windowHeight = Math.Max(160, area.Height * 0.68);
                    var stepX = Math.Max(24, area.Width * 0.05);
                    var stepY = Math.Max(24, area.Height * 0.05);
                    var maxOffsetX = Math.Max(0, area.Width - windowWidth - gap);
                    var maxOffsetY = Math.Max(0, area.Height - windowHeight - gap);

                    for (int i = 0; i < windows.Count; i++)
                    {
                        var x = area.X + gap + Math.Min(i * stepX, maxOffsetX);
                        var y = area.Y + gap + Math.Min(i * stepY, maxOffsetY);
                        SetWindowBounds(windows[i], new Rect(x, y, windowWidth, windowHeight), clearSnapState: true);
                        BringToFront(windows[i]);
                    }
                    break;
                }
                case WindowArrangeMode.Horizontal:
                {
                    var count = windows.Count;
                    var cellWidth = area.Width / count;
                    for (int i = 0; i < count; i++)
                    {
                        var x = area.X + i * cellWidth + gap;
                        var y = area.Y + gap;
                        var width = Math.Max(120, cellWidth - (gap * 2));
                        var height = Math.Max(100, area.Height - (gap * 2));
                        SetWindowBounds(windows[i], new Rect(x, y, width, height), clearSnapState: true);
                        BringToFront(windows[i]);
                    }
                    break;
                }
                case WindowArrangeMode.Vertical:
                {
                    var count = windows.Count;
                    var cellHeight = area.Height / count;
                    for (int i = 0; i < count; i++)
                    {
                        var x = area.X + gap;
                        var y = area.Y + i * cellHeight + gap;
                        var width = Math.Max(120, area.Width - (gap * 2));
                        var height = Math.Max(100, cellHeight - (gap * 2));
                        SetWindowBounds(windows[i], new Rect(x, y, width, height), clearSnapState: true);
                        BringToFront(windows[i]);
                    }
                    break;
                }
                default:
                {
                    var count = windows.Count;
                    var cols = (int)Math.Ceiling(Math.Sqrt(count));
                    var rows = (int)Math.Ceiling(count / (double)cols);
                    var cellWidth = area.Width / cols;
                    var cellHeight = area.Height / rows;

                    for (int i = 0; i < count; i++)
                    {
                        var row = i / cols;
                        var col = i % cols;
                        var x = area.X + col * cellWidth + gap;
                        var y = area.Y + row * cellHeight + gap;
                        var width = Math.Max(140, cellWidth - (gap * 2));
                        var height = Math.Max(110, cellHeight - (gap * 2));

                        SetWindowBounds(windows[i], new Rect(x, y, width, height), clearSnapState: true);
                        BringToFront(windows[i]);
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// Close the specific window. The window will be removed from the multi-window view.
        /// </summary>
        /// <param name="window"></param>
        public void CloseWindow(MultiWindowItem window, bool force = false)
        {
            window.Close(force);
            if (Children.Contains(window))
            {
                Children.Remove(window);
            }
        }

        /// <summary>
        /// <b>This method is for compatibility purposes only. DO NOT use it in your code.</b>
        /// use <see cref="AddWindow(MultiWindowItem)"/> instead to add a window to this multi-window view.
        /// </summary>
        /// <remarks>
        /// This method also can add a window to this multi-window view. 
        /// The window must be of type <see cref="MultiWindowItem"/>. The window will be displayed on top of the existing windows.
        /// </remarks>
        /// <param name="children"></param>
        /// <exception cref="InvalidOperationException">when the window is not a <see cref="MultiWindowItem"/>.</exception>
        [Obsolete("This method is for compatibility purposes only. Use AddWindow(MultiWindowItem) instead to add a window to this multi-window view.",false)]
        public void Add(IView children)
        {
            if (children is MultiWindowItem item)
            {
                AddWindow(item);
            }
            else
            {
                if (Initialized)
                {
                    throw new InvalidOperationException("Only MultiWindowItem can be added to MultiWindowView.");
                }
                else
                {
                    base.Children.Add(children);
                }
            }
        }

        /// <summary>
        /// <b>This method is for compatibility purposes only. DO NOT use it in your code.</b>
        /// use <see cref="CloseWindow(MultiWindowItem, bool)"/> instead to close window to this multi-window view.
        /// </summary>
        /// <remarks>
        /// This method also can close a window to this multi-window view. 
        /// The window must be of type <see cref="MultiWindowItem"/>.
        /// </remarks>
        /// <param name="children"></param>
        /// <exception cref="InvalidOperationException">when the window is not a <see cref="MultiWindowItem"/>.</exception>
        [Obsolete("This method is for compatibility purposes only. Use CloseWindow(MultiWindowItem, bool) instead to close a window to this multi-window view.", false)]
        public new bool Remove(IView children)
        {
            if (children is MultiWindowItem item)
            {
                CloseWindow(item);
                return true;
            }
            else
            {
                if (Initialized)
                {
                    throw new InvalidOperationException("Only MultiWindowItem can be removed from MultiWindowView.");
                }
                else
                {
                    return base.Children.Remove(children);
                }
            }
        }
    }
}
