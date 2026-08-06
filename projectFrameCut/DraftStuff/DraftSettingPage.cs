using LocalizedResources;
using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationAPIBase.Views.TabbedView;
using projectFrameCut.Asset;
using projectFrameCut.Controls;
using projectFrameCut.Render;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System.Reflection;


namespace projectFrameCut.DraftStuff;

public class DraftSettingPage
{
    #region init

    private const string SaveSlotDirectoryName = "saveSlots";
    private readonly string? standaloneProjectPath;

    private bool IsStandaloneJsonMode => !string.IsNullOrWhiteSpace(standaloneProjectPath);

    public View? HistoryTabContent { get; private set; }

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
                Header = Localized.DraftSettingPage_Tab_History,
                Tag = "history",
                Content = BuildHistoryGraphTab(true)
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
        var historyTabContent = BuildHistoryGraphTab();
        HistoryTabContent = historyTabContent;
        tabView.TabItems.Add(new TabbedViewItem
        {
            Header = Localized.DraftSettingPage_Tab_History,
            Tag = "history",
            Content = historyTabContent
        });
        tabView.TabItems.Add(new TabbedViewItem
        {
            Header = Localized.DraftSettingPage_Tab_Messages,
            Tag = "messages",
            Content = BuildHistoryLogsTab()
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

        tabView.OnTabSwitched += (s, e) =>
        {
            if (tabView.SelectedItem.Tag == "history")
            {
                var clearHistoryButtonsLayout = new HorizontalStackLayout
                {
                    Spacing = 8,
                    HorizontalOptions = LayoutOptions.Start
                };

                var popOutButton = new Button
                {
                    Text = "↗",
                    Command = new Command(async () =>
                    {
                        var w = new ApplicationAPIBase.Views.MultiWindowView.MultiWindowItem
                        {
                            Title = Localized.DraftSettingPage_Tab_History,
                            Content = BuildHistoryGraphTab(),
                            IsNavigationVisible = false,
                            IsPopOutVisible = true
                        };
                        parent.MainMultiWindowView.AddWindow(w);
                        await Task.Delay(50);
                        parent.MainMultiWindowView.BringToFront(w);
                        await parent.HidePopup();
                    })
                };

                ToolTipProperties.SetText(popOutButton, ApplicationAPIBase.Localize.APIBaseLocalizedResources.Localized?.MultiWindowView_PopOut ?? "As a standalone window");

                var clearOldHistoryButton = new Button
                {
                    Text = Localized.DraftSettingPage_Tab_History_Cleanup,
                    BackgroundColor = Color.FromArgb("#D2691E"),
                    TextColor = Colors.White,
                    HorizontalOptions = LayoutOptions.Start
                };
                clearOldHistoryButton.Clicked += async (_, _) => await ShowCleanupOptionsAsync();

                clearHistoryButtonsLayout.Children.Add(clearOldHistoryButton);
                clearHistoryButtonsLayout.Add(popOutButton);
                tabView.HeaderRightContent = clearHistoryButtonsLayout;
            }
            else
            {
                tabView.HeaderRightContent = new Border { StrokeThickness = 0, WidthRequest = 1, HeightRequest = 1 };
            }
        };

    }

    #endregion

    #region general
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
            if ((await (GetHostPage()?.DisplayPromptAsync(Localized._Warn, Localized.DraftSettingPage_Advanced_Warn, Localized._OK, Localized._Cancel) ?? Task.FromResult("")))?.Trim() == "yes")
            {
                info.NormallyExited = true;
                await SaveStandaloneProjectInfo(info);
            }
        })
        .AddButton(Localized.DraftSettingPage_Advanced_ForceUpgrade, async (s, e) =>
        {
            if ((await (GetHostPage()?.DisplayPromptAsync(Localized._Warn, Localized.DraftSettingPage_Advanced_Warn, Localized._OK, Localized._Cancel) ?? Task.FromResult("")))?.Trim() == "yes")
            {
                info.LastOpenAPIBaseVersion = IPluginBase.CurrentPluginAPIVersion;
                info.LastOpenAppVersion = Assembly.GetExecutingAssembly()?.GetName()?.Version?.ToString() ?? "Unknown";
                info.LastOpenAppName = MauiProgram.AssemblyName;
                await SaveStandaloneProjectInfo(info);
            }
        })
        .AddButton(Localized.DraftSettingPage_Advanced_DiscardSaveSlots, async (s, e) =>
        {
            if ((await (GetHostPage()?.DisplayPromptAsync(Localized._Warn, Localized.DraftSettingPage_Advanced_Warn, Localized._OK, Localized._Cancel) ?? Task.FromResult("")))?.Trim() == "yes")
            {
                Directory.Delete(System.IO.Path.Combine(ResolveJsonProjectRoot(), "saveSlots"), true);
            }
        })
        .AddButton(Localized.DraftSettingPage_Advanced_ReloadHistory, async (s, e) =>
        {
            try
            {
                string saveSlotsPath = System.IO.Path.Combine(ResolveJsonProjectRoot(), SaveSlotDirectoryName);
                if (!System.IO.Directory.Exists(saveSlotsPath))
                {
                    await ShowInfoAsync("SaveSlots directory does not exist.");
                    return;
                }

                var newMapping = new Dictionary<Guid, ProjectJSONStructure.SnapshotIDMappingStructure>();

                // Read all save slots and create mapping entries with Previous pointers
                foreach (var slotPath in System.IO.Directory.GetDirectories(saveSlotsPath, "slot_*"))
                {
                    string timelinePath = System.IO.Path.Combine(slotPath, "timeline.json");
                    if (!System.IO.File.Exists(timelinePath))
                        continue;

                    try
                    {
                        string json = System.IO.File.ReadAllText(timelinePath);
                        var draft = System.Text.Json.JsonSerializer.Deserialize<DraftStructureJSON>(json, DraftPage.DraftJSONOption);
                        if (draft is null || draft.SnapshotID == Guid.Empty)
                            continue;

                        if (!newMapping.ContainsKey(draft.SnapshotID))
                        {
                            newMapping[draft.SnapshotID] = new ProjectJSONStructure.SnapshotIDMappingStructure
                            {
                                Previous = draft.PreviousSnapshot
                            };
                        }
                    }
                    catch { }
                }

                if (newMapping.Count == 0)
                {
                    await ShowInfoAsync("No valid save slots found.");
                    return;
                }

                // Link Next pointers: for each entry, point its Previous to this entry
                foreach (var kv in newMapping)
                {
                    if (kv.Value.Previous != Guid.Empty && newMapping.TryGetValue(kv.Value.Previous, out var prevEntry) && !prevEntry.Next.Contains(kv.Key))
                    {
                        prevEntry.Next.Add(kv.Key);
                    }
                }

                info.SnapshotIDMapping = newMapping;

                // Set LastSnapshotID to the head (no Next)
                var head = newMapping.FirstOrDefault(kv => kv.Value.Next.Count == 0);
                if (head.Key != Guid.Empty)
                {
                    info.LastSnapshotID = head.Key;
                }

                await SaveStandaloneProjectInfo(info);
                tabView.SelectedItem.Content = BuildAdvancedTab();
                await ShowInfoAsync($"SnapshotIDMapping rebuilt with {newMapping.Count} entries.");
            }
            catch (Exception ex)
            {
                await ShowInfoAsync($"Failed to rebuild SnapshotIDMapping: {ex.Message}");
            }
        })
        .ListenToChanges(OnStandaloneAdvancedPropertiesChanged)
        .BuildWithScrollView(null);


    }
    #endregion

    #region history

    public View BuildClassicHistoryTab()
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

            if (parent.ProjectInfo.SnapshotIDMapping.TryGetValue(parent.CurrentSnapshotID, out var curPtr))
            {
                ToolTipProperties.SetText(slotLabel, $"forked from {curPtr.Previous}, next forks [{string.Join(", ", curPtr.Next)}]");
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
            tabView.SelectedItem.Content = BuildHistoryGraphTab();
        }
        catch (Exception ex)
        {
            parent.SetStateFail();
            parent.SetStatusText(Localized._ExceptionTemplate(ex));
        }
    }

    public View BuildHistoryGraphTab(bool listOnly = false)
    {
        // Standalone mode (no DraftPage) — use builder directly, read-only
        if (IsStandaloneJsonMode)
        {
            var standData = HistoryGraphDataBuilder.BuildStandalone(standaloneProjectPath);
            var standaloneGraph = new HistoryGraphView();
            standaloneGraph.ViewMode = HistoryViewMode.List;
            standaloneGraph.LoadHistory(standData.Nodes, standData.Edges,
                standData.Nodes.Count > 0 ? standData.Nodes[0].SnapshotID : Guid.Empty);
            HistoryTabContent = standaloneGraph;
            return standaloneGraph;
        }

        var provider = new DraftHistoryGraphProvider(parent);
        parent.RegisterHistoryProvider(provider);
        var (graphNodes, graphEdges) = provider.BuildGraphData();

        var historyView = new HistoryGraphView(provider);
        if (listOnly)
            historyView.ViewMode = HistoryViewMode.List;
        historyView.LoadHistory(graphNodes, graphEdges, parent.CurrentSnapshotID);

        HistoryTabContent = historyView;
        return historyView;
    }

    private View BuildHistoryLogsTab()
    {
        var root = new VerticalStackLayout
        {
            Spacing = 10,
            Padding = new Thickness(10)
        };

        var refreshButton = new Button
        {
            Text = Localized.DraftSettingPage_Advanced_ReloadHistory,
            HorizontalOptions = LayoutOptions.Start
        };
        refreshButton.Clicked += (_, _) =>
        {
            tabView.SelectedItem.Content = BuildHistoryLogsTab();
        };
        root.Children.Add(refreshButton);

        if (parent.HistoryLogs.Count == 0)
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
            return root;
        }

        var logs = parent.HistoryLogs
            .OrderByDescending(x => x.Key)
            .Take(500);

        foreach (var item in logs)
        {
            var level = item.Value.Level ?? "Info";
            var message = string.IsNullOrWhiteSpace(item.Value.Message) ? "(empty)" : item.Value.Message;
            var levelColor = level switch
            {
                "Error" => Colors.IndianRed,
                "Warning" => Colors.Goldenrod,
                _ => Colors.LightSkyBlue
            };

            var entry = new Border
            {
                Stroke = Color.FromArgb("#2AFFFFFF"),
                StrokeThickness = 1,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                Padding = new Thickness(10, 8),
                Content = new VerticalStackLayout
                {
                    Spacing = 4,
                    Children =
                    {
                        new Label
                        {
                            Text = $"{new DateTime(item.Key):yyyy-MM-dd HH:mm:ss.fff} · {level}",
                            TextColor = levelColor,
                            FontSize = 12,
                            FontAttributes = FontAttributes.Bold
                        },
                        new Label
                        {
                            Text = message,
                            FontSize = 13,
                            LineBreakMode = LineBreakMode.WordWrap
                        }
                    }
                }
            };
            root.Children.Add(entry);
        }

        return new ScrollView { Content = root };
    }

    private async Task ShowCleanupOptionsAsync()
    {
        if (GetHostPage() is not Page hostPage)
        {
            return;
        }

        string result = await hostPage.DisplayActionSheetAsync(
            Localized.DraftSettingPage_Tab_History_CleanupAll_Title,
            Localized._Cancel,
            Localized.DraftSettingPage_Tab_History_CleanupAll,
            Localized.DraftSettingPage_Tab_History_HoursAgo(1),
            Localized.DraftSettingPage_Tab_History_HoursAgo(24),
            Localized.DraftSettingPage_Tab_History_HoursAgo(48),
            Localized.DraftSettingPage_Tab_History_HoursAgo(72),
            Localized.DraftSettingPage_Tab_History_DaysAgo(7),
            Localized.DraftSettingPage_Tab_History_DaysAgo(14)
        );

        if (string.IsNullOrWhiteSpace(result) || result == Localized._Cancel)
        {
            return;
        }

        var cutoffTime = result switch
        {
            _ when result == Localized.DraftSettingPage_Tab_History_HoursAgo(1) => DateTime.Now.AddHours(-1),
            _ when result == Localized.DraftSettingPage_Tab_History_HoursAgo(24) => DateTime.Now.AddHours(-24),
            _ when result == Localized.DraftSettingPage_Tab_History_HoursAgo(48) => DateTime.Now.AddHours(-48),
            _ when result == Localized.DraftSettingPage_Tab_History_HoursAgo(72) => DateTime.Now.AddHours(-72),
            _ when result == Localized.DraftSettingPage_Tab_History_DaysAgo(7) => DateTime.Now.AddDays(-7),
            _ when result == Localized.DraftSettingPage_Tab_History_DaysAgo(14) => DateTime.Now.AddDays(-14),
            _ => DateTime.Now.AddDays(1)
        };

        if (cutoffTime > DateTime.Now)
        {
            await ClearPastHistoryAsync();
        }
        else
        {
            await ClearHistoryBeforeDateAsync(cutoffTime, result);
        }

    }

    private async Task ClearHistoryBeforeDateAsync(DateTime cutoffDateTime, string cutoffLabel)
    {
        Guid currentSnapshotId;
        Dictionary<Guid, ProjectJSONStructure.SnapshotIDMappingStructure> mapping;
        List<SaveSlotHistoryItem> historyItems;
        ProjectJSONStructure? standaloneProjectInfo = null;
        string projectRoot = ResolveJsonProjectRoot();

        if (IsStandaloneJsonMode)
        {
            if (!TryLoadStandaloneProjectInfo(out var info, out var loadError))
            {
                await ShowInfoAsync(loadError);
                return;
            }

            standaloneProjectInfo = info;
            mapping = info.SnapshotIDMapping ?? [];
            currentSnapshotId = info.LastSnapshotID;
        }
        else
        {
            mapping = parent.ProjectInfo.SnapshotIDMapping ?? [];
            currentSnapshotId = parent.CurrentSnapshotID;
        }

        if (currentSnapshotId == Guid.Empty)
        {
            await ShowInfoAsync("Current snapshot is unavailable.");
            return;
        }

        historyItems = ReadSaveSlotHistory();
        var historyById = historyItems.ToDictionary(h => h.SnapshotID);

        var snapshotIdsToDelete = new List<Guid>();
        foreach (var kv in mapping)
        {
            if (kv.Key == currentSnapshotId)
            {
                continue;
            }

            if (historyById.TryGetValue(kv.Key, out var item) && item.SavedAt < cutoffDateTime)
            {
                snapshotIdsToDelete.Add(kv.Key);
            }
        }

        if (snapshotIdsToDelete.Count == 0)
        {
            if (IsStandaloneJsonMode)
            {
                await ShowInfoAsync(Localized.DraftSettingPage_Tab_History_CleanupAll_None);
            }
            else
            {
                parent.SetStateOK(Localized.DraftSettingPage_Tab_History_CleanupAll_None);
            }
            return;
        }

        bool confirm = await ConfirmAsync(
            Localized._Warn,
            Localized.DraftSettingPage_Tab_History_CleanupAll_WarnRange(cutoffLabel, snapshotIdsToDelete.Count)
        );
        if (!confirm)
        {
            return;
        }

        try
        {
            foreach (var snapshotId in snapshotIdsToDelete)
            {
                DeleteSaveSlotDirectory(projectRoot, snapshotId);

                if (mapping.TryGetValue(snapshotId, out var entry))
                {
                    if (entry.Previous != Guid.Empty && mapping.TryGetValue(entry.Previous, out var prevEntry))
                    {
                        prevEntry.Next.Remove(snapshotId);
                        foreach (var nextId in entry.Next)
                        {
                            if (!prevEntry.Next.Contains(nextId))
                                prevEntry.Next.Add(nextId);
                        }
                    }
                    foreach (var nextId in entry.Next)
                    {
                        if (mapping.TryGetValue(nextId, out var nextEntry))
                            mapping[nextId] = nextEntry with { Previous = entry.Previous };
                    }
                }

                mapping.Remove(snapshotId);
            }

            if (IsStandaloneJsonMode && standaloneProjectInfo is not null)
            {
                standaloneProjectInfo.SnapshotIDMapping = mapping;
                await SaveStandaloneProjectInfo(standaloneProjectInfo);
                await ShowInfoAsync(Localized._Done);
            }
            else
            {
                parent.ProjectInfo.SnapshotIDMapping = mapping;
                string projectFilePath = ResolveLiveProjectFilePath();
                if (string.IsNullOrWhiteSpace(projectFilePath))
                {
                    throw new InvalidOperationException("Project file path is invalid.");
                }

                await System.IO.File.WriteAllTextAsync(projectFilePath, System.Text.Json.JsonSerializer.Serialize(parent.ProjectInfo, DraftPage.DraftJSONOption));
                parent.ProjectInfo.SaveSnapshotMapping(System.IO.Path.GetDirectoryName(projectFilePath)!, DraftPage.DraftJSONOption);
                parent.SetStateOK(Localized._Done);
            }

            tabView.SelectedItem.Content = BuildHistoryGraphTab();
        }
        catch (Exception ex)
        {
            if (IsStandaloneJsonMode)
            {
                await ShowInfoAsync($"Failed to clear history: {ex.Message}");
            }
            else
            {
                parent.SetStateFail();
                parent.SetStatusText(Localized._ExceptionTemplate(ex));
            }
        }
    }

    private async Task ClearPastHistoryAsync()
    {
        Guid currentSnapshotId;
        Dictionary<Guid, ProjectJSONStructure.SnapshotIDMappingStructure> mapping;
        ProjectJSONStructure? standaloneProjectInfo = null;
        string projectRoot = ResolveJsonProjectRoot();

        if (IsStandaloneJsonMode)
        {
            if (!TryLoadStandaloneProjectInfo(out var info, out var loadError))
            {
                await ShowInfoAsync(loadError);
                return;
            }

            standaloneProjectInfo = info;
            mapping = info.SnapshotIDMapping ?? [];
            currentSnapshotId = info.LastSnapshotID;
        }
        else
        {
            mapping = parent.ProjectInfo.SnapshotIDMapping ?? [];
            currentSnapshotId = parent.CurrentSnapshotID;
        }

        if (currentSnapshotId == Guid.Empty)
        {
            await ShowInfoAsync("Current snapshot is unavailable.");
            return;
        }

        if (!mapping.TryGetValue(currentSnapshotId, out var currentEntry) || currentEntry.Previous == Guid.Empty)
        {
            if (IsStandaloneJsonMode)
            {
                await ShowInfoAsync(Localized.DraftSettingPage_Tab_History_CleanupAll_None);
            }
            else
            {
                parent.SetStateOK(Localized.DraftSettingPage_Tab_History_CleanupAll_None);
            }
            return;
        }

        bool confirm = await ConfirmAsync(Localized._Warn, Localized.DraftSettingPage_Tab_History_CleanupAll_Warn);
        if (!confirm)
        {
            return;
        }

        try
        {
            var pastSnapshotIds = CollectPastSnapshotIds(currentSnapshotId, mapping);
            foreach (var snapshotId in pastSnapshotIds)
            {
                DeleteSaveSlotDirectory(projectRoot, snapshotId);
                mapping.Remove(snapshotId);
            }

            if (mapping.TryGetValue(currentSnapshotId, out var entryAfterCleanup))
            {
                mapping[currentSnapshotId] = entryAfterCleanup with { Previous = Guid.Empty };
            }

            if (IsStandaloneJsonMode && standaloneProjectInfo is not null)
            {
                standaloneProjectInfo.SnapshotIDMapping = mapping;
                await SaveStandaloneProjectInfo(standaloneProjectInfo);
                await ShowInfoAsync(Localized._Done);
            }
            else
            {
                parent.ProjectInfo.SnapshotIDMapping = mapping;
                string projectFilePath = ResolveLiveProjectFilePath();
                if (string.IsNullOrWhiteSpace(projectFilePath))
                {
                    throw new InvalidOperationException("Project file path is invalid.");
                }

                await System.IO.File.WriteAllTextAsync(projectFilePath, System.Text.Json.JsonSerializer.Serialize(parent.ProjectInfo, DraftPage.DraftJSONOption));
                parent.ProjectInfo.SaveSnapshotMapping(System.IO.Path.GetDirectoryName(projectFilePath)!, DraftPage.DraftJSONOption);
                parent.SetStateOK(Localized._Done);
            }

            tabView.SelectedItem.Content = BuildHistoryGraphTab();
        }
        catch (Exception ex)
        {
            if (IsStandaloneJsonMode)
            {
                await ShowInfoAsync($"Failed to clear past history: {ex.Message}");
            }
            else
            {
                parent.SetStateFail();
                parent.SetStatusText(Localized._ExceptionTemplate(ex));
            }
        }
    }

    private static List<Guid> CollectPastSnapshotIds(
        Guid currentSnapshotId,
        Dictionary<Guid, ProjectJSONStructure.SnapshotIDMappingStructure> mapping)
    {
        var result = new List<Guid>();
        var visited = new HashSet<Guid>();
        var cursor = currentSnapshotId;

        while (cursor != Guid.Empty
            && visited.Add(cursor)
            && mapping.TryGetValue(cursor, out var currentEntry)
            && currentEntry.Previous != Guid.Empty)
        {
            Guid previousId = currentEntry.Previous;
            result.Add(previousId);
            cursor = previousId;
        }

        return result;
    }

    private static void DeleteSaveSlotDirectory(string projectRoot, Guid snapshotId)
    {
        if (string.IsNullOrWhiteSpace(projectRoot) || snapshotId == Guid.Empty)
        {
            return;
        }

        string slotPath = System.IO.Path.Combine(projectRoot, SaveSlotDirectoryName, $"slot_{snapshotId}");
        if (System.IO.Directory.Exists(slotPath))
        {
            System.IO.Directory.Delete(slotPath, true);
        }
    }

    private string ResolveLiveProjectFilePath()
    {
        string projectRoot = ResolveJsonProjectRoot();
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return string.Empty;
        }

        string pjfcPath = System.IO.Path.Combine(projectRoot, "project.pjfc");
        if (System.IO.File.Exists(pjfcPath))
        {
            return pjfcPath;
        }

        return System.IO.Path.Combine(projectRoot, "project.json");
    }

    private async Task RestoreSlotStandalone(Guid snapshotId, string date)
    {
        if (string.IsNullOrWhiteSpace(standaloneProjectPath)) return;

        string slotDir = System.IO.Path.Combine(standaloneProjectPath, SaveSlotDirectoryName, $"slot_{snapshotId}");
        string srcTimeline = System.IO.Path.Combine(slotDir, "timeline.json");
        string srcAssets = System.IO.Path.Combine(slotDir, "assets.json");

        if (!System.IO.File.Exists(srcTimeline))
        {
            await ShowInfoAsync("Snapshot data not found on disk.");
            return;
        }

        bool confirm = await ConfirmAsync(Localized._Warn, Localized.DraftSettingPage_Tab_History_ApplyWarn(date));
        if (!confirm) return;

        try
        {
            string dstTimeline = System.IO.Path.Combine(standaloneProjectPath, "timeline.json");
            string dstAssets = System.IO.Path.Combine(standaloneProjectPath, "assets.json");

            if (System.IO.File.Exists(dstTimeline))
            {
                string backupTimeline = dstTimeline + ".backup";
                System.IO.File.Copy(dstTimeline, backupTimeline, overwrite: true);
            }
            if (System.IO.File.Exists(dstAssets))
            {
                string backupAssets = dstAssets + ".backup";
                System.IO.File.Copy(dstAssets, backupAssets, overwrite: true);
            }

            System.IO.File.Copy(srcTimeline, dstTimeline, overwrite: true);
            if (System.IO.File.Exists(srcAssets))
            {
                System.IO.File.Copy(srcAssets, dstAssets, overwrite: true);
            }

            await ShowInfoAsync(Localized._Done);
            tabView.SelectedItem.Content = BuildHistoryGraphTab();
        }
        catch (Exception ex)
        {
            await ShowInfoAsync($"Failed to restore snapshot: {ex.Message}");
        }
    }

    internal List<SaveSlotHistoryItem> ReadSaveSlotHistory()
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

    #endregion

    #region mgnt
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

        // Effect management now runs on the IEffectProvider system.
        // EffectProviders is the preferred shape; legacy EffectBundles is still auto-migrated
        // when no provider data exists (keep-for-compatibility read). If plugins are not loaded
        // or migration fails, the effect section shows a fallback message instead of crashing.
        var effectProviders = new List<(IEffectProvider Provider, bool MigratedFromBundle)>();
        string? providerLoadError = null;
        try
        {
            if (clip.EffectProviders is { Length: > 0 } || clip.EffectBundles is { Length: > 0 })
            {
                var factories = EffectServices.GetAvailableEffectProviders();
                if (factories.Count == 0)
                {
                    providerLoadError = "Effect providers are unavailable: the plugin system has not been initialized.";
                }
                else if (clip.EffectProviders is { Length: > 0 })
                {
                    var restored = EffectBindingHelper.MigrateToEffectProviders(clip.EffectProviders, null);
                    effectProviders.AddRange(restored.Values.Select(p => (p, false)));
                }
                else
                {
                    var restored = EffectBindingHelper.MigrateToEffectProviders(null, clip.EffectBundles);
                    effectProviders.AddRange(restored.Values.Select(p => (p, true)));
                }
            }
        }
        catch (Exception ex)
        {
            providerLoadError = $"Failed to load effect providers: {ex.Message}";
        }

        var sortedProviders = effectProviders
            .OrderBy(x => x.Provider.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Provider.TypeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Provider.Id)
            .ToList();

        layout.Children.Add(new Label
        {
            Text = SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResources?.Tabs_Effect ?? "Effect",
            FontSize = 13,
            FontAttributes = FontAttributes.Bold,
            Margin = new Thickness(0, 8, 0, 0)
        });

        if (providerLoadError is not null)
        {
            layout.Children.Add(new Label
            {
                Text = providerLoadError,
                FontSize = 11,
                TextColor = Colors.IndianRed,
                LineBreakMode = LineBreakMode.CharacterWrap
            });
        }
        else if (sortedProviders.Count == 0)
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
            foreach (var item in sortedProviders)
            {
                var provider = item.Provider;
                var providerInfo = new Label
                {
                    Text = BuildStandaloneEffectProviderSummary(provider, item.MigratedFromBundle),
                    FontSize = 11,
                    LineBreakMode = LineBreakMode.CharacterWrap,
                    Opacity = 0.9
                };

                var deleteProviderButton = new Button
                {
                    Text = Localized.DraftPage_ContextMenu_Delete,
                    BackgroundColor = Colors.OrangeRed,
                    FontSize = 11,
                    Padding = new Thickness(8, 4),
                    HorizontalOptions = LayoutOptions.Start
                };

                deleteProviderButton.Clicked += async (_, _) =>
                {
                    bool confirmProviderDelete = await ConfirmAsync(
                        Localized._Warn,
                        Localized.HomePage_ProjectContextMenu_Delete_Confirm0($"'{provider.Name}' ({provider.TypeName}@'{clip.Name}')"));
                    if (!confirmProviderDelete)
                    {
                        return;
                    }

                    if (!RemoveStandaloneEffectProvider(clip, provider.Id))
                    {
                        await ShowInfoAsync("Effect provider not found.");
                        return;
                    }

                    SetEditableClipDtos(draft, clips);
                    await SaveJsonProjectDataAsync(projectRoot, draft, assets, Localized._Done);
                    tabView.SelectedItem.Content = BuildClipAndAssetManageTab();
                };

                var providerCard = new VerticalStackLayout
                {
                    Spacing = 4,
                    Padding = new Thickness(8, 6),
                    BackgroundColor = new Color(1f, 1f, 1f, 0.03f),
                    Children = { providerInfo, deleteProviderButton }
                };
                layout.Children.Add(providerCard);
            }
        }

        var bundleIdSet = new HashSet<Guid>(effectProviders.Select(x => x.Provider.Id));
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

    /// <summary>
    /// Removes an <see cref="IEffectProvider"/> from the clip's provider-native JSON array.
    /// The provider's output binding is cleared as well (a provider being the final output would
    /// otherwise leave a dangling final-output marker in the stored configuration).
    /// </summary>
    private static bool RemoveStandaloneEffectProvider(ClipDraftDTO clip, Guid providerId)
    {
        if (clip.EffectProviders is not { Length: > 0 } || providerId == Guid.Empty)
        {
            return false;
        }

        var providers = clip.EffectProviders.ToList();
        int removeIndex = providers.FindIndex(p => p.Id == providerId);
        if (removeIndex < 0)
        {
            return false;
        }

        providers.RemoveAt(removeIndex);
        if (providers.Count > 0)
        {
            string removedId = providerId.ToString();
            foreach (var provider in providers)
            {
                if (provider.AnchorsBindingState is { } state
                    && state.TryGetValue(EffectProviderAnchorExtensions.OutputKey, out var output)
                    && output == removedId)
                {
                    state[EffectProviderAnchorExtensions.OutputKey] = IEffectProvider.NoConnectionGUID.ToString();
                }
            }
        }
        clip.EffectProviders = providers.Count == 0 ? null : providers.ToArray();
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

    private static string BuildStandaloneEffectSummary(EffectAndMixtureJSONStructure effect)
    {
        int parameterCount = effect.Parameters?.Count ?? 0;
        string bindingId = string.IsNullOrWhiteSpace(effect.BindedEffectGroupID) ? "(none)" : effect.BindedEffectGroupID;
        return $"Name: {effect.Name} | ClipType: {effect.TypeName} | Index: {effect.Index} | Enabled: {effect.Enabled}\n"
             + $"Implement: {effect.ImplementType} | Params: {parameterCount} | BindedEffectProvidingSystemID: {bindingId}";
    }

    /// <summary>
    /// Builds a read-only summary of an <see cref="IEffectProvider"/> instance for the standalone
    /// clip-management card. <paramref name="migratedFromBundle"/> marks providers restored from a
    /// legacy <c>EffectBundles</c> array so the UI can hint that they are still on the old format.
    /// </summary>
    private static string BuildStandaloneEffectProviderSummary(IEffectProvider provider, bool migratedFromBundle)
    {
        if (provider is null) return "(null provider)";

        int parameterCount = provider.Fields?.Count ?? 0;
        int bindingCount = provider.AnchorsBindingState?.Count ?? 0;
        string target = provider.Target.ToString();
        string input = provider.GetMainInputSource();
        string inputDisplay = input == IEffectProvider.InputAnchorGUID.ToString()
            ? "Source"
            : input == IEffectProvider.NoConnectionGUID.ToString()
                ? "(none)"
                : input;
        string output = provider.IsFinalOutputSource() ? "Final" : "(none)";
        string legacyHint = migratedFromBundle ? " [from legacy bundle]" : string.Empty;
        return $"Name: {provider.Name} | Type: {provider.TypeName} | Id: {provider.Id} | Enabled: {provider.Enabled}\n"
             + $"Target: {target} | Input: {inputDisplay} | Output: {output} | Fields: {parameterCount} | Bindings: {bindingCount}{legacyHint}";
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
                .Distinct()
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

    #endregion

    #region update


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
            project.SnapshotIDMapping = ProjectJSONStructure.LoadSnapshotMapping(projectRoot, DraftPage.DraftJSONOption);
            if (project.SnapshotIDMapping.Count == 0)
            {
                project.SnapshotIDMapping = ProjectJSONStructure.RebuildSnapshotMappingFromSlots(projectRoot, DraftPage.DraftJSONOption);
            }
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
            info.SnapshotIDMapping = ProjectJSONStructure.LoadSnapshotMapping(standaloneProjectPath, DraftPage.DraftJSONOption);
            if (info.SnapshotIDMapping.Count == 0)
            {
                info.SnapshotIDMapping = ProjectJSONStructure.RebuildSnapshotMappingFromSlots(standaloneProjectPath, DraftPage.DraftJSONOption);
            }
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
        info.SaveSnapshotMapping(standaloneProjectPath, DraftPage.DraftJSONOption);
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
    #endregion

    #region misc

    private static void SetEditableClipDtos(DraftStructureJSON draft, IEnumerable<ClipDraftDTO> clips)
    {
        draft.Clips = clips.ToArray();
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
        parent.Clips = new System.Collections.Concurrent.ConcurrentDictionary<Guid, ClipElementUI>(clips);
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

    private static bool IsRealClipId(Guid clipId)
        => clipId != Guid.Empty;

    private static int RemoveStandaloneClipWithDependents(DraftStructureJSON draft, Guid clipId)
    {
        if (clipId == Guid.Empty)
        {
            return 0;
        }

        var clips = GetEditableClipDtos(draft);
        var idsToDelete = new HashSet<Guid>
        {
            clipId,
        };

        foreach (var clip in clips)
        {
            if (clip.ClipType != ClipMode.TransformClip)
            {
                continue;
            }

            bool linkedByPrev = clip.MetaData?.TryGetValue("transformPrevId", out var prev) == true
                && Guid.TryParse(prev?.ToString(), out var prevGuid) && prevGuid == clipId;
            bool linkedByNext = clip.MetaData?.TryGetValue("transformNextId", out var next) == true
                && Guid.TryParse(next?.ToString(), out var nextGuid) && nextGuid == clipId;

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


    private async Task RefreshDraftAfterManualMutate(string successText)
    {
        var draft = DraftImportAndExportHelper.ExportFromDraftPage(parent, includeUiOnlyClips: false);
        parent.ProjectDuration = Math.Max(draft.Duration, draft.AudioDuration);

        await parent.ClipEditor.UpdateClips(parent.Clips);
        parent.ClipEditor.SetCurrentFrame((uint)Math.Max(0, parent.CurrentFrame));
        await parent.previewer.UpdateDraft(draft);
        parent.DynamicPreviewProvider.SetClips(parent.previewer.Clips);
        await parent.Save();
        parent.SetStateOK(successText);
    }

    private async void UpgradePlaceResizeButton_Clicked(object? sender, EventArgs e)
    {
        try
        {
            parent.SetStateBusy(Localized.DraftPage_ApplyingChanges);

            var clips = parent.Clips.Values
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
                parent.DynamicPreviewProvider.SetClips(parent.previewer.Clips);
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
    #endregion
}

