
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
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        
#if WINDOWS
        if (AcceleratorInfos.Length == 0)
        {
            Task t = new(GetAccelInfo);
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
        rootPPB = new()
        {
            WidthOfContent = 3
        };
        rootPPB
            .AddText(new PropertyPanel.TitleAndDescriptionLineLabel(SettingLocalizedResources.Render_AccelOptsTitle, SettingLocalizedResources.Render_AccelOptsSubTitle, 20, 12))
#if WINDOWS
            .AddPicker("accel_DeviceId", SettingLocalizedResources.Render_SelectAccel, accels, int.TryParse(GetSetting("accel_DeviceId", ""), out var result) ? accels[result] : "", null)
            .AddSwitch("accel_enableMultiAccel", SettingLocalizedResources.Render_EnableMultiAccel, bool.TryParse(GetSetting("accel_enableMultiAccel", "false"), out var result1) ? result1 : false, null)
#else
            .AddText(new PropertyPanel.SingleLineLabel(SettingLocalizedResources.Render_AccelOptsNotSupported, 14))
#endif
            .ListenToChanges(SettingInvoker);
        Content = new ScrollView { Content = rootPPB.Build() };
    }
#if WINDOWS
    private void GetAccelInfo()
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

            AcceleratorInfos = JsonSerializer.Deserialize<AcceleratorInfo[]>(accelInfoJson);
        }
        catch (Exception ex)
        {
            Log(ex, "get accel info", this);
        }

    }
#endif

    private async void SettingInvoker(PropertyPanelPropertyChangedEventArgs args)
    {
        try
        {
            switch (args.Id)
            {
                case "accel_DeviceId":
                    if (args.Value is string str)
                    {
                        var idxStr = str.Substring(str.IndexOf('#') + 1, str.IndexOf(':') - str.IndexOf('#') - 1);
                        if(uint.TryParse(idxStr, out var result))
                        {
                            WriteSetting("accel_DeviceId", result.ToString()); 
                        }
                    }
                    goto done;

            }

            if (args.Value != null)
            {
                WriteSetting(args.Id, args.Value?.ToString() ?? "");
            }

            done:
            BuildPPB();
        }
        catch (Exception ex)
        {
            await DisplayAlert(Localized._Warn, Localized._ExceptionTemplate(ex), Localized._OK);
        }
    }

}