namespace projectFrameCut.AIAssistance;

using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using projectFrameCut.ApplicationAPIBase.Views.MarkdownToXAML;

/// <summary>
/// 可折叠的"已关闭的子 Agent 会话"卡片控件。
/// 显示子 Agent 的完整对话历史，默认折叠。
/// </summary>
internal sealed class ClosedAgentCardView
{
    private readonly Border _root;
    private readonly Border _contentBorder;
    private readonly VerticalStackLayout _messagesContainer;
    private readonly Label _toggleIcon;
    private readonly Label _titleLabel;
    private readonly Label _collapseIndicator;
    private bool _isExpanded;

    private static readonly Color LightToggleColor = Color.FromArgb("#FF666666");
    private static readonly Color DarkToggleColor = Color.FromArgb("#FFAAAAAA");
    private static readonly Color LightTitleColor = Color.FromArgb("#FF445566");
    private static readonly Color DarkTitleColor = Color.FromArgb("#FF8899BB");
    private static readonly Color LightCollapseColor = Color.FromArgb("#FF888888");
    private static readonly Color DarkCollapseColor = Color.FromArgb("#FF777777");
    private static readonly Color LightHeaderBg = Color.FromArgb("#FFE0E8F0");
    private static readonly Color DarkHeaderBg = Color.FromArgb("#FF28303F");
    private static readonly Color LightContentBg = Color.FromArgb("#FFF5F8FC");
    private static readonly Color DarkContentBg = Color.FromArgb("#FF1A2230");
    private static readonly Color LightRootBg = Color.FromArgb("#FFECF0F5");
    private static readonly Color DarkRootBg = Color.FromArgb("#FF1A1E28");
    private static readonly Color LightUserColor = Color.FromArgb("#FF222222");
    private static readonly Color DarkUserColor = Color.FromArgb("#FFEEEEEE");
    private static readonly Color LightAssistantColor = Color.FromArgb("#FF555555");
    private static readonly Color DarkAssistantColor = Color.FromArgb("#FFBBBBBB");
    private static readonly Color LightSeparatorColor = Color.FromArgb("#FFD0D8E0");
    private static readonly Color DarkSeparatorColor = Color.FromArgb("#FF30384A");

    /// <summary>获取此卡片的根 View，可添加到任意 Layout 中。</summary>
    public View View => _root;

    public ClosedAgentCardView(ClosedSubAgentSnapshot session)
    {
        int msgCount = session.Messages.Count;
        int userCount = session.Messages.Count(m => m.IsUser);
        int assistantCount = msgCount - userCount;

        // Toggle icon
        _toggleIcon = new Label
        {
            Text = "▶", // ▶
            FontSize = 9,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 0, 4, 0),
        };
        _toggleIcon.SetAppThemeColor(Label.TextColorProperty, LightToggleColor, DarkToggleColor);

        // Title
        _titleLabel = new Label
        {
            Text = $"Sub-Agent: {session.Title}  ({userCount} + {assistantCount} msgs)",
            FontSize = 11,
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        _titleLabel.SetAppThemeColor(Label.TextColorProperty, LightTitleColor, DarkTitleColor);

        // Collapse indicator
        _collapseIndicator = new Label
        {
            Text = "…", // …
            FontSize = 10,
            VerticalOptions = LayoutOptions.Center,
            IsVisible = true,
        };
        _collapseIndicator.SetAppThemeColor(Label.TextColorProperty, LightCollapseColor, DarkCollapseColor);

        // Header
        var headerGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            Padding = new Thickness(6, 4),
        };
        headerGrid.SetAppThemeColor(Grid.BackgroundColorProperty, LightHeaderBg, DarkHeaderBg);
        headerGrid.Add(_toggleIcon, 0);
        headerGrid.Add(_titleLabel, 1);
        headerGrid.Add(_collapseIndicator, 2);

        var tapGesture = new TapGestureRecognizer();
        tapGesture.Tapped += (_, _) => ToggleExpanded();
        headerGrid.GestureRecognizers.Add(tapGesture);

        // Messages container
        _messagesContainer = new VerticalStackLayout
        {
            Spacing = 0,
            Padding = new Thickness(0),
        };

        // Render each message from the closed session
        foreach (var msg in session.Messages)
        {
            var messageView = CreateMessageView(msg);
            _messagesContainer.Children.Add(messageView);
        }

        // Content border (collapsible)
        _contentBorder = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(0, 0, 6, 6) },
            Padding = new Thickness(4, 4),
            Content = _messagesContainer,
            IsVisible = false,
        };
        _contentBorder.SetAppThemeColor(Border.BackgroundColorProperty, LightContentBg, DarkContentBg);

        // Root
        var stack = new VerticalStackLayout { Spacing = 0 };
        stack.Children.Add(headerGrid);
        stack.Children.Add(_contentBorder);

        _root = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            Padding = 0,
            Content = stack,
        };
        _root.SetAppThemeColor(Border.BackgroundColorProperty, LightRootBg, DarkRootBg);
    }

    /// <summary>切换展开/折叠状态。</summary>
    public void ToggleExpanded()
    {
        _isExpanded = !_isExpanded;
        _contentBorder.IsVisible = _isExpanded;
        _toggleIcon.Text = _isExpanded ? "▼" : "▶"; // ▼ / ▶
        _collapseIndicator.IsVisible = !_isExpanded;
    }

    /// <summary>
    /// 为一条已保存的消息创建显示 View。
    /// </summary>
    private static View CreateMessageView(AssistanceChatMessageSnapshot msg)
    {
        var messageStack = new VerticalStackLayout
        {
            Spacing = 2,
            Padding = new Thickness(8, 4),
        };

        // Sender label
        var senderLabel = new Label
        {
            Text = msg.Sender,
            FontSize = 10,
            FontAttributes = FontAttributes.Bold,
        };
        senderLabel.SetAppThemeColor(Label.TextColorProperty,
            msg.IsUser ? LightUserColor : LightAssistantColor,
            msg.IsUser ? DarkUserColor : DarkAssistantColor);

        messageStack.Children.Add(senderLabel);

        // Message content
        if (msg.IsUser && !string.IsNullOrWhiteSpace(msg.Message))
        {
            // User messages: plain text
            var contentLabel = new Label
            {
                Text = msg.Message,
                FontSize = 11,
                LineBreakMode = LineBreakMode.WordWrap,
            };
            contentLabel.SetAppThemeColor(Label.TextColorProperty, LightUserColor, DarkUserColor);
            messageStack.Children.Add(contentLabel);
        }
        else if (!msg.IsUser)
        {
            // Assistant messages: try to render reasoning/toolcalls + text
            if (!string.IsNullOrWhiteSpace(msg.ReasoningText))
            {
                messageStack.Children.Add(new Label
                {
                    Text = $"Thinking: {msg.ReasoningText}",
                    FontSize = 10,
                    FontAttributes = FontAttributes.Italic,
                    TextColor = Color.FromArgb("#FF888888"),
                    LineBreakMode = LineBreakMode.WordWrap,
                });
            }

            if (!string.IsNullOrWhiteSpace(msg.ToolCallsText))
            {
                messageStack.Children.Add(new Label
                {
                    Text = $"[Tool: {msg.ToolCallsText}]",
                    FontSize = 10,
                    FontFamily = "MarkdownCodeBlock",
                    TextColor = Color.FromArgb("#FFBB8844"),
                    LineBreakMode = LineBreakMode.WordWrap,
                });
            }

            if (!string.IsNullOrWhiteSpace(msg.Message))
            {
                try
                {
                    // Use Markdown2XAML for assistant messages
                    var mdView = Markdown2XAML.Convert(msg.Message);
                    mdView.MaximumWidthRequest = 500;
                    messageStack.Children.Add(mdView);
                }
                catch
                {
                    messageStack.Children.Add(new Label
                    {
                        Text = msg.Message,
                        FontSize = 11,
                        LineBreakMode = LineBreakMode.WordWrap,
                    });
                }
            }
        }

        return messageStack;
    }
}
