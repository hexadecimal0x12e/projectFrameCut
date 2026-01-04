using projectFrameCut.PropertyPanel;
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
    public PropertyPanel.PropertyPanelBuilder rootPPB;
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
            .AddText(new PropertyPanel.SingleLineLabel(SettingLocalizedResources.Plugin_ManagePlugins, 20))
            .AddButton("addButton", SettingLocalizedResources.Plugin_AddOne);

        foreach (var item in PluginManager.LoadedPlugins)
        {
            var plugin = item.Value;
            var name = plugin.ReadLocalizationItem("_PluginBase_Name_", plugin.Name) ?? plugin.Name;
            var desc = plugin.ReadLocalizationItem("_PluginBase_Description_", plugin.Description) ?? plugin.Description;
            rootPPB
                .AddSeparator()
                .AddText(new PropertyPanel.TitleAndDescriptionLineLabel(name, desc, 20, 16))
                .AddText(new PropertyPanel.SingleLineLabel(SettingLocalizedResources.Plugin_DetailInfo(plugin.Author, plugin.Version, plugin.PluginID), 12))
                .AddButton($"MoreOption,{item.Key}", SettingLocalizedResources.Plugin_MoreOption);


        }

        var disabledPlugins = PluginService.GetDisabledPlugins();
        if (PluginService.FailedLoadPlugin.Any() || disabledPlugins.Any())
        {
            rootPPB
                .AddSeparator()
                .AddText(new PropertyPanel.TitleAndDescriptionLineLabel(SettingLocalizedResources.Plugin_FailLoad, SettingLocalizedResources.Plugin_FailLoad_Subtitle, 20, 12));

            foreach (var disabledPlugin in disabledPlugins)
            {
                rootPPB
                    .AddText(new PropertyPanel.TitleAndDescriptionLineLabel(disabledPlugin.Id, SettingLocalizedResources.Plugin_FailLoad_Disabled, 20, 16))
                    .AddButton($"EnablePlugin,{disabledPlugin.Id}", SettingLocalizedResources.Plugin_Enable(disabledPlugin.Id));
            }
            foreach (var failedPlugin in PluginService.FailedLoadPlugin)
            {
                rootPPB
                    .AddText(new PropertyPanel.TitleAndDescriptionLineLabel(failedPlugin.Key, SettingLocalizedResources.Plugin_FailLoad_FailedBeacuse(failedPlugin.Value), 20, 16))
                    .AddButton($"RemoveFailedPlugin,{failedPlugin.Key}", SettingLocalizedResources.Plugin_Remove);
            }
        }

        var scv = rootPPB.AddSeparator().ListenToChanges((e) => SettingInvoker(e, this)).BuildWithScrollView();
        DropGestureRecognizer drop = new();
        drop.AllowDrop = true;
        drop.Drop += async (s, e) =>
        {
            foreach (var item in await FileDropHelper.GetFilePathsFromDrop(e))
            {
                await PluginService.AddAPlugin(item, this);
            }
        };
        scv.GestureRecognizers.Add(drop);
        Content = scv;


    }

    private async Task BuildAdvancedConfig(string id)
    {
        var plugin = PluginManager.LoadedPlugins[id];
        var page = new ContentPage { };
        var name = plugin.ReadLocalizationItem("_PluginBase_Name_", plugin.Name) ?? plugin.Name;
        var desc = plugin.ReadLocalizationItem("_PluginBase_Description_", plugin.Description) ?? plugin.Description;
        var ppb = new PropertyPanelBuilder()
            .AddText(new PropertyPanel.TitleAndDescriptionLineLabel(SettingLocalizedResources.Plugin_DetailConfig(name), SettingLocalizedResources.Plugin_DetailConfig_Subtitle(name)));
        if (!plugin.Configuration.Any())
        {
            ppb.AddText(new SingleLineLabel(SettingLocalizedResources.Plugin_DetailConfig_None(name), 16, FontAttributes.None, Colors.Gray));
        }
        else
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
        ppb.AddText(new SingleLineLabel(Localized.HomePage_ProjectContextMenu(name), 20, FontAttributes.None))
            .AddButton($"ViewProvided,{id}", SettingLocalizedResources.Plugin_ViewWhatProvided(plugin.Name));
        if (plugin.LocalizationProvider.TryGetValue("option", out var optKVP) && optKVP.TryGetValue("_IsInternalPlugin", out var isInternal) && bool.TryParse(isInternal, out var result) && result)
        {
            ppb.AddText(new SingleLineLabel(SettingLocalizedResources.Plugin_CannotRemoveInternalPlugin, 14, default, Colors.Grey));
        }
        else
        {
            ppb
              .AddButton($"DisablePlugin,{id}", SettingLocalizedResources.Plugin_Disable(plugin.Name))
              .AddButton($"GotoHomepage,{id}", SettingLocalizedResources.Plugin_GotoHomepage(plugin.Name))
              //.AddButton($"UpdatePlugin,{id}", SettingLocalizedResources.Plugin_UpdatePlugin(plugin.Name)) //todo
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

    private async void SettingInvoker(PropertyPanelPropertyChangedEventArgs args, Page currentPage = null)
    {
        try
        {
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
                    await PluginService.AddAPlugin(result.FullPath, this);
                }
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
                if (await DisplayAlertAsync(Localized._Warn, SettingLocalizedResources.CommonStr_RebootRequired(), Localized._Confirm, Localized._Cancel))
                {
                    await MainSettingsPage.RebootApp(currentPage ?? this);
                }
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
                        FileSystemService.OpenFolderAsync(Path.Combine(MauiProgram.BasicDataPath, "Plugins", plugin.PluginID));
                        break;
                    }
                case "DisablePlugin":
                    {
                        PluginService.DisablePlugin(plugin.PluginID);
                        if (await DisplayAlertAsync(Localized._Warn, SettingLocalizedResources.CommonStr_RebootRequired(), Localized._Confirm, Localized._Cancel))
                        {
                            await MainSettingsPage.RebootApp(currentPage ?? this);
                        }
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
                            await MainSettingsPage.RebootApp(currentPage ?? this);

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

            //BuildPPB(); //No any plan to implement dynamic update plugins
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(Localized._Warn, Localized._ExceptionTemplate(ex), Localized._OK);
        }
    }
}