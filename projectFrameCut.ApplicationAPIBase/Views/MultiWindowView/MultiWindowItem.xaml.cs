using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using System;
using System.ComponentModel;

namespace projectFrameCut.ApplicationAPIBase.Views.MultiWindowView
{
    /// <summary>
    /// 窗口关闭事件参数，允许取消关闭操作
    /// </summary>
    public class WindowClosingEventArgs : CancelEventArgs
    {
        public WindowClosingEventArgs() : base(false)
        {
        }
    }

    public partial class MultiWindowItem : Border
    {
        private Point? dragStartPoint = null;
        private Rect originalBounds;
        private bool isDragging = false;
        private ResizeDirection? resizeDirection = null;
        private bool isMaximized = false;
        private Rect normalBounds;

        public static readonly BindableProperty TitleProperty =
            BindableProperty.Create(nameof(Title), typeof(string), typeof(MultiWindowItem), "Window",
                propertyChanged: OnTitleChanged);

        public static readonly BindableProperty WindowContentProperty =
            BindableProperty.Create(nameof(WindowContent), typeof(View), typeof(MultiWindowItem), null,
                propertyChanged: OnContentChanged);

        public static readonly BindableProperty WindowBackgroundColorProperty =
            BindableProperty.Create(nameof(WindowBackgroundColor), typeof(Color), typeof(MultiWindowItem), Colors.White,
                propertyChanged: OnWindowBackgroundColorChanged);

        public static readonly BindableProperty TitleBarBackgroundColorProperty =
            BindableProperty.Create(nameof(TitleBarBackgroundColor), typeof(Color), typeof(MultiWindowItem), Color.FromArgb("#F0F0F0"),
                propertyChanged: OnTitleBarBackgroundColorChanged);

        public static readonly BindableProperty TitleBarTextColorProperty =
            BindableProperty.Create(nameof(TitleBarTextColor), typeof(Color), typeof(MultiWindowItem), Colors.Black,
                propertyChanged: OnTitleBarTextColorChanged);

        public static readonly BindableProperty BorderColorProperty =
            BindableProperty.Create(nameof(BorderColor), typeof(Color), typeof(MultiWindowItem), Color.FromArgb("#CCCCCC"),
                propertyChanged: OnBorderColorChanged);

        public static readonly BindableProperty ShowTitleBarProperty =
            BindableProperty.Create(nameof(ShowTitleBar), typeof(bool), typeof(MultiWindowItem), true,
                propertyChanged: OnShowTitleBarChanged);

        public static readonly BindableProperty ShowTitleTextProperty =
            BindableProperty.Create(nameof(ShowTitleText), typeof(bool), typeof(MultiWindowItem), true,
                propertyChanged: OnShowTitleTextChanged);

        public static readonly BindableProperty ShowCloseButtonProperty =
            BindableProperty.Create(nameof(ShowCloseButton), typeof(bool), typeof(MultiWindowItem), true,
                propertyChanged: OnShowCloseButtonChanged);

        public static readonly BindableProperty ShowMaximizeButtonProperty =
            BindableProperty.Create(nameof(ShowMaximizeButton), typeof(bool), typeof(MultiWindowItem), true,
                propertyChanged: OnShowMaximizeButtonChanged);

        public static readonly BindableProperty ShowMinimizeButtonProperty =
            BindableProperty.Create(nameof(ShowMinimizeButton), typeof(bool), typeof(MultiWindowItem), true,
                propertyChanged: OnShowMinimizeButtonChanged);

        public static readonly BindableProperty CanMoveProperty =
            BindableProperty.Create(nameof(CanMove), typeof(bool), typeof(MultiWindowItem), true);

        public static readonly BindableProperty CanResizeProperty =
            BindableProperty.Create(nameof(CanResize), typeof(bool), typeof(MultiWindowItem), true,
                propertyChanged: OnCanResizeChanged);

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public View WindowContent
        {
            get => (View)GetValue(WindowContentProperty);
            set => SetValue(WindowContentProperty, value);
        }

        public Color WindowBackgroundColor
        {
            get => (Color)GetValue(WindowBackgroundColorProperty);
            set => SetValue(WindowBackgroundColorProperty, value);
        }

        public Color TitleBarBackgroundColor
        {
            get => (Color)GetValue(TitleBarBackgroundColorProperty);
            set => SetValue(TitleBarBackgroundColorProperty, value);
        }

        public Color TitleBarTextColor
        {
            get => (Color)GetValue(TitleBarTextColorProperty);
            set => SetValue(TitleBarTextColorProperty, value);
        }

        public Color BorderColor
        {
            get => (Color)GetValue(BorderColorProperty);
            set => SetValue(BorderColorProperty, value);
        }

        public bool ShowTitleBar
        {
            get => (bool)GetValue(ShowTitleBarProperty);
            set => SetValue(ShowTitleBarProperty, value);
        }

        public bool ShowTitleText
        {
            get => (bool)GetValue(ShowTitleTextProperty);
            set => SetValue(ShowTitleTextProperty, value);
        }

        public bool ShowCloseButton
        {
            get => (bool)GetValue(ShowCloseButtonProperty);
            set => SetValue(ShowCloseButtonProperty, value);
        }

        public bool ShowMaximizeButton
        {
            get => (bool)GetValue(ShowMaximizeButtonProperty);
            set => SetValue(ShowMaximizeButtonProperty, value);
        }

        public bool ShowMinimizeButton
        {
            get => (bool)GetValue(ShowMinimizeButtonProperty);
            set => SetValue(ShowMinimizeButtonProperty, value);
        }

        public bool CanMove
        {
            get => (bool)GetValue(CanMoveProperty);
            set => SetValue(CanMoveProperty, value);
        }

        public bool CanResize
        {
            get => (bool)GetValue(CanResizeProperty);
            set => SetValue(CanResizeProperty, value);
        }

        /// <summary>
        /// 处理调整大小时的 X 方向增量。如果不为 null，将使用此函数处理原始的拖动距离
        /// </summary>
        public Func<double, double>? ResizeDeltaXProcessor { get; set; }

        /// <summary>
        /// 处理调整大小时的 Y 方向增量。如果不为 null，将使用此函数处理原始的拖动距离
        /// </summary>
        public Func<double, double>? ResizeDeltaYProcessor { get; set; }

        public event EventHandler<EventArgs> CloseRequested;
        public event EventHandler<EventArgs> Minimized;
        public event EventHandler<EventArgs> Maximized;
        public event EventHandler<EventArgs> Restored;
        
        // 生命周期事件
        public event EventHandler Appearing;
        public event EventHandler Disappearing;
        public event EventHandler Focused;
        public event EventHandler Unfocused;
        public event EventHandler<WindowClosingEventArgs> Closing;

        public MultiWindowItem()
        {
            InitializeComponent();
            SetupGestureRecognizers();
        }

        #region 生命周期方法

        /// <summary>
        /// 窗口显示时调用
        /// </summary>
        protected virtual void OnAppear()
        {
            Appearing?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 窗口隐藏时调用
        /// </summary>
        protected virtual void OnDisappear()
        {
            Disappearing?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 窗口获得焦点时调用（被选中/置顶）
        /// </summary>
        protected virtual void OnFocus()
        {
            Focused?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 窗口失去焦点时调用
        /// </summary>
        protected virtual void OnUnfocus()
        {
            Unfocused?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 关闭按钮点击时调用，可以通过设置 e.Cancel = true 来取消关闭
        /// </summary>
        /// <param name="e">关闭事件参数</param>
        protected virtual void OnClosingButtonClick(WindowClosingEventArgs e)
        {
            Closing?.Invoke(this, e);
        }

        /// <summary>
        /// 内部调用，触发OnAppear
        /// </summary>
        internal void RaiseAppear()
        {
            OnAppear();
        }

        /// <summary>
        /// 内部调用，触发OnDisappear
        /// </summary>
        internal void RaiseDisappear()
        {
            OnDisappear();
        }

        /// <summary>
        /// 内部调用，触发OnFocus
        /// </summary>
        internal void RaiseFocus()
        {
            OnFocus();
        }

        /// <summary>
        /// 内部调用，触发OnUnfocus
        /// </summary>
        internal void RaiseUnfocus()
        {
            OnUnfocus();
        }

        #endregion

        private static void OnTitleChanged(BindableObject bindable, object oldValue, object newValue)
        {
            var control = (MultiWindowItem)bindable;
            control.titleLabel.Text = newValue?.ToString() ?? "Window";
        }

        private static void OnContentChanged(BindableObject bindable, object oldValue, object newValue)
        {
            var control = (MultiWindowItem)bindable;
            control.contentPresenter.Content = newValue as View;
        }

        private static void OnWindowBackgroundColorChanged(BindableObject bindable, object oldValue, object newValue)
        {
            var control = (MultiWindowItem)bindable;
            control.BackgroundColor = (Color)newValue;
        }

        private static void OnTitleBarBackgroundColorChanged(BindableObject bindable, object oldValue, object newValue)
        {
            var control = (MultiWindowItem)bindable;
            control.titleBar.BackgroundColor = (Color)newValue;
        }

        private static void OnTitleBarTextColorChanged(BindableObject bindable, object oldValue, object newValue)
        {
            var control = (MultiWindowItem)bindable;
            control.titleLabel.TextColor = (Color)newValue;
        }

        private static void OnBorderColorChanged(BindableObject bindable, object oldValue, object newValue)
        {
            var control = (MultiWindowItem)bindable;
            control.Stroke = (Color)newValue;
        }

        private static void OnShowTitleBarChanged(BindableObject bindable, object oldValue, object newValue)
        {
            var control = (MultiWindowItem)bindable;
            control.titleBar.IsVisible = (bool)newValue;
        }

        private static void OnShowTitleTextChanged(BindableObject bindable, object oldValue, object newValue)
        {
            var control = (MultiWindowItem)bindable;
            control.titleLabel.IsVisible = (bool)newValue;
        }

        private static void OnShowCloseButtonChanged(BindableObject bindable, object oldValue, object newValue)
        {
            var control = (MultiWindowItem)bindable;
            control.closeButton.IsVisible = (bool)newValue;
        }

        private static void OnShowMaximizeButtonChanged(BindableObject bindable, object oldValue, object newValue)
        {
            var control = (MultiWindowItem)bindable;
            control.maximizeButton.IsVisible = (bool)newValue;
        }

        private static void OnShowMinimizeButtonChanged(BindableObject bindable, object oldValue, object newValue)
        {
            var control = (MultiWindowItem)bindable;
            control.minimizeButton.IsVisible = (bool)newValue;
        }

        private static void OnCanResizeChanged(BindableObject bindable, object oldValue, object newValue)
        {
            var control = (MultiWindowItem)bindable;
            control.SetResizeHandlesEnabled((bool)newValue);
        }

        private void SetupGestureRecognizers()
        {
            // 标题栏拖动
            var titleBarPanGesture = new PanGestureRecognizer();
            titleBarPanGesture.PanUpdated += OnTitleBarPan;
            titleBar.GestureRecognizers.Add(titleBarPanGesture);

            // 双击标题栏最大化/还原
            var titleBarTapGesture = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
            titleBarTapGesture.Tapped += (s, e) => ToggleMaximize();
            titleBar.GestureRecognizers.Add(titleBarTapGesture);

            // 设置调整大小的手柄
            SetupResizeHandle(resizeTopLeft, ResizeDirection.TopLeft);
            SetupResizeHandle(resizeTop, ResizeDirection.Top);
            SetupResizeHandle(resizeTopRight, ResizeDirection.TopRight);
            SetupResizeHandle(resizeLeft, ResizeDirection.Left);
            SetupResizeHandle(resizeRight, ResizeDirection.Right);
            SetupResizeHandle(resizeBottomLeft, ResizeDirection.BottomLeft);
            SetupResizeHandle(resizeBottom, ResizeDirection.Bottom);
            SetupResizeHandle(resizeBottomRight, ResizeDirection.BottomRight);
        }

        private void SetupResizeHandle(View handle, ResizeDirection direction)
        {
            var panGesture = new PanGestureRecognizer();
            panGesture.PanUpdated += (s, e) => OnResizePan(s, e, direction);
            handle.GestureRecognizers.Add(panGesture);
        }

        private void OnTitleBarPan(object sender, PanUpdatedEventArgs e)
        {
            if (isMaximized || !CanMove) return;

            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    dragStartPoint = new Point(this.TranslationX, this.TranslationY);
                    isDragging = true;
                    break;

                case GestureStatus.Running:
                    if (dragStartPoint.HasValue)
                    {
                        this.TranslationX = dragStartPoint.Value.X + e.TotalX;
                        this.TranslationY = dragStartPoint.Value.Y + e.TotalY;
                    }
                    break;

                case GestureStatus.Completed:
                case GestureStatus.Canceled:
                    isDragging = false;
                    break;
            }
        }

        private void OnResizePan(object sender, PanUpdatedEventArgs e, ResizeDirection direction)
        {
            if (isMaximized || !CanResize) return;

            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    resizeDirection = direction;
                    originalBounds = new Rect(this.TranslationX, this.TranslationY, this.Width, this.Height);
                    break;

                case GestureStatus.Running:
                    if (resizeDirection.HasValue)
                    {
                        double deltaX = ResizeDeltaXProcessor != null ? ResizeDeltaXProcessor(e.TotalX) : e.TotalX;
                        double deltaY = ResizeDeltaYProcessor != null ? ResizeDeltaYProcessor(e.TotalY) : e.TotalY;
                        PerformResize(deltaX, deltaY, resizeDirection.Value);
                    }
                    break;

                case GestureStatus.Completed:
                case GestureStatus.Canceled:
                    resizeDirection = null;
                    break;
            }
        }

        private void PerformResize(double deltaX, double deltaY, ResizeDirection direction)
        {
            const double minWidth = 200;
            const double minHeight = 150;

            double newX = originalBounds.X;
            double newY = originalBounds.Y;
            double newWidth = originalBounds.Width;
            double newHeight = originalBounds.Height;

            switch (direction)
            {
                case ResizeDirection.TopLeft:
                    newWidth = originalBounds.Width - deltaX;
                    newHeight = originalBounds.Height - deltaY;
                    // 应用最小尺寸限制
                    if (newWidth < minWidth)
                    {
                        deltaX = originalBounds.Width - minWidth;
                        newWidth = minWidth;
                    }
                    if (newHeight < minHeight)
                    {
                        deltaY = originalBounds.Height - minHeight;
                        newHeight = minHeight;
                    }
                    newX = originalBounds.X + deltaX;
                    newY = originalBounds.Y + deltaY;
                    break;

                case ResizeDirection.Top:
                    newHeight = originalBounds.Height - deltaY;
                    if (newHeight < minHeight)
                    {
                        deltaY = originalBounds.Height - minHeight;
                        newHeight = minHeight;
                    }
                    newY = originalBounds.Y + deltaY;
                    break;

                case ResizeDirection.TopRight:
                    newWidth = originalBounds.Width + deltaX;
                    newHeight = originalBounds.Height - deltaY;
                    if (newWidth < minWidth)
                    {
                        newWidth = minWidth;
                    }
                    if (newHeight < minHeight)
                    {
                        deltaY = originalBounds.Height - minHeight;
                        newHeight = minHeight;
                    }
                    newY = originalBounds.Y + deltaY;
                    break;

                case ResizeDirection.Left:
                    newWidth = originalBounds.Width - deltaX;
                    if (newWidth < minWidth)
                    {
                        deltaX = originalBounds.Width - minWidth;
                        newWidth = minWidth;
                    }
                    newX = originalBounds.X + deltaX;
                    break;

                case ResizeDirection.Right:
                    newWidth = originalBounds.Width + deltaX;
                    if (newWidth < minWidth)
                    {
                        newWidth = minWidth;
                    }
                    break;

                case ResizeDirection.BottomLeft:
                    newWidth = originalBounds.Width - deltaX;
                    newHeight = originalBounds.Height + deltaY;
                    if (newWidth < minWidth)
                    {
                        deltaX = originalBounds.Width - minWidth;
                        newWidth = minWidth;
                    }
                    if (newHeight < minHeight)
                    {
                        newHeight = minHeight;
                    }
                    newX = originalBounds.X + deltaX;
                    break;

                case ResizeDirection.Bottom:
                    newHeight = originalBounds.Height + deltaY;
                    if (newHeight < minHeight)
                    {
                        newHeight = minHeight;
                    }
                    break;

                case ResizeDirection.BottomRight:
                    newWidth = originalBounds.Width + deltaX;
                    newHeight = originalBounds.Height + deltaY;
                    if (newWidth < minWidth)
                    {
                        newWidth = minWidth;
                    }
                    if (newHeight < minHeight)
                    {
                        newHeight = minHeight;
                    }
                    break;
            }

            // 应用计算后的位置和尺寸
            this.WidthRequest = newWidth;
            this.HeightRequest = newHeight;
            this.TranslationX = newX;
            this.TranslationY = newY;
        }

        private void OnMinimizeClicked(object sender, EventArgs e)
        {
            this.IsVisible = false;
            Minimized?.Invoke(this, EventArgs.Empty);
        }

        private void OnMaximizeClicked(object sender, EventArgs e)
        {
            ToggleMaximize();
        }

        private void ToggleMaximize()
        {
            if (!isMaximized)
            {
                Maximize();
            }
            else
            {
                Restore();
            }
        }

        public void Maximize()
        {
            if (isMaximized) return;

            // 保存当前状态
            normalBounds = new Rect(this.TranslationX, this.TranslationY, 
                this.WidthRequest > 0 ? this.WidthRequest : this.Width, 
                this.HeightRequest > 0 ? this.HeightRequest : this.Height);

            // 获取父容器尺寸
            if (this.Parent is AbsoluteLayout parent)
            {
                // 重置平移
                this.TranslationX = 0;
                this.TranslationY = 0;
                
                // 使用父容器的实际尺寸
                double containerWidth = parent.Width;
                double containerHeight = parent.Height;
                
                // 设置窗口填满整个容器
                this.WidthRequest = containerWidth;
                this.HeightRequest = containerHeight;
                
                // 使用AbsoluteLayout进行定位
                AbsoluteLayout.SetLayoutBounds(this, new Rect(0, 0, containerWidth, containerHeight));
            }

            // 禁用调整大小手柄
            SetResizeHandlesEnabled(false);
            
            isMaximized = true;
            maximizeButton.Text = "◱";
            Maximized?.Invoke(this, EventArgs.Empty);
        }

        public void Restore()
        {
            if (!isMaximized) return;

            // 恢复原始大小和位置
            this.TranslationX = normalBounds.X;
            this.TranslationY = normalBounds.Y;
            this.WidthRequest = normalBounds.Width;
            this.HeightRequest = normalBounds.Height;
            
            // 使用AbsoluteLayout恢复布局
            if (this.Parent is AbsoluteLayout)
            {
                AbsoluteLayout.SetLayoutBounds(this, 
                    new Rect(normalBounds.X, normalBounds.Y, normalBounds.Width, normalBounds.Height));
            }

            // 启用调整大小手柄
            SetResizeHandlesEnabled(true);
            
            isMaximized = false;
            maximizeButton.Text = "□";
            Restored?.Invoke(this, EventArgs.Empty);
        }

        private void OnCloseClicked(object sender, EventArgs e)
        {
            var args = new WindowClosingEventArgs();
            OnClosingButtonClick(args);
            
            // 如果没有被取消，才触发关闭请求
            if (!args.Cancel)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        public void BringToFront()
        {
            // MAUI中通过移除再添加来实现置顶
            if (this.Parent is Layout parent)
            {
                var index = parent.Children.IndexOf(this);
                if (index >= 0 && index < parent.Children.Count - 1)
                {
                    parent.Children.Remove(this);
                    parent.Children.Add(this);
                    
                    // 触发焦点事件
                    RaiseFocus();
                }
            }
        }

        private void SetResizeHandlesEnabled(bool enabled)
        {
            resizeTopLeft.IsVisible = enabled;
            resizeTop.IsVisible = enabled;
            resizeTopRight.IsVisible = enabled;
            resizeLeft.IsVisible = enabled;
            resizeRight.IsVisible = enabled;
            resizeBottomLeft.IsVisible = enabled;
            resizeBottom.IsVisible = enabled;
            resizeBottomRight.IsVisible = enabled;
        }

        /// <summary>
        /// 重写IsVisible属性以触发生命周期事件
        /// </summary>
        protected override void OnPropertyChanged(string propertyName = null)
        {
            base.OnPropertyChanged(propertyName);

            if (propertyName == nameof(IsVisible))
            {
                if (IsVisible)
                {
                    RaiseAppear();
                }
                else
                {
                    RaiseDisappear();
                }
            }
        }

        private enum ResizeDirection
        {
            TopLeft,
            Top,
            TopRight,
            Left,
            Right,
            BottomLeft,
            Bottom,
            BottomRight
        }
    }
}