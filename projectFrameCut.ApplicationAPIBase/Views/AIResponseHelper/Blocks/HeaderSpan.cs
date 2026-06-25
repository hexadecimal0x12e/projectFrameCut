using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.ApplicationAPIBase.Views.AIResponseHelper.Blocks
{
    public partial class HeaderSpan : Span
    {
        public static BindableProperty HeaderLevelProperty = BindableProperty.Create(nameof(HeaderLevel), typeof(HeaderLevel), typeof(HeaderSpan), HeaderLevel.None,propertyChanged: HandleHeaderLevelChanged);

        public HeaderLevel HeaderLevel
        {
            get { return (HeaderLevel)GetValue(HeaderLevelProperty); }
            set { SetValue(HeaderLevelProperty, value); }
        }

        private static void HandleHeaderLevelChanged(BindableObject bindable, object oldValue, object newValue)
        {
            if (bindable is not Span s) return;
            s.FontSize = newValue switch
            {
                HeaderLevel.H1 => 32,
                HeaderLevel.H2 => 28,
                HeaderLevel.H3 => 24,
                HeaderLevel.H4 => 20,
                HeaderLevel.H5 => 16,
                HeaderLevel.H6 => 12,
                _ => 14
            };
            s.FontAttributes = FontAttributes.Bold;
        }

        public HeaderSpan()
        {

        }
    }

    public enum HeaderLevel
    {
        None = 0,
        H1 = 1,
        H2 = 2,
        H3 = 3,
        H4 = 4,
        H5 = 5,
        H6 = 6,
    }
}
