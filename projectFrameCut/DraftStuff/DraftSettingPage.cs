

using LocalizedResources;
using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationAPIBase.Views.TabbedView;
using projectFrameCut.Asset;
using projectFrameCut.Controls;
using projectFrameCut.Render;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Shared;
using System.Reflection;


namespace projectFrameCut.DraftStuff;

public class DraftSettingPage
{
    private const string SaveSlotDirectoryName = "saveSlots";
    private readonly string? standaloneProjectPath;

    private bool IsStandaloneJsonMode => !string.IsNullOrWhiteSpace(standaloneProjectPath);

    private sealed class SaveSlotHistoryItem
    {
        public Guid SnapshotID { get; init; }
        public DateTime SavedAt { get; init; }
        public string ChangeReason { get; init; } = string.Empty;
        public string ChangedBy { get; internal set; } = string.Empty;
        public Guid ChangedByUserID { get; internal set; }
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

    public DraftSettingPage(string projectPath)
    {
        tabView = new();
        tabView.HeadersPanel.BackgroundColor = Colors.Transparent;
        parent = null!;
        standaloneProjectPath = projectPath;
        Build();
    }

    public void Build()
    {
        if (IsStandaloneJsonMode)
        {
            tabView.TabItems.Clear();
            tabView.TabItems.Add(new TabbedViewItem
            {
                Header = Localized.DraftSettingPage_Tab_ClipMgnt,
                Content = BuildClipAndAssetManageTab()
            });
            tabView.TabItems.Add(new TabbedViewItem
            {
                Header = Localized.MainSettingsPage_Tab_Misc,
                Content = BuildAdvancedTab()
            });
            return;
        }

        tabView.TabItems.Add(new TabbedViewItem
        {
            Header = Localized.MainSettingsPage_Tab_General,
            Content = BuildGeneralTab()
        });
        tabView.TabItems.Add(new TabbedViewItem
        {
            Header = Localized.DraftSettingPage_Tab_History,
            Tag = "history",
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
            bool isCurrent = item.SnapshotID == parent.CurrentSnapshotID;
            string reason = string.IsNullOrWhiteSpace(item.ChangeReason) ? Localized.DraftSettingPage_Tab_History_UnknownOperation : item.ChangeReason.Trim();

            var slotLabel = new Label
            {
                Text = (isCurrent ? "*" : "") + reason,
                FontSize = 16,
                FontAttributes = isCurrent ? FontAttributes.Bold : FontAttributes.None,
                VerticalOptions = LayoutOptions.Center
            };

            if(parent.ProjectInfo.SnapshotIDMapping.TryGetValue(parent.CurrentSnapshotID, out var curPtr))
            {
                ToolTipProperties.SetText(slotLabel, $"forked from {curPtr.prevoius}, next fork {curPtr.next}");
            }
            else
            {
                ToolTipProperties.SetText(slotLabel, $"unknown fork info");
            }

            var lastChangeLabel = new Label
            {
                VerticalOptions = LayoutOptions.Center,
                TextColor = Colors.White,
                FontSize = 12,
                Margin = new(0, 0, 8, 0),
                Text = item.ChangedBy + " - " +
                       (DateTime.Now.Ticks - item.SavedAt.Ticks >= 0 ?
                       TimeSpan.FromTicks(DateTime.Now.Ticks - item.SavedAt.Ticks) switch
                       {
                           var t when t.TotalMinutes < 1 => Localized.DraftSettingPage_Tab_History_Now,
                           var t when t.TotalHours < 2 => Localized.DraftSettingPage_Tab_History_MinutesAgo(t.Minutes),
                           var t when t.TotalHours < 48 => Localized.DraftSettingPage_Tab_History_HoursAgo((int)t.TotalHours),
                           var t when t.TotalDays < 14 => Localized.DraftSettingPage_Tab_History_DaysAgo((int)t.TotalDays),
                           _ => Localized.DraftSettingPage_Tab_History_VeryLongAgo
                       }
                       : Localized.HomePage_LastChangedOnFuture)
            };

            ToolTipProperties.SetText(lastChangeLabel, $"{item.SavedAt} - {item.ChangedBy}({item.ChangedByUserID})");

            var applyButton = new Button
            {
                Text = Localized._Apply,
                IsEnabled = !isCurrent,
                HorizontalOptions = LayoutOptions.End,
                VerticalOptions = LayoutOptions.Center
            };

            Guid targetSlot = item.SnapshotID;
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

    private void ApplyHistorySlot(Guid snapshotId)
    {
        try
        {
            parent.SetStateBusy(Localized.DraftPage_ApplyingChanges);
            parent.ApplySlot(snapshotId);
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
        foreach (var slotPath in System.IO.Directory.GetDirectories(saveSlotsPath, "slot_*"))
        {
            string timelinePath = System.IO.Path.Combine(slotPath, "timeline.json");
            if (!System.IO.File.Exists(timelinePath))
            {
                continue;
            }

            try
            {
                string json = System.IO.File.ReadAllText(timelinePath);
                var draft = System.Text.Json.JsonSerializer.Deserialize<DraftStructureJSON>(json, DraftPage.DraftJSONOption);
                if (draft is null || draft.SnapshotID == Guid.Empty)
                {
                    continue;
                }

                result.Add(new SaveSlotHistoryItem
                {
                    SnapshotID = draft.SnapshotID,
                    SavedAt = draft.SavedAt,
                    ChangeReason = draft.ChangeReason,
                    ChangedBy = string.IsNullOrWhiteSpace(draft.ChangedByUserDisplayName) ? "Anonymous" : draft.ChangedByUserDisplayName,
                    ChangedByUserID = draft.ChangedByUser
                });
            }
            catch
            {
                // Ignore broken slot files and continue loading other records.
            }
        }

        return result
            .OrderByDescending(i => i.SavedAt)
            .ThenByDescending(i => i.SnapshotID)
            .ToList();
    }

    private View BuildCompatibilityTab()
    {
        PropertyPanelBuilder ppb = new();
        ppb.AddButton(Localized.DraftSettingPage_Tab_Compatibility_UpgradePlaceResize, UpgradePlaceResizeButton_Clicked);
        return ppb.BuildWithScrollView();
    }

    public View BuildClipAndAssetManageTab()
    {
        if (!TryLoadJsonProjectData(out var project, out var draft, out var assets, out var projectRoot, out var error))
        {
            var errorLayout = new VerticalStackLayout
            {
                Spacing = 10,
                Padding = new Thickness(10),
                Children =
                {
                    new Label
                    {
                        Text = error,
                        TextColor = Colors.IndianRed,
                        LineBreakMode = LineBreakMode.CharacterWrap
                    }
                }
            };

            return new ScrollView { Content = errorLayout };
        }

        var clips = GetEditableClipDtos(draft)
            .Where(c => IsRealClipId(c.Id))
            .OrderBy(c => c.LayerIndex)
            .ThenBy(c => c.StartFrame)
            .ToList();

        var root = new VerticalStackLayout
        {
            Spacing = 10,
            Padding = new Thickness(10)
        };

        root.Children.Add(new Label
        {
            Text = Localized.DraftSettingPage_Tab_ClipMgnt_Warn,
            FontSize = 18,
            LineBreakMode = LineBreakMode.CharacterWrap,
            TextColor = Colors.Yellow,
            Background = Colors.Black
        });

        root.Children.Add(new Label
        {
            Text = Localized.DraftSettingPage_Tab_ClipMgnt_ProjInfo(project?.ProjectName ?? "?", clips.Count),
            FontSize = 12,
            LineBreakMode = LineBreakMode.CharacterWrap
        });

        if (clips.Count == 0)
        {
            root.Children.Add(new Label
            {
                Text = Localized.DraftSettingPage_Tab_ClipMgnt_NoData,
                Opacity = 0.75
            });
        }

        foreach (var clip in clips)
        {
            root.Children.Add(BuildStandaloneClipEditorCard(clip, draft, clips, assets, projectRoot));
        }

        root.Children.Add(new BoxView
        {
            HeightRequest = 1,
            Color = Colors.Gray,
            Opacity = 0.35,
            Margin = new Thickness(0, 6)
        });

        root.Children.Add(new Label
        {
            Text = Localized.DraftSettingPage_Tab_ClipMgnt_ProjAssetInfo(assets.Count),
            FontSize = 18,
            FontAttributes = FontAttributes.Bold
        });

        if (assets.Count == 0)
        {
            root.Children.Add(new Label
            {
                Text = Localized.DraftSettingPage_Tab_ClipMgnt_NoData,
                Opacity = 0.75
            });
        }

        foreach (var asset in assets.OrderBy(a => a.Name))
        {
            root.Children.Add(BuildStandaloneAssetEditorCard(asset, draft, clips, assets, projectRoot));
        }

        return new ScrollView { Content = root };
    }

    private View BuildStandaloneClipEditorCard(ClipDraftDTO clip, DraftStructureJSON draft, List<ClipDraftDTO> clips, List<AssetItem> assets, string projectRoot)
    {

        var frame = new Border
        {
            Padding = new Thickness(10),
            Margin = new Thickness(0, 4),
            Stroke = Colors.Gray,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            Background = new SolidColorBrush(Colors.Transparent)
        };

        var nameEntry = new Entry { Text = clip.Name, Placeholder = "Name", HorizontalOptions = LayoutOptions.Fill };
        var trackEntry = new Entry { Text = clip.LayerIndex.ToString(), Placeholder = "Track", Keyboard = Keyboard.Numeric, HorizontalOptions = LayoutOptions.Fill };
        var startFrameEntry = new Entry { Text = clip.StartFrame.ToString(), Placeholder = "Start frame", Keyboard = Keyboard.Numeric, HorizontalOptions = LayoutOptions.Fill };
        var lengthEntry = new Entry { Text = clip.Duration.ToString(), Placeholder = "Length", Keyboard = Keyboard.Numeric, HorizontalOptions = LayoutOptions.Fill };
        var relStartEntry = new Entry { Text = clip.RelativeStartFrame.ToString(), Placeholder = "Relative start(frame)", Keyboard = Keyboard.Numeric, HorizontalOptions = LayoutOptions.Fill };
        var targetXEntry = new Entry { Text = clip.TargetX.ToString(), Placeholder = "TargetX", Keyboard = Keyboard.Numeric, HorizontalOptions = LayoutOptions.Fill };
        var targetYEntry = new Entry { Text = clip.TargetY.ToString(), Placeholder = "TargetY", Keyboard = Keyboard.Numeric, HorizontalOptions = LayoutOptions.Fill };
        var targetWEntry = new Entry { Text = clip.TargetWidth.ToString(), Placeholder = "TargetWidth", Keyboard = Keyboard.Numeric, HorizontalOptions = LayoutOptions.Fill };
        var targetHEntry = new Entry { Text = clip.TargetHeight.ToString(), Placeholder = "TargetHeight", Keyboard = Keyboard.Numeric, HorizontalOptions = LayoutOptions.Fill };

        var infoLabel = new Label
        {
            Text = $"{clip.Name} | {clip.Id} | {Localized.DraftPage_Track((int)clip.LayerIndex)}",
            FontAttributes = FontAttributes.Bold,
            FontSize = 13
        };

        var sourceLabel = new Label
        {
            Text = clip.FilePath,
            FontSize = 11,
            Opacity = 0.75,
            LineBreakMode = LineBreakMode.CharacterWrap
        };

        var saveButton = new Button
        {
            Text = Localized._Save,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Center
        };

        var deleteButton = new Button
        {
            Text = Localized.HomePage_ProjectContextMenu_Delete,
            BackgroundColor = Colors.IndianRed,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Center
        };


        var layout = new VerticalStackLayout { Spacing = 6 };
        layout.Children.Add(infoLabel);
        layout.Children.Add(sourceLabel);
        layout.Children.Add(new HorizontalStackLayout { Children = { new Label { Text = "Name", VerticalOptions = LayoutOptions.Center }, nameEntry }, Spacing = 8 });
        layout.Children.Add(new HorizontalStackLayout { Children = { new Label { Text = "Track", VerticalOptions = LayoutOptions.Center }, trackEntry }, Spacing = 8 });
        layout.Children.Add(new HorizontalStackLayout { Children = { new Label { Text = "StartFrame", VerticalOptions = LayoutOptions.Center }, startFrameEntry }, Spacing = 8 });
        layout.Children.Add(new HorizontalStackLayout { Children = { new Label { Text = "Duration", VerticalOptions = LayoutOptions.Center }, lengthEntry }, Spacing = 8 });
        layout.Children.Add(new HorizontalStackLayout { Children = { new Label { Text = "RelativeStartPoint", VerticalOptions = LayoutOptions.Center }, relStartEntry }, Spacing = 8 });
        layout.Children.Add(new HorizontalStackLayout { Children = { new Label { Text = "TargetX", VerticalOptions = LayoutOptions.Center }, targetXEntry }, Spacing = 8 });
        layout.Children.Add(new HorizontalStackLayout { Children = { new Label { Text = "TargetY", VerticalOptions = LayoutOptions.Center }, targetYEntry }, Spacing = 8 });
        layout.Children.Add(new HorizontalStackLayout { Children = { new Label { Text = "TargetWidth", VerticalOptions = LayoutOptions.Center }, targetWEntry }, Spacing = 8 });
        layout.Children.Add(new HorizontalStackLayout { Children = { new Label { Text = "TargetHeight" }, targetHEntry }, Spacing = 8 });

        var effectBundles = (clip.EffectBundles ?? [])
            .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(b => b.BundleTypeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(b => b.Id)
            .ToList();

        layout.Children.Add(new Label
        {
            Text = SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResources?.Tabs_Effect ?? "Effect",
            FontSize = 13,
            FontAttributes = FontAttributes.Bold,
            Margin = new Thickness(0, 8, 0, 0)
        });

        if (effectBundles.Count == 0)
        {
            layout.Children.Add(new Label
            {
                Text = Localized.DraftSettingPage_Tab_ClipMgnt_NoData,
                FontSize = 11,
                Opacity = 0.75
            });
        }
        else
        {
            foreach (var bundle in effectBundles)
            {
                var bundleInfo = new Label
                {
                    Text = BuildEffectBundleSummary(bundle),
                    FontSize = 11,
                    LineBreakMode = LineBreakMode.CharacterWrap,
                    Opacity = 0.9
                };

                var deleteBundleButton = new Button
                {
                    Text = Localized.DraftPage_ContextMenu_Delete,
                    BackgroundColor = Colors.OrangeRed,
                    FontSize = 11,
                    Padding = new Thickness(8, 4),
                    HorizontalOptions = LayoutOptions.Start
                };

                deleteBundleButton.Clicked += async (_, _) =>
                {
                    bool confirmBundleDelete = await ConfirmAsync(
                        Localized._Warn,
                        Localized.HomePage_ProjectContextMenu_Delete_Confirm0($"'{bundle.Name}' ({bundle.BundleTypeName}@'{clip.Name}')"));
                    if (!confirmBundleDelete)
                    {
                        return;
                    }

                    if (!RemoveStandaloneEffectBundle(clip, bundle.Id))
                    {
                        await ShowInfoAsync("Effect bundle not found.");
                        return;
                    }

                    SetEditableClipDtos(draft, clips);
                    await SaveJsonProjectDataAsync(projectRoot, draft, assets, Localized._Done);
                    tabView.SelectedItem.Content = BuildClipAndAssetManageTab();
                };

                var bundleCard = new VerticalStackLayout
                {
                    Spacing = 4,
                    Padding = new Thickness(8, 6),
                    BackgroundColor = new Color(1f, 1f, 1f, 0.03f),
                    Children = { bundleInfo, deleteBundleButton }
                };
                layout.Children.Add(bundleCard);
            }
        }

        var bundleIdSet = new HashSet<Guid>(effectBundles.Select(b => b.Id));
        var standaloneEffects = (clip.Effects ?? [])
            .Select((effect, idx) => new { Effect = effect, Index = idx })
            .Where(x => !IsEffectBoundToExistingBundle(x.Effect, bundleIdSet))
            .OrderBy(x => x.Effect.Index)
            .ThenBy(x => x.Effect.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Index)
            .ToList();

        layout.Children.Add(new Label
        {
            Text = SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResources?.Tabs_Effect_Classic ?? "Classic effect",
            FontSize = 13,
            FontAttributes = FontAttributes.Bold,
            Margin = new Thickness(0, 8, 0, 0)
        });

        if (standaloneEffects.Count == 0)
        {
            layout.Children.Add(new Label
            {
                Text = Localized.DraftSettingPage_Tab_ClipMgnt_NoData,
                FontSize = 11,
                Opacity = 0.75
            });
        }
        else
        {
            foreach (var item in standaloneEffects)
            {
                int effectArrayIndex = item.Index;
                var effectInfo = new Label
                {
                    Text = BuildStandaloneEffectSummary(item.Effect),
                    FontSize = 11,
                    LineBreakMode = LineBreakMode.CharacterWrap,
                    Opacity = 0.9
                };

                var deleteEffectButton = new Button
                {
                    Text = Localized.DraftPage_ContextMenu_Delete,
                    BackgroundColor = Colors.IndianRed,
                    FontSize = 11,
                    Padding = new Thickness(8, 4),
                    HorizontalOptions = LayoutOptions.Start
                };

                deleteEffectButton.Clicked += async (_, _) =>
                {
                    bool confirmEffectDelete = await ConfirmAsync(
                        Localized._Warn,
                        Localized.HomePage_ProjectContextMenu_Delete_Confirm0($"'{item.Effect.Name}' ({item.Effect.TypeName}@'{clip.Name}')"));
                    if (!confirmEffectDelete)
                    {
                        return;
                    }

                    if (!RemoveStandaloneEffectAt(clip, effectArrayIndex))
                    {
                        await ShowInfoAsync("Standalone effect not found.");
                        return;
                    }

                    SetEditableClipDtos(draft, clips);
                    await SaveJsonProjectDataAsync(projectRoot, draft, assets, Localized._Done);
                    tabView.SelectedItem.Content = BuildClipAndAssetManageTab();
                };

                var effectCard = new VerticalStackLayout
                {
                    Spacing = 4,
                    Padding = new Thickness(8, 6),
                    BackgroundColor = new Color(1f, 1f, 1f, 0.03f),
                    Children = { effectInfo, deleteEffectButton }
                };
                layout.Children.Add(effectCard);
            }
        }

        layout.Children.Add(new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Star }
            ],
            Children =
            {
                saveButton,
                deleteButton
            }
        });
        Grid.SetColumn(deleteButton, 1);

        saveButton.Clicked += async (_, _) =>
        {
            if (!TryReadEntryUInt(trackEntry.Text, out uint newTrack)
                || !TryReadEntryUInt(startFrameEntry.Text, out uint startFrame)
                || !TryReadEntryUInt(lengthEntry.Text, out uint duration)
                || !TryReadEntryInt(targetXEntry.Text, out int newTargetX)
                || !TryReadEntryInt(targetYEntry.Text, out int newTargetY)
                || !TryReadEntryInt(targetWEntry.Text, out int newTargetW)
                || !TryReadEntryInt(targetHEntry.Text, out int newTargetH))
            {
                await ShowInfoAsync("Invalid number in clip fields.");
                return;
            }

            clip.Name = string.IsNullOrWhiteSpace(nameEntry.Text) ? clip.Name : nameEntry.Text.Trim();
            clip.LayerIndex = newTrack;
            clip.SubLayerIndex = newTrack;
            clip.StartFrame = startFrame;
            clip.Duration = Math.Max(1u, duration);
            clip.TargetX = newTargetX;
            clip.TargetY = newTargetY;
            clip.TargetWidth = Math.Max(0, newTargetW);
            clip.TargetHeight = Math.Max(0, newTargetH);

            SetEditableClipDtos(draft, clips);
            await SaveJsonProjectDataAsync(projectRoot, draft, assets, $"Clip updated: {clip.Name}");
            tabView.SelectedItem.Content = BuildClipAndAssetManageTab();
        };



        deleteButton.Clicked += async (_, _) =>
        {
            bool confirm = await ConfirmAsync(Localized._Warn, Localized.HomePage_ProjectContextMenu_Delete_Confirm0(clip.Name));
            bool confirm2 = confirm && (await ConfirmAsync(Localized._Warn, Localized.HomePage_ProjectContextMenu_Delete_Confirm1(clip.Name)));
            bool confirm3 = confirm2 && (await PromptAsync(Localized._Warn, Localized.HomePage_ProjectContextMenu_Delete_Confirm2Input(clip.Name)) == "yes");
            if (!confirm3)
            {
                return;
            }

            int removed = RemoveStandaloneClipWithDependents(draft, clip.Id);
            if (removed <= 0)
            {
                await ShowInfoAsync("Clip not found.");
                return;
            }

            await SaveJsonProjectDataAsync(projectRoot, draft, assets, $"Deleted clip: {clip.Name}");
            tabView.SelectedItem.Content = BuildClipAndAssetManageTab();
        };


        frame.Content = layout;
        return frame;
    }

    private static bool RemoveStandaloneEffectAt(ClipDraftDTO clip, int effectArrayIndex)
    {
        if (clip.Effects is null || effectArrayIndex < 0 || effectArrayIndex >= clip.Effects.Length)
        {
            return false;
        }

        var effects = clip.Effects.ToList();
        effects.RemoveAt(effectArrayIndex);
        clip.Effects = effects.Count == 0 ? null : effects.ToArray();
        return true;
    }

    private static bool RemoveStandaloneEffectBundle(ClipDraftDTO clip, Guid bundleId)
    {
        if (clip.EffectBundles is null)
        {
            return false;
        }

        var bundles = clip.EffectBundles.ToList();
        int removeIndex = bundles.FindIndex(b => b.Id == bundleId);
        if (removeIndex < 0)
        {
            return false;
        }

        bundles.RemoveAt(removeIndex);
        clip.EffectBundles = bundles.Count == 0 ? null : bundles.ToArray();

        if (clip.Effects is not null)
        {
            string groupId = bundleId.ToString();
            foreach (var effect in clip.Effects)
            {
                if (string.Equals(effect.BindedEffectGroupID, groupId, StringComparison.OrdinalIgnoreCase))
                {
                    effect.BindedEffectGroupID = string.Empty;
                }
            }
        }

        return true;
    }

    private static bool IsEffectBoundToExistingBundle(EffectAndMixtureJSONStructure effect, HashSet<Guid> bundleIds)
    {
        if (effect is null || string.IsNullOrWhiteSpace(effect.BindedEffectGroupID))
        {
            return false;
        }

        return Guid.TryParse(effect.BindedEffectGroupID.Trim(), out var gid) && bundleIds.Contains(gid);
    }

    private static string BuildEffectBundleSummary(EffectBundleJSONStructure bundle)
    {
        int parameterCount = bundle.Parameters?.Count ?? 0;
        int multiInputCount = bundle.BindedInputIds?.Length ?? 0;
        return $"Name: {bundle.Name} | ClipType: {bundle.BundleTypeName} | Id: {bundle.Id}\n"
             + $"Input: {bundle.BindedInputId} | Output: {bundle.BindedOutputId} | MultiInput: {multiInputCount} | Params: {parameterCount}";
    }

    private static string BuildStandaloneEffectSummary(EffectAndMixtureJSONStructure effect)
    {
        int parameterCount = effect.Parameters?.Count ?? 0;
        string bindingId = string.IsNullOrWhiteSpace(effect.BindedEffectGroupID) ? "(none)" : effect.BindedEffectGroupID;
        return $"Name: {effect.Name} | ClipType: {effect.TypeName} | Index: {effect.Index} | Enabled: {effect.Enabled}\n"
             + $"Implement: {effect.ImplementType} | Params: {parameterCount} | BindedEffectGroupID: {bindingId}";
    }

    private View BuildStandaloneAssetEditorCard(AssetItem asset, DraftStructureJSON draft, List<ClipDraftDTO> clips, List<AssetItem> assets, string projectRoot)
    {
        string assetId = asset.AssetId ?? string.Empty;
        int usedByClipCount = string.IsNullOrWhiteSpace(assetId)
            ? 0
            : clips.Count(c => IsClipReferencingAsset(c, asset, projectRoot));

        var frame = new Border
        {
            Padding = new Thickness(10),
            Margin = new Thickness(0, 4),
            Stroke = Colors.Gray,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            Background = new SolidColorBrush(Colors.Transparent)
        };

        var label = new Label
        {
            Text = $"{asset.Name} | {assetId}",
            FontAttributes = FontAttributes.Bold,
            FontSize = 13
        };

        var pathLabel = new Label
        {
            Text = asset.Path,
            FontSize = 11,
            Opacity = 0.75,
            LineBreakMode = LineBreakMode.CharacterWrap
        };

        var usageLabel = new Label
        {
            Text = Localized.DraftSettingPage_Tab_ClipMgnt_ReferenceBy(usedByClipCount),
            FontSize = 12,
            Opacity = 0.85
        };

        var deleteButton = new Button
        {
            Text = Localized.DraftPage_ContextMenu_Delete,
            BackgroundColor = usedByClipCount > 0 ? Colors.OrangeRed : Colors.IndianRed
        };

        deleteButton.Clicked += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(assetId))
            {
                await ShowInfoAsync("AssetId is empty.");
                return;
            }

            var referencedClipIds = clips
                .Where(c => IsClipReferencingAsset(c, asset, projectRoot))
                .Select(c => c.Id)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            bool confirm = await ConfirmAsync(
                Localized._Warn,
                referencedClipIds.Count > 0
                    ? Localized.DraftSettingPage_Tab_ClipMgnt_DeleteAssetReferenced(asset.Name, referencedClipIds.Count)
                    : Localized.HomePage_ProjectContextMenu_Delete_Confirm0(asset.Name));
            if (referencedClipIds.Count < 0)
            {
                if (!confirm) return;
                if ((await PromptAsync(
                       Localized._Warn,
                       Localized.HomePage_ProjectContextMenu_Delete_Confirm2(asset.Name)))?.Trim() != "yes")
                    return;
            }
            else
            {
                if (confirm && (await PromptAsync(
                   Localized._Warn,
                   Localized.DraftSettingPage_Tab_ClipMgnt_DeleteAssetReferenced_Warn2(asset.Name, string.Join(Environment.NewLine, clips.Where(c => IsClipReferencingAsset(c, asset, projectRoot)).Select(c => $"- {c.Name}").ToList()))))?.Trim() == "yes")
                {
                    foreach (var clipId in referencedClipIds)
                    {
                        RemoveStandaloneClipWithDependents(draft, clipId);
                    }
                }
                else
                {
                    if ((await PromptAsync(
                       Localized._Warn,
                       Localized.HomePage_ProjectContextMenu_Delete_Confirm2(asset.Name)))?.Trim() != "yes")
                        return;
                }
            }

            assets.RemoveAll(a => string.Equals(a.AssetId, assetId, StringComparison.OrdinalIgnoreCase));
            TryDeleteProjectLocalAssetFile(asset, projectRoot);

            await SaveJsonProjectDataAsync(projectRoot, draft, assets, $"Deleted asset: {asset.Name}");
            tabView.SelectedItem.Content = BuildClipAndAssetManageTab();
        };

        var layout = new VerticalStackLayout { Spacing = 5 };
        layout.Children.Add(label);
        layout.Children.Add(pathLabel);
        layout.Children.Add(usageLabel);
        layout.Children.Add(deleteButton);
        frame.Content = layout;
        return frame;
    }

    private bool TryLoadJsonProjectData(out ProjectJSONStructure project, out DraftStructureJSON draft, out List<AssetItem> assets, out string projectRoot, out string error)
    {
        project = new ProjectJSONStructure { ProjectName = "Unknown Project" };
        draft = new DraftStructureJSON();
        assets = [];
        projectRoot = ResolveJsonProjectRoot();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            error = "Project path is empty.";
            return false;
        }

        string projectPath = System.IO.Path.Combine(projectRoot, "project.pjfc");
        string timelinePath = System.IO.Path.Combine(projectRoot, "timeline.json");
        string assetsPath = System.IO.Path.Combine(projectRoot, "assets.json");

        if (!System.IO.File.Exists(timelinePath) || !System.IO.File.Exists(assetsPath))
        {
            error = $"timeline.json or assets.json not found in {projectRoot}.";
            return false;
        }
        if (!File.Exists(projectPath))
        {
            if (File.Exists(System.IO.Path.Combine(projectRoot, "project.json")))
            {
                projectPath = System.IO.Path.Combine(projectRoot, "project.json");
            }
            else
            {
                error = $"project.json or project.pjfc not found in {projectRoot}.";
                return false;
            }
        }

        try
        {
            project = System.Text.Json.JsonSerializer.Deserialize<ProjectJSONStructure>(System.IO.File.ReadAllText(projectPath), DraftPage.DraftJSONOption) ?? new ProjectJSONStructure { ProjectName = "Unknown Project" };
            draft = System.Text.Json.JsonSerializer.Deserialize<DraftStructureJSON>(System.IO.File.ReadAllText(timelinePath), DraftPage.DraftJSONOption) ?? new DraftStructureJSON();
            assets = System.Text.Json.JsonSerializer.Deserialize<List<AssetItem>>(System.IO.File.ReadAllText(assetsPath), DraftPage.DraftJSONOption) ?? [];
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private async Task SaveJsonProjectDataAsync(string projectRoot, DraftStructureJSON draft, List<AssetItem> assets, string changeReason)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return;
        }

        draft.ChangeReason = changeReason;
        draft.SavedAt = DateTime.Now;

        string timelinePath = System.IO.Path.Combine(projectRoot, "timeline.json");
        string assetsPath = System.IO.Path.Combine(projectRoot, "assets.json");

        await System.IO.File.WriteAllTextAsync(timelinePath, System.Text.Json.JsonSerializer.Serialize(draft, DraftPage.DraftJSONOption));
        await System.IO.File.WriteAllTextAsync(assetsPath, System.Text.Json.JsonSerializer.Serialize(assets, DraftPage.DraftJSONOption));

        if (!IsStandaloneJsonMode)
        {
            ApplyJsonProjectDataToDraftPage(draft, assets);
            await RefreshDraftAfterManualMutate(changeReason);
        }
    }

    private static List<ClipDraftDTO> GetEditableClipDtos(DraftStructureJSON draft)
    {
        var result = new List<ClipDraftDTO>();
        foreach (var clipObj in draft.Clips ?? Array.Empty<object>())
        {
            ClipDraftDTO? dto = clipObj switch
            {
                ClipDraftDTO c => c,
                System.Text.Json.JsonElement je => System.Text.Json.JsonSerializer.Deserialize<ClipDraftDTO>(je.GetRawText(), DraftPage.DraftJSONOption),
                _ => null
            };

            if (dto is not null)
            {
                result.Add(dto);
            }
        }

        return result;
    }

    private static void SetEditableClipDtos(DraftStructureJSON draft, IEnumerable<ClipDraftDTO> clips)
    {
        draft.Clips = clips.Cast<object>().ToArray();
    }

    private string ResolveJsonProjectRoot()
        => IsStandaloneJsonMode ? standaloneProjectPath ?? string.Empty : parent.WorkingPath;

    private void ApplyJsonProjectDataToDraftPage(DraftStructureJSON draft, List<AssetItem> assets)
    {
        if (IsStandaloneJsonMode)
        {
            return;
        }

        (var clips, _) = DraftImportAndExportHelper.ImportFromJSON(draft, parent.ProjectInfo);
        parent.Clips = new System.Collections.Concurrent.ConcurrentDictionary<string, ClipElementUI>(clips);
        parent.Assets = CreateAssetDictionary(assets);

        foreach (var item in parent.Tracks)
        {
            while (item.Value.Children.Count > 0)
            {
                item.Value.Children.RemoveAt(0);
            }
        }

        foreach (var kv in parent.Clips.OrderBy(kv => kv.Value.origTrack ?? 0).ThenBy(kv => kv.Value.origX))
        {
            var item = kv.Value;
            int trackIndex = item.origTrack ?? 0;
            if (!parent.Tracks.ContainsKey(trackIndex))
            {
                parent.AddATrack(trackIndex);
            }

            parent.AddAClip(item);
            parent.RegisterClip(item, true);
        }

    }

    private static System.Collections.Concurrent.ConcurrentDictionary<string, AssetItem> CreateAssetDictionary(IEnumerable<AssetItem> assets)
    {
        var result = new System.Collections.Concurrent.ConcurrentDictionary<string, AssetItem>();
        foreach (var asset in assets)
        {
            string key = string.IsNullOrWhiteSpace(asset.AssetId)
                ? $"unknown+{Guid.NewGuid()}"
                : asset.AssetId;
            result[key] = asset;
        }

        return result;
    }

    private static bool IsRealClipId(string clipId)
        => !string.IsNullOrWhiteSpace(clipId)
        && !clipId.StartsWith("ghost_", StringComparison.Ordinal)
        && !clipId.StartsWith("shadow_", StringComparison.Ordinal);

    private static int RemoveStandaloneClipWithDependents(DraftStructureJSON draft, string clipId)
    {
        if (string.IsNullOrWhiteSpace(clipId))
        {
            return 0;
        }

        var clips = GetEditableClipDtos(draft);
        var idsToDelete = new HashSet<string>(StringComparer.Ordinal)
        {
            clipId,
            "ghost_" + clipId,
            "shadow_" + clipId
        };

        foreach (var clip in clips)
        {
            if (clip.ClipType != ClipMode.TransformClip)
            {
                continue;
            }

            bool linkedByPrev = clip.MetaData?.TryGetValue("transformPrevId", out var prev) == true
                && string.Equals(prev?.ToString(), clipId, StringComparison.Ordinal);
            bool linkedByNext = clip.MetaData?.TryGetValue("transformNextId", out var next) == true
                && string.Equals(next?.ToString(), clipId, StringComparison.Ordinal);

            if (linkedByPrev || linkedByNext)
            {
                idsToDelete.Add(clip.Id);
            }
        }

        int before = clips.Count;
        clips.RemoveAll(c => idsToDelete.Contains(c.Id));
        SetEditableClipDtos(draft, clips);
        return before - clips.Count;
    }

    private bool IsClipReferencingAsset(ClipDraftDTO clip, AssetItem asset, string projectRoot)
    {
        if (clip is null || asset is null)
        {
            return false;
        }

        string source = clip.FilePath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        string assetId = asset.AssetId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(assetId) && source.StartsWith("$", StringComparison.Ordinal))
        {
            return string.Equals(source[1..], assetId, StringComparison.OrdinalIgnoreCase);
        }

        if (string.IsNullOrWhiteSpace(asset.Path) || string.IsNullOrWhiteSpace(projectRoot))
        {
            return false;
        }

        try
        {
            string clipPath = source;
            if (!System.IO.Path.IsPathRooted(clipPath))
            {
                clipPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(projectRoot, clipPath));
            }

            string assetPath = asset.Path;
            if (!System.IO.Path.IsPathRooted(assetPath))
            {
                assetPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(projectRoot, assetPath));
            }

            return string.Equals(clipPath, assetPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void TryDeleteProjectLocalAssetFile(AssetItem asset, string projectRoot)
    {
        try
        {
            if (asset is null || string.IsNullOrWhiteSpace(asset.Path) || string.IsNullOrWhiteSpace(projectRoot))
            {
                return;
            }

            string projectAssetsDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(projectRoot, "assets"));
            string fullPath = asset.Path;
            if (!System.IO.Path.IsPathRooted(fullPath))
            {
                fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(projectRoot, fullPath));
            }

            if (fullPath.StartsWith(projectAssetsDir, StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(fullPath))
            {
                System.IO.File.Delete(fullPath);
            }
        }
        catch
        {
            // Ignore file system failure because json updates are already complete.
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        if (!IsStandaloneJsonMode)
        {
            return await parent.DisplayAlertAsync(title, message, Localized._Confirm, Localized._Cancel);
        }

        if (GetHostPage() is Page p)
        {
            return await p.DisplayAlertAsync(title, message, Localized._Confirm, Localized._Cancel);
        }

        return true;
    }

    private async Task ShowInfoAsync(string message)
    {
        if (!IsStandaloneJsonMode)
        {
            parent.SetStateFail();
            parent.SetStatusText(message);
            return;
        }

        if (GetHostPage() is Page p)
        {
            await p.DisplayAlertAsync(Localized._Info, message, Localized._Confirm);
        }
    }

    private static Page? GetHostPage()
        => Application.Current?.Windows.FirstOrDefault()?.Page;

    private static bool TryReadEntryInt(string? text, out int value)
        => int.TryParse((text ?? string.Empty).Trim(), out value);

    private static bool TryReadEntryUInt(string? text, out uint value)
        => uint.TryParse((text ?? string.Empty).Trim(), out value);

    private static bool IsRealClip(ClipElementUI clip)
        => !clip.Id.StartsWith("ghost_", StringComparison.Ordinal) && !clip.Id.StartsWith("shadow_", StringComparison.Ordinal);

    private bool IsClipReferencingAsset(ClipElementUI clip, AssetItem asset)
    {
        if (clip is null || asset is null)
        {
            return false;
        }

        string source = clip.SourcePath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        string assetId = asset.AssetId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(assetId) && source.StartsWith("$", StringComparison.Ordinal))
        {
            return string.Equals(source[1..], assetId, StringComparison.OrdinalIgnoreCase);
        }

        if (string.IsNullOrWhiteSpace(asset.Path))
        {
            return false;
        }

        try
        {
            string clipPath = source;
            if (!System.IO.Path.IsPathRooted(clipPath) && !string.IsNullOrWhiteSpace(parent.WorkingPath))
            {
                clipPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(parent.WorkingPath, clipPath));
            }

            string assetPath = asset.Path;
            if (!System.IO.Path.IsPathRooted(assetPath) && !string.IsNullOrWhiteSpace(parent.WorkingPath))
            {
                assetPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(parent.WorkingPath, assetPath));
            }

            return string.Equals(clipPath, assetPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private int RemoveClipWithDependents(string clipId)
    {
        if (string.IsNullOrWhiteSpace(clipId))
        {
            return 0;
        }

        var idsToDelete = new HashSet<string>(StringComparer.Ordinal)
        {
            clipId,
            "ghost_" + clipId,
            "shadow_" + clipId
        };

        foreach (var kv in parent.Clips)
        {
            if (kv.Value?.ClipType != ClipMode.TransformClip)
            {
                continue;
            }

            if (kv.Value.ExtraData.TryGetValue("transformPrevId", out var prev) && string.Equals(prev?.ToString(), clipId, StringComparison.Ordinal)
                || kv.Value.ExtraData.TryGetValue("transformNextId", out var next) && string.Equals(next?.ToString(), clipId, StringComparison.Ordinal))
            {
                idsToDelete.Add(kv.Key);
            }
        }

        int removedCount = 0;
        foreach (var id in idsToDelete)
        {
            if (!parent.Clips.TryGetValue(id, out var clip))
            {
                continue;
            }

            if (clip.origTrack is int tr && parent.Tracks.TryGetValue(tr, out var trackLayout))
            {
                trackLayout.Children.Remove(clip.Clip);
            }

            if (parent.Clips.TryRemove(id, out _))
            {
                removedCount++;
            }
        }

        return removedCount;
    }

    private void TryDeleteProjectLocalAssetFile(AssetItem asset)
    {
        try
        {
            if (asset is null || string.IsNullOrWhiteSpace(asset.Path) || string.IsNullOrWhiteSpace(parent.WorkingPath))
            {
                return;
            }

            string projectAssetsDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(parent.WorkingPath, "assets"));
            string fullPath = asset.Path;
            if (!System.IO.Path.IsPathRooted(fullPath))
            {
                fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(parent.WorkingPath, fullPath));
            }

            if (fullPath.StartsWith(projectAssetsDir, StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(fullPath))
            {
                System.IO.File.Delete(fullPath);
            }
        }
        catch
        {
            // Ignore file system failure because dictionary removal is still complete.
        }
    }

    private async Task RefreshDraftAfterManualMutate(string successText)
    {
        var draft = DraftImportAndExportHelper.ExportFromDraftPage(parent, includeUiOnlyClips: false);
        parent.ProjectDuration = Math.Max(draft.Duration, draft.AudioDuration);

        await parent.ClipEditor.UpdateClips(parent.Clips);
        parent.ClipEditor.SetCurrentFrame((uint)Math.Max(0, parent.CurrentFrame));
        await parent.previewer.UpdateDraft(draft);
        await parent.DynamicPreviewProvider.UpdateDraft(draft);
        await parent.Save();
        parent.SetStateOK(successText);
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
        var enableHDR = parent.ProjectInfo.Properties.TryGetValue("EnableHDR", out var eh) && bool.TryParse(eh, out var result) ? result : false;
        PropertyPanelBuilder ppb = new();
        ppb.AddEntry("targetFrameRate", Localized.DraftSettingPage_General_TargetFramerate, parent.ProjectInfo.TargetFrameRate.ToString(), "60", null, default);
        ppb.AddPicker("relativeResolution", Localized.DraftSettingPage_General_RelativeResultion, resolutions, $"{parent.ProjectInfo.RelativeWidth}x{parent.ProjectInfo.RelativeHeight}", null);
        ppb.AddCheckbox("enableHDR", Localized.DraftSettingPage_General_EnableHDR, enableHDR, null);
        ppb.AppendWhen(enableHDR, c => c.AddEntry("HdrMaximumBrightness", Localized.DraftSettingPage_General_HDRLimit, parent.ProjectInfo.Properties.TryGetValue("HdrMaximumBrightness", out var hdrMaxiumBrightness) ? hdrMaxiumBrightness : "1000", null, null));
        ppb.AppendWhen(enableHDR, c => c.AddEntry("sdrClipBrightness", Localized.DraftSettingPage_General_SDRBrightness,
            parent.ProjectInfo.Properties.TryGetValue("SdrClipBrightness", out var sdrClipBrightness)
                ? sdrClipBrightness
                : (parent.ProjectInfo.Properties.TryGetValue("sdrClipBrightness", out var legacySdrClipBrightness)
                    ? legacySdrClipBrightness
                    : "203"),
            null, null));
        return ppb.ListenToChanges(OnPropertiesChanged).BuildWithScrollView(null);
    }

    public ScrollView BuildAdvancedTab()
    {
        if (IsStandaloneJsonMode)
        {
            return BuildStandaloneAdvancedTab();
        }

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

    private ScrollView BuildStandaloneAdvancedTab()
    {
        if (!TryLoadStandaloneProjectInfo(out var info, out var error))
        {
            var errorLayout = new VerticalStackLayout
            {
                Padding = new Thickness(10),
                Spacing = 10,
                Children =
                {
                    new Label
                    {
                        Text = error,
                        TextColor = Colors.IndianRed,
                        LineBreakMode = LineBreakMode.CharacterWrap
                    }
                }
            };
            return new ScrollView { Content = errorLayout };
        }

        return new PropertyPanelBuilder()
        .AddEntry("targetFrameRate", Localized.DraftSettingPage_General_TargetFramerate, info.TargetFrameRate.ToString(), "60", null, default)
        .AddPicker("relativeResolution", Localized.DraftSettingPage_General_RelativeResultion, resolutions, $"{info.RelativeWidth}x{info.RelativeHeight}", null)
        .AddText(new TitleAndDescriptionLineLabel(Localized.DraftSettingPage_Advanced_UserDefinedProperties, Localized.DraftSettingPage_Advanced_UserDefinedProperties_Subtitle))
        .Foreach(info.UserDefinedProperties, (p, item) => p.AddEntry($"CustomOption,{item.Key}", item.Key, item.Value, Localized.DraftSettingPage_Advanced_UserDefinedProperties_KeepBlankToRemove, null, default))
        .AddButton(Localized.DraftSettingPage_Advanced_UserDefinedProperties_Add, async (s, e) =>
        {
            var key = await PromptAsync(Localized._Info, Localized.DraftSettingPage_Advanced_UserDefinedProperties_Add_InputKey, string.Empty);
            if (string.IsNullOrWhiteSpace(key)) return;

            if (!info.UserDefinedProperties.ContainsKey(key))
            {
                info.UserDefinedProperties[key] = string.Empty;
                await SaveStandaloneProjectInfo(info);
            }

            tabView.SelectedItem.Content = BuildAdvancedTab();
        })
        .AddButton("SaveCustomOption", Localized._Save)

        .AddSeparator()
        .AddText(new SingleLineLabel(SettingsManager.SettingLocalizedResources.Misc_DiagOptions, 25))
        .AddButton(Localized.DraftSettingPage_Advanced_DiscardUnsavedChange, async (s, e) =>
        {
            if ((await (GetHostPage()?.DisplayPromptAsync(Localized._Warn, Localized.DraftSettingPage_Advanced_Warn, Localized._OK, Localized._Cancel) ?? Task.FromResult("")))?.Trim() == Localized._OK)
            {
                info.NormallyExited = true;
                await SaveStandaloneProjectInfo(info);
            }
        })
        .AddButton(Localized.DraftSettingPage_Advanced_ForceUpgrade, async (s, e) =>
        {
            if ((await (GetHostPage()?.DisplayPromptAsync(Localized._Warn, Localized.DraftSettingPage_Advanced_Warn, Localized._OK, Localized._Cancel) ?? Task.FromResult("")))?.Trim() == Localized._OK)
            {
                info.LastOpenAPIBaseVersion = IPluginBase.CurrentPluginAPIVersion;
                info.LastOpenAppVersion = Assembly.GetExecutingAssembly()?.GetName()?.Version?.ToString() ?? "Unknown";
                await SaveStandaloneProjectInfo(info);
            }
        })
        .AddButton(Localized.DraftSettingPage_Advanced_DiscardSaveSlots, async (s, e) =>
        {
            if ((await (GetHostPage()?.DisplayPromptAsync(Localized._Warn, Localized.DraftSettingPage_Advanced_Warn, Localized._OK, Localized._Cancel) ?? Task.FromResult("")))?.Trim() == Localized._OK)
            {
                Directory.Delete(System.IO.Path.Combine(ResolveJsonProjectRoot(), "saveSlots"), true);
            }
        })
        .ListenToChanges(OnStandaloneAdvancedPropertiesChanged)
        .BuildWithScrollView(null);


    }

    public async void OnPropertiesChanged(object? sender, PropertyPanelPropertyChangedEventArgs e)
    {
        switch (e.Id)
        {
            case "targetFrameRate":
                if (e.Value is string s && uint.TryParse(s, out var result))
                    parent.ProjectInfo.TargetFrameRate = result;
                break;
            case "enableHDR":
                if (e.Value is bool b)
                    parent.ProjectInfo.Properties["EnableHDR"] = b.ToString();
                break;
            case "sdrClipBrightness":
                try
                {
                    var d = Convert.ToUInt32(e.Value);
                    if (d < int.MaxValue) parent.ProjectInfo.Properties["SdrClipBrightness"] = d.ToString();
                }
                catch { }
                break;
            case "HdrMaximumBrightness":
                try
                {
                    var c = Convert.ToUInt32(e.Value);
                    if (c < int.MaxValue) parent.ProjectInfo.Properties["HdrMaximumBrightness"] = c.ToString();
                }
                catch { }
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

    private async void OnStandaloneAdvancedPropertiesChanged(object? sender, PropertyPanelPropertyChangedEventArgs e)
    {
        if (!TryLoadStandaloneProjectInfo(out var info, out _))
        {
            await ShowInfoAsync("Failed to read project file.");
            return;
        }

        switch (e.Id)
        {
            case "targetFrameRate":
                if (e.Value is string s && uint.TryParse(s, out var fps))
                {
                    info.TargetFrameRate = fps;
                }
                break;
            case "relativeResolution":
                if (e.Value is string res)
                {
                    if (res == Localized.DraftPage_PrevResultion_Custom)
                    {
                        var widthInput = await GetHostPage().DisplayPromptAsync(Localized._Info, Localized.DraftPage_PrevResultion_Custom_InputWidth, initialValue: "1920");
                        var heightInput = await GetHostPage().DisplayPromptAsync(Localized._Info, Localized.DraftPage_PrevResultion_Custom_InputHeight, initialValue: "1080");
                        if (int.TryParse(widthInput, out int w) && int.TryParse(heightInput, out int h))
                        {
                            info.RelativeWidth = w;
                            info.RelativeHeight = h;
                        }
                    }
                    else
                    {
                        var parts = res.Split('x');
                        if (parts.Length == 2 &&
                            int.TryParse(parts[0], out var w) &&
                            int.TryParse(parts[1], out var h))
                        {
                            info.RelativeWidth = w;
                            info.RelativeHeight = h;
                        }
                    }

                }
                break;
            case "SaveCustomOption":
                if (sender is PropertyPanelBuilder ppb)
                {
                    foreach (var item in ppb.Properties.Where(c => c.Key.StartsWith("CustomOption", StringComparison.Ordinal)))
                    {
                        var key = item.Key.Substring("CustomOption,".Length);
                        if (item.Value is string val)
                        {
                            if (string.IsNullOrWhiteSpace(val))
                            {
                                info.UserDefinedProperties.Remove(key);
                            }
                            else
                            {
                                info.UserDefinedProperties[key] = val;
                            }
                        }
                    }

                    await SaveStandaloneProjectInfo(info);
                    tabView.SelectedItem.Content = BuildAdvancedTab();
                    return;
                }
                break;
        }

        await SaveStandaloneProjectInfo(info);
    }

    private bool TryLoadStandaloneProjectInfo(out ProjectJSONStructure info, out string error)
    {
        info = new ProjectJSONStructure();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(standaloneProjectPath))
        {
            error = "Project path is empty.";
            return false;
        }

        string projectFilePath = ResolveStandaloneProjectFilePath();
        if (!System.IO.File.Exists(projectFilePath))
        {
            error = $"Project file not found: {projectFilePath}";
            return false;
        }

        try
        {
            info = System.Text.Json.JsonSerializer.Deserialize<ProjectJSONStructure>(System.IO.File.ReadAllText(projectFilePath), DraftPage.DraftJSONOption) ?? new ProjectJSONStructure();
            info.UserDefinedProperties ??= new Dictionary<string, string>();
            info.Properties ??= new Dictionary<string, string>();
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private async Task SaveStandaloneProjectInfo(ProjectJSONStructure info)
    {
        if (string.IsNullOrWhiteSpace(standaloneProjectPath))
        {
            return;
        }

        string projectFilePath = ResolveStandaloneProjectFilePath();
        await System.IO.File.WriteAllTextAsync(projectFilePath, System.Text.Json.JsonSerializer.Serialize(info, DraftPage.DraftJSONOption));
    }

    private string ResolveStandaloneProjectFilePath()
    {
        if (string.IsNullOrWhiteSpace(standaloneProjectPath))
        {
            return string.Empty;
        }

        string pjfcPath = System.IO.Path.Combine(standaloneProjectPath, "project.pjfc");
        if (System.IO.File.Exists(pjfcPath))
        {
            return pjfcPath;
        }

        return System.IO.Path.Combine(standaloneProjectPath, "project.json");
    }

    private async Task<string?> PromptAsync(string title, string message, string initialValue = "")
    {
        if (!IsStandaloneJsonMode)
        {
            return await parent.DisplayPromptAsync(title, message, Localized._Confirm, Localized._Cancel, initialValue: initialValue);
        }

        if (GetHostPage() is Page p)
        {
            return await p.DisplayPromptAsync(title, message, Localized._Confirm, Localized._Cancel, initialValue: initialValue);
        }

        return null;
    }

    public View Content => tabView;
}