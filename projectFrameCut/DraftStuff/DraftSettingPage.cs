

using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationAPIBase.Views.TabbedView;
using projectFrameCut.Controls;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;


namespace projectFrameCut.DraftStuff;

public class DraftSettingPage
{
    private const string SaveSlotDirectoryName = "saveSlots";

    private sealed class SaveSlotHistoryItem
    {
        public int SlotIndex { get; init; }
        public DateTime SavedAt { get; init; }
        public string ChangeReason { get; init; } = string.Empty;
    }

    public TabbedView tabView;
    public DraftPage parent;

    public DraftSettingPage(DraftPage parent)
    {
        tabView = new();
        tabView.HeadersPanel.BackgroundColor = Colors.Transparent;
        this.parent = parent;
        Build();
    }

    public void Build()
    {
        tabView.TabItems.Add(new TabbedViewItem
        {
            Header = Localized.MainSettingsPage_Tab_General,
            Content = BuildGeneralTab()
        });
        tabView.TabItems.Add(new TabbedViewItem
        {
            Header = Localized.DraftSettingPage_Tab_History,
            Content = BuildHistoryTab()
        });
        tabView.TabItems.Add(new TabbedViewItem
        {
            Header = Localized.DraftSettingPage_Tab_Compatibility,
            Content = BuildCompatibilityTab()
        });
        tabView.TabItems.Add(new TabbedViewItem
        {
            Header = Localized.MainSettingsPage_Tab_Misc,
            Content = BuildAdvancedTab()
        });

    }

    public View BuildHistoryTab()
    {
        var history = ReadSaveSlotHistory();

        var root = new VerticalStackLayout
        {
            Spacing = 10,
            Padding = new Thickness(10)
        };

        root.Children.Add(new Label
        {
            Text = Localized.DraftSettingPage_Tab_History,
            FontSize = 24,
            FontAttributes = FontAttributes.Bold
        });

        if (history.Count == 0)
        {
            root.Children.Add(new Label
            {
                Text = Localized.DraftSettingPage_Tab_History_NotAvailable,
                FontSize = 25,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center
            });
            root.Children.Add(new Label
            {
                Text = Localized.DraftSettingPage_Tab_History_NotAvailable_Sub,
                FontSize = 13,
                Opacity = 0.75,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center
            });

            root.HorizontalOptions = LayoutOptions.Center;
            root.VerticalOptions = LayoutOptions.Center;
            return root;
        }

        foreach (var item in history.OrderByDescending(c => c.SavedAt))
        {
            bool isCurrent = item.SlotIndex == parent.CurrentSaveSlotIndex;
            string reason = string.IsNullOrWhiteSpace(item.ChangeReason) ? Localized.DraftSettingPage_Tab_History_UnknownOperation : item.ChangeReason.Trim();

            var slotLabel = new Label
            {
                Text = (isCurrent ? "*" : "") + reason,
                FontSize = 16,
                FontAttributes = isCurrent ? FontAttributes.Bold : FontAttributes.None,
                VerticalOptions = LayoutOptions.Center
            };


            var lastChangeLabel = new Label
            {
                VerticalOptions = LayoutOptions.Center,
                TextColor = Colors.White,
                FontSize = 12,
                Margin = new(0, 0, 8, 0),
                Text = DateTime.Now.Ticks - item.SavedAt.Ticks >= 0 ?
                       TimeSpan.FromTicks(DateTime.Now.Ticks - item.SavedAt.Ticks) switch
                       {
                           var t when t.TotalMinutes < 1 => Localized.DraftSettingPage_Tab_History_Now,
                           var t when t.TotalHours < 2 => Localized.DraftSettingPage_Tab_History_MinutesAgo(t.Minutes),
                           var t when t.TotalHours < 48 => Localized.DraftSettingPage_Tab_History_HoursAgo((int)t.TotalHours),
                           var t when t.TotalDays < 14 => Localized.DraftSettingPage_Tab_History_DaysAgo((int)t.TotalDays),
                           _ => Localized.DraftSettingPage_Tab_History_VeryLongAgo
                       }
                       : Localized.HomePage_LastChangedOnFuture
            };

            var applyButton = new Button
            {
                Text = Localized._Apply,
                IsEnabled = !isCurrent,
                HorizontalOptions = LayoutOptions.End,
                VerticalOptions = LayoutOptions.Center
            };

            int targetSlot = item.SlotIndex;
            applyButton.Clicked += (_, _) => ApplyHistorySlot(targetSlot);

            var titleRow = new Grid
            {
                ColumnDefinitions =
                [
                    new ColumnDefinition { Width = GridLength.Star },
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = GridLength.Auto }
                ]
            };
            titleRow.Children.Add(slotLabel);
            titleRow.Children.Add(lastChangeLabel);
            titleRow.Children.Add(applyButton);
            Grid.SetColumn(lastChangeLabel, 1);
            Grid.SetColumn(applyButton, 2);
            root.Add(titleRow);

        }

        return new ScrollView { Content = root };
    }

    private void ApplyHistorySlot(int slotIndex)
    {
        try
        {
            parent.SetStateBusy(Localized.DraftPage_ApplyingChanges);
            parent.ApplySlot(slotIndex);
            tabView.SelectedItem.Content = BuildHistoryTab();
        }
        catch (Exception ex)
        {
            parent.SetStateFail();
            parent.SetStatusText(Localized._ExceptionTemplate(ex));
        }
    }

    private List<SaveSlotHistoryItem> ReadSaveSlotHistory()
    {
        if (string.IsNullOrWhiteSpace(parent.WorkingPath))
        {
            return [];
        }

        string saveSlotsPath = System.IO.Path.Combine(parent.WorkingPath, SaveSlotDirectoryName);
        if (!System.IO.Directory.Exists(saveSlotsPath))
        {
            return [];
        }

        var result = new List<SaveSlotHistoryItem>();
        foreach (var slotPath in System.IO.Directory.GetDirectories(saveSlotsPath))
        {
            string slotName = System.IO.Path.GetFileName(slotPath);
            if (!TryParseSlotIndex(slotName, out int slotIndex))
            {
                continue;
            }

            string timelinePath = System.IO.Path.Combine(slotPath, "timeline.json");
            if (!System.IO.File.Exists(timelinePath))
            {
                continue;
            }

            try
            {
                string json = System.IO.File.ReadAllText(timelinePath);
                var draft = System.Text.Json.JsonSerializer.Deserialize<DraftStructureJSON>(json, DraftPage.DraftJSONOption);
                if (draft is null)
                {
                    continue;
                }

                result.Add(new SaveSlotHistoryItem
                {
                    SlotIndex = slotIndex,
                    SavedAt = draft.SavedAt,
                    ChangeReason = draft.ChangeReason
                });
            }
            catch
            {
                // Ignore broken slot files and continue loading other records.
            }
        }

        return result
            .OrderByDescending(i => i.SavedAt)
            .ThenBy(i => i.ChangeReason, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(i => i.SlotIndex)
            .ToList();
    }

    private static bool TryParseSlotIndex(string slotName, out int slotIndex)
    {
        slotIndex = -1;
        const string prefix = "slot_";

        if (string.IsNullOrWhiteSpace(slotName) || !slotName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return int.TryParse(slotName[prefix.Length..], out slotIndex);
    }

    private View BuildCompatibilityTab()
    {
        PropertyPanelBuilder ppb = new();
        ppb.AddButton(Localized.DraftSettingPage_Tab_Compatibility_UpgradePlaceResize, UpgradePlaceResizeButton_Clicked);
        return ppb.BuildWithScrollView();
    }

    private async void UpgradePlaceResizeButton_Clicked(object? sender, EventArgs e)
    {
        try
        {
            parent.SetStateBusy(Localized.DraftPage_ApplyingChanges);

            var clips = parent.Clips.Values
                .Where(c => !c.Id.StartsWith("ghost_", StringComparison.Ordinal) && !c.Id.StartsWith("shadow_", StringComparison.Ordinal))
                .ToList();

            int migratedCount = 0;
            int failedCount = 0;

            foreach (var clip in clips)
            {
                try
                {
                    var dto = DraftImportAndExportHelper.ExportClipElementFromDraftPage(parent, clip, wrapSoundtrackAsClip: false);
                    if (dto == null || !HasLegacyPlaceResizeEffects(dto))
                    {
                        continue;
                    }

                    DraftImportAndExportHelper.MigrateLegacyPlaceResizeToTargetRect(dto, parent.ProjectInfo);

                    clip.TargetWidth = dto.TargetWidth;
                    clip.TargetHeight = dto.TargetHeight;
                    clip.TargetX = dto.TargetX;
                    clip.TargetY = dto.TargetY;
                    clip.ExtraData = dto.MetaData?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, object>();
                    clip.Effects = dto.Effects?.ToDictionary(
                        effect => string.IsNullOrWhiteSpace(effect.Name) ? $"Effect-{Guid.NewGuid()}" : effect.Name,
                        effect => PluginManager.CreateEffect(effect, parent.ProjectInfo.RelativeWidth, parent.ProjectInfo.RelativeHeight))
                        ?? new Dictionary<string, IEffect>();

                    migratedCount++;
                }
                catch
                {
                    failedCount++;
                }
            }

            if (migratedCount > 0)
            {
                var draft = DraftImportAndExportHelper.ExportFromDraftPage(parent, includeUiOnlyClips: false);
                parent.ProjectDuration = Math.Max(draft.Duration, draft.AudioDuration);

                await parent.ClipEditor.UpdateClips(parent.Clips);
                parent.ClipEditor.SetCurrentFrame((uint)Math.Max(0, parent.CurrentFrame));
                await parent.previewer.UpdateDraft(draft);
                await parent.DynamicPreviewProvider.UpdateDraft(draft);
                await parent.Save();
            }

            if (failedCount > 0)
            {
                parent.SetStateFail();
                parent.SetStatusText(Localized.DraftSettingPage_Tab_Compatibility_UpgradePlaceResize_OK(migratedCount, failedCount));
                return;
            }

            if (migratedCount == 0)
            {
                parent.SetStateOK(Localized.DraftSettingPage_Tab_Compatibility_UpgradePlaceResize_OK(-1, 0));
                return;
            }

            parent.SetStateOK(Localized.DraftSettingPage_Tab_Compatibility_UpgradePlaceResize_OK(migratedCount, 0));
        }
        catch (Exception ex)
        {
            parent.SetStateFail();
            parent.SetStatusText(Localized._ExceptionTemplate(ex));
        }
    }

    private static bool HasLegacyPlaceResizeEffects(ClipDraftDTO dto)
        => dto.Effects?.Any(IsLegacyPlaceOrResizeEffect) == true;

    private static bool IsLegacyPlaceOrResizeEffect(EffectAndMixtureJSONStructure effect)
    {
        if (string.Equals(effect.Name, "__Internal_Place__", StringComparison.Ordinal)
            || string.Equals(effect.Name, "__Internal_Resize__", StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(effect.Name))
        {
            return false;
        }

        return string.Equals(effect.TypeName, "Place", StringComparison.OrdinalIgnoreCase)
            || string.Equals(effect.TypeName, "Resize", StringComparison.OrdinalIgnoreCase);
    }

    static string[] resolutions = ["640x480", "1280x720", "1920x1080", "2560x1440", "3840x2160", Localized.DraftPage_PrevResultion_Custom];

    public ScrollView BuildGeneralTab()
    {
        PropertyPanelBuilder ppb = new();
        ppb.AddEntry("targetFrameRate", Localized.DraftSettingPage_General_TargetFramerate, parent.ProjectInfo.TargetFrameRate.ToString(), "60", null, default);
        ppb.AddPicker("relativeResolution", Localized.DraftSettingPage_General_RelativeResultion, resolutions, $"{parent.ProjectInfo.RelativeWidth}x{parent.ProjectInfo.RelativeHeight}", null);
        return ppb.ListenToChanges(OnPropertiesChanged).BuildWithScrollView(null);
    }
    public ScrollView BuildAdvancedTab()
    {
        PropertyPanelBuilder ppb = new();
        ppb.AddText(new TitleAndDescriptionLineLabel(Localized.DraftSettingPage_Advanced_UserDefinedProperties, Localized.DraftSettingPage_Advanced_UserDefinedProperties_Subtitle));
        foreach (var item in parent.ProjectInfo.UserDefinedProperties)
        {
            ppb.AddEntry($"CustomOption,{item.Key}", item.Key, item.Value, Localized.DraftSettingPage_Advanced_UserDefinedProperties_KeepBlankToRemove, null, default);
        }
        ppb.AddButton(Localized.DraftSettingPage_Advanced_UserDefinedProperties_Add, async (s, e) =>
        {
            var key = await parent.DisplayPromptAsync(Localized._Info, Localized.DraftSettingPage_Advanced_UserDefinedProperties_Add_InputKey, Localized._Confirm, Localized._Cancel);
            ppb.AddEntry($"CustomOption,{key}", key, "", Localized.DraftSettingPage_Advanced_UserDefinedProperties_KeepBlankToRemove, null, default);
            if (string.IsNullOrWhiteSpace(key)) return;
            parent.ProjectInfo.UserDefinedProperties.Add(key, "");
            tabView.SelectedItem.Content = BuildAdvancedTab();
        });
        ppb.AddButton("SaveCustomOption", Localized._Save);
        return ppb.ListenToChanges(OnPropertiesChanged).BuildWithScrollView(null);
    }

    public async void OnPropertiesChanged(object? sender, PropertyPanelPropertyChangedEventArgs e)
    {
        switch (e.Id)
        {
            case "targetFrameRate":
                if (e.Value is string s && uint.TryParse(s, out var result))
                    parent.ProjectInfo.TargetFrameRate = result;
                break;
            case "relativeResolution":
                if (e.Value is string res)
                {
                    if (res == Localized.DraftPage_PrevResultion_Custom)
                    {
                        var widthInput = await parent.DisplayPromptAsync(Localized._Info, Localized.DraftPage_PrevResultion_Custom_InputWidth, initialValue: "1920");
                        var heightInput = await parent.DisplayPromptAsync(Localized._Info, Localized.DraftPage_PrevResultion_Custom_InputHeight, initialValue: "1080");
                        if (int.TryParse(widthInput, out int w) && int.TryParse(heightInput, out int h))
                        {
                            parent.ProjectInfo.RelativeWidth = w;
                            parent.ProjectInfo.RelativeHeight = h;
                        }
                    }
                    else
                    {
                        var parts = res.Split('x');
                        if (parts.Length == 2 &&
                            int.TryParse(parts[0], out var w) &&
                            int.TryParse(parts[1], out var h))
                        {
                            parent.ProjectInfo.RelativeWidth = w;
                            parent.ProjectInfo.RelativeHeight = h;
                        }
                    }

                }
                break;
            case "SaveCustomOption":
                {
                    if (sender is PropertyPanelBuilder ppb)
                    {
                        foreach (var item in ppb.Properties.Where(c => c.Key.StartsWith("CustomOption")))
                        {
                            var key = item.Key.Substring("CustomOption,".Length);
                            if (item.Value is string val)
                            {
                                if (string.IsNullOrWhiteSpace(val))
                                {
                                    parent.ProjectInfo.UserDefinedProperties.Remove(key);
                                }
                                else
                                {
                                    parent.ProjectInfo.UserDefinedProperties[key] = val;
                                }
                            }
                        }
                    }

                    tabView.SelectedItem.Content = BuildAdvancedTab();

                    break;
                }


        }

        parent.SetStateOK(Localized.DraftPage_ChangesApplied);
    }

    public View Content => tabView;
}