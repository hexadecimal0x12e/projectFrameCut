using Microsoft.Maui.Controls.Shapes;

namespace projectFrameCut.ApplicationAPIBase.Views.MarkdownToXAML.Codeblock
{
    /// <summary>
    /// HTML 代码块渲染器。
    /// 将 ```html 代码块渲染为可交互的 WebView，
    /// 并在顶部提供"网页"和"源代码"两种视图模式的切换按钮。
    ///
    /// <para>默认显示渲染后的网页视图；点击"源代码"可查看原始 HTML。</para>
    ///
    /// <para>注册方式：</para>
    /// <code>
    /// Markdown2XAML.RegisterCodeBlockRenderer(new HtmlCodeBlockRenderer());
    /// </code>
    /// </summary>
    public class HtmlCodeBlockRenderer : CodeBlockRenderer
    {
        // ===== 颜色常量 =====
        private static readonly Color ActiveTabColor = Color.FromArgb("#4A90D9");
        private static readonly Color InactiveBorderColor = Color.FromArgb("#D0D0D0");
        private static readonly Color InactiveTextColor = Color.FromArgb("#888888");
        private static readonly Color HeaderBackgroundColor = Color.FromArgb("#08000000");

        // ===== 布局常量 =====
        private const double ContentHeight = 400;
        private const double ButtonHeight = 28;
        private const double ResizeHandleHeight = 10;
        private const double MinContentHeight = 100;
        private const double MaxContentHeight = 1200;
        private const double WidthResizeHandleWidth = 8;
        private const double MinContentWidth = 200;
        private const double MaxContentWidth = 1600;

        public override string Language => "html";

        public override bool SupportsStreaming => false;

        public override View Render(string code)
        {
            var webView = CreateWebView(code);
            var sourceView = CreateSourceView(code);

            // ContentView 用于在网页/源代码之间切换
            var contentArea = new ContentView
            {
                VerticalOptions = LayoutOptions.Fill,
                HorizontalOptions = LayoutOptions.Fill,
            };

            // 默认显示网页视图
            contentArea.Content = webView;

            // 当前内容区域高度，在拖拽缩放时会更新
            double currentHeight = ContentHeight;

            // 顶部操作栏
            var headerBar = CreateHeaderBar(contentArea, webView, sourceView);

            // 分隔线
            var separator = new BoxView
            {
                Color = Markdown2XAML.CodeBlockBorderColor,
                HeightRequest = 1,
                HorizontalOptions = LayoutOptions.Fill,
            };

            // 可拖拽的缩放手柄（高度）
            var resizeHandle = new Grid
            {
                HeightRequest = ResizeHandleHeight,
                HorizontalOptions = LayoutOptions.Fill,
                BackgroundColor = Color.FromArgb("#18FFFFFF"),
            };

            // 视觉指示：三条居中横线
            for (int i = -1; i <= 1; i++)
            {
                resizeHandle.Children.Add(new BoxView
                {
                    HeightRequest = 1.5,
                    WidthRequest = 20,
                    Color = Color.FromArgb("#60FFFFFF"),
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    TranslationY = i * 3.5,
                });
            }

            var heightPan = new PanGestureRecognizer();
            double dragStartHeight = currentHeight;
            heightPan.PanUpdated += (_, e) =>
            {
                switch (e.StatusType)
                {
                    case GestureStatus.Started:
                        dragStartHeight = currentHeight;
                        break;
                    case GestureStatus.Running:
                        currentHeight = Math.Clamp(dragStartHeight + e.TotalY, MinContentHeight, MaxContentHeight);
                        webView.HeightRequest = currentHeight;
                        sourceView.MaximumHeightRequest = currentHeight;
                        break;
                }
            };
            resizeHandle.GestureRecognizers.Add(heightPan);

            // ===== 代码块主体（带圆角的边框） =====
            var borderBlock = new Border
            {
                StrokeShape = new RoundRectangle { CornerRadius = Markdown2XAML.CodeBlockCornerRadius },
                BackgroundColor = Markdown2XAML.CodeBlockBackgroundColor,
                Stroke = Markdown2XAML.CodeBlockBorderColor,
                StrokeThickness = 1,
                Padding = new Thickness(0),
                Content = new VerticalStackLayout
                {
                    Spacing = 0,
                    Children = { headerBar, separator, contentArea, resizeHandle }
                }
            };

            // ===== 外层 Grid：代码块 + 宽度手柄 =====
            var outerGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                },
                Margin = new Thickness(0, 4),
            };

            // 宽度缩放手柄（右侧纵向条）
            var widthHandle = new Grid
            {
                WidthRequest = WidthResizeHandleWidth,
                VerticalOptions = LayoutOptions.Fill,
                BackgroundColor = Color.FromArgb("#18FFFFFF"),
            };

            // 视觉指示：三条竖线居中排列
            for (int i = -1; i <= 1; i++)
            {
                widthHandle.Children.Add(new BoxView
                {
                    WidthRequest = 1.5,
                    HeightRequest = 20,
                    Color = Color.FromArgb("#60FFFFFF"),
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    TranslationX = i * 3.5,
                });
            }

            var widthPan = new PanGestureRecognizer();
            double dragStartWidth = 0;
            widthPan.PanUpdated += (_, e) =>
            {
                switch (e.StatusType)
                {
                    case GestureStatus.Started:
                        dragStartWidth = outerGrid.Width > 0 ? outerGrid.Width : outerGrid.WidthRequest;
                        outerGrid.HorizontalOptions = LayoutOptions.Start;
                        outerGrid.WidthRequest = dragStartWidth;
                        break;
                    case GestureStatus.Running:
                        outerGrid.WidthRequest = Math.Clamp(dragStartWidth + e.TotalX, MinContentWidth, MaxContentWidth);
                        break;
                }
            };
            widthHandle.GestureRecognizers.Add(widthPan);

            // 组装到外层 Grid
            Grid.SetColumn(borderBlock, 0);
            outerGrid.Children.Add(borderBlock);
            Grid.SetColumn(widthHandle, 1);
            outerGrid.Children.Add(widthHandle);

            return outerGrid;
        }

        /// <summary>
        /// 将原始 HTML 代码包裹为完整的 HTML 文档（如果尚未是完整文档）。
        /// </summary>
        public virtual string WrapHtml(string code)
        {
            if (string.IsNullOrEmpty(code))
                return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body></body></html>";

            var trimmed = code.TrimStart();
            if (trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("<head", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("<body", StringComparison.OrdinalIgnoreCase))
            {
                return code;
            }

            // 对于 HTML 片段，自动包裹为完整文档
            return $"""
                <!DOCTYPE html>
                <html>
                <head>
                    <meta charset="utf-8">
                    <meta name="viewport" content="width=device-width, initial-scale=1.0">
                </head>
                <body style="margin:8px;padding:0;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif">
                    {code}
                </body>
                </html>
                """;
        }

        /// <summary>
        /// 创建顶部操作栏：语言标签 + 视图切换按钮。
        /// </summary>
        private Grid CreateHeaderBar(ContentView contentArea, View webView, View sourceView)
        {
            var headerBar = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto),   // 语言标签
                    new ColumnDefinition(GridLength.Star),    // 弹性间距
                    new ColumnDefinition(GridLength.Auto),   // 网页按钮
                    new ColumnDefinition(GridLength.Auto),   // 源代码按钮
                },
                Padding = new Thickness(12, 6),
                ColumnSpacing = 8,
                BackgroundColor = HeaderBackgroundColor,
            };

            // ---- 语言标签 ----
            var langLabel = new Label
            {
                Text = Language,
                FontSize = 11,
                TextColor = Markdown2XAML.CodeBlockTextColor.WithAlpha(0.6f),
                FontFamily = "MarkdownCodeBlock",
                VerticalOptions = LayoutOptions.Center,
            };
            Grid.SetColumn(langLabel, 0);
            headerBar.Children.Add(langLabel);

            // ---- 两个切换按钮 ----
            var webBtn = CreateToggleButton(Localize.APIBaseLocalizedResources.Localized.MarkdownToXAML_HtmlCodeblock_Preivew, true);
            var sourceBtn = CreateToggleButton(Localize.APIBaseLocalizedResources.Localized.MarkdownToXAML_HtmlCodeblock_Code, false);

            Grid.SetColumn(webBtn, 2);
            Grid.SetColumn(sourceBtn, 3);
            headerBar.Children.Add(webBtn);
            headerBar.Children.Add(sourceBtn);

            // 网页按钮点击 → 显示 WebView
            var webTap = new TapGestureRecognizer();
            webTap.Tapped += (_, _) =>
            {
                contentArea.Content = webView;
                SetActiveButton(webBtn, true);
                SetActiveButton(sourceBtn, false);
            };
            webBtn.GestureRecognizers.Add(webTap);

            // 源代码按钮点击 → 显示源代码
            var sourceTap = new TapGestureRecognizer();
            sourceTap.Tapped += (_, _) =>
            {
                contentArea.Content = sourceView;
                SetActiveButton(webBtn, false);
                SetActiveButton(sourceBtn, true);
            };
            sourceBtn.GestureRecognizers.Add(sourceTap);

            return headerBar;
        }

        /// <summary>
        /// 创建一个切换按钮。
        /// </summary>
        /// <param name="text">按钮文本</param>
        /// <param name="isActive">是否为激活状态</param>
        private static Border CreateToggleButton(string text, bool isActive)
        {
            var label = new Label
            {
                Text = text,
                FontSize = 12,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                TextColor = isActive ? Colors.White : InactiveTextColor,
            };

            return new Border
            {
                Content = label,
                BackgroundColor = isActive ? ActiveTabColor : Colors.Transparent,
                Stroke = isActive ? ActiveTabColor : InactiveBorderColor,
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 4 },
                Padding = new Thickness(10, 2),
                HeightRequest = ButtonHeight,
                VerticalOptions = LayoutOptions.Center,
            };
        }

        /// <summary>
        /// 更新按钮的激活/非激活视觉状态。
        /// </summary>
        private static void SetActiveButton(Border button, bool isActive)
        {
            if (button.Content is Label label)
            {
                label.TextColor = isActive ? Colors.White : InactiveTextColor;
            }
            button.BackgroundColor = isActive ? ActiveTabColor : Colors.Transparent;
            button.Stroke = isActive ? ActiveTabColor : InactiveBorderColor;
        }

        /// <summary>
        /// 创建渲染后的 WebView。
        /// </summary>
        private WebView CreateWebView(string code)
        {
            return new WebView
            {
                Source = new HtmlWebViewSource { Html = WrapHtml(code) },
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill,
                HeightRequest = ContentHeight,
            };
        }

        /// <summary>
        /// 创建源代码视图（带滚动）。
        /// </summary>
        private static View CreateSourceView(string code)
        {
            return new ScrollView
            {
                Content = new Label
                {
                    Text = code,
                    FontFamily = "MarkdownCodeBlock",
                    FontSize = 13,
                    TextColor = Markdown2XAML.CodeBlockTextColor,
                },
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill,
                MaximumHeightRequest = ContentHeight,
                Padding = Markdown2XAML.CodeBlockPadding,
            };
        }
    }

    public class MermaidCodeBlockRenderer : HtmlCodeBlockRenderer
    {
        public override string Language => "mermaid";

        public override string WrapHtml(string code)
        {
            return 
                $$"""
                <!doctype html>
                <html lang="en">
                <body style="background-color: {{Markdown2XAML.CodeBlockBackgroundColor.ToArgbHex(false)}};">
                  <pre class="mermaid">
                    {{code}}
                  </pre>

                  <script type="module">
                    import mermaid from 'https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.esm.min.mjs';
                    await mermaid.initialize({
                        theme: '{{(AppInfo.RequestedTheme == AppTheme.Dark ? "default" : "dark")}}',
                        startOnLoad: true,
                    });
                  </script>
                </body>
                </html>
                """;
        }
    }
}
