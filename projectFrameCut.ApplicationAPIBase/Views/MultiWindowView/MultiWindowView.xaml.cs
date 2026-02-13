using System;
using Microsoft.Maui.Controls;

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
        /// Collection of all <see cref="MultiWindowItem"/>.
        /// </summary>
        /// <remarks>
        /// To open a window, use <see cref="AddWindow(MultiWindowItem)"/> to the collection. 
        /// To close a window, use <see cref="CloseWindow(MultiWindowItem)"/>.
        /// </remarks>
        public IReadOnlyList<MultiWindowItem> Windows => base.Children.OfType<MultiWindowItem>().ToList();

        /// <summary>
        /// <b>DO NOT manipulate this collection directly.</b>
        /// To open a window, use <see cref="AddWindow(MultiWindowItem)"/> to the collection. 
        /// To close a window, use <see cref="CloseWindow(MultiWindowItem)"/>.
        /// </summary>
        public IList<IView> Children => base.Children;

        public MultiWindowView()
        {
            InitializeComponent();
            this.ChildAdded += OnChildAdded;
            this.ChildRemoved += OnChildRemoved;
        }


        private void OnChildAdded(object sender, ElementEventArgs? e)
        {
            if (e?.Element is MultiWindowItem item)
            {
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
        public void CloseWindow(MultiWindowItem window)
        {
            window.Close();
            if (Children.Contains(window))
            {
                Children.Remove(window);
            }
        }
    }
}
