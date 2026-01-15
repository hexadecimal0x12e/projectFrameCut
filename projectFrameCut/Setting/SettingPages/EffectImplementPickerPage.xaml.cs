using projectFrameCut.PropertyPanel;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.VideoMakeEngine;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using projectFrameCut.Shared;
using static projectFrameCut.Setting.SettingManager.SettingsManager;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.Plugin;

namespace projectFrameCut.Setting.SettingPages
{
    public partial class EffectImplementPickerPage : ContentPage
    {
        ConcurrentDictionary<string, EffectImplementType> effectImplementTypes = new();

        Dictionary<EffectImplementType, string> LocalizedImplementTypes =
            new Dictionary<EffectImplementType, string>
            {
                { EffectImplementType.NotSpecified , SettingLocalizedResources.RenderEffectImplement_NotSpecified },
                { EffectImplementType.ImageSharp , SettingLocalizedResources.RenderEffectImplement_ImageSharp },
                { EffectImplementType.HwAcceleration , SettingLocalizedResources.RenderEffectImplement_HwAcceleration },
                { EffectImplementType.IPicture , SettingLocalizedResources.RenderEffectImplement_IPicture},
            };


        public EffectImplementPickerPage()
        {
            if (File.Exists(Path.Combine(MauiProgram.BasicDataPath, "effectImplement.json")))
            {
                string json = File.ReadAllText(Path.Combine(MauiProgram.BasicDataPath, "effectImplement.json"));
                try
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, EffectImplementType>>(json);
                    if (dict != null)
                    {
                        effectImplementTypes = new ConcurrentDictionary<string, EffectImplementType>(dict);
                    }
                }
                catch (Exception ex)
                {
                    Log(ex, "read effectImplement", this);
                }
            }
            BuildPPB();

        }

        private void BuildPPB()
        {
            PropertyPanelBuilder ppb = new PropertyPanelBuilder();
            ppb.AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.RenderEffectImplement_Title, SettingLocalizedResources.RenderEffectImplement_Description))
               .AddText(new SingleLineLabel(SettingLocalizedResources.RenderEffectImplement_Hint, 14, default, Colors.Grey));


            foreach (var item in EffectHelper.EffectsEnum)
            {
                ppb.AddSeparator();

                var effect = item.Value();
                ppb.AddPicker(item.Key, ClipInfoBuilder.GetLocalizedEffectNames()[effect.TypeName], LocalizedImplementTypes.Where(c => c.Key != EffectImplementType.NotSpecified ? EffectHelper.EffectsFactoriesEnum[effect.TypeName].SupportsImplementTypes.Contains(c.Key) : true).Select(c => c.Value).ToArray(), LocalizedImplementTypes[effectImplementTypes.GetOrAdd(item.Key, EffectImplementType.NotSpecified)]);
            }

            ppb.ListenToChanges((args) =>
            {
                var id = args.Id;
                var newValue = args.Value as string;
                if (string.IsNullOrEmpty(newValue)) return;
                var key = LocalizedImplementTypes.ReverseLookup(newValue, EffectImplementType.NotSpecified);
                effectImplementTypes.AddOrUpdate(id, key, (k, v) => key);
                try
                {
                    var json = JsonSerializer.Serialize(effectImplementTypes);
                    File.WriteAllText(Path.Combine(MauiProgram.BasicDataPath, "effectImplement.json"), json);
                }
                catch (Exception ex)
                {
                    Log(ex, "write effectImplement", this);
                }
            });

            Content = ppb.BuildWithScrollView();
        }
    }
}
