using projectFrameCut.PropertyPanel;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using projectFrameCut.VideoMakeEngine;
using projectFrameCut.Shared;
using Microsoft.Maui.Platform;
using projectFrameCut.Render.VideoMakeEngine;


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

        // Helper to recreate effect with updated parameters / flags (init-only properties)
        private static IEffect ReCreateEffect(IEffect effect, Dictionary<string, object>? parameters = null, bool? enabled = null, int? index = null)
        {
            var paramDict = parameters ?? effect.Parameters;
            return effect switch
            {
                RemoveColorEffect => new RemoveColorEffect
                {
                    Enabled = enabled ?? effect.Enabled,
                    Index = index ?? effect.Index,
                    R = Convert.ToUInt16(paramDict["R"]),
                    G = Convert.ToUInt16(paramDict["G"]),
                    B = Convert.ToUInt16(paramDict["B"]),
                    A = Convert.ToUInt16(paramDict["A"]),
                    Tolerance = Convert.ToUInt16(paramDict["Tolerance"]),
                },
                PlaceEffect => new PlaceEffect
                {
                    Enabled = enabled ?? effect.Enabled,
                    Index = index ?? effect.Index,
                    StartX = Convert.ToInt32(paramDict["StartX"]),
                    StartY = Convert.ToInt32(paramDict["StartY"]),
                },
                CropEffect => new CropEffect
                {
                    Enabled = enabled ?? effect.Enabled,
                    Index = index ?? effect.Index,
                    StartX = Convert.ToInt32(paramDict["StartX"]),
                    StartY = Convert.ToInt32(paramDict["StartY"]),
                    Height = Convert.ToInt32(paramDict["Height"]),
                    Width = Convert.ToInt32(paramDict["Width"]),
                },
                _ => effect.WithParameters(paramDict), // fallback (won't update Enabled/Index for unknown types)
            };
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
                foreach (var effectKvp in clip.Effects.OrderBy(c => c.Value.Index))
                {
                    var effectKey = effectKvp.Key;
                    var effect = effectKvp.Value;
                    ppb.AddText(new TitleAndDescriptionLineLabel(effect.TypeName, effectKey));
                    ppb.AddCheckbox($"Effect|{effectKey}|Enabled", "Enabled", effect.Enabled);
                    ppb.AddEntry($"Effect|{effectKey}|Index", "Index", effect.Index.ToString(),"-1");
                    foreach (var paramName in effect.ParametersNeeded)
                    {
                        if (!effect.ParametersType.TryGetValue(paramName, out var paramType)) continue;

                        var currentVal = effect.Parameters.ContainsKey(paramName) ? effect.Parameters[paramName] : null;

                        if (currentVal is JsonElement je)
                        {
                            if (je.ValueKind == JsonValueKind.True || je.ValueKind == JsonValueKind.False)
                                currentVal = je.GetBoolean();
                            else if (je.ValueKind == JsonValueKind.String)
                                currentVal = je.GetString();
                            else
                                currentVal = je.ToString();
                        }

                        string controlId = $"Effect|{effectKey}|{paramName}";

                        if (paramType == "bool")
                        {
                            bool val = false;
                            if (currentVal is bool b) val = b;
                            else if (bool.TryParse(currentVal?.ToString(), out var bParsed)) val = bParsed;
                            ppb.AddCheckbox(controlId, paramName, val);
                        }
                        else
                        {
                            string valStr = currentVal?.ToString() ?? "";
                            ppb.AddEntry(controlId, paramName, valStr, "");
                        }
                    }
                    ppb.AddButton($"Effect|{effectKey}|Remove", "Remove this effect");
                    ppb.AddSeparator();
                }
            }

            ppb.AddText(new TitleAndDescriptionLineLabel("Add Effect", "Add a new effect"));
            ppb.AddPicker("NewEffectType", "Effect Type", EffectHelper.GetEffectTypes().ToArray(), 
                EffectHelper.GetEffectTypes().FirstOrDefault());
            ppb.AddButton("AddEffect", "Add Effect");

            ppb.PropertyChanged += async (s, e) =>
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
                            string strVal = e.Value?.ToString() ?? "";

                            // Handle Enabled / Index specially (not part of ParametersType)
                            if (paramName == "Enabled")
                            {
                                if (bool.TryParse(strVal, out var enabledVal))
                                {
                                    clip.Effects[effectKey] = ReCreateEffect(effect, null, enabledVal, null);
                                }
                                handler?.Invoke(s, e);
                                return;
                            }
                            if (paramName == "Index")
                            {
                                if (int.TryParse(strVal, out var indexVal))
                                {
                                    clip.Effects[effectKey] = ReCreateEffect(effect, null, null, indexVal);
                                }
                                handler?.Invoke(s, e);
                                return;
                            }

                            if (effect.ParametersType.TryGetValue(paramName, out var paramType))
                            {
                                try
                                {
                                    object? typedValue = null;
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
                                        clip.Effects[effectKey] = ReCreateEffect(effect, newParams, null, null);
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
                        if(EffectHelper.EffectsEnum.TryGetValue(typeName, out var creator))
                        {
                            try
                            {
                                newEffect = creator?.Invoke();
                            }
                            catch(Exception ex)
                            {
                                Log(ex, $"create effect of type {typeName}", this);
                            }
                        }


                        if (newEffect != null)
                        {
                            string newKey = typeName;
                            if (clip.Effects == null) clip.Effects = new Dictionary<string, IEffect>();
                            clip.Effects[newKey] = newEffect;
                            handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                            return;
                        }
                        else
                        {
                            Log($"Failed to create effect of type {typeName}.","error");
                            throw new InvalidDataException($"Failed to create effect of type {typeName}."); 
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
