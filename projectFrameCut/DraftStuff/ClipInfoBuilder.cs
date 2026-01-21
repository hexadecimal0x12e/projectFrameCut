using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Platform;
using projectFrameCut.Controls;

using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.VideoMakeEngine;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;
using projectFrameCut.Services;
using GridLength = Microsoft.Maui.GridLength;
using Thickness = Microsoft.Maui.Thickness;

using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Effect.ImageSharp;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Plugins;



using projectFrameCut.ApplicationAPIBase.PropertyPanelBuilders;





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

        /// <summary>
        /// Gets the default color hex string based on clip type.
        /// </summary>
        private static string GetDefaultColorHex(projectFrameCut.Shared.ClipMode clipType)
        {
            return clipType switch
            {
                projectFrameCut.Shared.ClipMode.VideoClip => Colors.CornflowerBlue.ToArgbHex(),
                projectFrameCut.Shared.ClipMode.PhotoClip => Colors.MediumSeaGreen.ToArgbHex(),
                projectFrameCut.Shared.ClipMode.AudioClip => Colors.Goldenrod.ToArgbHex(),
                projectFrameCut.Shared.ClipMode.SubtitleClip => Colors.SlateGray.ToArgbHex(),
                projectFrameCut.Shared.ClipMode.SolidColorClip => Colors.OrangeRed.ToArgbHex(),
                _ => Colors.Gray.ToArgbHex(),
            };
        }


        public ClipInfoBuilder(DraftPage page)
        {
            this.page = page;
            PPLocalizedResources = ISimpleLocalizerBase_PropertyPanel.GetMapping().TryGetValue(Localized._LocaleId_, out var pploc) ? pploc : ISimpleLocalizerBase_PropertyPanel.GetMapping().First().Value;
        }

        // Helper to recreate effect with updated parameters / flags (init-only properties)
        // This method adapts to plugin-based IEffect by using the standard WithParameters interface
        private IEffect ReCreateEffect(IEffect effect, Dictionary<string, object>? parameters = null, bool? enabled = null, int? index = null)
        {
            // Create a new effect with updated parameters using the standard IEffect interface
            var newEffect = effect.WithParameters(parameters ?? effect.Parameters);

            // Update mutable properties (Enabled and Index), preserve original if not provided
            newEffect.Enabled = enabled ?? effect.Enabled;
            newEffect.Index = index ?? effect.Index;

            // Preserve Name which might be lost during recreation
            newEffect.Name = effect.Name;

            newEffect.RelativeWidth = page.ProjectInfo.RelativeWidth;
            newEffect.RelativeHeight = page.ProjectInfo.RelativeHeight;

            return newEffect;
        }

        public static Dictionary<string, string> GetLocalizedEffectNames()
        {
            string GetEffectDisplayName(KeyValuePair<string, Func<IEffect>> e)
            {
                var instance = e.Value();
                var type = instance switch
                {
                    var t when t is IContinuousEffect => PPLocalizedResources.Effect_ContinuousEffect,
                    var t when t is IEffect => PPLocalizedResources.Effect_GeneralEffect,
                    _ => PPLocalizedResources.Effect_GeneralEffect,
                };
                if (instance.FromPlugin == InternalPluginBase.InternalPluginBaseID || SettingsManager.IsBoolSettingTrue("edit_AlwaysShowEffectsSource"))
                {
                    var dispName = PluginManager.GetLocalizationItem("DisplayName_Effect_" + e.Key, e.Key);
                    return $"{dispName} ({type})";
                }
                else
                {
                    var plg = PluginManager.LoadedPlugins.TryGetValue(instance.FromPlugin, out var value) ? value : null;
                    var dispName = plg?.GetLocalizationItemInSpecificPlugin("_PluginBase_Name_", plg.Name) ?? e.Key;
                    return $"{plg.GetLocalizationItemInSpecificPlugin("DisplayName_Effect_" + e.Key, e.Key)} ({PPLocalizedResources.Effect_FromPlugin(type, dispName)})";
                }

            }

            return EffectHelper.EffectsEnum.ToDictionary(c => c.Key, GetEffectDisplayName);
        }

        public View Build(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            TabbedView t = new();
            t.Background = page.Background;
            t.TabItems.Add(new TabbedViewItem
            {
                Header = Localized.MainSettingsPage_Tab_General,
                Content = BuildGeneralTab(clip, handler)
            });
            t.TabItems.Add(new TabbedViewItem
            {
                Header = PPLocalizedResources.Tabs_Effect,
                Content = BuildEffectTab(clip, handler)
            });
            if (SettingsManager.IsBoolSettingTrue("edit_ShowAllEffects"))
            {
                t.TabItems.Add(new TabbedViewItem
                {
                    Header = PPLocalizedResources.Tabs_Effect + " (classic)",
                    Content = BuildClassicEffectTab(clip, handler)
                });
            }

            t.TabItems.Add(new TabbedViewItem
            {
                Header = PPLocalizedResources.Tabs_SpeedRatio,
                Content = BuildSpeedAndRatioTab(clip, handler)
            });
            return t;
        }



        public View BuildGeneralTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            string currentColorHex = clip.ClipColor ?? GetDefaultColorHex(clip.ClipType);

            int valX = 0, valY = 0;
            int valW = page.ProjectInfo.RelativeWidth;
            int valH = page.ProjectInfo.RelativeHeight;

            if (clip.Effects != null)
            {
                if (clip.Effects.TryGetValue("__Internal_Place__", out var e) && e is PlaceEffect_ImageSharp p)
                {
                    valX = p.StartX;
                    valY = p.StartY;
                    if (p.RelativeWidth > 0 && p.RelativeWidth != page.ProjectInfo.RelativeWidth)
                    {
                        valX = (int)(p.StartX * ((double)page.ProjectInfo.RelativeWidth / p.RelativeWidth));
                        valY = (int)(p.StartY * ((double)page.ProjectInfo.RelativeHeight / p.RelativeHeight));
                    }
                }
                if (clip.Effects.TryGetValue("__Internal_Resize__", out var e2) && e2 is ResizeEffect_ImageSharp r)
                {
                    valW = r.Width;
                    valH = r.Height;
                    if (r.RelativeWidth > 0 && r.RelativeWidth != page.ProjectInfo.RelativeWidth)
                    {
                        valW = (int)(r.Width * ((double)page.ProjectInfo.RelativeWidth / r.RelativeWidth));
                        valH = (int)(r.Height * ((double)page.ProjectInfo.RelativeHeight / r.RelativeHeight));
                    }
                }
            }

            var ppb = new PropertyPanelBuilder()
            .AddText(new SingleLineLabel(Localized.PropertyPanel_General, 20))
            .AddEntry("displayName", Localized.PropertyPanel_General_DisplayName, clip.displayName, clip.displayName)
            .AddCustomChild(PPLocalizedResources.General_DisplayColor, (invoker) =>
            {
                var colorPreview = new BoxView
                {
                    WidthRequest = 30,
                    HeightRequest = 30,
                    CornerRadius = 5,
                    Color = Color.FromArgb(currentColorHex),
                    VerticalOptions = LayoutOptions.Center
                };

                var colorEntry = new Entry
                {
                    Text = currentColorHex,
                    Placeholder = "#RRGGBB",
                    WidthRequest = 100,
                    VerticalOptions = LayoutOptions.Center
                };

                colorEntry.TextChanged += (s, e) =>
                {
                    try
                    {
                        var color = Color.FromArgb(e.NewTextValue);
                        colorPreview.Color = color;
                    }
                    catch { }
                };

                colorEntry.Unfocused += (s, e) =>
                {
                    invoker(colorEntry.Text);
                };

                var resetButton = new Button
                {
                    Text = "↺",
                    WidthRequest = 40,
                    HeightRequest = 35,
                    Padding = 0,
                    VerticalOptions = LayoutOptions.Center
                };
                resetButton.Clicked += (s, e) =>
                {
                    var defaultColor = GetDefaultColorHex(clip.ClipType);
                    colorEntry.Text = defaultColor;
                    colorPreview.Color = Color.FromArgb(defaultColor);
                    invoker(null!); // Reset to default
                };

                var layout = new HorizontalStackLayout
                {
                    Spacing = 8,
                    Children = { colorPreview, colorEntry, resetButton }
                };

                return layout;
            }, "clipColor", currentColorHex)
            .AddSeparator(null)
            .AddText(new SingleLineLabel(PPLocalizedResources.General_LocationAndSize, 20))
            .AddEntry("placeX", PPLocalizedResources.General_LocationX, valX.ToString(), "0", null, default)
            .AddEntry("placeY", PPLocalizedResources.General_LocationY, valY.ToString(), "0", null, default)
            .AddEntry("resizeW", PPLocalizedResources._Width, valW.ToString(), page.ProjectInfo.RelativeWidth.ToString(), null, default)
            .AddEntry("resizeH", PPLocalizedResources._Height, valH.ToString(), page.ProjectInfo.RelativeHeight.ToString(), null, default)
            ;

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

            ppb.PropertyChanged += async (s, e) =>
            {
                if (e.Id == "clipColor")
                {
                    if (e.Value == null || string.IsNullOrWhiteSpace(e.Value?.ToString()))
                    {
                        clip.ClipColor = null; // Reset to default
                    }
                    else
                    {
                        clip.ClipColor = e.Value?.ToString();
                    }
                    clip.ApplyClipColor();
                    handler?.Invoke(s, e);
                    return;
                }
                if (e.Id.StartsWith("place") || e.Id.StartsWith("resize"))
                {
                    if (clip.Effects == null) clip.Effects = new Dictionary<string, IEffect>();

                    if (e.Id.StartsWith("place"))
                    {
                        // Get current values (normalized to current project resolution) from UI or Effect
                        int currentX = 0, currentY = 0;

                        PlaceEffect_ImageSharp existingP = null;
                        if (clip.Effects.TryGetValue("__Internal_Place__", out var eff) && eff is PlaceEffect_ImageSharp pe)
                        {
                            existingP = pe;
                            currentX = pe.StartX;
                            currentY = pe.StartY;
                            if (pe.RelativeWidth > 0 && pe.RelativeWidth != page.ProjectInfo.RelativeWidth)
                            {
                                currentX = (int)(pe.StartX * ((double)page.ProjectInfo.RelativeWidth / pe.RelativeWidth));
                                currentY = (int)(pe.StartY * ((double)page.ProjectInfo.RelativeHeight / pe.RelativeHeight));
                            }
                        }

                        if (e.Id == "placeX" && int.TryParse(e.Value?.ToString(), out var vx)) currentX = vx;
                        else if (ppb.Properties.TryGetValue("placeX", out var uiX) && int.TryParse(uiX.ToString(), out var uiXInt)) currentX = uiXInt;

                        if (e.Id == "placeY" && int.TryParse(e.Value?.ToString(), out var vy)) currentY = vy;
                        else if (ppb.Properties.TryGetValue("placeY", out var uiY) && int.TryParse(uiY.ToString(), out var uiYInt)) currentY = uiYInt;

                        var newP = new PlaceEffect_ImageSharp
                        {
                            StartX = currentX,
                            StartY = currentY,
                            RelativeWidth = page.ProjectInfo.RelativeWidth,
                            RelativeHeight = page.ProjectInfo.RelativeHeight,
                            Enabled = existingP?.Enabled ?? true,
                            Name = existingP?.Name ?? "__Internal_Place__",
                            Index = existingP?.Index ?? (int.MaxValue - 100)
                        };
                        clip.Effects["__Internal_Place__"] = newP;
                    }
                    else if (e.Id.StartsWith("resize"))
                    {
                        int currentW = page.ProjectInfo.RelativeWidth, currentH = page.ProjectInfo.RelativeHeight;
                        ResizeEffect_ImageSharp existingR = null;

                        if (clip.Effects.TryGetValue("__Internal_Resize__", out var eff) && eff is ResizeEffect_ImageSharp re)
                        {
                            existingR = re;
                            currentW = re.Width;
                            currentH = re.Height;
                            if (re.RelativeWidth > 0 && re.RelativeWidth != page.ProjectInfo.RelativeWidth)
                            {
                                currentW = (int)(re.Width * ((double)page.ProjectInfo.RelativeWidth / re.RelativeWidth));
                                currentH = (int)(re.Height * ((double)page.ProjectInfo.RelativeHeight / re.RelativeHeight));
                            }
                        }

                        if (e.Id == "resizeW" && int.TryParse(e.Value?.ToString(), out var vw)) currentW = vw;
                        else if (ppb.Properties.TryGetValue("resizeW", out var uiW) && int.TryParse(uiW.ToString(), out var uiWInt)) currentW = uiWInt;

                        if (e.Id == "resizeH" && int.TryParse(e.Value?.ToString(), out var vh)) currentH = vh;
                        else if (ppb.Properties.TryGetValue("resizeH", out var uiH) && int.TryParse(uiH.ToString(), out var uiHInt)) currentH = uiHInt;

                        var newR = new ResizeEffect_ImageSharp
                        {
                            Width = currentW,
                            Height = currentH,
                            RelativeWidth = page.ProjectInfo.RelativeWidth,
                            RelativeHeight = page.ProjectInfo.RelativeHeight,
                            Enabled = existingR?.Enabled ?? true,
                            Name = existingR?.Name ?? "__Internal_Resize__",
                            Index = existingR?.Index ?? (int.MinValue + 50),
                            PreserveAspectRatio = existingR?.PreserveAspectRatio ?? false
                        };
                        clip.Effects["__Internal_Resize__"] = newR;
                    }

                    handler?.Invoke(s, e);
                    return;
                }

                if (e.Id == "clipColor")
                {
                    if (e.Value == null || string.IsNullOrWhiteSpace(e.Value?.ToString()))
                    {
                        clip.ClipColor = null; // Reset to default
                    }
                    else
                    {
                        clip.ClipColor = e.Value?.ToString();
                    }
                    clip.ApplyClipColor();
                    handler?.Invoke(s, e);
                    return;
                }
                handler?.Invoke(s, e);
            };
            return ppb.BuildWithScrollView();
        }

        private static Dictionary<string, Func<IEffectBundle>> GetAvailableEffectBundles()
        {
            var bundles = new Dictionary<string, Func<IEffectBundle>>();
            if (!PluginManager.Inited) return bundles;

            foreach (var plugin in PluginManager.LoadedPlugins.Values)
            {
                if (plugin is IApplicationPluginBase appPlugin)
                {
                    foreach (var kvp in appPlugin.EffectBundleProvider)
                    {
                        if (!bundles.ContainsKey(kvp.Key))
                        {
                            bundles.Add(kvp.Key, kvp.Value);
                        }
                    }
                }
            }
            return bundles;
        }

        private void RebuildAllEffects(ClipElementUI clip)
        {
            var newEffects = new Dictionary<string, IEffect>();
            int globalIndex = 0;
            var factories = GetAvailableEffectBundles();

            if (clip.EffectBundles != null)
            {
                foreach (var bundleData in clip.EffectBundles)
                {
                    if (factories.TryGetValue(bundleData.BundleTypeName, out var factory))
                    {
                        try
                        {
                            var instance = factory();
                            instance.Parameters = bundleData.Parameters;
                            instance.Id = bundleData.Id;
                            // instance.Name might be set by factory or default. bundleData.Name stores user override or display name?
                            if (!string.IsNullOrEmpty(bundleData.Name))
                            {
                                instance.Name = bundleData.Name;
                            }
                            else
                            {
                                bundleData.Name = instance.Name;
                            }


                            var effects = instance.Create();
                            if (effects != null)
                            {
                                for (int i = 0; i < effects.Length; i++)
                                {
                                    var effect = effects[i];
                                    effect.Enabled = effect.Enabled && bundleData.Enabled;
                                    effect.Index = globalIndex++;
                                    effect.BindedEffectGroupID = bundleData.Id;
                                    string key = $"{bundleData.Id}_{i}";
                                    newEffects[key] = effect;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log(ex, $"Rebuild effects for bundle {bundleData.BundleTypeName}", this);
                        }
                    }
                }
            }
            clip.Effects = newEffects;
        }

        public View BuildEffectTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            PropertyPanelBuilder ppb = new();
            var bundlesFactories = GetAvailableEffectBundles();
            
            if (clip.EffectBundles != null)
            {
                foreach (var bundleData in clip.EffectBundles)
                {
                    if (bundlesFactories.TryGetValue(bundleData.BundleTypeName, out var factory))
                    {
                        try
                        {
                            var bundleUiInstance = factory();
                            bundleUiInstance.Parameters = bundleData.Parameters;
                            bundleUiInstance.Id = bundleData.Id;
                            if (!string.IsNullOrEmpty(bundleData.Name)) bundleUiInstance.Name = bundleData.Name;

                            var bundlePpb = bundleUiInstance.CreateUI();

                            ppb.AddText(new TitleAndDescriptionLineLabel(bundleUiInstance.Name ?? bundleData.BundleTypeName, bundleData.BundleTypeName));
                            ppb.AddCheckbox($"Bundle|{bundleData.Id}|Enabled", PPLocalizedResources._Enabled, bundleData.Enabled);

                            ppb.AddFromAnother(bundlePpb, bundleUiInstance);

                            ppb.AddButton($"Bundle|{bundleData.Id}|Remove", PPLocalizedResources.EffectProp_Remove);
                            ppb.AddSeparator();
                        }
                        catch (Exception ex)
                        {
                            ppb.AddText(new SingleLineLabel($"Error loading bundle {bundleData.BundleTypeName}: {ex.Message}"));
                        }
                    }
                    else
                    {
                        ppb.AddText(new SingleLineLabel($"Missing bundle plugin: {bundleData.BundleTypeName}"));
                        ppb.AddButton($"Bundle|{bundleData.Id}|Remove", PPLocalizedResources.EffectProp_Remove);
                    }
                }
            }

            ppb.AddText(new SingleLineLabel(PPLocalizedResources.Effect_Add_Title, 20));
            // Use keys as display names for now, ideally bundleUiInstance.Name but tricky without instantiating
            var pickerItems = bundlesFactories.Keys.ToArray();
            ppb.AddPicker("NewBundleType", PPLocalizedResources.Add_Effect_Select, pickerItems, pickerItems.FirstOrDefault());
            ppb.AddButton("AddBundle", PPLocalizedResources.Add_Effect);

            ppb.PropertyChanged += (s, e) =>
            {
                if (!ppb.Equals(s)) //from another
                {
                    if(s is IEffectBundle eb)
                    {
                        var data = eb.HandlePropertyPanelChange(e);
                        if (data != null)
                        {
                            var bundle = clip.EffectBundles?.FirstOrDefault(x => x.Id == eb.Id);
                            if (bundle != null)
                            {
                                foreach (var kvp in data)
                                {
                                    bundle.Parameters[kvp.Key] = kvp.Value;
                                }
                                RebuildAllEffects(clip);
                            }
                        }
                    }
                }
                else
                {
                    if (e.Id.StartsWith("Bundle|"))
                    {
                        var parts = e.Id.Split('|');
                        if (parts.Length >= 3)
                        {
                            string bundleId = parts[1];
                            string action = parts[2];

                            if (action == "Remove")
                            {
                                var b = clip.EffectBundles?.FirstOrDefault(x => x.Id == bundleId);
                                if (b != null)
                                {
                                    clip.EffectBundles.Remove(b);
                                    RebuildAllEffects(clip);
                                    handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                }
                            }
                            else if (action == "Enabled")
                            {
                                var b = clip.EffectBundles?.FirstOrDefault(x => x.Id == bundleId);
                                if (b != null && bool.TryParse(e.Value?.ToString(), out bool val))
                                {
                                    b.Enabled = val;
                                    RebuildAllEffects(clip);
                                }
                            }
                        }
                    }
                    else if (e.Id == "AddBundle")
                    {
                        if (ppb.Properties.TryGetValue("NewBundleType", out var typeObj) && typeObj is string bundleTypeName)
                        {
                            if (bundlesFactories.TryGetValue(bundleTypeName, out var factory))
                            {
                                var instance = factory();
                                var newData = new EffectBundleData
                                {
                                    BundleTypeName = bundleTypeName,
                                    Parameters = new Dictionary<string, object>(instance.Parameters ?? new Dictionary<string, object>()),
                                    Name = instance.Name ?? bundleTypeName,
                                    Enabled = true
                                };

                                if (clip.EffectBundles == null) clip.EffectBundles = new List<EffectBundleData>();
                                clip.EffectBundles.Add(newData);

                                RebuildAllEffects(clip);
                                handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                            }
                        }
                    }
                }
            };
            
            return ppb.Build();
        }

        public View BuildClassicEffectTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            // Get current color or default
            PropertyPanelBuilder ppb = new();

            var localizedEffectDisplayName = GetLocalizedEffectNames();

            if (clip.Effects != null)
            {
                foreach (var effectKvp in clip.Effects.OrderBy(c => c.Value.Index))
                {
                    var effectKey = effectKvp.Key;
                    var effect = effectKvp.Value;
                    var factory = effect.GetFactory(EffectHelper.EffectsFactoriesEnum);
                    ppb.AddText(new TitleAndDescriptionLineLabel(effect.Name, localizedEffectDisplayName[effect.TypeName]));
                    ppb.AddCheckbox($"Effect|{effectKey}|Enabled", PPLocalizedResources._Enabled, effect.Enabled);
                    if (SettingsManager.IsBoolSettingTrue("edit_ShowAllEffects")) ppb.AddEntry($"Effect|{effectKey}|Index", PPLocalizedResources.EffectProp_Index, effect.Index.ToString(), "-1");
                    foreach (var paramName in factory.ParametersNeeded)
                    {
                        if (!factory.ParametersType.TryGetValue(paramName, out var paramType)) continue;

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




            ppb.AddText(new SingleLineLabel(PPLocalizedResources.Effect_Add_Title, 20));
            ppb.AddPicker("NewEffectType", PPLocalizedResources.Add_Effect_Select, localizedEffectDisplayName.Values.ToArray(), localizedEffectDisplayName.Values.FirstOrDefault());
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

                            if (effect.GetFactory(EffectHelper.EffectsFactoriesEnum).ParametersType.TryGetValue(paramName, out var paramType))
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
                    if (ppb.Properties.TryGetValue("NewEffectType", out var typeObj) && typeObj is string locedTypeName)
                    {
                        var typeName = localizedEffectDisplayName.FirstOrDefault(c => c.Value == locedTypeName, new("unknown", "unknown")).Key;
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

                            int maxIndex = 0;
                            if (clip.Effects.Count > 0)
                            {
                                foreach (var item in clip.Effects.Values)
                                {
                                    if (item.Index >= maxIndex) maxIndex = item.Index + 1;
                                }
                            }
                            newEffect.Index = maxIndex;

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

            ppb.AddSeparator();
            ppb.AddText(new TitleAndDescriptionLineLabel(PPLocalizedResources.Effect_RenderOrder, PPLocalizedResources.Effect_RenderOrder_Hint));

            var orderContainer = new VerticalStackLayout { Spacing = 2, Padding = 5 };

            if (clip.Effects != null)
            {
                foreach (var effectKvp in clip.Effects.Where(c => SettingsManager.IsBoolSettingTrue("edit_ShowAllEffects") || c.Value.Name is null || !(c.Value.Name is not null && c.Value.Name.StartsWith("__Internal"))).OrderBy(c => c.Value.Index))
                {
                    orderContainer.Children.Add(BuildEffectOrderItem(effectKvp.Key, effectKvp.Value, clip, localizedEffectDisplayName, handler));
                }
            }

            ppb.AddCustomChild(orderContainer);
            var panel = ppb.Build();
            return panel;

        }



        private View BuildEffectOrderItem(string effectKey, IEffect effect, ClipElementUI clip, Dictionary<string, string> localizedEffectDisplayName, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            // Drag Drop Container
            var container = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = GridLength.Star }
                },
                Padding = new Thickness(5),
                BackgroundColor = Colors.Transparent
            };

            var dragHandle = new Label
            {
                Text = "⣿", // Grip icon
                FontSize = 20,
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };

            var nameLabel = new Label
            {
                Text = localizedEffectDisplayName.TryGetValue(effect.TypeName, out var name) ? name : effect.Name,
                VerticalOptions = LayoutOptions.Center,
                FontSize = 16
            };

            // Add Index for clarity
            var indexLabel = new Label
            {
                Text = $"[{effect.Index}]",
                VerticalOptions = LayoutOptions.Center,
                FontSize = 12,
                TextColor = Colors.Gray,
                Margin = new Thickness(10, 0, 0, 0)
            };

            var textStack = new HorizontalStackLayout
            {
                Children = { nameLabel, indexLabel },
                VerticalOptions = LayoutOptions.Center
            };

            var dragGesture = new DragGestureRecognizer();
            dragGesture.CanDrag = true;
            dragGesture.DragStarting += (s, e) =>
            {
                e.Data.Properties.Add("EffectKey", effectKey);
            };
            dragHandle.GestureRecognizers.Add(dragGesture);

            // Add Drop to the WHOLE container (so dropping anywhere on the item works)
            var dropGesture = new DropGestureRecognizer();
            dropGesture.AllowDrop = true;
            dropGesture.Drop += (s, e) =>
            {
                if (e.Data.Properties.TryGetValue("EffectKey", out var sourceKeyObj) && sourceKeyObj is string sourceKey)
                {
                    if (sourceKey == effectKey) return;

                    // Swap Request
                    if (clip.Effects.TryGetValue(sourceKey, out var sourceEffect) && clip.Effects.TryGetValue(effectKey, out var targetEffect))
                    {
                        // Swap Index
                        int tIdx = targetEffect.Index;
                        int sIdx = sourceEffect.Index;

                        clip.Effects[sourceKey] = ReCreateEffect(sourceEffect, null, null, tIdx);
                        clip.Effects[effectKey] = ReCreateEffect(targetEffect, null, null, sIdx);

                        handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                    }
                }
            };
            container.GestureRecognizers.Add(dropGesture);

            container.Children.Add(dragHandle); // Col 0
            container.Children.Add(textStack); // Col 1
            Grid.SetColumn(textStack, 1);

            // Add visual feedback or border
            var frame = new Border
            {
                Content = container,
                Stroke = Colors.Gray,
                StrokeThickness = 0.5,
                Padding = 0,
                Margin = new Thickness(0, 2)
            };
            // Ensure gesture works on frame? Or just container? 
            // Better put Drop on Frame
            frame.GestureRecognizers.Add(dropGesture);

            return frame;
        }

        private View BuildSpeedAndRatioTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            PropertyPanelBuilder ppb = new();
            ppb.AddEntry("speedRatio", Localized.PropertyPanel_General_SpeedRatio, clip.SecondPerFrameRatio.ToString(), "1");
            ppb.AddButton("applyButton", Localized._Apply);
            ppb.ListenToChanges(e =>
            {
                if(e.Id == "speedRatio")
                {
                    clip.SecondPerFrameRatio = float.TryParse(e.Value as string, out var result) ? result : 1;
                    clip.ApplySpeedRatio();
                }
            });
            var panel = ppb.Build();
            return panel;
        }

    }
}
