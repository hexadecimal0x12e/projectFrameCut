using projectFrameCut.Shared;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Drawing.Processing;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using SixLabors.ImageSharp.Processing;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Plugin;
using System.Text.Json;
using System.Text.Json.Serialization;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.EncodeAndDecode;

namespace projectFrameCut.Render.ClipsAndTracks
{
    public class VideoClip : IClip
    {
        public required string Id { get; init; }
        public Guid IdAsGUID { get; init => field = Guid.TryParse(Id, out value) ? value : throw new InvalidDataException("A clip's ID field SHOULD BE a valid guid."); }
        public required string Name { get; init; }
        public uint LayerIndex { get; init; } = 0;
        public uint SubLayerIndex { get; init; }
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; set; }
        public float FrameTime { get; init; }
        public float SecondPerFrameRatio { get => 1; init { } }

        public string? FilePath { get; set; }

        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public IEffect[]? EffectsInstances { get; set; }
        public Dictionary<string, object> ExtraData { get; set; }
        public bool ExtendToWholeDraft { get; set; }

        public bool NeedFilePath => true;

        [System.Text.Json.Serialization.JsonIgnore]
        public IVideoSource? Decoder { get; set; } = null;

        public ClipMode ClipType => ClipMode.VideoClip;
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;

        public string BindedSoundTrack { get; init; } = "";
        public int TargetWidth { get; set; }
        public int TargetHeight { get; set; }
        public int TargetX { get; set; }
        public int TargetY { get; set; }
        public ISpeedVarianceProvider? SpeedVarianceProviderInstance { get; set; }

        public string TargetDecoder { get; set; } = string.Empty;
        public double HDRBrightnessOffset { get; set; } = 0;

        [JsonIgnore()]
        public string? DecoderName => Decoder?.TypeName;


        public VideoClip()
        {
            (EffectsInstances, SpeedVarianceProviderInstance) = EffectHelper.GetEffectsInstancesAndSpeedVariance(Effects);
        }

        public IPicture GetFrameRelativeToStartPointOfSource(uint targetFrame, int targetWidth, int targetHeight, bool forceResize, IPicture.PicturePixelMode targetPPB)
        {
            if(Decoder is null)
            {
                throw new NullReferenceException("Decoder is null. Please init it.");
            }
            targetFrame = ClampFrameToDecoderRange(targetFrame);
            if(Decoder is HDRDecoderContext h)
            {
                return h.GetHDRFrame(targetFrame, hasAlpha: true).Resize(targetWidth, targetHeight, forceResize).SetBrightnessOffset(HDRBrightnessOffset).ToBitPerPixel(targetPPB);
            }
            return (Decoder ?? throw new NullReferenceException("Decoder is null. Please init it.")).GetFrame(targetFrame).Resize(targetWidth, targetHeight, forceResize).ToBitPerPixel(targetPPB);
        }

        private uint ClampFrameToDecoderRange(uint targetFrame)
        {
            if (Decoder is null)
            {
                return targetFrame;
            }

            long totalFrames = Decoder.TotalFrames;
            if (totalFrames <= 0)
            {
                return targetFrame;
            }

            uint maxFrame = (uint)Math.Max(0, totalFrames - 1);
            return targetFrame > maxFrame ? maxFrame : targetFrame;
        }

        void IClip.ReInit(IPicture.PicturePixelMode targetPPB)
        {
            if (string.IsNullOrWhiteSpace(FilePath)) throw new NullReferenceException($"VideoClip {Id}'s source path is null.");
            if (!string.IsNullOrWhiteSpace(TargetDecoder) && TargetDecoder != "auto")
            {
                var supportedPlugin = PluginManager.LoadedPlugins.Values.FirstOrDefault(p => p.VideoSourceProvider.ContainsKey(TargetDecoder)) ?? throw new NotSupportedException($"The specified video decoder '{TargetDecoder}' was not found for the file '{FilePath}'.");
                Decoder = supportedPlugin.VideoSourceProvider[TargetDecoder](null!).CreateNew(FilePath);
                return;
            }
            Decoder = PluginManager.CreateVideoSource(FilePath);
            (EffectsInstances, SpeedVarianceProviderInstance) = EffectHelper.GetEffectsInstancesAndSpeedVariance(Effects);

        }


        void IDisposable.Dispose()
        {
            Decoder?.Dispose();
        }

        public uint? GetClipLength() => null;
    }

    public class PhotoClip : IClip
    {
        public required string Id { get; init; }
        public Guid IdAsGUID { get; init => field = Guid.TryParse(Id, out value) ? value : throw new InvalidDataException("A clip's ID field SHOULD BE a valid guid."); }
        public required string Name { get; init; }
        public uint LayerIndex { get; init; } = 0;
        public uint SubLayerIndex { get; init; }
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; set; }
        public float FrameTime { get; init; }
        public float SecondPerFrameRatio { get => 1; init { } }

        public string? FilePath { get; set; } = string.Empty;
        public bool NeedFilePath => true;
        public Dictionary<string, object> ExtraData { get; set; }
        public bool ExtendToWholeDraft { get; set; }

        public bool Use16bpp = false;


        [System.Text.Json.Serialization.JsonIgnore]
        public IPicture? source { get; set; } = null;

        public ClipMode ClipType => ClipMode.PhotoClip;
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;

        public string BindedSoundTrack { get; init; } = "";



        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public IEffect[]? EffectsInstances { get; set; }
        public int TargetWidth { get; set; }
        public int TargetHeight { get; set; }
        public int TargetX { get; set; }
        public int TargetY { get; set; }
        public ISpeedVarianceProvider? SpeedVarianceProviderInstance { get; set; }

        public PhotoClip()
        {
            (EffectsInstances, SpeedVarianceProviderInstance) = EffectHelper.GetEffectsInstancesAndSpeedVariance(Effects);
        }
        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int targetWidth, int targetHeight, bool forceResize, IPicture.PicturePixelMode targetPPB) => source?.Resize(targetWidth, targetHeight, forceResize).ToBitPerPixel(targetPPB) ?? throw new NullReferenceException("Source is null. Please init it.");

        void IClip.ReInit(IPicture.PicturePixelMode targetPPB)
        {
            if (FilePath is null) throw new NullReferenceException($"PhotoClip {Id}'s source path is null.");
            source = targetPPB == 16 ? new Picture16bpp(FilePath) : new Picture8bpp(FilePath);
            source.CanBeDisposed = false;
            source.ProcessStack = new List<PictureProcessStack>
            {
                new PictureProcessStack
                {
                    Operator = GetType(),
                    OperationDisplayName = $"Created for PhotoClip {Name} ({Id})",
                    ProcessingFuncStackTrace = null,
                    Properties = new Dictionary<string, object>
                    {
                        { "Path", FilePath }
                    }
                }
            };
            (EffectsInstances, SpeedVarianceProviderInstance) = EffectHelper.GetEffectsInstancesAndSpeedVariance(Effects);

        }


        void IDisposable.Dispose()
        {
            //nothing to dispose
        }

        public uint? GetClipLength() => Duration;
    }

    public class SolidColorClip : IClip
    {
        public required string Id { get; init; }
        public Guid IdAsGUID { get; init => field = Guid.TryParse(Id, out value) ? value : throw new InvalidDataException("A clip's ID field SHOULD BE a valid guid."); }
        public required string Name { get; init; }
        public uint LayerIndex { get; init; } = 0;
        public uint SubLayerIndex { get; init; }
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; set; }
        public float FrameTime { get; init; }
        public float SecondPerFrameRatio { get => 1; init { } }

        public string? filePath { get; } = null;
        public ClipMode ClipType => ClipMode.SolidColorClip;
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;

        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public IEffect[]? EffectsInstances { get; set; }
        public bool NeedFilePath => false;
        public Dictionary<string, object> ExtraData { get; set; }
        public bool ExtendToWholeDraft { get; set; }

        public string BindedSoundTrack { get; init; } = "";


        string? IClip.FilePath { get => null; set => throw new InvalidOperationException("Set path is not supported by this type of clip."); }

        public ushort R { get; init; }
        public ushort G { get; init; }
        public ushort B { get; init; }
        public float? A { get; init; } = null;

        public bool UseFixedOutputSize { get; init; } = true;
        public int OutputWidth { get; init; } = 1920;
        public int OutputHeight { get; init; } = 1080;

        [JsonIgnore]
        public bool EffectiveUseFixedOutputSize => ResolveConfiguredBool("SolidColorUseFixedOutputSize", UseFixedOutputSize);

        [JsonIgnore]
        public int EffectiveOutputWidth => ResolveConfiguredInt("SolidColorOutputWidth", OutputWidth > 0 ? OutputWidth : targetWidth);

        [JsonIgnore]
        public int EffectiveOutputHeight => ResolveConfiguredInt("SolidColorOutputHeight", OutputHeight > 0 ? OutputHeight : targetHeight);

        [JsonIgnore]
        public bool ShouldUseFixedOutputSize => EffectiveUseFixedOutputSize && TargetWidth <= 0 && TargetHeight <= 0;

        public int targetWidth { get; init; } = 1920;
        public int targetHeight { get; init; } = 1080;
        public int TargetWidth { get; set; }
        public int TargetHeight { get; set; }
        public int TargetX { get; set; }
        public int TargetY { get; set; }
        public ISpeedVarianceProvider? SpeedVarianceProviderInstance { get; set; }

        public IPicture GetFrameRelativeToStartPointOfSource(uint targetFrame, int tWidth, int tHeight, bool forceResize)
        {
            var width = ShouldUseFixedOutputSize ? EffectiveOutputWidth : Math.Max(1, tWidth);
            var height = ShouldUseFixedOutputSize ? EffectiveOutputHeight : Math.Max(1, tHeight);
            return Picture16bpp.GenerateSolidColor(width, height, R, G, B, A);
        }

        public IPicture GetFrameRelativeToStartPointOfSource(uint targetFrame, int tWidth, int tHeight, bool forceResize, IPicture.PicturePixelMode targetPPB) => targetPPB.Value switch
        {
            16 => Picture16bpp.GenerateSolidColor(ShouldUseFixedOutputSize ? EffectiveOutputWidth : Math.Max(1, tWidth), ShouldUseFixedOutputSize ? EffectiveOutputHeight : Math.Max(1, tHeight), R, G, B, A),
            8 => Picture8bpp.GenerateSolidColor(ShouldUseFixedOutputSize ? EffectiveOutputWidth : Math.Max(1, tWidth), ShouldUseFixedOutputSize ? EffectiveOutputHeight : Math.Max(1, tHeight), (byte)(R / 257), (byte)(G / 257), (byte)(B / 257), A),
            _ => throw new NotSupportedException($"Unsupported target pixel mode {targetPPB}.")
        };

        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex)
            => Picture16bpp.GenerateSolidColor(EffectiveOutputWidth, EffectiveOutputHeight, R, G, B, A);

        public SolidColorClip()
        {
            (EffectsInstances, SpeedVarianceProviderInstance) = EffectHelper.GetEffectsInstancesAndSpeedVariance(Effects);

        }

        public void ReInit()
        {
            (EffectsInstances, SpeedVarianceProviderInstance) = EffectHelper.GetEffectsInstancesAndSpeedVariance(Effects);

        }

        public void Dispose()
        {

        }

        public uint? GetClipLength() => Duration;


        public void ReInit(IPicture.PicturePixelMode targetPPB)
        {
        }

        private int ResolveConfiguredInt(string key, int fallback)
        {
            if (ExtraData != null && ExtraData.TryGetValue(key, out var raw) && raw is not null)
            {
                if (raw is int i)
                {
                    return Math.Max(1, i);
                }

                if (raw is long l)
                {
                    return Math.Max(1, (int)Math.Min(int.MaxValue, l));
                }

                if (raw is JsonElement je)
                {
                    if (je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var jn))
                    {
                        return Math.Max(1, jn);
                    }

                    if (je.ValueKind == JsonValueKind.String && int.TryParse(je.GetString(), out var js))
                    {
                        return Math.Max(1, js);
                    }
                }

                if (int.TryParse(raw.ToString(), out var parsed))
                {
                    return Math.Max(1, parsed);
                }
            }

            return Math.Max(1, fallback);
        }

        private bool ResolveConfiguredBool(string key, bool fallback)
        {
            if (ExtraData != null && ExtraData.TryGetValue(key, out var raw) && raw is not null)
            {
                if (raw is bool b)
                {
                    return b;
                }

                if (raw is JsonElement je)
                {
                    if (je.ValueKind == JsonValueKind.True) return true;
                    if (je.ValueKind == JsonValueKind.False) return false;
                    if (je.ValueKind == JsonValueKind.String && bool.TryParse(je.GetString(), out var jb)) return jb;
                }

                if (bool.TryParse(raw.ToString(), out var parsed))
                {
                    return parsed;
                }
            }

            return fallback;
        }

    }

    public class TextClip : IClip
    {
        public required string Id { get; init; }
        public Guid IdAsGUID
        {
            get;
            init
            {
                if(!Guid.TryParse(Id, out field))
                {
                    Log($"A clip's ID field should be a valid guid. The input field has an invalid data '{Id}'", "warn");
                    field = Guid.Empty;
                }
            }
        }
        public required string Name { get; init; }
        public uint LayerIndex { get; init; } = 0;
        public uint SubLayerIndex { get; init; }
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; set; }
        public float FrameTime { get; init; }
        public float SecondPerFrameRatio { get => 1; init { } }

        public string? filePath { get; } = null;
        public ClipMode ClipType => ClipMode.TextClip;
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;

        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public IEffect[]? EffectsInstances { get; set; }
        public bool NeedFilePath => false;
        public Dictionary<string, object> ExtraData { get; set; }
        public bool ExtendToWholeDraft { get; set; }

        public string BindedSoundTrack { get; init; } = "";


        string? IClip.FilePath { get => null; set => throw new InvalidOperationException("Set path is not supported by this type of clip."); }

        public List<TextClipEntry> TextEntries { get; set; } = new List<TextClipEntry>();

        public string FontPath { get; set; } = string.Empty;
        private const int MaxTextFrameCacheEntries = 16;
        private readonly object textFrameCacheLock = new();
        private readonly Dictionary<string, IPicture> textFrameCache = new(StringComparer.Ordinal);

        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int targetWidth, int targetHeight, bool forceResize, IPicture.PicturePixelMode targetPPB)
        {
            var entriesToRender = ResolveTextEntriesForRender();
            string serializedEntries = JsonSerializer.Serialize(entriesToRender, JsonSerializerOptions.Web);
            string cacheKey = BuildFrameCacheKey(targetWidth, targetHeight, forceResize, targetPPB, serializedEntries);

            if (TryGetFrameFromCache(cacheKey, out var cachedFrame))
            {
                return cachedFrame;
            }

            using Image<Rgba64> canvas = new(targetWidth, targetHeight);

            foreach (var entry in entriesToRender)
            {
                if (string.IsNullOrEmpty(entry.text))
                    continue;

                Font font;
                if (GetFont().TryGet(entry.fontFamily, out var family))
                {
                    font = family.CreateFont(entry.fontSize, entry.fontStyle);
                }
                else
                {
                    Log($"Font {entry.fontFamily} not available, try fallback to HarmonyOS_Sans_SC_Regular...");
                    if (GetFont().TryGet("HarmonyOS_Sans_SC_Regular", out var defaultFamily))
                    {
                        font = defaultFamily.CreateFont(entry.fontSize, entry.fontStyle);
                    }
                    else
                    {
                        Log($"Font HarmonyOS_Sans_SC_Regular not available, try fallback to OS default one...");

                        var first = GetFont().Families.FirstOrDefault();
                        if (first != default)
                            font = first.CreateFont(entry.fontSize, entry.fontStyle);
                        else
                            continue;
                    }
                }

                var fillColor = Color.FromPixel(new Rgba64(entry.r, entry.g, entry.b, (ushort)((entry.a ?? 1.0f) * 65535)));
                var brush = Brushes.Solid(fillColor);

                var richTextOptions = new RichTextOptions(font)
                {
                    KerningMode = entry.applyKerning ? KerningMode.Standard : KerningMode.None,
                    LineSpacing = entry.lineSpacing,
                    HorizontalAlignment = entry.horizontalAlignment,
                    VerticalAlignment = entry.verticalAlignment,
                    Dpi = entry.dpi ?? 72f,
                    Origin = new PointF(entry.x, entry.y),
                };
                if (entry.wrappingWidth.HasValue)
                    richTextOptions.WrappingLength = entry.wrappingWidth.Value;

                // prepare stroke if requested
                bool hasStroke = entry.strokeWidth.HasValue && entry.strokeWidth.Value > 0f;
                SolidPen? pen = null;
                if (hasStroke)
                {
                    var strokeColor = Color.FromPixel(new Rgba64(entry.strokeR, entry.strokeG, entry.strokeB, 65535));
                    pen = Pens.Solid(strokeColor, entry.strokeWidth!.Value);
                }

                // Vertical CJK layout: draw char-by-char in a column.
                if (entry.UseVerticalLayout)
                {
                    if (Math.Abs(entry.rotation) > 0.0001f)
                    {
                        using var textLayer = new Image<Rgba64>(targetWidth, targetHeight);
                        DrawVerticalText(textLayer, entry, font, brush, pen, hasStroke);
                        var transformBuilder = new AffineTransformBuilder()
                            .AppendTranslation(new Vector2(-entry.x, -entry.y))
                            .AppendRotationDegrees(entry.rotation)
                            .AppendTranslation(new Vector2(entry.x, entry.y));
                        textLayer.Mutate(ctx => ctx.Transform(transformBuilder));
                        canvas.Mutate(ctx => ctx.DrawImage(textLayer, 1f));
                    }
                    else
                    {
                        DrawVerticalText(canvas, entry, font, brush, pen, hasStroke);
                    }
                }
                // If rotation is specified, draw to a temp layer then rotate it around the entry origin.
                else if (Math.Abs(entry.rotation) > 0.0001f)
                {
                    using var textLayer = new Image<Rgba64>(targetWidth, targetHeight);
                    textLayer.Mutate(ctx =>
                    {
                        if (hasStroke)
                            ctx.DrawText(richTextOptions, entry.text, brush, pen!);
                        else
                            ctx.DrawText(richTextOptions, entry.text, brush);
                    });
                    var transformBuilder = new AffineTransformBuilder()
                        .AppendTranslation(new Vector2(-entry.x, -entry.y))
                        .AppendRotationDegrees(entry.rotation)
                        .AppendTranslation(new Vector2(entry.x, entry.y));
                    textLayer.Mutate(ctx => ctx.Transform(transformBuilder));
                    canvas.Mutate(ctx => ctx.DrawImage(textLayer, 1f));
                }
                else
                {
                    canvas.Mutate(ctx =>
                    {
                        if (hasStroke)
                            ctx.DrawText(richTextOptions, entry.text, brush, pen!);
                        else
                            ctx.DrawText(richTextOptions, entry.text, brush);
                    });
                }
            }
            var stack = new List<PictureProcessStack>
                {
                    new PictureProcessStack
                    {
                        OperationDisplayName = "TextClip Render",
                        Operator = typeof(TextClip),
                        ProcessingFuncStackTrace = new System.Diagnostics.StackTrace(true),
                        StepUsed = null,
                        Properties = new Dictionary<string, object>
                        {
                            { "TextEntries", serializedEntries },
                            { "FontPath", FontPath }
                        }
                    }
                };

            IPicture rendered = targetPPB.Value switch
            {
                8 => new Picture8bpp(canvas) { ProcessStack = stack },
                16 => new Picture16bpp(canvas) { ProcessStack = stack },
                _ => throw new NotSupportedException($"Unsupported target pixel mode {targetPPB}.")
            };

            CacheRenderedFrame(cacheKey, rendered);
            return rendered.DeepCopy();
        }


        public TextClip()
        {
            (EffectsInstances, SpeedVarianceProviderInstance) = EffectHelper.GetEffectsInstancesAndSpeedVariance(Effects);

        }

        public void ReInit(IPicture.PicturePixelMode targetPPB)
        {
            ClearFrameCache();

            if (!string.IsNullOrWhiteSpace(FontPath))
            {
                fontsCache.Add(FontPath);
            }
            (EffectsInstances, SpeedVarianceProviderInstance) = EffectHelper.GetEffectsInstancesAndSpeedVariance(Effects);

        }


        public void Dispose()
        {
            ClearFrameCache();
        }

        public uint? GetClipLength() => Duration;


        private static FontCollection fontsCache = new();
        private static bool hasGetFontCache = false;
        public static FontCollection FontsCache { get { return fontsCache; } }

        public int TargetWidth { get; set; }
        public int TargetHeight { get; set; }
        public int TargetX { get; set; }
        public int TargetY { get; set; }
        public ISpeedVarianceProvider? SpeedVarianceProviderInstance { get; set; }

        public static FontCollection GetFont(bool force = false)
        {
            if (hasGetFontCache && !force) return fontsCache;
            fontsCache.AddSystemFonts();
            foreach (var item in Directory.GetFiles(AppContext.BaseDirectory, "*.ttf"))
            {
                fontsCache.Add(item);
            }
            hasGetFontCache = true;
            return fontsCache;

        }

        private string BuildFrameCacheKey(int targetWidth, int targetHeight, bool forceResize, IPicture.PicturePixelMode targetPPB, string serializedEntries)
            => $"{targetWidth}x{targetHeight}|forceResize={forceResize}|ppb={targetPPB.Value}|font={FontPath}|entries={serializedEntries}";

        private bool TryGetFrameFromCache(string cacheKey, out IPicture picture)
        {
            lock (textFrameCacheLock)
            {
                if (textFrameCache.TryGetValue(cacheKey, out var cachedFrame))
                {
                    if (!cachedFrame.Disposed)
                    {
                        picture = cachedFrame.DeepCopy();
                        return true;
                    }

                    textFrameCache.Remove(cacheKey);
                    try { cachedFrame.Dispose(true); } catch { }
                }
            }

            picture = null!;
            return false;
        }

        private void CacheRenderedFrame(string cacheKey, IPicture picture)
        {
            lock (textFrameCacheLock)
            {
                if (!textFrameCache.ContainsKey(cacheKey) && textFrameCache.Count >= MaxTextFrameCacheEntries)
                {
                    ClearFrameCacheUnsafe();
                }

                if (textFrameCache.TryGetValue(cacheKey, out var oldFrame))
                {
                    try { oldFrame.Dispose(true); } catch { }
                }

                picture.CanBeDisposed = false;
                textFrameCache[cacheKey] = picture;
            }
        }

        private void ClearFrameCache()
        {
            lock (textFrameCacheLock)
            {
                ClearFrameCacheUnsafe();
            }
        }

        private void ClearFrameCacheUnsafe()
        {
            foreach (var frame in textFrameCache.Values)
            {
                try { frame.Dispose(true); } catch { }
            }
            textFrameCache.Clear();
        }

        private IReadOnlyList<TextClipEntry> ResolveTextEntriesForRender()
        {
            if (ExtraData?.TryGetValue("TextEntries", out var rawEntries) == true)
            {
                if (rawEntries is List<TextClipEntry> list && list.Count > 0)
                    return list;

                if (rawEntries is JsonElement je)
                {
                    try
                    {
                        var parsed = je.Deserialize<List<TextClipEntry>>();
                        if (parsed is { Count: > 0 })
                            return parsed;
                    }
                    catch
                    {
                        // fall back to TextEntries
                    }
                }

                if (rawEntries is string json && !string.IsNullOrWhiteSpace(json))
                {
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<List<TextClipEntry>>(json);
                        if (parsed is { Count: > 0 })
                            return parsed;
                    }
                    catch
                    {
                        // fall back to TextEntries
                    }
                }
            }

            return TextEntries;
        }

        /// <summary>
        /// Returns true for characters in CJK Unified Ideographs, Hiragana, Katakana,
        /// Hangul, Bopomofo and related blocks that are naturally square in vertical layout.
        /// </summary>
        private static bool IsCjkCharacter(char c)
        {
            return (c >= '\u2E80' && c <= '\u2EFF') ||  // CJK Radicals Supplement
                   (c >= '\u2F00' && c <= '\u2FDF') ||  // Kangxi Radicals
                   (c >= '\u3000' && c <= '\u303F') ||  // CJK Symbols and Punctuation
                   (c >= '\u3040' && c <= '\u309F') ||  // Hiragana
                   (c >= '\u30A0' && c <= '\u30FF') ||  // Katakana
                   (c >= '\u3100' && c <= '\u312F') ||  // Bopomofo
                   (c >= '\u3200' && c <= '\u32FF') ||  // Enclosed CJK Letters and Months
                   (c >= '\u3300' && c <= '\u33FF') ||  // CJK Compatibility
                   (c >= '\u3400' && c <= '\u4DBF') ||  // CJK Extension A
                   (c >= '\u4E00' && c <= '\u9FFF') ||  // CJK Unified Ideographs
                   (c >= '\uAC00' && c <= '\uD7AF') ||  // Hangul Syllables
                   (c >= '\uF900' && c <= '\uFAFF') ||  // CJK Compatibility Ideographs
                   (c >= '\uFE30' && c <= '\uFE4F') ||  // CJK Compatibility Forms
                   (c >= '\uFF00' && c <= '\uFFEF');    // Halfwidth and Fullwidth Forms
        }

        /// <summary>
        /// Renders <paramref name="entry"/> text as a vertical column onto <paramref name="canvas"/>.
        /// CJK characters are drawn upright and stacked. Non-CJK characters are either kept
        /// horizontal (<see cref="TextClipEntry.KeepNonCJKTextAsHorizontal"/> = true) or
        /// rotated 90° CW to stand upright in the column (= false).
        /// </summary>
        private static void DrawVerticalText(
            Image<Rgba64> canvas,
            TextClipEntry entry,
            Font font,
            Brush brush,
            SolidPen? pen,
            bool hasStroke)
        {
            float dpi = entry.dpi ?? 72f;
            // Pixel height of one em at the given DPI.
            float emSize = entry.fontSize * (dpi / 72f);
            float charAdvance = emSize * entry.lineSpacing;
            float currentY = entry.y;

            foreach (char c in entry.text)
            {
                if (c == '\n' || c == '\r') continue;

                bool isCjk = IsCjkCharacter(c);
                // Non-CJK chars that should stand upright need a 90° CW rotation.
                bool needsRotation = !isCjk && !entry.KeepNonCJKTextAsHorizontal;

                var charOpts = new RichTextOptions(font)
                {
                    KerningMode = entry.applyKerning ? KerningMode.Standard : KerningMode.None,
                    Dpi = dpi,
                    Origin = new PointF(entry.x, currentY),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                };

                if (needsRotation)
                {
                    // Render on a full-size layer then rotate 90° CW around the cell centre.
                    using var charLayer = new Image<Rgba64>(canvas.Width, canvas.Height);
                    charLayer.Mutate(ctx =>
                    {
                        if (hasStroke)
                            ctx.DrawText(charOpts, c.ToString(), brush, pen!);
                        else
                            ctx.DrawText(charOpts, c.ToString(), brush);
                    });
                    float halfCell = emSize / 2f;
                    var transformBuilder = new AffineTransformBuilder()
                        .AppendTranslation(new Vector2(-(entry.x + halfCell), -(currentY + halfCell)))
                        .AppendRotationDegrees(90f)
                        .AppendTranslation(new Vector2(entry.x + halfCell, currentY + halfCell));
                    charLayer.Mutate(ctx => ctx.Transform(transformBuilder));
                    canvas.Mutate(ctx => ctx.DrawImage(charLayer, 1f));
                }
                else
                {
                    canvas.Mutate(ctx =>
                    {
                        if (hasStroke)
                            ctx.DrawText(charOpts, c.ToString(), brush, pen!);
                        else
                            ctx.DrawText(charOpts, c.ToString(), brush);
                    });
                }

                currentY += charAdvance;
            }
        }

    }

    public class MarkingClip : IClip
    {
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;

        public ClipMode ClipType => ClipMode.MarkingClip;

        public string Id { get; init; }
        public Guid IdAsGUID { get; init => field = Guid.TryParse(Id, out value) ? value : throw new InvalidDataException("A clip's ID field SHOULD BE a valid guid."); }
        public string Name { get; init; }
        public string BindedSoundTrack { get; init; }
        public uint LayerIndex { get; init; }
        public uint SubLayerIndex { get; init; }
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; set; }
        public float FrameTime { get; init; }
        public float SecondPerFrameRatio { get => 1; init { } }
        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public IEffect[]? EffectsInstances { get; set; }
        public string? FilePath { get; set; }
        public Dictionary<string, object> ExtraData { get; set; }
        public bool ExtendToWholeDraft { get; set; }


        public bool NeedFilePath => false;

        public int TargetWidth { get; set; }
        public int TargetHeight { get; set; }
        public int TargetX { get; set; }
        public int TargetY { get; set; }
        public ISpeedVarianceProvider? SpeedVarianceProviderInstance { get; set; }

        public string? MarkData;
        public Guid MarkID;

        public void Dispose()
        {
        }

        public uint? GetClipLength() => null;

        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex)
        {
            throw new NotImplementedException();
        }


        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int targetWidth, int targetHeight, bool forceResize)
        {
            throw new NotImplementedException();
        }

        public void ReInit()
        {
        }

        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int targetWidth, int targetHeight, bool forceResize, IPicture.PicturePixelMode targetPPB)
        {
            throw new NotImplementedException();
        }

        public void ReInit(IPicture.PicturePixelMode targetPPB)
        {
            throw new NotImplementedException();
        }

        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int requiredWidth, int requiredHeight, IPicture.PicturePixelMode targetPPB)
        {
            throw new NotImplementedException();
        }
    }

}
