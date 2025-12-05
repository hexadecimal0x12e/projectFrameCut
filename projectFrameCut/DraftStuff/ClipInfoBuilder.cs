using projectFrameCut.PropertyPanel;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using projectFrameCut.VideoMakeEngine;
using projectFrameCut.Shared;
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
            var ppb = new PropertyPanelBuilder()
        .AddText(new TitleAndDescriptionLineLabel(Localized.PropertyPanel_General, "general stuff"))
        .AddEntry("displayName", Localized.PropertyPanel_General_DisplayName, clip.displayName, "Clip 1")
        .AddEntry("speedRatio", Localized.PropertyPanel_General_SpeedRatio, clip.SecondPerFrameRatio.ToString(), "1")
        .AddSeparator(null);

            if (clip.Effects != null)
            {
                foreach (var effectKvp in clip.Effects)
                {
                    var effectKey = effectKvp.Key;
                    var effect = effectKvp.Value;
                    ppb.AddText(new TitleAndDescriptionLineLabel(effect.TypeName, effectKey));

                    foreach (var paramName in effect.ParametersNeeded)
                    {
                        if (!effect.ParametersType.TryGetValue(paramName, out var paramType)) continue;

                        var currentVal = effect.Parameters.ContainsKey(paramName) ? effect.Parameters[paramName] : null;
                        string controlId = $"Effect|{effectKey}|{paramName}";

                        if (paramType == "bool")
                        {
                            bool val = currentVal is bool b ? b : false;
                            ppb.AddCheckbox(controlId, paramName, val);
                        }
                        else
                        {
                            // Assume numeric or string
                            string valStr = currentVal?.ToString() ?? "";
                            ppb.AddEntry(controlId, paramName, valStr, "");
                        }
                    }
                    ppb.AddButton($"Effect|{effectKey}|Remove", "Remove this effect");
                    ppb.AddSeparator();
                }
            }

            ppb.AddText(new TitleAndDescriptionLineLabel("Add Effect", "Add a new effect"));
            ppb.AddPicker("NewEffectType", "Effect Type", new[] { "RemoveColor" }, "RemoveColor");
            ppb.AddButton("AddEffect", "Add Effect");

            ppb.PropertyChanged += (s, e) =>
            {
                if (e.Id.StartsWith("Effect|"))
                {
                    var parts = e.Id.Split('|');
                    if (parts.Length >= 3)
                    {
                        string effectKey = parts[1];
                        string paramName = parts[2];

                        if (paramName == "Remove")
                        {
                            if (clip.Effects != null && clip.Effects.ContainsKey(effectKey))
                            {
                                clip.Effects.Remove(effectKey);
                                handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                return;
                            }
                        }

                        if (clip.Effects != null && clip.Effects.TryGetValue(effectKey, out var effect))
                        {
                            if (effect.ParametersType.TryGetValue(paramName, out var paramType))
                            {
                                try
                                {
                                    object? typedValue = null;
                                    string strVal = e.Value?.ToString() ?? "";

                                    switch (paramType)
                                    {
                                        case "ushort": typedValue = ushort.Parse(strVal); break;
                                        case "int": typedValue = int.Parse(strVal); break;
                                        case "float": typedValue = float.Parse(strVal); break;
                                        case "double": typedValue = double.Parse(strVal); break;
                                        case "bool": typedValue = e.Value is bool b ? b : bool.Parse(strVal); break;
                                        case "string": typedValue = strVal; break;
                                    }

                                    if (typedValue != null)
                                    {
                                        var newParams = new Dictionary<string, object>(effect.Parameters);
                                        newParams[paramName] = typedValue;
                                        clip.Effects[effectKey] = effect.WithParameters(newParams);
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
                else if (e.Id == "AddEffect")
                {
                    if (ppb.Properties.TryGetValue("NewEffectType", out var typeObj) && typeObj is string typeName)
                    {
                        IEffect? newEffect = null;
                        if (typeName == "RemoveColor")
                        {
                            newEffect = new RemoveColorEffect();
                        }

                        if (newEffect != null)
                        {
                            string newKey = typeName + "@" + Guid.NewGuid().ToString().Substring(0, 8);
                            if (clip.Effects == null) clip.Effects = new Dictionary<string, IEffect>();
                            clip.Effects[newKey] = newEffect;
                            handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                            return;
                        }
                    }
                }
                handler?.Invoke(s, e);
            };

            ppb.AddCustomChild((ivk) =>
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


            var panel = ppb.Build();

#if WINDOWS
            ApplyAcrylic(panel);
#elif iDevices
            // panel.AddGlassEffect("SystemChromeMaterial", 8.0, 0.8);
#endif
            return panel;

        }
#if WINDOWS
        private void ApplyAcrylic(Layout panel)
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
