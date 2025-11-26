using projectFrameCut.Setting.SettingManager;
using System.Threading.Tasks;

namespace projectFrameCut.Setting.SettingPages;

public partial class DebugSettingPage : ContentPage
{
    public DebugSettingPage()
    {
        InitializeComponent();
        LogDiagnostics.IsToggled = SettingManager.SettingsManager.IsBoolSettingTrue("LogDiagnostics");
        DontPanicOnUnhandledException.IsToggled = SettingManager.SettingsManager.IsBoolSettingTrue("DontPanicOnUnhandledException");
        AutoRecoverDraft.IsToggled = SettingManager.SettingsManager.IsBoolSettingTrue("AutoRecoverDraft");
    }

    private void KeyEntry_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(KeyEntry.Text))
        {
            ValueEntry.Text = "";
            ValueEntry.Placeholder = "Type a key, and you'll see the value.";
            return;
        }
        if (SettingManager.SettingsManager.IsSettingExists(KeyEntry.Text))
        {
            ValueEntry.Text = SettingsManager.GetSetting(KeyEntry.Text);
        }
        else
        {
            ValueEntry.Text = string.Empty;
            ValueEntry.Placeholder = "Key not found.";
        }
    }

    private async void SaveSettingBtn_Clicked(object sender, EventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(KeyEntry.Text) && !string.IsNullOrWhiteSpace(ValueEntry.Text))
        {
            SettingsManager.WriteSetting(KeyEntry.Text.Trim(), ValueEntry.Text.Trim());
            await DisplayAlertAsync("Success", "Setting saved.", "OK");
        }
        else
        {
            await DisplayAlertAsync("Error", "Key and Value cannot be empty.", "OK");
        }
    }

    private void OptionsChanged(object sender, ToggledEventArgs e)
    {
        SettingsManager.WriteSetting("LogDiagnostics", LogDiagnostics.IsToggled.ToString());
        SettingsManager.WriteSetting("DontPanicOnUnhandledException", DontPanicOnUnhandledException.IsToggled.ToString());
        SettingsManager.WriteSetting("AutoRecoverDraft", AutoRecoverDraft.IsToggled.ToString());
    }

    private async void DeleteSettingBtn_Clicked(object sender, EventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(KeyEntry.Text))
        {
            if (SettingsManager.Settings.Remove(KeyEntry.Text.Trim(), out _))
            {
                ValueEntry.Text = string.Empty;
                SettingsManager.ToggleSaveSignal();
                await DisplayAlertAsync("Success", "Setting deleted.", "OK");
            }
        }
    }
}