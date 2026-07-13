namespace projectFrameCut.AIAssistance;

using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.ApplicationAPIBase.Localize;
using System.Text.RegularExpressions;

/// <summary>
/// 展示 AI 助手回复内容的预览 Popup，支持 HTML 渲染视图和原始 Markdown 视图之间的切换，
/// 并提供"复制所有内容"按钮。
/// </summary>
public partial class AIReplyCopyPopup : Popup
{
    // ===== 颜色常量（与 HtmlCodeBlockRenderer 保持一致） =====
    private static readonly Color ActiveTabColor = Color.FromArgb("#4A90D9");
    private static readonly Color InactiveBorderColor = Color.FromArgb("#D0D0D0");
    private static readonly Color InactiveTextColor = Color.FromArgb("#888888");
    private static readonly Color HeaderBackgroundColor = Color.FromArgb("#08000000");
    private static readonly Color CodeBlockTextColor = Color.FromArgb("#000000");
    private static readonly Color PopupContentBg = Color.FromArgb("#FFFFFF");
    private static readonly Color SeparatorColor = Color.FromArgb("#E0E0E0");

    // ===== 暗色模式颜色 =====
    private static readonly Color CodeBlockTextColorDark = Color.FromArgb("#D4D4D4");
    private static readonly Color PopupContentBgDark = Color.FromArgb("#252526");
    private static readonly Color SeparatorColorDark = Color.FromArgb("#333333");

    // ===== 布局常量 =====
    private const double PopupContentWidth = 720;
    private const double PopupContentHeight = 520;
    private const double TabButtonHeight = 28;

    private readonly string _markdownContent;
    private readonly WebView _webView;
    private readonly Editor _markdownEditor;
    private readonly ContentView _contentArea;
    private readonly Border _htmlTabButton;
    private readonly Border _markdownTabButton;
    private readonly Button _copyButton;
    private readonly Label _copyFeedbackLabel;

    private bool _isHtmlView = true;

    public AIReplyCopyPopup(string markdownContent)
    {
        _markdownContent = markdownContent ?? string.Empty;
        CanBeDismissedByTappingOutsideOfPopup = true;

        // 构建 WebView 和 Editor
        _webView = CreateWebView();
        _markdownEditor = CreateEditor();

        // 标签切换栏
        var tabBar = CreateTabBar(markdownContent.Contains("```xaml"), out _htmlTabButton, out _markdownTabButton);
        WireTabEvents();

        // 内容区域
        _contentArea = new ContentView
        {
            VerticalOptions = LayoutOptions.Fill,
            HorizontalOptions = LayoutOptions.Fill,
            Content = _webView,
        };

        // 分隔线
        var sepColor = IsDarkTheme ? SeparatorColorDark : SeparatorColor;
        var tabSeparator = new BoxView { HeightRequest = 1, HorizontalOptions = LayoutOptions.Fill, Color = sepColor };
        var bottomSeparator = new BoxView { HeightRequest = 1, HorizontalOptions = LayoutOptions.Fill, Color = sepColor };

        // 底部复制栏
        var bottomBar = CreateBottomBar(out _copyButton, out _copyFeedbackLabel);
        _copyButton.Clicked += OnCopyClicked;

        // 组装布局
        var rootLayout = new Grid
        {
            WidthRequest = PopupContentWidth,
            HeightRequest = PopupContentHeight,
            BackgroundColor = IsDarkTheme ? PopupContentBgDark : PopupContentBg,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
            },
            RowSpacing = 0,
        };

        Grid.SetRow(tabBar, 0);
        Grid.SetRow(tabSeparator, 1);
        Grid.SetRow(_contentArea, 2);
        Grid.SetRow(bottomSeparator, 3);
        Grid.SetRow(bottomBar, 4);

        rootLayout.Children.Add(tabBar);
        rootLayout.Children.Add(tabSeparator);
        rootLayout.Children.Add(_contentArea);
        rootLayout.Children.Add(bottomSeparator);
        rootLayout.Children.Add(bottomBar);

        // 带圆角和阴影的外层 Border
        Content = new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            Stroke = Colors.Transparent,
            StrokeThickness = 0,
            BackgroundColor = IsDarkTheme ? PopupContentBgDark : PopupContentBg,
            Shadow = new Shadow
            {
                Brush = new SolidColorBrush(Color.FromArgb("#40000000")),
                Offset = new Point(0, 4),
                Radius = 16,
                Opacity = 0.5f,
            },
            Content = rootLayout,
        };
    }

    private static bool IsDarkTheme => AppInfo.RequestedTheme == AppTheme.Dark;

    // =========================================
    // HTML 模板
    // =========================================

    private const string HtmlTemplate =
        // lang=html
        """
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <style>
                body { margin:16px; padding:0; font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif; font-size:14px; line-height:1.6; color:__TEXT_COLOR__; background:__BG_COLOR__; }
                pre { background:__CODE_BG__; padding:12px; border-radius:6px; overflow-x:auto; border:1px solid __BORDER_COLOR__; }
                code { background:__CODE_BG__; padding:2px 6px; border-radius:3px; font-size:0.9em; }
                pre code { background:none; padding:0; border:none; }
                table { border-collapse:collapse; width:100%; margin:8px 0; }
                th, td { border:1px solid __BORDER_COLOR__; padding:8px 12px; text-align:left; }
                th { background:__CODE_BG__; font-weight:600; }
                img { max-width:100%; height:auto; }
                blockquote { border-left:4px solid __BORDER_COLOR__; margin:12px 0; padding:4px 16px; color:__QUOTE_COLOR__; }
                h1, h2, h3, h4 { margin-top:20px; margin-bottom:10px; }
                h1 { font-size:1.6em; }
                h2 { font-size:1.3em; }
                h3 { font-size:1.1em; }
                p { margin:8px 0; }
                ul, ol { margin:8px 0; padding-left:24px; }
                a { color:#4A90D9; }
                hr { border:none; border-top:1px solid __BORDER_COLOR__; margin:16px 0; }
            </style>
        </head>
        <body>
            <div id="content"></div>
            <script src="https://cdn.jsdelivr.net/npm/marked/marked.min.js">
            </script>
            <script>
                (function() {
                    try {
                        var markdown = decodeURIComponent("__URI_ENCODED_MARKDOWN__");
                        document.getElementById('content').innerHTML = marked.parse(markdown);
                    } catch(e) {
                        document.getElementById('content').innerText = 'Render failed: ' + e.message;
                    }
                })();
            </script>
        </body>
        </html>
        """;

    private string BuildHtmlDocument()
    {
        var regex = XAMLCodeblockRegex();
        var escapedXAMLCodeblock = regex.Replace(_markdownContent, $"`{Localized.AIAssistant_AIReplyCopyPopup_XAMLBlockEscaped}`");
        // 使用 Uri.EscapeDataString 进行百分号编码，在 JS 端用 decodeURIComponent 解码，
        // 这样可以正确保留中文等多字节 UTF-8 字符（atob + base64 会破坏 UTF-8）。
        var encoded = Uri.EscapeDataString(_markdownContent);
        var dark = IsDarkTheme;

        return HtmlTemplate
            .Replace("__URI_ENCODED_MARKDOWN__", encoded)
            .Replace("__TEXT_COLOR__", dark ? "#D4D4D4" : "#1A1A1A")
            .Replace("__BG_COLOR__", dark ? "#1E1E1E" : "#FFFFFF")
            .Replace("__CODE_BG__", dark ? "#2D2D2D" : "#F5F5F5")
            .Replace("__BORDER_COLOR__", dark ? "#444444" : "#E0E0E0")
            .Replace("__QUOTE_COLOR__", dark ? "#888888" : "#666666");
    }

    // =========================================
    // UI 组件构建
    // =========================================

    private WebView CreateWebView()
    {
        return new WebView
        {
            Source = new HtmlWebViewSource { Html = BuildHtmlDocument() },
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
        };
    }

    private Editor CreateEditor()
    {
        return new Editor
        {
            Text = _markdownContent,
            IsReadOnly = true,
            FontFamily = "MarkdownCodeBlock",
            FontSize = 13,
            TextColor = IsDarkTheme ? CodeBlockTextColorDark : CodeBlockTextColor,
            BackgroundColor = Colors.Transparent,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            Margin = new Thickness(8, 4),
        };
    }

    private static Grid CreateTabBar(bool haveXAML, out Border htmlTab, out Border markdownTab)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),   
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
            },
            Padding = new Thickness(12, 8),
            ColumnSpacing = 8,
            BackgroundColor = HeaderBackgroundColor,
        };

        if (haveXAML)
        {
            var warnLabel = new Label
            {
                Text = Localized.AIAssistant_AIReplyCopyPopup_XAMLBlockEscapedWarn,
                FontSize = 12,
                Opacity = 0.8
            };
            Grid.SetColumn(warnLabel, 0);
            grid.Children.Add(warnLabel);
        }

        htmlTab = CreateTabButton(APIBaseLocalizedResources.Localized.MarkdownToXAML_HtmlCodeblock_Preivew, true);
        markdownTab = CreateTabButton(APIBaseLocalizedResources.Localized.MarkdownToXAML_HtmlCodeblock_Code, false);

        Grid.SetColumn(htmlTab, 2);
        Grid.SetColumn(markdownTab, 3);
        grid.Children.Add(htmlTab);
        grid.Children.Add(markdownTab);

        return grid;
    }

    private static Border CreateTabButton(string text, bool isActive)
    {
        return new Border
        {
            Content = new Label
            {
                Text = text,
                FontSize = 12,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                TextColor = isActive ? Colors.White : InactiveTextColor,
            },
            BackgroundColor = isActive ? ActiveTabColor : Colors.Transparent,
            Stroke = isActive ? ActiveTabColor : InactiveBorderColor,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 4 },
            Padding = new Thickness(12, 4),
            HeightRequest = TabButtonHeight,
            VerticalOptions = LayoutOptions.Center,
        };
    }

    private static void SetTabButtonActive(Border button, bool isActive)
    {
        if (button.Content is Label label)
            label.TextColor = isActive ? Colors.White : InactiveTextColor;
        button.BackgroundColor = isActive ? ActiveTabColor : Colors.Transparent;
        button.Stroke = isActive ? ActiveTabColor : InactiveBorderColor;
    }

    private static Grid CreateBottomBar(out Button copyBtn, out Label feedbackLabel)
    {
        copyBtn = new Button
        {
            Text = Localized.AIAssistant_AIReplyCopyPopup_Copy,
            FontSize = 13,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            HeightRequest = 30,
            Padding = new Thickness(16, 0),
            BackgroundColor = ActiveTabColor,
            TextColor = Colors.White,
            CornerRadius = 6,
        };

        feedbackLabel = new Label
        {
            Text = Localized._Done,
            FontSize = 13,
            TextColor = Color.FromArgb("#4CAF50"),
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center,
            IsVisible = false,
            Margin = new Thickness(4, 0, 12, 0),
        };

        var bar = new Grid
        {
            HeightRequest = 44,
            Padding = new Thickness(12, 0),
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
        };
        Grid.SetColumn(copyBtn, 0);
        Grid.SetColumn(feedbackLabel, 1);
        bar.Children.Add(copyBtn);
        bar.Children.Add(feedbackLabel);

        return bar;
    }

    // =========================================
    // 事件绑定
    // =========================================

    private void WireTabEvents()
    {
        var htmlTap = new TapGestureRecognizer();
        htmlTap.Tapped += (_, _) =>
        {
            if (_isHtmlView) return;
            _isHtmlView = true;
            _contentArea.Content = _webView;
            SetTabButtonActive(_htmlTabButton, true);
            SetTabButtonActive(_markdownTabButton, false);
        };
        _htmlTabButton.GestureRecognizers.Add(htmlTap);

        var mdTap = new TapGestureRecognizer();
        mdTap.Tapped += (_, _) =>
        {
            if (!_isHtmlView) return;
            _isHtmlView = false;
            _contentArea.Content = _markdownEditor;
            SetTabButtonActive(_htmlTabButton, false);
            SetTabButtonActive(_markdownTabButton, true);
        };
        _markdownTabButton.GestureRecognizers.Add(mdTap);
    }

    private async void OnCopyClicked(object? sender, EventArgs e)
    {
        try
        {
            if (_isHtmlView)
                await CopyHtmlContentAsync();
            else
                await Clipboard.Default.SetTextAsync(_markdownContent);

            await ShowCopyFeedbackAsync();
        }
        catch (Exception ex)
        {
            _copyFeedbackLabel.Text = Localized._ExceptionTemplate(ex);
            _copyFeedbackLabel.TextColor = Color.FromArgb("#F44336");
            _copyFeedbackLabel.IsVisible = true;
            await Task.Delay(2000);
            _copyFeedbackLabel.IsVisible = false;
        }
    }

    private async Task CopyHtmlContentAsync()
    {
        const string selectAndCopyScript = """
            (function() {
                try {
                    var content = document.getElementById('content');
                    var range = document.createRange();
                    range.selectNodeContents(content);
                    var selection = window.getSelection();
                    selection.removeAllRanges();
                    selection.addRange(range);
                    return document.execCommand('copy') ? 'ok' : 'fail';
                } catch(e) {
                    return 'error: ' + e.message;
                }
            })();
            """;

        var result = await _webView.EvaluateJavaScriptAsync(selectAndCopyScript);
        if (result == "fail" || result == null)
        {
            // fallback: Clipboard API
            const string clipboardApiScript = """
                (function() {
                    try {
                        var txt = document.getElementById('content').innerText;
                        navigator.clipboard.writeText(txt);
                        return 'ok';
                    } catch(e) {
                        return 'fail';
                    }
                })();
                """;
            await _webView.EvaluateJavaScriptAsync(clipboardApiScript);
        }
    }

    private async Task ShowCopyFeedbackAsync()
    {
        _copyButton.IsVisible = false;
        _copyFeedbackLabel.Text = Localized._Done;
        _copyFeedbackLabel.TextColor = Color.FromArgb("#4CAF50");
        _copyFeedbackLabel.IsVisible = true;
        await Task.Delay(1500);
        _copyFeedbackLabel.IsVisible = false;
        _copyButton.IsVisible = true;
    }

    [GeneratedRegex("(?:^|\\n) {0,3}(`{3,}|~{3,})[ \\t]*(xaml)[ \\t]*\\n([\\s\\S]*?)\\n {0,3}\\1[ \\t]*(?=\\n|$)")]
    private static partial Regex XAMLCodeblockRegex();
}
