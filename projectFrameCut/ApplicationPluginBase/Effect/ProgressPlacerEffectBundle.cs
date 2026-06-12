using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.InteractableEditor;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace projectFrameCut.ApplicationPluginBase.Effect
{
    public class ProgressPlacerEffectBundle : IEffectBundle, IKeyFramedEffectProvider
    {
        private const string ProgressListKey = "ProgressList";

        public string TypeName => "ProgressPlacer";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectType TypeOfEffect => EffectType.ContinuousClipPositionProvider;
        public EffectTarget Target => EffectTarget.Video | EffectTarget.IsKeyFramed | EffectTarget.IsNotVisibleInNewEffectSelector;

        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "ProgressPlacer";
        public bool Enabled { get; set; } = true;

        public Guid BindedInputId { get; set; } = IEffectBundle.InputAnchorGUID;
        public Guid BindedOutputId { get; set; } = IEffectBundle.OutputAnchorGUID;
        public List<Guid>? BindedInputIds { get; set; }
        public bool IsMultiInput => false;
        public bool IsUserAddableEffect => false;

        public string InputAnchorDisplayName => string.Empty;
        public string[]? InputAnchorsDisplayName => null;
        public string OutputAnchorDisplayName => string.Empty;

        public int StartPoint { get; set; }
        public int EndPoint { get; set; }

        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>
        {
            { ProgressListKey, "[]" }
        };

        public List<string> ParametersNeeded => new List<string> { ProgressListKey };
        public Dictionary<string, string> ParametersType => new Dictionary<string, string>
        {
            { ProgressListKey, "string" }
        };

        public IEffectFactory[] Create()
        {
            var factory = new ProgressPlacerFactory();
            this.ConfigureFactory(factory);
            return [factory];
        }

        public PropertyPanelBuilder CreateUI()
        {
            var panel = new PropertyPanelBuilder();
            panel.AddText(new SingleLineLabel(
                EffectBundleUiHelper.L("Effect_ProgressPlacer_Desc", "Configure keyframes in the Keyframe tab."), 14));
            return panel;
        }

        public Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            Parameters[args.Id] = args.Value;
            return Parameters;
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = EffectBundleUiHelper.L("DisplayName_Effect_ProgressPlacer", "Progress Placer"),
                Description = EffectBundleUiHelper.L("Description_Effect_ProgressPlacer", "Animate clip position, size via keyframes."),
                Thumbnail = ImageHelper.LoadFromAsset("icon_add")
            };
        }

        #region IKeyFramedEffectProvider

        IReadOnlyList<KeyFrameStepInfo> IKeyFramedEffectProvider.Steps
        {
            get
            {
                var list = GetProgressList();
                return list.Select((item, idx) =>
                    new KeyFrameStepInfo(item.Index, $"Keyframe #{idx + 1} ({(item.Index * 100):F0}%)"))
                    .ToList();
            }
        }

        PropertyPanelBuilder IKeyFramedEffectProvider.CreateStepUI(int index)
        {
            var list = GetProgressList();
            if (index < 0 || index >= list.Count)
                return new PropertyPanelBuilder();

            var item = list[index];
            var panel = new PropertyPanelBuilder();

            panel.AddSlider(
                $"step_progress_{index}",
                EffectBundleUiHelper.L("Effect_ProgressPlacer_Progress", "Progress"),
                0d, 1d, item.Index,
                eventCallMode: SliderUpdateEventCallMode.OnMouseUp);


            panel.AddButton(EffectBundleUiHelper.L("Effect_ProgressPlacer_OpenEditor", "Open editor"), async (s, e) =>
            {
                try
                {
                    if (Shell.Current?.CurrentPage is not DraftPage draftPage)
                        return;

                    var currentList = GetProgressList();
                    if (index < 0 || index >= currentList.Count)
                        return;

                    var item = currentList[index];

                    var posEditor = new ClipPlaceConfiguratorView
                    {
                        TargetX = item.Position.TargetX,
                        TargetY = item.Position.TargetY,
                        TargetWidth = item.Position.TargetWidth,
                        TargetHeight = item.Position.TargetHeight,
                        RelativeWidth = 1920,
                        RelativeHeight = 1080
                    };

                    var stack = new VerticalStackLayout
                    {
                        Spacing = 10,
                        Padding = new Thickness(20)
                    };


                    stack.Children.Add(posEditor);

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
                            var updatedList = GetProgressList();
                            if (index < 0 || index >= updatedList.Count) return;

                            updatedList[index] = new ProgressData(
                                item.Index,
                                posEditor.BuildPositionTuple());
                            SaveProgressList(updatedList);

                            await draftPage.HidePopup();

                            // Trigger UI refresh so the keyframe list picks up the changes
                            PropertyPanelPropertyChangedEventArgs.CreateAndInvoke(panel, $"step_editor_refresh_{index}", null);
                        }
                        catch (Exception ex)
                        {
                            Log(ex, "apply change in ProgressPlacer", this);
                        }
                    };

                    buttons.Children.Add(cancelBtn);
                    buttons.Children.Add(saveBtn);
                    stack.Children.Add(buttons);

                    await draftPage.ShowAPopup(stack, mode: "dialog");
                }
                catch (Exception ex)
                {
                    Log(ex, "apply change in ProgressPlacer", this);
                }
            });

            panel.AddSeparator();

            panel.AddCollapsibleSection(
                EffectBundleUiHelper.L("Effect_Placer_Collapsible_Position", "Position"),
                contentPanel =>
                {
                    EffectBundleUiHelper.AddNumericEntry(
                        contentPanel, $"step_x_{index}",
                        EffectBundleUiHelper.L("_StartX", "X"),
                        item.Position.TargetX.ToString(), "0");

                    EffectBundleUiHelper.AddNumericEntry(
                        contentPanel, $"step_y_{index}",
                        EffectBundleUiHelper.L("_StartY", "Y"),
                        item.Position.TargetY.ToString(), "0");

                    EffectBundleUiHelper.AddNumericEntry(
                        contentPanel, $"step_w_{index}",
                        EffectBundleUiHelper.L("_Width", "W"),
                        item.Position.TargetWidth.ToString(), "1");

                    EffectBundleUiHelper.AddNumericEntry(
                        contentPanel, $"step_h_{index}",
                        EffectBundleUiHelper.L("_Height", "H"),
                        item.Position.TargetHeight.ToString(), "1");
                });

            return panel;
        }

        bool IKeyFramedEffectProvider.HandleStepUIChange(int index, PropertyPanelPropertyChangedEventArgs args)
        {
            var list = GetProgressList();
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
            else if (args.Id == $"step_x_{index}" && TryParseEntryInt(args.Value, out var x))
            {
                item = item with { Position = item.Position with { TargetX = x } };
                changed = true;
            }
            else if (args.Id == $"step_y_{index}" && TryParseEntryInt(args.Value, out var y))
            {
                item = item with { Position = item.Position with { TargetY = y } };
                changed = true;
            }
            else if (args.Id == $"step_w_{index}" && TryParseEntryInt(args.Value, out var w))
            {
                item = item with { Position = item.Position with { TargetWidth = Math.Max(1, w) } };
                changed = true;
            }
            else if (args.Id == $"step_h_{index}" && TryParseEntryInt(args.Value, out var h))
            {
                item = item with { Position = item.Position with { TargetHeight = Math.Max(1, h) } };
                changed = true;
            }
            else if (args.Id == $"step_editor_refresh_{index}")
            {
                return true;
            }

            if (changed)
            {
                list[index] = item;
                list.Sort((a, b) => a.Index.CompareTo(b.Index));
                SaveProgressList(list);
            }

            return changed;
        }

        void IKeyFramedEffectProvider.AddStep(ClipPositionTuple defaultPosition)
        {
            var list = GetProgressList();
            var nextProgress = list.Count == 0
                ? 0d
                : Math.Min(1d, list.Max(item => item.Index) + 0.1d);
            list.Add(new ProgressData(nextProgress, defaultPosition));
            list.Sort((a, b) => a.Index.CompareTo(b.Index));
            SaveProgressList(list);
        }

        void IKeyFramedEffectProvider.RemoveStep(int index)
        {
            var list = GetProgressList();
            if (index >= 0 && index < list.Count)
            {
                list.RemoveAt(index);
                SaveProgressList(list);
            }
        }

        void IKeyFramedEffectProvider.UpsertStep(double progress, ClipPositionTuple position)
        {
            var list = GetProgressList();
            var clampedProgress = Math.Clamp(progress, 0d, 1d);
            var safePosition = new ClipPositionTuple(
                position.TargetX, position.TargetY,
                Math.Max(1, position.TargetWidth), Math.Max(1, position.TargetHeight),
                false);

            var existingIndex = list.FindIndex(p => Math.Abs(p.Index - clampedProgress) <= 0.000001d);
            if (existingIndex >= 0)
            {
                list[existingIndex] = new ProgressData(clampedProgress, safePosition);
            }
            else
            {
                list.Add(new ProgressData(clampedProgress, safePosition));
            }

            list.Sort((a, b) => a.Index.CompareTo(b.Index));
            SaveProgressList(list);
        }

        #endregion

        private List<ProgressData> GetProgressList()
        {
            if (!Parameters.TryGetValue(ProgressListKey, out var raw))
                return new List<ProgressData>();

            return ParseProgressList(raw);
        }

        private void SaveProgressList(List<ProgressData> list)
        {
            Parameters[ProgressListKey] = JsonSerializer.Serialize(list);
        }

        private static List<ProgressData> ParseProgressList(object? value)
        {
            if (value is null)
                return new List<ProgressData>();

            if (value is List<ProgressData> list)
                return list;

            if (value is ProgressData[] array)
                return new List<ProgressData>(array);

            if (value is JsonElement element)
            {
                var json = element.ValueKind == JsonValueKind.String
                    ? element.GetString()
                    : element.GetRawText();
                return string.IsNullOrWhiteSpace(json)
                    ? new List<ProgressData>()
                    : JsonSerializer.Deserialize<List<ProgressData>>(json) ?? new List<ProgressData>();
            }

            if (value is string jsonString)
            {
                return string.IsNullOrWhiteSpace(jsonString)
                    ? new List<ProgressData>()
                    : JsonSerializer.Deserialize<List<ProgressData>>(jsonString) ?? new List<ProgressData>();
            }

            return new List<ProgressData>();
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
