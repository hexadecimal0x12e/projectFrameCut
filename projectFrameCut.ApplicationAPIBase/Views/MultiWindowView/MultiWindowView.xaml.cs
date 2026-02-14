using System;
using Microsoft.Maui.Controls;
#pragma warning disable CS0108 // avoid hiding members cause XAML compatibility issues

namespace projectFrameCut.ApplicationAPIBase.Views.MultiWindowView
{
    /// <summary>
    /// A container for all <see cref="MultiWindowItem"/>.
    /// </summary>
    public partial class MultiWindowView : Grid
    {
        public event EventHandler<MultiWindowItem>? WindowAdded;
        public event EventHandler<MultiWindowItem>? WindowClosed;
        public event EventHandler<MultiWindowItem>? WindowFocused;

        /// <summary>
        /// Represents the currently active window.
        /// When set, the specified window will be brought to the front and receive focus.
        /// </summary>
        public MultiWindowItem ActiveWindow
        {
            get => field;
            set
            {
                if (!Windows.Contains(value, new MultiWindowItemComparer())) throw new InvalidOperationException($"Window {value.Title} ({value.WindowID}) is not part of the collection.");
                if (field != value)
                {
                    field = value;
                    BringToFront(field);
                    WindowFocused?.Invoke(this, field);
                }
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

        public MultiWindowView()
        {
            InitializeComponent();
            this.ChildAdded += OnChildAdded;
            this.ChildRemoved += OnChildRemoved;
            this.Unloaded += OnUnloaded;
        }

        private void OnUnloaded(object? sender, EventArgs e)
        {
            foreach (var item in _managedWindows.ToArray())
            {
                item.Close(true);
            }
            _managedWindows.Clear();
        }

        private void OnItemCloseClicked(object? sender, CloseEventArgs e)
        {
            if (!e.Cancel && sender is MultiWindowItem item)
            {
                _managedWindows.Remove(item);
                item.CloseClicked -= OnItemCloseClicked;
            }
        }

        private void OnChildAdded(object sender, ElementEventArgs? e)
        {
            if (e?.Element is MultiWindowItem item)
            {
                if (_managedWindows.Add(item))
                {
                    item.CloseClicked += OnItemCloseClicked;
                }

                WindowAdded?.Invoke(this, item);
                item.Activated += (s, args) =>
                {
                    BringToFront(item);
                    WindowFocused?.Invoke(this, item);
                };
            }
        }

        private void OnChildRemoved(object? sender, ElementEventArgs e)
        {
            if (e.Element is MultiWindowItem item)
            {
                item.Activated += (s, args) =>
                {
                    WindowClosed?.Invoke(this, item);
                };
            }
        }

        /// <summary>
        /// Bring the specific window to the front. The window will be displayed on top of the existing windows.
        /// </summary>
        /// <param name="item"></param>
        public void BringToFront(MultiWindowItem item)
        {
            int maxZ = 0;
            foreach (var child in Children)
            {
                if (child is View v && v.ZIndex > maxZ)
                {
                    maxZ = v.ZIndex;
                }
            }
            item.ZIndex = maxZ + 1;
            ActiveWindow = item;
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
        public void Add(IView children)
        {
            if (children is MultiWindowItem item)
            {
                AddWindow(item);
            }
            else
            {
                throw new InvalidOperationException("Only MultiWindowItem can be added to MultiWindowView.");
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
        public IView Remove(IView children)
        {
            if (children is MultiWindowItem item)
            {
                CloseWindow(item);
                return item;
            }
            else
            {
                throw new InvalidOperationException("Only MultiWindowItem can be removed from MultiWindowView.");
            }
        }
    }
}
