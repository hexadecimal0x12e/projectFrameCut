using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Layouts;
using Microsoft.Maui.Platform;
using projectFrameCut.Controls;

using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
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
using DataTemplate = Microsoft.Maui.Controls.DataTemplate;
using GridUnitType = Microsoft.Maui.GridUnitType;
using CommunityToolkit.Maui.Views;
using System.Diagnostics;









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


        public async Task<View> Build(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
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
                Content = await BuildEffectTab(clip, handler)
            });
            if (SettingsManager.IsBoolSettingTrue("edit_ShowAllEffects"))
            {
                t.TabItems.Add(new TabbedViewItem
                {
                    Header = PPLocalizedResources.Tabs_Effect_Classic,
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

                        PlaceEffect_ImageSharp? existingP = null;
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
                        ResizeEffect_ImageSharp? existingR = null;

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

                switch (e.Id)
                {
                    case "displayName":
                        clip.displayName = e.Value?.ToString() ?? clip.displayName;
                        break;
                    case "speedRatio":
                        {
                            if (e.Value is double ratio || double.TryParse(e.Value as string, out ratio))
                            {
                                if (ratio != 0f)
                                    clip.SecondPerFrameRatio = (float)ratio;
                            }

                            break;
                        }
                    default:
                        {

                            break;
                        }
                }

                handler?.Invoke(s, e);
            };
            return ppb.BuildWithScrollView();
        }

        public static void RebuildAllEffects(ClipElementUI clip)
        {
            var newEffects = clip.Effects ?? new();
            int globalIndex = clip.Effects?.Select(e => e.Value)?.OrderBy(e => e.Index).LastOrDefault(e => !e.Name.StartsWith("__Internal"))?.Index + 1 ?? 0;
            var factories = EffectServices.GetAvailableEffectBundles();

            if (clip.EffectBundles != null)
            {
                foreach (var bundleKvp in clip.EffectBundles.OrderBy(kvp => kvp.Value.Index).ThenBy(kvp => kvp.Key))
                {
                    var bundleId = bundleKvp.Key;
                    var bundleData = bundleKvp.Value;

                    if (string.IsNullOrWhiteSpace(bundleData.Id) || bundleData.Id != bundleId)
                    {
                        bundleData.Id = bundleId;
                    }

                    if (factories.TryGetValue(bundleData.BundleTypeName, out var factory))
                    {
                        try
                        {
                            var instance = factory();
                            instance.Parameters = bundleData.Parameters;
                            instance.Id = bundleId;

                            if (!string.IsNullOrEmpty(bundleData.Name))
                            {
                                instance.Name = bundleData.Name;
                            }
                            else
                            {
                                bundleData.Name = instance.Name;
                            }


                            var effectFactories = instance.Create();
                            //var effects = effectFactories.Select(f => f.Build(, )).ToArray();
                            List<IEffect> effects = new();
                            foreach (var item in effectFactories)
                            {
                                var impType = EffectHelper.DefaultImplementsType.GetValueOrDefault(item.TypeName, EffectImplementType.NotSpecified);
                                var param = EffectArgsHelper.ConvertElementDictToObjectDict(instance.Parameters, instance.ParametersType);
                                if (item is IBindableEffectFactory be)
                                {
                                    effects.Add(be.Build(impType, be.ID, be.BindedInputID, be.BindedInputIDs, param));
                                }
                                else
                                {
                                    effects.Add(item.Build(impType, param));
                                }
                            }

                            if (effects != null)
                            {
                                // First pass: set up all bindable effects with IDs
                                for (int i = 0; i < effects.Count; i++)
                                {
                                    var effect = effects[i];
                                    effect.Name = $"EffectBundle {bundleData.BundleTypeName}({bundleData.Id}) - Subeffect #{i}";
                                    effect.Enabled = effect.Enabled && bundleData.Enabled;
                                    effect.Index = globalIndex++;
                                    effect.BindedEffectGroupID = bundleData.Id;
                                    
                                    //// For IBindableArgumentEffect, ensure they have proper IDs
                                    //if (effect is IBindableArgumentEffect bindableEffect)
                                    //{
                                    //    // Generate ID if not set
                                    //    if (string.IsNullOrEmpty(bindableEffect.Id))
                                    //    {
                                    //        bindableEffect.Id = $"{bundleData.Id}_bindable_{i}_{Guid.NewGuid().ToString().Substring(0, 8)}";
                                    //    }
                                    //}
                                    
                                    string key = $"{bundleData.Id}_{i}";
                                    newEffects[key] = effect;
                                }
                                
                                // Second pass: wire up bindings between effects
                                // This allows effects to reference each other via their IDs
                                //for (int i = 0; i < effects.Count; i++)
                                //{
                                //    var effect = effects[i];
                                    
                                //    // Check if factory specified binding information
                                //    var effectFactory = effectFactories[i];
                                //    if (effectFactory is IEffectFactory factoryWithBinding)
                                //    {
                                //        // If the factory has BindedInputID, use it to wire up the effect
                                //        var bindedInputIdProp = factoryWithBinding.GetType().GetProperty("BindedInputID");
                                //        if (bindedInputIdProp != null && effect is IBindableArgumentEffect bindableEffect)
                                //        {
                                //            var bindedInputId = bindedInputIdProp.GetValue(factoryWithBinding) as string;
                                //            if (!string.IsNullOrEmpty(bindedInputId))
                                //            {
                                //                // Resolve the binding ID within this bundle's effects
                                //                // The binding ID might reference another effect by index or by ID
                                //                bindableEffect.BindedArgumentProviderID = bindedInputId;
                                //            }
                                //        }
                                //    }
                                //}
                            }
                        }
                        catch (Exception ex)
                        {
                            Log(ex, $"Rebuild effects for bundle {bundleData.BundleTypeName}");
                        }
                    }
                }
            }
            clip.Effects = newEffects
                .Where(e => string.IsNullOrWhiteSpace(e.Value.BindedEffectGroupID)
                            || (clip.EffectBundles?.ContainsKey(e.Value.BindedEffectGroupID) ?? false))
                .ToDictionary();
        }

        public async Task<View> BuildEffectTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            PropertyPanelBuilder ppb = new();
            var bundlesFactories = EffectServices.GetAvailableEffectBundles();

            if (clip.EffectBundles != null)
            {
                foreach (var bundleKvp in clip.EffectBundles.OrderBy(kvp => kvp.Value.Index).ThenBy(kvp => kvp.Key))
                {
                    var bundleId = bundleKvp.Key;
                    var bundleData = bundleKvp.Value;

                    // Keep key/id consistent
                    if (string.IsNullOrWhiteSpace(bundleData.Id) || bundleData.Id != bundleId)
                    {
                        bundleData.Id = bundleId;
                    }

                    if (bundlesFactories.TryGetValue(bundleData.BundleTypeName, out var factory))
                    {
                        try
                        {
                            var bundleUiInstance = factory();
                            bundleUiInstance.Parameters = bundleData.Parameters;
                            bundleUiInstance.Id = bundleId;
                            if (!string.IsNullOrEmpty(bundleData.Name)) bundleUiInstance.Name = bundleData.Name;

                            var bundlePpb = bundleUiInstance.CreateUI();

                            ppb.AddText(new TitleAndDescriptionLineLabel(bundleUiInstance.Name ?? bundleData.BundleTypeName, bundleData.BundleTypeName));
                            ppb.AddCheckbox($"Bundle|{bundleId}|Enabled", PPLocalizedResources._Enabled, bundleData.Enabled);
                            ppb.AddEntry($"Bundle|{bundleId}|Index", PPLocalizedResources.EffectProp_Index, bundleData.Index.ToString(), "0");

                            ppb.AddFromAnother(bundlePpb, bundleUiInstance);

                            ppb.AddButton($"Bundle|{bundleId}|Remove", PPLocalizedResources.EffectProp_Remove);
                            ppb.AddSeparator();
                        }
                        catch (Exception ex)
                        {
                            if (Debugger.IsAttached)
                            {
                                if (Microsoft.Maui.Controls.Application.Current?.Windows?.First()?.Page is Page page)
                                {
                                    if (await page.DisplayAlertAsync(Localized._Error, $"Error loading bundle {bundleData.BundleTypeName}: {ex.Message}", "Throw", Localized._OK)) throw;
                                }
                            }
                            Log(ex, $"loading bundle {bundleData.BundleTypeName}", this);
                            ppb.AddText(new Label { Text = $"Error loading bundle {bundleData.BundleTypeName}: {ex.Message}", TextColor = Colors.Yellow });
                            ppb.AddSeparator();
                        }
                    }
                    else
                    {
                        ppb.AddText(new SingleLineLabel($"Missing bundle plugin: {bundleData.BundleTypeName}"));
                        ppb.AddButton($"Bundle|{bundleId}|Remove", PPLocalizedResources.EffectProp_Remove);
                    }
                }
            }

            ppb.AddText(new SingleLineLabel(PPLocalizedResources.Effect_Add_Title, 20));
            ppb.AddCustomChild(BuildAddEffectPanel(bundlesFactories, ppb, clip, handler));

            ppb.PropertyChanged += (s, e) =>
            {
                if (!ppb.Equals(s)) //from another
                {
                    if (s is IEffectBundle eb)
                    {
                        var data = eb.HandlePropertyPanelChange(e);
                        if (data != null)
                        {
                            EffectBundleData? bundle = null;
                            if (clip.EffectBundles != null)
                            {
                                if (!clip.EffectBundles.TryGetValue(eb.Id, out bundle)) throw new KeyNotFoundException($"Effect bundle with ID {eb.Id} not found in clip.");
                            }

                            if (bundle != null)
                            {
                                bundle.Parameters = data;
                            }
                        }
                        RebuildAllEffects(clip);
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
                            if (!clip.EffectBundles?.ContainsKey(bundleId) ?? false) return;

                            if (action == "Remove")
                            {
                                clip.EffectBundles?.Remove(bundleId);
                                RebuildAllEffects(clip);
                                handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                            }
                            else if (action == "Enabled")
                            {
                                var b = clip.EffectBundles?[bundleId];
                                if (b != null && bool.TryParse(e.Value?.ToString(), out bool val))
                                {
                                    b.Enabled = val;
                                    RebuildAllEffects(clip);
                                }
                            }
                            else if (action == "Index")
                            {
                                var b = clip.EffectBundles?[bundleId];
                                if (b != null && int.TryParse(e.Value?.ToString(), out int indexVal))
                                {
                                    b.Index = indexVal;
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

                                if (clip.EffectBundles == null) clip.EffectBundles = new Dictionary<string, EffectBundleData>();
                                var nextIndex = clip.EffectBundles.Count == 0 ? 0 : (clip.EffectBundles.Values.Max(x => x.Index) + 1);
                                newData.Index = nextIndex;
                                clip.EffectBundles[newData.Id] = newData;

                                RebuildAllEffects(clip);
                                handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                            }
                        }
                    }
                }
            };

            ppb.AddSeparator();
            ppb.AddText(new TitleAndDescriptionLineLabel(PPLocalizedResources.Effect_RenderOrder, PPLocalizedResources.Effect_RenderOrder_Hint));

            var bundleOrderContainer = new VerticalStackLayout { Spacing = 2, Padding = 5 };

            if (clip.EffectBundles != null)
            {
                foreach (var bundleData in clip.EffectBundles.Values.OrderBy(v => v.Index).ThenBy(v => v.Id))
                {
                    bundleOrderContainer.Children.Add(BuildBundleOrderItem(bundleData, clip, handler));
                }
            }

            ppb.AddCustomChild(bundleOrderContainer);
#if DEBUG
            ppb.AddSeparator();
            ppb.AddButton("Rebuild", (s, e) =>
            {
                RebuildAllEffects(clip);
            });
#endif
            var panel = ppb.Build();
            return panel;
        }

        private static object AttemptTypeConversion(object value, string targetTypeStr)
        {
            if (value == null) return null;

            if (value is JsonElement j)
            {
                if (j.ValueKind == JsonValueKind.True || j.ValueKind == JsonValueKind.False)
                {
                    return j.GetBoolean();
                }
                if (j.TryGetSByte(out var sb)) return sb;
                if (j.TryGetByte(out var b)) return b;
                if (j.TryGetInt16(out var i16)) return i16;
                if (j.TryGetUInt16(out var u16)) return u16;
                if (j.TryGetInt32(out var i32)) return i32;
                if (j.TryGetUInt32(out var u32)) return u32;
                if (j.TryGetInt64(out var i64)) return i64;
                if (j.TryGetUInt64(out var u64)) return u64;
                if (j.TryGetSingle(out var f)) return f;
                if (j.TryGetDouble(out var d)) return d;
                if (j.TryGetDecimal(out var dec)) return dec;
                if (j.TryGetDateTimeOffset(out var dto)) return dto;
                if (j.TryGetDateTime(out var dt)) return dt;
                if (j.TryGetGuid(out var g)) return g;
                if (j.TryGetBytesFromBase64(out var bytes)) return bytes;
                return j.GetString();
            }

            Type? targetType = Type.GetType(targetTypeStr);
            if (targetType == null)
            {
                switch (targetTypeStr.ToLowerInvariant())
                {
                    case "int":
                    case "int32":
                    case "system.int32":
                        targetType = typeof(int);
                        break;
                    case "float":
                    case "single":
                    case "system.single":
                        targetType = typeof(float);
                        break;
                    case "double":
                    case "system.double":
                        targetType = typeof(double);
                        break;
                    case "bool":
                    case "boolean":
                    case "system.boolean":
                        targetType = typeof(bool);
                        break;
                    case "string":
                    case "system.string":
                        targetType = typeof(string);
                        break;
                    case "long":
                    case "int64":
                    case "system.int64":
                        targetType = typeof(long);
                        break;
                }
            }

            if (targetType != null)
            {
                if (targetType.IsInstanceOfType(value)) return value;

                try
                {
                    if (targetType.IsEnum)
                    {
                        if (value is string s)
                        {
                            return Enum.Parse(targetType, s);
                        }
                        return Enum.ToObject(targetType, value);
                    }
                    return Convert.ChangeType(value, targetType);
                }
                catch
                {
                    // Ignore conversion errors and return original
                }
            }

            return value;
        }

        private sealed class EffectBundleCardItem
        {
            public required string BundleTypeName { get; init; }
            public required string Title { get; init; }
            public required string Description { get; init; }
            public ImageSource? Thumbnail { get; init; }
            public MediaSource? VideoThumbnail { get; init; }
        }

        private View BuildAddEffectPanel(
            Dictionary<string, Func<IEffectBundle>> bundlesFactories,
            PropertyPanelBuilder ppb,
            ClipElementUI clip,
            EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            if (bundlesFactories == null || bundlesFactories.Count == 0)
            {
                return new Border
                {
                    StrokeShape = new RoundRectangle { CornerRadius = 12 },
                    Stroke = new SolidColorBrush(Colors.Gray.WithAlpha(0.25f)),
                    Background = new SolidColorBrush(Colors.Transparent),
                    Padding = 12,
                    Content = new Label
                    {
                        Text = PPLocalizedResources.Add_Effect_Select,
                        Opacity = 0.7,
                        FontSize = 13
                    }
                };
            }


            void AddBundle(string bundleTypeName)
            {
                ppb.Properties["NewBundleType"] = bundleTypeName;
                PropertyPanelPropertyChangedEventArgs.CreateAndInvoke(ppb, "AddBundle", bundleTypeName);
            }

            var cards = new List<EffectBundleCardItem>();
            foreach (var kvp in bundlesFactories.OrderBy(k => k.Key))
            {
                var bundleTypeName = kvp.Key;
                try
                {
                    var instance = kvp.Value();
                    EffectBundleDisplayItem? display = null;
                    try
                    {
                        display = instance.GetEffectBundleItem(Localized._LocaleId_);
                    }
                    catch
                    {
                        // ignore, fallback to Name/TypeName
                    }

                    cards.Add(new EffectBundleCardItem
                    {
                        BundleTypeName = bundleTypeName,
                        Title = EffectServices.GetLocalizedEffectBundleNames(Environment.NewLine).GetValueOrDefault(bundleTypeName, bundleTypeName),
                        Description = display?.Description ?? "",
                        Thumbnail = display?.Thumbnail,
                        VideoThumbnail = display?.VideoThumbnail,
                    });
                }
                catch
                {
                    cards.Add(new EffectBundleCardItem
                    {
                        BundleTypeName = bundleTypeName,
                        Title = bundleTypeName,
                        Description = "",
                        Thumbnail = null
                    });
                }
            }

            const double cardWidth = 210;
            const double cardHeight = 160;
            const double cardMargin = 6;

            var flex = new FlexLayout
            {
                Wrap = FlexWrap.Wrap,
                Direction = FlexDirection.Row,
                JustifyContent = FlexJustify.Start,
                AlignItems = FlexAlignItems.Start,
                AlignContent = FlexAlignContent.Start
            };

            BindableLayout.SetItemsSource(flex, cards);
            BindableLayout.SetItemTemplate(flex, new DataTemplate(() =>
            {
                var image = new Image
                {
                    HeightRequest = 64,
                    WidthRequest = 64,
                    Aspect = Aspect.AspectFill,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Start
                };
                image.SetBinding(Image.SourceProperty, nameof(EffectBundleCardItem.Thumbnail));

                var title = new Label
                {
                    FontSize = 14,
                    FontAttributes = FontAttributes.Bold,
                    LineBreakMode = LineBreakMode.TailTruncation
                };
                title.SetBinding(Label.TextProperty, nameof(EffectBundleCardItem.Title));

                var desc = new Label
                {
                    FontSize = 12,
                    Opacity = 0.75,
                    LineBreakMode = LineBreakMode.TailTruncation,
                    MaxLines = 2
                };
                desc.SetBinding(Label.TextProperty, nameof(EffectBundleCardItem.Description));

                var textStack = new VerticalStackLayout
                {
                    Spacing = 2,
                    Children = { title, desc }
                };

                var row = new VerticalStackLayout
                {
                    Spacing = 10
                };
                row.Children.Add(image);
                row.Children.Add(textStack);

                var border = new Border
                {
                    WidthRequest = cardWidth,
                    HeightRequest = cardHeight,
                    Margin = new Thickness(cardMargin),
                    Padding = 10,
                    StrokeShape = new RoundRectangle { CornerRadius = 12 },
                    Stroke = new SolidColorBrush(Colors.Gray.WithAlpha(0.25f)),
                    Background = new SolidColorBrush(Colors.Transparent),
                    Content = row
                };

#if WINDOWS || MACCATALYST
                var selectTap = new TapGestureRecognizer { NumberOfTapsRequired = 1, Buttons = ButtonsMask.Primary };
                selectTap.Tapped += (_, __) =>
                {
                    if (border.BindingContext is EffectBundleCardItem item)
                        ppb.Properties["NewBundleType"] = item.BundleTypeName;
                    foreach (var child in flex.Children)
                    {
                        if (child is Border b)
                        {
                            if (b == border)
                            {
                                b.Stroke = new SolidColorBrush(Colors.DodgerBlue);
                                b.StrokeThickness = 2;
                                b.Background = new SolidColorBrush(Colors.DodgerBlue.WithAlpha(0.1f));
                            }
                            else
                            {
                                b.Stroke = new SolidColorBrush(Colors.Gray.WithAlpha(0.25f));
                                b.StrokeThickness = 1;
                                b.Background = new SolidColorBrush(Colors.Transparent);
                            }
                        }
                    }
                };

                var addTap = new TapGestureRecognizer { NumberOfTapsRequired = 2, Buttons = ButtonsMask.Primary };
                addTap.Tapped += (_, __) =>
                {
                    if (border.BindingContext is EffectBundleCardItem item)
                        AddBundle(item.BundleTypeName);
                };

                border.GestureRecognizers.Add(selectTap);
                border.GestureRecognizers.Add(addTap);

                var rightTap = new TapGestureRecognizer { NumberOfTapsRequired = 1, Buttons = ButtonsMask.Secondary };
                rightTap.Tapped += async (_, __) =>
                {
                    if (border.BindingContext is not EffectBundleCardItem item) return;
                    var verbs = new[] { PPLocalizedResources.Add_Effect, Localized.AssetPage_ShowPreview, Localized._Cancel };
                    var action = await page.DisplayActionSheetAsync(item.Title, Localized._Cancel, null, verbs[0], verbs[1]);
                    if (action == verbs[0])
                    {
                        AddBundle(item.BundleTypeName);
                    }
                    else if (action == verbs[1])
                    {
                        await page.DisplayAlertAsync(Localized._Info, item.Description, Localized._OK);
                    }
                };
                border.GestureRecognizers.Add(rightTap);
#elif ANDROID || IOS
                var pointerGesture = new PointerGestureRecognizer();
                DateTime pointerDownTime = DateTime.MinValue;
                pointerGesture.PointerPressed += (_, __) => pointerDownTime = DateTime.Now;
                pointerGesture.PointerReleased += async (_, __) =>
                {
                    if (border.BindingContext is not EffectBundleCardItem item) return;
                    var duration = (DateTime.Now - pointerDownTime).TotalMilliseconds;
                    if (duration >= 500)
                    {
                        var action = await page.DisplayActionSheetAsync(item.Title, Localized._Cancel, null, PPLocalizedResources.Add_Effect, Localized.AssetPage_ShowPreview);
                        if (action == PPLocalizedResources.Add_Effect)
                            AddBundle(item.BundleTypeName);
                        else if (action == Localized.AssetPage_ShowPreview)
                            await page.DisplayAlertAsync(Localized._Info, item.Description, Localized._OK);
                    }
                    else
                    {
                        AddBundle(item.BundleTypeName);
                    }
                };
                border.GestureRecognizers.Add(pointerGesture);
#endif

                return border;
            }));

            return new VerticalStackLayout
            {
                Spacing = 6,
                Children =
                {
                    new Label
                    {
                        Text = PPLocalizedResources.Add_Effect_Select,
                        Opacity = 0.7,
                        FontSize = 13
                    },
                    flex
                }
            };
        }

        public View BuildClassicEffectTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            PropertyPanelBuilder ppb = new();
            ppb.AddText(new Label { Text = PPLocalizedResources.EffectProp_ClassicEffectPageWarn, TextColor = Colors.Yellow });

            var localizedEffectDisplayName = EffectServices.GetLocalizedEffectNames();

            if (clip.Effects != null)
            {
                foreach (var effectKvp in clip.Effects.OrderBy(c => c.Value.Index))
                {
                    var effectKey = effectKvp.Key;
                    var effect = effectKvp.Value;
                    var factory = effect.GetFactory(EffectHelper.EffectsFactoriesEnum);
                    ppb.AddText(new TitleAndDescriptionLineLabel(effect.Name, localizedEffectDisplayName.TryGetValue(effect.TypeName, out var disp) ? disp : effect.TypeName));
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
                    if(effect is IBindableArgumentEffect be)
                    {
                        ppb.AddSeparator();
                        ppb.AddCustomChild("ID", new Label { Text = be.Id });
                        if(be is not IBindableArgumentEffectMultipleValueProcesser)
                        {
                            ppb.AddCustomChild("Binded input ID", new Label { Text = be.BindedArgumentProviderID ?? "none" });
                        }
                        else if(be is IBindableArgumentEffectMultipleValueProcesser p)
                        {
                            ppb.AddCustomChild("Binded input IDs", new Label { Text = string.Join(Environment.NewLine,p.BindedArgumentProviderIDs) });
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
                                    clip.Effects[effectKey] = EffectServices.ReCreateEffect(effect, null, enabledVal, null, page: page);
                                }
                                handler?.Invoke(s, e);
                                return;
                            }
                            if (paramName == "Index")
                            {
                                if (int.TryParse(strVal, out var indexVal))
                                {
                                    clip.Effects[effectKey] = EffectServices.ReCreateEffect(effect, null, null, indexVal, page: page);
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
                                        clip.Effects[effectKey] = EffectServices.ReCreateEffect(effect, newParams, null, null, page: page);
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



        private View BuildBundleOrderItem(EffectBundleData bundleData, ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
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
                Text = "⣿", // drag grip
                FontSize = 20,
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };

            var nameLabel = new Label
            {
                Text = string.IsNullOrWhiteSpace(bundleData.Name) ? bundleData.BundleTypeName : bundleData.Name,
                VerticalOptions = LayoutOptions.Center,
                FontSize = 16
            };

            var textStack = new HorizontalStackLayout
            {
                Children = { nameLabel },
                VerticalOptions = LayoutOptions.Center
            };

            var dragGesture = new DragGestureRecognizer { CanDrag = true };
            dragGesture.DragStarting += (s, e) =>
            {
                e.Data.Properties.Add("BundleId", bundleData.Id);
            };
            dragHandle.GestureRecognizers.Add(dragGesture);

            var dropGesture = new DropGestureRecognizer { AllowDrop = true };
            dropGesture.Drop += (s, e) =>
            {
                if (clip.EffectBundles == null) return;

                if (e.Data.Properties.TryGetValue("BundleId", out var sourceKeyObj) && sourceKeyObj is string sourceId)
                {
                    if (sourceId == bundleData.Id) return;

                    // Dictionary has no inherent order: reorder by swapping Index values.
                    if (clip.EffectBundles.TryGetValue(sourceId, out var sourceBundle) &&
                        clip.EffectBundles.TryGetValue(bundleData.Id, out var targetBundle))
                    {
                        (sourceBundle.Index, targetBundle.Index) = (targetBundle.Index, sourceBundle.Index);
                        RebuildAllEffects(clip);
                        handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                    }
                }
            };
            container.GestureRecognizers.Add(dropGesture);

            container.Children.Add(dragHandle);
            container.Children.Add(textStack);
            Grid.SetColumn(textStack, 1);

            var frame = new Border
            {
                Content = container,
                Stroke = Colors.Gray,
                StrokeThickness = 0.5,
                Padding = 0,
                Margin = new Thickness(0, 2)
            };
            frame.GestureRecognizers.Add(dropGesture);

            return frame;
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

                        clip.Effects[sourceKey] = EffectServices.ReCreateEffect(sourceEffect, null, null, tIdx, page: page);
                        clip.Effects[effectKey] = EffectServices.ReCreateEffect(targetEffect, null, null, sIdx, page: page);

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
                if (e.Id == "speedRatio")
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
