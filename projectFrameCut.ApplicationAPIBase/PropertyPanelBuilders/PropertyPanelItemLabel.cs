using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.ApplicationAPIBase.PropertyPanelBuilders
{
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

    public class PropertyPanelItemLabel
    {
        private View? _view;

        public PropertyPanelItemLabel() { }
        public PropertyPanelItemLabel(View v) => _view = v;
        public virtual View LabelConfigure() => _view ?? throw new NullReferenceException("Trying to set a null label.");

        public static implicit operator PropertyPanelItemLabel(string text) => new SingleLineLabel(text);

        public static implicit operator PropertyPanelItemLabel(Label src) => new PropertyPanelItemLabel { _view = src };
    }
}
