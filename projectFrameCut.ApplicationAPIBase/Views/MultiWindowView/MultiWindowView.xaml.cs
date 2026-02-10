using System;
using Microsoft.Maui.Controls;

namespace projectFrameCut.ApplicationAPIBase.Views.MultiWindowView
{
    /// <summary>
    /// A container for all <see cref="MultiWindowItem"/>.
    /// </summary>
    public partial class MultiWindowView : Grid
    {
        public MultiWindowView()
        {
            InitializeComponent();
            this.ChildAdded += OnChildAdded;
        }

        private void OnChildAdded(object sender, ElementEventArgs e)
        {
            if (e.Element is MultiWindowItem item)
            {
                item.Activated += (s, args) => BringToFront(item);
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
