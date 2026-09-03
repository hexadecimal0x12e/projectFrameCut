using FFmpeg.AutoGen;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Services;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Shared;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using static projectFrameCut.Setting.SettingManager.SettingsManager;
using IPicture = projectFrameCut.Drawing.Base.IPicture;

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
        string[] codecs = ["Unknown"];
        string ffVersion = "unknown", ffArgs = "unknown";
        try
        {
            codecs = FFmpegHelper.CodecUtils
                .GetCodecsByType(FFmpeg.AutoGen.AVMediaType.AVMEDIA_TYPE_VIDEO, true)
                .Select(c => c.Name)
                .Order()
                .ToArray();
            ffVersion = $"FFmpeg {ffmpeg.av_version_info()}, {ffmpeg.avcodec_license()}";
            ffArgs = ffmpeg.avcodec_configuration();
        }
        catch (Exception ex)
        {
            Log(ex, "get codec list", this);
        }
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
                await DisplayAlertAsync("Error", "Key and Value cannot be empty.", "OK");
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
        .AppendWhen(SettingsManager.IsBoolSettingTrue("DeveloperMode"),
            c => c.AddText(SettingLocalizedResources.Advanced_ManualEditSetting)
                  .AddCustomChild(layout)
                  .AddSeparator()
                  .AddSwitch("DeveloperMode", SettingLocalizedResources.Advanced_DeveloperMode, SettingsManager.IsBoolSettingTrue("DeveloperMode"))
                  .AddSeparator())
        .AddText(SettingLocalizedResources.Advanced_Logging, fontSize: 20)
        .AddSwitch("LogUIMessageToLogger", SettingLocalizedResources.Advanced_LogUIMessageToLogger, SettingsManager.IsBoolSettingTrue("LogUIMessageToLogger"))
        .AddSwitch("DedicatedLogWindow", SettingLocalizedResources.Advanced_DedicatedLogWindow, SettingsManager.IsBoolSettingTrue("DedicatedLogWindow"))
        .AddSeparator()

        .AddText(SettingLocalizedResources.Advanced_Recover, fontSize: 20)
        .AddSwitch("DontPanicOnUnhandledException", SettingLocalizedResources.Advanced_DontPanicOnUnhandledException, SettingsManager.IsBoolSettingTrue("DontPanicOnUnhandledException"))
        .AddSwitch("AutoRecoverDraft", SettingLocalizedResources.Advanced_AutoRecoverDraft, SettingsManager.IsBoolSettingTrue("AutoRecoverDraft"))
        .AddSeparator()

        .AddText(Localized.AppShell_ProjectsTab, fontSize: 20)
        .AddSwitch("edit_ShowAllEffects", SettingLocalizedResources.Edit_ShowAllEffects, SettingsManager.IsBoolSettingTrue("edit_ShowAllEffects"), null)
        .AddSwitch("edit_IgnoreEffectsTargetInEffectTab", SettingLocalizedResources.Edit_IgnoreEffectsTargetInEffectTab, SettingsManager.IsBoolSettingTrue("edit_IgnoreEffectsTargetInEffectTab"), null)
        .AddSwitch("Edit_UseCommunityToolkitPopupInsteadOfOverlayLayer", SettingLocalizedResources.Edit_UseCommunityToolkitPopupInsteadOfOverlayLayer, SettingsManager.IsBoolSettingTrue("Edit_UseCommunityToolkitPopupInsteadOfOverlayLayer"), null)
        .AddSwitch("render_ForceDirectRenderTransport", SettingLocalizedResources.Render_ForceDirectRenderTransport, SettingsManager.IsBoolSettingTrue("render_ForceDirectRenderTransport"), null)
        .AddSwitch("render_RpcServerEnableHttp", SettingLocalizedResources.Render_RpcServerEnableHttp, SettingsManager.IsBoolSettingTrue("render_RpcServerEnableHttp"), null)
        .AddEntry("render_RpcServerHttpPort", SettingLocalizedResources.Render_RpcServerHttpPort, GetSetting("render_RpcServerHttpPort", ""), "39485")
        .AddSeparator()

        .AddText("IPicture", fontSize: 20)
        .AddSwitch("diag_TraceIPictureObject", SettingLocalizedResources.Advanced_TraceIPictureObject, SettingsManager.IsBoolSettingTrue("diag_TraceIPictureObject"))
        .AddSwitch("render_DisallowPictureModeDowngrade", SettingLocalizedResources.Render_DisallowPictureModeDowngrade, IsBoolSettingTrue("render_DisallowPictureModeDowngrade"), null)
        .AddSeparator()
        
        .AddText(Localized.MainSettingsPage_Tab_Render, fontSize: 20)
        .AddSwitch("render_SaveCheckpoint", SettingLocalizedResources.Render_SaveCheckpoint, IsBoolSettingTrue("render_SaveCheckpoint"), null)
        .AddSwitch("render_DumpDiagData", SettingLocalizedResources.Render_DumpDiagData, IsBoolSettingTrue("render_DumpDiagData"), null)
        .AddSeparator()

        .AddText(SettingLocalizedResources.Advanced_TextAndGlobalization, fontSize: 20)
        .AddPicker("OverrideCulture", SettingLocalizedResources.General_Language_OverrideCulture, overrideOpts.Values.ToArray(), overrideOpts.TryGetValue(GetSetting("OverrideCulture", "default"), out var k) ? k : "", null)
        .AddSwitch("UseSystemFont", SettingLocalizedResources.Advanced_UseSystemFont, SettingsManager.IsBoolSettingTrue("UseSystemFont"))
        .AddSwitch("diag_TypesettingEngineDiagMode", SettingLocalizedResources.Advanced_TypesettingEngineDiagMode, IsBoolSettingTrue("diag_TypesettingEngineDiagMode"), null)
        .AddSeparator()

        .AddText("UI", fontSize: 20)
        .AddSwitch("ui_ForceUseShell", SettingLocalizedResources.Advanced_UseMAUIShell, SettingsManager.IsBoolSettingTrue("ui_ForceUseShell"))
        .AddSwitch("ui_ShowWelcomePage", SettingLocalizedResources.Advanced_ShowWelcomePage, SettingsManager.IsBoolSettingTrue("ui_ShowWelcomePage"))
        .AddSeparator()

        .AddText(SettingLocalizedResources.GeneralCodec_Title, fontSize: 20)
        .AddPicker("codecs", SettingLocalizedResources.Advanced_TestCodec, codecs, "", null)
        .AddSeparator()

        .AddText(SettingLocalizedResources.Advanced_ExportPlugin, fontSize: 20)
        .AddPicker("exportPlugin", SettingLocalizedResources.Advanced_ExportPlugin_Select, projectFrameCut.Render.Plugin.PluginManager.LoadedPlugins.Select(c => c.Key).ToArray(), "Pick a plugin here")
        .AddSeparator()
        .AddEntry("argsFromEnv", "Environment.GetCommandLineArgs()", string.Join(',', Environment.GetCommandLineArgs()), "", c => c.IsReadOnly = true)
        .AddEntry("argsParsed", "MauiProgram.CmdlineArgs", string.Join(',', MauiProgram.CmdlineArgs), "", c => c.IsReadOnly = true)
        .AddSeparator()
        .AddText(SettingLocalizedResources.General_UserData, fontSize: 20)
        .AddButton(SettingLocalizedResources.Diag_OpenBaseData, async (s, e) =>
        {
            await FileSystemService.OpenFolderAsync(MauiProgram.BasicDataPath);
        })
        .AddButton(SettingLocalizedResources.Misc_OpenSettingsJson, async (s, e) =>
        {
            var jsonPath = Path.Combine(MauiProgram.BasicDataPath, "settings.json");
            await FileSystemService.OpenFileAsync(jsonPath);
        })
        .AddSeparator()
        .AddText(new SingleLineLabel(SettingLocalizedResources.Advanced_Reset, 20))
        .AddButton(SettingLocalizedResources.Advanced_ShowWelcomePage, async (_, _) => await Navigation.PushAsync(new SetupPage()))
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

                        DraftImportAndExportHelper.EnsureProjectDirectoryShellIntegration(projectDir);
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
            ToggleSaveSignal();
            await MainSettingsPage.RebootApp(this);
        })
        .AddButton(SettingLocalizedResources.Advanced_ClearPrefs, async (s, e) =>
        {
            if (!await DisplayAlertAsync(Title, SettingLocalizedResources.Advanced_AreYouSure, Localized._OK, Localized._Cancel)) return;
            if (await DisplayPromptAsync(Title, SettingLocalizedResources.Advanced_ClearPrefs_Warn2, Localized._OK, Localized._Cancel) != "ok") return;
            Preferences.Clear();
        },
        (b) =>
        {
            b.BackgroundColor = Color.FromRgba("FF9999FF");
            b.TextColor = Colors.Black;
        })
        .ListenToChanges(async (e) =>
        {
            switch (e.Id)
            {
                case "exportPlugin":
                    {
                        var pluginID = e.Value?.ToString();
                        if (!string.IsNullOrEmpty(pluginID))
                        {
                            var failReason = "";
                            try
                            {
                                var assemblyBytes = await PluginService.ExportVerifiedAssemblyAsync(pluginID);
                                var savePath = Path.Combine(FileSystem.CacheDirectory, $"{pluginID}.dll");
                                await File.WriteAllBytesAsync(savePath, assemblyBytes, default);
                                await Share.RequestAsync(new ShareFileRequest
                                {
                                    File = new ShareFile(savePath),
                                    Title = $"assembly for {pluginID}",
                                });
                                return;
                            }
                            catch (Exception ex)
                            {
                                failReason = ex.Message;
                            }
                            await DisplayAlertAsync(Localized._Error, $"failed\r\n({failReason ?? "unknown"})", Localized._OK);
                        }
                        return;
                    }
                case "OverrideCulture":
                    var DispName = e.Value?.ToString() ?? "default";
                    if (DispName == SettingLocalizedResources.General_Language_OverrideCulture_DontOverride)
                    {
                        Settings.Remove("OverrideCulture", out _);
                        ToggleSaveSignal();
                    }
                    else
                    {
                        try
                        {
                            var overrideLocate = overrideOpts.ReverseLookup(DispName);
                            WriteSetting("OverrideCulture", overrideLocate);
                        }
                        catch { }
                    }

                    await MainSettingsPage.RebootApp(this);
                    break;
                case "codecs":
                    {
                        var cid = e.Value?.ToString();
                        if (!string.IsNullOrEmpty(cid))
                        {
                            try
                            {
                                var writer = PluginManager.CreateVideoWriter(cid);
                                await DisplayAlertAsync(Localized._Info, $"Successfully create video writer with codec {writer.CodecName} ({cid}).", Localized._OK);
                            }
                            catch (Exception ex)
                            {
                                await DisplayAlertAsync(Localized._Error, $"Failed to create video writer with codec {cid}.\r\n({Localized._ExceptionTemplate(ex)})", Localized._OK); return;
                            }
                        }
                        break;
                    }
                case "render_SaveCheckpoint":
                    if (e.Value is bool b && b)
                    {
                        WriteSetting("render_SaveCheckpoint", "true");
                        Directory.CreateDirectory(Path.Combine(MauiProgram.DataPath, "RenderCheckpoint"));
                    }
                    else
                    {
                        WriteSetting("render_SaveCheckpoint", "false");
                    }
                    break;
                default:
                    SettingsManager.WriteSetting(e.Id, e.Value?.ToString());
                    await MainSettingsPage.RebootApp(this);
                    break;
            }




        });

        Content = ppb.BuildWithScrollView();
    }
}
