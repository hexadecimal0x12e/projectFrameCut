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
using projectFrameCut.LivePreview;
using projectFrameCut.InteractableEditor;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;

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
            .AddEntry("Edit_DefaultInfLengthClipLength", SettingLocalizedResources.Edit_DefaultInfLengthClipLength, GetSettingAs("Edit_DefaultInfLengthClipLength", 300, 300).ToString(), "300")
            .AddPicker("Edit_DefaultTransformRenderOrder", SettingLocalizedResources.Edit_DefaultTransformRenderOrder,
                TransformOrderStringMapping.Keys.ToArray(), TransformOrderStringMapping.First(p => p.Value == TransformServices.DefaultRenderOrder.ToString()).Key)
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
                case "Edit_DefaultTransformRenderOrder":
                    var order = TransformOrderStringMapping.GetValueOrDefault(args.Value?.ToString() ?? "", nameof(TransformRenderOrder.AfterEffects));
                    WriteSetting(args.Id, order);
                    LogDiagnostic($"Default transform render order changed to {order}.");
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
