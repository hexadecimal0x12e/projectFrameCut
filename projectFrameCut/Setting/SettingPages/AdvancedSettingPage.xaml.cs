using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
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
    private Dictionary<string, string> overrideOpts;

    public AdvancedSettingPage()
    {
        overrideOpts = new Dictionary<string, string>
        {
            {"default", SettingLocalizedResources.General_Language_OverrideCulture_DontOverride},
            {"zh-CN", SettingLocalizedResources.General_Language_OverrideCulture_OverrideTo
                    (__ISimpleLocalizerBase_zh_CN__._LocateDisplayName) },
            {"ja-JP", SettingLocalizedResources.General_Language_OverrideCulture_OverrideTo
                    (__ISimpleLocalizerBase_ja_JP__._LocateDisplayName) },
            {"ko-KR", SettingLocalizedResources.General_Language_OverrideCulture_OverrideTo
                    (__ISimpleLocalizerBase_ko_KR__._LocateDisplayName) },
        };
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
        .AddSwitch("ui_ForceUseShell", SettingLocalizedResources.Advanced_UseMAUIShell, SettingsManager.IsBoolSettingTrue("ui_ForceUseShell"))
        .AddSeparator()
        .AddSwitch("edit_ShowAllEffects", SettingLocalizedResources.Edit_ShowAllEffects, SettingsManager.IsBoolSettingTrue("edit_ShowAllEffects"), null)
        .AddPicker("OverrideCulture", SettingLocalizedResources.General_Language_OverrideCulture, overrideOpts.Values.ToArray(), overrideOpts.ReverseLookup(GetSetting("OverrideCulture", "default")), null)
        .AddSeparator()
        .AddText(SettingLocalizedResources.Advanced_ExportPlugin, fontSize: 20)
        .AddPicker("exportPlugin", SettingLocalizedResources.Advanced_ExportPlugin_Select, projectFrameCut.Render.Plugin.PluginManager.LoadedPlugins.Select(c => c.Key).ToArray(), "Pick a plugin here")
        .AddSeparator()
        .AddButton(SettingLocalizedResources.Diag_OpenBaseData, async (s, e) =>
        {
            await FileSystemService.OpenFolderAsync(MauiProgram.BasicDataPath);
        })
        .AddButton(SettingLocalizedResources.Misc_OpenSettingsJson, async (s, e) =>
        {
            var jsonPath = Path.Combine(MauiProgram.BasicDataPath, "settings.json");
            await FileSystemService.OpenFileAsync(jsonPath);
        })
        .AddText(new SingleLineLabel(SettingLocalizedResources.Advanced_Reset, 25))
        .AddButton(SettingLocalizedResources.Advanced_FixDraft, async (s, e) =>
        {
            if (!await DisplayAlertAsync(Title, SettingLocalizedResources.Advanced_FixDraft_Info, Localized._OK, Localized._Cancel)) return;

            try
            {
                int fixedCount = 0;
                int errorCount = 0;
                var draftsPath = Path.Combine(MauiProgram.DataPath, "My Drafts");

                if (!Directory.Exists(draftsPath))
                {
                    await DisplayAlertAsync(Localized._Info, "No drafts found", Localized._OK);
                    return;
                }

                foreach (var projectDir in Directory.GetDirectories(draftsPath, "*"))
                {
                    try
                    {
                        // 修复 project.json
                        var projectFile = Path.Combine(projectDir, "project.json");
                        if (File.Exists(projectFile))
                        {
                            var jsonText = File.ReadAllText(projectFile);
                            var modified = false;

                            // 替换键名
                            if (jsonText.Contains("\"projectName\""))
                            {
                                jsonText = jsonText.Replace("\"projectName\"", "\"ProjectName\"");
                                modified = true;
                            }
                            if (jsonText.Contains("\"targetFrameRate\""))
                            {
                                jsonText = jsonText.Replace("\"targetFrameRate\"", "\"TargetFrameRate\"");
                                modified = true;
                            }

                            if (modified)
                            {
                                File.WriteAllText(Path.ChangeExtension(projectFile, "pjfc"), jsonText);
                                File.Delete(projectFile);
                                fixedCount++;
                            }
                        }

                        // 修复 timeline.json
                        var timelineFile = Path.Combine(projectDir, "timeline.json");
                        if (File.Exists(timelineFile))
                        {
                            var jsonText = File.ReadAllText(timelineFile);
                            var modified = false;

                            if (jsonText.Contains("\"targetFrameRate\""))
                            {
                                jsonText = jsonText.Replace("\"targetFrameRate\"", "\"TargetFrameRate\"");
                                modified = true;
                            }

                            if (modified)
                            {
                                File.WriteAllText(timelineFile, jsonText);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        errorCount++;
                        Log(ex, $"Fix project JSON in {projectDir}", this);
                    }
                }
                if (errorCount == 0)
                {
                    await DisplayAlertAsync(Localized._Info, SettingLocalizedResources.Advanced_Success, Localized._OK);

                }
                else
                {
                    await DisplayAlertAsync(Localized._Info, $"Fixed {fixedCount} but {errorCount} failed.", Localized._OK);

                }
            }
            catch (Exception ex)
            {
                await DisplayAlertAsync(Localized._Error, $"Fail to fix: {ex.Message}", Localized._OK);
            }
        })
        .AddButton(SettingLocalizedResources.Advanced_ResetEffectImplement, async (s, e) =>
        {
            if (!await DisplayAlertAsync(Title, SettingLocalizedResources.Advanced_AreYouSure, Localized._OK, Localized._Cancel)) return;
            EffectHelper.DefaultImplementsType.Clear();
            var json = JsonSerializer.Serialize(EffectHelper.DefaultImplementsType);
            File.WriteAllText(Path.Combine(MauiProgram.BasicDataPath, "EffectImplement.json"), json);
            await DisplayAlertAsync(Localized._Info, SettingLocalizedResources.Advanced_Success, Localized._OK);

        })
        .AddButton(SettingLocalizedResources.Advanced_ResetUserID, async (s, e) =>
        {
            if (!await DisplayAlertAsync(Title, SettingLocalizedResources.Advanced_AreYouSure, Localized._OK, Localized._Cancel)) return;
            Settings.TryRemove("UserID", out _);
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
                else if (e.Id == "OverrideCulture")
                {
                    var DispName = e.Value?.ToString() ?? "default";
                    if(DispName == SettingLocalizedResources.General_Language_OverrideCulture_DontOverride)
                    {
                        Settings.Remove("OverrideCulture", out _);
                        ToggleSaveSignal();
                    }
                    else
                    {
                        var overrideLocate = overrideOpts[DispName];
                        WriteSetting("OverrideCulture", overrideLocate);
                    }

                    await MainSettingsPage.RebootApp(this);

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