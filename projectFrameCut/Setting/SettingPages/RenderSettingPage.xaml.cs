using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Shared;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization;

#if WINDOWS
using projectFrameCut.Render.HwAccelEngine.Platforms.Windows;

#endif

namespace projectFrameCut.Setting.SettingPages;

using static SettingManager.SettingsManager;

public partial class RenderSettingPage : ContentPage
{
    PropertyPanelBuilder rootPPB;
    bool showMoreOpts = false;
    Dictionary<int, string> GCOptionMapping = new();
    ConcurrentDictionary<string, EffectImplementType> effectImplementTypes = new();
#if WINDOWS
    AcceleratorDeviceInfo[] AcceleratorDevices = Array.Empty<AcceleratorDeviceInfo>();
#endif

    Dictionary<EffectImplementType, string> LocalizedImplementTypes = new Dictionary<EffectImplementType, string>
    {
            { EffectImplementType.NotSpecified , SettingLocalizedResources.RenderEffectImplement_NotSpecified },
            { EffectImplementType.HwAcceleration , SettingLocalizedResources.RenderEffectImplement_HwAcceleration },
            { EffectImplementType.IPicture , SettingLocalizedResources.RenderEffectImplement_IPicture},
    };

    Dictionary<string, string> AndroidHWAccelImpTypeMapping = new Dictionary<string, string>
    {
        {SettingLocalizedResources.Render_AndroidHwAccleImpType_Vulkan, "vulkan" },
        {SettingLocalizedResources.Render_AndroidHwAccleImpType_OpenGL, "opengl" },

    };

    Dictionary<string, string> AntiAliasModeMapping = new Dictionary<string, string>
    {
        { SettingLocalizedResources.Render_AntiAliasMode_None, "none" },
        { "SSAA 2x", "ssaa2x" },
        { "SSAA 4x", "ssaa4x" },
        { "SSAA 8x", "ssaa8x" },
    };

    string[] resolutions = new[] { "1280x720", "1920x1080", "2560x1440", "3840x2160", "7680x4320" };
    string[] framerates = new[] { "23.97", "24", "29.97", "30", "44.96", "45", "59.94", "60", "89.91", "90", "119.88", "120" };
    string[] encodings = new[] { "h264", "h265/hevc", "av1" };
    string[] bitdepths = new[] { "8bit", "10bit", "12bit" };

    public RenderSettingPage()
    {
        Title = Localized.MainSettingsPage_Tab_Render;
        Content = new VerticalStackLayout
        {
            Children =
            {
                new ActivityIndicator
                {
                    IsRunning = true,
                    VerticalOptions = LayoutOptions.Center,
                    HorizontalOptions = LayoutOptions.Center
                },
                new Label
                {
                    Text = SettingLocalizedResources.Render_LoadingAccels,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center
                }
            },
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        };
        GCOptionMapping = new Dictionary<int, string>
        {
            {0, SettingLocalizedResources.Render_GCOption_LetCLRDoGC },
            {1, SettingLocalizedResources.Render_GCOption_DoNormalCollection },
#if WINDOWS
            {2, SettingLocalizedResources.Render_GCOption_DoLOHCompression }
#endif
        };
        if (File.Exists(Path.Combine(MauiProgram.BasicDataPath, "EffectImplement.json")))
        {
            string json = File.ReadAllText(Path.Combine(MauiProgram.BasicDataPath, "EffectImplement.json"));
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, EffectImplementType>>(json);
                if (dict != null)
                {
                    effectImplementTypes = new ConcurrentDictionary<string, EffectImplementType>(dict);
                }
            }
            catch (Exception ex)
            {
                Log(ex, "read effectImplement", this);
            }
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

#if WINDOWS
        if (AcceleratorDevices.Length == 0)
        {
            Task t = new(() =>
            {
                AcceleratorDevices = AcceleratorsManager.DiscoverDevices();
            });
            t.Start();
            t.ContinueWith((_) => Dispatcher.Dispatch(BuildPPB));
        }
        else
        {
            BuildPPB();
        }
#else
        BuildPPB();
#endif
    }

    private void BuildPPB()
    {
        var isCPUBigLittleCore = ThreadAffinityHelper.GetCpuCoreGroups().Count > 1;
        if (!isCPUBigLittleCore && !Settings.ContainsKey("render_enableThreadAffinity")) WriteSetting("render_enableThreadAffinity", "False");
        Content = new VerticalStackLayout();
        rootPPB = new();
        rootPPB
            .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Render_DefaultExportOpts, SettingLocalizedResources.Render_DefaultExportOpts_Subtitle), null)
            .AddPicker("render_DefaultResolution", Localized.RenderPage_SelectResolution, resolutions, GetSetting("render_DefaultResolution", "3840x2160"), null)
            .AddPicker("render_DefaultFramerate", Localized.RenderPage_SelectFrameRate, framerates, GetSetting("render_DefaultFramerate", "60"), null)
            .AddPicker("render_DefaultEncoding", Localized.RenderPage_SelectEncoding, encodings, GetSetting("render_DefaultEncoding", "h264"), null)
            .AddPicker("render_DefaultBitDepth", Localized.RenderPage_SelectBitdepth, bitdepths, GetSetting("render_DefaultBitDepth", "8bit"), null)
            .AddSeparator()
            .AppendWhen(!IsBoolSettingTrueOrDefault("render_enableThreadAffinity", true), p => p.AddSlider("render_defaultMaxParallelWorkers", SettingLocalizedResources.Render_MaxParallelWorkers, 1, 64, (int)GetSettingAs<double>("render_defaultMaxParallelWorkers", 8, 8)))
            .AppendWhen(DeviceInfo.Idiom == DeviceIdiom.Desktop, p => p.AddPicker("render_DefaultPostRenderAction", Localized.RenderPage_PostRenderAction, RenderPageViewModel.PostRenderActionNames.Keys.ToArray(), Localized.DynamicLookup($"RenderPage_PostRenderAction_{GetSetting("render_DefaultPostRenderAction", "None")}", Localized.RenderPage_PostRenderAction_None), null))
            .AddPicker("render_preferredAntiAliasMode", SettingLocalizedResources.Render_AntiAliasMode, AntiAliasModeMapping.Keys.ToArray(), AntiAliasModeMapping.ReverseLookup(GetSetting("render_preferredAntiAliasMode", "ssaa4x"), "SSAA 4x"), null)
            .AddSeparator()
            .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Render_ComposeOption, SettingLocalizedResources.Render_ComposeOption_Desc))
            .AddCheckbox("render_preferHwAccelResizeProvider", SettingLocalizedResources.Render_PreferHwAccelResizeProvider, IsBoolSettingTrueOrDefault("render_preferHwAccelResizeProvider", true))
            .AddCheckbox("render_enableHwAccelRasterizer", SettingLocalizedResources.Render_EnableHwAccelRasterizer, IsBoolSettingTrueOrDefault("render_enableHwAccelRasterizer", true))
            .AddCheckbox("render_preferApproximateMixture", SettingLocalizedResources.Render_PreferApproximateMixture, IsBoolSettingTrueOrDefault("render_preferApproximateMixture", true))
            .AddCheckbox("render_enableBatchProcess", SettingLocalizedResources.Render_EnableBatchProcess, IsBoolSettingTrueOrDefault("render_enableBatchProcess", true))
            .AddSeparator()
            .AddCheckbox("render_RenderByLayer", SettingLocalizedResources.Render_RenderByLayer, IsBoolSettingTrue("render_RenderByLayer"), null)
            .AddCheckbox("render_prepareInWorkerThreads", SettingLocalizedResources.Render_PrepareInWorkerThreads, IsBoolSettingTrueOrDefault("render_prepareInWorkerThreads", true))
            .AddCheckbox("render_allowEffectOutOfOrder", SettingLocalizedResources.Render_AllowEffectOutOfOrder, IsBoolSettingTrueOrDefault("render_allowEffectOutOfOrder", true))
            .AppendWhen(IsBoolSettingTrueOrDefault("render_prepareInWorkerThreads", true) && PluginManager.LoadedPlugins.Any(c => !c.Key.StartsWith("projectFrameCut.Render")), p => p.AddText(new Label { Text = SettingLocalizedResources.Render_PrepareInWorkerThreads_3rdPluginWarn, TextColor = Colors.Yellow }))
            .AddCheckbox("render_enableThreadAffinity", SettingLocalizedResources.Render_EnableAutoThreadAffinity, IsBoolSettingTrueOrDefault("render_enableThreadAffinity", isCPUBigLittleCore), p => p.IsEnabled = isCPUBigLittleCore)
            .AppendWhen(!isCPUBigLittleCore, c => c.AddCustomChild(new Label { Text = SettingLocalizedResources.Render_EnableAutoThreadAffinity_Unsupported, TextColor = Colors.Gray, FontSize = 12 }))
            .AddSeparator();

#if WINDOWS
        var devices = AcceleratorDevices;
        string[] accelDisplayNames = devices.Length > 0
            ? devices.Select(a => $"{a.Name} ({a.Type})").ToArray()
            : ["No accelerator found"];
        var multiAccel = AcceleratorsManager.IsMultiAccelEnabled;
        var currentMainName = AcceleratorsManager.DefaultAccelerator?.Name ?? "";
        var renderingNames = AcceleratorsManager.AcceleratorsForRendering
            .Select(a => a.Name).ToHashSet();

        // Find which display string matches the current main accelerator
        var selectedDisplay = devices.FirstOrDefault(d => d.Name == currentMainName) is { } match
            ? $"{match.Name} ({match.Type})"
            : accelDisplayNames.FirstOrDefault() ?? "";

        rootPPB
            .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Render_AccelOptsTitle, SettingLocalizedResources.Render_AccelOptsSubTitle))
            .AppendWhen(devices.Length < 1 || !devices.Any(c => c.Type != "CPU"),
                (p) => p.AddCustomChild(new Label { Text = Localized.WelocmePage_NoAccel, TextColor = Colors.Yellow }))
            .AddCheckbox("accel_enableMultiAccel", SettingLocalizedResources.Render_EnableMultiAccel, multiAccel,
                (s) => s.IsEnabled = devices.Count(c => c.Type != "CPU") >= 2)
            .AppendWhen(devices.Count(c => c.Type != "CPU") < 2,
                (p) => p.AddText(new Label { Text = SettingLocalizedResources.Render_EnableMultiAccel_NotAvailable, TextColor = Colors.Gray, FontSize = 12 }))
            .AddPicker("accel_DeviceId",
                multiAccel ? SettingLocalizedResources.Render_SelectAccel_WhenMultiAccelEnabled : SettingLocalizedResources.Render_SelectAccel,
                accelDisplayNames, selectedDisplay, null);

        if (multiAccel && devices.Length > 0)
        {
            rootPPB
                .AddSeparator()
                .AddText(SettingLocalizedResources.Render_SelectAccel_MultiAccel, fontSize: 16)
                .AddCheckbox("selectAllAccels", SettingLocalizedResources.Render_SelectAccel_SelectAll,
                    renderingNames.Count == devices.Count(d => d.Type != "CPU"), null);

            for (int i = 0; i < devices.Length; i++)
            {
                var d = devices[i];
                if (d.Type == "CPU") continue;
                var key = $"accel_device_{i}";
                var isChecked = renderingNames.Contains(d.Name);
                rootPPB.AddCheckbox(key, $"{d.Type}: {d.Name}", isChecked, null);
            }
        }
#elif ANDROID
        rootPPB
            .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Render_AccelOptsTitle, SettingLocalizedResources.Render_AccelOptsSubTitle))
            .AddPicker("render_AndroidHWAccelType", SettingLocalizedResources.Render_AndroidHwAccleImpType, AndroidHWAccelImpTypeMapping.Keys.ToArray(), AndroidHWAccelImpTypeMapping.ReverseLookup(GetSetting("render_AndroidHWAccelType", "vulkan"), SettingLocalizedResources.Render_AndroidHwAccleImpType_Vulkan));
#endif
        rootPPB
            .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Render_RenderEffectImplement, SettingLocalizedResources.Render_RenderEffectImplement_Subtitle))
            .AddButton(SettingLocalizedResources.RenderEffectImplement_Title, async (s, e) => await Navigation.PushAsync(new EffectImplementPickerPage()), null)
            .AddSeparator();


        if (showMoreOpts)
        {
            rootPPB
                .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Render_AdvanceOpts, SettingLocalizedResources.Misc_DiagOptions_Subtitle))
                .AddCheckbox("render_forceImpType_ForceHwAccel", SettingLocalizedResources.Render_ForceImpType_ForceHwAccel, IsBoolSettingTrue("render_forceImpType_ForceHwAccel"), null)
                .AddCheckbox("render_forceImpType_ForceIPicture", SettingLocalizedResources.Render_ForceImpType_ForceIPicture, IsBoolSettingTrue("render_forceImpType_ForceIPicture"), null)
                .AddPicker("render_GCOption", SettingLocalizedResources.Render_GCOption, GCOptionMapping.Values.ToArray(), GCOptionMapping.TryGetValue(int.Parse(GetSetting("render_GCOption", "0")), out var value) ? value : SettingLocalizedResources.Render_GCOption_LetCLRDoGC)
                .AddCheckbox("render_BlockWrite", SettingLocalizedResources.Render_BlockWrite, IsBoolSettingTrue("render_BlockWrite"), null)
                .AddEntry("Render_AudioComposeBufferSize", SettingLocalizedResources.Render_AudioComposeBufferSize, GetSettingAs<int>("Render_AudioComposeBufferSize", 40960, 40960).ToString(), "40960", c => c.Keyboard = Keyboard.Numeric)
                .AppendWhen(!isCPUBigLittleCore ,c => c.AddCheckbox("render_enableThreadAffinity", SettingLocalizedResources.Render_EnableAutoThreadAffinity, IsBoolSettingTrueOrDefault("render_enableThreadAffinity", isCPUBigLittleCore)))
                .AddEntry("render_coreAffinityOverride", SettingLocalizedResources.Render_CoreAffinityOverride, GetSetting("render_coreAffinityOverride", ""), SettingLocalizedResources.Render_CoreAffinityOverride_Desc);

        }
        else
        {
            rootPPB.AddButton("showMoreOpts", SettingLocalizedResources.Render_AdvanceOpts_Show, null);
        }

        Content = rootPPB.ListenToChanges(SettingInvoker).BuildWithScrollView();
    }


    public async void SettingInvoker(PropertyPanelPropertyChangedEventArgs args)
    {
        try
        {
            switch (args.Id)
            {
#if WINDOWS
                case "accel_DeviceId":
                    if (args.Value is string str && AcceleratorDevices.Length > 0)
                    {
                        // str is in format "Name (Type)" — extract the name part
                        var name = str;
                        var parenIdx = str.LastIndexOf(" (", StringComparison.Ordinal);
                        if (parenIdx > 0) name = str.Substring(0, parenIdx);

                        if (AcceleratorDevices.Any(d => d.Name == name))
                        {
                            var dev = AcceleratorDevices.First(d => d.Name == name);
                            // Save single-accelerator config (disable multi-accel)
                            AcceleratorsManager.SetDefaultAccelerator(dev.Name);
                        }
                    }
                    return;
                case "accel_enableMultiAccel":
                    if (args.Value is bool en)
                    {
                        var mainName = AcceleratorsManager.DefaultAccelerator?.Name ?? "";
                        if (en)
                        {
                            // Enable multi-accel: include all non-CPU devices
                            var allNames = AcceleratorDevices.Where(d => d.Type != "CPU").Select(d => d.Name).ToArray();
                            AcceleratorsManager.ApplyConfiguration(
                                mainName, allNames, true);
                        }
                        else
                        {
                            // Disable multi-accel: use just the main accelerator
                            AcceleratorsManager.ApplyConfiguration(
                                mainName, [mainName], false);
                        }
                    }
                    BuildPPB();
                    return;
                case var _ when args.Id != null && args.Id.StartsWith("accel_device_"):
                    // Individual per-accelerator checkbox changed
                    try
                    {
                        if (!int.TryParse(args.Id.AsSpan("accel_device_".Length), out var devIdx)) return;
                        if (devIdx < 0 || devIdx >= AcceleratorDevices.Length) return;

                        // Collect all currently-checked device names from UI state
                        var checkedNames = new List<string>();
                        for (int i = 0; i < AcceleratorDevices.Length; i++)
                        {
                            if (AcceleratorDevices[i].Type == "CPU") continue;
                            var key = $"accel_device_{i}";
                            // The panel component holds the live toggle state
                            if (rootPPB?.Components?[key] is Microsoft.Maui.Controls.Switch sw && sw.IsToggled)
                                checkedNames.Add(AcceleratorDevices[i].Name);
                        }
                        if (checkedNames.Count == 0) checkedNames.Add(AcceleratorDevices[devIdx].Name); // keep at least one

                        var mainName = AcceleratorDevices.First().Name;
                        AcceleratorsManager.ApplyConfiguration(
                            mainName, checkedNames.ToArray(), true);

                        // Sync select-all checkbox
                        var nonCpuCount = AcceleratorDevices.Count(d => d.Type != "CPU");
                        Dispatcher.Dispatch(() =>
                        {
                            if (rootPPB?.Components?["selectAllAccels"] is Microsoft.Maui.Controls.Switch selectAll)
                                selectAll.IsToggled = checkedNames.Count >= nonCpuCount;
                        });
                    }
                    catch (Exception ex) { Log(ex); }
                    return;
                case "selectAllAccels":
                    try
                    {
                        var mainName = AcceleratorsManager.DefaultAccelerator?.Name ?? "";
                        if ((bool)args.Value)
                        {
                            // Select all non-CPU
                            var allNames = AcceleratorDevices.Where(d => d.Type != "CPU").Select(d => d.Name).ToArray();
                            AcceleratorsManager.ApplyConfiguration(
                                mainName, allNames, true);
                        }
                        else
                        {
                            // Deselect all but the first non-CPU (keep at least one)
                            var first = AcceleratorDevices.FirstOrDefault(d => d.Type != "CPU");
                            if (first is not null)
                                AcceleratorsManager.ApplyConfiguration(
                                    first.Name, [first.Name], true);
                        }
                    }
                    catch (Exception ex) { Log(ex); }
                    return;
#endif
                case "showMoreOpts":
                    {
                        showMoreOpts = true;
                        BuildPPB();
                        return;
                    }
                case "render_GCOption":
                    {
                        var key = GCOptionMapping.FirstOrDefault(k => k.Value == args.Value as string, new(0, "letCLRDoCollection"));
                        if (!OperatingSystem.IsWindows() && key.Key == 2)
                        {
                            await DisplayAlertAsync(Localized._Warn, "LOH is not supported on this platform.", Localized._OK);
                            return;
                        }
                        WriteSetting("render_GCOption", key.Key.ToString());
                        return;
                    }
                case "render_DefaultPostRenderAction":
                    {
                        if (args.Value is string localizedAction)
                        {
                            if (RenderPageViewModel.PostRenderActionNames.TryGetValue(localizedAction, out var actionEnum))
                            {
                                WriteSetting("render_DefaultPostRenderAction", actionEnum.ToString());
                            }
                        }
                        return;
                    }
                case "render_AndroidHWAccelType":
                    {
                        var type = AndroidHWAccelImpTypeMapping.TryGetValue(args.Value as string, out var hwt) ? hwt : "vulkan";
                        WriteSetting("render_AndroidHWAccelType", type);
                        await MainSettingsPage.RebootApp(this);
                        return;
                    }

                case var _ when args.Id != null && args.Id.StartsWith("effectImplement,"):
                    {
                        var effectKey = args.Id.Substring("effectImplement,".Length);
                        if (args.Value is string valStr)
                        {
                            var implementType = LocalizedImplementTypes.FirstOrDefault(k => k.Value == valStr, new(EffectImplementType.NotSpecified, "NotSpecified")).Key;
                            effectImplementTypes[effectKey] = implementType;
                            // persist to file
                            try
                            {
                                var dict = effectImplementTypes.ToDictionary(c => c.Key, c => c.Value);
                                var json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
                                File.WriteAllText(Path.Combine(MauiProgram.BasicDataPath, "EffectImplement.json"), json);
                            }
                            catch (Exception ex)
                            {
                                Log(ex, "write effectImplement");
                            }
                        }
                        return;
                    }
                case "render_forceImpType_ForceHwAccel":
                    {
                        WriteSetting(args.Id, args.Value?.ToString() ?? "");

                        if (IsBoolSettingTrue("render_forceImpType_ForceHwAccel") && IsBoolSettingTrue("render_forceImpType_ForceIPicture"))
                        {
                            WriteSetting("render_forceImpType_ForceIPicture", "False");
                            BuildPPB();
                        }

                        break;
                    }
                case "render_forceImpType_ForceIPicture":
                    {
                        WriteSetting(args.Id, args.Value?.ToString() ?? "");

                        if (IsBoolSettingTrue("render_forceImpType_ForceHwAccel") && IsBoolSettingTrue("render_forceImpType_ForceIPicture"))
                        {
                            WriteSetting("render_forceImpType_ForceHwAccel", "False");
                            BuildPPB();
                        }
                        break;
                    }
                case "render_enableThreadAffinity":
                    if (args.Value != null)
                    {
                        WriteSetting(args.Id, args.Value?.ToString() ?? "");
                    }
                    BuildPPB();
                    break;
                case "render_preferredAntiAliasMode":
                    {
                        var mode = AntiAliasModeMapping.TryGetValue(args.Value as string, out var aaMode) ? aaMode : "ssaa4x";
                        WriteSetting(args.Id, mode);
                        break;
                    }
                default:
                    if (args.Value != null)
                    {
                        WriteSetting(args.Id, args.Value?.ToString() ?? "");
                    }
                    break;

            }


        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(Localized._Warn, Localized._ExceptionTemplate(ex), Localized._OK);
        }
    }

}