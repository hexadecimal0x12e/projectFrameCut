namespace projectFrameCut.Setting.SettingPages;

using projectFrameCut.PropertyPanel;
using System.Globalization;
using static SettingManager.SettingsManager;

public partial class EditSettingPage : ContentPage
{
    public PropertyPanel.PropertyPanelBuilder rootPPB;

    public readonly Dictionary<string, string> ModeStringMapping = new Dictionary<string, string>
    {
        { SettingLocalizedResources.Edit_PreferredPopupMode_Right, "right" },
        { SettingLocalizedResources.Edit_PreferredPopupMode_Bottom, "bottom" },
        { SettingLocalizedResources.Edit_PreferredPopupMode_Clip, "clip" },
        { SettingLocalizedResources.Edit_PreferredPopupMode_Window, "window" },
        { SettingLocalizedResources.Edit_PreferredPopupMode_FixedView, "fixedview" },
    };

    public EditSettingPage()
    {
        BuildPPB();
    }

    void BuildPPB()
    {
        Title = Localized.MainSettingsPage_Tab_Edit;

        rootPPB = new();
        rootPPB.AddText(new PropertyPanel.TitleAndDescriptionLineLabel(SettingLocalizedResources.Edit_EditorPreference, SettingLocalizedResources.Edit_EditorPreference_Subtitle, 20, 12))
            .AddPicker("Edit_PreferredPopupMode",
                SettingLocalizedResources.Edit_PreferredPopupMode, ModeStringMapping.Keys.ToArray(),
                ModeStringMapping.FirstOrDefault(k => k.Value == GetSetting("Edit_PreferredPopupMode", "right"),
                                                 new KeyValuePair<string, string>("right", "right")).Key
            )
            .AddEntry("Edit_MaximumSaveSlot", SettingLocalizedResources.Edit_MaxiumSaveSlot, GetSetting("Edit_MaximumSaveSlot", "10"), "10")
            ;

        rootPPB.ListenToChanges(SettingInvoker);

        Content = new ScrollView { Content = rootPPB.Build() };
    }

    private async void SettingInvoker(PropertyPanelPropertyChangedEventArgs args)
    {
        bool needReboot = false;
        try
        {
            switch (args.Id)
            {

                case "Edit_PreferredPopupMode":
                    {   
                        var mode = ModeStringMapping.FirstOrDefault(k => k.Key == args.Value,
                                                 new KeyValuePair<string, string>("right", "right")).Value;
                        WriteSetting("Edit_PreferredPopupMode", mode);
                        goto done;
                    }
            }


            if (args.Value != null)
            {
                WriteSetting(args.Id, args.Value?.ToString() ?? "");
            }

        done:


            BuildPPB();
        }
        catch (Exception ex)
        {
            // 处理异常并通知用户
            await DisplayAlert(Localized._Warn, Localized._ExceptionTemplate(ex), Localized._OK);
        }
    }
}