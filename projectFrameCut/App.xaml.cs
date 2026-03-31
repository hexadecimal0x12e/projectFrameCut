using Microsoft.Maui.Controls;
using projectFrameCut.Services;
using System.Globalization;
using Microsoft.Maui.Handlers;
using projectFrameCut.ApplicationAPIBase.Helpers;
using ResourceDictionary = Microsoft.Maui.Controls.ResourceDictionary;
using Application = Microsoft.Maui.Controls.Application;










#if WINDOWS
using projectFrameCut.WinUI;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Composition.SystemBackdrops;

using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using WinRT.Interop;
using projectFrameCut.Platforms.Windows;
#endif

namespace projectFrameCut
{
    public partial class App : Microsoft.Maui.Controls.Application
    {

        public static App instance;
        private readonly UIThreadWatchdogService _watchdog;

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
#if WINDOWS
                if (SettingsManager.GetSetting("ui_defaultTheme", "default") != "default")
                {
                    (projectFrameCut.WinUI.App.Current as Microsoft.UI.Xaml.Application)?.RequestedTheme = SettingsManager.GetSetting("ui_defaultTheme", "default") switch
                    {
                        "dark" => Microsoft.UI.Xaml.ApplicationTheme.Dark,
                        "light" => Microsoft.UI.Xaml.ApplicationTheme.Light,
                        _ => Microsoft.UI.Xaml.ApplicationTheme.Light
                    };
                }
#endif
            }
            catch { }

#if WINDOWS
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
#pragma warning disable CS0618
                Program.Crash(e.ExceptionObject as Exception ?? new ExecutionEngineException($"projectFrameCut can't gather more information about this exception."));
#pragma warning restore CS0618

            };
#endif


        }

        protected override async void OnAppLinkRequestReceived(Uri uri)
        {
            base.OnAppLinkRequestReceived(uri);

            await Dispatcher.DispatchAsync(async () =>
            {
                HomePage.HasAlreadyLaunchedFromFile = false;
                Preferences.Set("LaunchedPJFCUri", uri.ToString());
                await Windows[0].Page!.Navigation.PopToRootAsync();

            });

        }

#if WINDOWS
        public static NavigationView MainNavView;
        public static Microsoft.UI.Xaml.Window NativeWindow;
        public static NavigationViewItem homeItem, assetItem, templateItem, debugItem, settingItem;
#endif

        protected override Microsoft.Maui.Controls.Window CreateWindow(IActivationState? activationState)
        {
            try
            {
                var watchdogService = Handler?.MauiContext?.Services.GetService<UIThreadWatchdogService>();
                if (watchdogService != null && !SettingsManager.IsBoolSettingTrue("ui_DisableUIThreadWatchdog") && !Environment.GetCommandLineArgs().Contains("--noUIWatchdog"))
                {
#if WINDOWS
                    int count = 0;
                    watchdogService.ThreadFrozen += (sender, e) =>
                    {
                        new Thread(Helper.HelperProgram.FrozenMain)
                        {
                            Name = "Frozen UI Thread",
                            Priority = ThreadPriority.Lowest
                        }.Start();
                    };
                    watchdogService.ThreadRecovered += (sender, e) =>
                    {
                        Helper.HelperProgram.CloseFrozenDiag();
                    };
                    watchdogService.FrozenContinues += (S, e) =>
                    {
                        if (!watchdogService.IsThreadFrozen) return;
                        count++;
                        if(count % 10 == 0)
                        {
                            new Thread(Helper.HelperProgram.FrozenMain)
                            {
                                Name = "Frozen UI Thread",
                                Priority = ThreadPriority.Lowest
                            }.Start();
                        }
                    };
#endif

                    watchdogService.Start();
                    Log("UI Thread Watchdog Service started");
                }
            }
            catch (Exception ex)
            {
                Log(ex, $"start UI Thread Watchdog Service", this);
            }
            var stylePath = Path.Combine(MauiProgram.DataPath, "style.xaml");
            var colorPath = Path.Combine(MauiProgram.DataPath, "color.xaml");
            try
            {
                if (File.Exists(stylePath) && File.Exists(colorPath) && !SettingsManager.IsBoolSettingTrue("ui_DisableUserStyle"))
                {
                    var styleXAML = File.ReadAllText(stylePath);
                    var colorXAML = File.ReadAllText(colorPath);
                    var resourceDictionary = new ResourceDictionary();
                    var colorResourceDictionary = new ResourceDictionary();

                    var loadedStyle = Microsoft.Maui.Controls.Xaml.Extensions.LoadFromXaml(resourceDictionary, styleXAML) as ResourceDictionary;
                    var loadedColor = Microsoft.Maui.Controls.Xaml.Extensions.LoadFromXaml(colorResourceDictionary, colorXAML) as ResourceDictionary;

                    if (Application.Current != null)
                    {
                        Application.Current.Resources.MergedDictionaries.Clear();
                        Application.Current.Resources.MergedDictionaries.Add(loadedColor);
                        Application.Current.Resources.MergedDictionaries.Add(loadedStyle);
                        Log("Applied user style.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log(ex, "Apply user style", this);
            }
            try
            {
#if WINDOWS
                if (CultureInfo.CurrentCulture.TextInfo.IsRightToLeft || SettingsManager.IsBoolSettingTrue("ui_ForceUseShell"))
                {
                    var shell = new AppShell(false);
                    var mauiWindow = new Microsoft.Maui.Controls.Window(shell);

                    shell.Items.Add(new ShellContent { Content = new HomePage(), Title = Localized.AppShell_ProjectsTab, Icon = ImageHelper.LoadFromAsset("icon_project"), Route = "home" });
                    shell.Items.Add(new ShellContent { Content = new TemplateViewPage(), Title = Localized.AppShell_TemplateTab, Icon = ImageHelper.LoadFromAsset("icon_template"), Route = "template" });
                    shell.Items.Add(new ShellContent { Content = new AssetsLibraryPage(), Title = Localized.AppShell_AssetsTab, Icon = ImageHelper.LoadFromAsset("icon_asset"), Route = "assets" });
                    shell.Items.Add(new ShellContent { Content = new MainSettingsPage(), Title = Localized._Settings, Icon = ImageHelper.LoadFromAsset("icon_setting"), Route = "options" });
                    return mauiWindow;

                }
                else
                {
                    var shell = new AppShell(true);
                    var mauiWindow = new Microsoft.Maui.Controls.Window(shell);

                    mauiWindow.HandlerChanged += (s, e) =>
                    {
                        MakeWindow(mauiWindow);
                    };
                    return mauiWindow;


                }

#else
                return new Microsoft.Maui.Controls.Window(new AppShell());
#endif
            }
            catch (Exception ex)
            {
                Log("*** FATAL: Cannot create main window.", "fatal");
                MauiProgram.Crash(ex);
                throw;
            }

        }

#if WINDOWS
        private void MakeWindow(Microsoft.Maui.Controls.Window mauiWindow, bool force = false)
        {
            var platformView = mauiWindow.Handler?.PlatformView;
            if (platformView is Microsoft.UI.Xaml.Window nativeWindow)
            {
                NativeWindow = nativeWindow;
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
                    IsSettingsVisible = false,
                };
                MainNavView = nav;

                homeItem = new NavigationViewItem { Content = Localized.AppShell_ProjectsTab, Tag = "HomePage", Height = 36, Padding = new(4) };
                homeItem.Icon = new Microsoft.UI.Xaml.Controls.SymbolIcon { Symbol = Symbol.Folder };

                templateItem = new NavigationViewItem { Content = Localized.AppShell_ProjectsTab, Tag = "TemplateViewPage", Height = 36, Padding = new(4) };
                templateItem.Icon = new Microsoft.UI.Xaml.Controls.SymbolIcon { Symbol = Symbol.SwitchApps };

                assetItem = new NavigationViewItem { Content = Localized.AppShell_AssetsTab, Tag = "Assets", Height = 36, Padding = new(4) };
                assetItem.Icon = new Microsoft.UI.Xaml.Controls.SymbolIcon { Symbol = Symbol.SlideShow };


                nav.MenuItems.Add(homeItem);
                nav.MenuItems.Add(templateItem);
                nav.MenuItems.Add(assetItem);

                settingItem = new NavigationViewItem { Content = Localized._Settings, Tag = "Setting", Height = 36, Padding = new(4) };
                settingItem.Icon = new Microsoft.UI.Xaml.Controls.SymbolIcon { Symbol = Symbol.Setting };
                nav.FooterMenuItems.Add(settingItem);


                try
                {
                    nativeWindow.Content = null;

                    nav.Content = originalContent;

                    nativeWindow.Content = nav;
                    nav.SelectedItem = homeItem;

                }
                catch (Exception ex)
                {
                    Log(ex, "Add WinUI3 Nav view", this);
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
                                case "TemplateViewPage":
                                    await Shell.Current.Navigation.PushAsync(new TemplateViewPage());
                                    break; 
                                case "Assets":
                                    await Shell.Current.Navigation.PushAsync(new AssetsLibraryPage());
                                    break;
                                case "Setting":
                                    await Shell.Current.Navigation.PushAsync(new MainSettingsPage());
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

        public static async void HideNavBar()
        {
            MainNavView?.IsPaneVisible = false;
            await Task.Delay(50);
            var appWindow = Current?.Windows[0];
            if (NativeWindow != null && appWindow != null)
            {
                NativeWindow.Content.InvalidateMeasure();
                await Task.Delay(50);
                NativeWindow.Content.InvalidateArrange();
                await Task.Delay(50);
                NativeWindow.ExtendsContentIntoTitleBar = false;
                await Task.Delay(150);
                NativeWindow.ExtendsContentIntoTitleBar = true;
                //await Task.Delay(150);
                //appWindow.Width = appWindow.Width - 8; //avoid the contents go inside navigation bar
                //await Task.Delay(150);
                //appWindow.Width = appWindow.Width + 8;
            }
        }
        public static async Task ShowNavBar()
        {
            if (MainNavView != null)
                MainNavView.IsPaneVisible = true;

            await Task.Delay(50);
            //if (appWindow != null)
            //{
            //    appWindow.Width = appWindow.Width - 8; //avoid the contents go inside navigation bar
            //    await Task.Delay(50);
            //    appWindow.Width = appWindow.Width + 8;
            //}
            var appWindow = Current?.Windows[0];
            if (NativeWindow != null && appWindow != null)
            {
                NativeWindow.Content.InvalidateMeasure();
                await Task.Delay(50);
                NativeWindow.Content.InvalidateArrange();
                await Task.Delay(50);
                NativeWindow.ExtendsContentIntoTitleBar = false;
                await Task.Delay(150);
                NativeWindow.ExtendsContentIntoTitleBar = true;
                //await Task.Delay(150);
                //appWindow.Width = appWindow.Width - 8; //avoid the contents go inside navigation bar
                //await Task.Delay(150);
                //appWindow.Width = appWindow.Width + 8;
            }
        }

#endif

    }
}
