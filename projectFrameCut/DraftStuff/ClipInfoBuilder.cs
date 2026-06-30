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
using projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;
using projectFrameCut.ApplicationAPIBase.Views.Pickers;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationAPIBase.Views.TabbedView;
using projectFrameCut.ApplicationPluginBase.DynamicPreviewProvider;
using projectFrameCut.ApplicationPluginBase.Effect;
using projectFrameCut.Asset;
using projectFrameCut.Controls;
using projectFrameCut.Converters;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.InteractableEditor;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
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

#pragma warning disable CS0618 // We need the old TextClipEntry for compatibility with old projects, so we will keep it for now.

namespace projectFrameCut.DraftStuff
{
    public class ClipInfoBuilder
    {
        #region id const
        private const string InternalRotationID = "__Internal_Rotation__";
        private const string InternalCropID = "__Internal_Crop__";
        private static readonly Guid InternalCropBundleGuid = new("a3a744cc-53b7-4d5e-8dd5-4c66077d9401");
        private static readonly Guid InternalColorAdjustmentBundleGuid = new("dc3cfef8-1782-4428-8862-f9a0995c02d9");
        private const string SolidColorOutputWidthKey = "SolidColorOutputWidth";
        private const string SolidColorOutputHeightKey = "SolidColorOutputHeight";
        private const string SolidColorUseFixedOutputSizeKey = "SolidColorUseFixedOutputSize";
        private const string AllowFreeScaleResizeKey = "AllowFreeScaleResize";
        private const string TextStyleProviderFromKey = "TextStyleProvider_FromPlugin";
        private const string TextStyleProviderTypeKey = "TextStyleProvider_TypeName";
        private const string TextStyleProviderParamsKey = "TextStyleProvider_Parameters";
        #endregion

        #region init
        DraftPage page;
        TabbedView tabbedView = new();

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
                _ => Colors.Gray.ToArgbHex(),
            };
        }


        public ClipInfoBuilder(DraftPage page)
        {
            this.page = page;
            PPLocalizedResources = ISimpleLocalizerBase_PropertyPanel.GetMapping().TryGetValue(Localized._LocaleId_, out var pploc) ? pploc : ISimpleLocalizerBase_PropertyPanel.GetMapping().First().Value;
        }


        public async Task<TabbedView> Build(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            tabbedView = new();
            tabbedView.Background = page.Background;
            tabbedView.TabItems.Add(new TabbedViewItem
            {
                Header = Localized.MainSettingsPage_Tab_General,
                Content = BuildGeneralTab(clip, handler),
                Tag = "general"
            });
            if (clip.ClipType == ClipMode.TextClip || clip.ClipType == ClipMode.SubtitleClip)
            {
                tabbedView.TabItems.Add(new TabbedViewItem
                {
                    Header = PPLocalizedResources.TextOption_TabTitle,
                    LazyAsyncContentFactory = () => BuildTextOptionTab(clip, handler),
                    Tag = "text"
                });
            }
            if (clip.isInfiniteLength || (clip.LeftHandle?.IsVisible == true && clip.RightHandle?.IsVisible == true))
            {
                tabbedView.TabItems.Add(new TabbedViewItem
                {
                    Header = PPLocalizedResources.Tabs_Timing,
                    LazyContentFactory = () => BuildTimingTab(clip, handler),
                    Tag = "timing"
                });
            }
            if (clip.ClipType == ClipMode.VideoClip || clip.ClipType == ClipMode.PhotoClip)
            {
                tabbedView.TabItems.Add(new TabbedViewItem
                {
                    Header = PPLocalizedResources.Tabs_SizeAndPosition,
                    LazyContentFactory = () => BuildSizeAndPositionTab(clip, handler),
                    Tag = "sizeAndPosition"
                });

            }
            if (clip.ClipType != ClipMode.MarkingClip)
            {
                tabbedView.TabItems.Add(new TabbedViewItem
                {
                    Header = Localized.InteractableEditor_KeyFrame,
                    LazyContentFactory = () => BuildKeyFrameTab(clip, handler),
                    Tag = "keyframe"
                });
                tabbedView.TabItems.Add(new TabbedViewItem
                {
                    Header = PPLocalizedResources.Tabs_Effect,
                    LazyAsyncContentFactory = () => BuildEffectTab(clip, handler),
                    Tag = "effect"
                });
                tabbedView.TabItems.Add(new TabbedViewItem
                {
                    Header = PPLocalizedResources.Tabs_Mixture,
                    LazyContentFactory = () => BuildMixtureTab(clip, handler),
                    Tag = "mixture"
                });
                if (clip.ClipType != ClipMode.AudioClip)
                {
                    tabbedView.TabItems.Add(new TabbedViewItem
                    {
                        Header = PPLocalizedResources.Tabs_ColorAdjust,
                        LazyContentFactory = () => BuildColorAdjustmentTab(clip, handler),
                        Tag = "colorAdjust"
                    });
                }
                if (!clip.isInfiniteLength)
                {
                    tabbedView.TabItems.Add(new TabbedViewItem
                    {
                        Header = PPLocalizedResources.Tabs_SpeedRatio,
                        LazyContentFactory = () => BuildSpeedAndRatioTab(clip, handler),
                        Tag = "speedAndRatio"
                    });
                }
                if (SettingsManager.IsBoolSettingTrue("edit_ShowAllEffects"))
                {
                    tabbedView.TabItems.Add(new TabbedViewItem
                    {
                        Header = PPLocalizedResources.Tabs_Effect_Classic,
                        LazyContentFactory = () => BuildClassicEffectTab(clip, handler),
                        Tag = "effectClassic"
                    });
                    if (clip.ClipType == ClipMode.TextClip || clip.ClipType == ClipMode.SubtitleClip)
                    {
                        tabbedView.TabItems.Add(new TabbedViewItem
                        {
                            Header = PPLocalizedResources.TextOption_TabTitle_Classic,
                            LazyContentFactory = () => BuildTextOptionClassicTab(clip, handler),
                            Tag = "textClassic"
                        });
                    }
                }
            }

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

        public View CurrentContent => tabbedView;

        #endregion

        #region general

        public View BuildGeneralTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            string currentColorHex = clip.ClipColor ?? GetDefaultColorHex(clip.ClipType);
            IClip? TargetInstance = null;
            VideoClip? TargetVideoClip = null;
            try
            {
                TargetInstance = DraftImportAndExportHelper.JSONToIClips(new DraftStructureJSON { Clips = [DraftImportAndExportHelper.ExportClipElementFromDraftPage(page, clip)] }, true, 8).FirstOrDefault();
                TargetVideoClip = TargetInstance as VideoClip;
            }
            catch
            {

            }
            string ToArgbHex(Color color)
            {
                var a = (int)Math.Round(color.Alpha * 255);
                var r = (int)Math.Round(color.Red * 255);
                var g = (int)Math.Round(color.Green * 255);
                var b = (int)Math.Round(color.Blue * 255);
                return $"#{a:X2}{r:X2}{g:X2}{b:X2}";
            }

            Color ParseArgbOrFallback(string? value, Color fallback)
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

            object? GetSolidColorRawValue(string key)
            {
                if (clip.ExtraData == null)
                {
                    return null;
                }

                if (clip.ExtraData.TryGetValue(key, out var value))
                {
                    return value;
                }

                return clip.ExtraData.TryGetValue(key.ToLowerInvariant(), out var lowerValue)
                    ? lowerValue
                    : null;
            }

            Color ResolveSolidColorFromExtraData()
            {
                int r16 = Math.Clamp(ReadIntValue(GetSolidColorRawValue("R"), ushort.MaxValue), ushort.MinValue, ushort.MaxValue);
                int g16 = Math.Clamp(ReadIntValue(GetSolidColorRawValue("G"), ushort.MaxValue), ushort.MinValue, ushort.MaxValue);
                int b16 = Math.Clamp(ReadIntValue(GetSolidColorRawValue("B"), ushort.MaxValue), ushort.MinValue, ushort.MaxValue);
                float a = Math.Clamp(ReadFloatValue(GetSolidColorRawValue("A"), 1f), 0f, 1f);

                return Color.FromRgba(r16 / 65535.0, g16 / 65535.0, b16 / 65535.0, a);
            }

            void SaveSolidColorToExtraData(Color color)
            {
                clip.ExtraData ??= new Dictionary<string, object>();
                clip.ExtraData["R"] = (ushort)Math.Clamp((int)Math.Round(color.Red * ushort.MaxValue), ushort.MinValue, ushort.MaxValue);
                clip.ExtraData["G"] = (ushort)Math.Clamp((int)Math.Round(color.Green * ushort.MaxValue), ushort.MinValue, ushort.MaxValue);
                clip.ExtraData["B"] = (ushort)Math.Clamp((int)Math.Round(color.Blue * ushort.MaxValue), ushort.MinValue, ushort.MaxValue);
                clip.ExtraData["A"] = (float)Math.Clamp(color.Alpha, 0f, 1f);
            }

            object? GetVideoDecoderRawValue()
            {
                if (clip.ExtraData == null)
                {
                    return null;
                }

                if (clip.ExtraData.TryGetValue("TargetDecoder", out var value))
                {
                    return value;
                }

                return clip.ExtraData.TryGetValue("targetdecoder", out var lowerValue)
                    ? lowerValue
                    : null;
            }

            string ReadVideoDecoderId()
            {
                var raw = GetVideoDecoderRawValue();
                if (raw is JsonElement je)
                {
                    if (je.ValueKind == JsonValueKind.String)
                    {
                        return je.GetString() ?? "auto";
                    }

                    return je.ToString();
                }

                return raw?.ToString() ?? "auto";
            }

            string currentSolidColorHex = clip.ClipType == ClipMode.SolidColorClip
                ? ToArgbHex(ResolveSolidColorFromExtraData())
                : "#FFFFFFFF";

            int valX = 0, valY = 0;
            int valW = page.ProjectInfo.RelativeWidth;
            int valH = page.ProjectInfo.RelativeHeight;
            valX = clip.TargetX;
            valY = clip.TargetY;
            if (clip.TargetWidth > 0) valW = clip.TargetWidth;
            if (clip.TargetHeight > 0) valH = clip.TargetHeight;

            if (clip.ClipType == ClipMode.SolidColorClip)
            {
                if (clip.TargetWidth > 0)
                {
                    valW = clip.TargetWidth;
                }
                else
                {
                    valW = ReadIntExtraData(clip.ExtraData, SolidColorOutputWidthKey, valW);
                }

                if (clip.TargetHeight > 0)
                {
                    valH = clip.TargetHeight;
                }
                else
                {
                    valH = ReadIntExtraData(clip.ExtraData, SolidColorOutputHeightKey, valH);
                }
            }

            string currentVideoDecoderId = ReadVideoDecoderId();
            if (string.IsNullOrWhiteSpace(currentVideoDecoderId))
            {
                currentVideoDecoderId = "auto";
            }

            var videoDecoderOptionLabelToId = new Dictionary<string, string>
            {
                [PPLocalizedResources.General_VideoCodec_TargetMode_Auto] = "auto",
                [PPLocalizedResources.General_VideoCodec_TargetMode_8bpp] = "DecoderContext8Bit",
                [PPLocalizedResources.General_VideoCodec_TargetMode_8bppHWaccel] = "DecoderContextHW",
                [PPLocalizedResources.General_VideoCodec_TargetMode_16bpp] = "DecoderContext16Bit",
                [PPLocalizedResources.General_VideoCodec_TargetMode_hdr] = "HDRDecoderContext",
            };
            var allVideoDecoderOptionLabelToId = new Dictionary<string, string>
            {
                [PPLocalizedResources.General_VideoCodec_TargetMode_Auto] = "auto",
                [PPLocalizedResources.General_VideoCodec_TargetMode_8bpp] = "DecoderContext8Bit",
                [PPLocalizedResources.General_VideoCodec_TargetMode_8bppHWaccel] = "DecoderContextHW",
                [PPLocalizedResources.General_VideoCodec_TargetMode_16bpp] = "DecoderContext16Bit",
                [PPLocalizedResources.General_VideoCodec_TargetMode_hdr] = "HDRDecoderContext",
                [PPLocalizedResources.General_VideoCodec_TargetMode_http] = "HttpDecoderContext",
                [PPLocalizedResources.General_VideoCodec_TargetMode_rpsv] = "RawPictureSequenceStreamVideoDecoderContext",
                [PPLocalizedResources.General_VideoCodec_TargetMode_ffmpegDevices] = "FFmpegDeviceDecoderContext",
            };

            if (!videoDecoderOptionLabelToId.Values.Contains(currentVideoDecoderId, StringComparer.Ordinal))
            {
                videoDecoderOptionLabelToId[PPLocalizedResources.General_VideoCodec_TargetMode_Unknown(currentVideoDecoderId)] = currentVideoDecoderId;
            }

            string selectedVideoDecoderLabel = videoDecoderOptionLabelToId
                .FirstOrDefault(kv => string.Equals(kv.Value, currentVideoDecoderId, StringComparison.Ordinal)).Key
                ?? PPLocalizedResources.General_VideoCodec_TargetMode_Auto;

            var ppb = new PropertyPanelBuilder()
            .AddText(new SingleLineLabel(Localized.PropertyPanel_General, 20))
            .AddEntry("displayName", Localized.PropertyPanel_General_DisplayName, clip.DisplayName, clip.DisplayName)
            .AddCustomChild(PPLocalizedResources.General_DisplayColor, (invoker) =>
            {
                var colorPreview = new BoxView
                {
                    WidthRequest = 30,
                    HeightRequest = 30,
                    CornerRadius = 5,
                    Color = ParseArgbOrFallback(currentColorHex, Color.FromArgb(GetDefaultColorHex(clip.ClipType))),
                    VerticalOptions = LayoutOptions.Center,
                    HorizontalOptions = LayoutOptions.Start
                };

                var colorHexLabel = new Label
                {
                    Text = currentColorHex,
                    WidthRequest = 108,
                    VerticalOptions = LayoutOptions.Center,
                    VerticalTextAlignment = TextAlignment.Center
                };

                bool isOpeningColorPicker = false;
                var openPickerTap = new TapGestureRecognizer();
                openPickerTap.Tapped += async (s, e) =>
                {
                    if (isOpeningColorPicker)
                    {
                        return;
                    }

                    isOpeningColorPicker = true;
                    try
                    {
                        var picker = new ColorPicker
                        {
                            SelectedColor = colorPreview.Color
                        };

                        picker.SelectedColorChanged += (sender, selectedColor) =>
                        {
                            var hex = ToArgbHex(selectedColor);
                            colorPreview.Color = selectedColor;
                            colorHexLabel.Text = hex;
                            invoker(hex);
                        };

                        var popupView = new VerticalStackLayout
                        {
                            Spacing = 10,
                            Padding = new Thickness(10, 0),
                            Children =
                            {
                                new Button
                                {
                                    Text = Localized._Hide,
                                    Command = new Command(async () => await page.HidePopup(true))
                                },
                                picker,

                            }
                        };

                        await page.ShowAPopup(new ScrollView { Content = popupView }, mode: "dialog");
                    }
                    catch
                    {
                    }
                    finally
                    {
                        isOpeningColorPicker = false;
                    }
                };
                colorPreview.GestureRecognizers.Add(openPickerTap);

                var resetButton = new Button
                {
                    Text = "\ue5d5",
                    FontFamily = "Icons",
                    WidthRequest = 40,
                    HeightRequest = 35,
                    Padding = 0,
                    VerticalOptions = LayoutOptions.Center
                };
                resetButton.Clicked += (s, e) =>
                {
                    invoker(null!); // Triggers random color generation via ApplyClipColor()
                    var newColor = clip.ClipColor ?? GetDefaultColorHex(clip.ClipType);
                    colorHexLabel.Text = newColor;
                    colorPreview.Color = Color.FromArgb(newColor);
                };

                var layout = new HorizontalStackLayout
                {
                    Spacing = 8,
                    Children = { colorPreview, colorHexLabel, resetButton }
                };

                return layout;
            }, "clipColor", currentColorHex)
            .AddSeparator(null)
            .AppendWhen(clip.ClipType == ClipMode.SolidColorClip,
            (c) =>
                c.AddText(new SingleLineLabel(PPLocalizedResources.General_SolidColor, 20))
                .AddCustomChild(PPLocalizedResources.General_Color, (invoker) =>
                {
                    var colorPreview = new BoxView
                    {
                        WidthRequest = 30,
                        HeightRequest = 30,
                        CornerRadius = 5,
                        Color = ParseArgbOrFallback(currentSolidColorHex, Colors.White),
                        VerticalOptions = LayoutOptions.Center,
                        HorizontalOptions = LayoutOptions.Start
                    };

                    var colorHexLabel = new Label
                    {
                        Text = currentSolidColorHex,
                        WidthRequest = 108,
                        VerticalOptions = LayoutOptions.Center,
                        VerticalTextAlignment = TextAlignment.Center
                    };

                    bool isOpeningColorPicker = false;
                    var openPickerTap = new TapGestureRecognizer();
                    openPickerTap.Tapped += async (s, e) =>
                    {
                        if (isOpeningColorPicker)
                        {
                            return;
                        }

                        isOpeningColorPicker = true;
                        try
                        {
                            var picker = new ColorPicker
                            {
                                SelectedColor = colorPreview.Color
                            };

                            picker.SelectedColorChanged += (sender, selectedColor) =>
                            {
                                var hex = ToArgbHex(selectedColor);
                                colorPreview.Color = selectedColor;
                                colorHexLabel.Text = hex;
                                invoker(hex);
                            };

                            var popupView = new VerticalStackLayout
                            {
                                Spacing = 10,
                                Padding = new Thickness(10, 0),
                                Children =
                                {
                                    new Button
                                    {
                                        Text = Localized._Hide,
                                        Command = new Command(async () => await page.HidePopup(true))
                                    },
                                    picker,

                                }
                            };

                            await page.ShowAPopup(new ScrollView { Content = popupView }, mode: "dialog");
                        }
                        catch
                        {
                        }
                        finally
                        {
                            isOpeningColorPicker = false;
                        }
                    };
                    colorPreview.GestureRecognizers.Add(openPickerTap);

                    var layout = new HorizontalStackLayout
                    {
                        Spacing = 8,
                        Children = { colorPreview, colorHexLabel }
                    };

                    return layout;
                }, "solidColor", currentSolidColorHex)
                .AddPositionTupleInputBox("place", new SingleLineLabel(PPLocalizedResources.General_LocationAndSize, 20), PositionTupleMode.XYWH, (valX, valY, valW, valH), entryWidth: 70)
                .AddSlider("rotationDeg", PPLocalizedResources.General_Rotation, 0, 360, 0))
            .AppendWhen(clip.ClipType == ClipMode.AudioClip,
            (c) =>
                c.AddText(new SingleLineLabel(PPLocalizedResources.General_Audio, 20))
                 .AddSlider("volume", PPLocalizedResources.General_Audio_Volume, clip.ExtraData.TryGetValue("Volume", out var volume) ? (double)volume : 1d, 0, 1))
            .AppendWhen(clip.ClipType == ClipMode.VideoClip,
            (c) =>
                c.AddText(new SingleLineLabel(PPLocalizedResources.General_VideoCodec, 20))
                 .AppendWhen(!(TargetInstance?.FilePath?.StartsWith("#") ?? false),
                     cc => cc.AddPicker(
                             "videoTargetDecoderMode",
                             PPLocalizedResources.General_VideoCodec_TargetMode,
                             videoDecoderOptionLabelToId.Keys.ToArray(),
                             selectedVideoDecoderLabel)
                             .AppendWhen((TargetVideoClip is not null && TargetVideoClip?.Decoder?.GetType() == typeof(HDRDecoderContext)),
                                cc1 => cc1.AddSlider("hdrBrightnessOffset", PPLocalizedResources.General_VideoCodec_HDRBrightnessOffset, -1, 1, TargetVideoClip?.HDRBrightnessOffset ?? 0, eventCallMode: SliderUpdateEventCallMode.OnMouseUp)),
                      cc => cc.AddCustomChild(PPLocalizedResources.General_VideoCodec_TargetMode, new Label { Text = allVideoDecoderOptionLabelToId.ReverseLookup(TargetVideoClip?.DecoderName ?? "Unknown", PPLocalizedResources.General_VideoCodec_TargetMode_Unknown(TargetVideoClip?.DecoderName ?? "Unknown")) }))
                 .AppendWhen(
                    clip is not null && !string.IsNullOrWhiteSpace(clip.SourcePath),
                        pp => pp.AppendWhen(clip.SourcePath.StartsWith("$"),
                            pp1 => pp1.AddCustomChild(PPLocalizedResources.General_VideoCodec_Source, new Label
                            {
                                Text = AssetDatabase.Assets.TryGetValue(clip.SourcePath.Substring(1), out var asset) ? $"{Localized.DraftPage_CenterMenuBar_Asset}: {asset.Name}({asset.Path})" : $"Unknown asset: {clip.SourcePath.Substring(1)}"
                            }),
                            pp1 => pp1.AddCustomChild(PPLocalizedResources.General_VideoCodec_Source, new Label { Text = System.IO.Path.GetFullPath(clip.SourcePath) })),
                    pp => pp.AddCustomChild(PPLocalizedResources.General_VideoCodec_Source, new Label { Text = "Unknown" })
                )
            .AppendWhen(clip.ClipType == ClipMode.MarkingClip,
                c => c.AddButton(PPLocalizedResources.General_Unbind, async (s, e) => await page.UnbindGroupingMarkerAsync(clip))))
            .AppendWhen(TargetInstance is IVectorContentClip vc,
                c =>
                {
                    string currentVectorAaLabel = PPLocalizedResources.General_VectorClip_AAMode_None;
                    if (clip.ExtraData is not null && clip.ExtraData.TryGetValue("VectorAntiAliasMode", out var aaObj) && aaObj is string aaStr)
                    {
                        currentVectorAaLabel = aaStr switch
                        {
                            "None" => PPLocalizedResources.General_VectorClip_AAMode_None,
                            "SSAA2x" => "SSAA 2x",
                            "SSAA4x" => "SSAA 4x",
                            "SSAA8x" => "SSAA 8x",
                            _ => PPLocalizedResources.General_VectorClip_AAMode_None
                        };
                    }

                    c.AddText(new SingleLineLabel(PPLocalizedResources.General_VectorClip, 20))
                     .AddPicker("vectorAntiAliasMode", PPLocalizedResources.General_VectorClip_AAMode, new[] { PPLocalizedResources.General_VectorClip_AAMode_Default, PPLocalizedResources.General_VectorClip_AAMode_None, "SSAA 2x", "SSAA 4x", "SSAA 8x" }, currentVectorAaLabel);
                })
            .AppendWhen(clip.ClipType == ClipMode.VectorCanvasClip,
                c =>
                {
                    c.AddButton("Open Vector Editor", async (s, e) =>
                    {
                        if (TargetInstance is not VectorCanvasClip vecClip)
                        {
                            page.SetStateFail("Target clip is invalid.");
                            return;
                        }
                        // Ensure the clip is initialised (SourcePicture may be null
                        // in composition-only mode, which is valid).
                        if (vecClip.SourcePicture is null && vecClip.Components.Count == 0 && vecClip.FilePath is not null)
                            vecClip.ReInit(default);

                        var editor = new StoryboardEditorView(vecClip, page.ProjectInfo.RelativeWidth, page.ProjectInfo.RelativeHeight);
                        var v = new ApplicationAPIBase.Views.MultiWindowView.MultiWindowItem
                        {
                            Title = PPLocalizedResources.EffectBindView_Title(clip.DisplayName),
                            Content = editor,
                            IsPopOutVisible = true
                        };
                        editor.ChangesApplied += (d) =>
                        {
                            clip.ExtraData = d;
                            handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("Storyboard", null, null));
                            page.MainMultiWindowView.CloseWindow(v);
                        };
                        editor.ChangesCancelled += (s, e) =>
                        {
                            page.MainMultiWindowView.CloseWindow(v);
                        };
                        page.MainMultiWindowView.AddWindow(v);
                    });
                });


            ppb.PropertyChanged += async (s, e) =>
            {
                clip.Effects ??= new Dictionary<string, IEffect>();
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
                if (e.Id == "solidColor" && clip.ClipType == ClipMode.SolidColorClip)
                {
                    var selectedColor = ParseArgbOrFallback(e.Value?.ToString(), Colors.White);
                    SaveSolidColorToExtraData(selectedColor);
                    handler?.Invoke(s, e);
                    return;
                }
                if (e.Id == "videoTargetDecoderMode" && clip.ClipType == ClipMode.VideoClip)
                {
                    var selectedLabel = e.Value?.ToString() ?? string.Empty;
                    if (!videoDecoderOptionLabelToId.TryGetValue(selectedLabel, out var selectedDecoderId))
                    {
                        selectedDecoderId = currentVideoDecoderId;
                    }

                    clip.ExtraData ??= new Dictionary<string, object>();
                    clip.ExtraData["TargetDecoder"] = selectedDecoderId;
                    currentVideoDecoderId = selectedDecoderId;

                    handler?.Invoke(s, e);
                    return;
                }
                if (e.Id == "hdrBrightnessOffset" && clip.ClipType == ClipMode.VideoClip)
                {
                    var offset = Convert.ToDouble(e?.Value ?? 1d);
                    clip.ExtraData ??= new Dictionary<string, object>();
                    clip.ExtraData["HDRBrightnessOffset"] = offset;
                    handler?.Invoke(s, e);
                    return;
                }
                if (e.Id.StartsWith("place_"))
                {
                    clip.Effects ??= new Dictionary<string, IEffect>();

                    switch (e.Id)
                    {
                        case "place_X":
                            clip.TargetX = (int)Math.Round(Convert.ToDouble(e.Value));
                            break;
                        case "place_Y":
                            clip.TargetY = (int)Math.Round(Convert.ToDouble(e.Value));
                            break;
                        case "place_W":
                            {
                                int w = Math.Max(1, (int)Math.Round(Convert.ToDouble(e.Value)));
                                if (clip.ClipType == ClipMode.SolidColorClip)
                                {
                                    clip.TargetWidth = w;
                                    clip.ExtraData ??= new Dictionary<string, object>();
                                    clip.ExtraData[SolidColorOutputWidthKey] = w;
                                    clip.ExtraData[SolidColorUseFixedOutputSizeKey] = true;
                                }
                                else
                                {
                                    clip.TargetWidth = w;
                                }
                                break;
                            }
                        case "place_H":
                            {
                                int h = Math.Max(1, (int)Math.Round(Convert.ToDouble(e.Value)));
                                if (clip.ClipType == ClipMode.SolidColorClip)
                                {
                                    clip.TargetHeight = h;
                                    clip.ExtraData ??= new Dictionary<string, object>();
                                    clip.ExtraData[SolidColorOutputHeightKey] = h;
                                    clip.ExtraData[SolidColorUseFixedOutputSizeKey] = true;
                                }
                                else
                                {
                                    clip.TargetHeight = h;
                                }
                                break;
                            }
                    }

                    handler?.Invoke(s, e);
                    return;
                }
                if (e.Id == "rotationDeg")
                {
                    if (e.Value is double deg)
                    {
                        var newR = new RotationEffect_IPicture
                        {
                            Angle = (float)deg,
                            Enabled = true,
                            Name = InternalRotationID,
                            Index = int.MinValue + 100,
                            RelativeWidth = page.ProjectInfo.RelativeWidth,
                            RelativeHeight = page.ProjectInfo.RelativeHeight,
                            ExpandCanvas = false
                        };
                        clip.Effects[InternalRotationID] = newR;
                    }

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

                if (e.Id == "vectorAntiAliasMode")
                {
                    clip.ExtraData ??= new Dictionary<string, object>();
                    clip.ExtraData["VectorAntiAliasMode"] = (e.Value?.ToString()) switch
                    {
                        var t when t == PPLocalizedResources.General_VectorClip_AAMode_None => "None",
                        "SSAA 2x" => "SSAA2x",
                        "SSAA 4x" => "SSAA4x",
                        "SSAA 8x" => "SSAA8x",
                        _ => ""
                    };
                    handler?.Invoke(s, e);
                    return;
                }

                switch (e.Id)
                {
                    case "displayName":
                        clip.DisplayName = e.Value?.ToString() ?? clip.DisplayName;
                        break;
                    case "volume":
                        {
                            if (e.Value is double vol || double.TryParse(e.Value as string, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out vol))
                            {
                                clip.ExtraData ??= new Dictionary<string, object>();
                                clip.ExtraData["Volume"] = vol;
                            }
                            break;
                        }
                    //case "speedRatio":
                    //    {
                    //        if (e.Value is double ratio || double.TryParse(e.Value as string, out ratio))
                    //        {
                    //            if (ratio != 0f)
                    //                clip.SecondPerFrameRatio = (float)ratio;
                    //        }

                    //        break;
                    //    }
                    default:
                        {

                            break;
                        }
                }

                handler?.Invoke(s, e);
            };
            return ppb.BuildWithScrollView();
        }

        #endregion

        #region size and pos

        public View BuildSizeAndPositionTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            clip.Effects ??= new Dictionary<string, IEffect>();

            int valX = 0, valY = 0;
            int valW = page.ProjectInfo.RelativeWidth;
            int valH = page.ProjectInfo.RelativeHeight;
            double rotationDeg = 0;
            bool allowFreeScaleResize = IsAllowFreeScaleResizeEnabled(clip);
            valX = clip.TargetX;
            valY = clip.TargetY;
            if (clip.TargetWidth > 0) valW = clip.TargetWidth;
            if (clip.TargetHeight > 0) valH = clip.TargetHeight;

            if (clip.Effects.TryGetValue(InternalRotationID, out var rotEff) && rotEff is RotationEffect_IPicture rot)
            {
                rotationDeg = rot.Angle;
            }

            IEffectBundle BuildDefaultCropBundle()
            {
                if (!EffectServices.GetAvailableEffectBundles().TryGetValue("Crop", out var cropBundleFactory))
                {
                    throw new KeyNotFoundException("Crop effect bundle factory not found.");
                }

                var bundle = cropBundleFactory();
                bundle.Id = InternalCropBundleGuid;
                bundle.Name = InternalCropID;
                bundle.Enabled = false;
                bundle.BindedInputId = IEffectBundle.InputAnchorGUID;
                bundle.BindedOutputId = IEffectBundle.OutputAnchorGUID;
                bundle.Parameters ??= new Dictionary<string, object>();
                bundle.Parameters["StartX"] = 0;
                bundle.Parameters["StartY"] = 0;
                bundle.Parameters["Width"] = page.ProjectInfo.RelativeWidth;
                bundle.Parameters["Height"] = page.ProjectInfo.RelativeHeight;
                bundle.Parameters["Angle"] = 0f;
                return bundle;
            }

            IEffectBundle NormalizeCropBundle(IEffectBundle? source, IEffect? fallbackEffect)
            {
                var normalized = BuildDefaultCropBundle();

                if (source != null && string.Equals(source.TypeName, "Crop", StringComparison.Ordinal))
                {
                    normalized.Enabled = source.Enabled;
                    normalized.Parameters["StartX"] = Math.Max(0, ReadDictionaryIntValue(source.Parameters, "StartX", 0));
                    normalized.Parameters["StartY"] = Math.Max(0, ReadDictionaryIntValue(source.Parameters, "StartY", 0));
                    normalized.Parameters["Width"] = Math.Max(1, ReadDictionaryIntValue(source.Parameters, "Width", page.ProjectInfo.RelativeWidth));
                    normalized.Parameters["Height"] = Math.Max(1, ReadDictionaryIntValue(source.Parameters, "Height", page.ProjectInfo.RelativeHeight));
                    normalized.Parameters["Angle"] = ReadDictionaryFloatValue(source.Parameters, "Angle", 0f);
                    return normalized;
                }

                if (fallbackEffect != null && IsCropEffect(fallbackEffect))
                {
                    normalized.Enabled = fallbackEffect.Enabled;
                    normalized.Parameters["StartX"] = Math.Max(0, ReadEffectIntParameter(fallbackEffect, "StartX", 0));
                    normalized.Parameters["StartY"] = Math.Max(0, ReadEffectIntParameter(fallbackEffect, "StartY", 0));
                    normalized.Parameters["Width"] = Math.Max(1, ReadEffectIntParameter(fallbackEffect, "Width", page.ProjectInfo.RelativeWidth));
                    normalized.Parameters["Height"] = Math.Max(1, ReadEffectIntParameter(fallbackEffect, "Height", page.ProjectInfo.RelativeHeight));
                    normalized.Parameters["Angle"] = ReadEffectFloatParameter(fallbackEffect, "Angle", 0f);
                }

                return normalized;
            }

            IEffect? existingCropEffect = TryFindInternalCropEffect(clip, out var existingCropEffectValue)
                ? existingCropEffectValue
                : null;

            clip.EffectBundles ??= new Dictionary<Guid, IEffectBundle>();
            clip.EffectBundles.TryGetValue(InternalCropBundleGuid, out var existingInternalCropBundle);
            var currentCropBundle = NormalizeCropBundle(existingInternalCropBundle, existingCropEffect);
            IEffectBundle previousCropPayload = currentCropBundle;

            var cropView = new ClipCropConfiguratorView
            {
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Start,
                Margin = new(8, 0, 8, 0),
            };

            cropView.LoadFromBundle(currentCropBundle, existingCropEffect);
            cropView.RelativeWidth = page.ProjectInfo.RelativeWidth;
            cropView.RelativeHeight = page.ProjectInfo.RelativeHeight;

            var transformPpb = new PropertyPanelBuilder()
                .AddPositionTupleInputBox("place", new SingleLineLabel(PPLocalizedResources.General_LocationAndSize, 25), PositionTupleMode.XYWH, (valX, valY, valW, valH), entryWidth: 70)
                .AddCheckbox("allowFreeScaleResize", PPLocalizedResources.General_LocationAndSize_FreeZoom, allowFreeScaleResize)
                .AddSlider("rotationDeg", PPLocalizedResources.General_Rotation, 0, 360, rotationDeg)
                .AddText(new SingleLineLabel(PPLocalizedResources.General_Crop, 25))
                .AddSwitch("cropEnable", PPLocalizedResources._Enabled, currentCropBundle.Enabled)
                .AppendWhen(currentCropBundle.Enabled,
                c => c.AddButton(PPLocalizedResources.Effect_ProgressPlacer_OpenEditor, async (_, _) => await page.ShowAPopup(content: cropView, mode: "dialog"))
                    .AddSeparator()
                    .AddEntry("cropStartX", PPLocalizedResources._StartX, cropView.StartX.ToString(), "0", e => e.Keyboard = Keyboard.Numeric, EntryUpdateEventCallMode.OnUnfocused)
                    .AddEntry("cropStartY", PPLocalizedResources._StartY, cropView.StartY.ToString(), "0", e => e.Keyboard = Keyboard.Numeric, EntryUpdateEventCallMode.OnUnfocused)
                    .AddEntry("cropWidth", PPLocalizedResources._Width, cropView.CropWidth.ToString(), "1", e => e.Keyboard = Keyboard.Numeric, EntryUpdateEventCallMode.OnUnfocused)
                    .AddEntry("cropHeight", PPLocalizedResources._Height, cropView.CropHeight.ToString(), "1", e => e.Keyboard = Keyboard.Numeric, EntryUpdateEventCallMode.OnUnfocused)
                    );

            cropView.ConfigurationChanged += (s, bundle) =>
            {
                clip.Effects ??= new Dictionary<string, IEffect>();
                clip.EffectBundles ??= new Dictionary<Guid, IEffectBundle>();

                if (!string.Equals(bundle.TypeName, "Crop", StringComparison.Ordinal))
                {
                    return;
                }

                var normalized = NormalizeCropBundle(bundle, existingCropEffect);
                currentCropBundle = normalized;
                clip.EffectBundles[InternalCropBundleGuid] = normalized;

                // The internal crop now comes from bundle conversion to effect.
                clip.Effects.Remove(InternalCropID);
                RebuildAllEffects(clip);

                if (TryFindInternalCropEffect(clip, out var rebuiltCrop))
                {
                    rebuiltCrop.RelativeWidth = page.ProjectInfo.RelativeWidth;
                    rebuiltCrop.RelativeHeight = page.ProjectInfo.RelativeHeight;
                    SyncOutputSizeFromCropIfNeeded(rebuiltCrop);
                    handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("crop", rebuiltCrop, previousCropPayload));
                }
                else
                {
                    handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("crop", normalized, previousCropPayload));
                }

                previousCropPayload = normalized;
                SyncCropInputsFromView();
            };

            bool syncingCropInputs = false;

            void SetTransformEntryText(string id, int value)
            {
                if (transformPpb.Components.TryGetValue(id, out var component) && component is Entry entry)
                {
                    var text = value.ToString();
                    if (entry.Text != text)
                    {
                        entry.Text = text;
                    }

                    transformPpb.Properties[id] = text;
                }
            }

            void ApplyResizeToModelWithCurrentMode(int width, int height)
            {
                width = Math.Max(1, width);
                height = Math.Max(1, height);

                clip.TargetWidth = width;
                clip.TargetHeight = height;
            }

            void SyncOutputSizeFromCropIfNeeded(IEffect crop)
            {
                if (!crop.Enabled)
                {
                    return;
                }

                if (!TryGetCropSize(crop, out var cropW, out var cropH))
                {
                    return;
                }

                int croppedW = Math.Max(1, cropW);
                int croppedH = Math.Max(1, cropH);

                SetTransformEntryText("place_W", croppedW);
                SetTransformEntryText("place_H", croppedH);
                ApplyResizeToModelWithCurrentMode(croppedW, croppedH);
            }

            void SnapSizeBackToSourceAspectIfNeeded()
            {
                if (!TryGetSourceAspectRatio(clip, [page.Assets, AssetDatabase.Assets], out var sourceAspect) || sourceAspect <= 0)
                {
                    return;
                }

                int currentW = ResolvePanelInt(transformPpb, "place_W", transformPpb.Properties.GetValueOrDefault("place_W"), "place_W", clip.TargetWidth > 0 ? clip.TargetWidth : page.ProjectInfo.RelativeWidth);
                int currentH = ResolvePanelInt(transformPpb, "place_H", transformPpb.Properties.GetValueOrDefault("place_H"), "place_H", clip.TargetHeight > 0 ? clip.TargetHeight : page.ProjectInfo.RelativeHeight);

                currentW = Math.Max(1, currentW);
                currentH = Math.Max(1, currentH);

                int snappedW;
                int snappedH;
                if (Math.Abs(((double)currentW / currentH) - sourceAspect) < 1e-6)
                {
                    snappedW = currentW;
                    snappedH = currentH;
                }
                else
                {
                    snappedW = currentW;
                    snappedH = Math.Max(1, (int)Math.Round(currentW / sourceAspect, MidpointRounding.AwayFromZero));
                }

                SetTransformEntryText("place_W", snappedW);
                SetTransformEntryText("place_H", snappedH);
                ApplyResizeToModelWithCurrentMode(snappedW, snappedH);
            }

            transformPpb.PropertyChanged += (s, e) =>
            {
                clip.Effects ??= new Dictionary<string, IEffect>();

                if (e.Id == "allowFreeScaleResize")
                {
                    bool allowFreeScale = e.Value is bool b
                        ? b
                        : bool.TryParse(e.Value?.ToString(), out var parsed) && parsed;

                    clip.ExtraData ??= new Dictionary<string, object>();
                    clip.ExtraData[AllowFreeScaleResizeKey] = allowFreeScale;

                    if (!allowFreeScale)
                    {
                        SnapSizeBackToSourceAspectIfNeeded();
                    }

                    handler?.Invoke(s, e);
                    return;
                }

                if (e.Id.StartsWith("place_"))
                {
                    switch (e.Id)
                    {
                        case "place_X":
                            clip.TargetX = (int)Math.Round(Convert.ToDouble(e.Value));
                            break;
                        case "place_Y":
                            clip.TargetY = (int)Math.Round(Convert.ToDouble(e.Value));
                            break;
                        case "place_W":
                            clip.TargetWidth = Math.Max(1, (int)Math.Round(Convert.ToDouble(e.Value)));
                            break;
                        case "place_H":
                            clip.TargetHeight = Math.Max(1, (int)Math.Round(Convert.ToDouble(e.Value)));
                            break;
                    }

                    handler?.Invoke(s, e);
                    return;
                }

                if (e.Id == "rotationDeg")
                {
                    if (e.Value is double deg)
                    {
                        RotationEffect_IPicture? existingRotation = null;
                        if (clip.Effects.TryGetValue(InternalRotationID, out var existingRot) && existingRot is RotationEffect_IPicture oldRot)
                        {
                            existingRotation = oldRot;
                        }

                        clip.Effects[InternalRotationID] = new RotationEffect_IPicture
                        {
                            Angle = (float)deg,
                            Enabled = existingRotation?.Enabled ?? true,
                            Name = existingRotation?.Name ?? InternalRotationID,
                            Index = existingRotation?.Index ?? (int.MinValue + 100),
                            RelativeWidth = page.ProjectInfo.RelativeWidth,
                            RelativeHeight = page.ProjectInfo.RelativeHeight,
                            ExpandCanvas = existingRotation?.ExpandCanvas ?? false,
                            ImplementType = existingRotation?.ImplementType ?? EffectImplementType.IPicture,
                            Id = string.IsNullOrWhiteSpace(existingRotation?.Id) ? InternalRotationID : existingRotation.Id
                        };
                    }

                    handler?.Invoke(s, e);
                    return;
                }

                if (!syncingCropInputs)
                {
                    if (e.Id == "cropStartX" && int.TryParse(e.Value?.ToString(), out var sx))
                    {
                        cropView.StartX = sx;
                    }
                    else if (e.Id == "cropStartY" && int.TryParse(e.Value?.ToString(), out var sy))
                    {
                        cropView.StartY = sy;
                    }
                    else if (e.Id == "cropWidth" && int.TryParse(e.Value?.ToString(), out var w))
                    {
                        cropView.CropWidth = w;
                    }
                    else if (e.Id == "cropHeight" && int.TryParse(e.Value?.ToString(), out var h))
                    {
                        cropView.CropHeight = h;
                    }
                    else if (e.Id == "cropEnable" && e.Value is bool cropEnabled)
                    {
                        cropView.Enabled = cropEnabled;
                        handler?.Invoke(s, e);
                        handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                        return;

                    }
                }

                handler?.Invoke(s, e);
            };



            void SetCropEntryText(string id, int value)
            {
                if (transformPpb is null)
                {
                    return;
                }

                if (transformPpb.Components.TryGetValue(id, out var component) && component is Entry entry)
                {
                    var text = value.ToString();
                    if (entry.Text != text)
                    {
                        entry.Text = text;
                    }
                    transformPpb.Properties[id] = text;
                }
            }

            void SetCropTextEntryText(string id, string value)
            {
                if (transformPpb is null)
                {
                    return;
                }

                if (transformPpb.Components.TryGetValue(id, out var component) && component is Entry entry)
                {
                    if (entry.Text != value)
                    {
                        entry.Text = value;
                    }

                    transformPpb.Properties[id] = value;
                }
            }

            void SyncCropInputsFromView()
            {
                syncingCropInputs = true;
                try
                {
                    SetCropEntryText("cropStartX", cropView.StartX);
                    SetCropEntryText("cropStartY", cropView.StartY);
                    SetCropEntryText("cropWidth", cropView.CropWidth);
                    SetCropEntryText("cropHeight", cropView.CropHeight);
                }
                finally
                {
                    syncingCropInputs = false;
                }
            }

            var scrollView = transformPpb.BuildWithScrollView();

            if (TryGetProgressPlacerBundle(clip, out _, out _, false))
            {
                var root = new Grid
                {
                    RowDefinitions =
                    {
                        new RowDefinition(GridLength.Auto),
                        new RowDefinition(GridLength.Star)
                    },
                    RowSpacing = 8
                };
                root.Add(new Label
                {
                    Text = PPLocalizedResources.KeyFrame_EditWarning,
                    TextColor = Colors.Yellow,
                    FontSize = 12,
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Center,
                    Margin = new Thickness(10, 10, 0, 0)
                }, 0, 0);
                root.Add(scrollView, 0, 1);

                SyncCropInputsFromView();
                return root;
            }

            SyncCropInputsFromView();
            return scrollView;
        }

        private bool TryGetProgressPlacerBundle(ClipElementUI clip, out IKeyFramedEffectProvider provider, out IEffectBundle bundle, bool createIfMissing = false)
        {
            clip.EffectBundles ??= new Dictionary<Guid, IEffectBundle>();

            foreach (var eb in clip.EffectBundles.Values)
            {
                if (string.Equals(eb.TypeName, "ProgressPlacer", StringComparison.Ordinal) && eb is IKeyFramedEffectProvider kfp)
                {
                    provider = kfp;
                    bundle = eb;
                    return true;
                }
            }

            if (createIfMissing)
            {
                var newBundle = new ProgressPlacerEffectBundle();
                clip.EffectBundles[newBundle.Id] = newBundle;
                AutoConnectBundleToOutput(clip, newBundle);
                RebuildAllEffects(clip);
                provider = newBundle;
                bundle = newBundle;
                return true;
            }

            provider = null!;
            bundle = null!;
            return false;
        }

        #endregion

        #region kf

        private View BuildKeyFrameTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            clip.EffectBundles ??= new Dictionary<Guid, IEffectBundle>();

            var root = new VerticalStackLayout
            {
                Spacing = 10,
                Padding = new Thickness(12, 10)
            };
            var title = new Label
            {
                Text = Localized.InteractableEditor_KeyFrame,
                FontSize = 20,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#E8EEF8")
            };
            root.Children.Add(title);
            ClipPositionTuple GetCurrentClipPosition()
            {
                int width = clip.TargetWidth > 0 ? clip.TargetWidth : (int)Math.Max(1, page.ProjectInfo.RelativeWidth);
                int height = clip.TargetHeight > 0 ? clip.TargetHeight : (int)Math.Max(1, page.ProjectInfo.RelativeHeight);
                return new ClipPositionTuple(clip.TargetX, clip.TargetY, width, height, false);
            }

            bool hasAnyProvider = false;

            foreach (var kvp in clip.EffectBundles)
            {
                if (kvp.Value is not IKeyFramedEffectProvider provider)
                    continue;

                hasAnyProvider = true;
                var section = BuildKeyframeProviderSectionUI(provider, clip, GetCurrentClipPosition, handler);
                root.Children.Add(section);
            }

            // Also check ProgressPlacer which may exist as its own bundle
            if (TryGetProgressPlacerBundle(clip, out var placerProvider, out _, false) && !hasAnyProvider)
            {
                hasAnyProvider = true;
                var section = BuildKeyframeProviderSectionUI(placerProvider, clip, GetCurrentClipPosition, handler);
                root.Children.Add(section);
            }

            if (!hasAnyProvider)
            {

                root.Children.Add(new Label
                {
                    Text = PPLocalizedResources.KeyFrame_NoSupport,
                    TextColor = Color.FromArgb("#A8B8CC"),
                    FontSize = 12,
                    LineBreakMode = LineBreakMode.WordWrap
                });
            }

            // 列出可用的支持关键帧的 Effect，供用户添加
            var allBundleFactories = EffectServices.GetAvailableEffectBundles();
            if (allBundleFactories.Count > 0)
            {
                root.Children.Add(new BoxView
                {
                    HeightRequest = 1,
                    Color = Colors.White.WithAlpha(0.1f),
                    Margin = new Thickness(0, 8)
                });

                root.Children.Add(new Label
                {
                    Text = PPLocalizedResources.KeyFrame_AddTitle,
                    FontSize = 16,
                    TextColor = Color.FromArgb("#C8C8CC"),
                    Margin = new Thickness(0, 4, 0, 8)
                });

                var addPpb = new PropertyPanelBuilder();
                root.Children.Add(BuildAddEffectPanel(
                    EffectTarget.IsKeyFramed | clip.GetEffectTarget(),
                    page,
                    allBundleFactories,
                    addPpb,
                    (s, e) =>
                    {
                        if (e.Id == "AddBundle" &&
                            addPpb.Properties.TryGetValue("NewBundleType", out var typeObj) &&
                            typeObj is string bundleTypeName &&
                            allBundleFactories.TryGetValue(bundleTypeName, out var factory))
                        {
                            var instance = factory();
                            instance.Id = Guid.NewGuid();
                            instance.BindedInputId = IEffectBundle.NoConnectionGUID;
                            instance.BindedOutputId = IEffectBundle.NoConnectionGUID;
                            clip.EffectBundles ??= new Dictionary<Guid, IEffectBundle>();
                            clip.EffectBundles[instance.Id] = instance;
                            AutoConnectBundleToOutput(clip, instance);
                            RebuildAllEffects(clip);
                            handler?.Invoke(this, new PropertyPanelPropertyChangedEventArgs("ProgressList", null, null));
                            // 重建标签页
                            var rebuiltTab = BuildKeyFrameTab(clip, handler);
                            if (root.Parent is ScrollView sv)
                            {
                                sv.Content = rebuiltTab;
                            }
                        }
                    },
                    showSubfix: false,
                    ignoreIsNotVisibleInNewEffectSelector: true //keyframed effect may not have visible UI but should still be addable from this panel
                ));
            }

            return new ScrollView { Content = root };
        }

        private View BuildKeyframeProviderSectionUI(
            IKeyFramedEffectProvider provider,
            ClipElementUI clip,
            Func<ClipPositionTuple> getDefaultPosition,
            EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            var section = new VerticalStackLayout { Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };

            string displayName = EffectBundleUiHelper.L(provider.TypeName, provider.TypeName);

            var actionsRow = new HorizontalStackLayout { Spacing = 8 };
            var collapseButton = new Label
            {
                Text = "▼",
                TextColor = Color.FromArgb("#A8B8CC"),
                FontSize = 14,
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Start
            };
            var title = new Label
            {
                Text = displayName,
                FontSize = 20,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#E8EEF8"),
                HorizontalOptions = LayoutOptions.Start
            };
            var addButton = new Button
            {
                Text = "+",
                CornerRadius = 8,
                FontAttributes = FontAttributes.Bold,
                HorizontalOptions = LayoutOptions.End
            };
            actionsRow.Children.Add(collapseButton);
            actionsRow.Children.Add(title);
            actionsRow.Children.Add(addButton);
            section.Children.Add(actionsRow);

            var listHost = new VerticalStackLayout { Spacing = 8 };
            section.Children.Add(listHost);

            bool isCollapsed = false;
            var collapseTap = new TapGestureRecognizer();
            var titleTap = new TapGestureRecognizer();
            void SwitchCollpaseMode()
            {
                isCollapsed = !isCollapsed;
                collapseButton.Text = isCollapsed ? "▶" : "▼";
                listHost.IsVisible = !isCollapsed;
            }
            collapseTap.Tapped += (_, _) => SwitchCollpaseMode();
            titleTap.Tapped += (_, _) => SwitchCollpaseMode();
            collapseButton.GestureRecognizers.Add(collapseTap);
            title.GestureRecognizers.Add(titleTap);

            // Store a stable reference to the "CropList" parameter name for the notification event
            string listParamName = $"{provider.TypeName}List";

            void RebuildList()
            {
                listHost.Children.Clear();

                var steps = provider.Steps;
                if (steps.Count == 0)
                {
                    listHost.Children.Add(new Label
                    {
                        Text = PPLocalizedResources.KeyFrame_Empty,
                        TextColor = Color.FromArgb("#A8B8CC"),
                        FontSize = 12
                    });
                    listHost.Children.Add(new Button
                    {
                        Text = PPLocalizedResources.EffectProp_Remove,
                        Command = new Command(() =>
                        {
                            if (provider is IEffectBundle bud) clip.EffectBundles?.Remove(bud.Id);
                            handler?.Invoke(this, new PropertyPanelPropertyChangedEventArgs(listParamName, null, null));
                            RebuildList();
                        }),
                        TextColor = Color.FromArgb("#FF8080")
                    });
                    return;
                }

                for (int i = 0; i < steps.Count; i++)
                {
                    int idx = i;
                    var stepInfo = steps[idx];

                    var card = new Border
                    {
                        Stroke = Colors.White.WithAlpha(0.10f),
                        StrokeShape = new RoundRectangle { CornerRadius = 10 },
                        Background = new SolidColorBrush(Color.FromArgb("#0FFFFFFF")),
                        Padding = new Thickness(10)
                    };

                    var stack = new VerticalStackLayout { Spacing = 8 };

                    var header = new Grid
                    {
                        ColumnDefinitions =
                        {
                            new ColumnDefinition(GridLength.Star),
                            new ColumnDefinition(GridLength.Auto)
                        }
                    };
                    header.Add(new Label
                    {
                        Text = PPLocalizedResources.KeyFrame_Progress(i, stepInfo.Progress),
                        FontAttributes = FontAttributes.Bold,
                        FontSize = 13,
                        TextColor = Color.FromArgb("#DDE7F3")
                    }, 0, 0);

                    var deleteButton = new Button
                    {
                        Text = "\ue9d5",
                        FontFamily = "Icons",
                        CornerRadius = 8,
                        Padding = new Thickness(10, 4),
                        TextColor = Color.FromArgb("#FF8080")
                    };
                    deleteButton.Clicked += (_, _) =>
                    {
                        provider.RemoveStep(idx);
                        RebuildAllEffects(clip);
                        handler?.Invoke(this, new PropertyPanelPropertyChangedEventArgs(listParamName, null, null));
                        RebuildList();
                    };
                    header.Add(deleteButton, 1, 0);
                    stack.Children.Add(header);

                    var stepPpb = provider.CreateStepUI(idx);
                    var stepView = stepPpb.Build();
                    stepPpb.PropertyChanged += (s, args) =>
                    {
                        if (provider.HandleStepUIChange(idx, args))
                        {
                            RebuildAllEffects(clip);
                            handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs(listParamName, null, null));
                            RebuildList();
                        }
                    };
                    stack.Children.Add(stepView);
                    card.Content = stack;
                    listHost.Children.Add(card);
                }
            }

            addButton.Clicked += (_, _) =>
            {
                if (isCollapsed)
                {
                    isCollapsed = !isCollapsed;
                    collapseButton.Text = isCollapsed ? "▶" : "▼";
                    listHost.IsVisible = !isCollapsed;
                }
                provider.AddStep(getDefaultPosition());
                RebuildAllEffects(clip);
                handler?.Invoke(this, new PropertyPanelPropertyChangedEventArgs(listParamName, null, null));
                RebuildList();
            };

            RebuildList();
            return section;
        }

        #endregion

        #region text

        public static View BuildTextEntryUI(TextClipEntry e, int idx, IEnumerable<FontItem> fontItems,
            Action<int, TextClipEntry> onChanged,
            Action<int> onRemove,
            bool canDeleteEntry = true,
            bool showAllOptions = false,
            Action<FontPicker>? ShowPicker = null,
            Action? HidePicker = null)
        {
            var currentEntry = e;
            Label SecLabel(string t) => new Label
            {
                Text = t,
                FontSize = 10,
                TextColor = Colors.White,
                FontAttributes = FontAttributes.Bold,
                Margin = new Thickness(0, 6, 0, 2)
            };
            BoxView Divider() => new BoxView
            {
                HeightRequest = 1,
                Color = Colors.White.WithAlpha(0.06f),
                Margin = new Thickness(0, 4)
            };

            var stack = new VerticalStackLayout { Spacing = 4 };
            var glyphWarning = new Label
            {
                TextColor = Colors.OrangeRed,
                FontSize = 12,
                IsVisible = false,
                LineBreakMode = LineBreakMode.WordWrap
            };

            void UpdateGlyphWarning()
            {
                var warning = TextServices.GetMissingGlyphWarning(currentEntry.fontFamily, currentEntry.text, currentEntry.fontSize);
                glyphWarning.Text = warning;
                glyphWarning.IsVisible = !string.IsNullOrWhiteSpace(warning);
            }

            var headerGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                }
            };
            headerGrid.Add(new Label
            {
                Text = PPLocalizedResources.TextOption_EntryTitle(idx + 1),
                FontAttributes = FontAttributes.Bold,
                FontSize = 13,
                TextColor = Color.FromArgb("#A8B8CC"),
                VerticalOptions = LayoutOptions.Center
            }, 0, 0);
            var removeBtn = new Button
            {
                Text = "✕",
                WidthRequest = 28,
                HeightRequest = 28,
                Padding = 0,
                BackgroundColor = Colors.Transparent,
                TextColor = Color.FromArgb("#FF6060"),
                FontSize = 13,
                HorizontalOptions = LayoutOptions.End,
                VerticalOptions = LayoutOptions.Center,
                IsVisible = canDeleteEntry
            };
            removeBtn.Clicked += (s, ev) => { onRemove?.Invoke(idx); };
            headerGrid.Add(removeBtn, 1, 0);
            stack.Children.Add(headerGrid);
            stack.Children.Add(Divider());

            // CONTENT
            stack.Children.Add(SecLabel(PPLocalizedResources.TextOption_Content));
            var editor = new Editor
            {
                Text = e.text,
                AutoSize = EditorAutoSizeOption.TextChanges,
                MinimumHeightRequest = 64,
                Placeholder = PPLocalizedResources.TextOption_Content_Placeholder
            };
            editor.TextChanged += (s, ev) =>
            {
                currentEntry = currentEntry with { text = editor.Text ?? string.Empty };
                UpdateGlyphWarning();
            };
            editor.Unfocused += (s, ev) =>
            {
                currentEntry = currentEntry with { text = editor.Text ?? string.Empty };
                onChanged?.Invoke(idx, currentEntry);
                UpdateGlyphWarning();
            };
            stack.Children.Add(editor);

            // POSITION
            stack.Children.Add(SecLabel(PPLocalizedResources.TextOption_Position));
            var posGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star)
                },
                ColumnSpacing = 6
            };
            var xEntry = new Entry { Text = e.x.ToString(), Placeholder = "0" };
            var yEntry = new Entry { Text = e.y.ToString(), Placeholder = "0" };
            xEntry.Unfocused += (s, ev) => { if (int.TryParse(xEntry.Text, out var nx)) onChanged?.Invoke(idx, e with { x = nx }); };
            yEntry.Unfocused += (s, ev) => { if (int.TryParse(yEntry.Text, out var ny)) onChanged?.Invoke(idx, e with { y = ny }); };
            posGrid.Add(new Label { Text = "X", VerticalOptions = LayoutOptions.Center, TextColor = Colors.White }, 0, 0);
            posGrid.Add(xEntry, 1, 0);
            posGrid.Add(new Label { Text = "Y", VerticalOptions = LayoutOptions.Center, TextColor = Colors.White }, 2, 0);
            posGrid.Add(yEntry, 3, 0);
            stack.Children.Add(posGrid);

            // FONT
            stack.Children.Add(SecLabel(PPLocalizedResources.TextOption_Font));
            var fonts = fontItems.Select(x => x.FontName).ToList();
            var currentFontName = fonts.Contains(e.fontFamily) ? e.fontFamily : fonts.FirstOrDefault() ?? string.Empty;
            currentEntry = currentEntry with { fontFamily = currentFontName };

            var fontGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(new GridLength(72))
                },
                ColumnSpacing = 6
            };
            var sizeEntry = new Entry { Text = e.fontSize.ToString(), Placeholder = "24" };
            sizeEntry.Unfocused += (s, ev) => { if (float.TryParse(sizeEntry.Text, out var ns)) onChanged?.Invoke(idx, e with { fontSize = ns }); };
            fontGrid.Add(new Label { Text = PPLocalizedResources.TextOption_Size, VerticalOptions = LayoutOptions.Center, TextColor = Colors.White }, 1, 0);
            fontGrid.Add(sizeEntry, 2, 0);

            var fontSelectBtn = new Button
            {
                Text = currentFontName,
                HorizontalOptions = LayoutOptions.Fill,
                BackgroundColor = Color.FromArgb("#1AFFFFFF"),
                TextColor = Colors.White,
                FontSize = 13,
                Padding = new Thickness(8, 4),
                CornerRadius = 6
            };

            void FontChanged(object? sender, FontItem font)
            {
                if (font == null) return;

                if (font.InnerFont is not null)
                    TextClipFontRegistry.RegisterFontFace(font.InnerFont);
                else if (!string.IsNullOrWhiteSpace(font.Path))
                    TextClipFontRegistry.AddFont(font.Path);

                fontSelectBtn.Text = font.DisplayName;
                currentEntry = currentEntry with { fontFamily = font.FontName };
                UpdateGlyphWarning();
                HidePicker?.Invoke();
                onChanged?.Invoke(idx, currentEntry);
            }
            if (ShowPicker is not null)
            {

                var fontPickerControl = new projectFrameCut.ApplicationAPIBase.Views.Pickers.FontPicker
                {
                    FontsSource = fontItems.GroupBy(c => TextHelper.DetectTextLanguage(c.DisplayName))
                                           .OrderByDescending(g => g.Count())
                                           .SelectMany(c => c),
                    PreviewRenderer = TextServices.RenderFontPreviewAsync,
                    Title = PPLocalizedResources.TextOption_Font
                };

                fontPickerControl.SelectedFontChanged += FontChanged;

                fontSelectBtn.Clicked += (s, ev) =>
                {
                    ShowPicker?.Invoke(fontPickerControl);
                };

                fontGrid.Add(fontSelectBtn, 0, 0);
            }
            else
            {
                var picker = new Picker { ItemsSource = fonts, SelectedIndex = Array.IndexOf(fonts.ToArray(), e.fontFamily) };
                picker.SelectedIndexChanged += (s, e) =>
                {
                    FontChanged(null, fontItems.FirstOrDefault(c => c.FontName == picker.SelectedItem as string, null));
                };
                fontGrid.Add(picker, 0, 0);

            }

            stack.Children.Add(fontGrid);
            UpdateGlyphWarning();
            stack.Children.Add(glyphWarning);


            var stylePicker = new Picker { Title = PPLocalizedResources.TextOption_Style, ItemsSource = new[] { PPLocalizedResources.TextOption_Style_Regular, PPLocalizedResources.TextOption_Style_Bold, PPLocalizedResources.TextOption_Style_Italic, PPLocalizedResources.TextOption_Style_BoldItalic }, SelectedItem = e.fontStyle switch { ClipFontStyle.Regular => PPLocalizedResources.TextOption_Style_Regular, ClipFontStyle.Bold => PPLocalizedResources.TextOption_Style_Bold, ClipFontStyle.Italic => PPLocalizedResources.TextOption_Style_Italic, ClipFontStyle.BoldItalic => PPLocalizedResources.TextOption_Style_BoldItalic, _ => PPLocalizedResources.TextOption_Style_Regular, } };
            stylePicker.SelectedIndexChanged += (s, ev) =>
            {
                if (stylePicker.SelectedItem is string sel)
                {
                    var fs = sel switch
                    {
                        var v when v == PPLocalizedResources.TextOption_Style_Bold => ClipFontStyle.Bold,
                        var v when v == PPLocalizedResources.TextOption_Style_Italic => ClipFontStyle.Italic,
                        var v when v == PPLocalizedResources.TextOption_Style_BoldItalic => ClipFontStyle.BoldItalic,
                        _ => ClipFontStyle.Regular,
                    };
                    onChanged?.Invoke(idx, e with { fontStyle = fs });
                }
            };
            stack.Children.Add(stylePicker);

            // TEXT COLOR
            stack.Children.Add(SecLabel(PPLocalizedResources.TextOption_Color));
            var colorSwatch = new Border
            {
                WidthRequest = 32,
                HeightRequest = 32,
                StrokeShape = new RoundRectangle { CornerRadius = 6 },
                Stroke = Colors.White.WithAlpha(0.12f),
                VerticalOptions = LayoutOptions.Center
            };
            try { colorSwatch.Background = new SolidColorBrush(Color.FromRgba(e.r / 65535.0, e.g / 65535.0, e.b / 65535.0, e.a ?? 1f)); } catch { }
            var colorEntry = new Entry { Text = $"#{((int)Math.Round(e.r / 257.0)):X2}{((int)Math.Round(e.g / 257.0)):X2}{((int)Math.Round(e.b / 257.0)):X2}" };
            colorEntry.Unfocused += (s, ev) =>
            {
                try
                {
                    var c = Color.FromArgb(colorEntry.Text);
                    ushort r = (ushort)Math.Round(c.Red * 65535);
                    ushort g = (ushort)Math.Round(c.Green * 65535);
                    ushort b = (ushort)Math.Round(c.Blue * 65535);
                    float a = (float)c.Alpha;
                    var updated = e with { r = r, g = g, b = b, a = a };
                    colorSwatch.Background = new SolidColorBrush(c);
                    onChanged?.Invoke(idx, updated);
                }
                catch { }
            };
            var colorRow = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
                ColumnSpacing = 8
            };
            colorRow.Add(colorSwatch, 0, 0);
            colorRow.Add(colorEntry, 1, 0);
            stack.Children.Add(colorRow);

            // ADVANCED (collapsible): ALIGNMENT + TYPOGRAPHY + STROKE
            var advancedStack = new VerticalStackLayout { Spacing = 4, IsVisible = false };

            // ALIGNMENT
            advancedStack.Children.Add(SecLabel(PPLocalizedResources.TextOption_LangType));
            var langTypePicker = new Picker
            {
                ItemsSource = new string[] { PPLocalizedResources.TextOption_LangType_Auto }.Concat(Enum.GetValues<TextLanguage>().Skip(1).Select(TextClipEntry.LocalizeLanguageName)).ToList(),
                SelectedItem = (int)e.Language
            };
            langTypePicker.SelectedIndexChanged += (s, ev) =>
            {
                onChanged?.Invoke(idx, e with { Language = (TextLanguage)langTypePicker.SelectedIndex });
            };
            advancedStack.Children.Add(langTypePicker);
            advancedStack.Children.Add(SecLabel(PPLocalizedResources.TextOption_Alignment));
            var alignGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(new GridLength(80))
                },
                ColumnSpacing = 6
            };
            var hAlignPicker = new Picker { Title = PPLocalizedResources.TextOption_HorizonOption, ItemsSource = new[] { PPLocalizedResources.TextOption_HorizonOption_Left, PPLocalizedResources.TextOption_HorizonOption_Center, PPLocalizedResources.TextOption_HorizonOption_Right }, SelectedItem = e.horizontalAlignment switch { ClipHorizontalAlignment.Left => PPLocalizedResources.TextOption_HorizonOption_Left, ClipHorizontalAlignment.Center => PPLocalizedResources.TextOption_HorizonOption_Center, ClipHorizontalAlignment.Right => PPLocalizedResources.TextOption_HorizonOption_Right, _ => PPLocalizedResources.TextOption_HorizonOption_Left, } };
            hAlignPicker.SelectedIndexChanged += (s, ev) =>
            {
                if (hAlignPicker.SelectedItem is string sel)
                {
                    ClipHorizontalAlignment ha = sel switch
                    {
                        var v when v == PPLocalizedResources.TextOption_HorizonOption_Center => ClipHorizontalAlignment.Center,
                        var v when v == PPLocalizedResources.TextOption_HorizonOption_Right => ClipHorizontalAlignment.Right,
                        _ => ClipHorizontalAlignment.Left,
                    };
                    onChanged?.Invoke(idx, e with { horizontalAlignment = ha });
                }
            };
            var vAlignPicker = new Picker { Title = PPLocalizedResources.TextOption_VerticalOption, ItemsSource = new[] { PPLocalizedResources.TextOption_VerticalOption_Top, PPLocalizedResources.TextOption_VerticalOption_Center, PPLocalizedResources.TextOption_VerticalOption_Bottom }, SelectedItem = e.verticalAlignment switch { ClipVerticalAlignment.Top => PPLocalizedResources.TextOption_VerticalOption_Top, ClipVerticalAlignment.Center => PPLocalizedResources.TextOption_VerticalOption_Center, ClipVerticalAlignment.Bottom => PPLocalizedResources.TextOption_VerticalOption_Bottom, _ => PPLocalizedResources.TextOption_VerticalOption_Top, } };
            vAlignPicker.SelectedIndexChanged += (s, ev) =>
            {
                if (vAlignPicker.SelectedItem is string sel)
                {
                    ClipVerticalAlignment va = sel switch
                    {
                        var v when v == PPLocalizedResources.TextOption_VerticalOption_Center => ClipVerticalAlignment.Center,
                        var v when v == PPLocalizedResources.TextOption_VerticalOption_Bottom => ClipVerticalAlignment.Bottom,
                        _ => ClipVerticalAlignment.Top,
                    };
                    onChanged?.Invoke(idx, e with { verticalAlignment = va });
                }
            };
            var wrapEntry = new Entry { Text = e.wrappingWidth?.ToString() ?? string.Empty, Placeholder = PPLocalizedResources.TextOption_WrapW_Hint };
            wrapEntry.Unfocused += (s, ev) =>
            {
                if (float.TryParse(wrapEntry.Text, out var w)) onChanged?.Invoke(idx, e with { wrappingWidth = w });
                else onChanged?.Invoke(idx, e with { wrappingWidth = null });
            };
            alignGrid.Add(hAlignPicker, 0, 0);
            alignGrid.Add(vAlignPicker, 1, 0);
            alignGrid.Add(new Label { Text = PPLocalizedResources.TextOption_WrapW, VerticalOptions = LayoutOptions.Center, TextColor = Colors.White }, 2, 0);
            alignGrid.Add(wrapEntry, 3, 0);
            advancedStack.Children.Add(alignGrid);


            // TYPOGRAPHY
            advancedStack.Children.Add(SecLabel(PPLocalizedResources.TextOption_Typography));
            var verticalLayoutSwitch = new Switch { IsToggled = e.UseVerticalLayout, VerticalOptions = LayoutOptions.Center };
            var keepNonCJKHorizontalSwitch = new Switch { IsToggled = e.KeepNonCJKTextAsHorizontal, VerticalOptions = LayoutOptions.Center, IsVisible = verticalLayoutSwitch.IsToggled };
            var verticalLabel = new Label { Text = PPLocalizedResources.TextOption_UseVerticalLayout, VerticalOptions = LayoutOptions.Center, TextColor = Colors.White };
            var nonCJKHorizentalLabel = new Label { Text = PPLocalizedResources.TextOption_KeepNonCJKHorizontal, VerticalOptions = LayoutOptions.Center, TextColor = Colors.White, IsVisible = verticalLayoutSwitch.IsToggled };

            verticalLayoutSwitch.Toggled += (s, ev) => { keepNonCJKHorizontalSwitch.IsVisible = verticalLayoutSwitch.IsToggled; nonCJKHorizentalLabel.IsVisible = verticalLayoutSwitch.IsToggled; onChanged?.Invoke(idx, e with { UseVerticalLayout = verticalLayoutSwitch.IsToggled }); };
            keepNonCJKHorizontalSwitch.Toggled += (s, ev) => { onChanged?.Invoke(idx, e with { KeepNonCJKTextAsHorizontal = keepNonCJKHorizontalSwitch.IsToggled }); };

            var verticalLayoutGrid = new HorizontalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    verticalLabel,
                    verticalLayoutSwitch,
                    nonCJKHorizentalLabel,
                    keepNonCJKHorizontalSwitch
                }
            };
            advancedStack.Children.Add(verticalLayoutGrid);

            var kerningSwitch = new Switch { IsToggled = e.applyKerning, VerticalOptions = LayoutOptions.Center };
            kerningSwitch.Toggled += (s, ev) => { onChanged?.Invoke(idx, e with { applyKerning = kerningSwitch.IsToggled }); };
            var lineSpacingEntry = new Entry { Text = e.lineSpacing.ToString() };
            lineSpacingEntry.Unfocused += (s, ev) => { if (float.TryParse(lineSpacingEntry.Text, out var ls)) onChanged?.Invoke(idx, e with { lineSpacing = ls }); };
            var typRow = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(new GridLength(80))
                },
                ColumnSpacing = 8
            };
            typRow.Add(new Label { Text = PPLocalizedResources.TextOption_Kerning, VerticalOptions = LayoutOptions.Center, TextColor = Colors.White }, 0, 0);
            typRow.Add(kerningSwitch, 1, 0);
            typRow.Add(new Label { Text = PPLocalizedResources.TextOption_LineSpacing, VerticalOptions = LayoutOptions.Center, TextColor = Colors.White, HorizontalOptions = LayoutOptions.End }, 2, 0);
            typRow.Add(lineSpacingEntry, 3, 0);
            advancedStack.Children.Add(typRow);

            var rotEntry = new Entry { Text = e.rotation.ToString(), Placeholder = "0" };
            rotEntry.Unfocused += (s, ev) => { if (float.TryParse(rotEntry.Text, out var r)) onChanged?.Invoke(idx, e with { rotation = r }); };
            var dpiEntry = new Entry { Text = e.dpi?.ToString() ?? string.Empty, Placeholder = "auto" };
            dpiEntry.Unfocused += (s, ev) =>
            {
                if (float.TryParse(dpiEntry.Text, out var d)) onChanged?.Invoke(idx, e with { dpi = d });
                else onChanged?.Invoke(idx, e with { dpi = null });
            };
            var rotDpiGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star)
                },
                ColumnSpacing = 6
            };
            rotDpiGrid.Add(new Label { Text = PPLocalizedResources.TextOption_Rotation, VerticalOptions = LayoutOptions.Center, TextColor = Colors.White }, 0, 0);
            rotDpiGrid.Add(rotEntry, 1, 0);
            rotDpiGrid.Add(new Label { Text = "DPI", VerticalOptions = LayoutOptions.Center, TextColor = Colors.White }, 2, 0);
            rotDpiGrid.Add(dpiEntry, 3, 0);
            advancedStack.Children.Add(rotDpiGrid);

            // STROKE
            advancedStack.Children.Add(SecLabel(PPLocalizedResources.TextOption_Stroke));
            var strokeWidthEntry = new Entry { Text = e.strokeWidth?.ToString() ?? string.Empty, Placeholder = PPLocalizedResources.TextOption_Stroke_Hint, MinimumWidthRequest = 150 };
            strokeWidthEntry.Unfocused += (s, ev) =>
            {
                if (float.TryParse(strokeWidthEntry.Text, out var sw)) onChanged?.Invoke(idx, e with { strokeWidth = sw });
                else onChanged?.Invoke(idx, e with { strokeWidth = null });
            };
            var strokeSwatch = new Border
            {
                WidthRequest = 32,
                HeightRequest = 32,
                StrokeShape = new RoundRectangle { CornerRadius = 6 },
                Stroke = Colors.White.WithAlpha(0.12f),
                VerticalOptions = LayoutOptions.Center
            };
            try { strokeSwatch.Background = new SolidColorBrush(Color.FromRgba(e.strokeR / 65535.0, e.strokeG / 65535.0, e.strokeB / 65535.0, 1.0)); } catch { }
            var strokeEntry = new Entry { Text = $"#{((int)Math.Round(e.strokeR / 257.0)):X2}{((int)Math.Round(e.strokeG / 257.0)):X2}{((int)Math.Round(e.strokeB / 257.0)):X2}" };
            strokeEntry.Unfocused += (s, ev) =>
            {
                try
                {
                    var c = Color.FromArgb(strokeEntry.Text);
                    ushort r = (ushort)Math.Round(c.Red * 65535);
                    ushort g = (ushort)Math.Round(c.Green * 65535);
                    ushort b = (ushort)Math.Round(c.Blue * 65535);
                    var updated = e with { strokeR = r, strokeG = g, strokeB = b };
                    strokeSwatch.Background = new SolidColorBrush(c);
                    onChanged?.Invoke(idx, updated);
                }
                catch { }
            };
            var strokeGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star)
                },
                ColumnSpacing = 8
            };
            strokeGrid.Add(strokeWidthEntry, 0, 0);
            strokeGrid.Add(strokeSwatch, 1, 0);
            strokeGrid.Add(strokeEntry, 2, 0);
            advancedStack.Children.Add(strokeGrid);

            if (showAllOptions)
            {
                var subTrackSwitch = new Switch { IsToggled = e.ShouldInSubtrack, VerticalOptions = LayoutOptions.Center };
                var subTrackLabel = new Label { Text = PPLocalizedResources.TextOption_PlaceInSubtrack, VerticalOptions = LayoutOptions.Center, TextColor = Colors.White };
                subTrackSwitch.Toggled += (s, ev) => { onChanged?.Invoke(idx, e with { ShouldInSubtrack = subTrackSwitch.IsToggled }); };
                var subTrackGrid = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(GridLength.Auto),
                        new ColumnDefinition(GridLength.Auto),
                        new ColumnDefinition(GridLength.Star)
                    },
                    ColumnSpacing = 8
                };
                subTrackGrid.Add(subTrackLabel, 0, 0);
                subTrackGrid.Add(subTrackSwitch, 1, 0);
                advancedStack.Children.Add(subTrackGrid);
            }

            // Advanced toggle button
            var advancedToggleBtn = new Button
            {
                Text = PPLocalizedResources.TextOption_Advanced_Collapsed,
                HorizontalOptions = LayoutOptions.Fill,
                BackgroundColor = Colors.Transparent,
                TextColor = Color.FromArgb("#A8B8CC"),
                FontSize = 11,
                Padding = new Thickness(0, 4),
                Margin = new Thickness(0, 4, 0, 0)
            };
            advancedToggleBtn.Clicked += (s, ev) =>
            {
                advancedStack.IsVisible = !advancedStack.IsVisible;
                advancedToggleBtn.Text = advancedStack.IsVisible
                    ? PPLocalizedResources.TextOption_Advanced_Expanded
                    : PPLocalizedResources.TextOption_Advanced_Collapsed;
            };
            stack.Children.Add(advancedToggleBtn);
            stack.Children.Add(advancedStack);

            var border = new Border
            {
                Stroke = Colors.White.WithAlpha(0.10f),
                StrokeShape = new RoundRectangle { CornerRadius = 10 },
                Padding = new Thickness(12, 10),
                Background = new SolidColorBrush(Color.FromArgb("#0FFFFFFF")),
                Content = stack
            };
            return border;
        }


        private async Task<View> BuildTextOptionTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            string providerFrom = "";
            ITextClipStyleProvider? styleProvider;
            GetAndUpdateTextClipEntries(clip, out providerFrom, out styleProvider);

            if (styleProvider == null)
            {
                List<TextClipEntry>? entries = null;
                if (clip.ExtraData != null && clip.ExtraData.TryGetValue("TextEntries", out var teObj))
                {
                    if (teObj is List<TextClipEntry> list)
                        entries = list;
                    else if (teObj is System.Text.Json.JsonElement je)
                    {
                        try { entries = System.Text.Json.JsonSerializer.Deserialize<List<TextClipEntry>>(je); }
                        catch { }
                    }
                }

                if (entries is { Count: 1 })
                {
                    var entry = entries[0];

                    static string ToArgbHex(ushort r, ushort g, ushort b, float a)
                    {
                        var ab = (byte)Math.Round(a * 255);
                        var rb = (byte)Math.Round(r / 65535.0 * 255);
                        var gb = (byte)Math.Round(g / 65535.0 * 255);
                        var bb = (byte)Math.Round(b / 65535.0 * 255);
                        return $"#{ab:X2}{rb:X2}{gb:X2}{bb:X2}";
                    }
                    static string ToArgbHexNoAlpha(ushort r, ushort g, ushort b)
                    {
                        var rb = (byte)Math.Round(r / 65535.0 * 255);
                        var gb = (byte)Math.Round(g / 65535.0 * 255);
                        var bb = (byte)Math.Round(b / 65535.0 * 255);
                        return $"#FF{rb:X2}{gb:X2}{bb:X2}";
                    }

                    var basic = new projectFrameCut.ApplicationPluginBase.Text.BasicTextStyleProvider
                    {
                        Parameters = new Dictionary<string, string>
                        {
                            ["Text"] = entry.text ?? string.Empty,
                            ["FontFamily"] = entry.fontFamily ?? "Arial",
                            ["FontSize"] = entry.fontSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["Color"] = ToArgbHex(entry.r, entry.g, entry.b, entry.a ?? 1f),
                            ["FontStyle"] = entry.fontStyle.ToString(),
                            ["HorizontalAlignment"] = entry.horizontalAlignment.ToString(),
                            ["VerticalAlignment"] = entry.verticalAlignment.ToString(),
                            ["WrappingWidth"] = entry.wrappingWidth?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                            ["ApplyKerning"] = entry.applyKerning.ToString(),
                            ["LineSpacing"] = entry.lineSpacing.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["Rotation"] = entry.rotation.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["StrokeWidth"] = entry.strokeWidth?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                            ["StrokeColor"] = ToArgbHexNoAlpha(entry.strokeR, entry.strokeG, entry.strokeB),
                            ["Dpi"] = entry.dpi?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                            ["UseVerticalLayout"] = entry.UseVerticalLayout.ToString(),
                            ["KeepNonCJKTextAsHorizontal"] = entry.KeepNonCJKTextAsHorizontal.ToString(),
                            ["LayoutMode"] = "FillClip"
                        }
                    };
                    styleProvider = basic;
                }
                else
                {
                    var fallback = new projectFrameCut.ApplicationPluginBase.Text.OldTextClipEntryTextStyleProvider();
                    if (clip.ExtraData != null && clip.ExtraData.TryGetValue("TextEntries", out var teObj2))
                    {
                        try
                        {
                            if (teObj2 is System.Text.Json.JsonElement je)
                            {
                                fallback.Parameters["TextEntriesJson"] = je.GetRawText();
                            }
                            else
                            {
                                fallback.Parameters["TextEntriesJson"] = System.Text.Json.JsonSerializer.Serialize(teObj2);
                            }
                        }
                        catch { }
                    }

                    styleProvider = fallback;
                }
            }

            var providerPpb = styleProvider.BuildPropertyPanel();
            var providerHost = new PropertyPanelBuilder();
            var fontItems = TextServices.LoadedFonts
                            .Select(x => x.Value)
                            .GroupBy(c => TextHelper.DetectTextLanguage(c.DisplayName))
                            .OrderByDescending(g => g.Count())
                            .SelectMany(g => g)
                            .ToList();
            providerHost.AddText(new SingleLineLabel(styleProvider.TypeName, 18, FontAttributes.Bold));
            providerHost.AddSeparator();
            //providerPpb.UseDialogFontPicker(
            //    page,
            //    "FontFamily",
            //   ,
            //    styleProvider.Parameters.TryGetValue("FontFamily", out var selectedFontName) ? selectedFontName : null,
            //   ,
            //    ,
            //    PPLocalizedResources.TextOption_Font);

            // Add LayoutMode picker managed centrally via ITextClipStyleProvider.LayoutMode
            Dictionary<string, TextClipLayoutMode> LocalizedLayoutOptionKVP = new Dictionary<string, TextClipLayoutMode>
            {
                { PPLocalizedResources.TextOption_LayoutMode_FixedWidth, TextClipLayoutMode.FixedWidth },
                { PPLocalizedResources.TextOption_LayoutMode_FillClip, TextClipLayoutMode.FillClip },
                { PPLocalizedResources.TextOption_LayoutMode_FixedSize, TextClipLayoutMode.FixedSize },
            }; //FixedHeight mode is buggy so hide now
            providerHost
            .AppendWhen(styleProvider.ShowLayoutModePicker,
                c => c.AddPicker("LayoutMode", PPLocalizedResources.TextOption_LayoutMode, LocalizedLayoutOptionKVP.Keys.ToArray(), LocalizedLayoutOptionKVP.ReverseLookup(styleProvider.LayoutMode, PPLocalizedResources.TextOption_LayoutMode_FixedWidth), picker =>
                {
#if iDevices
                    picker.Closed += (s, e) =>
                    {
                        if (picker.SelectedItem is string modeStr && !string.IsNullOrWhiteSpace(modeStr))
                        {
                            if (LocalizedLayoutOptionKVP.TryGetValue(modeStr, out var parsedMode))
                                styleProvider.LayoutMode = parsedMode;
                            HandlePanelChange(styleProvider, new PropertyPanelPropertyChangedEventArgs("LayoutMode", modeStr, styleProvider.Parameters.TryGetValue("LayoutMode", out var m) ? m : "FillClip"));
                        }
                    };
#else
                    picker.SelectedIndexChanged += (s, e) =>
                    {
                        if (picker.SelectedItem is string modeStr && !string.IsNullOrWhiteSpace(modeStr))
                        {
                            if (LocalizedLayoutOptionKVP.TryGetValue(modeStr, out var parsedMode))
                                styleProvider.LayoutMode = parsedMode;
                            HandlePanelChange(styleProvider, new PropertyPanelPropertyChangedEventArgs("LayoutMode", modeStr, styleProvider.Parameters.TryGetValue("LayoutMode", out var m) ? m : "FillClip"));
                        }
                    };
#endif
                })
            )
            .AppendWhen(styleProvider.ShowDefaultTextEditor,
                c => c.AddCustomChild(
                (c) =>
                {
                    var editor = new Editor
                    {
                        MinimumHeightRequest = 150,
                        Text = styleProvider.BasicText,
                        IsSpellCheckEnabled = true,
                        IsTextPredictionEnabled = true,
                        Placeholder = PPLocalizedResources.TextOption_Content_Placeholder
                    };
                    editor.Unfocused += (_, _) => c(editor.Text);
                    return editor;
                },
                "Text", styleProvider.BasicText)
                .AddButton(Localized._Apply, (_, _) => { })
            )
            .AppendWhen(styleProvider.ShowFontPicker,
                c => c.AddDialogFontPicker(
                "FontFamily",
                PPLocalizedResources.TextOption_Font,
                PPLocalizedResources.TextOption_Font,
                styleProvider.Parameters.TryGetValue("FontFamily", out var selectedFontName) ? selectedFontName : null,
                fontItems,
                page,
                font =>
                {
                    if (font.InnerFont is not null)
                        TextClipFontRegistry.RegisterFontFace(font.InnerFont);
                    else if (!string.IsNullOrWhiteSpace(font.Path))
                        TextClipFontRegistry.AddFont(font.Path);

                    HandlePanelChange(styleProvider, new PropertyPanelPropertyChangedEventArgs("FontFamily", font.FontName, styleProvider.Parameters.TryGetValue("FontFamily", out var f) ? f : string.Empty));
                },
                TextServices.RenderFontPreviewAsync)
            )
            .AddFromAnother(providerPpb, styleProvider)
            .AddSeparator()
            .AddButton(PPLocalizedResources.TextOption_ChangeStyle, async (_, _) =>
            {
                var available = TextStyleServices.GetAvailableTextStyleProviders();
                if (available.Count == 0) return;

                var styleNames = available.Keys.ToArray();
                var currentStyle = styleProvider.TypeName;
                var picked = await page.DisplayActionSheetAsync(
                    PPLocalizedResources.TextOption_ChangeStyle, null, null, styleNames);

                if (string.IsNullOrWhiteSpace(picked) || picked == currentStyle) return;
                if (!available.TryGetValue(picked, out var factory)) return;

                var newProvider = factory();
                // 保留基础文本
                newProvider.BasicText = styleProvider.BasicText;
                // 保留布局模式和换行宽度
                newProvider.Parameters["LayoutMode"] = styleProvider.LayoutMode.ToString();
                if (styleProvider.Parameters.TryGetValue("WrappingWidth", out var ww) && !string.IsNullOrWhiteSpace(ww))
                    newProvider.Parameters["WrappingWidth"] = ww;
                if (styleProvider.Parameters.TryGetValue("TextStyleManualSize", out var ms) && !string.IsNullOrWhiteSpace(ms))
                    newProvider.Parameters["TextStyleManualSize"] = ms;

                // 测量新样式的自然尺寸
                var newEntries = newProvider.BuildEntries();
                var newRect = TextMeasureHelper.MeasureBounds(newEntries, 1920, 1080);
                var newW = Math.Max(1, (int)Math.Ceiling(newRect.Width));
                var newH = Math.Max(1, (int)Math.Ceiling(newRect.Height));

                // 持久化新样式到 clip
                clip.ExtraData ??= new();
                clip.ExtraData[TextStyleProviderFromKey] = newProvider.FromPlugin;
                clip.ExtraData[TextStyleProviderTypeKey] = newProvider.TypeName;
                clip.ExtraData[TextStyleProviderParamsKey] = new Dictionary<string, string>(newProvider.Parameters);
                if (newEntries.Length > 0)
                    clip.ExtraData["TextEntries"] = newEntries.ToList();

                clip.TargetWidth = newW;
                clip.TargetHeight = newH;
                clip.IsMoveable = true;
                clip.IsHorizontalResizable = newProvider.IsHorizontalResizable;
                clip.IsVerticalResizable = newProvider.IsVerticalResizable;
                clip.CanSnapWhileResizing = newProvider.CanSnapWhileResizing;

                handler?.Invoke(new(), new PropertyPanelPropertyChangedEventArgs("TextStyleChanged", newProvider, newProvider));
            })
            .AddButton(PPLocalizedResources.TextOption_ReLayout, (_, _) =>
            {
                // 清除所有布局约束，强制重新测量文本的自然尺寸
                styleProvider.Parameters.Remove("WrappingWidth");
                styleProvider.Parameters.Remove("TextStyleManualSize");
                styleProvider.Parameters.Remove("FixedHeightValue");
                styleProvider.LayoutMode = TextClipLayoutMode.FillClip;

                // 重新测量文本的自然宽高
                var resetEntries = styleProvider.BuildEntries();
                var resetRect = TextMeasureHelper.MeasureBounds(resetEntries, 1920, 1080);
                var resetW = Math.Max(1, (int)Math.Ceiling(resetRect.Width));
                var resetH = Math.Max(1, (int)Math.Ceiling(resetRect.Height));

                clip.TargetWidth = resetW;
                clip.TargetHeight = resetH;
                clip.IsMoveable = true;
                clip.IsHorizontalResizable = styleProvider.IsHorizontalResizable;
                clip.IsVerticalResizable = styleProvider.IsVerticalResizable;
                clip.CanSnapWhileResizing = styleProvider.CanSnapWhileResizing;

                clip.ExtraData ??= new();
                // 持久化更新后的参数
                clip.ExtraData[TextStyleProviderParamsKey] = new Dictionary<string, string>(styleProvider.Parameters);

                if (resetEntries.Length > 0)
                {
                    clip.ExtraData["TextEntries"] = resetEntries.ToList();
                    handler?.Invoke(new(), new PropertyPanelPropertyChangedEventArgs("TextEntries", resetEntries, resetEntries));
                }
            }, (c) => c.TextColor = Color.FromArgb("#FF8080"));

            void HandlePanelChange(object? s, PropertyPanelPropertyChangedEventArgs e)
            {
                if (s is not ITextClipStyleProvider provider) return;

                // 字体变更时，确保 FontFace 已在 TextClipFontRegistry 中注册，
                // 避免 TextLayoutPipeline.ResolveFont 回退到 fallback 字体导致测量异常。
                if (e.Id == "FontFamily")
                {
                    var fontName = e.Value?.ToString();
                    if (!string.IsNullOrWhiteSpace(fontName) && !TextClipFontRegistry.TryGetFont(fontName, out _))
                    {
                        if (TextServices.LoadedFonts.TryGetValue(fontName, out var fontItem))
                        {
                            if (fontItem.InnerFont is not null)
                                TextClipFontRegistry.RegisterFontFace(fontItem.InnerFont);
                            else if (!string.IsNullOrWhiteSpace(fontItem.Path))
                                TextClipFontRegistry.AddFont(fontItem.Path);
                        }
                    }
                }

                // Text changes are managed centrally by ClipInfoBuilder rather than
                // each individual style provider.
                if (e.Id == "Text")
                {
                    provider.BasicText = e.Value?.ToString() ?? string.Empty;
                    provider.Parameters["Text"] = provider.BasicText;
                }

                (var updated, var newW, var newH) = provider.HandlePropertyPanelChange(e);
                if (updated != null)
                {
                    provider.Parameters = updated;
                    clip.ExtraData[TextStyleProviderParamsKey] = updated;
                }
                clip.TargetWidth = newW > 0 ? newW : clip.TargetWidth;
                clip.TargetHeight = newH > 0 ? newH : clip.TargetHeight;
                clip.IsMoveable = true;
                clip.IsHorizontalResizable = provider.IsHorizontalResizable;
                clip.IsVerticalResizable = provider.IsVerticalResizable;
                clip.CanSnapWhileResizing = provider.CanSnapWhileResizing;
                var updatedEntries = provider.BuildEntries();
                if (updatedEntries.Length > 0)
                {
                    clip.ExtraData["TextEntries"] = updatedEntries.ToList();
                    handler?.Invoke(new(), new PropertyPanelPropertyChangedEventArgs("TextEntries", updatedEntries, updatedEntries));
                }
            }
            ;
            providerHost.PropertyChanged += (_, e) => HandlePanelChange(styleProvider, e);

            return providerHost.BuildWithScrollView();
        }

        private static void GetAndUpdateTextClipEntries(ClipElementUI clip, out string? providerFrom, out ITextClipStyleProvider? styleProvider)
        {
            clip.ExtraData ??= new Dictionary<string, object>();


            if (clip.ExtraData.TryGetValue("TextEntries", out var entriesObj) && entriesObj is JsonElement je)
            {
                try
                {
                    var deserialized = JsonSerializer.Deserialize<List<TextClipEntry>>(je);
                    if (deserialized is { Count: > 0 })
                        clip.ExtraData["TextEntries"] = TextEntryMigration.MigrateFromTextClipEntries(deserialized);
                }
                catch { }
            }

            string? ReadStringValue(object? raw)
            {
                if (raw is string s) return s;
                if (raw is JsonElement elem && elem.ValueKind == JsonValueKind.String) return elem.GetString();
                return raw?.ToString();
            }

            Dictionary<string, string>? ReadParameters(object? raw)
            {
                if (raw is Dictionary<string, string> dict) return new Dictionary<string, string>(dict);
                if (raw is Dictionary<string, object> objDict)
                    return objDict.ToDictionary(k => k.Key, v => v.Value?.ToString() ?? string.Empty);
                if (raw is JsonElement elem)
                {
                    try { return JsonSerializer.Deserialize<Dictionary<string, string>>(elem); }
                    catch { return null; }
                }
                if (raw is string json && !string.IsNullOrWhiteSpace(json))
                {
                    try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
                    catch { return null; }
                }
                return null;
            }

            providerFrom = clip.ExtraData.TryGetValue(TextStyleProviderFromKey, out var providerFromObj) ? ReadStringValue(providerFromObj) : null;
            var providerType = clip.ExtraData.TryGetValue(TextStyleProviderTypeKey, out var providerTypeObj) ? ReadStringValue(providerTypeObj) : null;
            var providerParameters = clip.ExtraData.TryGetValue(TextStyleProviderParamsKey, out var providerParamsObj) ? ReadParameters(providerParamsObj) : null;

            styleProvider = null;
            if (!string.IsNullOrWhiteSpace(providerFrom) && !string.IsNullOrWhiteSpace(providerType))
            {
                styleProvider = TextStyleServices.RestoreTextStyleProvider(providerFrom, providerType, providerParameters);
                if (styleProvider != null && providerParameters != null)
                {
                    styleProvider.Parameters = new Dictionary<string, string>(providerParameters);
                }

                var rebuiltEntries = styleProvider?.BuildEntries();
                if (rebuiltEntries is { Length: > 0 })
                {
                    clip.ExtraData["TextEntries"] = rebuiltEntries.ToList();
                    //handler?.Invoke(new(), new PropertyPanelPropertyChangedEventArgs("TextEntries", rebuiltEntries, rebuiltEntries));
                }

                if (styleProvider != null)
                {
                    clip.IsMoveable = true;
                    clip.IsHorizontalResizable = styleProvider.IsHorizontalResizable;
                    clip.IsVerticalResizable = styleProvider.IsVerticalResizable;
                    clip.CanSnapWhileResizing = styleProvider.CanSnapWhileResizing;
                }
            }
        }

        private View BuildTextOptionClassicTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            clip.ExtraData ??= new Dictionary<string, object>();

            List<TextClipEntry>? entries = null;
            if (clip.ExtraData.TryGetValue("TextEntries", out var entriesObj))
            {
                if (entriesObj is List<TextClipEntry> list)
                    entries = list;
                else if (entriesObj is JsonElement je)
                {
                    try { entries = JsonSerializer.Deserialize<List<TextClipEntry>>(je); }
                    catch { entries = null; }
                }
            }

            if (entries == null)
            {
                entries = new List<TextClipEntry>
                {
                    new TextClipEntry
                    {
                        text = "",
                        x = 0,
                        y = 0,
                        fontFamily = TextClipFontRegistry.GetAllFonts().FirstOrDefault()?.UniqueName ?? TextClipFontRegistry.FallbackFamilyName ?? "Arial",
                        fontSize = 24f,
                        r = 65535,
                        g = 65535,
                        b = 65535,
                        a = 1f
                    }
                };
                clip.ExtraData["TextEntries"] = entries;
            }

            var fonts = TextServices.LoadedFonts.Select(c => c.Value);
            var entriesContainer = new VerticalStackLayout { Spacing = 8 };

            entriesContainer.Add(new Label { Text = PPLocalizedResources.TextOption_TabTitle_Classic_Warn, TextColor = Colors.Yellow });

            void UpdateStoredEntries()
            {
                clip.ExtraData["TextEntries"] = entries;
                handler?.Invoke(new(), new PropertyPanelPropertyChangedEventArgs("TextEntries", entries, entries));
            }

            void RebuildEntriesUI()
            {
                entriesContainer.Children.Clear();
                for (int i = 0; i < entries.Count; i++)
                {
                    int idx = i;
                    var e = entries[idx];
                    var view = BuildTextEntryUI(e, idx, fonts,
                        (id, newE) => { entries[id] = newE; UpdateStoredEntries(); },
                        (id) => { entries.RemoveAt(id); UpdateStoredEntries(); RebuildEntriesUI(); },
                        entries.Count > 1,
                        false,
                        (pickerView) =>
                        {
                            page.Dispatcher.Dispatch(async () =>
                            {
                                await page.ShowAPopup(pickerView, mode: "dialog");
                            });
                        },
                        () =>
                        {
                            page.Dispatcher.Dispatch(async () =>
                            {
                                await page.HidePopup();
                            });
                        });
                    entriesContainer.Children.Add(view);
                }
            }

            RebuildEntriesUI();

            var addBtn = new Button
            {
                Text = PPLocalizedResources.TextOption_AddAEntry,
                HorizontalOptions = LayoutOptions.Fill,
                CornerRadius = 8,
                FontAttributes = FontAttributes.Bold,
                Margin = new Thickness(0, 4, 0, 0)
            };
            addBtn.Clicked += async (s, e) =>
            {
                Dictionary<string, TextClipEntry> t = new();
                Setting.SettingPages.EditSettingPage.LoadTextTemplates(ref t);
                var picked = await page.DisplayActionSheetAsync(PPLocalizedResources.TextOption_AddAEntry, null, null, t.Keys.ToArray());
                if (string.IsNullOrWhiteSpace(picked)) return;
                if (!t.TryGetValue(picked, out var value))
                {
                    value = new TextClipEntry
                    {
                        text = "",
                        x = 0,
                        y = 0,
                        fontFamily = TextClipFontRegistry.FallbackFamilyName ?? "Arial",
                        fontSize = 24f,
                        r = 65535,
                        g = 65535,
                        b = 65535,
                        a = 1f
                    };
                }
                entries.Add(value);
                UpdateStoredEntries();
                RebuildEntriesUI();
            };

            entriesContainer.Children.Add(addBtn);

            var grid = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Star)
                },
                Padding = 8
            };

            var scroll = new ScrollView
            {
                Content = entriesContainer,
                VerticalOptions = LayoutOptions.Start
            };
            grid.Add(scroll, 0, 1);

            return new ScrollView { Content = grid };
        }
        #endregion

        #region effect
        public static void RebuildAllEffects(ClipElementUI clip, bool diag = false)
        {
            var newEffects = new Dictionary<string, IEffect>();
            int globalIndex = 0;

            // Preserve manually-added effects (those without a BindedEffectGroupID) from the current Effects.
            if (clip.Effects != null)
            {
                foreach (var kvp in clip.Effects)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Value.BindedEffectGroupID))
                    {
                        newEffects[kvp.Key] = kvp.Value;
                        if (kvp.Value.Index >= globalIndex)
                            globalIndex = kvp.Value.Index + 1;
                    }
                }
            }

            var factories = EffectServices.GetAvailableEffectBundles();
            if (clip.EffectBundles != null)
            {
                NormalizeBundlePipeline(clip);
                var sortedBundles = SortEffectBundles(clip.EffectBundles);
                if (!sortedBundles.ListAny()) return;
                for (int i = 0; i < sortedBundles.Count; i++)
                {
                    var bundleData = sortedBundles[i];
                    bundleData.Parameters ??= new();
                }
                var bundleDict = sortedBundles.ToDictionary(b => b.Id, b => b);
                var bundleParams = sortedBundles.ToDictionary(b => b.Id, bundleData => EffectArgsHelper.ConvertElementDictToObjectDict(bundleData.Parameters.Where(c => !c.Key.StartsWith("__DraftEffectBindingView")).ToDictionary(c => c.Key, c => c.Value), bundleData.ParametersType));
                var bundleFacts = sortedBundles
                    .Where(c => c.Enabled)
                    .SelectMany(bundle => bundle.Create().Select(effectFactory => (bundleId: bundle.Id, effectFactory)))
                    .ToList();
                var autoImps = EffectFactoryExtensions.DetermineEffectImplementTypes(bundleFacts.Select(c => c.effectFactory).ToArray());
                var subIdxByBundle = new Dictionary<Guid, int>();

                for (int i = 0; i < bundleFacts.Count; i++)
                {
                    var bundleId = bundleFacts[i].bundleId;
                    var fact = bundleFacts[i].effectFactory;
                    var bundleData = bundleDict[bundleId];
                    var impType = ResolveConfiguredImplementType(fact, autoImps[i]);
                    IEffect effect;
                    if (fact is IBindableEffectFactory be)
                    {
                        effect = be.Build(impType, be.ID, be.BindedInputID, be.BindedInputIDs, bundleParams[bundleId]);
                    }
                    else
                    {
                        effect = fact.Build(impType, bundleParams[bundleId]);
                    }
                    int subIdx = subIdxByBundle.ContainsKey(bundleId) ? subIdxByBundle[bundleId] : 0;
                    subIdxByBundle[bundleId] = subIdx + 1;
                    effect.Name = $"EffectBundle {bundleData.TypeName}({bundleData.Id}){Environment.NewLine} - Subeffect #{subIdx}";
                    effect.Enabled = bundleData.Enabled && bundleData.BindedOutputId != IEffectBundle.NoConnectionGUID;
                    effect.Index = globalIndex++;
                    effect.BindedEffectGroupID = bundleData.Id.ToString();
                    string key = $"{bundleData.Id}_{subIdx}";
                    if (newEffects.TryGetValue(key, out var previousEffect))
                    {
                        if (effect.RelativeWidth <= 0 && previousEffect.RelativeWidth > 0)
                        {
                            effect.RelativeWidth = previousEffect.RelativeWidth;
                        }

                        if (effect.RelativeHeight <= 0 && previousEffect.RelativeHeight > 0)
                        {
                            effect.RelativeHeight = previousEffect.RelativeHeight;
                        }
                    }

                    if (effect is not IBindableArgumentEffect) effect.Id = Guid.NewGuid().ToString();
                    newEffects[key] = effect;
                }

            }
            clip.Effects = newEffects
                .Where(e => string.IsNullOrWhiteSpace(e.Value.BindedEffectGroupID)
                            || (clip.EffectBundles?.ContainsKey(Guid.TryParse(e.Value.BindedEffectGroupID, out var g) ? g : Guid.Empty) ?? false))
                .ToDictionary();
        }

        private static List<IEffectBundle> SortEffectBundles(IReadOnlyDictionary<Guid, IEffectBundle> bundles)
        {
            var ordered = bundles.ToList();
            var adjacency = new Dictionary<Guid, List<Guid>>();
            var incoming = new Dictionary<Guid, int>();

            foreach (var kvp in ordered)
            {
                adjacency[kvp.Key] = new List<Guid>();
                incoming[kvp.Key] = 0;
            }

            foreach (var kvp in ordered)
            {
                var bundle = kvp.Value;
                var bundleId = kvp.Key;

                foreach (var inputId in GetInputDependencyIds(bundle))
                {
                    if (!bundles.ContainsKey(inputId) || inputId == bundleId) continue;
                    adjacency[inputId].Add(bundleId);
                    incoming[bundleId]++;
                }

                var outputId = bundle.BindedOutputId;
                if (IsValidOutputDependency(outputId) && bundles.ContainsKey(outputId) && outputId != bundleId)
                {
                    adjacency[bundleId].Add(outputId);
                    incoming[outputId]++;
                }
            }

            var queue = new Queue<Guid>(ordered.Where(kvp => incoming[kvp.Key] == 0).Select(kvp => kvp.Key));
            var result = new List<IEffectBundle>(ordered.Count);
            var visited = new HashSet<Guid>();

            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (!visited.Add(id)) continue;
                result.Add(bundles[id]);

                foreach (var next in adjacency[id])
                {
                    incoming[next]--;
                    if (incoming[next] == 0) queue.Enqueue(next);
                }
            }

            if (result.Count < ordered.Count)
            {
                var cycleIds = ordered.Where(kvp => !visited.Contains(kvp.Key)).Select(kvp => kvp.Key);
                throw new InvalidOperationException($"Effect bundle graph has a cycle. Unresolved ids: {string.Join(", ", cycleIds)}");
            }

            return result;
        }

        private static IEnumerable<Guid> GetInputDependencyIds(IEffectBundle bundle)
        {
            if (bundle.InputAnchorsDisplayName is not null)
            {
                if (bundle.BindedInputIds is null) yield break;
                foreach (var id in bundle.BindedInputIds)
                {
                    if (IsValidInputDependency(id)) yield return id;
                }
                yield break;
            }

            if (IsValidInputDependency(bundle.BindedInputId))
            {
                yield return bundle.BindedInputId;
                yield break;
            }

            if (bundle.BindedInputIds is not null && bundle.BindedInputIds.Count > 0 && IsValidInputDependency(bundle.BindedInputIds[0]))
            {
                // DraftEffectBindingView may store single-input connections in BindedInputIds[0].
                yield return bundle.BindedInputIds[0];
            }
        }

        private static bool IsValidInputDependency(Guid id)
        {
            return id != IEffectBundle.NoConnectionGUID && id != IEffectBundle.InputAnchorGUID;
        }

        private static bool IsValidOutputDependency(Guid id)
        {
            return id != IEffectBundle.NoConnectionGUID && id != IEffectBundle.OutputAnchorGUID;
        }

        /// <summary>
        /// 将新添加的 EffectBundle 自动接入到输出链中：插在距离输出画面最近的同Target Bundle 与输出画面之间。
        /// </summary>
        private static void AutoConnectBundleToOutput(ClipElementUI clip, IEffectBundle newBundle)
        {
            clip.EffectBundles ??= new Dictionary<Guid, IEffectBundle>();
            var target = clip.GetEffectTarget();

            var lastBundle = clip.EffectBundles.Values
                .FirstOrDefault(b => b.BindedOutputId == IEffectBundle.OutputAnchorGUID
                                  && AreTargetsCompatible(b.Target, target)
                                  && b.Id != newBundle.Id);

            if (lastBundle != null)
            {
                lastBundle.BindedOutputId = newBundle.Id;
                newBundle.BindedInputId = lastBundle.Id;
                newBundle.BindedOutputId = IEffectBundle.OutputAnchorGUID;
            }
            else
            {
                newBundle.BindedInputId = IEffectBundle.InputAnchorGUID;
                newBundle.BindedOutputId = IEffectBundle.OutputAnchorGUID;
            }
        }

        /// <summary>
        /// 判断两个 EffectTarget 是否兼容（可连接）。
        /// </summary>
        private static bool AreTargetsCompatible(EffectTarget a, EffectTarget b)
        {
            var aBase = a & ~(EffectTarget.IsKeyFramed | EffectTarget.IsNotVisibleInEffectEditor | EffectTarget.IsNotVisibleInNewEffectSelector);
            var bBase = b & ~(EffectTarget.IsKeyFramed | EffectTarget.IsNotVisibleInEffectEditor | EffectTarget.IsNotVisibleInNewEffectSelector);
            if (aBase == EffectTarget.NotSpecified || bBase == EffectTarget.NotSpecified)
                return true;
            return (aBase & bBase) != 0;
        }

        /// <summary>
        /// 验证并修复所有 EffectBundle 的连接一致性：
        /// - 自身连接 → 断开
        /// - 单向连接（A→B 但 B 没有指回 A）→ 断开
        /// - 扇入（多个 bundle 的输入指向同一个 source）→ 只保留第一个
        /// </summary>
        private static void ValidateAndFixBundleConnections(ClipElementUI clip)
        {
            if (clip.EffectBundles == null || clip.EffectBundles.Count == 0) return;
            var bundles = clip.EffectBundles;

            foreach (var bundle in bundles.Values)
            {
                // 自身连接
                if (bundle.BindedInputId == bundle.Id)
                {
                    bundle.BindedInputId = IEffectBundle.NoConnectionGUID;
                    if (bundle.BindedInputIds is not null && bundle.BindedInputIds.Count > 0)
                        bundle.BindedInputIds[0] = IEffectBundle.NoConnectionGUID;
                }
                if (bundle.BindedOutputId == bundle.Id)
                {
                    bundle.BindedOutputId = IEffectBundle.NoConnectionGUID;
                }

                // 单输入：BindedInputId 指向的 bundle 必须将其 BindedOutputId 指回自己
                if (bundle.InputAnchorsDisplayName is null)
                {
                    if (IsValidInputDependency(bundle.BindedInputId))
                    {
                        if (!bundles.TryGetValue(bundle.BindedInputId, out var src) || src.BindedOutputId != bundle.Id)
                        {
                            bundle.BindedInputId = IEffectBundle.NoConnectionGUID;
                            if (bundle.BindedInputIds is not null && bundle.BindedInputIds.Count > 0)
                                bundle.BindedInputIds[0] = IEffectBundle.NoConnectionGUID;
                        }
                    }
                }

                // 多输入：逐个检查 BindedInputIds
                if (bundle.BindedInputIds is not null && bundle.InputAnchorsDisplayName is not null)
                {
                    for (int i = 0; i < bundle.BindedInputIds.Count; i++)
                    {
                        var id = bundle.BindedInputIds[i];
                        if (IsValidInputDependency(id))
                        {
                            if (!bundles.TryGetValue(id, out var src) || src.BindedOutputId != bundle.Id)
                                bundle.BindedInputIds[i] = IEffectBundle.NoConnectionGUID;
                        }
                    }
                }

                // BindedOutputId 指向的 bundle 必须将其 BindedInputId/BindedInputIds 指回自己
                if (IsValidOutputDependency(bundle.BindedOutputId))
                {
                    if (!bundles.TryGetValue(bundle.BindedOutputId, out var tgt))
                    {
                        bundle.BindedOutputId = IEffectBundle.NoConnectionGUID;
                    }
                    else
                    {
                        bool pointsBack = tgt.BindedInputId == bundle.Id
                            || (tgt.BindedInputIds?.Contains(bundle.Id) ?? false);
                        if (!pointsBack)
                            bundle.BindedOutputId = IEffectBundle.NoConnectionGUID;
                    }
                }
            }

            // 扇入修复：不允许两个 bundle 的 BindedInputId 指向同一个 source
            var usedOutputs = new Dictionary<Guid, Guid>();
            foreach (var bundle in bundles.Values)
            {
                if (bundle.InputAnchorsDisplayName is null)
                {
                    if (IsValidInputDependency(bundle.BindedInputId))
                    {
                        if (usedOutputs.TryGetValue(bundle.BindedInputId, out var firstConsumer))
                        {
                            bundle.BindedInputId = IEffectBundle.NoConnectionGUID;
                            if (bundle.BindedInputIds is not null && bundle.BindedInputIds.Count > 0)
                                bundle.BindedInputIds[0] = IEffectBundle.NoConnectionGUID;
                        }
                        else
                        {
                            usedOutputs[bundle.BindedInputId] = bundle.Id;
                        }
                    }
                }
                else if (bundle.BindedInputIds is not null)
                {
                    for (int i = 0; i < bundle.BindedInputIds.Count; i++)
                    {
                        var id = bundle.BindedInputIds[i];
                        if (IsValidInputDependency(id))
                        {
                            if (usedOutputs.TryGetValue(id, out var firstConsumer))
                                bundle.BindedInputIds[i] = IEffectBundle.NoConnectionGUID;
                            else
                                usedOutputs[id] = bundle.Id;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 规范化 Bundle 管线：若检测到并行链，将其合并为单链。
        /// 仅在确实存在并行链时才执行重排，避免覆盖用户已手动配置好的单链顺序。
        /// 内部 Effect（ColorAdjustment、Crop 等）排在前面，用户 Effect 排在后面。
        /// Mixture 和 SpeedVariance 断开连接（它们在渲染时由系统直接提取，不参与绑定管线）。
        /// </summary>
        private static void NormalizeBundlePipeline(ClipElementUI clip)
        {
            if (clip.EffectBundles == null || clip.EffectBundles.Count <= 1) return;
            var bundles = clip.EffectBundles;

            var pipelineBundles = new List<IEffectBundle>();
            var detachedBundles = new List<IEffectBundle>();

            foreach (var b in bundles.Values)
            {
                if (b.Target.HasFlag(EffectTarget.SpeedVariance) || b.Target.HasFlag(EffectTarget.Mixture))
                    detachedBundles.Add(b);
                else
                    pipelineBundles.Add(b);
            }

            foreach (var b in detachedBundles)
            {
                b.BindedInputId = IEffectBundle.NoConnectionGUID;
                b.BindedOutputId = IEffectBundle.NoConnectionGUID;
                if (b.BindedInputIds != null)
                {
                    for (int i = 0; i < b.BindedInputIds.Count; i++)
                        b.BindedInputIds[i] = IEffectBundle.NoConnectionGUID;
                }
            }

            if (pipelineBundles.Count <= 1) return;

            var directToInputCount = pipelineBundles.Count(b =>
                b.BindedInputId == IEffectBundle.InputAnchorGUID ||
                (b.BindedInputIds?.Contains(IEffectBundle.InputAnchorGUID) ?? false));

            if (directToInputCount <= 1) return;

            var sorted = pipelineBundles
                .OrderBy(b => b.Target.HasFlag(EffectTarget.ColorAdjustment) ? 0 : 1)
                .ThenBy(b => b.Target.HasFlag(EffectTarget.IsNotVisibleInEffectEditor) ? 0 : 1)
                .ToList();

            for (int i = 0; i < sorted.Count; i++)
            {
                var b = sorted[i];
                b.BindedInputId = i == 0 ? IEffectBundle.InputAnchorGUID : sorted[i - 1].Id;
                b.BindedOutputId = i == sorted.Count - 1 ? IEffectBundle.OutputAnchorGUID : sorted[i + 1].Id;

                b.BindedInputIds ??= new List<Guid>();
                if (b.BindedInputIds.Count == 0)
                    b.BindedInputIds.Add(b.BindedInputId);
                else
                    b.BindedInputIds[0] = b.BindedInputId;
            }
        }

        public async Task<View> BuildEffectTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            ArgumentNullException.ThrowIfNull(clip);
            PropertyPanelBuilder ppb = new();
            ppb.AddButton(PPLocalizedResources.EffectBind_Title, async (s, e) =>
            {
                try
                {
                    var bindView = new DraftEffectBindingView();
                    bindView.LoadClip(clip, page, showAllEffect);
                    // Sync effect changes back to the property panel in real time
                    bindView.EffectBundlesChanged += () =>
                    {
                        RebuildAllEffects(clip, false);
                        handler?.Invoke(ppb, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                    };
                    if (page.UseCompactLayout ?? DeviceInfo.Idiom == DeviceIdiom.Phone)
                    {
                        page.Popup.Content = bindView;
                    }
                    else
                    {
                        var v = new ApplicationAPIBase.Views.MultiWindowView.MultiWindowItem
                        {
                            Title = PPLocalizedResources.EffectBindView_Title(clip.DisplayName),
                            Content = bindView,
                            IsPopOutVisible = true
                        };
                        page.MainMultiWindowView.AddWindow(v);
                        v.Maximize();
                        v.CloseClicked += (s, e) => RebuildAllEffects(clip, false);
                    }
                }
                catch (Exception ex)
                {
                    Log(ex, $"Show effect binding view for {clip.DisplayName}", this);
                    await page.DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
                    if (Debugger.IsAttached) throw;
                }

            });
            var bundlesFactories = EffectServices.GetAvailableEffectBundles();
            var haveManySpeedVarianceProvider = (clip.EffectBundles?.Count(c => c.Value.TypeOfEffect.HasFlag(EffectType.SpeedVarianceProvider)) ?? 0) >= 2;
            var haveManyMixtureProvider = (clip.EffectBundles?.Count(c => c.Value.TypeOfEffect.HasFlag(EffectType.MixtureProvider)) ?? 0) >= 2;
            if (clip.EffectBundles != null)
            {
                var filteredBundles = clip.EffectBundles
                    .Where(c =>
                        showAllEffect
                        || (!c.Value.Target.HasFlag(EffectTarget.IsNotVisibleInEffectEditor)
                             && c.Value.Target.HasFlag(clip.GetEffectTarget()))
                        || (c.Value.Target == EffectTarget.SpeedVariance && haveManySpeedVarianceProvider)
                        || (c.Value.Target == EffectTarget.Mixture && haveManyMixtureProvider))
                    .ToList();

                // Sort bundles in input→output order by traversing the connection chain
                var sortedBundles = new List<KeyValuePair<Guid, IEffectBundle>>();
                var visitedIds = new HashSet<Guid>();
                var traverseQueue = new Queue<Guid>();
                foreach (var b in filteredBundles)
                {
                    if (b.Value.BindedInputId == IEffectBundle.InputAnchorGUID || (b.Value.BindedInputIds?.Contains(IEffectBundle.InputAnchorGUID) ?? false))
                    {
                        traverseQueue.Enqueue(b.Key);
                    }
                }
                while (traverseQueue.Count > 0)
                {
                    var id = traverseQueue.Dequeue();
                    if (!visitedIds.Add(id)) continue;
                    var bundleKvp = filteredBundles.First(b => b.Key == id);
                    sortedBundles.Add(bundleKvp);
                    foreach (var b in filteredBundles)
                    {
                        if ((b.Value.BindedInputId == id || (b.Value.BindedInputIds?.Contains(id) ?? false)) && !visitedIds.Contains(b.Key))
                        {
                            traverseQueue.Enqueue(b.Key);
                        }
                    }
                }
                // Append any remaining bundles not connected to the main chain
                foreach (var b in filteredBundles)
                {
                    if (!visitedIds.Contains(b.Key))
                        sortedBundles.Add(b);
                }

                foreach (var bundleKvp in sortedBundles)
                {
                    var bundleId = bundleKvp.Key;
                    var bundleInstance = bundleKvp.Value;
                    var locedName = EffectServices.GetLocalizedEffectBundleNames()[bundleInstance.TypeName];
                    if (string.IsNullOrWhiteSpace(bundleInstance.Name)) bundleInstance.Name = locedName;

                    string GetInputAnchorSelection(Guid id)
                    {
                        if (id == IEffectBundle.NoConnectionGUID) return PPLocalizedResources.EffectBind_NoConnection;
                        if (id == IEffectBundle.InputAnchorGUID) return PPLocalizedResources.EffectBind_SourcePicture;
                        if (clip.EffectBundles != null && clip.EffectBundles.TryGetValue(id, out var b))
                        {
                            if (!showAllEffect && b.Target.HasFlag(EffectTarget.IsNotVisibleInEffectEditor))
                                return GetInputAnchorSelection(b.BindedInputId);
                            return $"{b.Name} ({b.Id})";
                        }
                        return string.Empty;
                    }

                    string GetOutputAnchorSelection(Guid id)
                    {
                        if (id == IEffectBundle.NoConnectionGUID) return PPLocalizedResources.EffectBind_NoConnection;
                        if (id == IEffectBundle.OutputAnchorGUID) return PPLocalizedResources.EffectBind_FinalResult;
                        if (clip.EffectBundles != null && clip.EffectBundles.TryGetValue(id, out var b))
                        {
                            if (!showAllEffect && b.Target.HasFlag(EffectTarget.IsNotVisibleInEffectEditor))
                                return GetOutputAnchorSelection(b.BindedOutputId);
                            return $"{b.TypeName} ({b.Id})";
                        }
                        return string.Empty;
                    }

                    try
                    {

                        var bundlePpb = bundleInstance.CreateUI();

                        ppb.AddText(new TitleAndDescriptionLineLabel(bundleInstance.Name ?? bundleInstance.TypeName, bundleInstance.TypeName));
                        ppb.AddCheckbox($"Bundle|{bundleId}|Enabled", PPLocalizedResources._Enabled, bundleInstance.Enabled);
                        ppb.AddEntry($"Bundle|{bundleId}|Name", "Name", bundleInstance.Name ?? locedName, locedName);

                        if (!bundleInstance.Target.HasFlag(EffectTarget.IsKeyFramed))
                        {
                            ppb.AddSeparator();

                            ppb.AddFromAnother(bundlePpb, bundleInstance);
                        }

                        ppb.AddSeparator();

                        // 计算当前输入锚点选中项（用于 Picker 默认值和确保当前选中项不被过滤掉）
                        Guid resolvedInAnchorId;
                        if (bundleInstance.InputAnchorsDisplayName is null)
                        {
                            resolvedInAnchorId = bundleInstance.BindedInputId;
                            if (resolvedInAnchorId == IEffectBundle.NoConnectionGUID && bundleInstance.BindedInputIds is not null && bundleInstance.BindedInputIds.Count > 0)
                                resolvedInAnchorId = bundleInstance.BindedInputIds[0];
                        }
                        else
                        {
                            resolvedInAnchorId = bundleInstance.BindedInputId;
                        }

                        // 构建过滤后的 InAnchor 下拉选项：排除自身、类型不兼容和（showAllEffect=false 时）内部 bundle
                        var inAnchorBundleOptions = clip.EffectBundles
                            .Where(b => b.Key != bundleId
                                && AreTargetsCompatible(b.Value.Target, bundleInstance.Target)
                                && (showAllEffect || !b.Value.Target.HasFlag(EffectTarget.IsNotVisibleInEffectEditor)))
                            .Select(b => $"{b.Value.Name} ({b.Key})")
                            .ToList();
                        var curIn = GetInputAnchorSelection(resolvedInAnchorId);
                        if (curIn is not null && !inAnchorBundleOptions.Contains(curIn))
                            inAnchorBundleOptions.Add(curIn);

                        if (bundleInstance.InputAnchorsDisplayName is null)
                        {
                            ppb.AddPicker($"Bundle|{bundleId}|InAnchor", string.IsNullOrWhiteSpace(bundleInstance.InputAnchorDisplayName) ? PPLocalizedResources.EffectBind_InputAnchor : PPLocalizedResources.EffectBind_InputAnchorWithName(bundleInstance.InputAnchorDisplayName), inAnchorBundleOptions.Append(PPLocalizedResources.EffectBind_SourcePicture).Append(PPLocalizedResources.EffectBind_NoConnection).ToArray(), GetInputAnchorSelection(resolvedInAnchorId));
                        }
                        else
                        {
                            foreach (var item in bundleInstance.InputAnchorsDisplayName)
                            {
                                var idx = Array.IndexOf(bundleInstance.InputAnchorsDisplayName, item);
                                var currentId = (bundleInstance.BindedInputIds != null && idx >= 0 && idx < bundleInstance.BindedInputIds.Count)
                                    ? bundleInstance.BindedInputIds[idx]
                                    : IEffectBundle.NoConnectionGUID;
                                ppb.AddPicker($"Bundle|{bundleId}|InAnchors|{item}", string.IsNullOrWhiteSpace(item) ? PPLocalizedResources.EffectBind_InputAnchor : PPLocalizedResources.EffectBind_InputAnchorWithName(item), inAnchorBundleOptions.Append(PPLocalizedResources.EffectBind_SourcePicture).Append(PPLocalizedResources.EffectBind_NoConnection).Distinct(StringComparer.InvariantCultureIgnoreCase).ToArray(), GetInputAnchorSelection(currentId));

                            }
                        }

                        // 构建过滤后的 OutAnchor 下拉选项：排除自身、类型不兼容和（showAllEffect=false 时）内部 bundle
                        var outTargetBundleOptions = clip.EffectBundles
                            .Where(b => b.Key != bundleId
                                && AreTargetsCompatible(b.Value.Target, bundleInstance.Target)
                                && (showAllEffect || !b.Value.Target.HasFlag(EffectTarget.IsNotVisibleInEffectEditor)))
                            .Select(b => $"{b.Value.TypeName} ({b.Key})")
                            .ToList();
                        var curOut = GetOutputAnchorSelection(bundleInstance.BindedOutputId);
                        if (curOut is not null && !outTargetBundleOptions.Contains(curOut))
                            outTargetBundleOptions.Add(curOut);

                        ppb.AddPicker($"Bundle|{bundleId}|OutAnchor", string.IsNullOrWhiteSpace(bundleInstance.OutputAnchorDisplayName) ? PPLocalizedResources.EffectBind_OutputAnchor : PPLocalizedResources.EffectBind_OutputAnchorWithName(bundleInstance.OutputAnchorDisplayName), outTargetBundleOptions.Append(PPLocalizedResources.EffectBind_FinalResult).Append(PPLocalizedResources.EffectBind_NoConnection).Distinct(StringComparer.InvariantCultureIgnoreCase).ToArray(), GetOutputAnchorSelection(bundleInstance.BindedOutputId));

                        ppb.AddButton($"Bundle|{bundleId}|Remove", PPLocalizedResources.EffectProp_Remove);
                        ppb.AddSeparator();
                    }
                    catch (Exception ex)
                    {
                        if (Debugger.IsAttached)
                        {
                            if (Microsoft.Maui.Controls.Application.Current?.Windows?.First()?.Page is Page page)
                            {
                                if (await page.DisplayAlertAsync(Localized._Error, $"Error loading bundle {bundleInstance.TypeName}: {ex.Message}", "Throw", Localized._OK)) throw;
                            }
                        }
                        Log(ex, $"loading bundle {bundleInstance.TypeName}", this);
                        ppb.AddText(new Label { Text = $"Error loading bundle {bundleInstance.TypeName}: {ex.Message}", TextColor = Colors.Yellow });
                        ppb.AddSeparator();
                    }
                }
            }

            ppb.AddText(new SingleLineLabel(PPLocalizedResources.Effect_Add_Title, 20));
            ppb.AddCustomChild(BuildAddEffectPanel(clip.GetEffectTarget(), page, bundlesFactories, ppb, handler, hideKeyFramedBundles: true));

            static bool TryParseAnchorSelection(string? selection, string anchorLabel, Guid anchorGuid, out Guid id)
            {
                if (string.IsNullOrWhiteSpace(selection))
                {
                    id = IEffectBundle.NoConnectionGUID;
                    return false;
                }

                if (selection == PPLocalizedResources.EffectBind_NoConnection)
                {
                    id = IEffectBundle.NoConnectionGUID;
                    return true;
                }

                if (selection == anchorLabel)
                {
                    id = anchorGuid;
                    return true;
                }

                var open = selection.LastIndexOf('(');
                var close = selection.LastIndexOf(')');
                if (open >= 0 && close > open)
                {
                    var guidText = selection.Substring(open + 1, close - open - 1);
                    if (Guid.TryParse(guidText, out var parsed))
                    {
                        id = parsed;
                        return true;
                    }
                }

                id = IEffectBundle.NoConnectionGUID;
                return false;
            }

            ppb.PropertyChanged += (s, e) =>
            {
                ArgumentNullException.ThrowIfNull(clip);
                clip.EffectBundles ??= new();

                if (!ppb.Equals(s)) //from another
                {
                    if (s is IEffectBundle eb)
                    {
                        var data = eb.HandlePropertyPanelChange(e);
                        IEffectBundle? bundle = null;
                        if (data != null)
                        {
                            if (!clip?.EffectBundles?.TryGetValue(eb.Id, out bundle) ?? false) throw new KeyNotFoundException($"Effect bundle with ID {eb.Id} not found in clip.");
                            if (bundle is null) throw new KeyNotFoundException($"Effect bundle with ID {eb.Id} not found in clip.");
                            bundle.Parameters = data;

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
                            Guid bundleId = new(parts[1]);
                            string action = parts[2];
                            if (!clip.EffectBundles?.ContainsKey(bundleId) ?? false) return;

                            switch (action)
                            {
                                case "Remove":
                                    clip.EffectBundles?.Remove(bundleId);
                                    RebuildAllEffects(clip);
                                    handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                    break;
                                case "Name":
                                    if (clip.EffectBundles.TryGetValue(bundleId, out var nameBundle))
                                    {
                                        var locedName = EffectServices.GetLocalizedEffectBundleNames().GetValueOrDefault(nameBundle.TypeName, nameBundle.TypeName);
                                        var newName = e.Value?.ToString();
                                        nameBundle.Name = string.IsNullOrWhiteSpace(newName) ? locedName : newName;
                                        handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                    }
                                    break;
                                case "Enabled":
                                    if (clip.EffectBundles.TryGetValue(bundleId, out var enabledBundle))
                                    {
                                        if (bool.TryParse(e.Value?.ToString(), out var enabled))
                                        {
                                            clip.EffectBundles[bundleId].Enabled = enabled;
                                            RebuildAllEffects(clip);
                                            handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                        }
                                    }
                                    break;
                                case "InAnchor":
                                    if (clip.EffectBundles.TryGetValue(bundleId, out var inBundle))
                                    {
                                        if (TryParseAnchorSelection(e.Value?.ToString(), PPLocalizedResources.EffectBind_SourcePicture, IEffectBundle.InputAnchorGUID, out var newSourceId))
                                        {
                                            {
                                                var oldSourceId = inBundle.BindedInputId;

                                                if (IsValidInputDependency(oldSourceId) && oldSourceId != newSourceId)
                                                {
                                                    if (clip.EffectBundles.TryGetValue(oldSourceId, out var oldSource))
                                                    {
                                                        if (oldSource.BindedOutputId == inBundle.Id)
                                                            oldSource.BindedOutputId = IEffectBundle.NoConnectionGUID;
                                                    }
                                                }

                                                inBundle.BindedInputId = newSourceId;
                                                if (inBundle.InputAnchorsDisplayName is null)
                                                {
                                                    inBundle.BindedInputIds ??= new List<Guid>();
                                                    if (inBundle.BindedInputIds.Count == 0)
                                                        inBundle.BindedInputIds.Add(newSourceId);
                                                    else
                                                        inBundle.BindedInputIds[0] = newSourceId;
                                                }

                                                if (IsValidInputDependency(newSourceId))
                                                {
                                                    if (clip.EffectBundles.TryGetValue(newSourceId, out var newSource))
                                                    {
                                                        if (newSource.BindedOutputId != IEffectBundle.NoConnectionGUID &&
                                                            newSource.BindedOutputId != IEffectBundle.OutputAnchorGUID &&
                                                            newSource.BindedOutputId != inBundle.Id)
                                                        {
                                                            if (clip.EffectBundles.TryGetValue(newSource.BindedOutputId, out var conflictedTarget))
                                                            {
                                                                if (conflictedTarget.BindedInputId == newSourceId)
                                                                    conflictedTarget.BindedInputId = IEffectBundle.NoConnectionGUID;
                                                                if (conflictedTarget.BindedInputIds is not null)
                                                                {
                                                                    for (int ci = 0; ci < conflictedTarget.BindedInputIds.Count; ci++)
                                                                    {
                                                                        if (conflictedTarget.BindedInputIds[ci] == newSourceId)
                                                                            conflictedTarget.BindedInputIds[ci] = IEffectBundle.NoConnectionGUID;
                                                                    }
                                                                }
                                                            }
                                                        }
                                                        newSource.BindedOutputId = inBundle.Id;
                                                    }
                                                }

                                                ValidateAndFixBundleConnections(clip);
                                                RebuildAllEffects(clip);
                                                handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                            }
                                        }
                                    }
                                    break;
                                case "InAnchors":
                                    if (clip.EffectBundles.TryGetValue(bundleId, out var insBundle))
                                    {
                                        if (parts.Length >= 4 && insBundle.InputAnchorsDisplayName is not null)
                                        {
                                            var idx = Array.IndexOf(insBundle.InputAnchorsDisplayName, parts[3]);
                                            if (idx >= 0)
                                            {
                                                if (TryParseAnchorSelection(e.Value?.ToString(), PPLocalizedResources.EffectBind_SourcePicture, IEffectBundle.InputAnchorGUID, out var newSourceId))
                                                {
                                                    if (insBundle.BindedInputIds is null || insBundle.BindedInputIds.Count != insBundle.InputAnchorsDisplayName.Length)
                                                    {
                                                        insBundle.BindedInputIds = Enumerable.Repeat(IEffectBundle.NoConnectionGUID, insBundle.InputAnchorsDisplayName.Length).ToList();
                                                    }

                                                    // 1. 断开该端口的旧源端
                                                    var oldSourceId = insBundle.BindedInputIds[idx];
                                                    if (IsValidInputDependency(oldSourceId) && oldSourceId != newSourceId)
                                                    {
                                                        if (clip.EffectBundles.TryGetValue(oldSourceId, out var oldSource))
                                                        {
                                                            if (oldSource.BindedOutputId == insBundle.Id)
                                                                oldSource.BindedOutputId = IEffectBundle.NoConnectionGUID;
                                                        }
                                                    }

                                                    // 2. 设置新连接
                                                    insBundle.BindedInputIds[idx] = newSourceId;
                                                    if (insBundle.InputAnchorsDisplayName.Length == 1 && idx == 0)
                                                        insBundle.BindedInputId = newSourceId;

                                                    // 3. 如果新源端是有效的 bundle，将其 BindedOutputId 指向当前 bundle
                                                    if (IsValidInputDependency(newSourceId))
                                                    {
                                                        if (clip.EffectBundles.TryGetValue(newSourceId, out var newSource))
                                                        {
                                                            if (newSource.BindedOutputId != IEffectBundle.NoConnectionGUID &&
                                                                newSource.BindedOutputId != IEffectBundle.OutputAnchorGUID &&
                                                                newSource.BindedOutputId != insBundle.Id)
                                                            {
                                                                if (clip.EffectBundles.TryGetValue(newSource.BindedOutputId, out var conflictedTarget))
                                                                {
                                                                    if (conflictedTarget.BindedInputId == newSourceId)
                                                                        conflictedTarget.BindedInputId = IEffectBundle.NoConnectionGUID;
                                                                    if (conflictedTarget.BindedInputIds is not null)
                                                                    {
                                                                        for (int ci = 0; ci < conflictedTarget.BindedInputIds.Count; ci++)
                                                                        {
                                                                            if (conflictedTarget.BindedInputIds[ci] == newSourceId)
                                                                                conflictedTarget.BindedInputIds[ci] = IEffectBundle.NoConnectionGUID;
                                                                        }
                                                                    }
                                                                }
                                                            }
                                                            newSource.BindedOutputId = insBundle.Id;
                                                        }
                                                    }

                                                    ValidateAndFixBundleConnections(clip);
                                                    RebuildAllEffects(clip);
                                                    handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                                }
                                            }
                                        }
                                    }
                                    break;
                                case "OutAnchor":
                                    if (clip.EffectBundles.TryGetValue(bundleId, out var outBundle))
                                    {
                                        if (TryParseAnchorSelection(e.Value?.ToString(), PPLocalizedResources.EffectBind_FinalResult, IEffectBundle.OutputAnchorGUID, out var newTargetId))
                                        {
                                            // 智能插入：当 showAllEffect=false 且用户选择"输出画面"时，
                                            // 将当前 Effect 插入到链尾（lastBundle→Final 变为 lastBundle→thisBundle→Final），
                                            // 避免绕过隐藏的内部 Effect。
                                            bool outAnchorHandled = false;
                                            if (newTargetId == IEffectBundle.OutputAnchorGUID && !showAllEffect)
                                            {
                                                var lastBundle = clip.EffectBundles?.Values
                                                    .FirstOrDefault(b => b.Id != outBundle.Id
                                                        && b.BindedOutputId == IEffectBundle.OutputAnchorGUID
                                                        && AreTargetsCompatible(b.Target, outBundle.Target));
                                                if (lastBundle != null)
                                                {
                                                    // a. 断开旧目标端
                                                    var oldTargetId = outBundle.BindedOutputId;
                                                    if (IsValidOutputDependency(oldTargetId) && oldTargetId != newTargetId)
                                                    {
                                                        if (clip.EffectBundles.TryGetValue(oldTargetId, out var oldTarget))
                                                        {
                                                            if (oldTarget.BindedInputId == outBundle.Id)
                                                                oldTarget.BindedInputId = IEffectBundle.NoConnectionGUID;
                                                            if (oldTarget.BindedInputIds is not null)
                                                            {
                                                                for (int ci = 0; ci < oldTarget.BindedInputIds.Count; ci++)
                                                                {
                                                                    if (oldTarget.BindedInputIds[ci] == outBundle.Id)
                                                                        oldTarget.BindedInputIds[ci] = IEffectBundle.NoConnectionGUID;
                                                                }
                                                            }
                                                        }
                                                    }

                                                    // b. lastBundle 的输出重定向到 outBundle
                                                    lastBundle.BindedOutputId = outBundle.Id;

                                                    // c. outBundle 从 lastBundle 接收，输出到 Final
                                                    outBundle.BindedInputId = lastBundle.Id;
                                                    outBundle.BindedOutputId = IEffectBundle.OutputAnchorGUID;
                                                    outBundle.BindedInputIds ??= new List<Guid>();
                                                    if (outBundle.BindedInputIds.Count == 0)
                                                        outBundle.BindedInputIds.Add(lastBundle.Id);
                                                    else
                                                        outBundle.BindedInputIds[0] = lastBundle.Id;

                                                    ValidateAndFixBundleConnections(clip);
                                                    RebuildAllEffects(clip);
                                                    handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                                    outAnchorHandled = true;
                                                }
                                            }

                                            if (!outAnchorHandled)
                                            {
                                                var oldTargetId = outBundle.BindedOutputId;

                                                // 1. 断开旧目标端的输入指向
                                                if (IsValidOutputDependency(oldTargetId) && oldTargetId != newTargetId)
                                                {
                                                    if (clip.EffectBundles.TryGetValue(oldTargetId, out var oldTarget))
                                                    {
                                                        if (oldTarget.BindedInputId == outBundle.Id)
                                                            oldTarget.BindedInputId = IEffectBundle.NoConnectionGUID;
                                                        if (oldTarget.BindedInputIds is not null)
                                                        {
                                                            for (int ci = 0; ci < oldTarget.BindedInputIds.Count; ci++)
                                                            {
                                                                if (oldTarget.BindedInputIds[ci] == outBundle.Id)
                                                                    oldTarget.BindedInputIds[ci] = IEffectBundle.NoConnectionGUID;
                                                            }
                                                        }
                                                    }
                                                }

                                                // 2. 设置新输出端
                                                outBundle.BindedOutputId = newTargetId;

                                                // 3. 如果新目标是有效的 bundle，将其 BindedInputId 指向当前 bundle
                                                if (IsValidOutputDependency(newTargetId))
                                                {
                                                    if (clip.EffectBundles.TryGetValue(newTargetId, out var newTarget))
                                                    {
                                                        if (newTarget.BindedInputId != IEffectBundle.NoConnectionGUID &&
                                                            newTarget.BindedInputId != IEffectBundle.InputAnchorGUID &&
                                                            newTarget.BindedInputId != outBundle.Id)
                                                        {
                                                            if (clip.EffectBundles.TryGetValue(newTarget.BindedInputId, out var conflictedSource))
                                                            {
                                                                if (conflictedSource.BindedOutputId == newTargetId)
                                                                    conflictedSource.BindedOutputId = IEffectBundle.NoConnectionGUID;
                                                            }
                                                        }
                                                        newTarget.BindedInputId = outBundle.Id;
                                                        if (newTarget.BindedInputIds is null || newTarget.BindedInputIds.Count == 0)
                                                            newTarget.BindedInputIds = [outBundle.Id];
                                                        else if (newTarget.BindedInputIds[0] == IEffectBundle.NoConnectionGUID
                                                                 || newTarget.InputAnchorsDisplayName is null)
                                                            newTarget.BindedInputIds[0] = outBundle.Id;
                                                    }
                                                }

                                                ValidateAndFixBundleConnections(clip);
                                                RebuildAllEffects(clip);
                                                handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                            }
                                        }
                                    }
                                    break;
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
                                instance.Id = Guid.NewGuid();
                                instance.BindedInputId = IEffectBundle.NoConnectionGUID;
                                instance.BindedOutputId = IEffectBundle.NoConnectionGUID;
                                clip.EffectBundles ??= new Dictionary<Guid, IEffectBundle>();
                                clip.EffectBundles[instance.Id] = instance;
                                AutoConnectBundleToOutput(clip, instance);

                                RebuildAllEffects(clip);
                                handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                            }
                        }
                    }
                }
            };
            ppb.AppendWhen(SettingsManager.IsBoolSettingTrue("DeveloperMode"),
                  p => p.AddSeparator()
                        .AddButton(PPLocalizedResources.EffectTab_ShowAll, (s, e) =>
                        {
                            showAllEffect = !showAllEffect;
                            handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                        })
                        .AddButton(PPLocalizedResources.EffectTab_Rebuild, async (s, e) =>
                        {
                            try
                            {
                                RebuildAllEffects(clip, true);
                                await page.DisplayAlertAsync(Localized._Info, SettingsManager.SettingLocalizedResources.Advanced_Success, Localized._OK);

                            }
                            catch (Exception ex)
                            {
                                if (await page.DisplayAlertAsync("Error", Localized._ExceptionTemplate(ex), "Throw", Localized._OK)) throw;
                            }
                        }));

            var panel = ppb.BuildWithScrollView();
            return panel;
        }


        private sealed class EffectBundleCardItem
        {
            public required string BundleTypeName { get; init; }
            public required string Title { get; init; }
            public required string Description { get; init; }
            public ImageSource? Thumbnail { get; init; }
            public MediaSource? VideoThumbnail { get; init; }
            public required string EffectTypeName { get; init; }
        }

        private static string GetEffectTypeName(EffectTarget target)
        {
            StringBuilder result = new();
            if (target.HasFlag(EffectTarget.Video)) result.Append("视频");
            if (target.HasFlag(EffectTarget.Audio)) result.Append("音频");
            if (target.HasFlag(EffectTarget.SpeedVariance)) result.Append("变速");
            if (target.HasFlag(EffectTarget.Mixture)) result.Append("混合");
            if (target.HasFlag(EffectTarget.ColorAdjustment)) result.Append("调色");
            if (target.HasFlag(EffectTarget.IsKeyFramed)) result.Append(" | 关键帧");
            return result.ToString();
        }

        public static View BuildAddEffectPanel(
            EffectTarget target,
            Page page,
            Dictionary<string, Func<IEffectBundle>> bundlesFactories,
            PropertyPanelBuilder ppb,
            EventHandler<PropertyPanelPropertyChangedEventArgs> handler,
            bool showSubfix = true,
            bool ignoreIsNotVisibleInNewEffectSelector = false,
            bool hideKeyFramedBundles = false)
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
                        Text = PPLocalizedResources.Add_Effect_None,
                        Opacity = 0.7,
                        FontSize = 13
                    }
                };
            }


            void AddBundle(string bundleTypeName)
            {
                ppb.Properties["NewBundleType"] = bundleTypeName;
                PropertyPanelPropertyChangedEventArgs.CreateAndInvoke(ppb, "AddBundle", bundleTypeName);
                handler?.Invoke(ppb, new PropertyPanelPropertyChangedEventArgs("AddBundle", bundleTypeName, bundleTypeName));
            }

            var cards = new List<EffectBundleCardItem>();
            foreach (var kvp in bundlesFactories
                                .Select(c => (c.Value(), c))
                                .Where(c =>
                                    target == EffectTarget.NotSpecified
                                    || target == EffectTarget.Text
                                       ? (c.Item1.Target.HasFlag(EffectTarget.Video) || c.Item1.Target.HasFlag(EffectTarget.Text))
                                       : c.Item1.Target.HasFlag(target)
                                    && (!c.Item1.Target.HasFlag(EffectTarget.IsNotVisibleInEffectEditor) || ignoreIsNotVisibleInNewEffectSelector)
                                    && (!hideKeyFramedBundles || (!c.Item1.Target.HasFlag(EffectTarget.IsKeyFramed) && c.Item1 is not IKeyFramedEffectProvider)))
                                .Select(c => c.c).OrderBy(k => k.Key))
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
                        Title = EffectServices.GetLocalizedEffectBundleNames(Environment.NewLine, showSubfix).GetValueOrDefault(bundleTypeName, bundleTypeName),
                        Description = display?.Description ?? "",
                        Thumbnail = display?.Thumbnail,
                        VideoThumbnail = display?.VideoThumbnail,
                        EffectTypeName = GetEffectTypeName(instance.Target),
                    });
                }
                catch
                {
                    cards.Add(new EffectBundleCardItem
                    {
                        BundleTypeName = bundleTypeName,
                        Title = bundleTypeName,
                        Description = "",
                        Thumbnail = null,
                        EffectTypeName = "?"
                    });
                }
            }

            const double cardWidth = 210;
            const double cardHeight = 160;
            const double cardMargin = 6;

            // ─── Collect unique categories for filter ───
            var allCategories = cards
                .Select(c => c.EffectTypeName)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct()
                .OrderBy(c => c)
                .ToList();

            var flex = new FlexLayout
            {
                Wrap = FlexWrap.Wrap,
                Direction = FlexDirection.Row,
                JustifyContent = FlexJustify.Start,
                AlignItems = FlexAlignItems.Start,
                AlignContent = FlexAlignContent.Start
            };

            // Filter state
            string? filterSearchText = null;
            string? filterCategory = null;

            void ApplyFilter()
            {
                IEnumerable<EffectBundleCardItem> filtered = cards;

                if (!string.IsNullOrWhiteSpace(filterSearchText))
                {
                    var lower = filterSearchText.ToLowerInvariant();
                    filtered = filtered.Where(c =>
                        c.Title.Contains(lower, StringComparison.OrdinalIgnoreCase) ||
                        c.Description.Contains(lower, StringComparison.OrdinalIgnoreCase) ||
                        c.BundleTypeName.Contains(lower, StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrWhiteSpace(filterCategory))
                {
                    filtered = filtered.Where(c => c.EffectTypeName == filterCategory);
                }

                BindableLayout.SetItemsSource(flex, filtered.ToList());
            }

            // ─── Search bar ───
            var searchBar = new SearchBar
            {
                Placeholder = PPLocalizedResources.Effect_Add_Search
            };
            searchBar.TextChanged += (_, e) =>
            {
                filterSearchText = e.NewTextValue;
                ApplyFilter();
            };

            // ─── Category filter picker ───
            var categoryPicker = new Picker
            {
                WidthRequest = 130
            };
            categoryPicker.Items.Add(PPLocalizedResources.Effect_Add_Search_Any);
            foreach (var cat in allCategories)
            {
                categoryPicker.Items.Add(cat);
            }
            categoryPicker.SelectedIndexChanged += (_, _) =>
            {
                filterCategory = categoryPicker.SelectedIndex <= 0 ? null : categoryPicker.Items[categoryPicker.SelectedIndex];
                ApplyFilter();
            };

            // ─── Filter bar ───
            var filterBar = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                },
                Margin = new Thickness(0, 0, 0, 8)
            };
            filterBar.Add(new Border
            {
                StrokeShape = new RoundRectangle { CornerRadius = 8 },
                Margin = new Thickness(0, 0, 8, 0),
                Content = searchBar
            }, 0, 0);
            filterBar.Add(categoryPicker, 1, 0);

            BindableLayout.SetItemsSource(flex, cards);
            BindableLayout.SetEmptyView(flex, new Label
            {
                Text = PPLocalizedResources.Add_Effect_None,
                FontSize = 18,
            });
            BindableLayout.SetItemTemplate(flex, new DataTemplate(() =>
            {
                // ─── Preview image ───
                var image = new Image
                {
                    Aspect = Aspect.AspectFill,
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Fill
                };
                image.SetBinding(Image.SourceProperty, nameof(EffectBundleCardItem.Thumbnail));

                // ─── Video preview (hidden by default) ───
                var mediaPlayer = new MediaElement
                {
                    Aspect = Aspect.AspectFill,
                    ShouldAutoPlay = true,
                    ShouldLoopPlayback = true,
                    ShouldMute = true,
                    ShouldShowPlaybackControls = false,
                    IsVisible = false,
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Fill
                };

                // ─── Effect type label (bottom-left overlay on thumbnail) ───
                var typeLabel = new Label
                {
                    FontSize = 11,
                    TextColor = Colors.White,
                    VerticalOptions = LayoutOptions.End,
                    HorizontalOptions = LayoutOptions.Start,
                    Margin = new Thickness(6, 0, 0, 6),
                    Padding = new Thickness(4, 2),
                    Background = new SolidColorBrush(Colors.Black.WithAlpha(0.5f)),
                };
                typeLabel.SetBinding(Label.TextProperty, nameof(EffectBundleCardItem.EffectTypeName));

                // ─── Effect name label (bottom-right overlay on thumbnail) ───
                var nameLabel = new Label
                {
                    FontSize = 11,
                    TextColor = Colors.White,
                    VerticalOptions = LayoutOptions.End,
                    HorizontalOptions = LayoutOptions.End,
                    Margin = new Thickness(0, 0, 6, 6),
                    Padding = new Thickness(4, 2),
                    Background = new SolidColorBrush(Colors.Black.WithAlpha(0.5f)),
                    LineBreakMode = LineBreakMode.TailTruncation,
                };
                nameLabel.SetBinding(Label.TextProperty, nameof(EffectBundleCardItem.Title));

                // ─── Preview area (fills card) ───
                var previewGrid = new Grid
                {
                    Children = { image, mediaPlayer, typeLabel, nameLabel }
                };

                // ─── Hover overlay (hidden by default, slides up from bottom) ───
                var hoverTitle = new Label
                {
                    FontSize = 13,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = Colors.White,
                    LineBreakMode = LineBreakMode.TailTruncation
                };
                hoverTitle.SetBinding(Label.TextProperty, nameof(EffectBundleCardItem.Title));

                var hoverDesc = new Label
                {
                    FontSize = 11,
                    TextColor = Colors.White.WithAlpha(0.85f),
                    LineBreakMode = LineBreakMode.TailTruncation,
                    MaxLines = 2
                };
                hoverDesc.SetBinding(Label.TextProperty, nameof(EffectBundleCardItem.Description));

                var hoverOverlay = new Border
                {
                    IsVisible = false,
                    VerticalOptions = LayoutOptions.End,
                    Background = new SolidColorBrush(Colors.Gray.WithAlpha(0.85f)),
                    Padding = new Thickness(8, 6),
                    StrokeThickness = 0,
                    Content = new VerticalStackLayout
                    {
                        Spacing = 2,
                        Children = { hoverTitle, hoverDesc }
                    }
                };

                // ─── Main card container ───
                var cardContent = new Grid
                {
                    Children = { previewGrid, hoverOverlay }
                };

                var border = new Border
                {
                    WidthRequest = cardWidth,
                    HeightRequest = cardHeight,
                    Margin = new Thickness(cardMargin),
                    Padding = 0,
                    StrokeShape = new RoundRectangle { CornerRadius = 12 },
                    Stroke = new SolidColorBrush(Colors.Gray.WithAlpha(0.25f)),
                    StrokeThickness = 1,
                    Background = new SolidColorBrush(Colors.Transparent),
                    Content = cardContent
                };

                // ─── Hover handling ───
                void OnHover(bool isHovered)
                {
                    if (border.BindingContext is EffectBundleCardItem item)
                    {
                        if (isHovered)
                        {
                            hoverOverlay.IsVisible = true;
                            if (item.VideoThumbnail is not null)
                            {
                                mediaPlayer.Source = item.VideoThumbnail;
                                mediaPlayer.IsVisible = true;
                                image.IsVisible = false;
                                mediaPlayer.Play();
                            }
                        }
                        else
                        {
                            hoverOverlay.IsVisible = false;
                            mediaPlayer.Pause();
                            mediaPlayer.Source = null;
                            mediaPlayer.IsVisible = false;
                            image.IsVisible = true;
                        }
                    }
                }

#if MACCATALYST || WINDOWS
                var pointerGesture = new PointerGestureRecognizer();
                pointerGesture.PointerEntered += (_, _) => OnHover(true);
                pointerGesture.PointerExited += (_, _) => OnHover(false);
                cardContent.GestureRecognizers.Add(pointerGesture);
#endif

                void SelectCard(Border selected)
                {
                    foreach (var child in flex.Children)
                    {
                        if (child is Border b)
                        {
                            bool isSelected = b == selected;
                            b.Stroke = new SolidColorBrush(isSelected ? Colors.DodgerBlue : Colors.Gray.WithAlpha(0.25f));
                            b.StrokeThickness = isSelected ? 2 : 1;
                            b.Background = new SolidColorBrush(isSelected ? Colors.DodgerBlue.WithAlpha(0.1f) : Colors.Transparent);
                        }
                    }
                }

                UIServices.RegisterSelectOrContextMenu(
                    border,
                    OnSelected: () =>
                    {
                        if (border.BindingContext is EffectBundleCardItem item)
                        {
                            ppb.Properties["NewBundleType"] = item.BundleTypeName;
                            SelectCard(border);
                        }
                    },
                    OnClicked: () =>
                    {
                        if (border.BindingContext is EffectBundleCardItem item)
                            AddBundle(item.BundleTypeName);
                    },
                    OnContextMenuClick: async () =>
                    {
                        if (border.BindingContext is not EffectBundleCardItem item) return;
                        var verbs = new[] { PPLocalizedResources.Add_Effect, Localized.AssetPage_ShowPreview };
                        int action = Array.IndexOf(verbs, await page.DisplayActionSheetAsync(item.Title, Localized._Cancel, null, verbs));
                        switch (action)
                        {
                            case 0:
                                AddBundle(item.BundleTypeName);
                                break;
                            case 1:
                                await page.DisplayAlertAsync(Localized._Info, item.Description, Localized._OK);
                                break;
                        }
                    }
                );

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
                    filterBar,
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
                    ppb.AddEntry($"Effect|{effectKey}|Index", PPLocalizedResources.EffectProp_Index, effect.Index.ToString(), "-1");
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
                    ppb.AddSeparator();
                    IEffectBundle? eb = null;
                    ppb.AddCustomChild("IEffect.ID", new Label { Text = effect.Id });
                    ppb.AddCustomChild("IEffect.TypeName", new Label { Text = effect.TypeName });
                    ppb.AddCustomChild("IEffect.TypeOfEffect", new Label { Text = effect.TypeOfEffect.ToString() });
                    ppb.AddCustomChild("IEffect.ImplementType", new Label { Text = effect.ImplementType.ToString() });
                    ppb.AppendWhen(
                        condition: (Guid.TryParse(effect.BindedEffectGroupID, out var g) && (clip.EffectBundles?.TryGetValue(g, out eb) ?? false) && eb is not null),
                        onTrue: c => c.AddCustomChild("Binded IEffectBundle", new Label { Text = $"{eb.Name} ({effect.BindedEffectGroupID})" })
                                      .AddCustomChild("IEffectBundle.EffectTarget", new Label { Text = eb?.Target is not null ? eb.Target.ToString() : "No bundle" }),
                        onFalse: c => c.AddCustomChild("Binded IEffectBundle", new Label { Text = $"Unknown bundle '{effect.BindedEffectGroupID}'" }));
                    if (effect is IBindableArgumentEffect be)
                    {
                        ppb.AddSeparator();
                        ppb.AddText("IBindableArgumentEffect effect prop:");
                        switch (be.EffectRole)
                        {
                            case BindableArgumentEffectType.ValueProvider:
                                ppb.AddCustomChild("Output anchor name", new Label { Text = (be as IBindableArgumentEffectValueProvider)?.OutputAnchorName ?? "none" });
                                break;
                            case BindableArgumentEffectType.OneInputValueProcessor:
                                ppb.AddCustomChild($"Input anchor {(be as IBindableArgumentEffectOneInputResultGenerator)?.InputAnchorName ?? "unknown"}", new Label { Text = $"{be.BindedArgumentProviderID} " });
                                ppb.AddCustomChild("Output anchor name", new Label { Text = (be as IBindableArgumentEffectOneInputResultGenerator)?.OutputAnchorName ?? "none" });
                                break;
                            case BindableArgumentEffectType.ManyInputValueProcessor:
                                if (be is IBindableArgumentEffectManyToOneValueProcesser mpe)
                                {
                                    foreach (var item in mpe.BindedArgumentProviderIDs)
                                    {
                                        var idx = mpe.BindedArgumentProviderIDs.IndexOf(item);
                                        string inAnchorName = "unknown";
                                        if (idx >= 0 && mpe.InputAnchorDisplayNames.Length < idx) inAnchorName = mpe.InputAnchorDisplayNames[idx];
                                        ppb.AddCustomChild($"Input anchor {inAnchorName}", new Label { Text = item });

                                    }
                                }
                                ppb.AddCustomChild("Output anchor name", new Label { Text = (be as IBindableArgumentEffectOneInputResultGenerator)?.OutputAnchorName ?? "none" });
                                break;
                            case BindableArgumentEffectType.OneInputResultGenerator:
                                ppb.AddCustomChild($"Input anchor {(be as IBindableArgumentEffectOneInputResultGenerator)?.InputAnchorName ?? "unknown"}", new Label { Text = $"{be.BindedArgumentProviderID} " });
                                break;

                            case BindableArgumentEffectType.ManyInputResultGenerator:
                                if (be is IBindableArgumentEffectManyInputResultGenerator mpg)
                                {
                                    foreach (var item in mpg.BindedArgumentProviderIDs)
                                    {
                                        var idx = mpg.BindedArgumentProviderIDs.IndexOf(item);
                                        string inAnchorName = "unknown";
                                        if (idx >= 0 && mpg.InputAnchorDisplayNames.Length < idx) inAnchorName = mpg.InputAnchorDisplayNames[idx];
                                        ppb.AddCustomChild($"Input anchor {inAnchorName}", new Label { Text = item });

                                    }
                                }
                                break;
                            default:
                                ppb.AddText(PPLocalizedResources.EffectProp_UnknownRole);
                                break;


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
                            clip.Effects ??= new Dictionary<string, IEffect>();

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
                foreach (var effectKvp in clip.Effects.OrderBy(c => c.Value.Index))
                {
                    orderContainer.Children.Add(BuildEffectOrderItem(effectKvp.Key, effectKvp.Value, clip, localizedEffectDisplayName, handler));
                }
            }

            ppb.AddCustomChild(orderContainer);
            var panel = ppb.BuildWithScrollView();
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
                if (clip.Effects == null) return;
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

        #endregion

        #region color adjustment
        private View BuildColorAdjustmentTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            clip.Effects ??= new Dictionary<string, IEffect>();
            clip.EffectBundles ??= new Dictionary<Guid, IEffectBundle>();

            var colorAdjustBundleFactories = EffectServices.GetAvailableEffectBundles().Select(c => (c, c.Value())).Where(c => c.Item2.Target == EffectTarget.ColorAdjustment && c.Item2.TypeName != "ColorAdjustment").Select(C => C.c).ToDictionary(c => c.Key, c => c.Value);
            var localizedBundleNames = EffectServices.GetLocalizedEffectBundleNames("", false);
            ColorAdjustmentEffectBundle bundle = null!;
            if (!clip.EffectBundles.TryGetValue(InternalColorAdjustmentBundleGuid, out var b) || b is not ColorAdjustmentEffectBundle cb)
            {
                bundle = new ColorAdjustmentEffectBundle() { Id = InternalColorAdjustmentBundleGuid };
            }
            else
            {
                bundle = cb;
            }

            var ppb = new PropertyPanelBuilder();
            ppb.AddFromAnother(bundle.CreateUI(), bundle);
            foreach (var item in clip.EffectBundles.Where(c => c.Value.Target == EffectTarget.ColorAdjustment && c.Value.Id != InternalColorAdjustmentBundleGuid))
            {
                var bundleId = item.Key;
                var bundleInstance = item.Value;
                var locedName = localizedBundleNames.TryGetValue(item.Value.Name, out var locName) ? locName : item.Value.TypeName;
                ppb.AddSeparator();
                ppb.AddText(new SingleLineLabel(locedName, 25));
                ppb.AddCheckbox($"Effect|{bundleId}|Enabled", PPLocalizedResources._Enabled, bundleInstance.Enabled);
                ppb.AddFromAnother(item.Value.CreateUI(), item.Value);
                ppb.AddButton($"Bundle|{bundleId}|Remove", PPLocalizedResources.EffectProp_Remove);
            }
            ppb.AppendWhen(colorAdjustBundleFactories.Any(), c => c.AddSeparator());
            foreach (var item in colorAdjustBundleFactories)
            {
                ppb.AddCustomChild(localizedBundleNames.TryGetValue(item.Key, out var value) ? value : item.Key, new Button
                {
                    Text = Localized.DraftPage_CenterMenuBar_AddClip,
                    Command = new Command(
                        () =>
                        {
                            PropertyPanelPropertyChangedEventArgs.CreateAndInvoke(ppb, "AddBundle", item.Key);
                        })
                });
            }
            ppb.PropertyChanged += (s, e) =>
            {
                if (s is IEffectBundle senderBundle)
                {
                    if (clip.EffectBundles.TryGetValue(senderBundle.Id, out var editingBundle))
                    {
                        var updated = senderBundle.HandlePropertyPanelChange(e);
                        editingBundle.Parameters = updated;
                        RebuildAllEffects(clip);
                        clip.ApplySpeedRatio();
                        handler?.Invoke(s, e);
                    }
                    else
                    {
                        clip.EffectBundles[InternalColorAdjustmentBundleGuid] =
                            new ColorAdjustmentEffectBundle()
                            {
                                Id = InternalColorAdjustmentBundleGuid,
                                Parameters = senderBundle.HandlePropertyPanelChange(e)
                            };

                        RebuildAllEffects(clip);
                        clip.ApplySpeedRatio();
                        handler?.Invoke(s, e);
                    }
                    return;
                }
                else if (e.Id.StartsWith("Bundle|"))
                {
                    var parts = e.Id.Split('|');
                    if (parts.Length >= 3)
                    {
                        Guid bundleId = new(parts[1]);
                        string action = parts[2];
                        if (!clip.EffectBundles?.ContainsKey(bundleId) ?? false) return;

                        switch (action)
                        {
                            case "Remove":
                                clip.EffectBundles?.Remove(bundleId);
                                RebuildAllEffects(clip);
                                handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                break;
                            case "Enabled":
                                if (clip.EffectBundles.TryGetValue(bundleId, out var enabledBundle))
                                {
                                    if (bool.TryParse(e.Value?.ToString(), out var enabled))
                                    {
                                        enabledBundle.Enabled = enabled;
                                        RebuildAllEffects(clip);
                                        handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                                    }
                                }
                                break;
                        }
                    }
                }
                else if (e.Id == "AddBundle")
                {
                    if (e.Value is string bundleTypeName)
                    {
                        if (colorAdjustBundleFactories.TryGetValue(bundleTypeName, out var factory))
                        {
                            var instance = factory();
                            instance.Id = Guid.NewGuid();
                            instance.BindedInputId = IEffectBundle.NoConnectionGUID;
                            instance.BindedOutputId = IEffectBundle.NoConnectionGUID;
                            clip.EffectBundles ??= new Dictionary<Guid, IEffectBundle>();
                            clip.EffectBundles[instance.Id] = instance;

                            RebuildAllEffects(clip);
                            handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                        }
                    }
                }
                handler?.Invoke(s, e);
            };

            ppb.AddButton(PPLocalizedResources.ColorAdjustment_Reset, (_, _) =>
            {
                clip.EffectBundles?.Remove(InternalColorAdjustmentBundleGuid);
                RebuildAllEffects(clip);
                handler?.Invoke(this, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
            }, (c) => c.TextColor = Color.FromArgb("#FF8080"));

            return ppb.BuildWithScrollView();
        }
        #endregion

        #region speed and ratio

        private View BuildSpeedAndRatioTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            static bool IsSpeedVarianceBundle(IEffectBundle bundle) => bundle.TypeOfEffect == EffectType.SpeedVarianceProvider && bundle.Target == EffectTarget.SpeedVariance;

            clip.Effects ??= new Dictionary<string, IEffect>();
            clip.EffectBundles ??= new Dictionary<Guid, IEffectBundle>();

            var allBundleFactories = EffectServices.GetAvailableEffectBundles();
            var localizedBundleNames = EffectServices.GetLocalizedEffectBundleNames("", false);

            var speedBundleFactoryItems = allBundleFactories
                .Where(kvp => kvp.Value().Target == EffectTarget.SpeedVariance)
                .Select(kvp => new
                {
                    TypeName = kvp.Key,
                    Factory = kvp.Value,
                    DisplayName = localizedBundleNames.GetValueOrDefault(kvp.Key, kvp.Key)
                })
                .OrderBy(x => x.DisplayName, StringComparer.Ordinal)
                .ToList();

            var speedBundles = clip.EffectBundles
                ?.Where(kvp => kvp.Value.Target == EffectTarget.SpeedVariance)
                ?.Select(c => c.Value)
                ?.ToList() ?? [];

            var ppb = new PropertyPanelBuilder();

            if (speedBundles.Count > 1)
            {
                ppb.AddText(new Label
                {
                    Text = PPLocalizedResources.SpeedAndRatio_ErrMultiplePvd,
                    TextColor = Colors.Orange
                });
            }

            if (speedBundles.Count == 0)
            {
                ppb.AddText(new SingleLineLabel(PPLocalizedResources.SpeedAndRatio_None, 20));
            }
            var bundle = speedBundles.FirstOrDefault();
            if (bundle is not null)
            {
                var bundleId = bundle.Id;
                string localizedName = localizedBundleNames.GetValueOrDefault(bundle.TypeName, bundle.TypeName);

                ppb.AddText(new SingleLineLabel(localizedName ?? bundle.Name, 25));

                try
                {
                    var bundlePpb = bundle.CreateUI();
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
                    clip.EffectBundles?.Remove(bundleId);
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
                ppb.AppendWhen(speedBundles.Count == 0 && speedBundleFactoryItems.Count > 0, c => c.AddCustomChild(BuildAddEffectPanel(EffectTarget.SpeedVariance, page, allBundleFactories, ppb, handler, false)));
            }

            ppb.PropertyChanged += (s, e) =>
            {
                if (s is IEffectBundle senderBundle)
                {
                    if (clip.EffectBundles.TryGetValue(senderBundle.Id, out var editingBundle))
                    {
                        var updated = senderBundle.HandlePropertyPanelChange(e);
                        editingBundle.Parameters = updated;
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
                else if (e.Id == "AddBundle")
                {
                    int currentCount = clip.EffectBundles.Values.Count(IsSpeedVarianceBundle);
                    if (currentCount >= 1)
                    {
                        page.Dispatcher.Dispatch(async () =>
                        {
                            await page.DisplayAlertAsync(Localized._Info, PPLocalizedResources.SpeedAndRatio_ErrSingle, Localized._OK);
                        });
                        return;
                    }
                    if (ppb.Properties.TryGetValue("NewBundleType", out var typeObj) && typeObj is string bundleTypeName)
                    {
                        if (allBundleFactories.TryGetValue(bundleTypeName, out var factory))
                        {
                            var instance = factory();
                            instance.Id = Guid.NewGuid();
                            instance.BindedInputId = IEffectBundle.NoConnectionGUID;
                            instance.BindedOutputId = IEffectBundle.NoConnectionGUID;
                            clip.EffectBundles ??= new Dictionary<Guid, IEffectBundle>();
                            clip.EffectBundles[instance.Id] = instance;
                            AutoConnectBundleToOutput(clip, instance);

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

        #region mixture

        private View BuildMixtureTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            static bool IsMixtureBundle(IEffectBundle bundle) => bundle.TypeOfEffect == EffectType.MixtureProvider && bundle.Target == EffectTarget.Mixture;

            clip.Effects ??= new Dictionary<string, IEffect>();
            clip.EffectBundles ??= new Dictionary<Guid, IEffectBundle>();

            var allBundleFactories = EffectServices.GetAvailableEffectBundles();
            var localizedBundleNames = EffectServices.GetLocalizedEffectBundleNames("", false);

            var mixtureBundleFactoryItems = allBundleFactories
                .Where(kvp => kvp.Value().Target == EffectTarget.Mixture)
                .Select(kvp => new
                {
                    TypeName = kvp.Key,
                    Factory = kvp.Value,
                    DisplayName = localizedBundleNames.GetValueOrDefault(kvp.Key, kvp.Key)
                })
                .OrderBy(x => x.DisplayName, StringComparer.Ordinal)
                .ToList();

            var mixtureBundles = clip.EffectBundles
                ?.Where(kvp => kvp.Value.Target == EffectTarget.Mixture)
                ?.Select(c => c.Value)
                ?.ToList() ?? [];

            var ppb = new PropertyPanelBuilder();

            if (mixtureBundles.Count > 1)
            {
                ppb.AddText(new Label
                {
                    Text = PPLocalizedResources.Mixture_ErrMultiplePvd,
                    TextColor = Colors.Orange
                });
            }

            if (mixtureBundles.Count == 0)
            {
                ppb.AddText(new SingleLineLabel(PPLocalizedResources.Mixture_None));
            }

            var bundle = mixtureBundles.FirstOrDefault();
            if (bundle is not null)
            {
                var bundleId = bundle.Id;
                string localizedName = localizedBundleNames.GetValueOrDefault(bundle.TypeName, bundle.TypeName);

                ppb.AddText(new SingleLineLabel(localizedName ?? bundle.Name, 20));
                ppb.AddText(new Label
                {
                    Text = PPLocalizedResources.Mixture_UnsupportWarn,
                    TextColor = Colors.Yellow
                });
                try
                {
                    var bundlePpb = bundle.CreateUI();
                    ppb.AddFromAnother(bundlePpb, bundle);
                }
                catch (Exception ex)
                {
                    Log(ex, $"loading mixture bundle {bundle.TypeName}", this);
                    ppb.AddText(new Label
                    {
                        Text = $"Error loading bundle UI: {ex.Message}",
                        TextColor = Colors.Yellow
                    });
                }

                ppb.AddButton(PPLocalizedResources.EffectProp_Remove, (s, e) =>
                {
                    clip.EffectBundles?.Remove(bundleId);
                    RebuildAllEffects(clip);
                    handler?.Invoke(s, new PropertyPanelPropertyChangedEventArgs("__REFRESH_PANEL__", null, null));
                    return;
                });
                ppb.AddSeparator();
            }
            else
            {
                ppb.AppendWhen(mixtureBundleFactoryItems.Count > 0, c => c.AddCustomChild(BuildAddEffectPanel(EffectTarget.Mixture, page, allBundleFactories, ppb, handler, false)));
            }

            ppb.PropertyChanged += (s, e) =>
            {
                if (s is IEffectBundle senderBundle)
                {
                    if (clip.EffectBundles.TryGetValue(senderBundle.Id, out var editingBundle))
                    {
                        var updated = senderBundle.HandlePropertyPanelChange(e);
                        editingBundle.Parameters = updated;
                        RebuildAllEffects(clip);
                        handler?.Invoke(s, e);
                    }
                    return;
                }
                else if (e.Id == "AddBundle")
                {
                    int currentCount = clip.EffectBundles.Values.Count(IsMixtureBundle);
                    if (currentCount >= 1)
                    {
                        page.Dispatcher.Dispatch(async () =>
                        {
                            await page.DisplayAlertAsync(Localized._Info, PPLocalizedResources.Mixture_ErrSingle, Localized._OK);
                        });
                        return;
                    }
                    if (ppb.Properties.TryGetValue("NewBundleType", out var typeObj) && typeObj is string bundleTypeName)
                    {
                        if (allBundleFactories.TryGetValue(bundleTypeName, out var factory))
                        {
                            var instance = factory();
                            instance.Id = Guid.NewGuid();
                            instance.BindedInputId = IEffectBundle.NoConnectionGUID;
                            instance.BindedOutputId = IEffectBundle.NoConnectionGUID;
                            clip.EffectBundles ??= new Dictionary<Guid, IEffectBundle>();
                            clip.EffectBundles[instance.Id] = instance;
                            AutoConnectBundleToOutput(clip, instance);

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

            var fromBundle = clip.Effects.Values.FirstOrDefault(e =>
                IsCropEffect(e)
                && string.Equals(e.BindedEffectGroupID, InternalCropBundleGuid.ToString(), StringComparison.Ordinal));
            if (fromBundle != null)
            {
                effect = fromBundle;
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

        private static EffectImplementType ResolveConfiguredImplementType(IEffectFactory factory, EffectImplementType fallback)
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

        private class DummyEffectBundle : IEffectBundle
        {
            public Guid Id { get; set; }
            public string TypeName => "Dummy";
            public string Name { get => "Dummy Effect Bundle"; set { } }
            public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
            public bool Enabled { get; set; } = true;
            public Guid BindedInputId { get; set; }
            public Guid BindedOutputId { get; set; }

            public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

            public bool IsNormalEffect => false;

            public bool IsContinuousEffect => false;

            public bool IsBindableEffect => false;

            public EffectType TypeOfEffect => EffectType.NotSpecified;

            public EffectTarget Target => EffectTarget.Video;

            public string InputAnchorDisplayName => "blackhole";

            public string[]? InputAnchorsDisplayName => null;

            public string OutputAnchorDisplayName => "blackhole";

            public bool IsMultiInput => false;

            public List<Guid>? BindedInputIds { get; set; }
            public int StartPoint { get; set; }
            public int EndPoint { get; set; }

            public List<string> ParametersNeeded => [];

            public Dictionary<string, string> ParametersType => [];


            public IEffectFactory[] Create()
            {
                throw new NotImplementedException();
            }

            public PropertyPanelBuilder CreateUI()
            {
                throw new NotImplementedException();
            }

            public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
            {
                return new EffectBundleDisplayItem
                {
                    Name = Name,
                    Description = "This is a dummy effect bundle used for testing and ordering in converting EffectBundles to normal effect(s).",
                    Thumbnail = null,
                    VideoThumbnail = null
                };
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
