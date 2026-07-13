namespace projectFrameCut.ApplicationAPIBase.Views.MarkdownToXAML.Spans
{
    /// <summary>
    /// 行内图片 Span。由于 MAUI Label 不支持内嵌 Image 控件，
    /// 行内图片以带图标的可点击文本形式展示，点击后打开图片 URL。
    /// </summary>
    public partial class ImageSpan : Span
    {
        public static readonly BindableProperty ImageUrlProperty =
            BindableProperty.Create(nameof(ImageUrl), typeof(string), typeof(ImageSpan), null);

        public static readonly BindableProperty AltTextProperty =
            BindableProperty.Create(nameof(AltText), typeof(string), typeof(ImageSpan), null);

        public string ImageUrl
        {
            get => (string)GetValue(ImageUrlProperty);
            set => SetValue(ImageUrlProperty, value);
        }

        public string AltText
        {
            get => (string)GetValue(AltTextProperty);
            set => SetValue(AltTextProperty, value);
        }

        public ImageSpan()
        {
            TextColor = Colors.Blue;
            TextDecorations = TextDecorations.Underline;
            GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(async () =>
                {
                    if (!string.IsNullOrEmpty(ImageUrl))
                    {
                        await Launcher.OpenAsync(ImageUrl);
                    }
                })
            });
        }
    }
}
