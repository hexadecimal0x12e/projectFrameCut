using projectFrameCut.PropertyPanel;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
            var ppb = new PropertyPanelBuilder()
        .AddText(new TitleAndDescriptionLineLabel(Localized.PropertyPanel_General, "general stuff"))
        .AddEntry("displayName", Localized.PropertyPanel_General_DisplayName, clip.displayName, "Clip 1", null)
        .AddEntry("speedRatio", Localized.PropertyPanel_General_SpeedRatio, clip.SecondPerFrameRatio.ToString(), "1", null)
        .AddSeparator(null)
        .AddText("effect")
        .AddCheckbox("removeColorBox", "enable remove color", false)
        .AddChildrensInALine((c) =>
        {
            c
            .AddText("R")
            .AddEntry("removeColor_color", "0", "0")
            .AddText("G")
            .AddEntry("removeColor_channelG", "0", "0")
            .AddText("B")
            .AddEntry("removeColor_channelB", "0", "0")
            ;

        })
        .AddSeparator()
        .AddText("Effects (JSON)")
        //.AddCustomChild((ivk) =>
        //{
        //    var editor = new Editor
        //    {
        //        Text = JsonSerializer.Serialize(clip.Effects, savingOpts),
        //        HeightRequest = 180,
        //    };
        //    editor.TextChanged += (s, e) =>
        //    {
        //        try
        //        {
        //            // Try to deserialize to ensure content is valid before invoking
        //            if (JsonSerializer.Deserialize<Dictionary<string, projectFrameCut.Shared.Effects>>(editor.Text) is not Dictionary<string, projectFrameCut.Shared.Effects> updated)
        //            {
        //                return;
        //            }
        //            ivk(editor.Text);
        //        }
        //        catch (Exception)
        //        {
        //        }
        //    };
        //    return editor;
        //}, "effectsEditor", JsonSerializer.Serialize(clip.Effects, savingOpts))
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
        }, "rawJsonEditor", JsonSerializer.Serialize(clip, savingOpts))
         .ListenToChanges(handler);


            return ppb.Build();

        }

    }
}
