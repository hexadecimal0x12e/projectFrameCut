using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Project;
using projectFrameCut.Converters;
using projectFrameCut.Render;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Path = System.IO.Path;


namespace projectFrameCut.DraftStuff
{
    [DebuggerDisplay("{DisplayName}, {ClipType} ({Id})")]
    public class ClipElementUI : IClipElementUI
    {
        public required Guid Id { get; set; }
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

        public bool IsMoveable { get; set; } = true;
        public bool IsHorizontalResizable { get; set; } = true;
        public bool IsVerticalResizable { get; set; } = true;
        public bool CanSnapWhilePlacing { get; set; } = true;
        public bool CanSnapWhileResizing { get; set; } = true;
        public bool AllowFreeScaleResize { get; set; } = false;
        public bool ShowDefaultBorder { get; set; } = true;

        public uint lengthInFrame { get; set; } = 0;
        /// <summary>
        /// Indicates whether a clip's <b>SOURCE</b> is infinite length.
        /// <b>NOT MEANS The Clip itself is infinite length</b> when this prop is true.
        /// </summary>
        // this is a legacy thing and pretty confusing
        public bool isInfiniteLength { get; set; } = false;
        public uint maxFrameCount { get; set; } = 0;
        public uint relativeStartFrame { get; set; } = 0u;

        public float sourceSecondPerFrame { get; set; } = 1f;
        public float SecondPerFrameRatio => GetAverageSpeedRatio();

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

        /// <summary>
        /// Indicates whether this clip is a temporary "ghost" overlay used during drag-and-drop operations.
        /// </summary>
        [JsonIgnore]
        public bool IsGhost { get; set; } = false;

        /// <summary>
        /// Indicates whether this clip is a temporary "shadow" overlay used during drag-and-drop operations.
        /// </summary>
        [JsonIgnore]
        public bool IsShadow { get; set; } = false;

        public Dictionary<string, IEffect>? Effects { get; set; } = new();

        public Dictionary<Guid, IEffectProvider>? EffectProviders { get; set; } = new();

        public Dictionary<string, object> ExtraData { get; set; } = new();

        public float GetAverageSpeedRatio()
        {
            float ratio = 0;

            var spvProvider = Effects?.FirstOrDefault(c => c.Value.TypeOfEffect == EffectType.SpeedVarianceProvider);
            if (spvProvider?.Value is ISpeedVarianceProvider pvd)
            {
                uint sourceLength = maxFrameCount;
                if (sourceLength == 0)
                {
                    sourceLength = 1;
                }

                try
                {
                    uint effectiveLength = pvd.GetEffectiveLength(sourceLength);
                    ratio = (float)effectiveLength / sourceLength;
                }
                catch
                {
                    ratio = 1f;
                }
            }
            return ratio > 0 ? ratio : 1;
        }

        public void UpdateSourceDuration()
        {
            if (ClipType != ClipMode.VideoClip && ClipType != ClipMode.AudioClip && ClipType != ClipMode.ExtendClip)
            {
                isInfiniteLength = true;
                return;
            }
            if (string.IsNullOrWhiteSpace(SourcePath)) return;
            try
            {
                var src = PluginManager.CreateVideoSource(SourcePath, 8);
                maxFrameCount = (uint)src.TotalFrames;

            }
            catch (Exception ex)
            {
                Log(ex, $"Refresh clip {DisplayName}'s duration", this);
            }
        }

        public void ApplySpeedRatio()
        {
            Clip.WidthRequest = origLength * GetAverageSpeedRatio();

        }

        public void ApplyClipColor()
        {
            if (!string.IsNullOrWhiteSpace(ClipColor))
            {
                try
                {
                    var color = Color.FromArgb(ClipColor);
                    Clip.Background = new SolidColorBrush(color);
                    return;
                }
                catch
                {
                    // Invalid color string, fall through to generate a new one
                }
            }

            // Generate a random color and persist it so the clip keeps the same color
            // across reloads instead of getting a different random color each time.
            var brush = DetermineAssetColor(ClipType);
            Clip.Background = brush;
            if (brush is SolidColorBrush scb)
            {
                ClipColor = scb.Color.ToArgbHex();
            }
        }

        public void ApplyInitializationFailureIndicator()
        {
            if (!ClipInitializationFailure.IsMarked(ExtraData)) return;

            var description = ClipInitializationFailure.GetDescription(ExtraData);
            Clip.Stroke = new SolidColorBrush(Color.FromArgb("#FFFF00FF"));
            Clip.StrokeThickness = 4;
            ToolTipProperties.SetText(Clip, $"{DisplayName}\nClip initialization failed\n{description}");
            SemanticProperties.SetDescription(Clip, $"{DisplayName}, initialization failed, {description}");

            if (Clip.Content is Grid grid)
            {
                var content = grid.Children
                    .OfType<View>()
                    .FirstOrDefault(view => Grid.GetColumn(view) == 1);
                if (content is HorizontalStackLayout row && !row.Children.Any(child => child is Element element && element.ClassId == "ClipInitializationFailureIndicator"))
                {
                    row.Children.Insert(0, new Label
                    {
                        Text = "⚠",
                        TextColor = Colors.Magenta,
                        FontAttributes = FontAttributes.Bold,
                        InputTransparent = true,
                        ClassId = "ClipInitializationFailureIndicator"
                    });
                }
            }
        }

        public void ClearInitializationFailureIndicator()
        {
            ClipInitializationFailure.Clear(ExtraData);
            Clip.Stroke = Colors.Gray;
            Clip.StrokeThickness = 2;
            ToolTipProperties.SetText(Clip, DisplayName);
            SemanticProperties.SetDescription(Clip, $"{DisplayName}, {TypeName}");

            if (Clip.Content is Grid grid)
            {
                var content = grid.Children.OfType<View>().FirstOrDefault(view => Grid.GetColumn(view) == 1);
                if (content is HorizontalStackLayout row)
                {
                    var indicators = row.Children.Where(child => child is Element element && element.ClassId == "ClipInitializationFailureIndicator").ToArray();
                    foreach (var indicator in indicators) row.Children.Remove(indicator);
                }
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

        public EffectTarget GetEffectTarget() => ClipType switch
        {
            ClipMode.Special or ClipMode.MarkingClip => EffectTarget.NotSpecified,
            ClipMode.AudioClip => EffectTarget.Audio,
            ClipMode.TextClip or ClipMode.SubtitleClip => EffectTarget.Text,
            _ => EffectTarget.Video
        };

        public void UpdateContent(View? content)
        {
            if (content is not null) content.BindingContext = this;
            var cont = content ?? new HorizontalStackLayout
            {
                Children =
                {
                    new Label
                    {
                        Text = string.IsNullOrWhiteSpace(DisplayName) ? $"Unnamed clip {Id.ToString()[^4..]}" : DisplayName,
                        LineBreakMode = LineBreakMode.TailTruncation,
                        MaxLines = 1,
                        HorizontalOptions = LayoutOptions.Center,
                        VerticalOptions = LayoutOptions.Center,
                        HorizontalTextAlignment = TextAlignment.Center,
                        VerticalTextAlignment = TextAlignment.Center,
                        InputTransparent = true
                    }
                },
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                InputTransparent = true,
                Padding = 0,
                Spacing = 0,
                BindingContext = this
            };

            Grid.SetColumn(LeftHandle, 0);
            Grid.SetColumn(RightHandle, 2);
            Grid.SetColumn(cont, 1);

            if (Clip.Content is Grid existingGrid)
            {
                // Update in-place: swap only the content column so gesture recognizers on the Grid survive.
                for (int i = existingGrid.Children.Count - 1; i >= 0; i--)
                {
                    if (existingGrid.Children[i] is Microsoft.Maui.Controls.View v && Grid.GetColumn(v) == 1)
                        existingGrid.Children.RemoveAt(i);
                }
                existingGrid.Children.Add(cont);
                Grid.SetColumn(cont, 1);
            }
            else
            {
                Clip.Content = new Grid
                {
                    Children =
                    {
                        LeftHandle,
                        cont,
                        RightHandle
                    },
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(30, GridUnitType.Absolute) },
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = new GridLength(30, GridUnitType.Absolute) }
                    }
                };
            }

            ToolTipProperties.SetText(Clip, DisplayName);
            SemanticProperties.SetDescription(Clip, $"{DisplayName}, {TypeName}");

        }

        public override string ToString()
        {
            return $"{DisplayName}, {ClipType} ({Id})";
        }

        public override bool Equals(object? obj)
        {
            if (obj is not ClipElementUI c) return false;
            return c.Id == Id;
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }

        [SetsRequiredMembers]
#pragma warning disable CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑添加 "required" 修饰符或声明为可为 null。
        public ClipElementUI()
#pragma warning restore CS8618 // 在退出构造函数时，不可为 null 的字段必须包含非 null 值。请考虑添加 "required" 修饰符或声明为可为 null。
        {
        }

        private static double _defaultClipHeight = 62;

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

        public static ClipElementUI CreateClip(
        double startX,
        double width,
        int trackIndex,
        Guid? id = null,
        string? labelText = null,
        Brush? background = null,
        Border? prototype = null,
        uint relativeStart = 0,
        uint maxFrames = 0,
        View? ContentOverride = null)
        {

            Guid cid = id ?? Guid.NewGuid();

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
                Text = string.IsNullOrWhiteSpace(labelText) ? $"Unnamed clip {cid.ToString()[^4..]}" : labelText,
                LineBreakMode = LineBreakMode.TailTruncation,
                MaxLines = 1,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
                InputTransparent = true
            };

            var cont = ContentOverride ?? new HorizontalStackLayout
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

        private static readonly Brush[] _defaultClipColorPalette = new Brush[]
        {
            new SolidColorBrush(Color.FromRgb(255, 107, 107)),  // 柔和红
            new SolidColorBrush(Color.FromRgb(78, 205, 196)),   // 青绿
            new SolidColorBrush(Color.FromRgb(255, 159, 67)),   // 橙色
            new SolidColorBrush(Color.FromRgb(69, 162, 255)),   // 蓝色
            new SolidColorBrush(Color.FromRgb(255, 214, 10)),   // 黄色
            new SolidColorBrush(Color.FromRgb(175, 82, 222)),   // 紫色
            new SolidColorBrush(Color.FromRgb(46, 213, 115)),   // 绿色
            new SolidColorBrush(Color.FromRgb(255, 82, 82)),    // 珊瑚红
            new SolidColorBrush(Color.FromRgb(100, 181, 246)),  // 浅蓝
            new SolidColorBrush(Color.FromRgb(255, 138, 101)),  // 深橙
            new SolidColorBrush(Color.FromRgb(129, 199, 132)),  // 浅绿
            new SolidColorBrush(Color.FromRgb(186, 104, 200)),  // 紫罗兰
            new SolidColorBrush(Color.FromRgb(255, 171, 145)),  // 桃色
            new SolidColorBrush(Color.FromRgb(128, 222, 234)),  // 青色
            new SolidColorBrush(Color.FromRgb(240, 98, 146)),   // 粉红
        };

        private static Brush[]? _clipColorPalette;

        private static Brush[] GetPalette()
        {
            if (_clipColorPalette == null)
            {
                _clipColorPalette = TryLoadPaletteFromFile() ?? _defaultClipColorPalette;
            }
            return _clipColorPalette;
        }

        private static Brush[]? TryLoadPaletteFromFile()
        {
            try
            {
                var palettePath = Path.Combine(MauiProgram.DataPath, "palette.json");
                if (!System.IO.File.Exists(palettePath)) return null;

                var json = System.IO.File.ReadAllText(palettePath);
                var hexColors = JsonSerializer.Deserialize<string[]>(json);
                if (hexColors == null || hexColors.Length == 0) return null;

                var brushes = new Brush[hexColors.Length];
                for (int i = 0; i < hexColors.Length; i++)
                {
                    brushes[i] = new SolidColorBrush(Color.FromArgb(hexColors[i]));
                }
                return brushes;
            }
            catch
            {
                return null;
            }
        }

        public static Brush DetermineAssetColor(ClipMode? mode)
        {
            var palette = GetPalette();
            return palette[Random.Shared.Next(palette.Length)];
        }
        public static Brush DetermineAssetColor(AssetType type, ClipMode? mode = null)
        {
            var palette = GetPalette();
            return palette[Random.Shared.Next(palette.Length)];
        }

    }

}
