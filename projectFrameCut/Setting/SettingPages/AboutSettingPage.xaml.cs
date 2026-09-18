using Microsoft.Maui.Controls;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Shared;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.Setting.SettingPages;

public partial class AboutSettingPage : ContentPage
{
    private int count = 0;
    public AboutSettingPage()
    {
        InitializeComponent();
        SizeChanged += AboutSettingPage_SizeChanged;
        AppLogoIcon.Source = ImageHelper.LoadFromAsset("projectframecut");
        AppLogoIcon_Narrow.Source = ImageHelper.LoadFromAsset("projectframecut");
        AppRuntimeVersionLabel.Text = $"Runtime: {RuntimeInformation.FrameworkDescription}";
        AppMauiVersionLabel.Text = $"MAUI: Microsoft.Maui.Controls {typeof(View).Assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "11.0.?"}";
        AppVersionLabel.Text = $"Version {Assembly.GetExecutingAssembly()?.GetName()?.Version?.ToString() ?? "Unknown"}";
        AppVersionLabel_Narrow.Text = AppVersionLabel.Text;
        try
        {
            var renderType = typeof(Renderer).Assembly;
            var drawingType = typeof(Drawing.Base.IPicture).Assembly;
            string renderHash = "", drawingHash = "", drawingCommit = "unknown", programDate = "?", channel = "N/A", fullName = AppInfo.PackageName;
            try
            {
#pragma warning disable IL3000 // we have already detected that the assembly is not dynamic, so it's safe to get the location
                renderHash = !renderType.IsDynamic && Path.Exists(renderType.Location) ? HashServices.ComputeFileHash(renderType.Location) : "unknown";
                drawingHash = !drawingType.IsDynamic && Path.Exists(drawingType.Location) ? HashServices.ComputeFileHash(drawingType.Location) : "unknown";
                try
                {
                    var appType = Assembly.GetExecutingAssembly();
                    programDate = !appType.IsDynamic && Path.Exists(appType.Location) ? $"on {File.GetLastWriteTime(appType.Location):yyyy-MM-dd HH:mm:ss}" : "";
                }
                catch
                {
                    programDate = "?";
                }
#pragma warning restore IL3000
                drawingCommit = (drawingType.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.2+unknown commit").Split('+').Last().Substring(0, 8);
#if WINDOWS
                channel = WinUI.Program.ChannelId ?? "NotDef";
                fullName = WinUI.App.GetPackageFullName() ?? "NotDef";
#endif

            }
            catch { renderHash = "unknown"; }

            AppDetailVersionLabel.Text =
                $"""
                Instance ID: {AppInfo.PackageName}
                RPC Protocol: v{RenderProtocol.CurrentVersion} ({RenderProtocol.PipeProtocolVersion}, {RenderProtocol.MinimumSupportedVersion})
                IPluginBase API: v{IPluginBase.CurrentPluginAPIVersion} | IApplicationPluginBase API: v{IApplicationPluginBase.CurrentAppLevelPluginAPIVersion}
                Full package Name: {fullName} | Channel: {channel} | Store: {(MauiProgram.IsStoreMode ? "Yes" : "No")}
                """;
            AppBuildInfoLabel.Text = $"{MauiProgram.AssemblyName}: {MauiProgram.ProgramConfig}@{MauiProgram.ProgramCommit} {programDate}";
            AppDetailVersionLabel_Narrow.Text = AppDetailVersionLabel.Text;
            AppDetailVersionLabel.StyleId = "SelectableLabel";
            AppDetailVersionLabel_Narrow.StyleId = "SelectableLabel";
        }
        catch { }
    }

    private void AboutSettingPage_SizeChanged(object? sender, EventArgs e)
    {
        if (Width > Height)
        {
            WideLayout.IsVisible = true;
            NarrowLayout.IsVisible = false;
        }
        else
        {
            NarrowLayout.IsVisible = true;
            WideLayout.IsVisible = false;
        }
    }

}