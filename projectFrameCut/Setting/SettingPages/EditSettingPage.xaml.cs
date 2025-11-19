namespace projectFrameCut.Setting.SettingPages;

public partial class EditSettingPage : ContentPage
{
	public PropertyPanel.PropertyPanelBuilder rootPPB;

	public EditSettingPage()
	{
		rootPPB = new();
		rootPPB.AddText("");

		Content = rootPPB.Build();
	}
}