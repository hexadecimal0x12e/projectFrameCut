using Microsoft.Maui.Controls;

namespace projectFrameCut.ApplicationAPIBase.Views.TabbedView
{
    [ContentProperty(nameof(Content))]
    public partial class TabbedViewItem : ContentView
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

        public static readonly BindableProperty TagProperty =
            BindableProperty.Create(nameof(Tag), typeof(string), typeof(TabbedViewItem), "");

        public string Tag
        {
            get => (string)GetValue(TagProperty);
            set => SetValue(TagProperty, value);
        }
    }
}
