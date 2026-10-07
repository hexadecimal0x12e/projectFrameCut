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
        private const string SolidColorOutputWidthKey = "SolidColorOutputWidth";
        private const string SolidColorOutputHeightKey = "SolidColorOutputHeight";
        private const string SolidColorUseFixedOutputSizeKey = "SolidColorUseFixedOutputSize";
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


        public async Task<TabbedView> Build(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            tabbedView = new();
            tabbedView.Background = page.Background;
            if (clip.ClipType == ClipMode.VideoClip) page.EnsureGeneratedSoundTrack(clip);
            tabbedView.TabItems.Add(new TabbedViewItem
            {
                Header = Localized.MainSettingsPage_Tab_General,
                Content = BuildGeneralTab(clip, handler),
                Tag = "general"
            });
            if (clip.ClipType is ClipMode.VectorCanvasClip or ClipMode.VectorComponentClip)
            {
                tabbedView.TabItems.Add(new TabbedViewItem
                {
                    Header = Localized.VectorContentEditorView_Properties,
                    LazyContentFactory = () => BuildVectorComponentTab(clip, handler),
                    Tag = "vector"
                });
            }
            if (clip.ClipType == ClipMode.AudioClip || (clip.ClipType == ClipMode.VideoClip && page.GetBoundSoundTrack(clip) is not null))
            {
                tabbedView.TabItems.Add(new TabbedViewItem
                {
                    Header = PPLocalizedResources.General_Audio,
                    LazyContentFactory = () => BuildAudioTab(clip, handler),
                    Tag = "audio"
                });
            }
            if (clip.ClipType == ClipMode.TextClip || clip.ClipType == ClipMode.SubtitleClip)
            {
                tabbedView.TabItems.Add(new TabbedViewItem
                {
                    Header = PPLocalizedResources.TextOption_TabTitle,
                    LazyAsyncContentFactory = () => BuildTextOptionTab(clip, handler),
                    Tag = "text"
                });
            }
            if (clip.isInfiniteLength || (clip.LeftHandle?.IsVisible == true && clip.RightHandle?.IsVisible == true)
                || clip.ClipType != ClipMode.MarkingClip)
            {
                tabbedView.TabItems.Add(new TabbedViewItem
                {
                    Header = PPLocalizedResources.Tabs_Timing,
                    LazyContentFactory = () => BuildTimingTab(clip, handler),
                    Tag = "timing"
                });
            }
            if (clip.ClipType is ClipMode.VideoClip or ClipMode.PhotoClip or ClipMode.VectorComponentClip)
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
                    Header = Localized.Transform_Tab,
                    LazyContentFactory = () => BuildTransformTab(clip),
                    Tag = "transform"
                });
                if (clip.ClipType != ClipMode.AudioClip)
                {
                    tabbedView.TabItems.Add(new TabbedViewItem
                    {
                        Header = PPLocalizedResources.Tabs_Mixture,
                        LazyContentFactory = () => BuildMixtureTab(clip, handler),
                        Tag = "mixture"
                    });
                    tabbedView.TabItems.Add(new TabbedViewItem
                    {
                        Header = PPLocalizedResources.Tabs_ColorAdjust,
                        LazyContentFactory = () => BuildColorAdjustmentTab(clip, handler),
                        Tag = "colorAdjust"
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

        public async Task<TabbedView> BuildFixed(
            ClipElementUI? clip,
            EventHandler<PropertyPanelPropertyChangedEventArgs> handler,
            ProjectAddClipView addClipView)
        {
            var result = clip is null ? new TabbedView { Background = page.Background } : await Build(clip, handler);
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

        public View BuildGeneralTab(ClipElementUI clip, EventHandler<PropertyPanelPropertyChangedEventArgs> handler)
        {
            string currentColorHex = clip.ClipColor ?? GetDefaultColorHex(clip.ClipType);
            var targetVideoClip = page.GetLoadedClipInstance(clip.Id) as VideoClip;
            ExternalVideoSourceDescriptor? externalSource = null;
            if (RemoteRpcVideoSource.IsExternalPath(clip.SourcePath)
                && (ProjectExternalVideoSource.TryGetDescriptor(clip.SourcePath!, out var source)
                    || RemoteRpcVideoSource.TryGetDescriptor(clip.SourcePath!, out source)))
                externalSource = source;
            var mediaResources = PPLocalizedResources.General_VideoCodec_SourceInfo.StartsWith("Unset localization item:", StringComparison.Ordinal)
                ? ISimpleLocalizerBase_PropertyPanel.GetMapping()["zh-CN"] : PPLocalizedResources;
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

            string GetVideoSourceText()
            {
                if (string.IsNullOrWhiteSpace(clip.SourcePath)) return Localized._Unknown;
                if (externalSource is { } descriptor)
                {
                    var name = string.IsNullOrWhiteSpace(descriptor.Name) ? descriptor.SourceId : descriptor.Name;
                    if (!string.IsNullOrWhiteSpace(descriptor.ClientName) && descriptor.ClientName != name)
                        name += $"\n{descriptor.ClientName}";
                    var info = new List<string>();
                    if (descriptor.Width > 0 && descriptor.Height > 0) info.Add($"{descriptor.Width}\u00D7{descriptor.Height}");
                    if (double.IsFinite(descriptor.Fps) && descriptor.Fps > 0) info.Add($"{descriptor.Fps:0.###} fps");
                    if (descriptor.TotalFrames >= 0) info.Add(mediaResources.General_VideoCodec_FrameCount(descriptor.TotalFrames));
                    return info.Count == 0 ? name : $"{name}\n{string.Join(" \u00B7 ", info)}";
                }

                if (clip.SourcePath.StartsWith("$"))
                {
                    return AssetDatabase.Assets.TryGetValue(clip.SourcePath.Substring(1), out var asset)
                        ? $"{Localized.DraftPage_CenterMenuBar_Asset}: {asset.Name}({asset.Path})"
                        : $"Unknown asset: {clip.SourcePath.Substring(1)}";
                }

                return System.IO.Path.GetFullPath(clip.SourcePath);
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

            string GetVideoTargetFormatText()
            {
                if (externalSource is null)
                {
                    var id = targetVideoClip?.DecoderName ?? (currentVideoDecoderId == "auto" ? null : currentVideoDecoderId);
                    return id is null ? Localized._Unknown : allVideoDecoderOptionLabelToId.ReverseLookup(id, PPLocalizedResources.General_VideoCodec_TargetMode_Unknown(id));
                }

                var decoder = targetVideoClip?.Decoder;
                string format;
                if (decoder is IHDRVideoSource || decoder is null && externalSource.SupportsHdr)
                    format = PPLocalizedResources.General_VideoCodec_TargetMode_hdr;
                else
                    format = (decoder?.ResultBitPerPixel ?? (externalSource.HasKnownResultBitsPerPixel ? externalSource.ResultBitsPerPixel : 0)) switch
                    {
                        8 => PPLocalizedResources.General_VideoCodec_TargetMode_8bpp,
                        16 => PPLocalizedResources.General_VideoCodec_TargetMode_16bpp,
                        _ => Localized._Unknown
                    };
                return externalSource.SupportsAlpha ? $"{format} \u00B7 Alpha" : format;
            }

            if (!videoDecoderOptionLabelToId.Values.Contains(currentVideoDecoderId, StringComparer.Ordinal))
            {
                videoDecoderOptionLabelToId[PPLocalizedResources.General_VideoCodec_TargetMode_Unknown(currentVideoDecoderId)] = currentVideoDecoderId;
            }

            string selectedVideoDecoderLabel = videoDecoderOptionLabelToId
                .FirstOrDefault(kv => string.Equals(kv.Value, currentVideoDecoderId, StringComparison.Ordinal)).Key
                ?? PPLocalizedResources.General_VideoCodec_TargetMode_Auto;

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
            .AppendWhen(clip.ClipType == ClipMode.VideoClip,
            (c) =>
                c.AddText(new SingleLineLabel(PPLocalizedResources.General_VideoCodec, 20))
                 .AppendWhen(!(clip.SourcePath?.StartsWith("#") ?? false),
                     cc => cc.AddPicker(
                             "videoTargetDecoderMode",
                             PPLocalizedResources.General_VideoCodec_TargetMode,
                             videoDecoderOptionLabelToId.Keys.ToArray(),
                             selectedVideoDecoderLabel)
                             .AppendWhen(targetVideoClip?.Decoder?.GetType() == typeof(HDRDecoderContext),
                                cc1 => cc1.AddSlider("hdrBrightnessOffset", PPLocalizedResources.General_VideoCodec_HDRBrightnessOffset, -1, 1, targetVideoClip?.HDRBrightnessOffset ?? 0, eventCallMode: SliderUpdateEventCallMode.OnMouseUp)),
                      cc => cc.AddCustomChild(PPLocalizedResources.General_VideoCodec_TargetMode, new Label { Text = GetVideoTargetFormatText() }))
                 .AppendWhen(
                    clip is not null && !string.IsNullOrWhiteSpace(clip.SourcePath),
                        pp => pp.AddCustomChild(externalSource is null ? PPLocalizedResources.General_VideoCodec_Source : mediaResources.General_VideoCodec_SourceInfo, new Label
                        {
                            Text = GetVideoSourceText(),
                            LineBreakMode = LineBreakMode.WordWrap
                        }),
                    pp => pp.AddCustomChild(externalSource is null ? PPLocalizedResources.General_VideoCodec_Source : mediaResources.General_VideoCodec_SourceInfo, new Label { Text = Localized._Unknown })
                )
            .AppendWhen(clip.ClipType == ClipMode.MarkingClip,
                c => c.AddButton(PPLocalizedResources.General_Unbind, async (s, e) => await page.UnbindGroupingMarkerAsync(clip)))
            .AppendWhen(clip.ClipType is ClipMode.TextClip or ClipMode.SubtitleClip or ClipMode.VectorCanvasClip or ClipMode.VectorComponentClip,
                c =>
                {
                    string currentVectorAaLabel = PPLocalizedResources.General_VectorClip_AAMode_None;
                    if (clip.ExtraData is not null && clip.ExtraData.TryGetValue("VectorAntiAliasMode", out var aaObj))
                    {
                        string aaStr = "None";
                        if (aaObj is JsonElement aaJsonElem)
                        {
                            aaStr = aaJsonElem.GetString() ?? "None";
                        }
                        else if (aaObj is string s)
                        {
                            aaStr = s;
                        }

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
                }));

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
                            ExpandCanvas = false,
                            Parameters = new() { ["Angle"] = (float)deg, ["ExpandCanvas"] = false },
                            Id = InternalRotationID
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
            public Guid Id { get; set; }
            public string TypeName => "Dummy";
            public string Name { get => "Dummy Effect Provider"; set { } }
            public Dictionary<string, object> MetaData { get; set; } = new Dictionary<string, object>();
            public bool Enabled { get; set; } = true;
            public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
            public EffectType TypeOfEffect => EffectType.NotSpecified;
            public EffectTarget Target => EffectTarget.Video;

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
