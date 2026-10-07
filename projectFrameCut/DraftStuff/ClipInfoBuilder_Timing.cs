using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationAPIBase.Views.TabbedView;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;
using ContentView = Microsoft.Maui.Controls.ContentView;
using GridLength = Microsoft.Maui.GridLength;
using Switch = Microsoft.Maui.Controls.Switch;
using Thickness = Microsoft.Maui.Thickness;

namespace projectFrameCut.DraftStuff
{
    public partial class ClipInfoBuilder
    {
        #region timing

        private View BuildTimingTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            var tabs = new CompactTabView();
            Action refreshTiming = () => { };
            Action refreshSpeed = () => { };
            tabs.TabItems.Add(new TabbedViewItem
            {
                Header = PPLocalizedResources.Tabs_Timing,
                Tag = "timing",
                LazyContentFactory = () => BuildTimingOptions(clip, handler, out refreshTiming)
            });
            if (!clip.isInfiniteLength && clip.ClipType != ClipMode.MarkingClip)
            {
                tabs.TabItems.Add(new TabbedViewItem
                {
                    Header = PPLocalizedResources.Tabs_SpeedRatio,
                    Tag = "speedAndRatio",
                    LazyContentFactory = () => BuildSpeedAndRatioTab(clip, handler, out refreshSpeed)
                });
            }
            tabs.OnTabSwitched += (_, item) =>
            {
                page.SelectedTimingTab = item.Tag;
                if (item.Tag == "timing") refreshTiming();
                else refreshSpeed();
            };
            tabs.SelectByTag(page.SelectedTimingTab);
            return tabs;
        }

        private uint ReadTimingLength(ClipElementUI clip) => clip.origLength > 0
            ? (uint)Math.Clamp(Math.Round(clip.origLength / page.FrameToPixel(1)), 1d, uint.MaxValue)
            : Math.Max(1u, clip.lengthInFrame);

        private View BuildTimingOptions(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler, out Action refresh)
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
            bool finite = !clip.isInfiniteLength && clip.maxFrameCount > 0;
            bool extendToWhole = ReadExtendToWholeDraft(clip);
            bool updating = false;
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

            var fields = new VerticalStackLayout { Spacing = 8, IsEnabled = !extendToWhole };
            var startEntry = new Entry { Keyboard = Keyboard.Numeric, HorizontalOptions = LayoutOptions.Fill };
            var sourceEntry = new Entry { Keyboard = Keyboard.Numeric, HorizontalOptions = LayoutOptions.Fill };
            var lengthEntry = new Entry { Keyboard = Keyboard.Numeric, HorizontalOptions = LayoutOptions.Fill };
            fields.Children.Add(new Label { Text = PPLocalizedResources.Timing_StartFrame, FontSize = 13 });
            fields.Children.Add(startEntry);
            if (!clip.isInfiniteLength)
            {
                fields.Children.Add(new Label { Text = PPLocalizedResources.Timing_SourceStartFrame, FontSize = 13 });
                fields.Children.Add(sourceEntry);
            }
            fields.Children.Add(new Label { Text = PPLocalizedResources.Timing_InfLength_Input, FontSize = 13 });
            fields.Children.Add(lengthEntry);

            var infoLabel = new Label { FontSize = 12, TextColor = Colors.Gray };
            var errorLabel = new Label { FontSize = 12, TextColor = Colors.Orange, IsVisible = false };
            var applyBtn = new Button { Text = Localized._Apply, HorizontalOptions = LayoutOptions.End };
            ClipRangeSlider? rangeSlider = null;
            if (finite)
            {
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

                rangeSlider = new ClipRangeSlider
                {
                    Maximum = clip.maxFrameCount,
                    ThumbnailPath = thumbPath,
                    Margin = new Thickness(10, 20, 10, 20)
                };
                rangeSlider.ValuesChanged += (s, e) =>
                {
                    uint newStart = (uint)Math.Round(rangeSlider.LowerValue);
                    uint newEnd = (uint)Math.Round(rangeSlider.UpperValue);
                    updating = true;
                    sourceEntry.Text = newStart.ToString();
                    lengthEntry.Text = Math.Max(1u, newEnd - newStart).ToString();
                    updating = false;
                    UpdatePreview();
                };
                rangeSlider.DragCompleted += (s, e) =>
                {
                    uint newStart = (uint)Math.Round(rangeSlider.LowerValue);
                    uint newEnd = (uint)Math.Round(rangeSlider.UpperValue);
                    ApplyFrames(s, ReadStartFrame(), newStart, Math.Max(1u, newEnd - newStart));
                };
                fields.Children.Add(rangeSlider);
            }

            fields.Children.Add(new HorizontalStackLayout
            {
                HorizontalOptions = LayoutOptions.End,
                Spacing = 8,
                Children =
                {
                    new Button { Text = "-1s", Command = new Command(() => ChangeLength(-Math.Max(1L, (long)Math.Round(fps)))) },
                    new Button { Text = "+1s", Command = new Command(() => ChangeLength(Math.Max(1L, (long)Math.Round(fps)))) }
                }
            });
            fields.Children.Add(infoLabel);
            fields.Children.Add(errorLabel);
            fields.Children.Add(applyBtn);
            stack.Children.Add(fields);

            uint ReadStartFrame() => (uint)Math.Clamp(Math.Round(Math.Max(0, clip.Clip.TranslationX)
                / (page.FrameToPixel(1) * clip.SecondPerFrameRatio)), 0, uint.MaxValue - 1d);

            bool TryReadFrames(out uint start, out uint sourceStart, out uint length)
            {
                sourceStart = clip.relativeStartFrame;
                length = 0;
                return uint.TryParse(startEntry.Text, out start)
                    && (clip.isInfiniteLength || uint.TryParse(sourceEntry.Text, out sourceStart))
                    && uint.TryParse(lengthEntry.Text, out length) && length > 0
                    && (ulong)start + length <= uint.MaxValue
                    && (ulong)sourceStart + length <= uint.MaxValue
                    && (!finite || (ulong)sourceStart + length <= clip.maxFrameCount);
            }

            void UpdatePreview()
            {
                if (updating) return;
                bool valid = TryReadFrames(out _, out var sourceStart, out var length);
                applyBtn.IsEnabled = valid;
                errorLabel.IsVisible = !valid;
                errorLabel.Text = valid ? string.Empty : PPLocalizedResources.Timing_InvalidFrames;
                infoLabel.Text = valid && fps > 0 ? PPLocalizedResources.Timing_LengthInfo_Start(sourceStart, length, fps) : string.Empty;
                if (valid && rangeSlider is not null)
                {
                    rangeSlider.LowerValue = sourceStart;
                    rangeSlider.UpperValue = (double)sourceStart + length;
                }
            }

            void SyncFields()
            {
                uint sourceStart = finite ? Math.Min(clip.relativeStartFrame, clip.maxFrameCount - 1) : clip.relativeStartFrame;
                updating = true;
                startEntry.Text = ReadStartFrame().ToString();
                sourceEntry.Text = sourceStart.ToString();
                lengthEntry.Text = (finite ? Math.Min(ReadTimingLength(clip), clip.maxFrameCount - sourceStart) : ReadTimingLength(clip)).ToString();
                fields.IsEnabled = !ReadExtendToWholeDraft(clip);
                updating = false;
                UpdatePreview();
            }

            void ApplyFrames(object? sender, uint start, uint sourceStart, uint length)
            {
                if ((ulong)start + length > uint.MaxValue || (ulong)sourceStart + length > uint.MaxValue) return;
                var old = (ReadStartFrame(), clip.relativeStartFrame, ReadTimingLength(clip));
                if (old == (start, sourceStart, length)) return;
                clip.relativeStartFrame = sourceStart;
                clip.lengthInFrame = length;
                clip.origLength = page.FrameToPixel(length);
                clip.ApplySpeedRatio();
                if (old.Item1 != start)
                {
                    clip.Clip.TranslationX = page.FrameToPixel(start) * clip.SecondPerFrameRatio;
                    clip.origX = clip.layoutX = clip.Clip.TranslationX;
                }
                SyncFields();
                Log($"Updated clip {clip.Id} timing: {old} -> {(start, sourceStart, length)}.");
                handler?.Invoke(sender, new PropertyPanelPropertyChangedEventArgs("timing", (start, sourceStart, length), old));
            }

            void ChangeLength(long delta)
            {
                if (!uint.TryParse(lengthEntry.Text, out var length)) return;
                uint max = uint.MaxValue;
                if (finite)
                {
                    if (!uint.TryParse(sourceEntry.Text, out var sourceStart) || sourceStart >= clip.maxFrameCount) return;
                    max = clip.maxFrameCount - sourceStart;
                }
                lengthEntry.Text = Math.Clamp((long)length + delta, 1, max).ToString();
            }

            void ApplyEntries(object? sender, EventArgs e)
            {
                if (TryReadFrames(out var start, out var sourceStart, out var length)) ApplyFrames(sender, start, sourceStart, length);
            }
            startEntry.TextChanged += (_, _) => UpdatePreview();
            sourceEntry.TextChanged += (_, _) => UpdatePreview();
            lengthEntry.TextChanged += (_, _) => UpdatePreview();
            applyBtn.Clicked += ApplyEntries;
            startEntry.Completed += ApplyEntries;
            sourceEntry.Completed += ApplyEntries;
            lengthEntry.Completed += ApplyEntries;

            if (!finite && (clip.origTrack ?? -1) >= DraftPage.SubTrackOffset)
            {
                bool hasOtherClipsInTrack = page.Clips.Values.Any(c => c is not null && c.Id != clip.Id
                    && c.ShouldDisplayInUI && !c.IsGhost && !c.IsShadow && c.origTrack == clip.origTrack);
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
                    fields.IsEnabled = !e.Value;
                    handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("ExtendToWholeDraft", e.Value, extendToWhole));
                    extendToWhole = e.Value;
                };
                var row = new Grid
                {
                    ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
                    ColumnSpacing = 8,
                    Margin = new Thickness(0, 8, 0, 0)
                };
                row.Add(new Label
                {
                    Text = PPLocalizedResources.Timing_InfLength_ExtendToWholeDraft,
                    FontSize = 13,
                    VerticalOptions = LayoutOptions.Center
                }, 0, 0);
                row.Add(extendSwitch, 1, 0);
                stack.Children.Add(row);
                stack.Children.Add(new Label
                {
                    Text = hasOtherClipsInTrack ? PPLocalizedResources.Timing_InfLength_ExtendToWholeDraft_NotAvailable
                        : PPLocalizedResources.Timing_InfLength_ExtendToWholeDraft_Available,
                    FontSize = 11,
                    TextColor = hasOtherClipsInTrack ? Colors.Orange : Colors.Gray
                });
            }

            refresh = SyncFields;
            SyncFields();
            return new ScrollView { Content = stack };
        }
        #endregion

        #region speed and ratio

        private View BuildSpeedAndRatioTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler, out Action refresh)
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
            float fps = page.ProjectInfo.TargetFrameRate;
            var durationHintLabel = new Label { FontSize = 12, TextColor = Colors.Gray };
            void UpdateDurationHint()
            {
                uint length = ReadTimingLength(clip);
                uint effectiveLength = clip.Effects?.Values.OfType<ISpeedVarianceProvider>().FirstOrDefault()?.GetEffectiveLength(length) ?? length;
                durationHintLabel.Text = fps > 0 ? PPLocalizedResources.SpeedAndRatio_Duration(length / (double)fps, effectiveLength / (double)fps) : string.Empty;
            }
            refresh = UpdateDurationHint;

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
                UpdateDurationHint();
                ppb.AddCustomChild(durationHintLabel);
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
                        UpdateDurationHint();
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
