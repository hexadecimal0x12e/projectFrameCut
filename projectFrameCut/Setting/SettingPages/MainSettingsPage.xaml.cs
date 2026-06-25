using Microsoft.Maui.Controls;
using projectFrameCut.Setting.SettingPages;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using IPicture = projectFrameCut.Drawing.Base.IPicture;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.Compose;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Drawing.Vector.ImportExport;
using projectFrameCut.Drawing.Text.Typology;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Effect;
using projectFrameCut.InteractableEditor;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Services;
using projectFrameCut.Shared;

using static projectFrameCut.Setting.SettingManager.SettingsManager;
using projectFrameCut.Render.HwAccelEngine.VectorRasterizer;

namespace projectFrameCut
{
    public partial class MainSettingsPage : ContentPage
    {
        public static Page instance;

        public MainSettingsPage()
        {
            InitializeComponent();

            instance = this;
            HintLabel.Text = SettingLocalizedResources.General_SelectAPageToGo;
            string channelStr = "";
#if WINDOWS
            channelStr = $" ({Helper.HelperProgram.AppChannel} channel)";
#endif
            VersionLabel.Text = $"{Localized.AppBrand} v{Assembly.GetExecutingAssembly()?.GetName()?.Version?.ToString() ?? "Unknown"}{channelStr}";
            CopyrightText.Text += DateTime.Now.Year.ToString();
            if (MauiProgram.IsStoreMode) PluginSettingButton.IsVisible = false; //plugin could broke store review
            if (IsBoolSettingTrue("DeveloperMode"))
            {
                TestPageButton.IsVisible = true;
                AdvancedSettingButton.IsVisible = true;
            }
        }

        private async void OnGeneralSettingClicked(object sender, EventArgs e)
        {
            await NavigateAsync(new GeneralSettingPage());
        }
        private async void OnEditSettingClicked(object sender, EventArgs e)
        {
            await NavigateAsync(new EditSettingPage());
        }
        private async void OnAISettingClicked(object sender, EventArgs e)
        {
            await NavigateAsync(new AISettingPage());
        }
        private async void OnRenderSettingClicked(object sender, EventArgs e)
        {
            await NavigateAsync(new RenderSettingPage());
        }
        private async void OnMiscSettingClicked(object sender, EventArgs e)
        {
            await NavigateAsync(new MiscSettingPage());
        }
        private async void OnPluginSettingClicked(object sender, EventArgs e)
        {
            await NavigateAsync(new PluginSettingPage());
        }
        private async void OnAboutSettingClicked(object sender, EventArgs e)
        {
            await NavigateAsync(new AboutSettingPage());
        }

        private async void AdvancedSettingButton_Clicked(object sender, EventArgs e)
        {
            await NavigateAsync(new AdvancedSettingPage());
        }

        private async void TestPageButton_Clicked(object sender, EventArgs e)
        {
            await NavigateAsync(new TestPage());
        }

        private Task NavigateAsync(Page page)
        {
            try
            {
                return Navigation.PushAsync(page);
            }
            catch (Exception ex)
            {
                Log(ex, $"Navigate to {page.GetType().Name}", this);
                var errpage = new ContentPage
                {
                    Content = new Label
                    {
                        Text = Localized.AppShell_NavFailed(ex, page.GetType().Name),
                        HorizontalOptions = LayoutOptions.Center,
                        VerticalOptions = LayoutOptions.Center
                    }
                };
                return Navigation.PushAsync(errpage);
            }
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            SyncSettingToModules();
        }

        public static async Task RebootApp(Page currentPage)
        {
            var conf = await currentPage.DisplayAlertAsync(Localized._Info,
                                        SettingLocalizedResources.CommonStr_RebootRequired(),
                                        Localized._Confirm,
                                        Localized._Cancel);
            if (conf)
            {
                await FlushAndStopAsync();
                if (Debugger.IsAttached) //let user to reboot in debugger
                {
                    Debugger.Break();
                    Environment.Exit(0);
                }
#if WINDOWS
                WinUI.Program.RebootApp();
#endif  
                Environment.Exit(0);

            }
        }
        public static void SyncSettingToModules()
        {

            if (!IsSettingExists("UserName") || string.IsNullOrWhiteSpace(GetSetting("UserName", "")))
            {
                try
                {
                    var rnd = new RandomNameGenerator(Localized.RandomNameGenerator_Adjectives.Replace("，", ",").Split(',').Select(c => c.TrimStart(' ').TrimEnd(' ').Trim()), Localized.RandomNameGenerator_Nouns.Replace("，", ",").Split(',').Select(c => c.TrimStart(' ').TrimEnd(' ').Trim()), (a, b) => Localized.RandomNameGenerator_Contacter(a, b));
                    WriteSetting("UserName", rnd.Generate());
                }
                catch
                {
                    WriteSetting("UserName", OperatingSystem.IsWindows() ? Environment.UserName : "default user");

                }
            }


            if (IsBoolSettingTrue("render_SaveCheckpoint"))
            {
                Directory.CreateDirectory(Path.Combine(MauiProgram.DataPath, "RenderCheckpoint"));
                MyLoggerExtensions.SaveDiagResult = true;
                MyLoggerExtensions.DiagResultPath = Path.Combine(MauiProgram.DataPath, "RenderCheckpoint");

            }
            else
            {
                MyLoggerExtensions.SaveDiagResult = false;
            }
            if (IsBoolSettingTrue("render_forceImpType_ForceHwAccel"))
            {
                EffectHelper.ForcePreferToType = EffectImplementType.HwAcceleration;
            }
            else if (IsBoolSettingTrue("render_forceImpType_ForceIPicture"))
            {
                EffectHelper.ForcePreferToType = EffectImplementType.IPicture;
            }
            else
            {
                EffectHelper.ForcePreferToType = null;
            }
            IPicture.AllowPixelModeDowngrade = !IsBoolSettingTrue("render_DisallowPictureModeDowngrade");
            PictureLifecycleTracker.Enabled = IsBoolSettingTrue("diag_TraceIPictureObject");
            PictureLifecycleTracker.TrackCollection = IsBoolSettingTrue("diag_TraceIPictureObject");
            var vfdCahceDir = GetSetting("codec_VideoFrameDiskCachePath", Path.Combine(FileSystem.CacheDirectory, "VideoFrameCache"));
            Directory.CreateDirectory(vfdCahceDir);
            VideoFrameDiskCache.CacheBaseDir = vfdCahceDir;
            VideoFrameDiskCache.EnableCompression = IsBoolSettingTrueOrDefault("codec_VideoFrameDiskCacheEnableCompress", true);
            VideoFrameDiskCache.MaximumCacheSizeBytes = GetSettingAs<long>("codec_VideoFrameDiskCacheMaxSizeMB", 0, 0) * 1024 * 1024;
            IVideoSource.EnableMemoryCache = IsBoolSettingTrueOrDefault("codec_EnableMemoryCache", true);
            IVideoSource.EnableDiskCache = IsBoolSettingTrueOrDefault("codec_EnableDiskCache", true);
            ClassicOverlayMixture.EnableApproximatePath = IsBoolSettingTrue("render_preferApproximateMixture");
            IVectorContentClip.GlobalDefaultAntiAliasMode = GetSetting("render_preferredAntiAliasMode", "ssaa4x") switch { "ssaa8x" => AntiAliasMode.SSAA8x, "ssaa4x" => AntiAliasMode.SSAA4x, "ssaa2x" => AntiAliasMode.SSAA2x, _ => AntiAliasMode.None };
            IVectorContentClip.GlobalDefaultRasterizer = IsBoolSettingTrueOrDefault("render_enableHwAccelRasterizer", true) ? new VectorToPictureHwAccel() : new CPUVectorPictureRasterizer();
            NormalTypesettingEngine.DebugDumpAdvance = Debugger.IsAttached && IsBoolSettingTrue("diag_TypesettingEngineDiagMode");
            TextClip.DiagMode = IsBoolSettingTrue("diag_TypesettingEngineDiagMode");
            DynamicPreview.DisableVectorPreviewPaths = IsBoolSettingTrueOrDefault("render_DisallowVectorClipToMAUIPathInPreview", true);
            DynamicPreview.DisableEffectDynamicPreview = IsBoolSettingTrue("render_DisallowViewBasedEffectInPreview");
        }
        int count = 0;
        private async void TapGestureRecognizer_Tapped(object sender, TappedEventArgs e)
        {
            count++;
            if (MauiProgram.IsStoreMode)
            {
                if (count >= 20)
                {
                    var response = await DisplayPromptAsync(Localized._Info, "");
                    if (!string.IsNullOrWhiteSpace(response)) WriteSetting("StoreModeOverride", response);
                }
            }
            else
            {
                if (count >= 20)
                {
                    if (!IsBoolSettingTrue("DeveloperMode"))
                    {
                        WriteSetting("DeveloperMode", "True");
                        await DisplayAlertAsync(Localized._Info, "🛠️✅", Localized._OK);
                    }
                }
            }

        }


    }
}
