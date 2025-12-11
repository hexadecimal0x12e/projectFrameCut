using projectFrameCut.PropertyPanel;
using projectFrameCut.Render.Plugins;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using System.Globalization;
using System.Reflection;
using System.Text;
using static projectFrameCut.Setting.SettingManager.SettingsManager;

namespace projectFrameCut.Setting.SettingPages;

public partial class PluginSettingPage : ContentPage
{
    public PropertyPanel.PropertyPanelBuilder rootPPB;


    public PluginSettingPage()
    {
        BuildPPB();
    }

    void BuildPPB()
    {
        Title = Localized.MainSettingsPage_Tab_Plugin;
        rootPPB = new();
        rootPPB
            .AddText(new PropertyPanel.SingleLineLabel(SettingLocalizedResources.Plugin_ManagePlugins, 20))
            .AddButton("addButton",SettingLocalizedResources.Plugin_AddOne);

        foreach (var item in PluginManager.LoadedPlugins)
        {
            var plugin = item.Value;
            rootPPB
                .AddSeparator()
                .AddText(new PropertyPanel.TitleAndDescriptionLineLabel(plugin.Name, plugin.Description, 20, 14))
                .AddText(new PropertyPanel.SingleLineLabel(SettingLocalizedResources.Plugin_DetailInfo(plugin.Author, plugin.Version, plugin.PluginID), 12))
                //.AddButton($"ViewProvided,{item.Key}", SettingLocalizedResources.Plugin_ViewWhatProvided(plugin.Name))
                //.AddButton($"UpdatePlugin,{item.Key}", SettingLocalizedResources.Plugin_UpdatePlugin(plugin.Name))
                .AddButton($"MoreOption,{item.Key}", SettingLocalizedResources.Plugin_MoreOption);


        }

        rootPPB
            .AddSeparator()
            .ListenToChanges(SettingInvoker);

        Content = new ScrollView { Content = rootPPB.Build() };
    }

    private async void SettingInvoker(PropertyPanelPropertyChangedEventArgs args)
    {
        try
        {
            if(args.Id == "addButton")
            {
                var result = await FilePicker.Default.PickAsync(new PickOptions
                {
                    FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                    {
                        { DevicePlatform.WinUI, new[] { ".dll" } },
                        { DevicePlatform.Android, new[] { "application/octet-stream", "application/x-msdownload", "application/x-dosexec" } },
#if iDevices
                        {DevicePlatform.iOS, new[] {""} },
                        {DevicePlatform.MacCatalyst, new[] {""} }
#endif
                    }),
                });

                if (result != null)
                {
                    string dllPath = result.FullPath;
                    var asb = Assembly.LoadFrom(dllPath);
                    var module = asb.GetModule(asb.GetName().Name + ".dll");
                    var types = module.GetTypes();
                    var ldr = types.First(a => a.Name == "PluginLoader");
                    if(ldr is null)
                    {
                        await DisplayAlertAsync(Localized._Warn, "unable to find loader", Localized._OK);
                        return;
                    }
                    var ldrMethod = ldr.GetMethod("Load");
                    var pluginObj = ldrMethod?.Invoke(null, [Localized._LocaleId_]);
                    if(pluginObj is IPluginBase pluginInstance)
                    {
                        var conf = await DisplayAlertAsync(Localized._Warn, SettingLocalizedResources.Plugin_AddWarn(pluginInstance.Name), Localized._OK, Localized._Cancel);
                        if (conf)
                        {
                            //todo: save plugin private key
                            //await SecureStorage.Default.SetAsync("oauth_token", "secret-oauth-token-value");
                            PluginManager.LoadFrom(pluginInstance);
                            BuildPPB();
                        }
                    }
                }
                return;
            }

            var flags = args.Id.Split(',');

            var flag = flags[0];
            var id = flags[1];
            if (!PluginManager.LoadedPlugins.TryGetValue(id, out var plugin))
            {
                await DisplayAlertAsync(Localized._Warn, $"plugin {id} not found", Localized._OK);
                return;
            }

            switch (flag)
            {
                case "ViewProvided":
                    {

                        StringBuilder providedContent = new();

                        providedContent.AppendLine("Clips:");
                        foreach (var item in plugin.ClipProvider)
                        {
                            providedContent.AppendLine($"- {item.Key}");
                        }
                        providedContent.AppendLine();

                        providedContent.AppendLine("Effect:");
                        foreach (var item in plugin.EffectProvider)
                        {
                            providedContent.AppendLine($"- {item.Key}");
                        }
                        providedContent.AppendLine();
                        providedContent.AppendLine("Mixture:");
                        foreach (var item in plugin.MixtureProvider)
                        {
                            providedContent.AppendLine($"- {item.Key}");
                        }
                        providedContent.AppendLine();
                        providedContent.AppendLine("Computer:");
                        foreach (var item in plugin.ComputerProvider)
                        {
                            providedContent.AppendLine($"- {item.Key}");
                        }
                        providedContent.AppendLine();
                        providedContent.AppendLine("VideoSource:");
                        foreach (var item in plugin.VideoSourceProvider)
                        {
                            providedContent.AppendLine($"- {item.Key}");
                        }


                        await DisplayAlertAsync(Localized._Info, providedContent.ToString(), Localized._OK);


                        break;
                    }

                case "UpdatePlugin":
                    {
                        //todo
                        break;
                    }

                case "MoreOption":
                    {

                        List<string> actions = new()
                        {
                            SettingLocalizedResources.Plugin_ViewWhatProvided(plugin.Name),
                            SettingLocalizedResources.Plugin_UpdatePlugin(plugin.Name),
                            SettingLocalizedResources.Plugin_OpenDataDir,
                            SettingLocalizedResources.Plugin_Remove,
                        };

                        var selection = await DisplayActionSheetAsync(Localized.HomePage_ProjectContextMenu(plugin.Name), Localized._Cancel, null, actions.ToArray());
                        if(selection == SettingLocalizedResources.Plugin_ViewWhatProvided(plugin.Name))
                        {
                            SettingInvoker(new PropertyPanelPropertyChangedEventArgs($"ViewProvided,{id}", null, null));
                        }
                        else if (selection == SettingLocalizedResources.Plugin_UpdatePlugin(plugin.Name))
                        {
                            SettingInvoker(new PropertyPanelPropertyChangedEventArgs($"UpdatePlugin,{id}", null, null));
                        }
                        else if (selection == SettingLocalizedResources.Plugin_OpenDataDir)
                        {
                            try
                            {
                                //await Launcher.Default.OpenAsync(new OpenFileRequest
                                //{
                                //    File = new ReadOnlyFile(dataDir)
                                //});
                            }
                            catch
                            {
                                //await DisplayAlertAsync(Localized._Warn, SettingLocalizedResources.Plugin_FailedToOpenDataDir(dataDir), Localized._OK);
                            }
                        }
                        else if (selection == SettingLocalizedResources.Plugin_Remove)
                        {
                            //var confirm = await DisplayAlertAsync(Localized._Warn, SettingLocalizedResources.Plugin_ConfirmRemove(plugin.Name), Localized._Yes, Localized._No);
                            //if (confirm)
                            //{
                            //    PluginManager.UnloadPlugin(plugin.PluginID);
                            //    BuildPPB();
                            //}
                        }
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