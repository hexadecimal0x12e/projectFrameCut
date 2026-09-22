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
using System.Runtime.InteropServices;
#endif

namespace projectFrameCut.Setting.SettingPages
{
    public class SecuritySettingPage : ContentPage
    {
#if WINDOWS
        private const string PluginInternetSetting = "Security_IsolatedPlugins_AllowInternet";
        private const string PluginServerSetting = "Security_IsolatedPlugins_AllowServer";
        private const string PluginLoopbackSetting = "Security_IsolatedPlugins_AllowLoopback";
        private static readonly SemaphoreSlim FirewallLock = new(1, 1);
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
                .AddCheckbox(PluginInternetSetting, SettingLocalizedResources.Security_IsolatedPlugins_AllowInternet, IsIsolatedPluginNetworkAccessEnabled(PluginInternetSetting))
                .AddCheckbox(PluginServerSetting, SettingLocalizedResources.Security_IsolatedPlugins_AllowServer, IsIsolatedPluginNetworkAccessEnabled(PluginServerSetting))
                .AddCheckbox(PluginLoopbackSetting, SettingLocalizedResources.Security_IsolatedPlugins_AllowLoopback, IsIsolatedPluginLoopbackAccessEnabled())
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
                    if (args.Id is PluginInternetSetting or PluginServerSetting or PluginLoopbackSetting)
                    {
                        await FirewallLock.WaitAsync();
                        try
                        {
                            SetNetworkPolicyBusy(true);
                            await Task.Delay(50);
                            var enabled = Convert.ToBoolean(args.Value);
                            await Task.Run(() => SetIsolatedPluginNetworkPolicyElevated(args.Id, enabled));
                            Log($"Set isolated plugin network policy '{args.Id}' to '{enabled}'.");
                        }
                        finally
                        {
                            SetNetworkPolicyBusy(false);
                            FirewallLock.Release();
                        }
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

        private static void SetIsolatedPluginNetworkPolicyElevated(string setting, bool enabled)
        {
            var target = setting switch
            {
                PluginInternetSetting => "internet",
                PluginServerSetting => "server",
                PluginLoopbackSetting => "loopback",
                _ => throw new ArgumentOutOfRangeException(nameof(setting)),
            };
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
                    $"--target={target}",
                    $"--enabled={enabled}",
                    $"--packageFamilyName={WindowsPluginIsolationPlatform.PackageFamilyName}",
                    "--forceRouteToCLI",
                },
            }) ?? throw new InvalidOperationException("Failed to start the elevated projectFrameCut CLI.");
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"The elevated projectFrameCut CLI failed with exit code {process.ExitCode}.");

            var applied = setting == PluginLoopbackSetting
                ? GetIsolatedPluginLoopbackAccessEnabled(WindowsPluginIsolationPlatform.AppContainerSid.Value)
                : IsIsolatedPluginNetworkAccessEnabled(setting);
            if (applied != enabled)
                throw new InvalidOperationException("The elevated projectFrameCut CLI did not apply the requested network policy.");
        }

        internal static void ConfigureIsolatedPluginNetworkPolicy(string target, bool enabled, string packageFamilyName)
        {
            if (string.IsNullOrWhiteSpace(packageFamilyName))
                throw new ArgumentException("Package family name is required.", nameof(packageFamilyName));
            var sid = WindowsPluginIsolationPlatform.GetAppContainerSid(packageFamilyName).Value;
            switch (target.ToLowerInvariant())
            {
                case "internet":
                    SetFirewallRule(PluginInternetSetting, enabled, packageFamilyName, sid);
                    break;
                case "server":
                    SetFirewallRule(PluginServerSetting, enabled, packageFamilyName, sid);
                    break;
                case "loopback":
                    SetIsolatedPluginLoopbackAccess(enabled, packageFamilyName, sid);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(target), "Target must be internet, server, or loopback.");
            }
        }

        private static void SetFirewallRule(string setting, bool enabled, string packageFamilyName, string appContainerSid)
        {
            var inbound = setting == PluginServerSetting;
            var ruleName = GetFirewallRuleName(inbound, packageFamilyName);
            object? policy = null;
            object? rules = null;
            object? rule = null;
            try
            {
                policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!);
                rules = ((dynamic)policy!).Rules;
                try
                {
                    ((dynamic)rules).Remove(ruleName);
                }
                catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002))
                {
                }

                if (enabled) return;

                rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!);
                dynamic r = rule!;
                r.Name = ruleName;
                r.Description = inbound
                    ? "Blocks inbound network connections to isolated projectFrameCut plugins."
                    : "Blocks outbound network connections from isolated projectFrameCut plugins.";
                r.Grouping = "projectFrameCut";
                r.Direction = inbound ? 1 : 2;
                r.Protocol = 256;
                r.Profiles = int.MaxValue;
                r.InterfaceTypes = "All";
                r.Action = 0;
                r.LocalAppPackageId = appContainerSid;
                r.Enabled = true;
                ((dynamic)rules).Add(r);
            }
            finally
            {
                if (rule is not null && Marshal.IsComObject(rule)) Marshal.FinalReleaseComObject(rule);
                if (rules is not null && Marshal.IsComObject(rules)) Marshal.FinalReleaseComObject(rules);
                if (policy is not null && Marshal.IsComObject(policy)) Marshal.FinalReleaseComObject(policy);
            }
        }

        private static bool IsIsolatedPluginNetworkAccessEnabled(string setting)
        {
            object? policy = null;
            object? rules = null;
            object? rule = null;
            try
            {
                var inbound = setting == PluginServerSetting;
                var packageFamilyName = WindowsPluginIsolationPlatform.PackageFamilyName;
                policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!);
                rules = ((dynamic)policy!).Rules;
                rule = ((dynamic)rules).Item(GetFirewallRuleName(inbound, packageFamilyName));
                dynamic r = rule;
                return !(r.Enabled && r.Action == 0 && r.Direction == (inbound ? 1 : 2));
            }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002))
            {
                return true;
            }
            catch (Exception ex)
            {
                Log(ex, "read the isolated plugin firewall policy", typeof(SecuritySettingPage));
                return true;
            }
            finally
            {
                if (rule is not null && Marshal.IsComObject(rule)) Marshal.FinalReleaseComObject(rule);
                if (rules is not null && Marshal.IsComObject(rules)) Marshal.FinalReleaseComObject(rules);
                if (policy is not null && Marshal.IsComObject(policy)) Marshal.FinalReleaseComObject(policy);
            }
        }

        private static string GetFirewallRuleName(bool inbound, string packageFamilyName) =>
            $"projectFrameCut isolated plugins - {(inbound ? "inbound" : "outbound")} - {packageFamilyName}";

        private static bool IsIsolatedPluginLoopbackAccessEnabled()
        {
            try
            {
                return GetIsolatedPluginLoopbackAccessEnabled(WindowsPluginIsolationPlatform.AppContainerSid.Value);
            }
            catch (Exception ex)
            {
                Log(ex, "read the isolated plugin loopback exemption", typeof(SecuritySettingPage));
                return false;
            }
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
