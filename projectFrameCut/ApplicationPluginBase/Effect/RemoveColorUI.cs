using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Views.Pickers;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using Microsoft.Maui.Controls;
using System;
using System.Collections.Generic;

namespace projectFrameCut.ApplicationPluginBase.Effect
{
    /// <summary>
    /// Custom property UI of the RemoveColor effect: a color swatch that opens the color picker.
    /// </summary>
    public class RemoveColorUI : EffectProviderUI
    {
        public RemoveColorUI(IEffectProvider inner) : base(inner)
        {
        }

        public override (Dictionary<string, object>? newParams, Dictionary<string, IEffectArgumentField>? newFields) HandlePropertyPanelChange(IEffectProvider source, PropertyPanelPropertyChangedEventArgs args)
        {
            if (args.Id == "ColorPreview" && args.Value is Color color)
            {
                Inner.Parameters["R"] = (ushort)(color.Red * 65535.0);
                Inner.Parameters["G"] = (ushort)(color.Green * 65535.0);
                Inner.Parameters["B"] = (ushort)(color.Blue * 65535.0);
                Inner.Parameters["A"] = (ushort)(color.Alpha * 65535.0);
                return (Inner.Parameters, Inner.Fields);
            }

            return base.HandlePropertyPanelChange(source, args);
        }

        public override PropertyPanelBuilder CreateUI()
        {
            ushort r = EffectProviderHelper.GetUShort(Inner.Parameters, "R", 0);
            ushort g = EffectProviderHelper.GetUShort(Inner.Parameters, "G", 0);
            ushort b = EffectProviderHelper.GetUShort(Inner.Parameters, "B", 0);
            ushort a = EffectProviderHelper.GetUShort(Inner.Parameters, "A", ushort.MaxValue);
            ushort tolerance = EffectProviderHelper.GetUShort(Inner.Parameters, "Tolerance", 1200);

            var panel = new PropertyPanelBuilder();

            // Color swatch that opens the ColorPicker overlay on DraftPage
            panel.AddCustomChild(
                EffectProviderHelper.ParamLabel("Color"),
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

                            Inner.Parameters["R"] = newR;
                            Inner.Parameters["G"] = newG;
                            Inner.Parameters["B"] = newB;
                            Inner.Parameters["A"] = newA;
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
                                    Text = EffectProviderHelper.L("Hide", "Done"),
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
                EffectProviderHelper.ParamLabel("Tolerance"),
                0,
                ushort.MaxValue,
                tolerance,
                null,
                SliderUpdateEventCallMode.OnMouseUp);
            return panel;
        }
    }
}
