using System;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;

namespace projectFrameCut.ApplicationAPIBase.Views.MultiWindowView
{
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

        public static readonly BindableProperty IsNavigationVisibleProperty =
            BindableProperty.Create(nameof(IsNavigationVisible), typeof(bool), typeof(MultiWindowItem), true);

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
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

        #endregion

        #region Events

        public event EventHandler CloseClicked;
        public event EventHandler MinimizeClicked;
        public event EventHandler MaximizeClicked;
        public event EventHandler Activated;

        #endregion

        #region Fields

        private double _startX, _startY;
        private double _startWidth, _startHeight;

        // For window state restoration
        private double _preMaxWidth, _preMaxHeight, _preMaxX, _preMaxY;
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

        private readonly System.Collections.Generic.Stack<View> _backStack = new();
        private readonly System.Collections.Generic.Stack<View> _forwardStack = new();

        #endregion

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
             if(_minimizeBtn != null) _minimizeBtn.IsVisible = IsMinimizable;
             if(_maximizeBtn != null) _maximizeBtn.IsVisible = IsMaximizable;
             if(_closeBtn != null) _closeBtn.IsVisible = IsClosable;
        }

        #region Event Handlers

        private void OnCloseTapped(object sender, EventArgs e)
        {
            CloseClicked?.Invoke(this, EventArgs.Empty);
            // Optionally remove self from parent if no external handler
            if (Parent is Layout layout)
            {
                layout.Children.Remove(this);
            }
        }

        private void OnMinimizeTapped(object sender, EventArgs e)
        {
             MinimizeClicked?.Invoke(this, EventArgs.Empty);
             ToggleMinimize();
        }

        private void OnMaximizeTapped(object sender, EventArgs e)
        {
            MaximizeClicked?.Invoke(this, EventArgs.Empty);
            ToggleMaximize();
        }

        private void OnBackTapped(object sender, EventArgs e)
        {
            if (CanGoBack) GoBack();
        }

        private void OnForwardTapped(object sender, EventArgs e)
        {
            if (CanGoForward) GoForward();
        }

        #endregion

        #region Actions

        private void ToggleMinimize()
        {
             if (_isMinimized)
             {
                 // Restore
                 this.HeightRequest = _preMinHeight;
                 _isMinimized = false;
                 // Re-enable resize handles
                 if(_resizeGrid != null) _resizeGrid.IsVisible = true;
             }
             else
             {
                 _preMinHeight = this.HeightRequest > 0 ? this.HeightRequest : this.Height;
                 // Minimize to TitleHeight approx 32 + borders
                 this.HeightRequest = 35; 
                 _isMinimized = true;
                 // Disable resize handles
                 if(_resizeGrid != null) _resizeGrid.IsVisible = false;
             }
        }

        private void ToggleMaximize()
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
                _isMaximized = false;
                
                // Re-enable resize handles
                if(_resizeGrid != null) _resizeGrid.IsVisible = true;
                if(_visualRoot != null) _visualRoot.StrokeShape = new RoundRectangle { CornerRadius = 10 };
            }
            else
            {
                // Snapshot
                _preMaxX = this.TranslationX;
                _preMaxY = this.TranslationY;
                _preMaxWidth = this.Width;
                _preMaxHeight = this.Height;

                // Maximize
                // We use Fill options to let the Grid layout engine handle the size, 
                // ensuring it stays maximized even if parent resizes.
                parentContainer.InvalidateMeasure(); // Force layout update just in case
                
                this.TranslationX = 0;
                this.TranslationY = 0;
                this.HorizontalOptions = LayoutOptions.Fill;
                this.VerticalOptions = LayoutOptions.Fill;
                
                // Clear explicit requests so Fill works
                this.WidthRequest = -1; 
                this.HeightRequest = -1;
                
                _isMaximized = true;

                // Disable resize handles
                if(_resizeGrid != null) _resizeGrid.IsVisible = false;
                if(_visualRoot != null) _visualRoot.StrokeShape = new RoundRectangle { CornerRadius = 0 };
            }
        }

        #endregion

        #region Navigation Methods

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

            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    _startX = this.TranslationX;
                    _startY = this.TranslationY;
                    break;
                case GestureStatus.Running:
                    this.TranslationX = _startX + e.TotalX;
                    this.TranslationY = _startY + e.TotalY;
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
}
