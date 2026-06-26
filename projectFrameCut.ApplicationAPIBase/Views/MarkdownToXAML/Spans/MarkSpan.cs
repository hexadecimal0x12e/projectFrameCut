using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace projectFrameCut.ApplicationAPIBase.Views.MarkdownToXAML.Spans
{
    public partial class MarkSpan : Span
    {
        public static readonly BindableProperty MarkBackgroundColorProperty =
            BindableProperty.Create(nameof(MarkBackgroundColor), typeof(Color), typeof(MarkSpan),
                Color.FromArgb("#FFFF00"));

        public static readonly BindableProperty MarkTextColorProperty =
            BindableProperty.Create(nameof(MarkTextColor), typeof(Color), typeof(MarkSpan), null);

        public Color MarkBackgroundColor
        {
            get => (Color)GetValue(MarkBackgroundColorProperty);
            set => SetValue(MarkBackgroundColorProperty, value);
        }

        public Color MarkTextColor
        {
            get => (Color)GetValue(MarkTextColorProperty);
            set => SetValue(MarkTextColorProperty, value);
        }

        public MarkSpan()
        {
            BackgroundColor = MarkBackgroundColor;
        }
    }
}
