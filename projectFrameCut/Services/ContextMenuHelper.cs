using Microsoft.Maui.Controls;
using System;
using System.Collections.Generic;
using System.Windows.Input;


#if WINDOWS
using Microsoft.UI.Xaml.Controls;
using Microsoft.Maui.Platform;
using MenuFlyoutSeparator = Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator;
using MenuFlyoutItem = Microsoft.UI.Xaml.Controls.MenuFlyoutItem;
using MenuFlyout = Microsoft.UI.Xaml.Controls.MenuFlyout;

#endif

namespace projectFrameCut.Services
{
    public interface IContextMenuBuilder
    {
        IContextMenuBuilder AddCommand(string text, Action action);
        IContextMenuBuilder AddCommand(string text, Action action, ImageSource icon);
        IContextMenuBuilder AddSeparator();

        bool TryShow(IView anchor);
    }
#if WINDOWS

    public class WindowsContextMenuBuilder : IContextMenuBuilder
    {
        private readonly List<ContextMenuItem> _items = new();

        private class ContextMenuItem
        {
            public string Text { get; set; }
            public Action Action { get; set; }
            public ImageSource Icon { get; set; }
            public bool IsSeparator { get; set; }
        }

        public IContextMenuBuilder AddCommand(string text, Action action)
        {
            _items.Add(new ContextMenuItem { Text = text, Action = action });
            return this;
        }

        public IContextMenuBuilder AddCommand(string text, Action action, ImageSource icon)
        {
            _items.Add(new ContextMenuItem { Text = text, Action = action, Icon = icon });
            return this;
        }

        public IContextMenuBuilder AddSeparator()
        {
            _items.Add(new ContextMenuItem { IsSeparator = true });
            return this;
        }

        public bool TryShow(IView anchor)
        {
            if (anchor?.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement frameworkElement)
            {
                var flyout = new MenuFlyout();
                foreach (var item in _items)
                {
                    if (item.IsSeparator)
                    {
                        flyout.Items.Add(new MenuFlyoutSeparator());
                    }
                    else
                    {
                        var flyoutItem = new MenuFlyoutItem
                        {
                            Text = item.Text
                        };
                        flyoutItem.Click += (s, e) => item.Action?.Invoke();
                        
                        // TODO: Implement Icon support for Windows
                        // if (item.Icon != null) { ... }

                        flyout.Items.Add(flyoutItem);
                    }
                }
                
                flyout.ShowAt(frameworkElement);
                return true;
            }
            return false;
        }
    }
#endif


}
