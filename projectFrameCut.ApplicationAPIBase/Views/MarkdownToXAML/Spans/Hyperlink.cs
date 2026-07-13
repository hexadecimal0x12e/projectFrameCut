using projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;
using System;
using System.Collections.Generic;
using System.Text;
using static projectFrameCut.ApplicationAPIBase.Localize.APIBaseLocalizedResources;

namespace projectFrameCut.ApplicationAPIBase.Views.MarkdownToXAML
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
                    try
                    {
                        Element parent = this.Parent;
                        int i = 0;
                        for (; i <= 64; i++)
                        {
                            if (parent is null) break;
                            if (parent is MultiWindowItem mvi)
                            {
                                if (!(await mvi.DisplayAlertAsync(Localized._Warn, Localized.MarkdownToXAML_HyperlinkSpan_SureOpen(Url), Localized._OK, Localized._Cancel))) return;
                                break;
                            }
                            else if (parent is ContentPage p)
                            {
                                if (!(await p.DisplayAlertAsync(Localized._Warn, Localized.MarkdownToXAML_HyperlinkSpan_SureOpen(Url), Localized._OK, Localized._Cancel))) return;
                                break;
                            }

                            parent = parent?.Parent;
                        }
                        if (i >= 63)
                        {
                            if (!(await (Application.Current?.Windows?[0]?.Page?.DisplayAlertAsync(Localized._Warn, Localized.MarkdownToXAML_HyperlinkSpan_SureOpen(Url), Localized._OK, Localized._Cancel) ?? Task.FromResult(true)))) return;
                        }
                    }
                    catch
                    {
                        if (!(await (Application.Current?.Windows?[0]?.Page?.DisplayAlertAsync(Localized._Warn, Localized.MarkdownToXAML_HyperlinkSpan_SureOpen(Url), Localized._OK, Localized._Cancel) ?? Task.FromResult(true)))) return;
                    }

                    await Launcher.OpenAsync(Url.Split(' ', StringSplitOptions.TrimEntries)[0]);


                })
            });
        }
    }
}
