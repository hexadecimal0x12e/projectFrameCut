using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Views.Pickers;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace projectFrameCut.ApplicationPluginBase.Effect
{
    public class RemoveColorEffectBundle : IEffectBundle
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string Name { get; set; } = string.Empty;

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>
        {
            { "R", (ushort)0 },
            { "G", (ushort)0 },
            { "B", (ushort)0 },
            { "A", ushort.MaxValue },
            { "Tolerance", (ushort)1200 },
        };

        private static readonly Dictionary<string, EffectBundleSettableFields> s_settableFields = new()
        {
            {
                "Color", new EffectBundleSettableFields
                {
                    Id = "Color",
                    DisplayName = "Color",
                    Description = "Color to remove (16-bit RGBA)",
                    ValueType = EffectBundleSettableFields.FieldType.Color,
                    DefaultValue = """{"r":0,"g":0,"b":0,"a":1.0}""",
                    MinValue = "",
                    MaxValue = "",
                }
            },
            { "Tolerance", EffectBundleHelper.UShortField("Tolerance", "Tolerance", "Color removal tolerance", (ushort)1200, (ushort)0, ushort.MaxValue) },
        };

        public Dictionary<string, EffectBundleSettableFields> SettableFields => s_settableFields;

        public bool HandleSettableFieldsChange(EffectBundleSettableFields field, object value, out string feedback)
        {
            if (field.Id == "Color")
            {
                return TrySetColor(value, out feedback);
            }

            return EffectBundleHelper.HandleSettableFieldChange(Parameters, field, value, out feedback);
        }

        private bool TrySetColor(object value, out string feedback)
        {
            // Handle Color object (from ColorPicker / UI)
            if (value is Color color)
            {
                Parameters["R"] = (ushort)(color.Red * 65535.0);
                Parameters["G"] = (ushort)(color.Green * 65535.0);
                Parameters["B"] = (ushort)(color.Blue * 65535.0);
                Parameters["A"] = (ushort)(color.Alpha * 65535.0);
                feedback = "";
                return true;
            }

            // Handle JsonElement (from serialization/scripting — documented format: {"r":65535,"g":65535,"b":65535,"a":1.0})
            if (value is JsonElement json)
            {
                TrySetComponent(json, "r", "R");
                TrySetComponent(json, "g", "G");
                TrySetComponent(json, "b", "B");
                if (json.TryGetProperty("a", out var aProp)
                    && aProp.ValueKind == JsonValueKind.Number
                    && aProp.TryGetDouble(out var aDouble))
                {
                    Parameters["A"] = (ushort)(aDouble * 65535.0);
                }
                feedback = "";
                return true;
            }

            // Handle Dictionary<string, object> (from programmatic access)
            if (value is Dictionary<string, object> dict)
            {
                TryGetUShort(dict, "r", "R");
                TryGetUShort(dict, "g", "G");
                TryGetUShort(dict, "b", "B");
                if (dict.TryGetValue("a", out var aRaw) && aRaw is double aDbl)
                {
                    Parameters["A"] = (ushort)(aDbl * 65535.0);
                }
                feedback = "";
                return true;
            }

            feedback = $"Unsupported value type '{value?.GetType().Name}' for Color field.";
            return false;
        }

        private void TrySetComponent(JsonElement json, string jsonKey, string paramKey)
        {
            if (json.TryGetProperty(jsonKey, out var prop)
                && EffectBundleHelper.TryConvertToUShort(prop, out var val))
            {
                Parameters[paramKey] = val;
            }
        }

        private void TryGetUShort(Dictionary<string, object> dict, string dictKey, string paramKey)
        {
            if (dict.TryGetValue(dictKey, out var raw)
                && EffectBundleHelper.TryConvertToUShort(raw, out var val))
            {
                Parameters[paramKey] = val;
            }
        }

        public List<string> ParametersNeeded => new List<string>
        {
            "R",
            "G",
            "B",
            "A",
            "Tolerance",
        };

        public Dictionary<string, string> ParametersType => new Dictionary<string, string>
        {
            {"R", "ushort" },
            {"G", "ushort" },
            {"B", "ushort" },
            {"A", "ushort" },
            {"Tolerance", "ushort" },
        };

        public string TypeName => "RemoveColor";

        public bool IsNormalEffect => true;

        public bool IsContinuousEffect => false;

        public bool IsBindableEffect => false;

        public EffectType TypeOfEffect => EffectType.NormalEffect;

        public EffectTarget Target => EffectTarget.Video;

        public Guid BindedInputId { get; set; } = IEffectBundle.InputAnchorGUID;
        public Guid BindedOutputId { get; set; } = IEffectBundle.OutputAnchorGUID;
        public List<Guid>? BindedInputIds { get; set; }

        public bool IsMultiInput => false;
        public string InputAnchorDisplayName => string.Empty;
        public string[]? InputAnchorsDisplayName => null;
        public string OutputAnchorDisplayName => string.Empty;
        public bool Enabled { get; set; } = true;

        public int StartPoint { get; set; }
        public int EndPoint { get; set; }

        public IEffectFactory[] Create()
        {
            var factory = new RemoveColorEffectFactory();
            this.ConfigureFactory(factory);
            return new IEffectFactory[] { factory };
        }

        public PropertyPanelBuilder CreateUI()
        {
            ushort r = EffectBundleHelper.GetUShort(Parameters, "R", 0);
            ushort g = EffectBundleHelper.GetUShort(Parameters, "G", 0);
            ushort b = EffectBundleHelper.GetUShort(Parameters, "B", 0);
            ushort a = EffectBundleHelper.GetUShort(Parameters, "A", ushort.MaxValue);
            ushort tolerance = EffectBundleHelper.GetUShort(Parameters, "Tolerance", 1200);

            var panel = new PropertyPanelBuilder();

            // Color swatch that opens the ColorPicker overlay on DraftPage
            panel.AddCustomChild(
                EffectBundleHelper.ParamLabel("Color"),
                (invoker) =>
                {
                    var currentColor = Color.FromRgba(
                        r / 65535.0,
                        g / 65535.0,
                        b / 65535.0,
                        a / 65535.0);

                    var swatch = new Border
                    {
                        WidthRequest = 48,
                        HeightRequest = 32,
                        BackgroundColor = currentColor,
                        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                        StrokeThickness = 2,
                        Stroke = Color.FromArgb("#3A3B41"),
                        HorizontalOptions = LayoutOptions.Start,
                        VerticalOptions = LayoutOptions.Center,
                    };

                    swatch.GestureRecognizers.Add(new TapGestureRecognizer());
                    ((TapGestureRecognizer)swatch.GestureRecognizers[0]).Tapped += async (s, args) =>
                    {
                        if (AppShell.instance.CurrentPage is not DraftPage draftPage)
                            return;

                        var picker = new ColorPicker { SelectedColor = currentColor };

                        picker.SelectedColorChanged += (_, color) =>
                        {
                            var newR = (ushort)(color.Red * 65535.0);
                            var newG = (ushort)(color.Green * 65535.0);
                            var newB = (ushort)(color.Blue * 65535.0);
                            var newA = (ushort)(color.Alpha * 65535.0);

                            Parameters["R"] = newR;
                            Parameters["G"] = newG;
                            Parameters["B"] = newB;
                            Parameters["A"] = newA;
                            swatch.BackgroundColor = color;
                            currentColor = color;

                            invoker(color);
                        };

                        var popupContent = new VerticalStackLayout
                        {
                            Spacing = 10,
                            Padding = new Thickness(10, 0),
                            Children =
                            {
                                new Button
                                {
                                    Text = EffectBundleHelper.L("Hide", "Done"),
                                    Command = new Command(async () => await draftPage.HidePopup(true)),
                                },
                                picker,
                            },
                        };

                        await draftPage.ShowAPopup(
                            new ScrollView { Content = popupContent },
                            mode: "dialog");
                    };

                    return swatch;
                },
                "ColorPreview",
                "#00000000");

            // Keep the Tolerance slider as-is
            panel.AddSlider(
                "Tolerance",
                EffectBundleHelper.ParamLabel("Tolerance"),
                0,
                ushort.MaxValue,
                tolerance,
                null,
                SliderUpdateEventCallMode.OnMouseUp);
            return panel;
        }

        public Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            if (args.Id == "ColorPreview" && args.Value is Color color)
            {
                Parameters["R"] = (ushort)(color.Red * 65535.0);
                Parameters["G"] = (ushort)(color.Green * 65535.0);
                Parameters["B"] = (ushort)(color.Blue * 65535.0);
                Parameters["A"] = (ushort)(color.Alpha * 65535.0);
                return Parameters;
            }

            if (args.Id == "Tolerance"
                && EffectBundleHelper.TrySetUShort(Parameters, args.Id, args.Value))
            {
                return Parameters;
            }

            return Parameters;
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = EffectBundleHelper.L("DisplayName_Effect_RemoveColor", "Remove Color"),
                Description = EffectBundleHelper.L("Description_Effect_RemoveColor", "Remove a specific color from the image based on tolerance."),
                Thumbnail = ImageSource.FromFile(FileSystemService.GetAppPackageFileSync("EffectSample", "removeColor.png"))
            };
        }
    }
}
