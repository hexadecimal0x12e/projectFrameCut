using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.Contracts;
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
        private bool showAllRpcPrincipals;
        private const string RpcPipeSidPrefix = "ExternalRpcPipeSid:";
        private const string RpcPipeSingleConnection = "Security_ExternalRpcPipe_SingleConnection";
#endif

        public SecuritySettingPage()
        {
            Title = Localized.MainSettingsPage_Tab_Security;
            BuildPPB();

        }
        public PropertyPanelBuilder? rootPPB;

        public void BuildPPB()
        {
            var rpcPipes = ExternalRpcPipeSettings.Load(MauiProgram.BasicDataPath);
            var externalSources = ExternalSourceSettings.Load(MauiProgram.BasicDataPath);
            Content = new VerticalStackLayout();
            rootPPB = new();
            rootPPB
                //.AddText(new SingleLineLabel(SettingLocalizedResources.Security_General, 25))
                // not applicable for the oss branch

                .AddText(new SingleLineLabel(SettingLocalizedResources.Security_RemoteContent, 25))
                .AddCheckbox("Security_RemoteContent_EnableHttpDecoder", SettingLocalizedResources.Security_RemoteContent_EnableHttpDecoder, IsBoolSettingTrueOrDefault("Security_RemoteContent_EnableHttpDecoder", true))
                .AddCheckbox("Security_RemoteContent_EnableRemoteContent", SettingLocalizedResources.Security_RemoteContent_EnableRemoteContent, IsBoolSettingTrueOrDefault("Security_RemoteContent_EnableRemoteContent", true))
                .AddSeparator()
                .AddText(new SingleLineLabel(SettingLocalizedResources.Security_ExternalSources, 25))
                .AddCheckbox(ExternalSourceSettings.AllowRpcSourcesSetting, SettingLocalizedResources.Security_ExternalSources_AllowRpcSources, externalSources.AllowRpcSources)
                .AddCheckbox(ExternalSourceSettings.AllowAssemblySourcesSetting, SettingLocalizedResources.Security_ExternalSources_AllowAssemblySources, externalSources.AllowAssemblySources)
                .AddSeparator()
                .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Security_ExternalRpcPipe, SettingLocalizedResources.Security_ExternalRpcPipe_AllowExternalRPC_Description))
                .AddCheckbox(ExternalRpcPipeSettings.AllowExternalRpcSetting, new SingleLineLabel(SettingLocalizedResources.Security_ExternalRpcPipe_AllowExternalRPC), rpcPipes.AllowExternalRPC)
#if WINDOWS
                .AppendWhen(rpcPipes.AllowExternalRPC, ppb => ppb
                    .AddCheckbox(RpcPipeSingleConnection, SettingLocalizedResources.Security_ExternalRpcPipe_SingleConnection, ExternalRpcPipePermissions.LoadSingleConnection(MauiProgram.BasicDataPath))
                    .AppendWhen(!rpcPipes.AllowAnyUser, d => d.AddCustomChild(BuildExternalRpcPipePermissions()))
                    .AddCheckbox(ExternalRpcPipeSettings.AllowAnyUserSetting, SettingLocalizedResources.Security_ExternalRpcPipe_AllowAnyUser, rpcPipes.AllowAnyUser))
#endif
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
                    if (args.Id is ExternalSourceSettings.AllowRpcSourcesSetting or ExternalSourceSettings.AllowAssemblySourcesSetting)
                    {
                        var configuration = ExternalSourceSettings.Load(MauiProgram.BasicDataPath);
                        if (args.Id == ExternalSourceSettings.AllowRpcSourcesSetting) configuration.AllowRpcSources = Convert.ToBoolean(args.Value);
                        else configuration.AllowAssemblySources = Convert.ToBoolean(args.Value);
                        configuration.Save(MauiProgram.BasicDataPath);
                        Log($"Set external source security setting '{args.Id}' to '{args.Value}'.");
                        return;
                    }
                    if (args.Id == ExternalRpcPipeSettings.AllowExternalRpcSetting)
                    {
                        var configuration = ExternalRpcPipeSettings.Load(MauiProgram.BasicDataPath);
                        configuration.AllowExternalRPC = Convert.ToBoolean(args.Value);
                        configuration.Save(MauiProgram.BasicDataPath);
                        Log($"Set external RPC client availability to '{configuration.AllowExternalRPC}'.");
                        BuildPPB();
                        return;
                    }
#if WINDOWS
                    if (args.Id == ExternalRpcPipeSettings.AllowAnyUserSetting)
                    {
                        if (!await DisplayAlertAsync(Localized._Warn, SettingLocalizedResources.Security_ExternalRpcPipe_AllowAnyUser_Warning, Localized._OK, Localized._Cancel))
                        {
                            BuildPPB();
                            return;
                        }
                        var configuration = ExternalRpcPipeSettings.Load(MauiProgram.BasicDataPath);
                        configuration.AllowAnyUser = Convert.ToBoolean(args.Value);
                        configuration.Save(MauiProgram.BasicDataPath);
                        Log($"Set external RPC pipes to allow any user and integrity level: {configuration.AllowAnyUser}.");
                        return;
                    }
                    if (args.Id == RpcPipeSingleConnection)
                    {
                        var enabled = Convert.ToBoolean(args.Value);
                        ExternalRpcPipePermissions.SaveSingleConnection(MauiProgram.BasicDataPath, enabled);
                        Log($"Set external RPC pipes to single-connection mode: {enabled}.");
                        return;
                    }
                    if (args.Id.StartsWith(RpcPipeSidPrefix, StringComparison.Ordinal))
                    {
                        var sid = args.Id[RpcPipeSidPrefix.Length..];
                        var allowed = ExternalRpcPipePermissions.LoadAllowedSids(MauiProgram.BasicDataPath);
                        var enabled = Convert.ToBoolean(args.Value);
                        if (enabled) allowed.Add(sid);
                        else allowed.Remove(sid);
                        ExternalRpcPipePermissions.SaveAllowedSids(MauiProgram.BasicDataPath, allowed);
                        Log($"Updated external RPC pipe access for SID '{sid}' to '{enabled}'.");
                        return;
                    }
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
        private View BuildExternalRpcPipePermissions()
        {
            var accounts = new VerticalStackLayout();
            var refresh = new Button { Text = SettingLocalizedResources._Refresh };
            var showAll = new Button();
            var add = new Button { Text = SettingLocalizedResources.Security_ExternalRpcPipe_AddPrincipal };
            showAll.Clicked += async (_, _) =>
            {
                showAllRpcPrincipals = !showAllRpcPrincipals;
                await LoadExternalRpcPrincipalsAsync(accounts, refresh, showAll, add);
            };
            refresh.Clicked += async (_, _) => await LoadExternalRpcPrincipalsAsync(accounts, refresh, showAll, add);
            add.Clicked += async (_, _) =>
            {
                refresh.IsEnabled = showAll.IsEnabled = add.IsEnabled = false;
                try
                {
                    var input = await DisplayPromptAsync(SettingLocalizedResources.Security_ExternalRpcPipe_AddPrincipal,
                        SettingLocalizedResources.Security_ExternalRpcPipe_AddPrincipal_Prompt, Localized._OK, Localized._Cancel);
                    if (string.IsNullOrWhiteSpace(input)) return;
                    var principal = await Task.Run(() => ExternalRpcPipePermissions.ResolvePrincipal(input));
                    ExternalRpcPipePermissions.AddPrincipal(MauiProgram.BasicDataPath, principal);
                    Log($"Added external RPC pipe access for '{principal.Name}' ({principal.Sid}).");
                    await LoadExternalRpcPrincipalsAsync(accounts, refresh, showAll, add);
                }
                catch (Exception ex)
                {
                    Log(ex, "add an external RPC pipe principal", this);
                    await DisplayAlertAsync(Localized._Warn, Localized._ExceptionTemplate(ex), Localized._OK);
                }
                finally { refresh.IsEnabled = showAll.IsEnabled = add.IsEnabled = true; }
            };
            _ = LoadExternalRpcPrincipalsAsync(accounts, refresh, showAll, add);
            return new VerticalStackLayout
            {
                Spacing = 8,
                VerticalOptions = LayoutOptions.Start,
                Children = { refresh, accounts, showAll, add }
            };
        }

        private async Task LoadExternalRpcPrincipalsAsync(VerticalStackLayout accounts, Button refresh, Button showAll, Button add)
        {
            refresh.IsEnabled = showAll.IsEnabled = add.IsEnabled = false;
            showAll.Text = showAllRpcPrincipals ? SettingLocalizedResources.Security_ExternalRpcPipe_ShowCommon : SettingLocalizedResources.Security_ExternalRpcPipe_ShowAll;
            accounts.Children.Clear();
            accounts.Children.Add(new ActivityIndicator { IsRunning = true });
            try
            {
                var principals = await Task.Run(ExternalRpcPipePermissions.EnumerateLocalPrincipals);
                var configuration = ExternalRpcPipeSettings.Load(MauiProgram.BasicDataPath);
                var allowed = ExternalRpcPipePermissions.LoadAllowedSids(MauiProgram.BasicDataPath);
                var ppb = new PropertyPanelBuilder { WidthOfContent = 0.1 };
                var visible = principals.Where(p =>
                {
                    if (showAllRpcPrincipals || configuration.ManualPrincipals.ContainsKey(p.Sid)) return true;
                    if (p.IsGroup)
                        return !p.Sid.StartsWith("S-1-5-32-", StringComparison.Ordinal) || p.Sid is "S-1-5-32-544" or "S-1-5-32-545";
                    // Guest, krbtgt, DefaultAccount, WDAGUtilityAccount.
                    return p.Sid[(p.Sid.LastIndexOf('-') + 1)..] is not ("501" or "502" or "503" or "504");
                });
                foreach (var group in visible.GroupBy(p => p.IsGroup))
                {
                    ppb.AddText(group.Key ? SettingLocalizedResources.Security_ExternalRpcPipe_Groups : SettingLocalizedResources.Security_ExternalRpcPipe_Users, fontSize: 18);
                    foreach (var p in group)
                        ppb.AddCheckbox(RpcPipeSidPrefix + p.Sid, new TitleAndDescriptionLineLabel(p.Name, p.Sid, 14, 12), allowed.Contains(p.Sid))
                            .AddSeparator(b => b.Opacity = 0.25);
                }
                var missing = allowed.Union(configuration.ManualPrincipals.Keys, StringComparer.OrdinalIgnoreCase)
                    .Except(principals.Select(p => p.Sid), StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
                if (missing.Length > 0)
                {
                    ppb.AddText(SettingLocalizedResources.Security_ExternalRpcPipe_OtherSids, fontSize: 18);
                    foreach (var sid in missing)
                    {
                        var name = configuration.ManualPrincipals.GetValueOrDefault(sid);
                        if (string.IsNullOrWhiteSpace(name) || name == sid) name = "<unknown user>";
                        ppb.AddCheckbox(RpcPipeSidPrefix + sid, new TitleAndDescriptionLineLabel(name, sid, 14, 12), allowed.Contains(sid));
                        ppb.AddSeparator(b => b.Opacity = 0.25);
                    }
                }
                ppb.ListenToChanges(SettingInvoker);
                accounts.Children.Clear();
                accounts.Children.Add(ppb.Build());
            }
            catch (Exception ex)
            {
                Log(ex, "enumerate external RPC pipe accounts", this);
                accounts.Children.Clear();
                accounts.Children.Add(new Label { Text = Localized._ExceptionTemplate(ex) });
            }
            finally
            {
                refresh.IsEnabled = showAll.IsEnabled = add.IsEnabled = true;
            }
        }

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
