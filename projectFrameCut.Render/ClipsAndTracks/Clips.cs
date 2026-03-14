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

namespace projectFrameCut.Render.ClipsAndTracks
{
    public class VideoClip : IClip
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public uint LayerIndex { get; init; } = 0;
        public uint SubLayerIndex { get; init; }
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; init; }
        public float FrameTime { get; init; }
        public float SecondPerFrameRatio { get; init; }

        public string? FilePath { get; set; }

        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public IEffect[]? EffectsInstances { get; init; }
        public Dictionary<string, object> ExtraData { get; set; }
        public bool ExtendToWholeDraft { get; set; }

        public bool NeedFilePath => true;

        [System.Text.Json.Serialization.JsonIgnore]
        public IVideoSource? Decoder { get; set; } = null;

        public ClipMode ClipType => ClipMode.VideoClip;
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;

        public string BindedSoundTrack { get; init; } = "";

        public VideoClip()
        {
            EffectsInstances = EffectHelper.GetEffectsInstances(Effects);

        }

        public IPicture GetFrameRelativeToStartPointOfSource(uint targetFrame, int targetWidth, int targetHeight, bool forceResize, IPicture.PicturePixelMode targetPPB) => (Decoder ?? throw new NullReferenceException("Decoder is null. Please init it.")).GetFrame(targetFrame).Resize(targetWidth, targetHeight, forceResize).ToBitPerPixel(targetPPB);

        void IClip.ReInit(IPicture.PicturePixelMode targetPPB)
        {
            Decoder = PluginManager.CreateVideoSource(FilePath ?? throw new NullReferenceException($"VideoClip {Id}'s source path is null."));
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
        public required string Name { get; init; }
        public uint LayerIndex { get; init; } = 0;
        public uint SubLayerIndex { get; init; }
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; init; }
        public float FrameTime { get; init; }
        public float SecondPerFrameRatio { get; init; }

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
        public IEffect[]? EffectsInstances { get; init; }

        public PhotoClip()
        {
            EffectsInstances = EffectHelper.GetEffectsInstances(Effects);

        }
        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int targetWidth, int targetHeight, bool forceResize, IPicture.PicturePixelMode targetPPB) => source?.Resize(targetWidth, targetHeight, forceResize).ToBitPerPixel(targetPPB) ?? throw new NullReferenceException("Source is null. Please init it.");

        void IClip.ReInit(IPicture.PicturePixelMode targetPPB)
        {
            if (FilePath is null) throw new NullReferenceException($"PhotoClip {Id}'s source path is null.");
            source = targetPPB == 16 ? new Picture16bpp(FilePath) : new Picture8bpp(FilePath);
            source.Disposed = null;
            source.ProcessStack = new List<PictureProcessStack>
            {
                new PictureProcessStack
                {
                    Operator = GetType(),
                    OperationDisplayName = $"Created for PhotoClip {Name} ({Id})",
                    ProcessingFuncStackTrace = null,
                    Properties = new Dictionary<string, object>
                    {
                        { "SourcePath", FilePath }
                    }
                }
            };
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
        public required string Name { get; init; }
        public uint LayerIndex { get; init; } = 0;
        public uint SubLayerIndex { get; init; }
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; init; }
        public float FrameTime { get; init; }
        public float SecondPerFrameRatio { get; init; }

        public string? filePath { get; } = null;
        public ClipMode ClipType => ClipMode.SolidColorClip;
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;

        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public IEffect[]? EffectsInstances { get; init; }
        public bool NeedFilePath => false;
        public Dictionary<string, object> ExtraData { get; set; }
        public bool ExtendToWholeDraft { get; set; }

        public string BindedSoundTrack { get; init; } = "";


        string? IClip.FilePath { get => null; set => throw new InvalidOperationException("Set path is not supported by this type of clip."); }

        public ushort R { get; init; }
        public ushort G { get; init; }
        public ushort B { get; init; }
        public float? A { get; init; } = null;

        public int targetWidth { get; init; } = 1920;
        public int targetHeight { get; init; } = 1080;

        public IPicture GetFrameRelativeToStartPointOfSource(uint targetFrame, int tWidth, int tHeight, bool forceResize) => Picture16bpp.GenerateSolidColor(tWidth, tHeight, R, G, B, A);
        public IPicture GetFrameRelativeToStartPointOfSource(uint targetFrame, int tWidth, int tHeight, bool forceResize, IPicture.PicturePixelMode targetPPB) => targetPPB.Value switch
        {
            16 => Picture16bpp.GenerateSolidColor(tWidth, tHeight, R, G, B, A),
            8 => Picture8bpp.GenerateSolidColor(tWidth, tHeight, (byte)(R / 257), (byte)(G / 257), (byte)(B / 257), A),
            _ => throw new NotSupportedException($"Unsupported target pixel mode {targetPPB}.")
        };

        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex) => Picture16bpp.GenerateSolidColor(targetWidth, targetHeight, R, G, B, A);

        public SolidColorClip()
        {
            EffectsInstances = EffectHelper.GetEffectsInstances(Effects);
        }

        public void ReInit()
        {

        }

        public void Dispose()
        {

        }

        public uint? GetClipLength() => Duration;


        public void ReInit(IPicture.PicturePixelMode targetPPB)
        {
        }
    }

    public class TextClip : IClip
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public uint LayerIndex { get; init; } = 0;
        public uint SubLayerIndex { get; init; }
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; init; }
        public float FrameTime { get; init; }
        public float SecondPerFrameRatio { get; init; }

        public string? filePath { get; } = null;
        public ClipMode ClipType => ClipMode.TextClip;
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;

        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public IEffect[]? EffectsInstances { get; init; }
        public bool NeedFilePath => false;
        public Dictionary<string, object> ExtraData { get; set; }
        public bool ExtendToWholeDraft { get; set; }

        public string BindedSoundTrack { get; init; } = "";


        string? IClip.FilePath { get => null; set => throw new InvalidOperationException("Set path is not supported by this type of clip."); }

        public List<TextClipEntry> TextEntries { get; init; } = new List<TextClipEntry>();

        public string FontPath { get; set; } = string.Empty;

        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int targetWidth, int targetHeight, bool forceResize)
            => GetFrameRelativeToStartPointOfSource(frameIndex, targetWidth, targetHeight, forceResize, IPicture.PicturePixelMode.BytePicture);

        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int targetWidth, int targetHeight, bool forceResize, IPicture.PicturePixelMode targetPPB)
        {
            Image<Rgba64> canvas = new(targetWidth, targetHeight);

            foreach (var entry in TextEntries)
            {
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
                            { "TextEntries", JsonSerializer.Serialize(TextEntries, JsonSerializerOptions.Web) },
                            { "FontPath", FontPath }
                        }
                    }
                };

            return targetPPB.Value switch
            {
                8 => new Picture8bpp(canvas) { ProcessStack = stack },
                16 => new Picture16bpp(canvas) { ProcessStack = stack },
                _ => throw new NotSupportedException($"Unsupported target pixel mode {targetPPB}.")
            };
        }

        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex)
        {
            throw new NotSupportedException();
        }

        public TextClip()
        {
            EffectsInstances = EffectHelper.GetEffectsInstances(Effects);
        }

        public void ReInit(IPicture.PicturePixelMode targetPPB)
        {
            if (!string.IsNullOrWhiteSpace(FontPath))
            {
                fontsCache.Add(FontPath);
            }

        }


        public void Dispose()
        {

        }

        public uint? GetClipLength() => Duration;


        private static FontCollection fontsCache = new();
        private static bool hasGetFontCache = false;
        public static FontCollection FontsCache { get { return fontsCache; } }
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
        public string Name { get; init; }
        public string BindedSoundTrack { get; init; }
        public uint LayerIndex { get; init; }
        public uint SubLayerIndex { get; init; }
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; init; }
        public float FrameTime { get; init; }
        public float SecondPerFrameRatio { get; init; }
        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public IEffect[]? EffectsInstances { get; init; }
        public string? FilePath { get; set; }
        public Dictionary<string, object> ExtraData { get; set; }
        public bool ExtendToWholeDraft { get; set; }


        public bool NeedFilePath => false;

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
    }

}
