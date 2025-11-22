namespace projectFrameCut.Setting.SettingPages;

using static SettingManager.SettingsManager;

public partial class EditSettingPage : ContentPage
{
	public PropertyPanel.PropertyPanelBuilder rootPPB;

	public EditSettingPage()
	{
        Title = Localized.MainSettingsPage_Tab_Edit;

        rootPPB = new();
		rootPPB.AddText("");

		Content = rootPPB.Build();
	}
}