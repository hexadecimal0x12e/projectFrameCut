using Microsoft.Maui.Controls;


#if WINDOWS
using projectFrameCut.WinUI;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using WinRT.Interop;
#endif

namespace projectFrameCut
{
    public partial class App : Microsoft.Maui.Controls.Application
    {

        public static App instance;

        // If the app was launched/opened via a .pjfc file, this will hold the incoming URI string.
        public string? LaunchedPjfcUri { get; private set; }

        public App()
        {
            instance = this;
            InitializeComponent();
            try
            {
                instance?.UserAppTheme = SettingsManager.GetSetting("ui_defaultTheme", "default") switch
                {
                    "dark" => AppTheme.Dark,
                    "light" => AppTheme.Light,
                    _ => AppTheme.Unspecified
                };
            }
            catch { }

            try
            {
                var uri = Microsoft.Maui.Storage.Preferences.Get("OpenedPjfcUri", (string?)null);
                if (!string.IsNullOrWhiteSpace(uri))
                {
                    LaunchedPjfcUri = uri;
                    // remove preference once read to avoid reprocessing
                    Microsoft.Maui.Storage.Preferences.Remove("OpenedPjfcUri");
                }
            }
            catch
            {
                // ignore if preferences fail
            }
        }

#if WINDOWS
        public static NavigationView MainNavView;
        public static NavigationViewItem homeItem, assetItem, debugItem, settingItem;
#endif

        protected override Microsoft.Maui.Controls.Window CreateWindow(IActivationState? activationState)
        {
            var mauiWindow = new Microsoft.Maui.Controls.Window(new AppShell());

#if WINDOWS
            mauiWindow.HandlerChanged += (s, e) =>
            {
                MakeWindow(mauiWindow);
            };

#endif

            return mauiWindow;
        }

#if WINDOWS
        private void MakeWindow(Microsoft.Maui.Controls.Window mauiWindow, bool force = false)
        {
            var platformView = mauiWindow.Handler?.PlatformView;
            if (platformView is Microsoft.UI.Xaml.Window nativeWindow)
            {
                var uiApp = Microsoft.UI.Xaml.Application.Current;
                if (uiApp != null)
                {
                    bool hasXamlControlsResources = false;
                    foreach (var rd in uiApp.Resources.MergedDictionaries)
                    {
                        if (rd is Microsoft.UI.Xaml.Controls.XamlControlsResources)
                        {
                            hasXamlControlsResources = true;
                            break;
                        }
                    }
                    if (!hasXamlControlsResources)
                    {
                        uiApp.Resources.MergedDictionaries.Add(new Microsoft.UI.Xaml.Controls.XamlControlsResources());
                    }
                }

                if (nativeWindow.Content is NavigationView && !force)
                    return;

                nativeWindow.SystemBackdrop = new DesktopAcrylicBackdrop();

                var originalContent = nativeWindow.Content;

                var nav = new NavigationView
                {
                    IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
                    PaneDisplayMode = NavigationViewPaneDisplayMode.LeftCompact,
                    OpenPaneLength = 240,
                    CompactPaneLength = 48,
                    IsBackEnabled = true,
                    IsTitleBarAutoPaddingEnabled = true,
                    IsSettingsVisible = false
                };
                MainNavView = nav;


                homeItem = new NavigationViewItem { Content = Localized.AppShell_ProjectsTab, Tag = "HomePage", Height = 36, Padding = new(4) };
                homeItem.Icon = new Microsoft.UI.Xaml.Controls.SymbolIcon { Symbol = Symbol.Folder };

                assetItem = new NavigationViewItem { Content = Localized.AppShell_AssetsTab, Tag = "Assets", Height = 36, Padding = new(4) };
                assetItem.Icon = new Microsoft.UI.Xaml.Controls.SymbolIcon { Symbol = Symbol.MapDrive };

                

                nav.MenuItems.Add(homeItem);
                nav.MenuItems.Add(assetItem);

                settingItem = new NavigationViewItem { Content = Localized._Settings, Tag = "Setting", Height = 36, Padding = new(4) };
                settingItem.Icon = new Microsoft.UI.Xaml.Controls.SymbolIcon { Symbol = Symbol.Setting };
                nav.FooterMenuItems.Add(settingItem);

                if (bool.TryParse(SettingsManager.GetSetting("DeveloperMode", false.ToString()), out var dbg) ? dbg : false)
                {
                    debugItem = new NavigationViewItem { Content = Localized.AppShell_DebugTab, Tag = "Debug", Height = 36, Padding = new(4) };
                    debugItem.Icon = new Microsoft.UI.Xaml.Controls.SymbolIcon { Symbol = Symbol.Repair };
                    nav.MenuItems.Add(debugItem);
                }

                try
                {
                    nativeWindow.Content = null;

                    nav.Content = originalContent;

                    nativeWindow.Content = nav;
                    nav.SelectedItem = homeItem;

                }
                catch
                {
                    nativeWindow.Content = originalContent;
                }

                nav.ItemInvoked += async (senderNav, argsNav) =>
                {
                    var invoked = argsNav.InvokedItemContainer as NavigationViewItem;
                    var tag = invoked?.Tag as string;
                    if (string.IsNullOrWhiteSpace(tag))
                        return;

                    Microsoft.Maui.Controls.Application.Current?.Dispatcher.Dispatch(async () =>
                    {
                        try
                        {
                            switch (tag)
                            {
                                case "HomePage":
                                    await Shell.Current.Navigation.PushAsync(new HomePage());
                                    break;
                                case "Assets":
                                    await Shell.Current.Navigation.PushAsync(new AssetsLibraryPage());
                                    break;
                                case "Setting":
                                    await Shell.Current.Navigation.PushAsync(new MainSettingsPage());
                                    break;
                                case "Debug":
                                    await Shell.Current.Navigation.PushAsync(new TestPage());
                                    break;
                            }
                        }
                        catch (System.Exception ex)
                        {
                            Log(ex, $"Navigate to {tag} failed", this);
                            await AppShell.instance.DisplayAlertAsync(Localized._Info, Localized.AppShell_NavFailed(ex, tag), Localized._OK);
                            
                        }
                    });
                };

            }
        }

        public static void HideNavBar()
        {
            MainNavView?.IsPaneVisible = false;

        }
        public static void ShowNavBar()
        {
            MainNavView?.IsPaneVisible = true;
            Thread.Sleep(50);
            var appWindow = Current?.Windows[0]; 
            appWindow?.Width = appWindow.Width - 1; //avoid the contents go inside navigation bar
            appWindow?.Width = appWindow.Width + 1;

        }

#endif

    }
}
