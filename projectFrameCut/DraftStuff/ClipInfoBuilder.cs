using projectFrameCut.PropertyPanel;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Maui.Platform;
using projectFrameCut.Render.VideoMakeEngine;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.Render.Plugin;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;

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
        // This method adapts to plugin-based IEffect by using the standard WithParameters interface
        private IEffect ReCreateEffect(IEffect effect, Dictionary<string, object>? parameters = null, bool? enabled = null, int? index = null)
        {
            // Create a new effect with updated parameters using the standard IEffect interface
            var newEffect = effect.WithParameters(parameters ?? effect.Parameters);

            // Update mutable properties (Enabled and Index)
            if (enabled.HasValue)
                newEffect.Enabled = enabled.Value;
            if (index.HasValue)
                newEffect.Index = index.Value;

            newEffect.RelativeWidth = page.ProjectInfo.RelativeWidth;
            newEffect.RelativeHeight = page.ProjectInfo.RelativeHeight;

            return newEffect;
        }

        public View Build(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            var ppb = new PropertyPanelBuilder()
            .AddText(new SingleLineLabel(Localized.PropertyPanel_General, 20))
            .AddEntry("displayName", Localized.PropertyPanel_General_DisplayName, clip.displayName, "Clip 1")
            .AddEntry("speedRatio", Localized.PropertyPanel_General_SpeedRatio, clip.SecondPerFrameRatio.ToString(), "1")
            .AddSeparator(null);

            if (clip.Effects != null)
            {
                foreach (var effectKvp in clip.Effects.OrderBy(c => c.Value.Index))
                {
                    var effectKey = effectKvp.Key;
                    var effect = effectKvp.Value;
                    ppb.AddText(new TitleAndDescriptionLineLabel(effect.Name, PluginManager.GetLocalizationItem($"EffectType_{effect.TypeName}", effect.TypeName)));
                    ppb.AddCheckbox($"Effect|{effectKey}|Enabled", PPLocalizedResources._Enabled, effect.Enabled);
                    ppb.AddEntry($"Effect|{effectKey}|Index", PPLocalizedResources.EffectProp_Index, effect.Index.ToString(), "-1");
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
                            ppb.AddCheckbox(controlId, PluginManager.GetLocalizationItem($"_{paramName}", paramName), val);
                        }
                        else
                        {
                            string valStr = currentVal?.ToString() ?? "";
                            ppb.AddEntry(controlId, PluginManager.GetLocalizationItem($"_{paramName}", paramName), valStr, "");
                        }
                    }
                    ppb.AddButton($"Effect|{effectKey}|Remove", PPLocalizedResources.EffectProp_Remove);
                    ppb.AddSeparator();
                }
            }

            ppb.AddPicker("NewEffectType", PPLocalizedResources.Add_Effect_Select, EffectHelper.GetEffectTypes().ToArray(),
                EffectHelper.GetEffectTypes().FirstOrDefault());
            ppb.AddButton("AddEffect", PPLocalizedResources.Add_Effect);

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
                        if (EffectHelper.EffectsEnum.TryGetValue(typeName, out var creator))
                        {
                            try
                            {
                                newEffect = creator?.Invoke();
                            }
                            catch (Exception ex)
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
                            Log($"Failed to create effect of type {typeName}.", "error");
                            throw new InvalidDataException($"Failed to create effect of type {typeName}.");
                        }
                    }
                }
                handler?.Invoke(s, e);
            };
#if DEBUG //end user don't want to see raw json editor
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
            }, "rawJsonEditor", JsonSerializer.Serialize(clip, savingOpts))
            .AddCustomChild(new Rectangle { WidthRequest = 50, HeightRequest = 120, Fill = Colors.Transparent });
#endif

            var panel = ppb.Build();
            return panel;

        }

    }
}
