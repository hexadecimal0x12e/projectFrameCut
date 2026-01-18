using projectFrameCut.ApplicationAPIBase.PropertyPanelBuilders;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.VideoMakeEngine;
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
            { EffectImplementType.ImageSharp , SettingLocalizedResources.RenderEffectImplement_ImageSharp },
            { EffectImplementType.HwAcceleration , SettingLocalizedResources.RenderEffectImplement_HwAcceleration },
            { EffectImplementType.IPicture , SettingLocalizedResources.RenderEffectImplement_IPicture},
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
        Content = new VerticalStackLayout();
        rootPPB = new();
        rootPPB
            .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Render_DefaultExportOpts, SettingLocalizedResources.Render_DefaultExportOpts_Subtitle), null)
            .AddPicker("render_DefaultResolution", Localized.RenderPage_SelectResolution, resolutions, GetSetting("render_DefaultResolution", "3840x2160"), null)
            .AddPicker("render_DefaultFramerate", Localized.RenderPage_SelectFrameRate, framerates, GetSetting("render_DefaultFramerate", "30"), null)
            .AddPicker("render_DefaultEncoding", Localized.RenderPage_SelectEncoding, encodings, GetSetting("render_DefaultEncoding", "h264"), null)
            .AddPicker("render_DefaultBitDepth", Localized.RenderPage_SelectBitdepth, bitdepths, GetSetting("render_DefaultBitDepth", "8bit"), null)
            .AddSeparator();

        rootPPB
            .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Render_RenderEffectImplement, SettingLocalizedResources.Render_RenderEffectImplement_Subtitle))
            .AddButton(SettingLocalizedResources.RenderEffectImplement_Title, async (s, e) => await Navigation.PushAsync(new EffectImplementPickerPage()), null)
            .AddSeparator();

#if WINDOWS
        string[] accels = ["Unknown"];
        try
        {
            accels = AcceleratorInfos?.Select(a => $"#{a.index}: {a.name} ({a.Type})").ToArray() ?? ["Unknown"];
        }
        catch (Exception ex) { Log(ex); }
        var multiAccel = IsBoolSettingTrue("accel_enableMultiAccel");

        rootPPB
            .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Render_AccelOptsTitle, SettingLocalizedResources.Render_AccelOptsSubTitle, 20, 12))
            .AddSwitch("accel_enableMultiAccel", SettingLocalizedResources.Render_EnableMultiAccel, multiAccel, null)
            .AddPicker("accel_DeviceId", multiAccel ? SettingLocalizedResources.Render_SelectAccel_WhenMultiAccelEnabled : SettingLocalizedResources.Render_SelectAccel, accels, int.TryParse(GetSetting("accel_DeviceId", ""), out var result) ? accels[result] : "", null);

        try
        {
            if (multiAccel && AcceleratorInfos?.Length > 0)
            {
                rootPPB
                    .AddSeparator()
                    .AddText(SettingLocalizedResources.Render_SelectAccel_MultiAccel, fontSize: 16)
                    .AddSwitch("selectAllAccels", SettingLocalizedResources.Render_SelectAccel_SelectAll, GetSetting("accel_MultiDeviceID", "all") == "all", null);

                for (int i = 1; i < AcceleratorInfos.Length; i++) //nobody wants to use CPU accel
                {
                    var key = $"accel_multi_{i}";
                    var def = bool.TryParse(GetSetting(key, "false"), out var v) ? v : false;
                    rootPPB.AddSwitch(key, $"{AcceleratorInfos[i].Type}: {AcceleratorInfos[i].name}", def, null);
                }
            }
        }
        catch (Exception ex) { Log(ex); }

#endif


        if (showMoreOpts)
        {
            rootPPB
                .AddSeparator()
                .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Render_AdvanceOpts, SettingLocalizedResources.Misc_DiagOptions_Subtitle, 20, 12))
                .AddPicker("render_GCOption", SettingLocalizedResources.Render_GCOption, GCOptionMapping.Values.ToArray(), GCOptionMapping.TryGetValue(int.Parse(GetSetting("render_GCOption", "0")), out var value) ? value : SettingLocalizedResources.Render_GCOption_LetCLRDoGC)
                .AddSwitch("render_BlockWrite", SettingLocalizedResources.Render_BlockWrite, IsBoolSettingTrue("render_BlockWrite"), null);

        }
        else
        {
            rootPPB.AddSeparator().AddButton("showMoreOpts", SettingLocalizedResources.Render_AdvanceOpts_Show, null);
        }

        Content = rootPPB.ListenToChanges(SettingInvoker).BuildWithScrollView();
    }
#if WINDOWS
    public static AcceleratorInfo[] GetAccelInfo()
    {
        try
        {
            ILGPU.Context context = ILGPU.Context.CreateDefault();
            var devices = context.Devices.ToList();
            List<AcceleratorInfo> listAccels = new();
            for (uint i = 0; i < devices.Count; i++)
            {
                var item = devices[(int)i];
                listAccels.Add(new AcceleratorInfo(i, item.Name, item.AcceleratorType.ToString()));
            }

            return listAccels.ToArray();
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
                            WriteSetting("accel_DeviceId", result.ToString());
                        }
                    }
                    return;
                case "showMoreOpts":
                    {
                        showMoreOpts = true;
                        BuildPPB();
                        break;
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

                        if (AcceleratorInfos != null && AcceleratorInfos.Length > 0)
                        {
                            var selected = new List<int>();
                            for (int i = 0; i < AcceleratorInfos.Length; i++)
                            {
                                if (bool.TryParse(GetSetting($"accel_multi_{i}", "false"), out var v) && v) selected.Add(i);
                            }
                            if (selected.Count == 0) WriteSetting("accel_MultiDeviceID", "");
                            else if (selected.Count == AcceleratorInfos.Length) WriteSetting("accel_MultiDeviceID", "all");
                            else WriteSetting("accel_MultiDeviceID", string.Join(',', selected));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log(ex);
                    }
                    BuildPPB();
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
                    BuildPPB();
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

            }

            if (args.Value != null)
            {
                WriteSetting(args.Id, args.Value?.ToString() ?? "");
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert(Localized._Warn, Localized._ExceptionTemplate(ex), Localized._OK);
        }
    }

}