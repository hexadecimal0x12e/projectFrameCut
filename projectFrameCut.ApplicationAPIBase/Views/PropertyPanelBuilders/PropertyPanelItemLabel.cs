using Microsoft.Maui.Controls.Shapes;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders
{
    [DebuggerNonUserCode()]
    public class SingleLineLabel(string text, int fontsize = 14, FontAttributes fontAttributes = FontAttributes.None, Color? TextColor = null) : PropertyPanelItemLabel
    {
        public override View LabelConfigure()
        {
            var l = new Label { Text = text, FontSize = fontsize, FontAttributes = fontAttributes, VerticalOptions = LayoutOptions.Center };
            if (TextColor is not null) l.TextColor = TextColor;
            return l;
        }

        public static implicit operator SingleLineLabel(string text) => new SingleLineLabel(text);
    }

    [DebuggerNonUserCode()]
    public class TitleAndDescriptionLineLabel(string title, string description, int titleFontSize = 25, int contentFontSize = 14) : PropertyPanelItemLabel
    {
        public override View LabelConfigure() => new VerticalStackLayout
        {
            Children =
            {
                new Label { Text = title, FontSize = titleFontSize, FontAttributes = FontAttributes.Bold },
                new Label { Text = description, FontSize = contentFontSize }
            }
        };
    }

    [DebuggerNonUserCode()]
    public class PropertyPanelItemLabel
    {
        private View? _view;

        public PropertyPanelItemLabel() { }
        public PropertyPanelItemLabel(View v) => _view = v;
        public virtual View LabelConfigure() => _view ?? throw new NullReferenceException("Trying to set a null label.");

        public static implicit operator PropertyPanelItemLabel(string text) => new SingleLineLabel(text);

        public static implicit operator PropertyPanelItemLabel(Label src) => new PropertyPanelItemLabel { _view = src };
    }

    [DebuggerNonUserCode()]
    public class InfoSingleLineLabel(string text, string infoText, int fontsize = 14, FontAttributes fontAttributes = FontAttributes.None, Color? TextColor = null) : PropertyPanelItemLabel
    {
        public override View LabelConfigure()
        {
            var mainLabel = new Label { Text = text, FontSize = fontsize, FontAttributes = fontAttributes, VerticalOptions = LayoutOptions.Center };
            if (TextColor is not null) mainLabel.TextColor = TextColor;

            var infoFrame = new Border
            {
                WidthRequest = 16,
                HeightRequest = 16,
                StrokeShape = new RoundRectangle { CornerRadius = 14 },
                BackgroundColor = Microsoft.Maui.Graphics.Colors.Transparent,
                Stroke = Microsoft.Maui.Graphics.Color.FromArgb("#2FA6FF"),
                Padding = new Thickness(0),
                Content = new Label { Text = "i", FontSize = Math.Max(12, fontsize - 2), TextColor = Microsoft.Maui.Graphics.Color.FromArgb("#2FA6FF"), HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center },
                VerticalOptions = LayoutOptions.Center
            };

            var popupFrame = new Border
            {
                IsVisible = false,
                StrokeShape = new RoundRectangle { CornerRadius = 8 },
                BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#2D2D2D"),
                Padding = new Thickness(8),
                Content = new Label { Text = infoText, TextColor = Microsoft.Maui.Graphics.Colors.White }
            };

            var tap = new TapGestureRecognizer();
            tap.Tapped += (s, e) =>
            {
                popupFrame.IsVisible = !popupFrame.IsVisible;
            };
            infoFrame.GestureRecognizers.Add(tap);

            return new VerticalStackLayout
            {
                Spacing = 2,
                Children =
                {
                    new HorizontalStackLayout
                    {
                        Spacing = 8,
                        VerticalOptions = LayoutOptions.Center,
                        Children = { mainLabel, infoFrame }
                    },
                    popupFrame
                }
            };
        }

        public static implicit operator InfoSingleLineLabel(string text) => new InfoSingleLineLabel(text, "");
    }

    [DebuggerNonUserCode()]
    public class InfoPopup : CommunityToolkit.Maui.Views.Popup
    {
        public InfoPopup(string info)
        {
            Content = new Frame
            {
                CornerRadius = 8,
                BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#2D2D2D"),
                Padding = new Thickness(12),
                Content = new Label { Text = info, TextColor = Microsoft.Maui.Graphics.Colors.White }
            };
        }
    }
}
