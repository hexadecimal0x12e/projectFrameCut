using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace projectFrameCut.ApplicationAPIBase.Views.AIResponseHelper.Blocks
{
    public partial class KbdSpan : Span
    {
        public static readonly BindableProperty KbdBackgroundColorProperty =
            BindableProperty.Create(nameof(KbdBackgroundColor), typeof(Color), typeof(KbdSpan),
                Color.FromArgb("#F5F5F5"));

        public static readonly BindableProperty KbdTextColorProperty =
            BindableProperty.Create(nameof(KbdTextColor), typeof(Color), typeof(KbdSpan),
                Color.FromArgb("#333333"));

        public Color KbdBackgroundColor
        {
            get => (Color)GetValue(KbdBackgroundColorProperty);
            set => SetValue(KbdBackgroundColorProperty, value);
        }

        public Color KbdTextColor
        {
            get => (Color)GetValue(KbdTextColorProperty);
            set => SetValue(KbdTextColorProperty, value);
        }

        public KbdSpan()
        {
            BackgroundColor = KbdBackgroundColor;
            TextColor = KbdTextColor;
            FontFamily = "MarkdownCodeBlock";
        }
    }
}
