using CommunityToolkit.Maui.Views;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.InteractableEditor;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace projectFrameCut.ApplicationPluginBase.Effect
{
    /// <summary>
    /// Custom property UI of the ProgressCrop effect, including the keyframe-step editing UI.
    /// </summary>
    public class ProgressCropUI : EffectProviderUI, IKeyFramedEffectProvider
    {
        private const string CropListKey = "CropList";

        public ProgressCropUI(IEffectProvider inner) : base(inner)
        {
        }

        public string TypeName => Inner.TypeName;

        public string FromPlugin => Inner.FromPlugin;

        public override PropertyPanelBuilder CreateUI(IEffectProvider _)
        {
            var panel = new PropertyPanelBuilder();
            panel.AddText(new SingleLineLabel(
                EffectProviderHelper.L("Effect_ProgressPlacer_Desc", "Configure keyframes in the Keyframe tab."), 14));
            return panel;
        }

        #region IKeyFramedEffectProvider

        IReadOnlyList<KeyFrameStepInfo> IKeyFramedEffectProvider.Steps
        {
            get
            {
                var list = GetCropList();
                return list.Select((item, idx) =>
                    new KeyFrameStepInfo(item.Index, $"Keyframe #{idx + 1} ({(item.Index * 100):F0}%)"))
                    .ToList();
            }
        }

        PropertyPanelBuilder IKeyFramedEffectProvider.CreateStepUI(int index)
        {
            var list = GetCropList();
            if (index < 0 || index >= list.Count)
                return new PropertyPanelBuilder();

            var item = list[index];
            var panel = new PropertyPanelBuilder();

            panel.AddSlider(
                $"step_progress_{index}",
                EffectProviderHelper.L("Effect_ProgressPlacer_Progress", "Progress"),
                0d, 1d, item.Index,
                eventCallMode: SliderUpdateEventCallMode.OnMouseUp);

            panel.AddButton(EffectProviderHelper.L("Effect_ProgressPlacer_OpenEditor", "Open editor"), async (s, e) =>
            {
                try
                {
                    if (Shell.Current?.CurrentPage is not DraftPage draftPage)
                        return;

                    var currentList = GetCropList();
                    if (index < 0 || index >= currentList.Count)
                        return;

                    var item = currentList[index];

                    var cropEditor = new ClipCropConfiguratorView
                    {
                        StartX = item.StartX,
                        StartY = item.StartY,
                        CropWidth = item.Width,
                        CropHeight = item.Height,
                        Angle = item.Angle,
                        RelativeWidth = 1920,
                        RelativeHeight = 1080
                    };

                    var stack = new VerticalStackLayout
                    {
                        Spacing = 10,
                        Padding = new Thickness(20)
                    };

                    stack.Children.Add(cropEditor);

                    var buttons = new HorizontalStackLayout { Spacing = 10, HorizontalOptions = LayoutOptions.End, Margin = new Thickness(0, 8, 0, 0) };

                    var cancelBtn = new Button
                    {
                        Text = Localized._Cancel,
                        BackgroundColor = Color.FromArgb("#3A3A3C"),
                        TextColor = Colors.White,
                        CornerRadius = 8,
                        Padding = new Thickness(16, 8)
                    };
                    cancelBtn.Clicked += async (_, _) => await draftPage.HidePopup();

                    var saveBtn = new Button
                    {
                        Text = Localized._Save,
                        BackgroundColor = Color.FromArgb("#4A90D9"),
                        TextColor = Colors.White,
                        CornerRadius = 8,
                        Padding = new Thickness(16, 8)
                    };
                    saveBtn.Clicked += async (_, _) =>
                    {
                        try
                        {
                            var updatedList = GetCropList();
                            if (index < 0 || index >= updatedList.Count) return;

                            updatedList[index] = new CropData(
                                item.Index,
                                cropEditor.StartX,
                                cropEditor.StartY,
                                cropEditor.CropWidth,
                                cropEditor.CropHeight,
                                cropEditor.Angle);
                            SaveCropList(updatedList);

                            await draftPage.HidePopup();

                            // Trigger UI refresh
                            PropertyPanelPropertyChangedEventArgs.CreateAndInvoke(panel, $"step_editor_refresh_{index}", null);
                        }
                        catch (Exception ex)
                        {
                            Log(ex, "Save progress cropper", this);
                        }
                    };

                    buttons.Children.Add(cancelBtn);
                    buttons.Children.Add(saveBtn);
                    stack.Children.Add(buttons);

                    await draftPage.ShowAPopup(stack, mode: "dialog");
                }
                catch (Exception ex)
                {
                    Log(ex, "Save progress cropper", this);
                }
            });

            panel.AddSeparator();

            panel.AddCollapsibleSection(
                EffectProviderHelper.L("Effect_Crop_Collapsible_Transform", "Transform"),
                contentPanel =>
                {
                    EffectProviderHelper.AddNumericEntry(
                        contentPanel, $"step_startX_{index}",
                        EffectProviderHelper.L("_StartX", "X"),
                        item.StartX.ToString(), "0");

                    EffectProviderHelper.AddNumericEntry(
                        contentPanel, $"step_startY_{index}",
                        EffectProviderHelper.L("_StartY", "Y"),
                        item.StartY.ToString(), "0");

                    EffectProviderHelper.AddNumericEntry(
                        contentPanel, $"step_w_{index}",
                        EffectProviderHelper.L("_Width", "W"),
                        item.Width.ToString(), "1");

                    EffectProviderHelper.AddNumericEntry(
                        contentPanel, $"step_h_{index}",
                        EffectProviderHelper.L("_Height", "H"),
                        item.Height.ToString(), "1");

                    contentPanel.AddSlider(
                        $"step_angle_{index}",
                        EffectProviderHelper.L("General_Rotation", "Rotation"),
                        -180d, 180d, item.Angle,
                        eventCallMode: SliderUpdateEventCallMode.OnMouseUp);
                });

            return panel;
        }

        bool IKeyFramedEffectProvider.HandleStepUIChange(int index, PropertyPanelPropertyChangedEventArgs args)
        {
            var list = GetCropList();
            if (index < 0 || index >= list.Count)
                return false;

            var item = list[index];
            var changed = false;

            if (args.Id == $"step_progress_{index}")
            {
                if (args.Value is double d)
                {
                    item = item with { Index = Math.Clamp(d, 0d, 1d) };
                    changed = true;
                }
                else if (double.TryParse(args.Value?.ToString(), out var parsed))
                {
                    item = item with { Index = Math.Clamp(parsed, 0d, 1d) };
                    changed = true;
                }
            }
            else if (args.Id == $"step_startX_{index}" && TryParseEntryInt(args.Value, out var x))
            {
                item = item with { StartX = x };
                changed = true;
            }
            else if (args.Id == $"step_startY_{index}" && TryParseEntryInt(args.Value, out var y))
            {
                item = item with { StartY = y };
                changed = true;
            }
            else if (args.Id == $"step_w_{index}" && TryParseEntryInt(args.Value, out var w))
            {
                item = item with { Width = Math.Max(1, w) };
                changed = true;
            }
            else if (args.Id == $"step_h_{index}" && TryParseEntryInt(args.Value, out var h))
            {
                item = item with { Height = Math.Max(1, h) };
                changed = true;
            }
            else if (args.Id == $"step_angle_{index}")
            {
                if (args.Value is double dAngle)
                {
                    item = item with { Angle = (float)dAngle };
                    changed = true;
                }
                else if (float.TryParse(args.Value?.ToString(), out var fAngle))
                {
                    item = item with { Angle = fAngle };
                    changed = true;
                }
            }
            else if (args.Id == $"step_editor_refresh_{index}")
            {
                return true;
            }

            if (changed)
            {
                list[index] = item;
                list.Sort((a, b) => a.Index.CompareTo(b.Index));
                SaveCropList(list);
            }

            return changed;
        }

        void IKeyFramedEffectProvider.AddStep(ClipPositionTuple defaultPosition)
        {
            var list = GetCropList();
            var nextProgress = list.Count == 0
                ? 0d
                : Math.Min(1d, list.Max(item => item.Index) + 0.1d);

            int defaultX = EffectProviderHelper.GetFieldInt(Inner.Fields, "StartX", 0);
            int defaultY = EffectProviderHelper.GetFieldInt(Inner.Fields, "StartY", 0);
            int defaultW = Math.Max(1, EffectProviderHelper.GetFieldInt(Inner.Fields, "Width", 1280));
            int defaultH = Math.Max(1, EffectProviderHelper.GetFieldInt(Inner.Fields, "Height", 720));
            float defaultAngle = EffectProviderHelper.GetFieldFloat(Inner.Fields, "Angle", 0f);

            list.Add(new CropData(nextProgress, defaultX, defaultY, defaultW, defaultH, defaultAngle));
            list.Sort((a, b) => a.Index.CompareTo(b.Index));
            SaveCropList(list);
        }

        void IKeyFramedEffectProvider.RemoveStep(int index)
        {
            var list = GetCropList();
            if (index >= 0 && index < list.Count)
            {
                list.RemoveAt(index);
                SaveCropList(list);
            }
        }

        void IKeyFramedEffectProvider.UpsertStep(double progress, ClipPositionTuple position)
        {
            var list = GetCropList();
            var clampedProgress = Math.Clamp(progress, 0d, 1d);
            var safeCrop = new CropData(
                clampedProgress,
                position.TargetX,
                position.TargetY,
                Math.Max(1, position.TargetWidth),
                Math.Max(1, position.TargetHeight),
                0f);

            var existingIndex = list.FindIndex(p => Math.Abs(p.Index - clampedProgress) <= 0.000001d);
            if (existingIndex >= 0)
            {
                list[existingIndex] = safeCrop;
            }
            else
            {
                list.Add(safeCrop);
            }

            list.Sort((a, b) => a.Index.CompareTo(b.Index));
            SaveCropList(list);
        }

        #endregion

        private List<CropData> GetCropList()
        {
            var raw = EffectProviderHelper.GetFieldRawValue(Inner.Fields, CropListKey);
            if (raw is null)
                return new List<CropData>();

            if (raw is List<CropData> list)
                return new List<CropData>(list);

            if (raw is CropData[] array)
                return new List<CropData>(array);

            string json = raw switch
            {
                JsonElement je => je.ValueKind == JsonValueKind.String ? je.GetString() ?? "[]" : je.GetRawText(),
                string s => s,
                _ => raw.ToString() ?? "[]"
            };

            if (string.IsNullOrWhiteSpace(json))
                return new List<CropData>();

            return JsonSerializer.Deserialize<List<CropData>>(json) ?? new List<CropData>();
        }

        private void SaveCropList(List<CropData> list)
        {
            EffectProviderHelper.SetFieldValue(Inner.Fields, CropListKey, JsonSerializer.Serialize(list), EffectArgumentFieldType.String);
        }

        private static bool TryParseEntryInt(object? value, out int result)
        {
            if (value is int i)
            {
                result = i;
                return true;
            }
            if (value is double d)
            {
                result = (int)Math.Round(d);
                return true;
            }
            if (int.TryParse(value?.ToString(), out result))
                return true;
            result = 0;
            return false;
        }
    }
}
