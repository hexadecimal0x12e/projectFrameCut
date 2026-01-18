
using projectFrameCut.ApplicationAPIBase.PropertyPanelBuilders;
using projectFrameCut.Controls;


namespace projectFrameCut.DraftStuff;

public class DraftSettingPage
{
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
            Header = Localized.MainSettingsPage_Tab_Misc,
            Content = BuildAdvancedTab()
        });

    }
    static string[] resolutions = ["640x480", "1280x720", "1920x1080", "2560x1440", "3840x2160", Localized.DraftPage_PrevResultion_Custom];

    public ScrollView BuildGeneralTab()
    {
        PropertyPanelBuilder ppb = new();
        ppb.AddEntry("targetFrameRate", Localized.DraftSettingPage_General_TargetFramerate, parent.ProjectInfo.targetFrameRate.ToString(), "60", null, default);
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
                    parent.ProjectInfo.targetFrameRate = result;
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