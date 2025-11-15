using projectFrameCut.PropertyPanel;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using projectFrameCut.VideoMakeEngine;
using Microsoft.Maui.Platform;

#if WINDOWS
using Microsoft.UI.Xaml;

#endif

#if IOS
using projectFrameCut.Platforms.iOS;

#endif

namespace projectFrameCut.DraftStuff
{
    public class ClipInfoBuilder
    {
        DraftPage page;

        static JsonSerializerOptions savingOpts = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };


        public ClipInfoBuilder(DraftPage page)
        {
            this.page = page;
        }


        public View Build(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            RemoveColorEffect? existingRce = null;

            if (clip.Effects is not null)
            {
                existingRce = clip.Effects.TryGetValue("removeColor", out var existingEffect) && existingEffect is RemoveColorEffect rce ? rce : null;

            }
            bool hasRemoveColor = existingRce != null;
            string defaultR = hasRemoveColor ? existingRce!.R.ToString() : "0";
            string defaultG = hasRemoveColor ? existingRce!.G.ToString() : "0";
            string defaultB = hasRemoveColor ? existingRce!.B.ToString() : "0";
            string defaultA = hasRemoveColor ? existingRce!.A.ToString() : "0";
            string defaultTolerance = hasRemoveColor ? existingRce!.Tolerance.ToString() : "0";

            var ppb = new PropertyPanelBuilder()
        .AddText(new TitleAndDescriptionLineLabel(Localized.PropertyPanel_General, "general stuff"))
        .AddEntry("displayName", Localized.PropertyPanel_General_DisplayName, clip.displayName, "Clip 1", null)
        .AddEntry("speedRatio", Localized.PropertyPanel_General_SpeedRatio, clip.SecondPerFrameRatio.ToString(), "1", null)
        .AddSeparator(null)
        .AddText("effect")
        .AddCheckbox("removeColorBox", "enable remove color", hasRemoveColor)
        .AddChildrensInALine((c) =>
        {
            c
            .AddText("R")
            .AddEntry("removeColor_color", defaultR, "0")
            .AddText("G")
            .AddEntry("removeColor_channelG", defaultG, "0")
            .AddText("B")
            .AddEntry("removeColor_channelB", defaultB, "0")
            .AddText("A")
            .AddEntry("removeColor_channelA", defaultA, "0")
            .AddText("Tolerance")
            .AddEntry("removeColor_tolerance", defaultTolerance, "0")
            ;

        })
        .AddSeparator()
        .AddCustomChild((ivk) =>
        {
            var editor = new Editor
            {
                Text = JsonSerializer.Serialize(clip, savingOpts),
                HeightRequest = 300,
            };
            editor.TextChanged += (s, e) =>
            {
                try
                {
                    if (JsonSerializer.Deserialize<ClipElementUI>(editor.Text) is not ClipElementUI updatedClip)
                    {
                        return;
                    }
                    ivk(editor.Text);
                }
                catch (Exception)
                {
                }
            };
            return editor;
        }, "rawJsonEditor", JsonSerializer.Serialize(clip, savingOpts));

            ppb.ListenToChanges((s, e) =>
            {
                if (e.Id == "removeColorBox")
                {
                    if ((bool)e.Value)
                    {
                        if (ushort.TryParse(ppb.Properties["removeColor_color"].ToString(), out var r) &&
                            ushort.TryParse(ppb.Properties["removeColor_channelG"].ToString(), out var g) &&
                            ushort.TryParse(ppb.Properties["removeColor_channelB"].ToString(), out var b) &&
                            ushort.TryParse(ppb.Properties["removeColor_channelA"].ToString(), out var a) &&
                            ushort.TryParse(ppb.Properties["removeColor_tolerance"].ToString(), out var tol))
                        {
                            clip.Effects["RemoveColorEffect"] = new RemoveColorEffect { R = r, G = g, B = b, A = a, Tolerance = tol };
                        }
                    }
                    else
                    {
                        clip.Effects.Remove("RemoveColorEffect");
                    }
                }
                else if (e.Id.StartsWith("removeColor_") && e.Id != "removeColorBox")
                {
                    if (clip.Effects.TryGetValue("RemoveColorEffect", out var eff) && eff is RemoveColorEffect &&
                        ushort.TryParse(ppb.Properties["removeColor_color"].ToString(), out var r) &&
                        ushort.TryParse(ppb.Properties["removeColor_channelG"].ToString(), out var g) &&
                        ushort.TryParse(ppb.Properties["removeColor_channelB"].ToString(), out var b) &&
                        ushort.TryParse(ppb.Properties["removeColor_channelA"].ToString(), out var a) &&
                        ushort.TryParse(ppb.Properties["removeColor_tolerance"].ToString(), out var tol))
                    {
                        clip.Effects["RemoveColorEffect"] = new RemoveColorEffect { R = r, G = g, B = b, A = a, Tolerance = tol };
                    }
                }
                handler(s, e);
            });

            var panel = ppb.Build();

#if WINDOWS
            ApplyAcrylic(panel);
#elif iDevices
            panel.AddGlassEffect("SystemChromeMaterial", 8.0, 0.8);
#endif
            return panel;

        }
#if WINDOWS
        private void ApplyAcrylic(VerticalStackLayout panel)
        {
            var acrylicBrush = new Microsoft.UI.Xaml.Media.AcrylicBrush
            {
                TintOpacity = 0.3,
                FallbackColor = Microsoft.UI.Colors.Gray,
            };
            panel.HandlerChanged += (s, e) =>
            {
                var stack = panel.Handler?.PlatformView as LayoutPanel;
                if (stack != null)
                {
                    stack.Background = acrylicBrush;
                }
            };
        }
#endif
    }
}
