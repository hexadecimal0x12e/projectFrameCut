using projectFrameCut.Services;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.HwAccelEngine;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Shared;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.Setting.SettingPages;

using static SettingManager.SettingsManager;

public partial class RenderSettingPage : ContentPage
{
    PropertyPanelBuilder rootPPB;
    bool showMoreOpts = false;
    Dictionary<int, string> GCOptionMapping = new();
    ConcurrentDictionary<string, EffectImplementType> effectImplementTypes = new();
#if WINDOWS || LINUX
    AcceleratorDeviceInfo[] AcceleratorDevices = Array.Empty<AcceleratorDeviceInfo>();
    Task<AcceleratorDeviceInfo[]>? acceleratorDiscovery;
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

    Dictionary<string, string> OutputCacheBehaviors = new()
    {
        { SettingLocalizedResources.Render_OutputCacheBehavior_Chunk, "chunk" },
        { SettingLocalizedResources.Render_OutputCacheBehavior_Disabled, "disabled" },
    };

    Dictionary<string, string> ChunkSizeModeMapping = new Dictionary<string, string>
    {
        { SettingLocalizedResources.Render_ChunkRender_SizeMode_Frames, "frames" },
        { SettingLocalizedResources.Render_ChunkRender_SizeMode_Seconds, "seconds" },
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
#if WINDOWS || LINUX
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

    protected override async void OnAppearing()
    {
        base.OnAppearing();

#if WINDOWS || LINUX
        AcceleratorDevices = (await (acceleratorDiscovery ??= Task.Run(AcceleratorsManager.DiscoverDevices)))
            .Where(IsSelectableAccelerator)
            .ToArray();
#endif
        BuildPPB();
    }

#if WINDOWS || LINUX
    private static bool IsSelectableAccelerator(AcceleratorDeviceInfo device) =>
        device.Type != "CPU" && !(device.Type == "OpenCL"
            && device.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase)
            && device.Name.Contains(" CPU", StringComparison.OrdinalIgnoreCase));
#endif

    private void BuildPPB()
    {
        var scrollY = (Content as ScrollView)?.ScrollY ?? 0;
        string cacheBehavior = IsBoolSettingTrueOrDefault("render_enableChunkRender", false) ? "chunk" : "disabled";
        var isCPUBigLittleCore = ThreadAffinityHelper.GetCpuCoreGroups().Count > 1;
        if (!isCPUBigLittleCore && !Settings.ContainsKey("render_enableThreadAffinity")) WriteSetting("render_enableThreadAffinity", "False");
        var suggestedMaxPendingFrame = ((int)(Environment.WorkingSet / ((1920 * 1080 * 3) + 32)) / 2 + 5) / 10 * 10;
        Content = new VerticalStackLayout();
        rootPPB = new();
        rootPPB
            .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Render_DefaultExportOpts, SettingLocalizedResources.Render_DefaultExportOpts_Subtitle), null)
            .AddPicker("render_DefaultResolution", Localized.RenderPage_SelectResolution, resolutions, GetSetting("render_DefaultResolution", "3840x2160"), null)
            .AddPicker("render_DefaultFramerate", Localized.RenderPage_SelectFrameRate, framerates, GetSetting("render_DefaultFramerate", "60"), null)
            .AddPicker("render_DefaultBitRate", Localized.RenderPage_BitRate, RenderPageViewModel.BitRateOptions.Keys.ToArray(), RenderPageViewModel.BitRateOptions.ReverseLookup(GetSetting("render_DefaultBitRate", "auto"), Localized.RenderPage_BitRateAuto))
            .AppendWhen(GetSetting("render_DefaultBitRate", "auto") == "custom", p => p.AddEntry("render_DefaultCustomBitRateMbps", $"{Localized.RenderPage_BitRate} (Mbps)", GetSetting("render_DefaultCustomBitRateMbps", ""), Localized.RenderPage_BitRateAuto, c => c.Keyboard = Keyboard.Numeric))
            .AddSeparator()
            .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Render_RenderingBehavior, SettingLocalizedResources.Render_RenderingBehavior_Subtitle), null)
            .AppendWhen(DeviceInfo.Idiom == DeviceIdiom.Desktop, p => p.AddPicker("render_DefaultPostRenderAction", Localized.RenderPage_PostRenderAction, RenderPageViewModel.PostRenderActionNames.Keys.ToArray(), RenderPageViewModel.PostRenderActionNames.ReverseLookup(RenderPageViewModel.GetPlatformPostRenderAction(Enum.TryParse<PostRenderAction>(GetSetting("render_DefaultPostRenderAction", "None"), out var postRenderAction) ? postRenderAction : PostRenderAction.None), Localized.RenderPage_PostRenderAction_None), null))
            .AddPicker("render_preferredAntiAliasMode", SettingLocalizedResources.Render_AntiAliasMode, AntiAliasModeMapping.Keys.ToArray(), AntiAliasModeMapping.ReverseLookup(GetSetting("render_preferredAntiAliasMode", "ssaa4x"), "SSAA 4x"), null)
            .AddCheckbox("render_chunkResume", SettingLocalizedResources.Render_ChunkRender_Resume, IsBoolSettingTrueOrDefault("render_chunkResume", true))
            .AddCheckbox("render_reuseDynamicPreviewCache", SettingLocalizedResources.Render_ReuseDynamicPreviewCache, IsBoolSettingTrueOrDefault("render_reuseDynamicPreviewCache", false))
            .AddSeparator()
            .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Render_HwAccelOption, SettingLocalizedResources.Render_HwAccelOption_Desc))
            .AddCheckbox("render_preferHwAccelResizeProvider", SettingLocalizedResources.Render_PreferHwAccelResizeProvider, IsBoolSettingTrueOrDefault("render_preferHwAccelResizeProvider", true))
            .AddCheckbox("render_PreferDecoderCropResize", SettingLocalizedResources.Render_PreferDecoderCropResize, IsBoolSettingTrueOrDefault("render_PreferDecoderCropResize", true))
            .AddCheckbox("render_enableHwAccelRasterizer", SettingLocalizedResources.Render_EnableHwAccelRasterizer, IsBoolSettingTrueOrDefault("render_enableHwAccelRasterizer", true))
            .AddCheckbox("render_preferApproximateMixture", SettingLocalizedResources.Render_PreferApproximateMixture, IsBoolSettingTrueOrDefault("render_preferApproximateMixture", true))
            .AddSeparator()
            .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Render_Scheduling, SettingLocalizedResources.Render_Scheduling_Desc))
            .AddButton(SettingLocalizedResources.Render_ScheduleBenchmark, async (_, _) => await Navigation.PushAsync(new ScheduleBenchmarkPage()))
            .AddEntry("render_defaultMaxParallelWorkers", SettingLocalizedResources.Render_MaxParallelWorkers, GetSetting("render_defaultMaxParallelWorkers", "8"), "8", c => c.Keyboard = Keyboard.Numeric)
            .AddEntry("render_renderAheadFrames", SettingLocalizedResources.Render_RenderAheadFrames, GetSetting("render_renderAheadFrames", "0"), SettingLocalizedResources.Render_RenderAheadFrames_Desc, c => c.Keyboard = Keyboard.Numeric)
            .AddCheckbox("render_allowWorkerOversubscription", SettingLocalizedResources.Render_AllowWorkerOversubscription, IsBoolSettingTrueOrDefault("render_allowWorkerOversubscription", true))
            .AddCheckbox("render_enableThreadAffinity", SettingLocalizedResources.Render_EnableAutoThreadAffinity, IsBoolSettingTrueOrDefault("render_enableThreadAffinity", isCPUBigLittleCore), p => p.IsEnabled = isCPUBigLittleCore)
            .AppendWhen(!isCPUBigLittleCore, c => c.AddCustomChild(new Label { Text = SettingLocalizedResources.Render_EnableAutoThreadAffinity_Unsupported, TextColor = Colors.Gray, FontSize = 12 }))
            .AddSeparator()
            .AddPicker("render_outputCacheBehavior", SettingLocalizedResources.Render_OutputCacheBehavior, OutputCacheBehaviors.Keys.ToArray(), OutputCacheBehaviors.ReverseLookup(cacheBehavior, SettingLocalizedResources.Render_OutputCacheBehavior_Disabled))
            .AddCheckbox("render_enableMemoryBudget", SettingLocalizedResources.Render_EnableMemoryBudget, RenderSchedulingSettings.EnableMemoryBudget)
            .AddEntry("render_maxBufferedMiB", SettingLocalizedResources.Render_MaxBufferedMiB, GetSetting("render_maxBufferedMiB", "0"), SettingLocalizedResources.Render_MaxBufferedMiB_Desc, c => c.Keyboard = Keyboard.Numeric)
            .AddSeparator();

#if WINDOWS || LINUX
        var devices = AcceleratorDevices;
        string[] accelDisplayNames = devices.Length > 0
            ? devices.Select(a => $"{a.Name} ({a.Type})").ToArray()
            : ["No accelerator found"];
        var config = AcceleratorsManager.GetConfiguration(devices);
        SyncAcceleratorSettings(config);
        var multiAccel = config.EnableMultiAccel;
        var renderingNames = config.RenderingAcceleratorNames.ToHashSet();
        var selectedDisplay = devices.FirstOrDefault(d => d.Name == config.MainAcceleratorName) is { } match
            ? $"{match.Name} ({match.Type})"
            : "";

        rootPPB
            .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Render_AccelOptsTitle, SettingLocalizedResources.Render_AccelOptsSubTitle))
            .AppendWhen(devices.Length < 1 || !devices.Any(c => c.Type != "CPU"),
                (p) => p.AddCustomChild(new Label { Text = Localized.WelocmePage_NoAccel, TextColor = Colors.Yellow }))
            .AddCheckbox("accel_enableMultiAccel", SettingLocalizedResources.Render_EnableMultiAccel, multiAccel,
                (s) => s.IsEnabled = multiAccel || devices.Count(c => c.Type != "CPU") >= 2)
            .AppendWhen(devices.Count(c => c.Type != "CPU") < 2,
                (p) => p.AddText(new Label { Text = SettingLocalizedResources.Render_EnableMultiAccel_NotAvailable, TextColor = Colors.Gray, FontSize = 12 }))
            .AddPicker("accel_DeviceId",
                multiAccel ? SettingLocalizedResources.Render_SelectAccel_WhenMultiAccelEnabled : SettingLocalizedResources.Render_SelectAccel,
                accelDisplayNames, selectedDisplay, p => p.IsEnabled = devices.Length > 0);

        if (multiAccel && devices.Length > 0)
        {
            rootPPB
                .AddSeparator()
                .AddText(SettingLocalizedResources.Render_SelectAccel_MultiAccel, fontSize: 16)
                .AddCheckbox("selectAllAccels", SettingLocalizedResources.Render_SelectAccel_SelectAll,
                    devices.Where(d => d.Type != "CPU").All(d => renderingNames.Contains(d.Name)), null);

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

        if (showMoreOpts)
        {
            rootPPB
                .AddSeparator()
                .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Render_AdvanceOpts, SettingLocalizedResources.Misc_DiagOptions_Subtitle))
                .AppendWhen(cacheBehavior == "chunk",
                p => p.AddPicker("render_chunkSizeMode", SettingLocalizedResources.Render_ChunkRender_SizeMode, ChunkSizeModeMapping.Keys.ToArray(), ChunkSizeModeMapping.ReverseLookup(GetSetting("render_chunkSizeMode", "frames"), SettingLocalizedResources.Render_ChunkRender_SizeMode_Frames), null)
                .AddEntry("render_chunkFrames", SettingLocalizedResources.Render_ChunkRender_Frames, GetSetting("render_chunkFrames", "3600"), SettingLocalizedResources.Render_ChunkRender_Frames_Desc, c => c.Keyboard = Keyboard.Numeric)
                .AddEntry("render_chunkSeconds", SettingLocalizedResources.Render_ChunkRender_Seconds, GetSetting("render_chunkSeconds", "60"), SettingLocalizedResources.Render_ChunkRender_Seconds_Desc, c => c.Keyboard = Keyboard.Numeric)
                .AddCheckbox("render_chunkKeepFiles", SettingLocalizedResources.Render_ChunkRender_KeepFiles, IsBoolSettingTrue("render_chunkKeepFiles"))
                .AddSeparator()
                .AddEntry("Render_AudioComposeBufferSize", SettingLocalizedResources.Render_AudioComposeBufferSize, GetSettingAs<int>("Render_AudioComposeBufferSize", 40960, 40960).ToString(), "40960", c => c.Keyboard = Keyboard.Numeric))
                .AddSeparator()
                .AddEntry("render_coreAffinityOverride", SettingLocalizedResources.Render_CoreAffinityOverride, GetSetting("render_coreAffinityOverride", ""), SettingLocalizedResources.Render_CoreAffinityOverride_Desc)
                .AddCheckbox("render_forceImpType_ForceHwAccel", SettingLocalizedResources.Render_ForceImpType_ForceHwAccel, IsBoolSettingTrueOrDefault("render_forceImpType_ForceHwAccel", true), null)
                .AddCheckbox("render_forceImpType_ForceIPicture", SettingLocalizedResources.Render_ForceImpType_ForceIPicture, IsBoolSettingTrue("render_forceImpType_ForceIPicture"), null)
                .AddPicker("render_GCOption", SettingLocalizedResources.Render_GCOption, GCOptionMapping.Values.ToArray(), GCOptionMapping.GetValueOrDefault(GetSettingAs("render_GCOption", 0, 0), SettingLocalizedResources.Render_GCOption_LetCLRDoGC));

        }
        else
        {
            rootPPB.AddButton("showMoreOpts", SettingLocalizedResources.Render_AdvanceOpts_Show, null);
        }

        Content = rootPPB.ListenToChanges(SettingInvoker).BuildWithScrollView();
        if (scrollY > 0 && Content is ScrollView scroll)
            Dispatcher.Dispatch(async () => await scroll.ScrollToAsync(0, scrollY, false));
    }

#if WINDOWS || LINUX
    private void SyncAcceleratorSettings(AcceleratorsManager.AcceleratorsStore config)
    {
        if (AcceleratorDevices.FirstOrDefault(d => d.Name == config.MainAcceleratorName) is not { } main)
            return;
        WriteIfChanged("accel_DeviceId", main.Index.ToString());
        WriteIfChanged("accel_MultiDeviceID", string.Join(",", AcceleratorDevices
            .Where(d => config.RenderingAcceleratorNames.Contains(d.Name)).Select(d => d.Index)));
        WriteIfChanged("accel_enableMultiAccel", config.EnableMultiAccel.ToString());

        void WriteIfChanged(string key, string value)
        {
            if (!Settings.TryGetValue(key, out var current) || current != value)
                WriteSetting(key, value);
        }
    }

    private void SaveAcceleratorConfiguration(string mainName, string[] renderingNames, bool multiAccel)
    {
        AcceleratorsManager.ApplyConfiguration(mainName, renderingNames, multiAccel);
        BuildPPB();
    }
#endif


    public async void SettingInvoker(PropertyPanelPropertyChangedEventArgs args)
    {
        try
        {
            switch (args.Id)
            {
                case "render_DefaultBitRate":
                    WriteSetting(args.Id, RenderPageViewModel.BitRateOptions.GetValueOrDefault(args.Value?.ToString() ?? "", "auto"));
                    BuildPPB();
                    return;
                case "render_DefaultCustomBitRateMbps":
                    if (!RenderPageViewModel.TryParseVideoBitRate(args.Value?.ToString() ?? "", out _))
                    {
                        await DisplayAlertAsync(Localized._Error, Localized.RenderPage_InvalidBitRate, Localized._OK);
                        return;
                    }
                    WriteSetting(args.Id, args.Value?.ToString()?.Trim() ?? "");
                    return;
#if WINDOWS || LINUX
                case "accel_DeviceId":
                    {
                        if (args.Value is string str && AcceleratorDevices.FirstOrDefault(d => $"{d.Name} ({d.Type})" == str) is { } dev)
                        {
                            var config = AcceleratorsManager.GetConfiguration(AcceleratorDevices);
                            SaveAcceleratorConfiguration(dev.Name,
                                config.EnableMultiAccel ? config.RenderingAcceleratorNames : [dev.Name], config.EnableMultiAccel);
                        }
                        return;
                    }
                case "accel_enableMultiAccel":
                    if (args.Value is bool en)
                    {
                        var config = AcceleratorsManager.GetConfiguration(AcceleratorDevices);
                        SaveAcceleratorConfiguration(config.MainAcceleratorName, en
                            ? AcceleratorDevices.Where(d => d.Type != "CPU").Select(d => d.Name).ToArray()
                            : [config.MainAcceleratorName], en);
                    }
                    return;
                case var _ when args.Id != null && args.Id.StartsWith("accel_device_"):
                    {
                        if (!int.TryParse(args.Id.AsSpan("accel_device_".Length), out var devIdx)) return;
                        if (devIdx < 0 || devIdx >= AcceleratorDevices.Length) return;
                        if (args.Value is not bool isChecked || AcceleratorDevices[devIdx].Type == "CPU") return;
                        var config = AcceleratorsManager.GetConfiguration(AcceleratorDevices);
                        if (!config.EnableMultiAccel) return;

                        var checkedNames = new HashSet<string>();
                        for (int i = 0; i < AcceleratorDevices.Length; i++)
                        {
                            if (AcceleratorDevices[i].Type == "CPU") continue;
                            if (rootPPB.Components.TryGetValue($"accel_device_{i}", out var control) && control is CheckBox cb && cb.IsChecked)
                                checkedNames.Add(AcceleratorDevices[i].Name);
                        }
                        if (isChecked)
                            checkedNames.Add(AcceleratorDevices[devIdx].Name);
                        else
                            checkedNames.Remove(AcceleratorDevices[devIdx].Name);
                        if (checkedNames.Count == 0)
                        {
                            BuildPPB();
                            return;
                        }
                        if (checkedNames.SetEquals(config.RenderingAcceleratorNames)) return;
                        SaveAcceleratorConfiguration(config.MainAcceleratorName, checkedNames.ToArray(), true);
                        return;
                    }
                case "selectAllAccels":
                    {
                        if (args.Value is not bool selectAll) return;
                        var names = AcceleratorDevices.Where(d => d.Type != "CPU").Select(d => d.Name).ToArray();
                        if (names.Length == 0) return;
                        var config = AcceleratorsManager.GetConfiguration(AcceleratorDevices);
                        SaveAcceleratorConfiguration(config.MainAcceleratorName,
                            selectAll ? names : [names.Contains(config.MainAcceleratorName) ? config.MainAcceleratorName : names[0]], true);
                        return;
                    }
#endif
                case "showMoreOpts":
                    {
                        showMoreOpts = true;
                        BuildPPB();
                        return;
                    }
                case "render_renderAheadFrames":
                case "render_maxBufferedMiB":
                    if (long.TryParse(args.Value?.ToString(), out var number) && number >= 0
                        && number <= (args.Id == "render_renderAheadFrames" ? int.MaxValue : long.MaxValue / (1024 * 1024)))
                    {
                        WriteSetting(args.Id, number.ToString());
                    }
                    return;
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
                case "render_preferredAntiAliasMode":
                    {
                        var mode = AntiAliasModeMapping.TryGetValue(args.Value as string, out var aaMode) ? aaMode : "ssaa4x";
                        WriteSetting(args.Id, mode);
                        break;
                    }
                case "render_chunkSizeMode":
                    {
                        var mode = ChunkSizeModeMapping.TryGetValue(args.Value as string, out var chunkMode) ? chunkMode : "frames";
                        WriteSetting(args.Id, mode);
                        break;
                    }
                case "render_outputCacheBehavior":
                    if (args.Value is string selected && OutputCacheBehaviors.TryGetValue(selected, out var behavior))
                    {
                        WriteSetting("render_enableChunkRender", (behavior == "chunk").ToString());
                        BuildPPB();
                    }
                    break;
                case "render_enableThreadAffinity":
                case "render_enableDiskCacheRouting":
                    if (args.Value != null)
                    {
                        WriteSetting(args.Id, args.Value?.ToString() ?? "");
                    }
                    BuildPPB();
                    break;
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
            Log(ex, $"Change render setting '{args.Id}'", this);
            await DisplayAlertAsync(Localized._Warn, Localized._ExceptionTemplate(ex), Localized._OK);
        }
    }

}
