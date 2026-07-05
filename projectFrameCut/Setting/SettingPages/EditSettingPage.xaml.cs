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
using projectFrameCut.Shared;
using projectFrameCut.Services;
using projectFrameCut.Render.Compose;

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

    Dictionary<string, TextClipEntry> TextTemplates = new();

    static string[] resolutions = ["1280x720", "1920x1080", "2560x1440", "3840x2160"];
    static bool LoadTextPreview = false;

    public EditSettingPage()
    {
#if WINDOWS
        try
        {
            if (!IsBoolSettingTrue("Edit_NoLoadTextTemplatePreview"))
            {
                // AcceleratorsManager was initialized during plugin load.
                // Any available non-CPU accelerator is sufficient for text preview.
                LoadTextPreview = projectFrameCut.Render.HwAccelEngine.Platforms.Windows.AcceleratorsManager.DefaultAccelerator is not null;
            }
        }
        catch { LoadTextPreview = false; }
#endif
        Title = Localized.MainSettingsPage_Tab_Edit;
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
        LoadTextTemplates(ref TextTemplates);
        Task.Run(BuildPPB);
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

            var entry = new TextClipEntry
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

        // Build editor UI
        var editorView = ClipInfoBuilder.BuildTextEntryUI(entry with { text = entry.SampleText ?? "AaBbYyZz" }, 0, TextServices.LoadedFonts.Select(C => C.Value),
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
            false,
            true,
            (v) =>
            {
                Dispatcher.Dispatch(() =>
                {
                    Navigation.PushAsync(new ContentPage
                    {
                        Title = LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResources.TextOption_Font,
                        Content = v
                    });
                });
            },
            () =>
            {
                Dispatcher.Dispatch(() =>
                {
                    Navigation.PopAsync();
                });
            });

        var saveBtn = new Button { Text = Localized._Confirm };
        var closeBtn = new Button { Text = Localized._Cancel, BackgroundColor = Color.FromRgba("FF9999FF"), TextColor = Colors.Black };

        var btnRow = new HorizontalStackLayout { Spacing = 8, Children = { saveBtn, closeBtn } };

        var content = new VerticalStackLayout { Spacing = 8, Padding = new Thickness(12), Children = { editorView, btnRow } };



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
            var popupPage = new ContentPage
            {
                Title = styleId,
                Content = new ScrollView { Content = content }
            };
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

    public static void LoadTextTemplates(ref Dictionary<string, TextClipEntry> TextTemplates)
    {

        var templatePath = Path.Combine(MauiProgram.BasicDataPath, "TextTemplates.json");
        if (File.Exists(templatePath))
        {
            try
            {
                var json = File.ReadAllText(templatePath);
                var t = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, TextClipEntry>>(json) ?? new();
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
            var defaultTemplates = new List<TextClipEntry>
            {
                new TextClipEntry
                {
                    StyleId = "Default",
                    r = 65535,
                    g = 65535,
                    b = 65535,
                    a = null,
                    fontFamily = "Arial",
                    fontSize = 20
                },
                new TextClipEntry
                {
                    StyleId = "Title",
                    r = 65535,
                    g = 65535,
                    b = 65535,
                    a = null,
                    fontFamily = "Arial",
                    fontSize = 64
                },
                new TextClipEntry
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

    async void BuildPPB()
    {
        rootPPB = new();
        rootPPB.AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Edit_EditorPreference, SettingLocalizedResources.Edit_EditorPreference_Subtitle))
            .AppendWhen(DeviceInfo.Idiom != DeviceIdiom.Phone,
                c => c.AddPicker("Edit_PreferredPopupMode",
                SettingLocalizedResources.Edit_PreferredPopupMode, ModeStringMapping.Keys.ToArray(),
                ModeStringMapping.FirstOrDefault(k => k.Value == GetSetting("Edit_PreferredPopupMode", "right"), new KeyValuePair<string, string>(SettingLocalizedResources.Edit_PreferredPopupMode_Right, "right")).Key)
                      .AddSwitch("Edit_EnableClipInfoPopup", SettingLocalizedResources.Edit_EnableClipInfoPopup, IsBoolSettingTrue("Edit_EnableClipInfoPopup"), null)
            )
            .AddSwitch("Edit_UpperContentHeight_AutoSave", SettingLocalizedResources.Edit_UpperContentHeight_AutoSave, IsBoolSettingTrue("Edit_UpperContentHeight_AutoSave"), null)
            .AppendWhen(!IsBoolSettingTrue("Edit_UpperContentHeight_AutoSave"), p => p.AddEntry("Edit_UpperContentHeight", SettingLocalizedResources.Edit_UpperContentHeight, GetSetting("Edit_UpperContentHeight", "250"), "250"))
            //.AddEntry("Edit_MaximumSaveSlot", SettingLocalizedResources.Edit_MaxiumSaveSlot, GetSetting("Edit_MaximumSaveSlot", "50"), "50")
            .AddEntry("Edit_DefaultInfLengthClipLength", SettingLocalizedResources.Edit_DefaultInfLengthClipLength, GetSettingAs<int>("Edit_DefaultInfLengthClipLength", 300, 300).ToString(), "300")

            .AddSeparator()

            .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Edit_AddView, SettingLocalizedResources.Edit_AddView_Subtitle))
            //.AddPicker("Edit_AddView_DefaultOrderOption", SettingLocalizedResources.Edit_AddView_DefaultOrderOption, OrderOptionStringMapping.Keys.ToArray(), OrderOptionStringMapping.FirstOrDefault(k => k.Value == GetSetting("Edit_AddView_DefaultOrderOption", "date"), new KeyValuePair<string, string>(Localized.AssetPage_OrderBy_AddDate, "date")).InternalPlaceID, null)
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
#pragma warning disable CS0618 // 类型或成员已过时
                        TextClip t = new TextClip
                        {
                            Id = Guid.Empty,
                            Name = s.StyleId,
                            TextEntries = TextEntryMigration.MigrateFromTextClipEntries(new List<TextClipEntry>
                            {
                                e.Value with { text = sample }
                            })
                        };
#pragma warning restore CS0618 // 类型或成员已过时


                        var fs = s.fontSize > 0 ? s.fontSize : 36;
                        var imgHeight = Math.Clamp((int)(fs * 1.2) + 4, 24, 200);
                        var imgWidth = Math.Clamp((int)(sample.Length * fs * 0.6) + 20, 100, 1200);

                        var textPic = t.GetFrameRelativeToStartPointOfSource(0, imgWidth, imgHeight, true, 8);

                        b.AddText(SettingLocalizedResources.Edit_AddView_Text_Template_TemplateItem(e.Key))
                         .AddCustomChild(new Image { Source = textPic.ToImageSource(), Aspect = Aspect.AspectFit, HeightRequest = imgHeight, WidthRequest = Math.Min(imgWidth, 600), HorizontalOptions = LayoutOptions.Start, VerticalOptions = LayoutOptions.Start, Margin = new Thickness(0), Background = Color.FromRgb((255 - e.Value.r / 257), (255 - e.Value.g / 257), (255 - e.Value.b / 257)) })
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
                        var f = await Dispatcher.DispatchAsync(() => FilePicker.PickAsync(
                                new PickOptions
                                {
                                    FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                                    {
                                        { DevicePlatform.WinUI, [".json"]},
                                        { DevicePlatform.Android, ["application/json"] },
                                        { DevicePlatform.iOS, ["public.json"]  },
                                        { DevicePlatform.MacCatalyst, ["json"] }
                                    })
                                }));
                        using var json = await (f?.OpenReadAsync() ?? new(() => Stream.Null));
                        using var sr = new StreamReader(json ?? Stream.Null);
                        var text = await sr.ReadToEndAsync();
                        var importedTemplates = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, TextClipEntry>>(text);
                        if (importedTemplates != null)
                        {
                            var exists = importedTemplates.Where(k => TextTemplates.ContainsKey(k.Key));
                            if (exists.Any())
                            {
                                var conf = await Dispatcher.DispatchAsync(() => DisplayPromptAsync(Localized._Warn,
                                            SettingLocalizedResources.Edit_AddView_Text_Template_Import_Warn(string.Join(", ", exists.Select(c => c.Key))),
                                            Localized._Confirm,
                                            Localized._Cancel,
                                            "no",
                                            -1,
                                            Keyboard.Default,
                                            ""));
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
                    await Dispatcher.DispatchAsync(() => Share.RequestAsync(new ShareFileRequest { File = new ShareFile(Path.Combine(MauiProgram.BasicDataPath, "TextTemplates.json")) }));
                })
            .AddButton(SettingLocalizedResources.Edit_AddView_Text_Template_Reset,
                        async (s, e) =>
                        {
                            await Dispatcher.DispatchAsync(() => DisplayPromptAsync(Localized._Warn,
                                                     SettingLocalizedResources.Edit_AddView_Text_Template_Reset_Warn,
                                                     Localized._Confirm,
                                                     Localized._Cancel,
                                                     "no",
                                                     -1,
                                                     Keyboard.Default,
                                                     ""))
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
            .AddSwitch("Edit_UseDynamicPreview", SettingLocalizedResources.Edit_UseDynamicPreview, IsBoolSettingTrue("Edit_UseDynamicPreview"), null)
            .AppendWhen(IsBoolSettingTrue("Edit_UseDynamicPreview"),
                c => c.AddEntry("Edit_DynamicPreviewResolutionDivisor", SettingLocalizedResources.Edit_DynamicPreviewResolutionDivisor, GetSetting("Edit_DynamicPreviewResolutionDivisor", "1"), "1")
                      .AddEntry("Edit_DynamicPreviewTimeout", SettingLocalizedResources.Edit_DynamicPreviewTimeout, GetSetting("Edit_DynamicPreviewTimeout", "5000"), "5000")
            )
            .AppendWhen(!IsBoolSettingTrue("Edit_UseDynamicPreview"), 
                c => c.AddPicker("Edit_LiveVideoPreviewDefaultResolution", SettingLocalizedResources.Edit_LiveVideoPreviewDefaultResolution, resolutions, GetSetting("Edit_LiveVideoPreviewDefaultResolution", "1280x720"), null)
                      .AddEntry("Edit_LiveVideoPreviewBufferLength", SettingLocalizedResources.Edit_LiveVideoPreviewBufferLength, GetSetting("Edit_LiveVideoPreviewBufferLength", "240"), "240")
                      .AddEntry("Edit_LiveVideoPreviewZoomFactor", SettingLocalizedResources.Edit_LiveVideoPreviewZoomFactor, GetSetting("Edit_LiveVideoPreviewZoomFactor", "8"), "8")
            )
            .AddSeparator()


            .AddText(new SingleLineLabel(SettingLocalizedResources.Edit_MiscOption, 25, FontAttributes.Bold))
            .AddPicker("Edit_ProxyOption", SettingLocalizedResources.Edit_ProxyOption, ProxyStringMapping.Keys.ToArray(), ProxyStringMapping.FirstOrDefault(k => k.Value == GetSetting("Edit_ProxyOption", "ask"), new KeyValuePair<string, string>(SettingLocalizedResources.Edit_ProxyOption_Ask, "ask")).Key, null)
            .AddSwitch("Edit_Denoise", SettingLocalizedResources.Edit_Denoise, IsBoolSettingTrue("Edit_Denoise"), null)
            .AddSwitch("Edit_LockScrollViewAfterSelection", SettingLocalizedResources.Edit_LockScrollViewAfterSelection, IsBoolSettingTrueOrDefault("Edit_LockScrollViewAfterSelection", true), null)
#if WINDOWS || MACCATALYST
            .AddSwitch("Edit_AlwaysShowToolbarButtons", SettingLocalizedResources.Edit_AlwaysShowToolbarButtons, IsBoolSettingTrue("Edit_AlwaysShowToolbarButtons"), null)
#endif
            ;


        Dispatcher.Dispatch(() =>
        {
            Content = rootPPB.ListenToChanges(SettingInvoker).BuildWithScrollView();

        });
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

                case "TextTemplates":
                    {
                        File.WriteAllText(Path.Combine(MauiProgram.BasicDataPath, "TextTemplates.json"), System.Text.Json.JsonSerializer.Serialize(TextTemplates));
                        BuildPPB();
                        break;
                    }
                case "Edit_UpperContentHeight_AutoSave":
                case "Edit_UseDynamicPreview":
                    if (args.Value != null)
                    {
                        WriteSetting(args.Id, args.Value?.ToString() ?? "");
                    }
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
            // �����쳣��֪ͨ�û�
            await DisplayAlertAsync(Localized._Warn, Localized._ExceptionTemplate(ex), Localized._OK);
        }
    }
}