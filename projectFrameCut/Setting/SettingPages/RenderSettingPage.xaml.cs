
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

namespace projectFrameCut.Setting.SettingPages;

using static SettingManager.SettingsManager;

public partial class RenderSettingPage : ContentPage
{
    PropertyPanelBuilder rootPPB;
    AcceleratorInfo[] AcceleratorInfos = Array.Empty<AcceleratorInfo>();
    bool showMoreOpts = false;
    Dictionary<int, string> GCOptionMapping = new();
    ConcurrentDictionary<string, EffectImplementType> effectImplementTypes = new();

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
        if (AcceleratorInfos.Length == 0)
        {
            Task t = new(() =>
            {
                AcceleratorInfos = GetAccelInfo();
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
        string[] accels = ["Unknown"];
        try
        {
            accels = AcceleratorInfos?.Select(a => $"#{a.index}: {a.name} ({a.Type})").ToArray() ?? ["Unknown"];
        }
        catch (Exception ex) { Log(ex); }
        var multiAccel = IsBoolSettingTrue("accel_enableMultiAccel");

        if (!int.TryParse(GetSetting("accel_DeviceId", "-1"), out var result) || result < 0 || !(AcceleratorInfos?.Any(c => c.index == result) ?? false))
        {
            var bestAccel = AcceleratorInfos?.Select(c => (c, c.Type switch { "Cuda" => 10, "OpenCL" => 5, "CPU" => -10, _ => 1 })).OrderByDescending(c => c.Item2).ThenByDescending(c => c.c.name).FirstOrDefault();
            WriteSetting("accel_DeviceId", (bestAccel?.c.index ?? 0).ToString());
            Log($"No accelerator defined yet; set to best one {bestAccel?.c.name} ({bestAccel?.c.Type}) by default.");
        }

        rootPPB
            .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Render_AccelOptsTitle, SettingLocalizedResources.Render_AccelOptsSubTitle))
            .AppendWhen(AcceleratorInfos?.Count() < 1, (p) => p.AddCustomChild(new Label { Text = Localized.WelocmePage_NoAccel, TextColor = Colors.Yellow }))
            .AddCheckbox("accel_enableMultiAccel", SettingLocalizedResources.Render_EnableMultiAccel, multiAccel, (s) => s.IsEnabled = AcceleratorInfos?.Count(c => c.Type != "CPU") >= 2)
            .AppendWhen(AcceleratorInfos?.Count(c => c.Type != "CPU") < 2, (p) => p.AddText(new Label { Text = SettingLocalizedResources.Render_EnableMultiAccel_NotAvailable, TextColor = Colors.Gray, FontSize = 12 }))
            .AddPicker("accel_DeviceId", multiAccel ? SettingLocalizedResources.Render_SelectAccel_WhenMultiAccelEnabled : SettingLocalizedResources.Render_SelectAccel, accels, int.TryParse(GetSetting("accel_DeviceId", ""), out result) ? accels[result] : "", null);


        try
        {
            if (multiAccel && AcceleratorInfos?.Length > 0)
            {
                rootPPB
                    .AddSeparator()
                    .AddText(SettingLocalizedResources.Render_SelectAccel_MultiAccel, fontSize: 16)
                    .AddCheckbox("selectAllAccels", SettingLocalizedResources.Render_SelectAccel_SelectAll, GetSetting("accel_MultiDeviceID", "all") == "all", null);

                for (int i = 0; i < AcceleratorInfos.Length; i++) //nobody wants to use CPU accel
                {
                    var key = $"accel_multi_{i + 1}";
                    var def = bool.TryParse(GetSetting(key, "false"), out var v) ? v : false;
                    rootPPB.AddCheckbox(key, $"{AcceleratorInfos[i].Type}: {AcceleratorInfos[i].name}", def, null);
                }
            }
        }
        catch (Exception ex) { Log(ex); }
        finally { rootPPB.AddSeparator(); }
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
                .AddEntry("render_coreAffinityOverride", SettingLocalizedResources.Render_CoreAffinityOverride, GetSetting("render_coreAffinityOverride", ""), SettingLocalizedResources.Render_CoreAffinityOverride_Desc);

        }
        else
        {
            rootPPB.AddButton("showMoreOpts", SettingLocalizedResources.Render_AdvanceOpts_Show, null);
        }

        Content = rootPPB.ListenToChanges(SettingInvoker).BuildWithScrollView();
    }
#if WINDOWS
    public static AcceleratorInfo[] GetAccelInfo()
    {
        try
        {
            ILGPU.Context context = ILGPU.Context.CreateDefault();
            var devices = context.Devices.Where(C => C.AcceleratorType != ILGPU.Runtime.AcceleratorType.CPU).ToList();
            List<AcceleratorInfo> listAccels = new();
            for (uint i = 0; i < devices.Count; i++)
            {
                var item = devices[(int)i];
                listAccels.Add(new AcceleratorInfo(i, item.Name, item.AcceleratorType.ToString()));
            }

            return listAccels.Any() ? listAccels.ToArray() : [new AcceleratorInfo(0, "No support accelerator found on this device.", "CPU")];
        }
        catch (Exception ex)
        {
            Log(ex, "get accel info");
        }
        return Array.Empty<AcceleratorInfo>();

    }
#endif



    public async void SettingInvoker(PropertyPanelPropertyChangedEventArgs args)
    {
        try
        {
            switch (args.Id)
            {
                case "accel_DeviceId":
                    if (args.Value is string str)
                    {
                        var idxStr = str.Substring(str.IndexOf('#') + 1, str.IndexOf(':') - str.IndexOf('#') - 1);
                        if (uint.TryParse(idxStr, out var result))
                        {
                            WriteSetting("accel_DeviceId", (result + 1).ToString());
                        }
                    }
                    return;
                case "showMoreOpts":
                    {
                        showMoreOpts = true;
                        BuildPPB();
                        return;
                    }
                case "accel_enableMultiAccel":
                    if (args.Value is bool en)
                    {
                        WriteSetting("accel_enableMultiAccel", en.ToString());
                        if (en)
                        {
                            try
                            {
                                var saved = GetSetting("accel_MultiDeviceID", "");
                                if (!string.IsNullOrWhiteSpace(saved) && AcceleratorInfos != null)
                                {
                                    if (saved == "all")
                                    {
                                        for (int i = 0; i < AcceleratorInfos.Length; i++) WriteSetting($"accel_multi_{i}", "true");
                                    }
                                    else
                                    {
                                        var parts = saved.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => int.TryParse(s, out var id) ? id : -1).Where(x => x >= 0).ToHashSet();
                                        for (int i = 0; i < AcceleratorInfos.Length; i++) WriteSetting($"accel_multi_{i}", parts.Contains(i) ? "true" : "false");
                                    }
                                }
                            }
                            catch (Exception ex) { Log(ex); }
                        }
                    }
                    BuildPPB();
                    return;
                case var _ when args.Id != null && args.Id.StartsWith("accel_multi_"):
                    // Individual per-accelerator switch changed: persist it and update aggregated accel_MultiDeviceID
                    try
                    {
                        // write this individual switch
                        WriteSetting(args.Id, args.Value?.ToString() ?? "false");
                        var selected = new List<int>();

                        if (AcceleratorInfos != null && AcceleratorInfos.Length > 0)
                        {
                            for (int i = 0; i < AcceleratorInfos.Length; i++)
                            {
                                if (bool.TryParse(GetSetting($"accel_multi_{i}", "false"), out var v) && v) selected.Add(i);
                            }
                            if (selected.Count == 0) WriteSetting("accel_MultiDeviceID", "");
                            else if (selected.Count == AcceleratorInfos.Length) WriteSetting("accel_MultiDeviceID", "all");
                            else WriteSetting("accel_MultiDeviceID", string.Join(',', selected));
                        }

                        Dispatcher.Dispatch(() =>
                        {
                            if (rootPPB?.Components?["selectAllAccels"] is Microsoft.Maui.Controls.Switch selectAllSwitch)
                            {
                                selectAllSwitch.IsToggled = selected.Count == AcceleratorInfos?.Length;
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        Log(ex);
                    }

                    return;
                case "selectAllAccels":
                    try
                    {
                        if ((bool)args.Value)
                        {
                            WriteSetting("accel_enableMultiAccel", "true");
                            WriteSetting($"accel_multi_0", "false");
                            if (AcceleratorInfos is not null)
                            {
                                for (int i = 1; i < AcceleratorInfos.Length; i++)
                                {
                                    WriteSetting($"accel_multi_{i}", "true");
                                }
                            }
                            WriteSetting("accel_MultiDeviceID", "all");
                        }
                        else if (!(bool)args.Value)
                        {
                            WriteSetting("accel_MultiDeviceID", string.Join(",", Enumerable.Range(1, AcceleratorInfos.Length - 1).Select(c => c.ToString())));

                        }

                    }
                    catch (Exception ex) { Log(ex); }
                    //BuildPPB();
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