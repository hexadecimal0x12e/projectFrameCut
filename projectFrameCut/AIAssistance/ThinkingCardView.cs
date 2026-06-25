using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace projectFrameCut.AIAssistance;

/// <summary>
/// 可折叠的"思考中"卡片控件。
/// 流式传输时文字可不断更新，用户可点击标题栏展开/折叠。
/// 通过 <see cref="View"/> 属性获取可添加到布局的 View。
/// </summary>
public class ThinkingCardView
{
    private readonly Border _root;
    private readonly Border _contentBorder;
    private readonly Label _contentLabel;
    private readonly Label _toggleIcon;
    private readonly Label _collapseIndicator;
    private bool _isExpanded = true;

    private static readonly Color LightToggleColor = Color.FromArgb("#FF666666");
    private static readonly Color DarkToggleColor = Color.FromArgb("#FFAAAAAA");
    private static readonly Color LightTitleColor = Color.FromArgb("#FF445566");
    private static readonly Color DarkTitleColor = Color.FromArgb("#FF8899BB");
    private static readonly Color LightCollapseColor = Color.FromArgb("#FF888888");
    private static readonly Color DarkCollapseColor = Color.FromArgb("#FF777777");
    private static readonly Color LightHeaderBg = Color.FromArgb("#FFE8EEF6");
    private static readonly Color DarkHeaderBg = Color.FromArgb("#FF28303F");
    private static readonly Color LightContentColor = Color.FromArgb("#FF666666");
    private static readonly Color DarkContentColor = Color.FromArgb("#FFBBBBBB");
    private static readonly Color LightRootBg = Color.FromArgb("#FFF0F4FA");
    private static readonly Color DarkRootBg = Color.FromArgb("#FF1E2633");

    /// <summary>获取此卡片的根 View，可添加到任意 Layout 中。</summary>
    public View View => _root;

    public ThinkingCardView(string initialText = "")
    {
        // Toggle icon
        _toggleIcon = new Label
        {
            Text = "▼",
            FontSize = 9,
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 0, 4, 0),
        };
        _toggleIcon.SetAppThemeColor(Label.TextColorProperty, LightToggleColor, DarkToggleColor);

        // Title label
        var titleLabel = new Label
        {
            Text = Localized.AIAssistant_ChatView_Thinking,
            FontSize = 11,
            FontAttributes = FontAttributes.Bold,
            VerticalOptions = LayoutOptions.Center,
        };
        titleLabel.SetAppThemeColor(Label.TextColorProperty, LightTitleColor, DarkTitleColor);

        // Collapse indicator (shown when collapsed)
        _collapseIndicator = new Label
        {
            Text = "…",
            FontSize = 10,
            VerticalOptions = LayoutOptions.Center,
            IsVisible = false,
        };
        _collapseIndicator.SetAppThemeColor(Label.TextColorProperty, LightCollapseColor, DarkCollapseColor);

        // Header grid
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
        headerGrid.Add(titleLabel, 1);
        headerGrid.Add(_collapseIndicator, 2);

        var tapGesture = new TapGestureRecognizer();
        tapGesture.Tapped += (_, _) => ToggleExpanded();
        headerGrid.GestureRecognizers.Add(tapGesture);

        // Content label
        _contentLabel = new Label
        {
            Text = initialText,
            FontSize = 11,
            LineBreakMode = LineBreakMode.WordWrap,
        };
        _contentLabel.SetAppThemeColor(Label.TextColorProperty, LightContentColor, DarkContentColor);

        // Content border (collapsible area)
        _contentBorder = new Border
        {
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(0, 0, 6, 6) },
            Padding = new Thickness(8, 6),
            BackgroundColor = Colors.Transparent,
            Content = _contentLabel,
        };

        // Root structure
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

    /// <summary>更新思考文本内容（流式追加）。</summary>
    public void UpdateText(string text)
    {
        if (MainThread.IsMainThread)
        {
            _contentLabel.Text = text;
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(() => _contentLabel.Text = text);
        }
    }

    /// <summary>切换展开/折叠状态。</summary>
    public void ToggleExpanded()
    {
        _isExpanded = !_isExpanded;
        _contentBorder.IsVisible = _isExpanded;
        _toggleIcon.Text = _isExpanded ? "▼" : "▶";
        _collapseIndicator.IsVisible = !_isExpanded;
    }
}
