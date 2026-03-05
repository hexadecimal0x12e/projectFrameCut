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
        public MixtureMode MixtureMode { get; init; } = MixtureMode.Overlay;
        public string? FilePath { get; set; }
        public Dictionary<string, object>? MixtureArgs { get; init; }
        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public IEffect[]? EffectsInstances { get; init; }
        public Dictionary<string, object> ExtraData { get; set; }

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

        public IPicture GetFrameRelativeToStartPointOfSource(uint targetFrame, int targetWidth, int targetHeight, bool forceResize) => (Decoder ?? throw new NullReferenceException("Decoder is null. Please init it.")).GetFrame(targetFrame).Resize(targetWidth, targetHeight, forceResize);

        void IClip.ReInit()
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
        public MixtureMode MixtureMode { get; init; } = MixtureMode.Overlay;
        public string? FilePath { get; set; } = string.Empty;
        public bool NeedFilePath => true;
        public Dictionary<string, object> ExtraData { get; set; }

        public bool Use16bpp = false;


        [System.Text.Json.Serialization.JsonIgnore]
        public IPicture? source { get; set; } = null;

        public ClipMode ClipType => ClipMode.PhotoClip;
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;

        public string BindedSoundTrack { get; init; } = "";


        public Dictionary<string, object>? MixtureArgs { get; init; }
        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public IEffect[]? EffectsInstances { get; init; }

        public PhotoClip()
        {
            EffectsInstances = EffectHelper.GetEffectsInstances(Effects);

        }
        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int targetWidth, int targetHeight, bool forceResize) => source?.Resize(targetWidth, targetHeight, forceResize) ?? throw new NullReferenceException("Source is null. Please init it.");

        void IClip.ReInit()
        {
            if (FilePath is null) throw new NullReferenceException($"PhotoClip {Id}'s source path is null.");
            source = Use16bpp ? new Picture16bpp(FilePath) : new Picture8bpp(FilePath);
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
        public MixtureMode MixtureMode { get; init; } = MixtureMode.Overlay;
        public string? filePath { get; } = null;
        public ClipMode ClipType => ClipMode.SolidColorClip;
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public Dictionary<string, object>? MixtureArgs { get; init; }
        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public IEffect[]? EffectsInstances { get; init; }
        public bool NeedFilePath => false;
        public Dictionary<string, object> ExtraData { get; set; }

        public string BindedSoundTrack { get; init; } = "";


        string? IClip.FilePath { get => null; set => throw new InvalidOperationException("Set path is not supported by this type of clip."); }

        public ushort R { get; init; }
        public ushort G { get; init; }
        public ushort B { get; init; }
        public float? A { get; init; } = null;

        public int targetWidth { get; init; } = 1920;
        public int targetHeight { get; init; } = 1080;

        public IPicture GetFrameRelativeToStartPointOfSource(uint targetFrame, int tWidth, int tHeight, bool forceResize) => Picture.GenerateSolidColor(tWidth, tHeight, R, G, B, A);

        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex) => Picture.GenerateSolidColor(targetWidth, targetHeight, R, G, B, A);

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
        public MixtureMode MixtureMode { get; init; } = MixtureMode.Overlay;
        public string? filePath { get; } = null;
        public ClipMode ClipType => ClipMode.TextClip;
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public Dictionary<string, object>? MixtureArgs { get; init; }
        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public IEffect[]? EffectsInstances { get; init; }
        public bool NeedFilePath => false;
        public Dictionary<string, object> ExtraData { get; set; }

        public string BindedSoundTrack { get; init; } = "";


        string? IClip.FilePath { get => null; set => throw new InvalidOperationException("Set path is not supported by this type of clip."); }

        public List<TextClipEntry> TextEntries { get; init; } = new List<TextClipEntry>();

        public string FontPath { get; set; } = string.Empty;

        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int targetWidth, int targetHeight, bool forceResize)
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

                // If rotation is specified, draw to a temp layer then rotate it around the entry origin.
                if (Math.Abs(entry.rotation) > 0.0001f)
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

            return new Picture(canvas)
            {
                ProcessStack = new List<PictureProcessStack>
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
                }
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

        public void ReInit()
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
        public MixtureMode MixtureMode { get; init; }
        public Dictionary<string, object>? MixtureArgs { get; init; }
        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public IEffect[]? EffectsInstances { get; init; }
        public string? FilePath { get; set; }
        public Dictionary<string, object> ExtraData { get; set; }

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

    }

}
