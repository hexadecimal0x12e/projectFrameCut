
using projectFrameCut.PropertyPanel;
using projectFrameCut.Shared;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.Setting.SettingPages;

using static SettingManager.SettingsManager;

public partial class RenderSettingPage : ContentPage
{
    PropertyPanel.PropertyPanelBuilder rootPPB;
    AcceleratorInfo[] AcceleratorInfos = Array.Empty<AcceleratorInfo>();
    bool showMoreOpts = false;
    Dictionary<string, string> GCOptionMapping = new();
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
        GCOptionMapping = new Dictionary<string, string>
        {
            {"letCLRDoCollection", SettingLocalizedResources.Render_GCOption_LetCLRDoGC },
            {"doNormalCollection", SettingLocalizedResources.Render_GCOption_DoNormalCollection },
            {"doLOHCompression", SettingLocalizedResources.Render_GCOption_DoLOHCompression }
        };
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
#if WINDOWS

        string[] accels = ["Unknown"];
        try
        {
            accels = AcceleratorInfos?.Select(a => $"#{a.index}: {a.name} ({a.Type})").ToArray() ?? ["Unknown"];
        }
        catch (Exception ex) { Log(ex); }

#endif

        rootPPB = new();
        rootPPB
            .AddText(new PropertyPanel.TitleAndDescriptionLineLabel(SettingLocalizedResources.Render_AccelOptsTitle, SettingLocalizedResources.Render_AccelOptsSubTitle, 20, 12))
#if WINDOWS
            .AddPicker("accel_DeviceId", SettingLocalizedResources.Render_SelectAccel, accels, int.TryParse(GetSetting("accel_DeviceId", ""), out var result) ? accels[result] : "", null)
            .AddSwitch("accel_enableMultiAccel", SettingLocalizedResources.Render_EnableMultiAccel, bool.TryParse(GetSetting("accel_enableMultiAccel", "false"), out var result1) ? result1 : false, null)
            .AddPicker("render_SelectRenderHost", SettingLocalizedResources.Render_SelectRenderHost, [SettingLocalizedResources.Render_RenderHost_UseLivePreviewInsteadOfBackend, SettingLocalizedResources.Render_RenderHost_UseBackendAsRenderHost], GetSetting("render_UseLivePreviewInsteadOfBackend", "True") == "True" ? SettingLocalizedResources.Render_RenderHost_UseLivePreviewInsteadOfBackend : SettingLocalizedResources.Render_RenderHost_UseBackendAsRenderHost);
        try
        {
            var multiEnabled = bool.TryParse(GetSetting("accel_enableMultiAccel", "false"), out var me) ? me : false;
            if (multiEnabled && AcceleratorInfos?.Length > 0)
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
#else
            .AddText(new PropertyPanel.SingleLineLabel(SettingLocalizedResources.Render_AccelOptsNotSupported, 14));
#endif
        rootPPB
            .AddSeparator()
            .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Render_DefaultExportOpts, SettingLocalizedResources.Render_DefaultExportOpts_Subtitle), null);

        var resolutions = new[] { "1280x720", "1920x1080", "2560x1440", "3840x2160", "7680x4320" };
        var framerates = new[] { "24", "30", "45", "60", "90", "120" };
        var encodings = new[] { "h264", "h265/hevc", "av1" };
        var bitdepths = new[] { "8bit", "10bit", "12bit" };

        rootPPB
            .AddPicker("render_DefaultResolution", Localized.RenderPage_SelectResolution, resolutions, GetSetting("render_DefaultResolution", "3840x2160"), null)
            .AddPicker("render_DefaultFramerate", Localized.RenderPage_SelectFrameRate, framerates, GetSetting("render_DefaultFramerate", "30"), null)
            .AddPicker("render_DefaultEncoding", Localized.RenderPage_SelectEncoding, encodings, GetSetting("render_DefaultEncoding", "h264"), null)
            .AddPicker("render_DefaultBitDepth", Localized.RenderPage_SelectBitdepth, bitdepths, GetSetting("render_DefaultBitDepth", "8bit"), null);

        if (showMoreOpts)
        {
            rootPPB
                .AddSeparator()
                .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Render_AdvanceOpts, SettingLocalizedResources.Misc_DiagOptions_Subtitle, 20, 12))
                .AddEntry("render_UserDefinedOpts", SettingLocalizedResources.Render_CustomOpts, GetSetting("render_UserDefinedOpts", ""), SettingLocalizedResources.Render_CustomOpts_Placeholder)
                .AddPicker("render_GCOption", SettingLocalizedResources.Render_GCOption, GCOptionMapping.Values.ToArray(), GCOptionMapping.TryGetValue(GetSetting("render_GCOption", "letCLRDoCollection"), out var value) ? value : SettingLocalizedResources.Render_GCOption_LetCLRDoGC)
                .AddSwitch("render_ShowBackendConsole", SettingLocalizedResources.Render_ShowBackendConsole, IsBoolSettingTrue("render_ShowBackendConsole"), null);
        }
        else
        {
            rootPPB.AddSeparator().AddButton("showMoreOpts", SettingLocalizedResources.Render_AdvanceOpts_Show, null);
        }

        Content = new ScrollView { Content = rootPPB.ListenToChanges(SettingInvoker).Build() };
    }
#if WINDOWS
    public static AcceleratorInfo[] GetAccelInfo()
    {
        try
        {
            string accelInfoJson = "";
            Process p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(AppContext.BaseDirectory, "projectFrameCut.Render.WindowsRender.exe"),
                    WorkingDirectory = Path.Combine(AppContext.BaseDirectory),
                    Arguments = $""" list_accels """,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = false,
                }
            };
            p.Start();
            p.WaitForExit();
            accelInfoJson = p.StandardError.ReadToEnd();

            return JsonSerializer.Deserialize<AcceleratorInfo[]>(accelInfoJson);
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
                case "render_SelectRenderHost":
                    {
                        var val = args.Value as string;
                        if (val == SettingLocalizedResources.Render_RenderHost_UseLivePreviewInsteadOfBackend)
                        {
                            WriteSetting("render_UseLivePreviewInsteadOfBackend", "True");
                        }
                        else 
                        {
                            WriteSetting("render_UseLivePreviewInsteadOfBackend", "False");
                        }

                        return;
                    }
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
                        var key = GCOptionMapping.FirstOrDefault(k => k.Value == args.Value as string, new("letCLRDoCollection", "letCLRDoCollection"));
                        WriteSetting("render_GCOption", key.Key);
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