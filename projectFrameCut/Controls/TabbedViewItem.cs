using Microsoft.Maui.Controls;

namespace projectFrameCut.Controls
{
    [ContentProperty(nameof(Content))]
    public class TabbedViewItem : ContentView
    {
        public static readonly BindableProperty HeaderProperty =
            BindableProperty.Create(nameof(Header), typeof(object), typeof(TabbedViewItem), null);

        public object Header
        {
            get => GetValue(HeaderProperty);
            set => SetValue(HeaderProperty, value);
        }

        public static readonly BindableProperty IsSelectedProperty =
            BindableProperty.Create(nameof(IsSelected), typeof(bool), typeof(TabbedViewItem), false);

        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }
    }
}
