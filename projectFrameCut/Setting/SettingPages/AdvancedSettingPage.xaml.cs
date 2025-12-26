using projectFrameCut.Setting.SettingManager;
using projectFrameCut.PropertyPanel;
using System.Threading.Tasks;
using static projectFrameCut.Setting.SettingManager.SettingsManager;

namespace projectFrameCut.Setting.SettingPages;

public partial class AdvancedSettingPage : ContentPage
{

    public AdvancedSettingPage()
    {
        BuildPPB();
    }

    void BuildPPB()
    {
        Title = Localized.MainSettingsPage_Tab_Advanced;
        var layout = new HorizontalStackLayout();
        var keyEntry = new Entry { Placeholder = "Key", MinimumWidthRequest = 200 };
        var valueEntry = new Entry { Placeholder = SettingLocalizedResources.Advanced_KeyBox_Hint, MinimumWidthRequest = 250, Margin = new Thickness(10, 0, 0, 0) };
        var saveBtn = new Button { Text = Localized._Save, Margin = new Thickness(10, 0, 0, 0) };
        var deleteBtn = new Button { Text = Localized._Remove, Margin = new Thickness(10, 0, 0, 0) };

        keyEntry.TextChanged += (s, e) =>
        {
            if (string.IsNullOrWhiteSpace(keyEntry.Text))
            {
                valueEntry.Text = "";
                valueEntry.Placeholder = SettingLocalizedResources.Advanced_KeyBox_Hint;
                return;
            }
            if (SettingsManager.IsSettingExists(keyEntry.Text))
            {
                valueEntry.Text = SettingsManager.GetSetting(keyEntry.Text);
            }
            else
            {
                valueEntry.Text = string.Empty;
                valueEntry.Placeholder = SettingLocalizedResources.Advanced_KeyNotFound;
            }
        };

        saveBtn.Clicked += async (s, e) =>
        {
            if (!string.IsNullOrWhiteSpace(keyEntry.Text) && !string.IsNullOrWhiteSpace(valueEntry.Text))
            {
                SettingsManager.WriteSetting(keyEntry.Text.Trim(), valueEntry.Text.Trim());
                await DisplayAlertAsync(Localized._Info, SettingLocalizedResources.Advanced_Success, Localized._OK);
            }
            else
            {
                await DisplayAlert("Error", "Key and Value cannot be empty.", "OK");
            }
        };

        deleteBtn.Clicked += async (s, e) =>
        {
            if (!string.IsNullOrWhiteSpace(keyEntry.Text))
            {
                if (SettingsManager.Settings.Remove(keyEntry.Text.Trim(), out _))
                {
                    valueEntry.Text = string.Empty;
                    SettingsManager.ToggleSaveSignal();
                    await DisplayAlertAsync(Localized._Info, SettingLocalizedResources.Advanced_Success, Localized._OK);
                }
            }
        };

        layout.Children.Add(keyEntry);
        layout.Children.Add(valueEntry);
        layout.Children.Add(saveBtn);
        layout.Children.Add(deleteBtn);
        var ppb = new PropertyPanelBuilder();

        ppb
        .AddText(new Label
        {
            Text = SettingLocalizedResources.Advanced_WarnLabel,
            TextColor = Colors.Yellow,
            FontSize = 32,
            FontAttributes = FontAttributes.Bold,
        })
        .AddSeparator()
        .AddText(SettingLocalizedResources.Advanced_ManualEditSetting)
        .AddCustomChild(layout)
        .AddSeparator()
        .AddSwitch("DeveloperMode", SettingLocalizedResources.Advanced_DeveloperMode, SettingsManager.IsBoolSettingTrue("DeveloperMode"))
        .AddSwitch("AutoRecoverDraft", SettingLocalizedResources.Advanced_AutoRecoverDraft, SettingsManager.IsBoolSettingTrue("AutoRecoverDraft"))
        .AddSwitch("DontPanicOnUnhandledException", SettingLocalizedResources.Advanced_DontPanicOnUnhandledException, SettingsManager.IsBoolSettingTrue("DontPanicOnUnhandledException"))
        .AddSwitch("DedicatedLogWindow", SettingLocalizedResources.Advanced_DedicatedLogWindow, SettingsManager.IsBoolSettingTrue("DedicatedLogWindow"))
        .AddSwitch("LogUIMessageToLogger", SettingLocalizedResources.Advanced_LogUIMessageToLogger, SettingsManager.IsBoolSettingTrue("LogUIMessageToLogger"))
        .AddSeparator()
        .AddButton(SettingLocalizedResources.Advanced_ResetUserID, async (s,e) =>
        {
            if (!await DisplayAlertAsync(Title, "Are you sure?", Localized._OK, Localized._Cancel)) return;
            SettingsManager.Settings.TryRemove("UserID", out _);
            await MainSettingsPage.RebootApp(this);
        })
        .ListenToChanges(async (e) =>
        {
            SettingsManager.WriteSetting(e.Id, e.Value?.ToString());
            await MainSettingsPage.RebootApp(this);


        });

        Content = ppb.BuildWithScrollView();
    }
}