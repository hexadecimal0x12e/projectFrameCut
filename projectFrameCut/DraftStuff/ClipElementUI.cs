using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.Converters;
using projectFrameCut.Render;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Path = System.IO.Path;
using projectFrameCut.ApplicationAPIBase.Project;


namespace projectFrameCut.DraftStuff
{
    [DebuggerDisplay("{displayName}, {ClipType} ({Id})")]
    public class ClipElementUI : IClipElementUI
    {
        static ClipElementUI()
        {
            ClipUpdateEventArgs.LocalizedChangeReasonBuilder = BuildLocalizedChangeReason;
        }

        private static string? BuildLocalizedChangeReason(ClipUpdateReason? reason, string? sourceName, string? details)
        {
            try
            {
                if (reason == ClipUpdateReason.PropertyChanged)
                {
                    return Localized.ClipUpdateReason_PropertyChanged(sourceName ?? "Clip", details ?? "Unknown");
                }

                if (Localized.IsItemExist($"ClipUpdateReason_{reason}"))
                {
                    return Localized.DynamicLookupWithArgs($"ClipUpdateReason_{reason}", sourceName ?? "Clip");
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        public required string Id { get; set; }
        [JsonIgnore]
        public required Border Clip { get; set; }
        [JsonIgnore]
        public required Border LeftHandle { get; set; }
        [JsonIgnore]
        public required Border RightHandle { get; set; }

        public bool ShouldDisplayInUI { get; set; } = true;

        public string DisplayName { get; set; } = "Clip";

        public ClipMovingStatus MovingStatus { get; set; } = ClipMovingStatus.Free;
        public double layoutX { get; set; }
        public double layoutY { get; set; }
        public double ghostLayoutX { get; set; }
        public double ghostLayoutY { get; set; }
        public double handleLayoutX { get; set; }

        public double defaultY { get; set; } = -1.0;
        public int? origTrack { get; set; } = null;
        public double origLength { get; set; } = 0;
        public double origX { get; set; } = 0;

        public uint lengthInFrame { get; set; } = 0;
        /// <summary>
        /// Indicates whether a clip's <b>SOURCE</b> is infinite length.
        /// <b>NOT MEANS The Clip itself is infinite length</b> when this prop is true.
        /// </summary>
        public bool isInfiniteLength { get; set; } = false;
        public uint maxFrameCount { get; set; } = 0;
        public uint relativeStartFrame { get; set; } = 0u;

        public float sourceSecondPerFrame { get; set; } = 1f;
        public float SecondPerFrameRatio { get; set; } = 1f;

        public ClipMode ClipType { get; set; } = ClipMode.Special;
        public string FromPlugin { get; set; } = string.Empty;
        public string TypeName { get; set; } = string.Empty;
        public string? SourcePath { get; set; } = null;
        public int TargetWidth { get; set; } = 0;
        public int TargetHeight { get; set; } = 0;
        public int TargetX { get; set; } = 0;
        public int TargetY { get; set; } = 0;
        public int SubLayerIndex { get; set; } = 0;
        public int SubTrackIndex
        {
            get => SubLayerIndex;
            set => SubLayerIndex = value;
        }

        public string? ClipColor { get; set; } = null;

        public Dictionary<string, IEffect>? Effects { get; set; } = new();
        public Dictionary<Guid, IEffectBundle>? EffectBundles { get; set; } = new();
        public Dictionary<string, object> ExtraData { get; set; } = new();

        public void ApplySpeedRatio()
        {
            Clip.WidthRequest = origLength * SecondPerFrameRatio;
        }

        public void ApplyClipColor()
        {
            if (!string.IsNullOrWhiteSpace(ClipColor))
            {
                try
                {
                    var color = Color.FromArgb(ClipColor);
                    Clip.Background = new SolidColorBrush(color);
                }
                catch
                {
                    // Invalid color string, use default
                    Clip.Background = DetermineAssetColor(ClipType);
                }
            }
            else
            {
                Clip.Background = DetermineAssetColor(ClipType);
            }
        }

        public bool IsExtraDataOptionIsTrue(string option) => ExtraData.TryGetValue(option, out var o) && IsObjectTrue(o);

        public bool IsClipFallInRange(uint targetFrame, IDraftPage workingPage)
        {
            var extend = IsExtraDataOptionIsTrue("ExtendToWholeDraft");
            if (workingPage is null || extend)
            {
                return extend;
            }

            double startPx = (Clip is not null) ? Clip.TranslationX : layoutX;
            if (double.IsNaN(startPx) || double.IsInfinity(startPx))
            {
                startPx = layoutX;
            }
            startPx = Math.Max(0d, startPx);

            uint startFrame = workingPage.PixelToFrame(startPx);

            uint effectiveLength = lengthInFrame;
            if (effectiveLength == 0)
            {
                double widthPx = origLength;
                if (Clip is not null)
                {
                    widthPx = Clip.WidthRequest > 0 ? Clip.WidthRequest : Clip.Width;
                }

                effectiveLength = Math.Max(1u, workingPage.PixelToFrame(Math.Max(0d, widthPx)));
            }

            ulong start = startFrame;
            ulong endExclusive = start + Math.Max(1u, effectiveLength);
            ulong frame = targetFrame;

            return frame >= start && frame < endExclusive;
        }

        private static bool IsObjectTrue(object? input)
        {
            if (input is null) return false;
            if (input is bool b) return b;
            if (input is string s) return bool.TryParse(s, out b) && b;
            if (input is JsonElement e) return e.ValueKind == JsonValueKind.True;
            return false;
        }

        [SetsRequiredMembers]
#pragma warning disable CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑添加 "required" 修饰符或声明为可为 null。
        public ClipElementUI()
#pragma warning restore CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑添加 "required" 修饰符或声明为可为 null。
        {
        }

        private static double _defaultClipHeight = 62;

        public static ClipElementUI CreateClip(
        double startX,
        double width,
        int trackIndex,
        string? id = null,
        string? labelText = null,
        Brush? background = null,
        Border? prototype = null,
        uint relativeStart = 0,
        uint maxFrames = 0)
        {

            string cid = id ?? Guid.NewGuid().ToString();

            // Build UI
            var clipBorder = new Border
            {
                Stroke = prototype?.Stroke ?? Colors.Gray,
                StrokeThickness = prototype?.StrokeThickness ?? 2,
                Background = background ?? prototype?.Background ?? new SolidColorBrush(Colors.CornflowerBlue),
                WidthRequest = width,
                HeightRequest = prototype?.HeightRequest > 0 ? prototype!.HeightRequest : _defaultClipHeight,
                StrokeShape = prototype?.StrokeShape ?? new RoundRectangle
                {
                    CornerRadius = 20,
                    BackgroundColor = Colors.White,
                    StrokeThickness = 0
                }
            };

            var leftHandle = new Border
            {
                Stroke = Colors.Gray,
                StrokeThickness = 2,
                Background = new SolidColorBrush(Colors.White),
                WidthRequest = 25,
                HeightRequest = 55,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                StrokeShape = new RoundRectangle
                {
                    CornerRadius = 20,
                    BackgroundColor = Colors.White,
                }
            };

            var rightHandle = new Border
            {
                Stroke = Colors.Gray,
                StrokeThickness = 2,
                Background = new SolidColorBrush(Colors.White),
                WidthRequest = 25,
                HeightRequest = 55,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                StrokeShape = new RoundRectangle
                {
                    CornerRadius = 20,
                    BackgroundColor = Colors.White,
                }
            };

            var element = new ClipElementUI
            {
                Id = cid,
                DisplayName = labelText ?? "Unnamed Clip",
                layoutX = 0,
                layoutY = 0,
                Clip = clipBorder,
                LeftHandle = leftHandle,
                RightHandle = rightHandle,
                maxFrameCount = maxFrames,
                relativeStartFrame = relativeStart,
                isInfiniteLength = width <= 0,
                origLength = width,
                origTrack = trackIndex,
                origX = startX
            };

            var titleLabel = new Label
            {
                Text = string.IsNullOrWhiteSpace(labelText) ? $"Unnamed clip {cid[^4..]}" : labelText,
                LineBreakMode = LineBreakMode.TailTruncation,
                MaxLines = 1,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
                InputTransparent = true
            };

            var cont = new HorizontalStackLayout
            {
                Children =
                {
                    titleLabel
                },
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                InputTransparent = true,
                Padding = 0,
                Spacing = 0
            };

            Grid.SetColumn(element.LeftHandle, 0);
            Grid.SetColumn(element.RightHandle, 2);
            Grid.SetColumn(cont, 1);

            element.Clip.Content = new Grid
            {
                Children =
                {
                    element.LeftHandle,
                    cont,
                    element.RightHandle
                },
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(30, GridUnitType.Absolute) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = new GridLength(30, GridUnitType.Absolute) }
                }
            };

            element.Clip.BindingContext = element;
            element.LeftHandle.BindingContext = element;
            element.RightHandle.BindingContext = element;

            if (!string.IsNullOrWhiteSpace(element.DisplayName))
            {
                ToolTipProperties.SetText(titleLabel, element.DisplayName);
                SemanticProperties.SetDescription(element.Clip, $"{element.DisplayName}, {element.TypeName}");
            }
            AutomationProperties.SetIsInAccessibleTree(element.Clip, true);

            return element;
        }

        public static ClipMode DetermineClipMode(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return ClipMode.Special;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            // Common video extensions
            string[] video = [".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v"];
            string[] image = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tiff"];
            string[] audio = [".mp3", ".wav", ".aac", ".flac", ".m4a", ".ogg"];
            string[] subtitle = [".srt", ".vtt", ".ass", ".ssa"];

            if (video.Contains(ext)) return ClipMode.VideoClip;
            if (image.Contains(ext)) return ClipMode.PhotoClip;
            if (audio.Contains(ext)) return ClipMode.AudioClip;
            if (subtitle.Contains(ext)) return ClipMode.SubtitleClip;

            return ClipMode.Special; // fallback
        }

        public static Brush DetermineAssetColor(ClipMode? mode)
        {
            return mode switch
            {
                ClipMode.VideoClip => new SolidColorBrush(Colors.CornflowerBlue),
                ClipMode.PhotoClip => new SolidColorBrush(Colors.MediumSeaGreen),
                ClipMode.AudioClip => new SolidColorBrush(Colors.Goldenrod),
                ClipMode.SubtitleClip => new SolidColorBrush(Colors.SlateGray),
                ClipMode.SolidColorClip => new SolidColorBrush(Colors.OrangeRed),
                ClipMode.MarkingClip => new SolidColorBrush(Color.FromRgba(51, 136, 255, 96)),
                ClipMode.TransformClip => new SolidColorBrush(Colors.AliceBlue),
                _ => new SolidColorBrush(Colors.Gray),
            };
        }
        public static Brush DetermineAssetColor(AssetType type, ClipMode? mode = null)
        {
            return type switch
            {
                AssetType.Video => new SolidColorBrush(Colors.CornflowerBlue),
                AssetType.Image => new SolidColorBrush(Colors.MediumSeaGreen),
                AssetType.Audio => new SolidColorBrush(Colors.Goldenrod),
                _ => DetermineAssetColor(mode)
            };
        }

    }
}