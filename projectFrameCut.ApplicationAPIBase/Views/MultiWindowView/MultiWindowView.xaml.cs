using Microsoft.Maui.Controls;
using Microsoft.Maui.Layouts;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace projectFrameCut.ApplicationAPIBase.Views.MultiWindowView
{
    /// <summary>
    /// 任务栏显示模式
    /// </summary>
    public enum TaskBarDisplayMode
    {
        /// <summary>
        /// 永远不显示任务栏
        /// </summary>
        Never,
        
        /// <summary>
        /// 只为最小化的窗口显示任务栏
        /// </summary>
        MinimizedOnly,
        
        /// <summary>
        /// 为所有窗口显示任务栏
        /// </summary>
        Always
    }

    public partial class MultiWindowView : ContentView
    {
        private ObservableCollection<WindowInfo> windows = new ObservableCollection<WindowInfo>();
        private ObservableCollection<WindowInfo> displayedWindows = new ObservableCollection<WindowInfo>();
        private int windowCounter = 1;

        public static readonly BindableProperty ContainerBackgroundColorProperty =
            BindableProperty.Create(nameof(ContainerBackgroundColor), typeof(Color), typeof(MultiWindowView), 
                Color.FromArgb("#E8E8E8"),
                propertyChanged: OnContainerBackgroundColorChanged);

        public static readonly BindableProperty TaskBarBackgroundColorProperty =
            BindableProperty.Create(nameof(TaskBarBackgroundColor), typeof(Color), typeof(MultiWindowView), 
                Color.FromArgb("#F5F5F5"),
                propertyChanged: OnTaskBarBackgroundColorChanged);

        public static readonly BindableProperty TaskBarDisplayModeProperty =
            BindableProperty.Create(nameof(TaskBarDisplayMode), typeof(TaskBarDisplayMode), typeof(MultiWindowView),
                TaskBarDisplayMode.Always,
                propertyChanged: OnTaskBarDisplayModeChanged);

        [Obsolete("Use TaskBarDisplayMode instead")]
        public bool ShowTaskBar
        {
            get => taskBar.IsVisible;
            set => taskBar.IsVisible = value;
        }

        public TaskBarDisplayMode TaskBarDisplayMode
        {
            get => (TaskBarDisplayMode)GetValue(TaskBarDisplayModeProperty);
            set => SetValue(TaskBarDisplayModeProperty, value);
        }

        public Color ContainerBackgroundColor
        {
            get => (Color)GetValue(ContainerBackgroundColorProperty);
            set => SetValue(ContainerBackgroundColorProperty, value);
        }

        public Color TaskBarBackgroundColor
        {
            get => (Color)GetValue(TaskBarBackgroundColorProperty);
            set => SetValue(TaskBarBackgroundColorProperty, value);
        }

        public MultiWindowView()
        {
            InitializeComponent();
            windowList.ItemsSource = displayedWindows;

            // 添加容器点击事件，用于取消窗口焦点
            var tapGesture = new TapGestureRecognizer();
            tapGesture.Tapped += OnContainerTapped;
            mdiContainer.GestureRecognizers.Add(tapGesture);
            
            // 初始化任务栏显示
            UpdateTaskBarVisibility();
        }

        private static void OnContainerBackgroundColorChanged(BindableObject bindable, object oldValue, object newValue)
        {
            var control = (MultiWindowView)bindable;
            control.mdiContainer.BackgroundColor = (Color)newValue;
        }

        private static void OnTaskBarBackgroundColorChanged(BindableObject bindable, object oldValue, object newValue)
        {
            var control = (MultiWindowView)bindable;
            control.taskBar.BackgroundColor = (Color)newValue;
        }

        private static void OnTaskBarDisplayModeChanged(BindableObject bindable, object oldValue, object newValue)
        {
            var control = (MultiWindowView)bindable;
            control.UpdateTaskBarVisibility();
        }

        /// <summary>
        /// 根据当前模式和窗口状态更新任务栏的显示
        /// </summary>
        private void UpdateTaskBarVisibility()
        {
            switch (TaskBarDisplayMode)
            {
                case TaskBarDisplayMode.Never:
                    taskBar.IsVisible = false;
                    displayedWindows.Clear();
                    break;

                case TaskBarDisplayMode.MinimizedOnly:
                    var minimizedWindows = windows.Where(w => w.IsMinimized).ToList();
                    taskBar.IsVisible = minimizedWindows.Any();
                    
                    displayedWindows.Clear();
                    foreach (var window in minimizedWindows)
                    {
                        displayedWindows.Add(window);
                    }
                    break;

                case TaskBarDisplayMode.Always:
                    taskBar.IsVisible = windows.Any();
                    
                    displayedWindows.Clear();
                    foreach (var window in windows)
                    {
                        displayedWindows.Add(window);
                    }
                    break;
            }
        }

        /// <summary>
        /// 创建并添加一个新窗口
        /// </summary>
        /// <param name="content">窗口内容</param>
        /// <param name="title">窗口标题</param>
        /// <param name="x">初始X坐标（可选）</param>
        /// <param name="y">初始Y坐标（可选）</param>
        /// <param name="width">初始宽度（可选）</param>
        /// <param name="height">初始高度（可选）</param>
        /// <returns>创建的窗口项</returns>
        public MultiWindowItem CreateWindow(View content, string title = null, 
            double? x = null, double? y = null, 
            double? width = null, double? height = null)
        {
            var windowItem = new MultiWindowItem
            {
                Title = title ?? $"Window {windowCounter++}",
                WindowContent = content,
                WidthRequest = width ?? 400,
                HeightRequest = height ?? 300
            };

            // 计算级联位置
            if (!x.HasValue || !y.HasValue)
            {
                int windowCount = windows.Count;
                double offset = windowCount * 30;
                x = offset % 200;
                y = offset % 200;
            }

            windowItem.TranslationX = x.Value;
            windowItem.TranslationY = y.Value;

            // 设置绝对布局参数
            AbsoluteLayout.SetLayoutBounds(windowItem, 
                new Rect(x.Value, y.Value, width ?? 400, height ?? 300));
            AbsoluteLayout.SetLayoutFlags(windowItem, AbsoluteLayoutFlags.None);

            // 添加事件处理
            windowItem.CloseRequested += (s, e) => CloseWindow(windowItem);
            windowItem.Minimized += (s, e) => MinimizeWindow(windowItem);
            windowItem.Maximized += (s, e) => MaximizeWindow(windowItem);
            windowItem.Restored += (s, e) => RestoreWindow(windowItem);

            // 添加点击事件以置顶
            var tapGesture = new TapGestureRecognizer();
            tapGesture.Tapped += (s, e) => BringWindowToFront(windowItem);
            windowItem.GestureRecognizers.Add(tapGesture);

            // 添加到容器
            mdiContainer.Children.Add(windowItem);

            // 添加到窗口列表
            var windowInfo = new WindowInfo
            {
                Window = windowItem,
                Title = windowItem.Title,
                IsMinimized = false
            };
            windows.Add(windowInfo);

            // 置顶新窗口（这会触发Focus事件）
            BringWindowToFront(windowItem);
            
            // 触发Appear事件
            windowItem.RaiseAppear();
            
            // 更新任务栏显示
            UpdateTaskBarVisibility();

            return windowItem;
        }

        /// <summary>
        /// 关闭窗口
        /// </summary>
        public void CloseWindow(MultiWindowItem window)
        {
            if (mdiContainer.Children.Contains(window))
            {
                mdiContainer.Children.Remove(window);
            }

            var windowInfo = windows.FirstOrDefault(w => w.Window == window);
            if (windowInfo != null)
            {
                windows.Remove(windowInfo);
            }
            
            // 更新任务栏显示
            UpdateTaskBarVisibility();
        }

        /// <summary>
        /// 移除窗口（CloseWindow的别名）
        /// </summary>
        public void RemoveWindow(MultiWindowItem window)
        {
            CloseWindow(window);
        }

        /// <summary>
        /// 最小化窗口
        /// </summary>
        public void MinimizeWindow(MultiWindowItem window)
        {
            var windowInfo = windows.FirstOrDefault(w => w.Window == window);
            if (windowInfo != null)
            {
                windowInfo.IsMinimized = true;
            }
            
            // 更新任务栏显示
            UpdateTaskBarVisibility();
        }

        /// <summary>
        /// 最大化窗口
        /// </summary>
        public void MaximizeWindow(MultiWindowItem window)
        {
            BringWindowToFront(window);
        }

        /// <summary>
        /// 还原窗口
        /// </summary>
        public void RestoreWindow(MultiWindowItem window)
        {
            var windowInfo = windows.FirstOrDefault(w => w.Window == window);
            if (windowInfo != null)
            {
                windowInfo.IsMinimized = false;
                window.IsVisible = true;
                BringWindowToFront(window);
            }
            
            // 更新任务栏显示
            UpdateTaskBarVisibility();
        }

        /// <summary>
        /// 将窗口置于最前
        /// </summary>
        public void BringWindowToFront(MultiWindowItem window)
        {
            if (mdiContainer.Children.Contains(window))
            {
                // 获取当前顶层窗口（如果存在）
                var currentTopWindow = mdiContainer.Children.LastOrDefault() as MultiWindowItem;
                
                // 如果当前顶层窗口不是要置顶的窗口，则触发失焦事件
                if (currentTopWindow != null && currentTopWindow != window)
                {
                    currentTopWindow.RaiseUnfocus();
                }
                
                mdiContainer.Children.Remove(window);
                mdiContainer.Children.Add(window);
                
                // 触发新窗口的获焦事件
                window.RaiseFocus();
            }
        }

        /// <summary>
        /// 关闭所有窗口
        /// </summary>
        public void CloseAllWindows()
        {
            mdiContainer.Children.Clear();
            windows.Clear();
            windowCounter = 1;
            
            // 更新任务栏显示
            UpdateTaskBarVisibility();
        }

        /// <summary>
        /// 层叠窗口
        /// </summary>
        public void CascadeWindows()
        {
            int index = 0;
            foreach (var windowInfo in windows.Where(w => !w.IsMinimized))
            {
                var window = windowInfo.Window;
                double offset = index * 30;
                
                window.Restore();
                window.TranslationX = offset;
                window.TranslationY = offset;
                window.WidthRequest = 400;
                window.HeightRequest = 300;
                
                index++;
            }
        }

        /// <summary>
        /// 平铺窗口
        /// </summary>
        public void TileWindows()
        {
            var visibleWindows = windows.Where(w => !w.IsMinimized).ToList();
            if (visibleWindows.Count == 0) return;

            int cols = (int)Math.Ceiling(Math.Sqrt(visibleWindows.Count));
            int rows = (int)Math.Ceiling((double)visibleWindows.Count / cols);

            double windowWidth = mdiContainer.Width / cols;
            double windowHeight = (mdiContainer.Height - (ShowTaskBar ? 40 : 0)) / rows;

            for (int i = 0; i < visibleWindows.Count; i++)
            {
                var window = visibleWindows[i].Window;
                int col = i % cols;
                int row = i / cols;

                window.Restore();
                window.TranslationX = col * windowWidth;
                window.TranslationY = row * windowHeight;
                window.WidthRequest = windowWidth - 5;
                window.HeightRequest = windowHeight - 5;
            }
        }

        private void OnContainerTapped(object sender, EventArgs e)
        {
            // 可以在这里处理容器点击事件
        }

        private void OnWindowListItemTapped(object sender, EventArgs e)
        {
            if (sender is View view && view.BindingContext is WindowInfo windowInfo)
            {
                if (windowInfo.IsMinimized || !windowInfo.Window.IsVisible)
                {
                    windowInfo.Window.IsVisible = true;
                    windowInfo.IsMinimized = false;
                    
                    // 更新任务栏显示
                    UpdateTaskBarVisibility();
                }
                BringWindowToFront(windowInfo.Window);
            }
        }

        /// <summary>
        /// 获取所有窗口
        /// </summary>
        public ObservableCollection<WindowInfo> GetWindows()
        {
            return windows;
        }


    }

    /// <summary>
    /// 窗口信息类
    /// </summary>
    public class WindowInfo
    {
        public MultiWindowItem Window { get; set; }
        public string Title { get; set; }
        public bool IsMinimized { get; set; }
    }
}