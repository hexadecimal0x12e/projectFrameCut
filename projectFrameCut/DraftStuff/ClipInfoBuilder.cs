using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using Microsoft.Extensions.AI;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics.Text;
using Microsoft.Maui.Layouts;
using Microsoft.Maui.Platform;
using projectFrameCut.AIAssistance;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationAPIBase.Text;
using projectFrameCut.ApplicationAPIBase.Views.MarkdownToXAML.Codeblock;
using projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;
using projectFrameCut.ApplicationAPIBase.Views.Pickers;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationAPIBase.Views.TabbedView;
using projectFrameCut.ApplicationPluginBase.Effect;
using projectFrameCut.Asset;
using projectFrameCut.Controls;
using projectFrameCut.Converters;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.InteractableEditor;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.ClipsAndTracks.Text;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.RenderAPIBase.Sources;
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
        #region id const
        private const string InternalRotationID = "__Internal_Rotation__";
        private const string InternalCropID = "__Internal_Crop__";
        private static readonly Guid InternalCropProviderGuid = new("a3a744cc-53b7-4d5e-8dd5-4c66077d9401");
        private static readonly Guid InternalColorAdjustmentProviderGuid = new("dc3cfef8-1782-4428-8862-f9a0995c02d9");
        private const string AllowFreeScaleResizeKey = "AllowFreeScaleResize";
        private const string DirectCropEnabledKey = "__Internal_DirectCropEnabled__";
        private const string DirectCropWidthKey = "__Internal_DirectCropWidth__";
        private const string DirectCropHeightKey = "__Internal_DirectCropHeight__";
        private const string TextStyleProviderFromKey = "TextStyleProvider_FromPlugin";
        private const string TextStyleProviderTypeKey = "TextStyleProvider_TypeName";
        private const string TextStyleProviderParamsKey = "TextStyleProvider_Parameters";
        #endregion

        #region init
        DraftPage page;
        TabbedView tabbedView = new();
        private string? lastSelectedTab;

        static JsonSerializerOptions savingOpts = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

        static bool showAllEffect = false;

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
                ClipMode.VectorCanvasClip or ClipMode.VectorComponentClip => Colors.CornflowerBlue.ToArgbHex(),
                _ => Colors.Gray.ToArgbHex(),
            };
        }


        public ClipInfoBuilder(DraftPage page)
        {
            this.page = page;
            PPLocalizedResources = ISimpleLocalizerBase_PropertyPanel.GetMapping().TryGetValue(Localized._LocaleId_, out var pploc) ? pploc : ISimpleLocalizerBase_PropertyPanel.GetMapping().First().Value;
        }


        public const string TabOrderSettingKey = "Edit_ClipInfoTabOrder";

        public static (string Tag, string Header)[] GetTabOrderOptions()
        {
            var r = ISimpleLocalizerBase_PropertyPanel.GetMapping().TryGetValue(Localized._LocaleId_, out var loc)
                ? loc : ISimpleLocalizerBase_PropertyPanel.GetMapping().First().Value;
            return
            [
                ("general", Localized.MainSettingsPage_Tab_General),
                ("sourceProperties", r.Tabs_SourceProperties),
                ("audio", r.General_Audio),
                ("text", r.TextOption_TabTitle),
                ("timing", r.Tabs_Timing),
                ("sizeAndPosition", r.Tabs_SizeAndPosition),
                ("keyframe", Localized.InteractableEditor_KeyFrame),
                ("effect", r.Tabs_Effect),
                ("transform", Localized.Transform_Tab),
                ("mixture", r.Tabs_Mixture),
                ("colorAdjust", r.Tabs_ColorAdjust),
                ("effectClassic", r.Tabs_Effect_Classic),
                ("textClassic", r.TextOption_TabTitle_Classic),
                ("add", Localized.DraftPage_CenterMenuBar_AddClip)
            ];
        }

        public static string[] GetTabOrder()
        {
            var defaults = GetTabOrderOptions().Select(t => t.Tag).ToArray();
            return SettingsManager.GetSetting(TabOrderSettingKey)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => t is "vector" or "mask" ? "sourceProperties" : t)
                .Where(t => defaults.Contains(t))
                .Concat(defaults)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        public Task<TabbedView> Build(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
            => BuildClipTabs(clip, handler, null);

        private async Task<TabbedView> BuildClipTabs(ClipElementUI clip,
            EventHandler<PropertyPanelPropertyChangedEventArgs> handler, ProjectAddClipView? addClipView)
        {
            tabbedView = new();
            tabbedView.Background = page.Background;
            var tabs = new List<TabbedViewItem>();
            EffectStageUI.Resolve(clip, page);
            if (clip.ClipType == ClipMode.VideoClip) page.EnsureGeneratedSoundTrack(clip);
            tabs.Add(new TabbedViewItem
            {
                Header = Localized.MainSettingsPage_Tab_General,
                Content = BuildGeneralTab(clip, handler),
                Tag = "general"
            });
            if (clip.ClipType is ClipMode.VideoClip or ClipMode.SolidColorClip or ClipMode.VectorCanvasClip
                or ClipMode.VectorComponentClip or ClipMode.MaskClip or ClipMode.TextClip or ClipMode.SubtitleClip
                || clip.ClipType != ClipMode.MarkingClip && clip.SupportedStages.Contains(Stage.SourceReplacement))
                tabs.Add(new TabbedViewItem
                {
                    Header = PPLocalizedResources.Tabs_SourceProperties,
                    LazyAsyncContentFactory = () => BuildSourcePropertiesTab(clip, handler),
                    Tag = "sourceProperties"
                });
            if (clip.ClipType == ClipMode.AudioClip || (clip.ClipType == ClipMode.VideoClip && page.GetBoundSoundTrack(clip) is not null))
            {
                tabs.Add(new TabbedViewItem
                {
                    Header = PPLocalizedResources.General_Audio,
                    LazyContentFactory = () => BuildAudioTab(clip, handler),
                    Tag = "audio"
                });
            }
            if (clip.ClipType == ClipMode.TextClip || clip.ClipType == ClipMode.SubtitleClip)
            {
                tabs.Add(new TabbedViewItem
                {
                    Header = clip.ClipType == ClipMode.SubtitleClip ? Localized.Subtitle_Title : PPLocalizedResources.TextOption_TabTitle,
                    LazyAsyncContentFactory = () => clip.ClipType == ClipMode.SubtitleClip ? BuildSubtitleOptionTab(clip, handler) : BuildTextOptionTab(clip, handler),
                    Tag = "text"
                });
            }
            if (clip.isInfiniteLength || (clip.LeftHandle?.IsVisible == true && clip.RightHandle?.IsVisible == true)
                || clip.ClipType != ClipMode.MarkingClip)
            {
                tabs.Add(new TabbedViewItem
                {
                    Header = PPLocalizedResources.Tabs_Timing,
                    LazyContentFactory = () => BuildTimingTab(clip, handler),
                    Tag = "timing"
                });
            }
            if (clip.ClipType is ClipMode.VideoClip or ClipMode.PhotoClip or ClipMode.VectorComponentClip or ClipMode.SubtitleClip or ClipMode.MaskClip)
            {
                tabs.Add(new TabbedViewItem
                {
                    Header = PPLocalizedResources.Tabs_SizeAndPosition,
                    LazyContentFactory = () => BuildSizeAndPositionTab(clip, handler),
                    Tag = "sizeAndPosition"
                });

            }
            if (clip.ClipType != ClipMode.MarkingClip)
            {
                tabs.Add(new TabbedViewItem
                {
                    Header = Localized.InteractableEditor_KeyFrame,
                    LazyContentFactory = () => BuildKeyFrameTab(clip, handler),
                    Tag = "keyframe"
                });
                tabs.Add(new TabbedViewItem
                {
                    Header = PPLocalizedResources.Tabs_Effect,
                    LazyAsyncContentFactory = () => BuildEffectTab(clip, handler),
                    Tag = "effect"
                });
                tabs.Add(new TabbedViewItem
                {
                    Header = Localized.Transform_Tab,
                    LazyContentFactory = () => BuildTransformTab(clip),
                    Tag = "transform"
                });
                if (clip.ClipType != ClipMode.AudioClip)
                {
                    tabs.Add(new TabbedViewItem
                    {
                        Header = PPLocalizedResources.Tabs_Mixture,
                        LazyContentFactory = () => BuildMixtureTab(clip, handler),
                        Tag = "mixture"
                    });
                    tabs.Add(new TabbedViewItem
                    {
                        Header = PPLocalizedResources.Tabs_ColorAdjust,
                        LazyContentFactory = () => BuildColorAdjustmentTab(clip, handler),
                        Tag = "colorAdjust"
                    });
                }
                if (SettingsManager.IsBoolSettingTrue("edit_ShowAllEffects"))
                {
                    tabs.Add(new TabbedViewItem
                    {
                        Header = PPLocalizedResources.Tabs_Effect_Classic,
                        LazyContentFactory = () => BuildClassicEffectTab(clip, handler),
                        Tag = "effectClassic"
                    });
                    if (clip.ClipType == ClipMode.TextClip)
                    {
                        tabs.Add(new TabbedViewItem
                        {
                            Header = PPLocalizedResources.TextOption_TabTitle_Classic,
                            LazyContentFactory = () => BuildTextOptionClassicTab(clip, handler),
                            Tag = "textClassic"
                        });
                    }
                }
            }

            if (addClipView is not null)
            {
                tabs.Add(new TabbedViewItem
                {
                    Header = Localized.DraftPage_CenterMenuBar_AddClip,
                    Content = addClipView,
                    Tag = "add"
                });
            }
            var order = GetTabOrder();
            tabbedView.TabItems = new(tabs.OrderBy(t =>
            {
                var i = Array.IndexOf(order, t.Tag);
                return i < 0 ? int.MaxValue : i;
            }));
            tabbedView.OnTabSwitched += (_, item) => lastSelectedTab = item.Tag;
            if (!string.IsNullOrEmpty(lastSelectedTab)) tabbedView.SelectByTag(lastSelectedTab);

            tabbedView.HeaderRightContent = new Button
            {
                Text = "\ue5d5",
                FontFamily = "Icons",
                WidthRequest = 40,
                HeightRequest = 35,
                Padding = 0,
                VerticalOptions = LayoutOptions.Center,
                Command = new Command(() =>
                {
                    tabbedView.Dispatcher.Dispatch(() =>
                    {
                        if (page.SelectedClip is not null) page.RefreshPropertyPanel(page.SelectedClip);
                    });
                })
            };

            return tabbedView;
        }

        public async Task<TabbedView> BuildFixed(
            ClipElementUI? clip,
            EventHandler<PropertyPanelPropertyChangedEventArgs> handler,
            ProjectAddClipView addClipView)
        {
            if (clip is not null) return await BuildClipTabs(clip, handler, addClipView);
            var result = new TabbedView { Background = page.Background };
            result.TabItems.Add(new TabbedViewItem
            {
                Header = Localized.DraftPage_CenterMenuBar_AddClip,
                Content = addClipView,
                Tag = "add"
            });
            return result;
        }

        public View CurrentContent => tabbedView;

        #endregion

        #region general

        private static string ToArgbHex(Color color)
        {
            var a = (int)Math.Round(color.Alpha * 255);
            var r = (int)Math.Round(color.Red * 255);
            var g = (int)Math.Round(color.Green * 255);
            var b = (int)Math.Round(color.Blue * 255);
            return $"#{a:X2}{r:X2}{g:X2}{b:X2}";
        }

        private static Color ParseArgbOrFallback(string? value, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            try
            {
                return Color.FromArgb(value);
            }
            catch
            {
                return fallback;
            }
        }

        private View BuildColorPropertyView(string value, Color fallback, Action<object> changed, Func<string>? reset = null)
        {
            var preview = new BoxView
            {
                WidthRequest = 30,
                HeightRequest = 30,
                CornerRadius = 5,
                Color = ParseArgbOrFallback(value, fallback),
                VerticalOptions = LayoutOptions.Center
            };
            var label = new Label { Text = value, WidthRequest = 108, VerticalOptions = LayoutOptions.Center };
            bool opening = false;
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) =>
            {
                if (opening) return;
                opening = true;
                try
                {
                    var picker = new ColorPicker { SelectedColor = preview.Color };
                    picker.SelectedColorChanged += (_, color) =>
                    {
                        preview.Color = color;
                        var hex = ToArgbHex(color);
                        label.Text = hex;
                        changed(hex);
                    };
                    await page.ShowAPopup(new ScrollView
                    {
                        Content = new VerticalStackLayout
                        {
                            Spacing = 10,
                            Padding = new Thickness(10, 0),
                            Children =
                            {
                                new Button { Text = Localized._Hide, Command = new Command(async () => await page.HidePopup(true)) },
                                picker
                            }
                        }
                    }, mode: "dialog");
                }
                catch (Exception ex) { Log(ex, "Open clip color picker", this); }
                finally { opening = false; }
            };
            preview.GestureRecognizers.Add(tap);
            var layout = new HorizontalStackLayout { Spacing = 8, Children = { preview, label } };
            if (reset is not null)
            {
                var button = new Button
                {
                    Text = "\ue5d5",
                    FontFamily = "Icons",
                    WidthRequest = 40,
                    HeightRequest = 35,
                    Padding = 0,
                    VerticalOptions = LayoutOptions.Center
                };
                button.Clicked += (_, _) =>
                {
                    label.Text = reset();
                    preview.Color = ParseArgbOrFallback(label.Text, fallback);
                };
                layout.Children.Add(button);
            }
            return layout;
        }

        public View BuildGeneralTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            string currentColorHex = clip.ClipColor ?? GetDefaultColorHex(clip.ClipType);
            var ppb = new PropertyPanelBuilder();
            if (ClipInitializationFailure.IsMarked(clip.ExtraData))
            {
                var failureDescription = ClipInitializationFailure.GetDescription(clip.ExtraData);
                var failureContent = new Grid
                {
                    ColumnSpacing = 10,
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(GridLength.Auto),
                        new ColumnDefinition(GridLength.Star)
                    }
                };
                failureContent.Add(new Label
                {
                    Text = "\u26A0",
                    TextColor = Colors.Magenta,
                    FontSize = 22,
                    FontAttributes = FontAttributes.Bold,
                    VerticalOptions = LayoutOptions.Start
                }, 0, 0);
                failureContent.Add(new Label
                {
                    Text = failureDescription,
                    TextColor = Colors.OrangeRed,
                    LineBreakMode = LineBreakMode.WordWrap,
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Center
                }, 1, 0);

                var failureNotice = new Border
                {
                    Margin = new Thickness(8, 8, 8, 4),
                    Padding = new Thickness(12),
                    BackgroundColor = Color.FromArgb("#33FF1744"),
                    Stroke = Color.FromArgb("#FFFF00FF"),
                    StrokeThickness = 1,
                    StrokeShape = new RoundRectangle { CornerRadius = 8 },
                    Content = failureContent
                };
                SemanticProperties.SetDescription(failureNotice, failureDescription);
                ppb.AddCustomChild(failureNotice);
            }

            ppb.AddText(new SingleLineLabel(Localized.PropertyPanel_General, 20))
            .AddEntry("displayName", Localized.PropertyPanel_General_DisplayName, clip.DisplayName, clip.DisplayName)
            .AddCustomChild(PPLocalizedResources.General_DisplayColor,
                invoker => BuildColorPropertyView(currentColorHex, Color.FromArgb(GetDefaultColorHex(clip.ClipType)), invoker, () =>
                {
                    invoker(null!);
                    return clip.ClipColor ?? GetDefaultColorHex(clip.ClipType);
                }), "clipColor", currentColorHex);

            if (clip.ClipType == ClipMode.MarkingClip)
                ppb.AddSeparator()
                    .AddButton(PPLocalizedResources.General_Unbind, async (s, e) => await page.UnbindGroupingMarkerAsync(clip));

            ppb.PropertyChanged += (s, e) =>
            {
                switch (e.Id)
                {
                    case "clipColor":
                        clip.ClipColor = string.IsNullOrWhiteSpace(e.Value?.ToString()) ? null : e.Value?.ToString();
                        clip.ApplyClipColor();
                        break;
                    case "displayName":
                        clip.DisplayName = e.Value?.ToString() ?? clip.DisplayName;
                        break;
                    default:
                        return;
                }
                handler?.Invoke(s, e);
            };
            return ppb.BuildWithScrollView();
        }

        #endregion

        #region misc
        private static int ReadIntExtraData(Dictionary<string, object>? data, string key, int fallback)
        {
            if (data != null && data.TryGetValue(key, out var raw) && raw is not null)
            {
                if (raw is int i) return Math.Max(1, i);
                if (raw is long l) return Math.Max(1, (int)Math.Min(int.MaxValue, l));
                if (raw is JsonElement je)
                {
                    if (je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var jn)) return Math.Max(1, jn);
                    if (je.ValueKind == JsonValueKind.String && int.TryParse(je.GetString(), out var js)) return Math.Max(1, js);
                }

                if (int.TryParse(raw.ToString(), out var parsed)) return Math.Max(1, parsed);
            }

            return Math.Max(1, fallback);
        }

        private static int ReadIntValue(object? raw, int fallback)
        {
            if (raw is null)
            {
                return fallback;
            }

            if (raw is int i)
            {
                return i;
            }

            if (raw is long l)
            {
                return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
            }

            if (raw is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var parsedNumber))
                {
                    return parsedNumber;
                }

                if (je.ValueKind == JsonValueKind.String && int.TryParse(je.GetString(), out var parsedString))
                {
                    return parsedString;
                }
            }

            return int.TryParse(raw.ToString(), out var parsed) ? parsed : fallback;
        }

        private static float ReadFloatValue(object? raw, float fallback)
        {
            if (raw is null)
            {
                return fallback;
            }

            if (raw is float f)
            {
                return f;
            }

            if (raw is double d)
            {
                return (float)d;
            }

            if (raw is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.Number && je.TryGetSingle(out var parsedNumber))
                {
                    return parsedNumber;
                }

                if (je.ValueKind == JsonValueKind.String && float.TryParse(je.GetString(), out var parsedString))
                {
                    return parsedString;
                }
            }

            return float.TryParse(raw.ToString(), out var parsed) ? parsed : fallback;
        }

        private static string ReadStringValue(object? raw, string fallback)
        {
            if (raw is string s)
            {
                return s;
            }

            if (raw is JsonElement elem && elem.ValueKind == JsonValueKind.String)
            {
                return elem.GetString() ?? fallback;
            }

            return raw?.ToString() ?? fallback;
        }

        private static int ReadDictionaryIntValue(IReadOnlyDictionary<string, object>? values, string key, int fallback)
            => values != null && values.TryGetValue(key, out var raw) ? ReadIntValue(raw, fallback) : fallback;

        private static float ReadDictionaryFloatValue(IReadOnlyDictionary<string, object>? values, string key, float fallback)
            => values != null && values.TryGetValue(key, out var raw) ? ReadFloatValue(raw, fallback) : fallback;

        private static int ReadProviderFieldInt(Dictionary<string, IEffectArgumentField>? fields, string key, int fallback)
            => fields != null && fields.TryGetValue(key, out var field) && field is StaticEffectArgumentField sf ? ReadIntValue(sf.Value, fallback) : fallback;

        private static float ReadProviderFieldFloat(Dictionary<string, IEffectArgumentField>? fields, string key, float fallback)
            => fields != null && fields.TryGetValue(key, out var field) && field is StaticEffectArgumentField sf ? ReadFloatValue(sf.Value, fallback) : fallback;

        private static int ReadEffectIntParameter(IEffect effect, string key, int fallback)
        {
            ArgumentNullException.ThrowIfNull(effect);
            return ReadDictionaryIntValue(effect.Parameters, key, fallback);
        }

        private static float ReadEffectFloatParameter(IEffect effect, string key, float fallback)
        {
            ArgumentNullException.ThrowIfNull(effect);
            return ReadDictionaryFloatValue(effect.Parameters, key, fallback);
        }

        private static string ReadDictionaryStringValue(IReadOnlyDictionary<string, object>? values, string key, string fallback)
            => values != null && values.TryGetValue(key, out var raw) ? ReadStringValue(raw, fallback) : fallback;

        private static string ReadEffectStringParameter(IEffect effect, string key, string fallback)
        {
            ArgumentNullException.ThrowIfNull(effect);
            return ReadDictionaryStringValue(effect.Parameters, key, fallback);
        }

        private static bool TryGetCropSize(IEffect effect, out int width, out int height)
        {
            width = Math.Max(0, ReadEffectIntParameter(effect, "Width", 0));
            height = Math.Max(0, ReadEffectIntParameter(effect, "Height", 0));
            return width > 0 && height > 0;
        }

        private static bool IsCropEffect(IEffect effect)
            => string.Equals(effect.TypeName, "Crop", StringComparison.Ordinal);

        private static bool TryFindInternalCropEffect(ClipElementUI clip, out IEffect effect)
        {
            effect = null!;
            if (clip.Effects == null || clip.Effects.Count == 0)
            {
                return false;
            }

            if (clip.Effects.TryGetValue(InternalCropID, out var legacyCrop) && IsCropEffect(legacyCrop))
            {
                effect = legacyCrop;
                return true;
            }

            var fromProvider = clip.Effects.Values.FirstOrDefault(e =>
                IsCropEffect(e)
                && string.Equals(e.BindedEffectProvidingSystemID, InternalCropProviderGuid.ToString(), StringComparison.Ordinal));
            if (fromProvider != null)
            {
                effect = fromProvider;
                return true;
            }

            var fromName = clip.Effects.Values.FirstOrDefault(e =>
                IsCropEffect(e)
                && string.Equals(e.Name, InternalCropID, StringComparison.Ordinal));
            if (fromName != null)
            {
                effect = fromName;
                return true;
            }

            return false;
        }

        private static void RemoveInternalCropEffects(ClipElementUI clip)
        {
            if (clip.Effects == null)
            {
                return;
            }

            foreach (var key in clip.Effects
                .Where(kv => IsCropEffect(kv.Value)
                    && (string.Equals(kv.Key, InternalCropID, StringComparison.Ordinal)
                        || string.Equals(kv.Value.Name, InternalCropID, StringComparison.Ordinal)
                        || string.Equals(kv.Value.BindedEffectProvidingSystemID, InternalCropProviderGuid.ToString(), StringComparison.Ordinal)))
                .Select(kv => kv.Key)
                .ToArray())
            {
                clip.Effects.Remove(key);
            }
        }

        private static EffectImplementType ResolveConfiguredImplementType(IEffectProvider factory, EffectImplementType fallback)
        {
            var configured = EffectHelper.DefaultImplementsType.GetValueOrDefault(
                $"{factory.FromPlugin}.{factory.TypeName}",
                EffectImplementType.NotSpecified);

            if (configured != EffectImplementType.NotSpecified && factory.SupportsImplementTypes.Contains(configured))
            {
                return configured;
            }

            return fallback;
        }

        private static int ResolvePanelInt(PropertyPanelBuilder panel, string changedId, object? changedValue, string targetId, int fallback)
        {
            if (changedId == targetId && TryParseNumeric(changedValue, out var changed))
                return changed;

            if (panel.Properties.TryGetValue(targetId, out var uiValue) && TryParseNumeric(uiValue, out var parsed))
                return parsed;

            return fallback;
        }

        private static bool TryParseNumeric(object? value, out int result)
        {
            result = 0;
            if (value is double d)
            {
                result = (int)Math.Round(d);
                return true;
            }
            if (value is int i)
            {
                result = i;
                return true;
            }
            return int.TryParse(value?.ToString(), out result);
        }

        private static bool ReadBoolExtraData(Dictionary<string, object>? data, string key, bool fallback)
        {
            if (data != null && data.TryGetValue(key, out var raw) && raw is not null)
            {
                if (raw is bool b)
                {
                    return b;
                }

                if (raw is JsonElement je)
                {
                    if (je.ValueKind == JsonValueKind.True) return true;
                    if (je.ValueKind == JsonValueKind.False) return false;
                    if (je.ValueKind == JsonValueKind.String && bool.TryParse(je.GetString(), out var parsedFromJe)) return parsedFromJe;
                }

                if (bool.TryParse(raw.ToString(), out var parsed))
                {
                    return parsed;
                }
            }

            return fallback;
        }

        private static bool IsAllowFreeScaleResizeEnabled(ClipElementUI clip)
        {
            return ReadBoolExtraData(clip.ExtraData, AllowFreeScaleResizeKey, false);
        }

        public static bool TryGetSourceAspectRatio(ClipElementUI clip, ConcurrentDictionary<string, AssetItem>[] assetDict, out double aspect)
        {
            aspect = 0;

            if (clip.ClipType is not (ClipMode.VideoClip or ClipMode.PhotoClip))
            {
                return false;
            }

            if (ReadBoolExtraData(clip.ExtraData, DirectCropEnabledKey, false))
            {
                int cropW = ReadIntExtraData(clip.ExtraData, DirectCropWidthKey, clip.TargetWidth);
                int cropH = ReadIntExtraData(clip.ExtraData, DirectCropHeightKey, clip.TargetHeight);
                if (cropW > 0 && cropH > 0)
                {
                    aspect = (double)cropW / cropH;
                    return true;
                }
            }

            if (TryFindInternalCropEffect(clip, out var cropEffect))
            {
                if (TryGetCropSize(cropEffect, out var cropW, out var cropH))
                {
                    aspect = (double)cropW / cropH;
                    return aspect > 0;
                }
                return false; // If there's a crop effect, we can't reliably get the source aspect ratio, so return false to let the caller handle it.
            }

            AssetItem? asset = null;
            if (!string.IsNullOrWhiteSpace(clip.SourcePath) && clip.SourcePath.StartsWith("$"))
            {
                var assetId = clip.SourcePath.Substring(1);
                foreach (var item in assetDict)
                {
                    if (item.TryGetValue(assetId, out var byPathAsset))
                    {
                        asset = byPathAsset;
                        break;
                    }
                }
            }

            if (asset != null && asset.Width > 0 && asset.Height > 0)
            {
                aspect = (double)asset.Width / asset.Height;
                return aspect > 0;
            }
            else if (File.Exists(clip.SourcePath))
            {
                if (clip.ClipType == ClipMode.PhotoClip)
                {
                    try
                    {
                        using var img = new Picture8bpp(clip.SourcePath);
                        aspect = (double)img.Width / img.Height;
                        return aspect > 0;
                    }
                    catch { }
                }
                if (clip.ClipType == ClipMode.VideoClip)
                {
                    try
                    {
                        var vid = PluginManager.CreateVideoSource(clip.SourcePath, 8);
                        if (vid.Height != 0) aspect = (double)vid.Width / vid.Height;
                        return aspect > 0;
                    }
                    catch { }
                }
            }
            else if (asset?.Path is not null && File.Exists(asset?.Path))
            {
                if (clip.ClipType == ClipMode.PhotoClip)
                {
                    try
                    {
                        using var img = new Picture8bpp(asset.Path);
                        aspect = (double)img.Width / img.Height;
                        return aspect > 0;
                    }
                    catch { }
                }
                if (clip.ClipType == ClipMode.VideoClip)
                {
                    try
                    {
                        var vid = PluginManager.CreateVideoSource(asset.Path, 8);
                        if (vid.Height != 0) aspect = (double)vid.Width / vid.Height;
                        return aspect > 0;
                    }
                    catch { }
                }
            }

            return false;
        }

        private class DummyEffectProvider : IEffectProvider
        {
            public bool IsDeterministic => false;

            public Guid Id { get; set; }
            public string TypeName => "Dummy";
            public string Name { get => "Dummy Effect Provider"; set { } }
            public Dictionary<string, object> MetaData { get; set; } = new Dictionary<string, object>();
            public bool Enabled { get; set; } = true;
            public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
            public EffectType TypeOfEffect => EffectType.NotSpecified;
            public EffectTarget Target => EffectTarget.Video;
            public IReadOnlyList<Stage> SupportedStages => [Stage.Picture];
            public Stage Stage { get; set; } = Stage.Picture;

            public IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> InFields => new Dictionary<string, EffectArgumentFieldDescriptor>();
            public EffectArgumentFieldDescriptor OutField => new EffectArgumentFieldDescriptor
            {
                Id = EffectProviderAnchorExtensions.OutputKey,
                TypeName = "IPicture",
                FieldType = EffectArgumentFieldType.IPicture,
            };
            public Dictionary<string, string> AnchorsBindingState { get; set; } = new();
            public Dictionary<string, IEffectArgumentField> Fields { get; set; } = new();

            public IEffect[] Build() => throw new NotImplementedException();

            public IEffect RestoreInstance(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
            {
                throw new NotImplementedException();
            }
        }

        private class ClipRangeSlider : ContentView
        {
            public double Maximum { get; set; }
            private double _lowerValue;
            public double LowerValue
            {
                get => _lowerValue;
                set { _lowerValue = value; UpdateLayout(); }
            }

            private double _upperValue;
            public double UpperValue
            {
                get => _upperValue;
                set { _upperValue = value; UpdateLayout(); }
            }

            private string? _thumbnailPath;
            public string? ThumbnailPath
            {
                get => _thumbnailPath;
                set { _thumbnailPath = value; RebuildThumbnails(); }
            }

            public event EventHandler ValuesChanged;
            public event EventHandler DragCompleted;

            AbsoluteLayout _layout;
            Border _track;
            HorizontalStackLayout _thumbnailLayout;
            Border _leftMask;
            Border _rightMask;
            Border _leftThumb;
            Border _rightThumb;
            Border _middleRegion;
            double _trackWidth;

            public ClipRangeSlider()
            {
                HeightRequest = 60;
                MinimumWidthRequest = 100;
                _layout = new AbsoluteLayout();

                _track = new Border { BackgroundColor = Color.FromArgb("#888888"), StrokeShape = new RoundRectangle { CornerRadius = 8 }, StrokeThickness = 0 };
                _thumbnailLayout = new HorizontalStackLayout { Spacing = 0 };
                _track.Content = _thumbnailLayout;

                _leftMask = new Border { BackgroundColor = Color.FromRgba(0, 0, 0, 150), StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(8, 0, 8, 0) }, StrokeThickness = 0, InputTransparent = true };
                _rightMask = new Border { BackgroundColor = Color.FromRgba(0, 0, 0, 150), StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(0, 8, 0, 8) }, StrokeThickness = 0, InputTransparent = true };

                _middleRegion = new Border { BackgroundColor = Colors.Transparent, StrokeThickness = 0 };

                _leftThumb = CreateThumb();
                _rightThumb = CreateThumb();

                _layout.Children.Add(_track);
                _layout.Children.Add(_middleRegion);
                _layout.Children.Add(_leftMask);
                _layout.Children.Add(_rightMask);
                _layout.Children.Add(_leftThumb);
                _layout.Children.Add(_rightThumb);

                Content = _layout;

                SizeChanged += (s, e) => { _trackWidth = Width; RebuildThumbnails(); UpdateLayout(); };

                AddPanGesture(_leftThumb, 0);
                AddPanGesture(_rightThumb, 1);
                AddPanGesture(_middleRegion, 2);
            }

            void RebuildThumbnails()
            {
                _thumbnailLayout.Children.Clear();
                if (string.IsNullOrEmpty(_thumbnailPath) || _trackWidth <= 0) return;

                int n = (int)Math.Ceiling(_trackWidth / 40.0) + 1;
                for (int i = 0; i < n; i++)
                {
                    _thumbnailLayout.Children.Add(new Image { Source = _thumbnailPath, Aspect = Aspect.AspectFill, HeightRequest = 40, WidthRequest = 40 });
                }
            }

            Border CreateThumb()
            {
                return new Border
                {
                    WidthRequest = 16,
                    HeightRequest = 60,
                    BackgroundColor = Colors.Transparent,
                    Stroke = Colors.White,
                    StrokeThickness = 2,
                    StrokeShape = new RoundRectangle { CornerRadius = 2 }
                };
            }

            void AddPanGesture(View thumb, int type)
            {
                var pan = new PanGestureRecognizer();
                double initialLowerValue = 0;
                double initialUpperValue = 0;
                pan.PanUpdated += (s, e) =>
                {
                    if (e.StatusType == GestureStatus.Started)
                    {
                        initialLowerValue = _lowerValue;
                        initialUpperValue = _upperValue;
                    }
                    else if (e.StatusType == GestureStatus.Running)
                    {
                        double deltaVal = (e.TotalX / _trackWidth) * Maximum;

                        if (type == 0) // Min
                        {
                            _lowerValue = Math.Clamp(initialLowerValue + deltaVal, 0, _upperValue - 1);
                        }
                        else if (type == 1) // Max
                        {
                            _upperValue = Math.Clamp(initialUpperValue + deltaVal, _lowerValue + 1, Maximum);
                        }
                        else if (type == 2) // Middle
                        {
                            double length = initialUpperValue - initialLowerValue;
                            double newLower = Math.Clamp(initialLowerValue + deltaVal, 0, Maximum - length);
                            _lowerValue = newLower;
                            _upperValue = newLower + length;
                        }
                        UpdateLayout();
                        ValuesChanged?.Invoke(this, EventArgs.Empty);
                    }
                    else if (e.StatusType == GestureStatus.Completed || e.StatusType == GestureStatus.Canceled)
                    {
                        DragCompleted?.Invoke(this, EventArgs.Empty);
                    }
                };
                thumb.GestureRecognizers.Add(pan);
            }

            void UpdateLayout()
            {
                if (_trackWidth <= 0 || Maximum <= 0) return;

                double minX = (_lowerValue / Maximum) * _trackWidth;
                double maxX = (_upperValue / Maximum) * _trackWidth;

                AbsoluteLayout.SetLayoutBounds(_track, new Rect(0, 10, _trackWidth, 40));

                AbsoluteLayout.SetLayoutBounds(_leftMask, new Rect(0, 10, minX, 40));
                AbsoluteLayout.SetLayoutBounds(_rightMask, new Rect(maxX, 10, Math.Max(0, _trackWidth - maxX), 40));

                AbsoluteLayout.SetLayoutBounds(_middleRegion, new Rect(minX, 10, Math.Max(0, maxX - minX), 40));

                AbsoluteLayout.SetLayoutBounds(_leftThumb, new Rect(minX - 8, 0, 16, 60));
                AbsoluteLayout.SetLayoutBounds(_rightThumb, new Rect(maxX - 8, 0, 16, 60));
            }
        }

        #endregion
    }
}
