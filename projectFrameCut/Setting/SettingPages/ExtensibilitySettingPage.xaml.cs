using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Plugins;

using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationAPIBase.Views.TabbedView;
using projectFrameCut.AIAssistance;
using projectFrameCut.ApplicationPluginBase;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RPCProtocol;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using static projectFrameCut.Setting.SettingManager.SettingsManager;

namespace projectFrameCut.Setting.SettingPages;

public partial class ExtensibilitySettingPage : ContentPage
{
    public PropertyPanelBuilder rootPPB;
    string AdvanceConfigPageViewing = "";
    string selectedTab = "UserPlugins";

    public ExtensibilitySettingPage()
    {
        AdvanceConfigPageViewing = "";
        BuildPPB();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        AdvanceConfigPageViewing = "";
    }

    async void BuildPPB()
    {
        if (!string.IsNullOrWhiteSpace(AdvanceConfigPageViewing))
        {
            await BuildAdvancedConfig(AdvanceConfigPageViewing);
        }
        Title = Localized.MainSettingsPage_Tab_Extensibility;
        rootPPB = BuildUserPlugins();
        var userPlugins = rootPPB.ListenToChanges((e) => SettingInvoker(e, this)).Build();
        var drop = new DropGestureRecognizer { AllowDrop = true };
        drop.Drop += async (_, e) =>
        {
            Dispatcher.Dispatch(() =>
            {
                Content = new VerticalStackLayout
                {
                    Children =
                    {
                        new ActivityIndicator { IsRunning = true },
                        new Label { Text = Localized.LandingPage_Loading }
                    },
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center
                };
            });
            foreach (var item in await FileDropHelper.GetFilePathsFromDrop(e))
            {
                await PluginService.AddAPlugin(item, this);
            }
            BuildPPB();
        };
        userPlugins.GestureRecognizers.Clear();
        userPlugins.GestureRecognizers.Add(drop);

        var tabs = new CompactTabView
        {
            TabItems = new ObservableCollection<TabbedViewItem>
            {
                new() { Header = SettingLocalizedResources.Plugin_ManagePlugins, Tag = "UserPlugins", Content = new ScrollView { Content = userPlugins } },
                new() { Header = SettingLocalizedResources.Plugin_AIProviderPlugin, Tag = "AIProviders", LazyContentFactory = BuildAIProviders().BuildWithScrollView },
                new() { Header = SettingLocalizedResources.ExternalRpc_Clients, Tag = "ExternalRpc", LazyContentFactory = BuildExternalRpcClients().ListenToChanges((e) => SettingInvoker(e, this)).BuildWithScrollView },
                new() { Header = Localized.MainSettingsPage_Tab_Advanced, Tag = "Advanced", LazyContentFactory = BuildPluginAdvanced().ListenToChanges((e) => SettingInvoker(e, this)).BuildWithScrollView }
            }
        };
        tabs.OnTabSwitched += (_, item) => selectedTab = item.Tag;
        tabs.SelectByTag(selectedTab);
        Content = tabs;
    }

    private PropertyPanelBuilder BuildUserPlugins()
    {
        var ppb = new PropertyPanelBuilder();
        foreach (var item in PluginManager.LoadedPlugins.Where(c => !IsInternalPlugin(c.Value)))
        {
            AddPlugin(ppb, item.Key, item.Value);
        }

        var disabledPlugins = PluginService.GetDisabledPlugins();
        if (PluginService.FailedLoadPlugin.Any() || disabledPlugins.Any())
        {
            ppb.AddSeparator()
                .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Plugin_FailLoad, SettingLocalizedResources.Plugin_FailLoad_Subtitle));
            foreach (var plugin in disabledPlugins)
            {
                ppb.AddText(new TitleAndDescriptionLineLabel(plugin.Id, SettingLocalizedResources.Plugin_FailLoad_Disabled))
                    .AddButton($"EnablePlugin,{plugin.Id}", SettingLocalizedResources.Plugin_Enable(plugin.Id));
            }
            foreach (var plugin in PluginService.FailedLoadPlugin)
            {
                ppb.AddText(new TitleAndDescriptionLineLabel(plugin.Key, SettingLocalizedResources.Plugin_FailLoad_FailedBeacuse(plugin.Value)))
                    .AddButton($"RemoveFailedPlugin,{plugin.Key}", SettingLocalizedResources.Plugin_Remove);
            }
        }
        return ppb.AddButton("addButton", SettingLocalizedResources.Plugin_AddOne);
    }

    private PropertyPanelBuilder BuildPluginAdvanced()
    {
        var ppb = new PropertyPanelBuilder();
        ppb.AddText(new SingleLineLabel(SettingLocalizedResources.Plugin_InternalPlugins, 20));
        foreach (var item in PluginManager.LoadedPlugins.Where(c => IsInternalPlugin(c.Value)))
        {
            AddPlugin(ppb, item.Key, item.Value);
        }
        ppb.AddSeparator()
            .AddText(new SingleLineLabel(Localized.MainSettingsPage_Tab_Advanced, 20))
            .AddButton(SettingLocalizedResources.Plugin_ReloadAllButton, async (_, _) =>
            {
                try
                {
                    PluginManager.ForceUnloadAll();
                }
                catch (Exception ex)
                {
                    Log(ex, "unload all");
                }
                try
                {
                    List<IPluginBase> plugins =
                    [
                        new InternalApplicationPluginBase(),
                        new Render.HwAccelEngine.HwAccelEnginePlugin()
                        {
#if ANDROID
                            DefaultComputeBackend = GetSetting("render_AndroidHWAccelType", "vulkan")
#endif
                        },
                        ..MauiProgram.IntegratedPlugins,
                        ..PluginService.LoadUserPlugins(),
                    ];
                    PluginManager.Init(plugins);
                    await DisplayAlertAsync(Localized._Info, SettingLocalizedResources.Advanced_Success, Localized._OK);
                }
                catch (Exception ex)
                {
                    Log(ex, "Load plugins", this);
                }
            })
            .AddCheckbox("DisablePluginEngine", SettingLocalizedResources.Advanced_DisablePluginEngine, IsBoolSettingTrue("DisablePluginEngine"));

        return ppb;
    }

    private static PropertyPanelBuilder BuildAIProviders()
    {
        var ppb = new PropertyPanelBuilder();
        var providers = AIProviderService.Current?.Registry.Providers.Values;
        if (providers is null) return ppb;
        foreach (var item in providers.OrderBy(c => c.Provider.Descriptor.DisplayName))
        {
            var provider = item.Provider.Descriptor;
            ppb.AddSeparator()
                .AddText(new TitleAndDescriptionLineLabel(provider.DisplayName, provider.Description))
                .AddText(new SingleLineLabel($"{item.Key}{Environment.NewLine}{provider.Capabilities}", 12));
        }
        return ppb;
    }

    private static PropertyPanelBuilder BuildExternalRpcClients()
    {
        var ppb = new PropertyPanelBuilder()；
        var clients = ExternalRpcAuthorizationStore.Read(ExternalRpcAuthorizationStore.GetPath(Path.Combine(CLIProgram.AppDataPath, "RpcRequest")))
            .Where(c => !c.Revoked).OrderBy(c => c.AppName).ToArray();
        if (clients.Length == 0)
            ppb.AddText(new SingleLineLabel(SettingLocalizedResources.ExternalRpc_NoClients, 14));
        foreach (var client in clients)
        {
            ppb.AddSeparator()
                .AddText(new TitleAndDescriptionLineLabel(client.AppName, $"{client.Author} ({client.ClientId})"))
                .AddText($"{Localized.VideoCacheManagePage_LastAccessIn}{client.LastUsedAt?.ToLocalTime().ToString("g") ?? "-"}")
                .AddButton($"ExternalRpcRevoke,{client.ClientId}", SettingLocalizedResources.ExternalRpc_Revoke);
        }
        return ppb;
    }

    private static void AddPlugin(PropertyPanelBuilder ppb, string id, IPluginBase plugin)
    {
        var name = plugin.ReadLocalizationItem("_PluginBase_Name_", Localized._LocaleId_) ?? plugin.Name;
        var desc = plugin.ReadLocalizationItem("_PluginBase_Description_", Localized._LocaleId_) ?? plugin.Description;
        var author = plugin.ReadLocalizationItem("_PluginBase_Author_", Localized._LocaleId_) ?? plugin.Author;
        ppb.AddSeparator()
            .AddText(new TitleAndDescriptionLineLabel(name, desc))
            .AddText(new SingleLineLabel(SettingLocalizedResources.Plugin_DetailInfo(author, plugin.Version, plugin.PluginID), 12))
            .AddButton($"MoreOption,{id}", SettingLocalizedResources.Plugin_MoreOption);
    }

    private static bool IsInternalPlugin(IPluginBase plugin) =>
        plugin.Properties.TryGetValue("IsInternalPlugin", out var value) && bool.TryParse(value, out var result) && result;

    private async Task BuildAdvancedConfig(string id)
    {
        if (!PluginManager.LoadedPlugins.TryGetValue(id, out var plugin))
        {
            await Navigation.PopAsync();
            BuildPPB();
            return;
        }
        var page = new ContentPage { };
        var name = plugin.ReadLocalizationItem("_PluginBase_Name_", Localized._LocaleId_) ?? plugin.Name;
        var desc = plugin.ReadLocalizationItem("_PluginBase_Description_", Localized._LocaleId_) ?? plugin.Description;
        var ppb = new PropertyPanelBuilder();

        Dictionary<string, PluginIsolationMode> isolationModes = [];
        if (PluginService.TryGetPluginItem(id, out var pluginItem) && pluginItem!.BackendKind == PluginBackendKind.ManagedAssembly && DesktopPluginIsolationPlatform.IsSupported)
        {
            if (OperatingSystem.IsWindows() && PluginService.SupportsIsolationMode(PluginIsolationMode.Containerized, pluginItem!.MaximumSupportedIsolationMode))
                isolationModes[SettingLocalizedResources.Plugin_IsolationMode_Containerized] = PluginIsolationMode.Containerized;
            if (PluginService.SupportsIsolationMode(PluginIsolationMode.ProcessIsolation, pluginItem.MaximumSupportedIsolationMode))
                isolationModes[SettingLocalizedResources.Plugin_IsolationMode_Process] = PluginIsolationMode.ProcessIsolation;
            if (PluginService.SupportsIsolationMode(PluginIsolationMode.None, pluginItem.MaximumSupportedIsolationMode))
                isolationModes[SettingLocalizedResources.Plugin_IsolationMode_None] = PluginIsolationMode.None;
            var currentMode = PluginService.GetConfiguredIsolationMode(id);
            ppb.AddText(new SingleLineLabel(SettingLocalizedResources.Plugin_DetailConfig(name), 25))
                .AddPicker("PluginIsolationMode", SettingLocalizedResources.Plugin_IsolationMode, isolationModes.Keys.ToArray(), isolationModes.First(c => c.Value == currentMode).Key)
               .AddSeparator()
               .AddText(new SingleLineLabel(SettingLocalizedResources.Plugin_DetailConfig_Subtitle(name), 14));
        }
        else
        {
            ppb.AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Plugin_DetailConfig(name), SettingLocalizedResources.Plugin_DetailConfig_Subtitle(name)));
        }

        if (plugin is IApplicationPluginBase appBase)
        {
            try
            {
                var settingPage = appBase.SettingPageProvider(ref appBase);
                if (settingPage is null)
                {
                    ppb.AddText(new SingleLineLabel(SettingLocalizedResources.Plugin_DetailConfig_None(name), 16, FontAttributes.None, Colors.Gray));
                }
                else
                {
                    ppb.AddCustomChild(settingPage);
                }
            }
            catch (Exception ex)
            {
                Log(ex, $"Create setting page for {name}", this);
                ppb.AddText(new SingleLineLabel($"Failed to create setting page: {Localized._ExceptionTemplate(ex)}", 16, FontAttributes.None, Colors.Red));
            }
        }
        else if (plugin.Configuration.Any())
        {
            foreach (var item in plugin.Configuration)
            {
                ppb.AddEntry($"PluginCfg,{item.Key}",
                    plugin.ConfigurationDisplayString.FirstOrDefault
                        (c => c.Key == Localized._LocaleId_, plugin.ConfigurationDisplayString.First()).Value
                        .FirstOrDefault(c => c.Key == item.Key, new KeyValuePair<string, string>(item.Key, item.Key))
                        .Value,
                    item.Value, item.Value);
            }
        }
        else
        {
            ppb.AddText(new SingleLineLabel(SettingLocalizedResources.Plugin_DetailConfig_None(name), 16, FontAttributes.None, Colors.Gray));
        }

        ppb.AddSeparator()
           .AddText(new SingleLineLabel(Localized.HomePage_ProjectContextMenu(name), 20, FontAttributes.None))
           .AddButton($"ViewProvided,{id}", SettingLocalizedResources.Plugin_ViewWhatProvided(plugin.Name));
        if (IsInternalPlugin(plugin))
        {
            ppb.AddText(new SingleLineLabel(SettingLocalizedResources.Plugin_CannotRemoveInternalPlugin, 14, default, Colors.Grey));
        }
        else
        {
            ppb
              .AddButton($"DisablePlugin,{id}", SettingLocalizedResources.Plugin_Disable(name))
              .AddButton($"GotoHomepage,{id}", SettingLocalizedResources.Plugin_GotoHomepage(name))
              //.AddButton($"UpdatePlugin,{id}", SettingLocalizedResources.Plugin_UpdatePlugin(name)) //todo
              .AddButton($"OpenDataDir,{id}", SettingLocalizedResources.Plugin_OpenDataDir)
              .AddButton($"RemovePlugin,{id}", SettingLocalizedResources.Plugin_Remove);
        }


        ppb.ListenToChanges(async (e) =>
        {
            if (e.Id == "PluginIsolationMode" && e.Value is string selectedMode && isolationModes.TryGetValue(selectedMode, out var mode) && mode != PluginService.GetConfiguredIsolationMode(id))
            {
                if (mode != PluginIsolationMode.Containerized)
                {
                    if (!await page.DisplayAlertAsync(Localized._Warn, SettingLocalizedResources.Plugin_IsolationMode_LowLevelWarn, Localized._Confirm, Localized._Cancel)) return;
                }
                PluginService.SetPluginIsolationMode(id, mode);
                _ = MainSettingsPage.RebootApp(page);
            }
            else if (e.Id.StartsWith("PluginCfg,"))
            {
                var cfgKey = e.Id.Split(',')[1];
                var newCfg = plugin.Configuration.ToDictionary(c => c.Key, c => c.Key == cfgKey ? e.Value?.ToString() ?? "" : c.Value);
                plugin.Configuration = newCfg;
            }
            else
            {
                SettingInvoker(e, page);
            }
        });

        page.Content = new ScrollView { Content = ppb.Build() };

        page.Disappearing += async (s, e) =>
        {
            await SavePluginConfiguration(plugin);
        };

        await Navigation.PushAsync(page);


    }

    private async Task SavePluginConfiguration(IPluginBase plugin)
    {
        try
        {
            var pluginDir = Path.Combine(MauiProgram.BasicDataPath, "Plugins", plugin.PluginID);
            Directory.CreateDirectory(pluginDir);

            var optionFilePath = Path.Combine(pluginDir, "option.json");
            var configJson = JsonSerializer.Serialize(plugin.Configuration);
            await File.WriteAllTextAsync(optionFilePath, configJson);
        }
        catch (Exception ex)
        {
            Log(ex, $"Failed to save plugin configuration for {plugin.PluginID}");
        }
    }

    private async void SettingInvoker(PropertyPanelPropertyChangedEventArgs args, Page? currentPage = null)
    {
        try
        {
            currentPage ??= this;
            if (args.Id.StartsWith("ExternalRpcRevoke,", StringComparison.Ordinal))
            {
                var eid = Guid.Parse(args.Id.Split(',', 2)[1]);
                if (await DisplayAlertAsync(Localized._Warn, SettingLocalizedResources.ExternalRpc_RevokePrompt, Localized._Confirm, Localized._Cancel))
                {
                    ExternalRpcAuthorizationStore.Revoke(ExternalRpcAuthorizationStore.GetPath(Path.Combine(CLIProgram.AppDataPath, "RpcRequest")), eid);
                    Log($"Persistent external RPC client authorization revoked: {eid}.");
                    BuildPPB();
                }
                return;
            }
            if (args.Id == "addButton")
            {
                await DisplayAlertAsync(Localized._Warn, SettingLocalizedResources.Plugin_LoadWarn, Localized._OK);
                var result = await FilePicker.Default.PickAsync(new PickOptions
                {
                    FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                    {
                        { DevicePlatform.WinUI, new[] { ".pjfcPlugin", ".bin" } },
                        { DevicePlatform.Android, new[] { "application/octet-stream", "application/x-msdownload", "application/x-dosexec" } },
#if iDevices
                        {DevicePlatform.iOS, new[] {""} },
                        {DevicePlatform.MacCatalyst, new[] {""} }
#endif
                    }),
                });

                if (result != null)
                {
                    Dispatcher.Dispatch(() =>
                    {
                        Content = new VerticalStackLayout
                        {
                            Children =
                            {
                                new ActivityIndicator
                                {
                                    IsRunning = true,
                                },
                                new Label
                                {
                                    Text = Localized.LandingPage_Loading,
                                }
                            },
                            HorizontalOptions = LayoutOptions.Center,
                            VerticalOptions = LayoutOptions.Center
                        };
                    });
                    await PluginService.AddAPlugin(result.FullPath, this);
                    BuildPPB();
                }
                return;
            }

            if (args.Id == "DisablePluginEngine")
            {
                SettingManager.SettingsManager.WriteSetting("DisablePluginEngine", args.Value?.ToString() ?? "false");
                await MainSettingsPage.RebootApp(this);
                return;
            }

            var flags = args.Id.Split(',');

            var flag = flags[0];
            var id = flags[1];

            if (flag == "RemoveFailedPlugin")
            {
                if (PluginService.FailedLoadPlugin.ContainsKey(id))
                {
                    if (!await DisplayAlertAsync(Localized._Warn, SettingLocalizedResources.Plugin_SureRemove(id), Localized._Confirm, Localized._Cancel))
                    {
                        return;
                    }

                    PluginService.FailedLoadPlugin.Remove(id);

                    try
                    {
                        PluginService.RemovePlugin(id);
                    }
                    catch
                    {
                    }

                    BuildPPB();
                }
                return;
            }
            if (flag == "EnablePlugin")
            {
                PluginService.EnablePlugin(id);
                var p = PluginService.CreateFromID(id, out var fail);
                if (p != null)
                {
                    PluginManager.LoadFrom(p);
                }
                else
                {
                    await DisplayAlertAsync(Localized._Error, fail, Localized._OK);
                }
                BuildPPB();
                return;
            }

            if (!PluginManager.LoadedPlugins.TryGetValue(id, out var plugin))
            {
                await DisplayAlertAsync(Localized._Warn, $"plugin {id} not found", Localized._OK);
                return;
            }

            switch (flag)
            {
                case "ViewProvided":
                    {
                        await DisplayAlertAsync(Localized._Info, PluginMetadata.GetWhatProvided(plugin), Localized._OK);


                        break;
                    }

                case "UpdatePlugin":
                    {
                        //todo
                        break;
                    }

                case "OpenDataDir":
                    {
                        await FileSystemService.OpenFolderAsync(Path.Combine(MauiProgram.BasicDataPath, "Plugins", plugin.PluginID));
                        break;
                    }
                case "DisablePlugin":
                    {
                        PluginService.DisablePlugin(plugin.PluginID);
                        PluginManager.UnloadPlugin(plugin.PluginID);
                        BuildPPB();
                        break;
                    }
                case "GotoHomepage":
                    {
                        if (!string.IsNullOrWhiteSpace(plugin.AuthorUrl)) await Launcher.OpenAsync(plugin.AuthorUrl);
                        break;
                    }

                case "RemovePlugin":
                    {
                        if (await DisplayAlertAsync(Localized._Warn, SettingLocalizedResources.Plugin_SureRemove(plugin.Name), Localized._Confirm, Localized._Cancel))
                        {
                            PluginService.RemovePlugin(plugin.PluginID);
                            PluginManager.UnloadPlugin(plugin.PluginID);
                            await MainSettingsPage.RebootApp(currentPage);
                            //BuildPPB();
                        }
                        break;
                    }
                case "MoreOption":
                    {
                        AdvanceConfigPageViewing = id;
                        await BuildAdvancedConfig(id);
                        break;
                    }


            }

        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(Localized._Warn, Localized._ExceptionTemplate(ex), Localized._OK);
        }
    }
}
