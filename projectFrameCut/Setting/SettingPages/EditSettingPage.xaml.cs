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
using projectFrameCut.Render.Effect;
using projectFrameCut.LivePreview;
using projectFrameCut.InteractableEditor;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using RoundRectangle = Microsoft.Maui.Controls.Shapes.RoundRectangle;

public partial class EditSettingPage : ContentPage
{
    public PropertyPanelBuilder rootPPB;

    public readonly Dictionary<string, string> ProxyStringMapping = new Dictionary<string, string>
    {
        { SettingLocalizedResources.Edit_ProxyOption_Ask, "ask" },
        { SettingLocalizedResources.Edit_ProxyOption_Always, "always" },
        { SettingLocalizedResources.Edit_ProxyOption_Never, "never" },

    };
    public readonly Dictionary<string, string> PreviewOutputModeStringMapping = new Dictionary<string, string>
    {
        { SettingLocalizedResources.Edit_NativePreviewOutputMode_Disabled, nameof(NativePreviewOutputMode.Disabled) },
        { SettingLocalizedResources.Edit_NativePreviewOutputMode_Required, nameof(NativePreviewOutputMode.Required) },
    };
    public readonly Dictionary<string, string> OrderOptionStringMapping = new Dictionary<string, string>
    {
        { Localized.AssetPage_OrderBy_AddDate, "date" },
        { Localized.AssetPage_OrderBy_Name, "name" },

    };

    Dictionary<string, TextClipEntry> TextTemplates = new();
    public readonly Dictionary<string, string> TransformOrderStringMapping = new()
    {
        [Localized.Transform_AfterEffects] = nameof(TransformRenderOrder.AfterEffects),
        [Localized.Transform_BeforeEffects] = nameof(TransformRenderOrder.BeforeEffects)
    };

    static string[] resolutions = ["1280x720", "1920x1080", "2560x1440", "3840x2160"];
    static bool LoadTextPreview = false;

    public EditSettingPage()
    {
        Title = Localized.MainSettingsPage_Tab_Edit;
        BuildPPB();
    }

    async void BuildPPB()
    {
        rootPPB = new();
        rootPPB.AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Edit_EditorPreference, SettingLocalizedResources.Edit_EditorPreference_Subtitle))
            .AddCheckbox("Edit_EnableMultiWindow", SettingLocalizedResources.Edit_EnableMultiWindow, IsBoolSettingTrueOrDefault("Edit_EnableMultiWindow", true))
            .AppendWhen(IsBoolSettingTrueOrDefault("Edit_EnableMultiWindow", true), c => c.AddCheckbox("Edit_RememberWindowLayout", SettingLocalizedResources.Edit_RememberWindowLayout, IsBoolSettingTrueOrDefault("Edit_RememberWindowLayout", true)))
            .AddCheckbox("Edit_EnableQuickCommandShortcuts", SettingLocalizedResources.Edit_EnableQuickCommandShortcuts, IsBoolSettingTrue("Edit_EnableQuickCommandShortcuts"))
            .AddCheckbox("Edit_AllowColorAdjustAppearInEffectEditor", SettingLocalizedResources.Edit_AllowColorAdjustAppearInEffectEditor, IsBoolSettingTrue("Edit_AllowColorAdjustAppearInEffectEditor"))
            .AddEntry("Edit_DefaultInfLengthClipLength", SettingLocalizedResources.Edit_DefaultInfLengthClipLength, GetSettingAs("Edit_DefaultInfLengthClipLength", 300, 300).ToString(), "300")
            .AddPicker("Edit_DefaultTransformRenderOrder", SettingLocalizedResources.Edit_DefaultTransformRenderOrder,
                TransformOrderStringMapping.Keys.ToArray(), TransformOrderStringMapping.First(p => p.Value == TransformServices.DefaultRenderOrder.ToString()).Key)
            .AddSeparator()
            .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Edit_ClipInfoTabOrder, SettingLocalizedResources.Edit_ClipInfoTabOrder_Subtitle))
            .AddCustomChild(BuildClipInfoTabOrder())
            .AddSeparator()
            .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Edit_PreviewOption, SettingLocalizedResources.Edit_PreviewOption_Subtitle))
            .AddCheckbox("Edit_UseDynamicPreview", SettingLocalizedResources.Edit_UseDynamicPreview, IsBoolSettingTrue("Edit_UseDynamicPreview"))
            .AddPicker("Edit_PreviewOutputMode", SettingLocalizedResources.Edit_NativePreviewOutputMode, PreviewOutputModeStringMapping.Keys.ToArray(), PreviewOutputModeStringMapping.FirstOrDefault(k => k.Value == GetSetting("Edit_PreviewOutputMode", nameof(NativePreviewOutputMode.Required)), new KeyValuePair<string, string>(SettingLocalizedResources.Edit_NativePreviewOutputMode_Required, nameof(NativePreviewOutputMode.Required))).Key, null)
            .AddCheckbox("Edit_PreviewWarmupEnabled", SettingLocalizedResources.Edit_PreviewWarmupEnabled, IsBoolSettingTrueOrDefault("Edit_PreviewWarmupEnabled", true))
            .AppendWhen(IsBoolSettingTrueOrDefault("Edit_PreviewWarmupEnabled", true),
                c => c.AddEntry("Edit_PreviewWarmupSeconds", SettingLocalizedResources.Edit_PreviewWarmupSeconds, GetSetting("Edit_PreviewWarmupSeconds", "5"), "5"))
            .AppendWhen(IsBoolSettingTrue("Edit_UseDynamicPreview"),
                c => c.AddCheckbox("Edit_UseLightweightDynamicPreviewHost", SettingLocalizedResources.Edit_UseLightweightDynamicPreviewHost, IsBoolSettingTrueOrDefault("Edit_UseLightweightDynamicPreviewHost", true))
                      .AddEntry("Edit_DynamicPreviewResolutionDivisor", SettingLocalizedResources.Edit_DynamicPreviewResolutionDivisor, GetSetting("Edit_DynamicPreviewResolutionDivisor", "1"), "1")
                      .AddEntry("Edit_DynamicPreviewTimeout", SettingLocalizedResources.Edit_DynamicPreviewTimeout, GetSetting("Edit_DynamicPreviewTimeout", "5000"), "5000")
            )
            .AppendWhen(!IsBoolSettingTrue("Edit_UseDynamicPreview"),
                c => c.AddPicker("Edit_LiveVideoPreviewDefaultResolution", SettingLocalizedResources.Edit_LiveVideoPreviewDefaultResolution, resolutions, GetSetting("Edit_LiveVideoPreviewDefaultResolution", "1280x720"))
                      .AddEntry("Edit_LiveVideoPreviewBufferLength", SettingLocalizedResources.Edit_LiveVideoPreviewBufferLength, GetSetting("Edit_LiveVideoPreviewBufferLength", "240"), "240")
                      .AddEntry("Edit_LiveVideoPreviewZoomFactor", SettingLocalizedResources.Edit_LiveVideoPreviewZoomFactor, GetSetting("Edit_LiveVideoPreviewZoomFactor", "8"), "8")
            )
            .AddSeparator()
            .AddText(new SingleLineLabel(SettingLocalizedResources.Edit_MiscOption, 25, FontAttributes.Bold))
            .AddPicker("Edit_ProxyOption", SettingLocalizedResources.Edit_ProxyOption, ProxyStringMapping.Keys.ToArray(), ProxyStringMapping.FirstOrDefault(k => k.Value == GetSetting("Edit_ProxyOption", "ask"), new KeyValuePair<string, string>(SettingLocalizedResources.Edit_ProxyOption_Ask, "ask")).Key)
            .AddCheckbox("Edit_AccelerateControlUpdates", SettingLocalizedResources.Edit_AccelerateControlUpdates, IsBoolSettingTrue("Edit_AccelerateControlUpdates"))
            .AddCheckbox("Edit_Denoise", SettingLocalizedResources.Edit_Denoise, IsBoolSettingTrue("Edit_Denoise"))
            .AddCheckbox("Edit_LockScrollViewAfterSelection", SettingLocalizedResources.Edit_LockScrollViewAfterSelection, IsBoolSettingTrueOrDefault("Edit_LockScrollViewAfterSelection", true))
#if WINDOWS || MACCATALYST
            .AddCheckbox("Edit_AlwaysShowToolbarButtons", SettingLocalizedResources.Edit_AlwaysShowToolbarButtons, IsBoolSettingTrue("Edit_AlwaysShowToolbarButtons"))
#endif
            .AddButton(SettingLocalizedResources.Edit_ResetQuickCommands, ResetQuickCommands)
            ;


        Dispatcher.Dispatch(() =>
        {
            Content = rootPPB.ListenToChanges(SettingInvoker).BuildWithScrollView();

        });
    }

    private View BuildClipInfoTabOrder()
    {
        var order = ClipInfoBuilder.GetTabOrder().ToList();
        var titles = ClipInfoBuilder.GetTabOrderOptions().ToDictionary(t => t.Tag, t => t.Header);
        var list = new HorizontalStackLayout { Spacing = 2, Padding = new Thickness(5, 5, 5, 0) };
        var rows = new Dictionary<string, Border>();
        foreach (var tag in order)
        {
            var row = new Border
            {
                Stroke = Colors.Gray,
                StrokeThickness = 0.5,
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(6, 6, 0, 0) },
                BackgroundColor = Colors.Gray,
                Margin = new Thickness(0, 2, 2, 0),
                Padding = new Thickness(10, 6),
                Content = new Label
                {
                    Text = titles[tag],
                    VerticalOptions = LayoutOptions.Center,
                    HorizontalOptions = LayoutOptions.Center,
                    Margin = new Thickness(8, 4),
                    TextColor = Colors.Black
                }
            };
            var drag = new DragGestureRecognizer { CanDrag = true };
            drag.DragStarting += (_, e) =>
            {
                e.Data.Properties[ClipInfoBuilder.TabOrderSettingKey] = tag;
                e.Data.Text = titles[tag];
            };
            row.GestureRecognizers.Add(drag);

            var drop = new DropGestureRecognizer { AllowDrop = true };
            drop.DragOver += (_, e) =>
            {
                var allowed = e.Data.Properties.TryGetValue(ClipInfoBuilder.TabOrderSettingKey, out var source)
                    && source is string t && t != tag && rows.ContainsKey(t);
                e.AcceptedOperation = allowed ? DataPackageOperation.Copy : DataPackageOperation.None;
                row.Stroke = allowed ? Colors.CornflowerBlue : Colors.Gray;
                row.StrokeThickness = allowed ? 2 : 0.5;
            };
            drop.DragLeave += (_, _) =>
            {
                row.Stroke = Colors.Gray;
                row.StrokeThickness = 0.5;
            };
            drop.Drop += (_, e) =>
            {
                row.Stroke = Colors.Gray;
                row.StrokeThickness = 0.5;
                if (!e.Data.Properties.TryGetValue(ClipInfoBuilder.TabOrderSettingKey, out var source)
                    || source is not string t || t == tag || !rows.ContainsKey(t)) return;
                e.Handled = true;
                var from = order.IndexOf(t);
                var to = order.IndexOf(tag);
                if (e.GetPosition(row) is { } p && p.X >= row.Width / 2) to++;
                if (from < to) to--;
                if (from == to) return;
                order.RemoveAt(from);
                order.Insert(to, t);
                WriteSetting(ClipInfoBuilder.TabOrderSettingKey, string.Join(",", order));
                list.Children.Remove(rows[t]);
                list.Children.Insert(to, rows[t]);
                LogDiagnostic($"Clip info tab order changed to {string.Join(",", order)}.");
            };
            row.GestureRecognizers.Add(drop);
            rows.Add(tag, row);
            list.Children.Add(row);
        }
        var reset = new Button { Text = SettingLocalizedResources.Edit_ClipInfoTabOrder_Reset, HorizontalOptions = LayoutOptions.End };
        reset.Clicked += (_, _) =>
        {
            WriteSetting(ClipInfoBuilder.TabOrderSettingKey, "");
            LogDiagnostic("Clip info tab order reset to default.");
            BuildPPB();
        };
        return new VerticalStackLayout
        {
            Spacing = 8,
            Margin = new Thickness(12, 0),
            Children =
            {
                new Border
                {
                    StrokeThickness = 0,
                    BackgroundColor = Color.FromArgb("#80404040"),
                    Content = new ScrollView
                    {
                        Orientation = ScrollOrientation.Horizontal,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Default,
                        Content = list
                    }
                },
                reset
            }
        };
    }

    private async void ResetQuickCommands(object? sender, EventArgs e)
    {
        if (await DisplayAlertAsync(Localized._Warn, SettingLocalizedResources.Advanced_AreYouSure, Localized._OK, Localized._Cancel))
        {
            WriteSetting("Edit_QuickCommands", DraftPage.DefaultQuickCommandOption);
            await DisplayAlertAsync(Localized._Info, Localized._Done, Localized._OK);
        }
    }

    private async void SettingInvoker(PropertyPanelPropertyChangedEventArgs args)
    {
        try
        {
            switch (args.Id)
            {
                case "Edit_AllowColorAdjustAppearInEffectEditor":
                    if (args.Value is bool enabled)
                    {
                        WriteSetting(args.Id, enabled.ToString());
                        ColorAdjustmentEffectProvider.AllowColorAdjustAppearInEffectEditor = enabled;
                    }
                    return;
                case "Edit_DefaultTransformRenderOrder":
                    var order = TransformOrderStringMapping.GetValueOrDefault(args.Value?.ToString() ?? "", nameof(TransformRenderOrder.AfterEffects));
                    WriteSetting(args.Id, order);
                    return;
                case "Edit_ProxyOption":
                    {
                        var mode = ProxyStringMapping.FirstOrDefault(k => k.Key == args.Value as string,
                                                 new KeyValuePair<string, string>("ask", "ask")).Value;
                        WriteSetting("Edit_ProxyOption", mode);
                        return;
                    }

                case "Edit_PreviewOutputMode":
                    {
                        var mode = PreviewOutputModeStringMapping.FirstOrDefault(k => k.Key == args.Value as string,
                            new KeyValuePair<string, string>(nameof(NativePreviewOutputMode.Required), nameof(NativePreviewOutputMode.Required))).Value;
                        WriteSetting(args.Id, mode);
                        LivePreviewer.DefaultOutputMode = DynamicPreview.DefaultOutputMode = mode == nameof(NativePreviewOutputMode.Disabled)
                            ? NativePreviewOutputMode.Disabled
                            : NativePreviewOutputMode.Required;
                        LogDiagnostic($"Preview output mode changed to {mode}.");
                        return;
                    }
                case "Edit_PreviewWarmupSeconds":
                    if (int.TryParse(args.Value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                        && seconds is >= 1 and <= 60)
                        WriteSetting(args.Id, seconds.ToString(CultureInfo.InvariantCulture));
                    return;
                case "TextTemplates":
                    {
                        File.WriteAllText(Path.Combine(MauiProgram.BasicDataPath, "TextTemplates.json"), System.Text.Json.JsonSerializer.Serialize(TextTemplates));
                        BuildPPB();
                        break;
                    }
                case "Edit_UpperContentHeight_AutoSave":
                case "Edit_UseDynamicPreview":
                case "Edit_PreviewWarmupEnabled":
                case "Edit_EnableMultiWindow":
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
            await DisplayAlertAsync(Localized._Warn, Localized._ExceptionTemplate(ex), Localized._OK);
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
}
