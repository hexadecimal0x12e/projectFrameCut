using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text;
using projectFrameCut.Setting;
using static projectFrameCut.Setting.SettingManager.SettingsManager;
#if WINDOWS
using projectFrameCut.Platforms.Windows;
using System.Diagnostics;
using static projectFrameCut.Platforms.Windows.WindowsPluginIsolationPlatform;
#endif

namespace projectFrameCut.Setting.SettingPages
{
    public class SecuritySettingPage : ContentPage
    {
#if WINDOWS
        private static readonly SemaphoreSlim NetworkPolicyLock = new(1, 1);
        private ScrollView? settingsContent;
        private ActivityIndicator? networkPolicyBusyIndicator;
#endif

        public SecuritySettingPage()
        {
            Title = Localized.MainSettingsPage_Tab_Security;
            BuildPPB();

        }
        public PropertyPanelBuilder? rootPPB;

        public void BuildPPB()
        {
            Content = new VerticalStackLayout();
            rootPPB = new();
            rootPPB
                //.AddText(new SingleLineLabel(SettingLocalizedResources.Security_General, 25))
                // not applicable for the oss branch

                .AddText(new SingleLineLabel(SettingLocalizedResources.Security_RemoteContent, 25))
                .AddCheckbox("Security_RemoteContent_EnableHttpDecoder", SettingLocalizedResources.Security_RemoteContent_EnableHttpDecoder, IsBoolSettingTrueOrDefault("Security_RemoteContent_EnableHttpDecoder", true))
                .AddCheckbox("Security_RemoteContent_EnableRemoteContent", SettingLocalizedResources.Security_RemoteContent_EnableRemoteContent, IsBoolSettingTrueOrDefault("Security_RemoteContent_EnableRemoteContent", true))
                .AddSeparator()
#if WINDOWS
                .AddText(new SingleLineLabel(SettingLocalizedResources.Security_IsolatedPlugins, 25))
                .AddCheckbox(PluginInternetSetting, SettingLocalizedResources.Security_IsolatedPlugins_AllowInternet, IsBoolSettingTrueOrDefault(PluginInternetSetting, true))
                .AddCheckbox(PluginServerSetting, SettingLocalizedResources.Security_IsolatedPlugins_AllowServer, IsBoolSettingTrueOrDefault(PluginInternetSetting, true) && IsBoolSettingTrueOrDefault(PluginServerSetting, true))
                .AddCheckbox(PluginLoopbackSetting, SettingLocalizedResources.Security_IsolatedPlugins_AllowLoopback, IsBoolSettingTrueOrDefault(PluginLoopbackSetting, false))
                .AddSeparator()
#endif
                .AddText(new SingleLineLabel(SettingLocalizedResources.Security_AICapabilities, 25))
                .AddCheckbox("Security_AICapabilities_AllowToolCall", SettingLocalizedResources.Security_AICapabilities_AllowToolCall, IsBoolSettingTrueOrDefault("Security_AICapabilities_AllowToolCall", true))
                .AddCheckbox("Security_AICapabilities_AllowModifyProject", SettingLocalizedResources.Security_AICapabilities_AllowModifyProject, IsBoolSettingTrueOrDefault("Security_AICapabilities_AllowModifyProject", true))
                .AddSeparator()

                .AddText(new SingleLineLabel(SettingLocalizedResources.Security_RichText, 25))
                .AddCheckbox("Security_RichText_EnableRendering", SettingLocalizedResources.Security_RichText_EnableRendering, IsBoolSettingTrueOrDefault("Security_RichText_EnableRendering", true))
                .AddCheckbox("Security_RichText_EnableDisplayingImage", SettingLocalizedResources.Security_RichText_EnableDisplayingImage, IsBoolSettingTrueOrDefault("Security_RichText_EnableDisplayingImage", true))
                .AddCheckbox("Security_RichText_EnableDisplayingHtml", SettingLocalizedResources.Security_RichText_EnableDisplayingHtml, IsBoolSettingTrueOrDefault("Security_RichText_EnableDisplayingHtml", true))
                .AddCheckbox("Security_RichText_EnableDisplayingXAML", SettingLocalizedResources.Security_RichText_EnableDisplayingXAML, IsBoolSettingTrueOrDefault("Security_RichText_EnableDisplayingXAML", true))
                .AddCheckbox("Security_RichText_EnableXAMLExternalSource", SettingLocalizedResources.Security_RichText_EnableXAMLExternalSource, IsBoolSettingTrueOrDefault("Security_RichText_EnableXAMLExternalSource", false));

            rootPPB.ListenToChanges(SettingInvoker);
            var content = rootPPB.BuildWithScrollView();
#if WINDOWS
            settingsContent = content;
            networkPolicyBusyIndicator = new ActivityIndicator
            {
                IsRunning = false,
                IsVisible = false,
                WidthRequest = 48,
                HeightRequest = 48,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            };
            Content = new Grid
            {
                Children =
                {
                    content,
                    networkPolicyBusyIndicator,
                },
            };
#else
            Content = content;
#endif
        }

        private async void SettingInvoker(PropertyPanelPropertyChangedEventArgs args)
        {
            try
            {
                if (args.Value != null)
                {
#if WINDOWS
                    if (args.Id is PluginInternetSetting or PluginServerSetting)
                    {
                        var enabled = Convert.ToBoolean(args.Value);
                        WriteSetting(args.Id, enabled.ToString());
                        if (args.Id == PluginInternetSetting && !enabled)
                            WriteSetting(PluginServerSetting, bool.FalseString);
                        else if (args.Id == PluginServerSetting && enabled)
                            WriteSetting(PluginInternetSetting, bool.TrueString);
                        Log($"Saved isolated plugin capability setting '{args.Id}' to '{enabled}'; restart required.");
                        BuildPPB();
                        await MainSettingsPage.RebootApp(this);
                        return;
                    }
                    if (args.Id == PluginLoopbackSetting)
                    {
                        await NetworkPolicyLock.WaitAsync();
                        try
                        {
                            SetNetworkPolicyBusy(true);
                            await Task.Delay(50);
                            var enabled = Convert.ToBoolean(args.Value);
                            await Task.Run(() => SetIsolatedPluginLoopbackAccessElevated(enabled));
                            WriteSetting(PluginLoopbackSetting, enabled.ToString());
                            Log($"Set isolated plugin loopback access to '{enabled}'; restart required to update capabilities.");
                        }
                        finally
                        {
                            SetNetworkPolicyBusy(false);
                            NetworkPolicyLock.Release();
                        }
                        await MainSettingsPage.RebootApp(this);
                        return;
                    }
#endif
                    WriteSetting(args.Id, args.Value?.ToString() ?? "");
                }

            }
            catch (Exception ex)
            {
                Log(ex, "update a security setting", this);
                BuildPPB();
                await DisplayAlertAsync(Localized._Warn, Localized._ExceptionTemplate(ex), Localized._OK);
            }
        }

#if WINDOWS
        private void SetNetworkPolicyBusy(bool busy)
        {
            if (settingsContent is not null) settingsContent.IsEnabled = !busy;
            if (networkPolicyBusyIndicator is null) return;
            networkPolicyBusyIndicator.IsVisible = busy;
            networkPolicyBusyIndicator.IsRunning = busy;
        }

        private static void SetIsolatedPluginLoopbackAccessElevated(bool enabled)
        {
            WindowsPluginIsolationPlatform.EnsureSandyProfile(WindowsPluginIsolationPlatform.PackageFamilyName);
            var executable = CliProcessLauncher.GetExecutableCandidates().FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException("The projectFrameCut CLI entry point was not found.");
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppContext.BaseDirectory,
                ArgumentList =
                {
                    "plugin_network_policy",
                    "--target=loopback",
                    $"--enabled={enabled}",
                    $"--packageFamilyName={WindowsPluginIsolationPlatform.PackageFamilyName}",
                    "--forceRouteToCLI",
                },
            }) ?? throw new InvalidOperationException("Failed to start the elevated projectFrameCut CLI.");
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"The elevated projectFrameCut CLI failed with exit code {process.ExitCode}.");
            if (GetIsolatedPluginLoopbackAccessEnabled(WindowsPluginIsolationPlatform.AppContainerSid.Value) != enabled)
                throw new InvalidOperationException("The elevated projectFrameCut CLI did not apply the requested loopback exemption.");
        }

        internal static void ConfigureIsolatedPluginNetworkPolicy(string target, bool enabled, string packageFamilyName)
        {
            if (!string.Equals(target, "loopback", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentOutOfRangeException(nameof(target), "Target must be loopback.");
            if (string.IsNullOrWhiteSpace(packageFamilyName))
                throw new ArgumentException("Package family name is required.", nameof(packageFamilyName));
            SetIsolatedPluginLoopbackAccess(enabled,
                WindowsPluginIsolationPlatform.GetContainerName(packageFamilyName),
                WindowsPluginIsolationPlatform.GetAppContainerSid(packageFamilyName).Value);
        }

        private static bool GetIsolatedPluginLoopbackAccessEnabled(string appContainerSid)
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "CheckNetIsolation.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "LoopbackExempt", "-s" },
            }) ?? throw new InvalidOperationException("Failed to start CheckNetIsolation.exe.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WhenAll(output, error).GetAwaiter().GetResult();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"CheckNetIsolation.exe failed with exit code {process.ExitCode}: {error.Result}");
            return output.Result.Contains(appContainerSid, StringComparison.OrdinalIgnoreCase);
        }

        private static void SetIsolatedPluginLoopbackAccess(bool enabled, string packageFamilyName, string appContainerSid)
        {
            if (GetIsolatedPluginLoopbackAccessEnabled(appContainerSid) == enabled) return;
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "CheckNetIsolation.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    "LoopbackExempt",
                    enabled ? "-a" : "-d",
                    $"-n={packageFamilyName}",
                },
            }) ?? throw new InvalidOperationException("Failed to start CheckNetIsolation.exe.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WhenAll(output, error).GetAwaiter().GetResult();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"CheckNetIsolation.exe failed with exit code {process.ExitCode}: {error.Result}{output.Result}");
            if (GetIsolatedPluginLoopbackAccessEnabled(appContainerSid) == enabled) return;
            throw new InvalidOperationException("CheckNetIsolation.exe did not apply the requested loopback exemption.");
        }
#endif
    }
}
