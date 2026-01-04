using projectFrameCut.PropertyPanel;
using projectFrameCut.Services;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Shared;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
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
            BackgroundColor = Colors.Black,
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
        .AddSwitch("UseSystemFont", SettingLocalizedResources.Advanced_UseSystemFont, SettingsManager.IsBoolSettingTrue("UseSystemFont"))
        .AddSeparator()
        .AddText(SettingLocalizedResources.Advanced_ExportPlugin)
        .AddPicker("exportPlugin", SettingLocalizedResources.Advanced_ExportPlugin_Select, projectFrameCut.Render.Plugin.PluginManager.LoadedPlugins.Select(c => c.Key).ToArray(), "")
        .AddSeparator()
        .AddButton(SettingLocalizedResources.Advanced_ResetUserID, async (s, e) =>
        {
            if (!await DisplayAlertAsync(Title, "Are you sure?", Localized._OK, Localized._Cancel)) return;
            SettingsManager.Settings.TryRemove("UserID", out _);
            await MainSettingsPage.RebootApp(this);
        })
        .ListenToChanges(async (e) =>
        {
            if (e.Id == "exportPlugin")
            {
                var pluginID = e.Value?.ToString();
                if (!string.IsNullOrEmpty(pluginID))
                {
                    var failReason = "";
                    try
                    {
                        var pluginRoot = Path.Combine(MauiProgram.BasicDataPath, "Plugins", pluginID);
                        if (Directory.Exists(pluginRoot))
                        {
                            var pluginPem = await SecureStorage.Default.GetAsync($"plugin_pem_{pluginID}");
                            if (string.IsNullOrEmpty(pluginPem))
                            {
                                throw new FileNotFoundException("Plugin PEM not found in secure storage", pluginID);
                            }

                            if (!File.Exists(Path.Combine(pluginRoot, pluginID + ".dll.enc")) || !File.Exists(Path.Combine(pluginRoot, pluginID + ".dll.sig")) || !File.Exists(Path.Combine(pluginRoot, "hashtable.json.enc")))
                            {
                                string? localizedPluginBrokenReason = null;
                                try
                                {
                                    localizedPluginBrokenReason = SettingsManager.SettingLocalizedResources.Plugin_FileMissing;
                                }
                                catch { }
                                failReason = localizedPluginBrokenReason ?? "Some of the plugin files are missing. Try reinstall it.";
                            }

                            var pemHash = HashServices.ComputeStringHash(pluginPem ?? string.Empty, SHA512.Create());
                            var pluginEnc = File.ReadAllBytes(Path.Combine(pluginRoot, pluginID + ".dll.enc"));
                            var htbEnc = File.ReadAllBytes(Path.Combine(pluginRoot, "hashtable.json.enc"));
                            var decBytes = FileCryptoService.DecryptToFileWithPassword(pemHash, pluginEnc);
                            var savePath = Path.Combine(FileSystem.CacheDirectory, $"{pluginID}.dll");
                            await File.WriteAllBytesAsync(savePath, decBytes, default);
                            await Share.RequestAsync(new ShareFileRequest()
                            {
                                File = new ShareFile(savePath),
                                Title = $"assembly for {pluginID}",
                            });
                            return;
                        }
                        else
                        {
                            string? localizedPluginBrokenReason = null;
                            try
                            {
                                localizedPluginBrokenReason = SettingsManager.SettingLocalizedResources.Plugin_FileMissing_DirectoryNotFound;
                            }
                            catch { }
                            failReason = localizedPluginBrokenReason ?? "Plugin file not found.";
                        }
                    }
                    catch (ReflectionTypeLoadException)
                    {
                        string? localizedFailReason = null;
                        try
                        {
                            localizedFailReason = SettingsManager.SettingLocalizedResources.Plugin_VersionMismatch;
                        }
                        catch { }
                        failReason = localizedFailReason ?? "plugin may be not up-to-date with the base API inside projectFrameCut. Try upgrade it.";
                    }

                    catch (Exception ex)
                    {
                        string? localizedPluginBrokenReason = null;
                        try
                        {
                            localizedPluginBrokenReason = Localized._ExceptionTemplate(ex);
                        }
                        catch { }
                        failReason = localizedPluginBrokenReason ?? $"An unhandled {ex.GetType().Name} exception occurs when trying to load plugin.\r\n({ex.Message})";
                    }
                    await DisplayAlertAsync(Localized._Error, $"failed\r\n({failReason ?? "unknown"})", Localized._OK);
                }
                return;
            }
            else
            {
                SettingsManager.WriteSetting(e.Id, e.Value?.ToString());
                await MainSettingsPage.RebootApp(this);
            }




        });

        Content = ppb.BuildWithScrollView();
    }
}