using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;
using static projectFrameCut.ApplicationAPIBase.Helpers.TextHelper;
using ContentView = Microsoft.Maui.Controls.ContentView;
using CornerRadius = Microsoft.Maui.CornerRadius;
using DataTemplate = Microsoft.Maui.Controls.DataTemplate;
using Environment = System.Environment;
using GridLength = Microsoft.Maui.GridLength;
using GridUnitType = Microsoft.Maui.GridUnitType;
using Switch = Microsoft.Maui.Controls.Switch;
using TextAlignment = Microsoft.Maui.TextAlignment;
using Thickness = Microsoft.Maui.Thickness;

#if WINDOWS
using Microsoft.UI.Xaml;

#endif

#if IOS
using projectFrameCut.Platforms.iOS;

#endif

namespace projectFrameCut.DraftStuff
{
    public partial class ClipInfoBuilder
    {
        #region timing

        private View BuildTimingTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            static bool ReadExtendToWholeDraft(ClipElementUI c)
            {
                if (c.ExtraData is null) return false;
                if (!c.ExtraData.TryGetValue("ExtendToWholeDraft", out var raw) || raw is null) return false;
                if (raw is bool b) return b;
                if (raw is JsonElement je)
                {
                    if (je.ValueKind == JsonValueKind.True) return true;
                    if (je.ValueKind == JsonValueKind.False) return false;
                    if (je.ValueKind == JsonValueKind.String && bool.TryParse(je.GetString(), out var parsed)) return parsed;
                }
                return bool.TryParse(raw.ToString(), out var fallback) && fallback;
            }

            float fps = page.ProjectInfo.TargetFrameRate;
            var stack = new VerticalStackLayout { Spacing = 8, Padding = new Thickness(8) };

            string srcInfo = clip.isInfiniteLength
                ? PPLocalizedResources.Timing_InfLength
                : PPLocalizedResources.Timing_LengthInfo(clip.maxFrameCount, fps);
            stack.Children.Add(new Label
            {
                Text = srcInfo,
                FontSize = 12,
                TextColor = Color.FromArgb("#AAAAAA"),
                Margin = new Thickness(0, 0, 0, 8)
            });

            if (!clip.isInfiniteLength && clip.maxFrameCount > 0)
            {
                // ── 有限长度：使用范围滑块 ──────────────────────────────────────
                uint safeStart = Math.Min(clip.relativeStartFrame, clip.maxFrameCount > 0 ? clip.maxFrameCount - 1 : 0);
                uint safeLen = clip.lengthInFrame > 0
                    ? Math.Min(clip.lengthInFrame, clip.maxFrameCount - safeStart)
                    : Math.Max(1u, clip.maxFrameCount - safeStart);

                string? thumbPath = null;
                if (!string.IsNullOrEmpty(clip.SourcePath))
                {
                    if (clip.SourcePath.StartsWith("$") && page.Assets.TryGetValue(clip.SourcePath.Substring(1), out var assetObj))
                    {
                        thumbPath = assetObj.ThumbnailPath;
                    }
                    else
                    {
                        thumbPath = clip.SourcePath;
                    }
                }

                var rangeSlider = new ClipRangeSlider
                {
                    Maximum = clip.maxFrameCount,
                    LowerValue = safeStart,
                    UpperValue = safeStart + safeLen,
                    ThumbnailPath = thumbPath,
                    Margin = new Thickness(10, 20, 10, 20)
                };

                var infoLabel = new Label
                {
                    Text = PPLocalizedResources.Timing_LengthInfo_Start(safeStart, safeLen, fps),
                    TextColor = Colors.White,
                    FontSize = 12,
                    HorizontalOptions = LayoutOptions.Center
                };

                rangeSlider.ValuesChanged += (s, e) =>
                {
                    uint newStart = (uint)Math.Round(rangeSlider.LowerValue);
                    uint newEnd = (uint)Math.Round(rangeSlider.UpperValue);
                    uint newLen = newEnd - newStart;
                    if (newLen < 1) newLen = 1;
                    infoLabel.Text = PPLocalizedResources.Timing_LengthInfo_Start(safeStart, safeLen, fps);
                };

                rangeSlider.DragCompleted += (s, e) =>
                {
                    uint newStart = (uint)Math.Round(rangeSlider.LowerValue);
                    uint newEnd = (uint)Math.Round(rangeSlider.UpperValue);
                    uint newLen = newEnd - newStart;
                    if (newLen < 1) newLen = 1;

                    clip.relativeStartFrame = newStart;
                    clip.lengthInFrame = newLen;

                    double newPx = page.FrameToPixel(newLen);
                    clip.Clip.WidthRequest = newPx;
                    clip.origLength = newPx;

                    handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("relativeStartFrame", newStart, newStart));
                    handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("lengthInFrame", newLen, newLen));
                };

                stack.Children.Add(rangeSlider);
                stack.Children.Add(infoLabel);
            }
            else
            {
                var extendToWhole = ReadExtendToWholeDraft(clip);

                // ── 无限长度：使用文本框手动输入 ────────────────────────────
                if (!extendToWhole)
                {
                    uint initFrames = clip.lengthInFrame > 0
                    ? clip.lengthInFrame
                    : page.PixelToFrame(clip.origLength > 0 ? clip.origLength : 300d);


                    stack.Children.Add(new Label
                    {
                        Text = PPLocalizedResources.Timing_InfLength_Input,
                        FontSize = 13,
                        TextColor = Colors.White,
                        Margin = new Thickness(0, 12, 0, 0)
                    });

                    var lengthEntry = new Entry
                    {
                        Text = initFrames.ToString(),
                        Keyboard = Keyboard.Numeric,
                        HorizontalOptions = LayoutOptions.Fill,
                        Placeholder = "42"
                    };

                    var add1sButton = new Button
                    {
                        Text = "+1s",
                        Command = new Command(() =>
                        {
                            this.page.Dispatcher.Dispatch(() => lengthEntry.Text = ((double.TryParse(lengthEntry.Text, out var v) ? v : 0) + (1 / page.SecondsPerFrame)).ToString());
                        })
                    };
                    var minus1sButton = new Button
                    {
                        Text = "-1s",
                        Command = new Command(() =>
                        {
                            this.page.Dispatcher.Dispatch(() => lengthEntry.Text = ((double.TryParse(lengthEntry.Text, out var v) ? v : 0) - (1 / page.SecondsPerFrame)).ToString());
                        })
                    };

                    var smallAddLine = new HorizontalStackLayout
                    {
                        Children =
                        {
                            minus1sButton,
                            add1sButton,
                        },
                        HorizontalOptions = LayoutOptions.End,
                        Spacing = 8
                    };

                    // 实时秒数提示
                    var secHintLabel = new Label
                    {
                        Text = fps > 0 ? $"≈ {initFrames / fps:F2}s" : string.Empty,
                        FontSize = 11,
                        TextColor = Color.FromArgb("#AAAAAA"),
                        HorizontalOptions = LayoutOptions.Start
                    };
                    lengthEntry.TextChanged += (s, e) =>
                    {
                        secHintLabel.Text = uint.TryParse(lengthEntry.Text, out var previewFrames) && fps > 0
                            ? $"≈ {previewFrames / fps:F2}s"
                            : string.Empty;
                    };

                    var applyBtn = new Button
                    {
                        Text = Localized._Apply,
                        HorizontalOptions = LayoutOptions.End,
                        Margin = new Thickness(0, 6, 0, 0)
                    };
                    applyBtn.Clicked += (s, e) =>
                    {
                        if (uint.TryParse(lengthEntry.Text, out var newLen) && newLen > 0)
                        {
                            clip.lengthInFrame = newLen;
                            double newPx = page.FrameToPixel(newLen);
                            clip.Clip.WidthRequest = newPx;
                            clip.origLength = newPx;
                            handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("lengthInFrame", newLen, newLen));
                        }
                    };

                    stack.Children.Add(lengthEntry);
                    stack.Children.Add(new Grid { Children = { secHintLabel, smallAddLine } });
                    stack.Children.Add(applyBtn);
                }
                if ((clip.origTrack ?? -1) >= DraftPage.SubTrackOffset)
                {
                    bool hasOtherClipsInTrack = page.Clips.Values.Any(c =>
                        c is not null
                        && c.Id != clip.Id
                        && c.ShouldDisplayInUI
                        && !c.IsGhost
                        && !c.IsShadow
                        && c.origTrack == clip.origTrack);

                    stack.Children.Add(new BoxView
                    {
                        HeightRequest = 1,
                        Color = Colors.White.WithAlpha(0.08f),
                        Margin = new Thickness(0, 8, 0, 2)
                    });

                    var extendSwitch = new Switch
                    {
                        IsToggled = extendToWhole,
                        HorizontalOptions = LayoutOptions.End,
                        VerticalOptions = LayoutOptions.Center,
                        IsEnabled = !hasOtherClipsInTrack
                    };

                    extendSwitch.Toggled += (s, e) =>
                    {
                        clip.ExtraData ??= new Dictionary<string, object>();
                        clip.ExtraData["ExtendToWholeDraft"] = e.Value;
                        handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("ExtendToWholeDraft", e.Value, extendToWhole));
                        extendToWhole = e.Value;
                    };

                    var row = new Grid
                    {
                        ColumnDefinitions =
                    {
                        new ColumnDefinition(GridLength.Star),
                        new ColumnDefinition(GridLength.Auto)
                    },
                        ColumnSpacing = 8,
                        Margin = new Thickness(0, 4, 0, 0)
                    };

                    row.Add(new Label
                    {
                        Text = PPLocalizedResources.Timing_InfLength_ExtendToWholeDraft,
                        FontSize = 13,
                        TextColor = Colors.White,
                        VerticalOptions = LayoutOptions.Center,
                    }, 0, 0);
                    row.Add(extendSwitch, 1, 0);

                    stack.Children.Add(row);

                    stack.Children.Add(new Label
                    {
                        Text = hasOtherClipsInTrack
                            ? PPLocalizedResources.Timing_InfLength_ExtendToWholeDraft_NotAvailable
                            : PPLocalizedResources.Timing_InfLength_ExtendToWholeDraft_Available,
                        FontSize = 11,
                        TextColor = hasOtherClipsInTrack ? Colors.Orange : Color.FromArgb("#AAAAAA")
                    });
                }
            }



            return new ScrollView { Content = stack };
        }
        #endregion

        #region speed and ratio

        private View BuildSpeedAndRatioTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            static bool IsSpeedVarianceProvider(IEffectProvider bundle) => bundle.TypeOfEffect == EffectType.SpeedVarianceProvider && bundle.Target == EffectTarget.SpeedVariance;

            clip.Effects ??= new Dictionary<string, IEffect>();
            clip.EffectProviders ??= new Dictionary<Guid, IEffectProvider>();

            var allProviderFactories = EffectServices.GetAvailableEffectProviders();
            var localizedProviderNames = EffectServices.GetLocalizedEffectProviderNames("", false);

            var speedProviderFactoryItems = allProviderFactories
                .Where(kvp => kvp.Value().Target == EffectTarget.SpeedVariance)
                .Select(kvp => new
                {
                    TypeName = kvp.Key,
                    Factory = kvp.Value,
                    DisplayName = localizedProviderNames.GetValueOrDefault(kvp.Key, kvp.Key)
                })
                .OrderBy(x => x.DisplayName, StringComparer.Ordinal)
                .ToList();

            var speedProviders = clip.EffectProviders
                ?.Where(kvp => kvp.Value.Target == EffectTarget.SpeedVariance)
                ?.Select(c => c.Value)
                ?.ToList() ?? [];

            var ppb = new PropertyPanelBuilder();

            if (speedProviders.Count > 1)
            {
                ppb.AddText(new Label
                {
                    Text = PPLocalizedResources.SpeedAndRatio_ErrMultiplePvd,
                    TextColor = Colors.Orange
                });
            }

            if (speedProviders.Count == 0)
            {
                ppb.AddText(new SingleLineLabel(PPLocalizedResources.SpeedAndRatio_None, 20));
            }
            var bundle = speedProviders.FirstOrDefault();
            if (bundle is not null)
            {
                var bundleId = bundle.Id;
                string localizedName = localizedProviderNames.GetValueOrDefault(bundle.TypeName, bundle.TypeName);

                ppb.AddText(new SingleLineLabel(localizedName ?? bundle.Name, 25));

                try
                {
                    var bundlePpb = EffectServices.GetUIProvider(bundle).CreateUI(bundle);
                    ppb.AddFromAnother(bundlePpb, bundle);
                }
                catch (Exception ex)
                {
                    Log(ex, $"loading speed variance bundle {bundle.TypeName}", this);
                    ppb.AddText(new Label
                    {
                        Text = $"Error loading bundle UI: {ex.Message}",
                        TextColor = Colors.Yellow
                    });
                }

                ppb.AddButton(PPLocalizedResources.EffectProp_Remove, (s, e) =>
                {
                    if (clip.EffectProviders is { } providers)
                        EffectBindingHelper.RemoveProvider(providers, bundleId);
                    RebuildAllEffects(clip);
                    handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                    return;
                });
                ppb.AddSeparator();
                var effLength = (clip.Effects.First(c => c.Value.TypeOfEffect == EffectType.SpeedVarianceProvider).Value as ISpeedVarianceProvider)?.GetEffectiveLength(clip.lengthInFrame) ?? clip.lengthInFrame;
                ppb.AddCustomChildWithID("durationHintLabel", new Label { Text = clip.lengthInFrame != 0 ? PPLocalizedResources.SpeedAndRatio_Duration((double)clip.lengthInFrame, (double)effLength) : "", FontSize = 12, TextColor = Colors.Gray });
            }
            else
            {
                ppb.AppendWhen(speedProviders.Count == 0 && speedProviderFactoryItems.Count > 0, c => c.AddCustomChild(BuildAddEffectPanel(EffectTarget.SpeedVariance, page, allProviderFactories, ppb, handler, false)));
            }

            ppb.PropertyChanged += (s, e) =>
            {
                if (s is IEffectProvider senderProvider)
                {
                    if (clip.EffectProviders.TryGetValue(senderProvider.Id, out var editingProvider))
                    {
                        var fieldUpdate = EffectServices.GetUIProvider(senderProvider).HandlePropertyPanelChange(senderProvider, e);
                        var updated = fieldUpdate.newParams;
                        if (fieldUpdate.newFields is not null)
                        {
                            editingProvider.Fields = fieldUpdate.newFields;
                        }
                        else if (updated is { Count: > 0 })
                        {
                            var fields = new Dictionary<string, IEffectArgumentField>();
                            foreach (var kvp in updated)
                            {
                                fields[kvp.Key] = new StaticEffectArgumentField(kvp.Value, EffectArgumentFieldType.Unknown);
                            }
                            editingProvider.Fields = fields;
                        }
                        RebuildAllEffects(clip);
                        clip.ApplySpeedRatio();
                        if (ppb.Components.TryGetValue("durationHintLabel", out var la) && la is Label l)
                        {
                            var effLength = (clip.Effects.First(c => c.Value.TypeOfEffect == EffectType.SpeedVarianceProvider).Value as ISpeedVarianceProvider)?.GetEffectiveLength(clip.lengthInFrame) ?? clip.lengthInFrame;
                            l.Text = clip.lengthInFrame != 0 ? PPLocalizedResources.SpeedAndRatio_Duration((double)clip.lengthInFrame, (double)effLength) : "";
                        }
                        handler?.Invoke(s, e);
                    }
                    return;
                }
                else if (e.Id == "AddProvider")
                {
                    int currentCount = clip.EffectProviders.Values.Count(IsSpeedVarianceProvider);
                    if (currentCount >= 1)
                    {
                        page.Dispatcher.Dispatch(async () =>
                        {
                            await page.DisplayAlertAsync(Localized._Info, PPLocalizedResources.SpeedAndRatio_ErrSingle, Localized._OK);
                        });
                        return;
                    }
                    if (ppb.Properties.TryGetValue("NewProviderType", out var typeObj) && typeObj is string bundleTypeName)
                    {
                        if (allProviderFactories.TryGetValue(bundleTypeName, out var factory))
                        {
                            var instance = factory();
                            instance.Id = Guid.NewGuid();
                            instance.DisconnectMainInput();
                            instance.SetFinalOutputSource(false);
                            clip.EffectProviders ??= new Dictionary<Guid, IEffectProvider>();
                            clip.EffectProviders[instance.Id] = instance;
                            EffectBindingHelper.AutoConnectProviderToOutput(clip.EffectProviders, instance, clip.GetEffectTarget());

                            RebuildAllEffects(clip);
                            handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                        }
                    }
                }

                handler?.Invoke(s, e);
            };

            return ppb.BuildWithScrollView();
        }

        #endregion
    }
}
