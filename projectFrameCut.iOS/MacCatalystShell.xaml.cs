using System;
#if IOS || MACCATALYST
using UIKit;
using System.Runtime.InteropServices;
#endif

namespace projectFrameCut
{
    public partial class AppShell_MacCatalyst : Shell
    {
        public static AppShell_MacCatalyst instance;

        public AppShell_MacCatalyst()
        {
            instance = this;
            InitializeComponent();
            Title = Localized.AppBrand;

            // Ensure routes are registered (optional explicit registration)
            try
            {
                Routing.RegisterRoute("home", typeof(HomePage));
                Routing.RegisterRoute("assets", typeof(AssetsLibraryPage));
                Routing.RegisterRoute("debug", typeof(DebuggingMainPage));
                Routing.RegisterRoute("options", typeof(MainSettingsPage));
            }
            catch (Exception ex)
            {
                Log(ex, "register routes", this);
            }

            this.Navigated += AppShell_MacCatalyst_Navigated;

#if MACCATALYST
            SetSfSymbol(ProjectsTab, "folder");
            SetSfSymbol(AssetsTab, "photo.on.rectangle");
            SetSfSymbol(DebugTab, "wrench.and.screwdriver");
            SetSfSymbol(SettingTab, "gearshape");
#endif
        }

        public void HideNavView()
        {
            this.FlyoutBehavior = FlyoutBehavior.Disabled;
        }

        public void ShowNavView()
        {
            this.FlyoutBehavior = FlyoutBehavior.Locked;
        }

        private async void AppShell_MacCatalyst_Navigated(object? sender, ShellNavigatedEventArgs e)
        {
            if (CurrentPage != null)
            {
                // Simple entry animation
                CurrentPage.Opacity = 0;
                CurrentPage.TranslationY = 20;

                await Task.WhenAll(
                    CurrentPage.FadeToAsync(1, 250, Easing.CubicOut),
                    CurrentPage.TranslateToAsync(0, 0, 250, Easing.CubicOut)
                );
            }
        }

#if MACCATALYST
        private void SetSfSymbol(ShellItem tab, string symbolName, UIImageSymbolWeight weight = UIImageSymbolWeight.Regular)
        {
            try
            {
                var config = UIImageSymbolConfiguration.Create(weight);
                var uiImage = UIImage.GetSystemImage(symbolName, config);
                if (uiImage == null) return;

                var pngData = uiImage.AsPNG();
                if (pngData != null)
                {
                    tab.Icon = ImageSource.FromStream(() => pngData.AsStream());
                }
            }
            catch (Exception ex)
            {
                Log(ex, $"Set SF Symbol {symbolName} failed.", this);
            }
        }
#endif
    }
}
