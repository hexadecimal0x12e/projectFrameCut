using Microsoft.Maui.Controls;
using projectFrameCut.Services;
using System.Globalization;
using Microsoft.Maui.Handlers;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.Controls;
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
using Microsoft.Maui.Platform;
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
        public static NavigationViewItem homeItem, assetItem, templateItem, debugItem, settingItem, createItem;
        internal static Microsoft.UI.Xaml.FrameworkElement? _shellContent;
        private string _titleBeforeModelShow = "";

        // Modal-tracking: save nav state before a modal appears, restore on dismiss.
        internal static bool _navWasVisibleBeforeModal;
        internal static bool _navWasOpenBeforeModal;
#endif

        protected override Microsoft.Maui.Controls.Window CreateWindow(IActivationState? activationState)
        {
            try
            {
                var watchdogService = Handler?.MauiContext?.Services.GetService<UIThreadWatchdogService>();
                if (watchdogService != null && !SettingsManager.IsBoolSettingTrue("ui_DisableUIThreadWatchdog") && !Environment.GetCommandLineArgs().Contains("--noUIWatchdog"))
                {
#if WINDOWS
                    bool frozenFromShown = false;
                    int count = 0;
                    watchdogService.ThreadFrozen += (sender, e) =>
                    {
                        if (frozenFromShown) return;
                        frozenFromShown = true;
                        new Thread(Helper.HelperProgram.FrozenMain)
                        {
                            Name = "Frozen UI Thread",
                            Priority = ThreadPriority.Lowest
                        }.Start();
                    };
                    watchdogService.ThreadRecovered += (sender, e) =>
                    {
                        Helper.HelperProgram.CloseFrozenDiag();
                        frozenFromShown = false;
                    };
                    watchdogService.FrozenContinues += (S, e) =>
                    {
                        if (!watchdogService.IsThreadFrozen || frozenFromShown) return;
                        count++;
                        if (count % 10 == 0)
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
            string styleXAML = "", colorXAML = "";
            if (!File.Exists(stylePath))
            {
                try
                {
                    string fileName = $"Styles.{DeviceInfo.Current.Platform}{DeviceInfo.Current.Idiom}.xaml";
                    if ((stylePath = FileSystemService.GetAppPackageFileSync("Styles", fileName)) != null && File.Exists(stylePath))
                    {
                        styleXAML = File.ReadAllText(stylePath);
                    }
                    else
                    {
                        styleXAML = "";
                    }
                }
                catch
                {
                    styleXAML = "";
                }
            }
            else
            {
                styleXAML = File.ReadAllText(stylePath);
            }

            if (!File.Exists(colorPath))
            {
                colorPath = FileSystemService.GetAppPackageFileSync("Styles", "Colors.xaml");
                colorXAML = File.ReadAllText(colorPath);
            }
            else
            {
                colorXAML = File.ReadAllText(colorPath);
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(colorXAML) && !string.IsNullOrWhiteSpace(styleXAML) && !SettingsManager.IsBoolSettingTrue("ui_DisableUserStyle"))
                {
                    var resourceDictionary = new ResourceDictionary();
                    var colorResourceDictionary = new ResourceDictionary();

                    var loadedStyle = Microsoft.Maui.Controls.Xaml.Extensions.LoadFromXaml(resourceDictionary, styleXAML) as ResourceDictionary;
                    var loadedColor = Microsoft.Maui.Controls.Xaml.Extensions.LoadFromXaml(colorResourceDictionary, colorXAML) as ResourceDictionary;

                    if (Application.Current != null)
                    {
                        Application.Current.Resources.MergedDictionaries.Clear();
                        Application.Current.Resources.MergedDictionaries.Add(loadedColor);
                        Application.Current.Resources.MergedDictionaries.Add(loadedStyle);
                        Log($"Applied style from {stylePath} and colors from {colorPath}");
                    }
                }
                else
                {
                    if(string.IsNullOrWhiteSpace(styleXAML))
                        Log($"No style file found at {stylePath}, using default style and colors.");
                    if (string.IsNullOrWhiteSpace(colorXAML))
                        Log($"No color file found at {colorPath}, using default style and colors.");
                    if(SettingsManager.IsBoolSettingTrue("ui_DisableUserStyle"))
                        Log($"User style is disabled by settings, using default style and colors.");
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
                    var shell = new WindowsAppShell(false);
                    var mauiWindow = new Microsoft.Maui.Controls.Window(shell);

                    shell.Items.Add(new ShellContent { Content = new HomePage(), Title = Localized.AppShell_ProjectsTab, Icon = ImageHelper.LoadFromAsset("icon_project"), Route = "home" });
                    shell.Items.Add(new ShellContent { Content = new AssetsLibraryPage(), Title = Localized.AppShell_AssetsTab, Icon = ImageHelper.LoadFromAsset("icon_add"), Route = "assets" });
                    shell.Items.Add(new ShellContent { Content = new CreatePage(), Title = Localized.AppShell_CreateTab, Icon = ImageHelper.LoadFromAsset("icon_create"), Route = "create" });
                    shell.Items.Add(new ShellContent { Content = new TemplateViewPage(), Title = Localized.AppShell_TemplateTab, Icon = ImageHelper.LoadFromAsset("icon_template"), Route = "template" });
                    shell.Items.Add(new ShellContent { Content = new MainSettingsPage(), Title = Localized._Settings, Icon = ImageHelper.LoadFromAsset("icon_setting"), Route = "options" });
                    return mauiWindow;

                }
                else
                {
                    var shell = new WindowsAppShell(true);
                    var mauiWindow = new Microsoft.Maui.Controls.Window(shell);

                    mauiWindow.HandlerChanged += (s, e) =>
                    {
                        MakeWindow(mauiWindow);
                    };
                    return mauiWindow;
                }

#else
#if IOS
                return new Microsoft.Maui.Controls.Window(new iOSAppShell());
#elif MACCATALYST
                return new Microsoft.Maui.Controls.Window(new MacAppShell());
#elif ANDROID
                return new Microsoft.Maui.Controls.Window(new AndroidAppShell());
#else //general fallback
                var shell = new Shell(false);
                var mauiWindow = new Microsoft.Maui.Controls.Window(shell);

                shell.Items.Add(new ShellContent { Content = new HomePage(), Title = Localized.AppShell_ProjectsTab, Icon = ImageHelper.LoadFromAsset("icon_project"), Route = "home" });
                shell.Items.Add(new ShellContent { Content = new AssetsLibraryPage(), Title = Localized.AppShell_AssetsTab, Icon = ImageHelper.LoadFromAsset("icon_add"), Route = "assets" });
                shell.Items.Add(new ShellContent { Content = new CreatePage(), Title = Localized.AppShell_CreateTab, Icon = ImageHelper.LoadFromAsset("icon_create"), Route = "create" });
                shell.Items.Add(new ShellContent { Content = new TemplateViewPage(), Title = Localized.AppShell_TemplateTab, Icon = ImageHelper.LoadFromAsset("icon_template"), Route = "template" });
                shell.Items.Add(new ShellContent { Content = new MainSettingsPage(), Title = Localized._Settings, Icon = ImageHelper.LoadFromAsset("icon_setting"), Route = "options" });
                return mauiWindow;
#endif
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

                // Idempotency: HandlerChanged can fire more than once. If we already bound a
                // MAUI navigation wrapper to this window's root NavigationView, bail unless the
                // caller explicitly forces a rebuild.
                if (MainNavView is not null && !force)
                    return;

                nativeWindow.Closed += async (s, e) =>
                {
                    if (AppShell.instance.CurrentPage is DraftPage pg)
                    {
                        try
                        {
                            await pg.Save(true, new ApplicationAPIBase.Project.ClipUpdateEventArgs { Reason = ApplicationAPIBase.Project.ClipUpdateReason.Unknown, DetailInfo = "Auto-save when closing window" });
                        }
                        catch (Exception ex)
                        {
                            Log(ex, "Auto-saving project when closing window", this);
                        }
                    }
                };

                try
                {
                    EnsureXamlControlsResources();

                    // Apply acrylic backdrop for the window background.
                    try { nativeWindow.SystemBackdrop = new DesktopAcrylicBackdrop(); }
                    catch { }

                    // Save the original MAUI content (WindowRootViewContainer) — we need
                    // it as window.Content so ModalNavigationManager can locate it.
                    var mauiContainer = nativeWindow.Content as Microsoft.UI.Xaml.UIElement;

                    // Pre-emptively fix null-ShellView crash on ShellFlyoutItemView.
                    if (mauiContainer is not null)
                        FixShellFlyoutItemViewCrash(mauiContainer);

                    if (mauiContainer is Microsoft.UI.Xaml.Controls.Panel container
                        && container.Children.FirstOrDefault() is Microsoft.UI.Xaml.FrameworkElement shellContent)
                    {
                        // Build native items directly.
                        var homeItemN = new NavigationViewItem { Content = Localized.AppShell_ProjectsTab, Tag = "HomePage", Height = 36, Icon = new SymbolIcon { Symbol = Symbol.Folder } };
                        var templateItemN = new NavigationViewItem { Content = Localized.AppShell_ProjectsTab, Tag = "TemplateViewPage", Height = 36, Icon = new SymbolIcon { Symbol = Symbol.SwitchApps } };
                        var assetItemN = new NavigationViewItem { Content = Localized.AppShell_AssetsTab, Tag = "Assets", Height = 36, Icon = new SymbolIcon { Symbol = Symbol.SlideShow } };
                        var createItemN = new NavigationViewItem { Content = Localized.AppShell_CreateTab, Tag = "Create", Height = 36, Icon = new SymbolIcon { Symbol = Symbol.Add } };
                        var settingItemN = new NavigationViewItem { Content = Localized._Settings, Tag = "Setting", Height = 36, Icon = new SymbolIcon { Symbol = Symbol.Setting } };
                        var assistantItemN = new NavigationViewItem { Content = Localized.AppShell_ChatWithAssistant, Tag = "Assistant", Height = 36, Icon = new SymbolIcon { Symbol = Symbol.Message } };

                        var nativeNav = new NavigationView
                        {
                            PaneDisplayMode = NavigationViewPaneDisplayMode.LeftCompact,
                            CompactPaneLength = 48,
                            OpenPaneLength = 240,
                            IsPaneToggleButtonVisible = true,
                            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
                            IsBackEnabled = false,
                            IsSettingsVisible = false,
                            IsPaneVisible = true,
                        };

                        nativeNav.MenuItems.Add(homeItemN);
                        nativeNav.MenuItems.Add(assetItemN);
                        nativeNav.MenuItems.Add(templateItemN);
                        nativeNav.FooterMenuItems.Add(assistantItemN);
                        nativeNav.FooterMenuItems.Add(createItemN);
                        nativeNav.FooterMenuItems.Add(settingItemN);

                        nativeNav.ItemInvoked += (sender, args) =>
                        {
                            var tag = args.InvokedItemContainer is NavigationViewItem nvi ? nvi.Tag?.ToString() : null;
                            if (string.IsNullOrWhiteSpace(tag)) return;
                            Microsoft.Maui.Controls.Application.Current?.Dispatcher.Dispatch(async () =>
                            {
                                try
                                {
                                    if (tag == "Assistant")
                                    {
                                        nativeNav.SelectedItem = homeItemN;
                                        await DisplayAssistantWindow();
                                        return;
                                    }
                                    Microsoft.Maui.Controls.Page? page = tag switch
                                    {
                                        var _ when args.IsSettingsInvoked => new MainSettingsPage(),
                                        "HomePage" => new HomePage(),
                                        "TemplateViewPage" => new TemplateViewPage(),
                                        "Assets" => new AssetsLibraryPage(),
                                        "Create" => new CreatePage(),
                                        "Setting" => new MainSettingsPage(),
                                        _ => null,
                                    };
                                    if (page is not null)
                                        await Shell.Current.Navigation.PushAsync(page);
                                }
                                catch (System.Exception ex)
                                {
                                    Log(ex, $"Navigate to {tag} failed", this);
                                }
                            });
                        };

                        nativeNav.SelectedItem = homeItemN;

                        // Push the Shell content right by adding a left margin equal to
                        // the pane width so it never overlaps with our NavigationView.
                        _shellContent = shellContent;
                        shellContent.Margin = new Microsoft.UI.Xaml.Thickness(48, 0, 0, 0);

                        // Sync the margin when the pane opens/closes.
                        nativeNav.PaneOpening += (_, _) =>
                            shellContent.Margin = new Microsoft.UI.Xaml.Thickness(240, 0, 0, 0);
                        nativeNav.PaneClosing += (_, _) =>
                            shellContent.Margin = new Microsoft.UI.Xaml.Thickness(48, 0, 0, 0);

                        // Overlay our NavigationView on top. After the template loads,
                        // make the content area (right of the pane) transparent and
                        // non-hit-testable so the Shell page beneath is interactive.
                        nativeNav.Loaded += (_, _) => DisableNavContentHitTest(nativeNav);
                        Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(nativeNav, 9999);
                        container.Children.Add(nativeNav);

                        MainNavView = nativeNav;
                        homeItem = homeItemN;
                        templateItem = templateItemN;
                        assetItem = assetItemN;
                        createItem = createItemN;
                        settingItem = settingItemN;

                        // Auto-hide nav bar when a modal page is pushed,
                        // restore it when the modal is dismissed.
                        Microsoft.Maui.Controls.Application.Current.ModalPushed += (_, _) =>
                        {
                            if (MainNavView is null || MainNavView.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed) return;
                            _titleBeforeModelShow = Application.Current.Windows[0].Title;
                            Application.Current.Windows[0].Title = "";
                            MainNavView.IsEnabled = false;
                        };
                        Microsoft.Maui.Controls.Application.Current.ModalPopped += (_, _) =>
                        {
                            if (MainNavView is null || MainNavView.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed) return;
                            Application.Current.Windows[0].Title = _titleBeforeModelShow;
                            MainNavView.IsEnabled = true;

                        };
                    }
                }
                catch (Exception ex)
                {
                    Log(ex, "Attach WinUI3 Nav view", this);
                }
            }
        }

        public static void HideNavBar()
        {
            if (MainNavView is null || _shellContent is null) return;
            MainNavView.IsPaneVisible = false;
            _shellContent.Margin = new Microsoft.UI.Xaml.Thickness(0);
        }

        public static void ShowNavBar()
        {
            if (MainNavView is null || _shellContent is null) return;
            MainNavView.IsPaneVisible = true;
            _shellContent.Margin = new Microsoft.UI.Xaml.Thickness(
                MainNavView.PaneDisplayMode == NavigationViewPaneDisplayMode.LeftCompact ? 48 : 240, 0, 0, 0);
        }

        /// <summary>
        /// Programmatically collapse the pane from expanded (240px) back to compact (48px).
        /// Safe to call at any time — no-op if already collapsed, hidden, or not initialized.
        /// </summary>
        public static void CollapseNavView()
        {
            if (MainNavView is null || _shellContent is null) return;
            if (!MainNavView.IsPaneOpen) return;
            MainNavView.IsPaneOpen = false;
            // PaneClosing event will reset margin to 48, but set it here too
            // in case the event fires asynchronously.
            _shellContent.Margin = new Microsoft.UI.Xaml.Thickness(48, 0, 0, 0);
        }

        /// <summary>
        /// After NavigationView loads, walks its template to find the SplitView content
        /// side and recursively disables hit-test + background there so clicks pass
        /// through to the Shell page beneath. The pane side is left interactive.
        /// </summary>
        private static void DisableNavContentHitTest(NavigationView nav)
        {
            nav.Loaded -= (_, _) => { }; // run once

            void Walk(FrameworkElement? parent)
            {
                if (parent is null) return;
                if (parent is Microsoft.UI.Xaml.Controls.SplitView split)
                {
                    if (split.Content is FrameworkElement contentChild)
                        DisableBranch(contentChild);
                    return;
                }
                for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
                {
                    if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i) is FrameworkElement fe)
                        Walk(fe);
                }
            }

            void DisableBranch(FrameworkElement root)
            {
                root.IsHitTestVisible = false;
                if (root is Microsoft.UI.Xaml.Controls.Panel panel)
                    panel.Background = null;
                for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
                {
                    if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i) is FrameworkElement fe)
                        DisableBranch(fe);
                }
            }

            Walk(nav);
        }

        /// <summary>
        /// Walk the MAUI Shell visual tree and fix any <c>ShellFlyoutItemView</c>
        /// whose <c>ShellView</c> property is null (causes NRE during MeasureOverride).
        /// Sets the property to the nearest <c>NavigationView</c> ancestor, which for
        /// MAUI is the <c>RootNavigationView</c> that implements <c>ShellView</c>.
        /// </summary>
        private static void FixShellFlyoutItemViewCrash(Microsoft.UI.Xaml.DependencyObject root)
        {
            WalkAndFix(root);

            static void WalkAndFix(Microsoft.UI.Xaml.DependencyObject element)
            {
                if (element is null) return;

                var typeName = element.GetType().FullName ?? "";
                if (typeName.Contains("ShellFlyoutItemView"))
                {
                    try
                    {
                        // Find the ShellView property (internal, may be defined on a base class).
                        var shellViewProp = element.GetType().GetProperty("ShellView",
                            System.Reflection.BindingFlags.Instance |
                            System.Reflection.BindingFlags.NonPublic |
                            System.Reflection.BindingFlags.Public);

                        if (shellViewProp is not null)
                        {
                            var currentValue = shellViewProp.GetValue(element);
                            if (currentValue is null)
                            {
                                // Walk up the visual tree to find the RootNavigationView
                                // (which implements ShellView).
                                var parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
                                while (parent is not null)
                                {
                                    if (parent is Microsoft.UI.Xaml.Controls.NavigationView nav)
                                    {
                                        shellViewProp.SetValue(element, nav);
                                        System.Diagnostics.Debug.WriteLine(
                                            $"[FixShellFlyoutItemView] Fixed null ShellView → {nav.GetType().Name}");
                                        break;
                                    }
                                    parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent);
                                }
                            }
                        }
                        else
                        {
                            // Fallback: try to find and set the backing field.
                            var shellViewField = element.GetType().GetField("_shellView",
                                System.Reflection.BindingFlags.Instance |
                                System.Reflection.BindingFlags.NonPublic);
                            if (shellViewField is not null && shellViewField.GetValue(element) is null)
                            {
                                var parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
                                while (parent is not null)
                                {
                                    if (parent is Microsoft.UI.Xaml.Controls.NavigationView nav)
                                    {
                                        shellViewField.SetValue(element, nav);
                                        break;
                                    }
                                    parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent);
                                }
                            }
                        }
                    }
                    catch { }
                }

                // Recurse into children.
                int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element);
                for (int i = 0; i < count; i++)
                {
                    var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element, i);
                    WalkAndFix(child);
                }
            }
        }

        /// <summary>
        /// Ensure <c>XamlControlsResources</c> (WinUI 2.8 / Fluent 2 theme) is
        /// loaded into the application resource dictionary BEFORE any WinUI controls
        /// are created. Without this, NavigationView and many other controls render
        /// with no visual template (invisible).
        /// </summary>
        private static void EnsureXamlControlsResources()
        {
            var uiApp = Microsoft.UI.Xaml.Application.Current;
            if (uiApp is null) return;

            foreach (var rd in uiApp.Resources.MergedDictionaries)
            {
                if (rd is Microsoft.UI.Xaml.Controls.XamlControlsResources) return;
            }

            // Insert at position 0 so WinUI styles take priority over any other
            // resource dictionaries that might shadow them (e.g. MAUI resources).
            uiApp.Resources.MergedDictionaries.Insert(
                0, new Microsoft.UI.Xaml.Controls.XamlControlsResources());
        }

#endif

        public static async Task DisplayAssistantWindow()
        {
#if WINDOWS
            Directory.CreateDirectory(Path.Combine(MauiProgram.DataPath, "Chats"));
#else
            Directory.CreateDirectory(Path.Combine(MauiProgram.DataPath, "chats"));
#endif
            var view = new AIAssistance.AssistanceChatSessionsView(MauiProgram.DataPath, null)
            {
                GlobalToolCallFactories = AIAssistance.AITools.BuildToolCallsWhileNoProject()
            };
            var content = new ContentPage { Content = view, Title = "" };
            var page = new NavigationPage(content) { Title = "" };
            NavigationPage.SetHasNavigationBar(page, false);
            NavigationPage.SetHasNavigationBar(content, false);
            var newWindow = new Microsoft.Maui.Controls.Window(page)
            {
                Title = "Assistant P",
            };
#if WINDOWS

            newWindow.HandlerChanged += (s, e) =>
            {
                var platformView = newWindow.Handler?.PlatformView;
                if (platformView is Microsoft.UI.Xaml.Window nativeWindow)
                {
                    nativeWindow.SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
                }
            };
#endif

            Application.Current?.OpenWindow(newWindow);
        }

    }

    public abstract class AppShell : Shell
    {
        public static AppShell? instance;
        public virtual void ShowNavView() { }

        public virtual void HideNavView() { }

        public virtual void CollapseNavView() { }
    }
}
