using System;
using Microsoft.Maui.Controls;

namespace projectFrameCut.ApplicationAPIBase.Views.MultiWindowView
{
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

        public void AddWindow(MultiWindowItem window)
        {
            this.Children.Add(window);
        }

        public void CloseWindow(MultiWindowItem window)
        {
            if (Children.Contains(window))
            {
                Children.Remove(window);
            }
        }
    }
}
