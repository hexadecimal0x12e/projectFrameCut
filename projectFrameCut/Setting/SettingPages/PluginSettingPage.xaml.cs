using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Plugins;

using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationPluginBase;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Services;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using static projectFrameCut.Setting.SettingManager.SettingsManager;

namespace projectFrameCut.Setting.SettingPages;

public partial class PluginSettingPage : ContentPage
{
    public PropertyPanelBuilder rootPPB;
    string AdvanceConfigPageViewing = "";

    public PluginSettingPage()
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
        Title = Localized.MainSettingsPage_Tab_Plugin;
        rootPPB = new();
        rootPPB
            .AddText(new SingleLineLabel(SettingLocalizedResources.Plugin_ManagePlugins, 20))
            .AddButton("addButton", SettingLocalizedResources.Plugin_AddOne);

        foreach (var item in PluginManager.LoadedPlugins)
        {
            var plugin = item.Value;
            var name = plugin.ReadLocalizationItem("_PluginBase_Name_", Localized._LocaleId_) ?? plugin.Name;
            var desc = plugin.ReadLocalizationItem("_PluginBase_Description_", Localized._LocaleId_) ?? plugin.Description;
            var author = plugin.ReadLocalizationItem("_PluginBase_Author_", Localized._LocaleId_) ?? plugin.Author;
            rootPPB
                .AddSeparator()
                .AddText(new TitleAndDescriptionLineLabel(name, desc))
                .AddText(new SingleLineLabel(SettingLocalizedResources.Plugin_DetailInfo(author, plugin.Version, plugin.PluginID), 12))
                .AddButton($"MoreOption,{item.Key}", SettingLocalizedResources.Plugin_MoreOption);


        }

        var disabledPlugins = PluginService.GetDisabledPlugins();
        if (PluginService.FailedLoadPlugin.Any() || disabledPlugins.Any())
        {
            rootPPB
                .AddSeparator()
                .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Plugin_FailLoad, SettingLocalizedResources.Plugin_FailLoad_Subtitle));

            foreach (var disabledPlugin in disabledPlugins)
            {
                rootPPB
                    .AddText(new TitleAndDescriptionLineLabel(disabledPlugin.Id, SettingLocalizedResources.Plugin_FailLoad_Disabled))
                    .AddButton($"EnablePlugin,{disabledPlugin.Id}", SettingLocalizedResources.Plugin_Enable(disabledPlugin.Id));
            }
            foreach (var failedPlugin in PluginService.FailedLoadPlugin)
            {
                rootPPB
                    .AddText(new TitleAndDescriptionLineLabel(failedPlugin.Key, SettingLocalizedResources.Plugin_FailLoad_FailedBeacuse(failedPlugin.Value)))
                    .AddButton($"RemoveFailedPlugin,{failedPlugin.Key}", SettingLocalizedResources.Plugin_Remove);
            }
        }


        rootPPB.AddSeparator().AddButton(SettingLocalizedResources.Plugin_ReloadAllButton, async (s, e) =>
        {
            Dictionary<string, string> pems = new();
            foreach (var item in PluginManager.LoadedPlugins)
            {
                var k = await SecureStorage.Default.GetAsync($"plugin_pem_{item.Key}");
                if (!string.IsNullOrEmpty(k)) pems[item.Key] = k;
            }
            try
            {
                PluginManager.ForceUnloadAll();
            }
            catch (Exception ex)
            {
                Log(ex, $"unload all");
            }
            try
            {
                var internalBase = new InternalApplicationPluginBase();
                List<IPluginBase> plugins =
                [
                    internalBase,
#if ANDROID
                    new Render.AndroidOpenGL.Platforms.Android.OpenGLPlugin(),
#elif WINDOWS
                    new projectFrameCut.Render.WindowsRender.ILGPUPlugin(),
#elif iDevices

#endif
                    ..PluginService.LoadUserPlugins((i) => pems.TryGetValue(i, out var p) ? p : throw new KeyNotFoundException()),
                ];


                PluginManager.Init(plugins);
                await DisplayAlertAsync(Localized._Info, SettingLocalizedResources.Advanced_Success, Localized._OK);

            }
            catch (Exception ex)
            {
                Log(ex, "Load plugins", this);
            }

        })
        .AddSwitch("DisablePluginEngine", SettingLocalizedResources.Advanced_DisablePluginEngine, IsBoolSettingTrue("DisablePluginEngine"));

        var scv = rootPPB.AddSeparator().ListenToChanges((e) => SettingInvoker(e, this)).Build();
        DropGestureRecognizer drop = new();
        drop.AllowDrop = true;
        drop.Drop += async (s, e) =>
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
            foreach (var item in await FileDropHelper.GetFilePathsFromDrop(e))
            {
                await PluginService.AddAPlugin(item, this);
            }
            BuildPPB();
        };
        scv.GestureRecognizers.Add(drop);
        Content = new ScrollView { Content = scv };


    }

    private async Task BuildAdvancedConfig(string id)
    {
        if(!PluginManager.LoadedPlugins.TryGetValue(id, out var plugin))
        {
            await Navigation.PopAsync();
            BuildPPB();
            return;
        }
        var page = new ContentPage { };
        var name = plugin.ReadLocalizationItem("_PluginBase_Name_", Localized._LocaleId_) ?? plugin.Name;
        var desc = plugin.ReadLocalizationItem("_PluginBase_Description_", Localized._LocaleId_) ?? plugin.Description;
        var ppb = new PropertyPanelBuilder()
            .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Plugin_DetailConfig(name), SettingLocalizedResources.Plugin_DetailConfig_Subtitle(name)));

        if (plugin is IApplicationPluginBase appBase)
        {
            try
            {
                var settingPage = appBase.SettingPageProvider(ref appBase);
                if (settingPage is null)
                {
                    ppb.AddText(new SingleLineLabel(SettingLocalizedResources.Plugin_DetailConfig_None(name), 16, FontAttributes.None, Colors.Gray));
                }
                ppb.AddCustomChild(settingPage);
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

        ppb.AddText(new SingleLineLabel(Localized.HomePage_ProjectContextMenu(name), 20, FontAttributes.None))
            .AddButton($"ViewProvided,{id}", SettingLocalizedResources.Plugin_ViewWhatProvided(plugin.Name));
        if (plugin.Properties.TryGetValue("IsInternalPlugin", out var isInternal) && bool.TryParse(isInternal, out var result) && result)
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


        ppb.ListenToChanges((e) =>
        {
            if (e.Id.StartsWith("PluginCfg,"))
            {
                var cfgKey = e.Id.Split(',')[1];
                plugin.Configuration[cfgKey] = e.Value?.ToString() ?? "";
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
            if (args.Id == "addButton")
            {
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

            if(args.Id == "DisablePluginEngine")
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
                var pem = await SecureStorage.Default.GetAsync($"plugin_pem_{id}");
                PluginService.EnablePlugin(id);
                var p = PluginService.CreateFromID(id, out var fail, pem);
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