namespace projectFrameCut.Setting.SettingPages;
using projectFrameCut.ApplicationAPIBase.PropertyPanelBuilders;
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
        { SettingLocalizedResources.Edit_PreferredPopupMode_FixedView, "fixedview" },
    };
    public readonly Dictionary<string, string> ProxyStringMapping = new Dictionary<string, string>
    {
        { SettingLocalizedResources.Edit_ProxyOption_Ask, "ask" },
        { SettingLocalizedResources.Edit_ProxyOption_Always, "always" },
        { SettingLocalizedResources.Edit_ProxyOption_Never, "never" },

    };

    static string[] resolutions = ["640x480", "1280x720", "1920x1080", "2560x1440", "3840x2160"];


    public EditSettingPage()
    {
        BuildPPB();
    }

    void BuildPPB()
    {
        Title = Localized.MainSettingsPage_Tab_Edit;

        rootPPB = new();
        rootPPB.AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Edit_EditorPreference, SettingLocalizedResources.Edit_EditorPreference_Subtitle, 20, 12))
            .AddPicker("Edit_PreferredPopupMode",
                SettingLocalizedResources.Edit_PreferredPopupMode, ModeStringMapping.Keys.ToArray(),
                ModeStringMapping.FirstOrDefault(k => k.Value == GetSetting("Edit_PreferredPopupMode", "right"), new KeyValuePair<string, string>(SettingLocalizedResources.Edit_PreferredPopupMode_Right, "right")).Key)
            .AddSwitch("Edit_UpperContentHeight_AutoSave", SettingLocalizedResources.Edit_UpperContentHeight_AutoSave, IsBoolSettingTrue("Edit_UpperContentHeight_AutoSave"), null)
            .AppendWhen(!IsBoolSettingTrue("Edit_UpperContentHeight_AutoSave"), p => p.AddEntry("Edit_UpperContentHeight", SettingLocalizedResources.Edit_UpperContentHeight, GetSetting("Edit_UpperContentHeight", "250"), "250"))
            .AddEntry("Edit_MaximumSaveSlot", SettingLocalizedResources.Edit_MaxiumSaveSlot, GetSetting("Edit_MaximumSaveSlot", "10"), "10")
            .AddSeparator()
            .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Edit_PreviewOption, SettingLocalizedResources.Edit_PreviewOption_Subtitle))
            .AddPicker("Edit_LiveVideoPreviewDefaultResolution", SettingLocalizedResources.Edit_LiveVideoPreviewDefaultResolution, resolutions, GetSetting("Edit_LiveVideoPreviewDefaultResolution", "1280x720"), null)
            .AddEntry("Edit_LiveVideoPreviewBufferLength", SettingLocalizedResources.Edit_LiveVideoPreviewBufferLength, GetSetting("Edit_LiveVideoPreviewBufferLength", "240"), "240")
            .AddEntry("Edit_LiveVideoPreviewZoomFactor", SettingLocalizedResources.Edit_LiveVideoPreviewZoomFactor, GetSetting("Edit_LiveVideoPreviewZoomFactor", "8"), "8")
            .AddSeparator()
            .AddText(new SingleLineLabel(SettingLocalizedResources.Edit_MiscOption,20,FontAttributes.Bold))
            .AddPicker("Edit_ProxyOption", SettingLocalizedResources.Edit_ProxyOption, ProxyStringMapping.Keys.ToArray(), ProxyStringMapping.FirstOrDefault(k => k.Value == GetSetting("Edit_ProxyOption", "ask"), new KeyValuePair<string, string>(SettingLocalizedResources.Edit_ProxyOption_Ask, "ask")).Key, null)
            .AddSwitch("Edit_Denoise", SettingLocalizedResources.Edit_Denoise, IsBoolSettingTrue("Edit_Denoise"), null)
#if WINDOWS || MACCATALYST
            .AddSwitch("Edit_AlwaysShowToolbarButtons", SettingLocalizedResources.Edit_AlwaysShowToolbarButtons, IsBoolSettingTrue("Edit_AlwaysShowToolbarButtons"), null)
#endif
            ;


        Content = rootPPB.ListenToChanges(SettingInvoker).BuildWithScrollView();
    }

    private async void SettingInvoker(PropertyPanelPropertyChangedEventArgs args)
    {
        bool needReboot = false;
        try
        {

            if (args.Value != null)
            {
                WriteSetting(args.Id, args.Value?.ToString() ?? "");
            }
            switch (args.Id)
            {
                case "Edit_PreferredPopupMode":
                    {
                        var mode = ModeStringMapping.FirstOrDefault(k => k.Key == args.Value,
                                                 new KeyValuePair<string, string>("right", "right")).Value;
                        WriteSetting("Edit_PreferredPopupMode", mode);
                        return;
                    }
                case "Edit_ProxyOption":
                    {
                        var mode = ProxyStringMapping.FirstOrDefault(k => k.Key == args.Value,
                                                 new KeyValuePair<string, string>("ask", "ask")).Value;
                        WriteSetting("Edit_ProxyOption", mode);
                        return;
                    }

                case "Edit_UpperContentHeight_AutoSave":
                    BuildPPB();
                    break;
            }




        }
        catch (Exception ex)
        {
            // 处理异常并通知用户
            await DisplayAlert(Localized._Warn, Localized._ExceptionTemplate(ex), Localized._OK);
        }
    }
}