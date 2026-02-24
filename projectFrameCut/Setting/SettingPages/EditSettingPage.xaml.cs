namespace projectFrameCut.Setting.SettingPages;

using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using projectFrameCut.DraftStuff;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.ViewModels;
using System.Globalization;
using static SettingManager.SettingsManager;

public partial class EditSettingPage : ContentPage
{
    public PropertyPanelBuilder rootPPB;

    public readonly Dictionary<string, string> ModeStringMapping = new Dictionary<string, string>
    {
        { SettingLocalizedResources.Edit_PreferredPopupMode_Right, "right" },
        { SettingLocalizedResources.Edit_PreferredPopupMode_Bottom, "bottom" },
        { SettingLocalizedResources.Edit_PreferredPopupMode_Clip, "clip" },
        { SettingLocalizedResources.Edit_PreferredPopupMode_Window, "window" },
    };
    public readonly Dictionary<string, string> ProxyStringMapping = new Dictionary<string, string>
    {
        { SettingLocalizedResources.Edit_ProxyOption_Ask, "ask" },
        { SettingLocalizedResources.Edit_ProxyOption_Always, "always" },
        { SettingLocalizedResources.Edit_ProxyOption_Never, "never" },

    };
    public readonly Dictionary<string, string> OrderOptionStringMapping = new Dictionary<string, string>
    {
        { Localized.AssetPage_OrderBy_AddDate, "date" },
        { Localized.AssetPage_OrderBy_Name, "name" },

    };

    Dictionary<string, TextClip.TextClipEntry> TextTemplates = new();

    static string[] resolutions = ["640x480", "1280x720", "1920x1080", "2560x1440", "3840x2160"];


    public EditSettingPage()
    {
        LoadTextTemplates(ref TextTemplates);
        BuildPPB();
    }

    async Task AddTextTemplateAsync()
    {
        try
        {
            var name = await DisplayPromptAsync(Localized._Info, SettingLocalizedResources.Edit_AddView_Text_Template_Add_InputName, Localized._OK, Localized._Cancel, "", -1, Keyboard.Default, "");
            if (string.IsNullOrWhiteSpace(name))
                return;
            if (TextTemplates.ContainsKey(name))
            {
                await DisplayAlertAsync(Localized._Warn, SettingLocalizedResources.Edit_AddView_Text_Template_Add_Exists, Localized._OK);
                return;
            }

            var entry = new TextClip.TextClipEntry
            {
                StyleId = name,
                r = 65535,
                g = 65535,
                b = 65535,
                a = null,
                fontFamily = "Arial",
                fontSize = 36
            };
            TextTemplates[name] = entry;
            File.WriteAllText(Path.Combine(MauiProgram.BasicDataPath, "TextTemplates.json"), System.Text.Json.JsonSerializer.Serialize(TextTemplates));
            PropertyPanelPropertyChangedEventArgs.CreateAndInvoke(rootPPB, "TextTemplates", null);
        }
        catch (Exception ex)
        {
            Log(ex, "Add template failed", this);
            await DisplayAlertAsync(Localized._Warn, Localized._ExceptionTemplate(ex), Localized._OK);
        }
    }

    async Task ConfigureTextTemplateAsync(string styleId)
    {
        if (!TextTemplates.TryGetValue(styleId, out var entry))
            return;

        // Provide at least the current font as available font list
        var fonts = TextClip.GetFont().Families.Select(c => c.Name);

        // Build editor UI
        var editorView = ClipInfoBuilder.BuildTextEntryUI(entry with { text = entry.SampleText ?? "AaBbYyZz" }, 0, fonts.ToArray(),
            (idx, updated) =>
            {
                // keep template key unchanged
                var u = updated with { StyleId = styleId, SampleText = updated.text };
                TextTemplates[styleId] = u;
                File.WriteAllText(Path.Combine(MauiProgram.BasicDataPath, "TextTemplates.json"), System.Text.Json.JsonSerializer.Serialize(TextTemplates));
                PropertyPanelPropertyChangedEventArgs.CreateAndInvoke(rootPPB, "TextTemplates", null);
            },
            (idx) =>
            {
                if (TextTemplates.Remove(styleId))
                {
                    File.WriteAllText(Path.Combine(MauiProgram.BasicDataPath, "TextTemplates.json"), System.Text.Json.JsonSerializer.Serialize(TextTemplates));
                    PropertyPanelPropertyChangedEventArgs.CreateAndInvoke(rootPPB, "TextTemplates", null);
                }
            },
            false);

        var saveBtn = new Button { Text = Localized._Confirm };
        var closeBtn = new Button { Text = Localized._Cancel, BackgroundColor = Color.FromRgba("FF9999FF"), TextColor = Colors.Black };

        var btnRow = new HorizontalStackLayout { Spacing = 8, Children = { saveBtn, closeBtn } };

        var content = new VerticalStackLayout { Spacing = 8, Padding = new Thickness(12), Children = { editorView, btnRow } };

        var popupPage = new ContentPage
        {
            Title = styleId,
            Content = new Border { Content = content, Padding = new Thickness(8), Stroke = Colors.Gray }
        };

        saveBtn.Clicked += async (s, e) =>
        {
            try
            {
                File.WriteAllText(Path.Combine(MauiProgram.BasicDataPath, "TextTemplates.json"), System.Text.Json.JsonSerializer.Serialize(TextTemplates));
                await Dispatcher.DispatchAsync(Navigation.PopAsync);
            }
            catch { }
        };
        closeBtn.Clicked += async (s, e) => await Dispatcher.DispatchAsync(Navigation.PopAsync);

        try
        {
            await Dispatcher.DispatchAsync(async () =>
            {
                await Navigation.PushAsync(popupPage);
            });
        }
        catch (Exception ex)
        {
            Log(ex, "Show template configure popup failed", this);
        }
    }

    public static void LoadTextTemplates(ref Dictionary<string, TextClip.TextClipEntry> TextTemplates)
    {

        var templatePath = Path.Combine(MauiProgram.BasicDataPath, "TextTemplates.json");
        if (File.Exists(templatePath))
        {
            try
            {
                var json = File.ReadAllText(templatePath);
                var t = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, TextClip.TextClipEntry>>(json) ?? new();
                foreach (var item in t)
                {
                    TextTemplates.Add(item.Key, item.Value);
                }
            }
            catch (Exception ex)
            {
                Log(ex, "Load text templates failed");
            }
        }
        if (TextTemplates.Count == 0)
        {
            var defaultTemplates = new List<TextClip.TextClipEntry>
            {
                new TextClip.TextClipEntry
                {
                    StyleId = "Default",
                    r = 65535,
                    g = 65535,
                    b = 65535,
                    a = null,
                    fontFamily = "Arial",
                    fontSize = 20
                },
                new TextClip.TextClipEntry
                {
                    StyleId = "Title",
                    r = 65535,
                    g = 65535,
                    b = 65535,
                    a = null,
                    fontFamily = "Arial",
                    fontSize = 64
                },
                new TextClip.TextClipEntry
                {
                    StyleId = "Subtitle",
                    r = 65535,
                    g = 65535,
                    b = 65535,
                    a = null,
                    fontFamily = "Arial",
                    fontSize = 32,
                    ShouldInSubtrack = true,
                }
            };
            TextTemplates = defaultTemplates.ToDictionary(c => c.StyleId);
            File.WriteAllText(Path.Combine(MauiProgram.BasicDataPath, "TextTemplates.json"), System.Text.Json.JsonSerializer.Serialize(TextTemplates));
        }
    }

    void BuildPPB()
    {
        Title = Localized.MainSettingsPage_Tab_Edit;

        rootPPB = new();
        rootPPB.AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Edit_EditorPreference, SettingLocalizedResources.Edit_EditorPreference_Subtitle))
            .AppendWhen(DeviceInfo.Idiom != DeviceIdiom.Phone,c => c.AddPicker("Edit_PreferredPopupMode",
                SettingLocalizedResources.Edit_PreferredPopupMode, ModeStringMapping.Keys.ToArray(),
                ModeStringMapping.FirstOrDefault(k => k.Value == GetSetting("Edit_PreferredPopupMode", "right"), new KeyValuePair<string, string>(SettingLocalizedResources.Edit_PreferredPopupMode_Right, "right")).Key))
            .AddSwitch("Edit_UpperContentHeight_AutoSave", SettingLocalizedResources.Edit_UpperContentHeight_AutoSave, IsBoolSettingTrue("Edit_UpperContentHeight_AutoSave"), null)
            .AppendWhen(!IsBoolSettingTrue("Edit_UpperContentHeight_AutoSave"), p => p.AddEntry("Edit_UpperContentHeight", SettingLocalizedResources.Edit_UpperContentHeight, GetSetting("Edit_UpperContentHeight", "250"), "250"))
            .AddEntry("Edit_MaximumSaveSlot", SettingLocalizedResources.Edit_MaxiumSaveSlot, GetSetting("Edit_MaximumSaveSlot", "10"), "10")
            .AddSeparator()

            .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Edit_AddView, SettingLocalizedResources.Edit_AddView_Subtitle))
            .AddPicker("Edit_AddView_DefaultOrderOption", SettingLocalizedResources.Edit_AddView_DefaultOrderOption, OrderOptionStringMapping.Keys.ToArray(), OrderOptionStringMapping.FirstOrDefault(k => k.Value == GetSetting("Edit_AddView_DefaultOrderOption", "date"), new KeyValuePair<string, string>(Localized.AssetPage_OrderBy_AddDate, "date")).Key, null)
            .AddSeparator()
            .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Edit_AddView_Text_Template, ""))
            .AddButton(SettingLocalizedResources.Edit_AddView_Text_Template_Add,
                async (s, e) =>
                {
                    await AddTextTemplateAsync();
                })
            .Foreach(TextTemplates,
                    (b, e) =>
                    {
                        b.AddSeparator();
                        var s = e.Value;
                        var sample = s.SampleText ?? "AaBbYyZz";
                        TextClip t = new TextClip
                        {
                            Id = s.StyleId,
                            Name = s.StyleId,
                            TextEntries = new List<TextClip.TextClipEntry>
                            {
                                e.Value with { text = sample }
                            }
                        };

                        var fs = s.fontSize > 0 ? s.fontSize : 36;
                        var imgHeight = Math.Clamp((int)(fs * 1.2) + 4, 24, 200);
                        var imgWidth = Math.Clamp((int)(sample.Length * fs * 0.6) + 20, 100, 1200);

                        var img = t.GetFrameRelativeToStartPointOfSource(0, imgWidth, imgHeight, true);

                        b.AddText(SettingLocalizedResources.Edit_AddView_Text_Template_TemplateItem(e.Key))
                         .AddCustomChild(new Image { Source = img.ToImageSource(), Aspect = Aspect.AspectFit, HeightRequest = imgHeight, WidthRequest = Math.Min(imgWidth, 600), HorizontalOptions = LayoutOptions.Start, VerticalOptions = LayoutOptions.Start, Margin = new Thickness(0) })
                         .AddButton(SettingLocalizedResources.Edit_AddView_Text_Template_Configure(s.StyleId), async (_, _) => { await ConfigureTextTemplateAsync(s.StyleId); })
                         .AddButton(Localized._Remove, (_, _) => { TextTemplates.Remove(e.Key); PropertyPanelPropertyChangedEventArgs.CreateAndInvoke(rootPPB, "TextTemplates", null); }, b => b.IsEnabled = s.StyleId != "Default")
                        ;
                    })
            .AddSeparator()
            .AddButton(SettingLocalizedResources.Edit_AddView_Text_Template_Export,
                async (s, e) =>
                {
                    try
                    {
                        var f = await FilePicker.PickAsync(
                                        new PickOptions
                                        {
                                            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                                            {
                                                                    { DevicePlatform.WinUI, [".json"]},
                                                                    { DevicePlatform.Android, ["application/json"] },
                                                                    { DevicePlatform.iOS, ["public.json"]  },
                                                                    { DevicePlatform.MacCatalyst, ["json"] }
                                            })
                                        });
                        using var json = await (f?.OpenReadAsync() ?? new(() => Stream.Null));
                        using var sr = new StreamReader(json ?? Stream.Null);
                        var text = await sr.ReadToEndAsync();
                        var importedTemplates = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, TextClip.TextClipEntry>>(text);
                        if (importedTemplates != null)
                        {
                            var exists = importedTemplates.Where(k => TextTemplates.ContainsKey(k.Key));
                            if (exists.Any())
                            {
                                var conf = await DisplayPromptAsync(Localized._Warn,
                                            SettingLocalizedResources.Edit_AddView_Text_Template_Import_Warn(string.Join(", ", exists.Select(c => c.Key))),
                                            Localized._Confirm,
                                            Localized._Cancel,
                                            "no",
                                            -1,
                                            Keyboard.Default,
                                            "");
                                if (conf != "yes")
                                {
                                    return;
                                }
                            }
                            foreach (var item in importedTemplates)
                            {
                                TextTemplates[item.Key] = item.Value;
                            }
                            PropertyPanelPropertyChangedEventArgs.CreateAndInvoke(rootPPB, "TextTemplates", null);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log(ex, "import text template failed", this);
                        await DisplayAlertAsync(Localized._Warn, Localized._ExceptionTemplate(ex), Localized._OK);

                    }
                })
            .AddButton(SettingLocalizedResources.Edit_AddView_Text_Template_Import,
                async (s, e) =>
                {
                    await Share.RequestAsync(new ShareFileRequest { File = new ShareFile(Path.Combine(MauiProgram.BasicDataPath, "TextTemplates.json")) });
                })
            .AddButton(SettingLocalizedResources.Edit_AddView_Text_Template_Reset,
                        async (s, e) =>
                        {
                            await DisplayPromptAsync(Localized._Warn,
                                                     SettingLocalizedResources.Edit_AddView_Text_Template_Reset_Warn,
                                                     Localized._Confirm,
                                                     Localized._Cancel,
                                                     "no",
                                                     -1,
                                                     Keyboard.Default,
                                                     "")
                            .ContinueWith(async (t) =>
                            {
                                if (t.Result != "yes")
                                    return;
                                TextTemplates.Clear();
                                File.Delete(Path.Combine(MauiProgram.BasicDataPath, "TextTemplates.json"));
                                LoadTextTemplates(ref TextTemplates);
                                Dispatcher.Dispatch(() => PropertyPanelPropertyChangedEventArgs.CreateAndInvoke(rootPPB, "TextTemplates", null));
                            });

                        },
                        b =>
                        {
                            b.BackgroundColor = Color.FromRgba("FF9999FF");
                            b.TextColor = Colors.Black;
                        })
            .AddSeparator()

            .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Edit_PreviewOption, SettingLocalizedResources.Edit_PreviewOption_Subtitle))
            .AddPicker("Edit_LiveVideoPreviewDefaultResolution", SettingLocalizedResources.Edit_LiveVideoPreviewDefaultResolution, resolutions, GetSetting("Edit_LiveVideoPreviewDefaultResolution", "1280x720"), null)
            .AddEntry("Edit_LiveVideoPreviewBufferLength", SettingLocalizedResources.Edit_LiveVideoPreviewBufferLength, GetSetting("Edit_LiveVideoPreviewBufferLength", "240"), "240")
            .AddEntry("Edit_LiveVideoPreviewZoomFactor", SettingLocalizedResources.Edit_LiveVideoPreviewZoomFactor, GetSetting("Edit_LiveVideoPreviewZoomFactor", "8"), "8")
            .AddSeparator()


            .AddText(new SingleLineLabel(SettingLocalizedResources.Edit_MiscOption, 20, FontAttributes.Bold))
            .AddPicker("Edit_ProxyOption", SettingLocalizedResources.Edit_ProxyOption, ProxyStringMapping.Keys.ToArray(), ProxyStringMapping.FirstOrDefault(k => k.Value == GetSetting("Edit_ProxyOption", "ask"), new KeyValuePair<string, string>(SettingLocalizedResources.Edit_ProxyOption_Ask, "ask")).Key, null)
            .AddSwitch("Edit_Denoise", SettingLocalizedResources.Edit_Denoise, IsBoolSettingTrue("Edit_Denoise"), null)
            .AddSwitch("Edit_LockScrollViewAfterSelection", SettingLocalizedResources.Edit_LockScrollViewAfterSelection, IsBoolSettingTrueOrDefault("Edit_LockScrollViewAfterSelection", true), null)
#if WINDOWS || MACCATALYST
            .AddSwitch("Edit_AlwaysShowToolbarButtons", SettingLocalizedResources.Edit_AlwaysShowToolbarButtons, IsBoolSettingTrue("Edit_AlwaysShowToolbarButtons"), null)
#endif
            ;


        Content = rootPPB.ListenToChanges(SettingInvoker).BuildWithScrollView();
    }

    private async void SettingInvoker(PropertyPanelPropertyChangedEventArgs args)
    {
        try
        {
            switch (args.Id)
            {
                case "Edit_PreferredPopupMode":
                    {
                        var mode = ModeStringMapping.FirstOrDefault(k => k.Key == args.Value as string,
                                                 new KeyValuePair<string, string>("right", "right")).Value;
                        WriteSetting("PreferredPopupMode", mode);
                        return;
                    }
                case "Edit_ProxyOption":
                    {
                        var mode = ProxyStringMapping.FirstOrDefault(k => k.Key == args.Value as string,
                                                 new KeyValuePair<string, string>("ask", "ask")).Value;
                        WriteSetting("Edit_ProxyOption", mode);
                        return;
                    }
                case "Edit_AddView_DefaultOrderOption":
                    {
                        var mode = OrderOptionStringMapping.FirstOrDefault(k => k.Key == args.Value as string,
                                                 new KeyValuePair<string, string>(Localized.AssetPage_OrderBy_AddDate, "date")).Value;
                        WriteSetting("Edit_AddView_DefaultOrderOption", mode);
                        return;
                    }
                case "TextTemplates":
                    {
                        File.WriteAllText(Path.Combine(MauiProgram.BasicDataPath, "TextTemplates.json"), System.Text.Json.JsonSerializer.Serialize(TextTemplates));
                        BuildPPB();
                        break;
                    }
                case "Edit_UpperContentHeight_AutoSave":
                    BuildPPB();
                    break;
                default:
                    {
                        if (args.Value != null)
                        {
                            WriteSetting(args.Id, args.Value?.ToString() ?? "");
                        }
                        return;
                    }
            }




        }
        catch (Exception ex)
        {
            // 处理异常并通知用户
            await DisplayAlert(Localized._Warn, Localized._ExceptionTemplate(ex), Localized._OK);
        }
    }
}