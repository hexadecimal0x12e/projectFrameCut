using System;
using System.Collections.Generic;
using System.Text;
using static projectFrameCut.ApplicationAPIBase.Localize.APIBaseLocalizedResources;

namespace projectFrameCut.ApplicationAPIBase.Views.AIResponseHelper
{
    public partial class HyperlinkSpan : Span
    {
        public static readonly BindableProperty UrlProperty =
            BindableProperty.Create(nameof(Url), typeof(string), typeof(HyperlinkSpan), null);

        public string Url
        {
            get { return (string)GetValue(UrlProperty); }
            set { SetValue(UrlProperty, value); }
        }

        public HyperlinkSpan()
        {
            TextDecorations = TextDecorations.Underline;
            TextColor = AppInfo.RequestedTheme == AppTheme.Dark
                ? Color.FromArgb("#82C7FF")
                : Color.FromArgb("#1A73E8");
            GestureRecognizers.Add(new TapGestureRecognizer
            {
                // Launcher.OpenAsync is provided by Essentials.
                Command = new Command(async () =>
                {
                    if(await (Application.Current?.Windows?[0]?.Page?.DisplayAlertAsync(Localized._Warn, Localized.AIResponseHelper_HyperlinkSpan_SureOpen(Url), Localized._OK, Localized._Cancel) ?? Task.FromResult(false)))
                    {
                        await Launcher.OpenAsync(Url);
                    }
                })
            });
        }
    }
}
