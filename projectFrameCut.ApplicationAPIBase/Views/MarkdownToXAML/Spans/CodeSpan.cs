using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace projectFrameCut.ApplicationAPIBase.Views.MarkdownToXAML.Spans
{
    public partial class CodeSpan : Span
    {
        public static readonly BindableProperty LanguageProperty =
            BindableProperty.Create(nameof(Language), typeof(string), typeof(CodeSpan), null);

        public static readonly BindableProperty CodeBackgroundColorProperty =
            BindableProperty.Create(nameof(CodeBackgroundColor), typeof(Color), typeof(CodeSpan),
                Markdown2XAML.CodeBlockBackgroundColor);

        public static readonly BindableProperty CodeTextColorProperty =
            BindableProperty.Create(nameof(CodeTextColor), typeof(Color), typeof(CodeSpan),
                Markdown2XAML.CodeBlockTextColor);

        public string Language
        {
            get => (string)GetValue(LanguageProperty);
            set => SetValue(LanguageProperty, value);
        }

        public Color CodeBackgroundColor
        {
            get => (Color)GetValue(CodeBackgroundColorProperty);
            set => SetValue(CodeBackgroundColorProperty, value);
        }

        public Color CodeTextColor
        {
            get => (Color)GetValue(CodeTextColorProperty);
            set => SetValue(CodeTextColorProperty, value);
        }

        public CodeSpan()
        {
            BackgroundColor = CodeBackgroundColor;
            TextColor = CodeTextColor;
            FontFamily = "MarkdownCodeBlock";
        }
    }
}
