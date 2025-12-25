using projectFrameCut.Shared;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Drawing.Processing;
using System;
using System.Collections.Generic;
using System.Text;
using SixLabors.ImageSharp.Processing;
using projectFrameCut.Render.VideoMakeEngine;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Plugin;

namespace projectFrameCut.Render.ClipsAndTracks
{
    public class VideoClip : IClip
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public uint LayerIndex { get; init; } = 0;
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; init; }
        public float FrameTime { get; init; }
        public float SecondPerFrameRatio { get; init; }
        public MixtureMode MixtureMode { get; init; } = MixtureMode.Overlay;
        public string? FilePath { get; init; }
        public Dictionary<string, object>? MixtureArgs { get; init; }
        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public IEffect[]? EffectsInstances { get; init; }

        [System.Text.Json.Serialization.JsonIgnore]
        public IVideoSource? Decoder { get; set; } = null;

        public ClipMode ClipType => ClipMode.VideoClip;
        public string FromPlugin => "projectFrameCut.Render.Plugins.InternalPluginBase";

        public string BindedSoundTrack { get; init; } = "";

        public VideoClip()
        {
            EffectsInstances = EffectHelper.GetEffectsInstances(Effects);

        }

        public IPicture GetFrameRelativeToStartPointOfSource(uint targetFrame) => (Decoder ?? throw new NullReferenceException("Decoder is null. Please init it.")).GetFrame(targetFrame);

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
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; init; }
        public float FrameTime { get; init; }
        public float SecondPerFrameRatio { get; init; }
        public MixtureMode MixtureMode { get; init; } = MixtureMode.Overlay;
        public string? FilePath { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonIgnore]
        public Picture? source { get; set; } = null;

        public ClipMode ClipType => ClipMode.PhotoClip;
        public string FromPlugin => "projectFrameCut.Render.Plugins.InternalPluginBase";

        public string BindedSoundTrack { get; init; } = "";


        public Dictionary<string, object>? MixtureArgs { get; init; }
        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public IEffect[]? EffectsInstances { get; init; }

        public PhotoClip()
        {
            EffectsInstances = EffectHelper.GetEffectsInstances(Effects);

        }


        public IPicture GetFrameRelativeToStartPointOfSource(uint targetFrame) => source ?? throw new NullReferenceException("Source is null. Please init it.");

        void IClip.ReInit()
        {
            source = new Picture(FilePath ?? throw new NullReferenceException($"VideoClip {Id}'s source path is null."));
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
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; init; }
        public float FrameTime { get; init; }
        public float SecondPerFrameRatio { get; init; }
        public MixtureMode MixtureMode { get; init; } = MixtureMode.Overlay;
        public string? filePath { get; } = null;
        public ClipMode ClipType => ClipMode.SolidColorClip;
        public string FromPlugin => "projectFrameCut.Render.Plugins.InternalPluginBase";
        public Dictionary<string, object>? MixtureArgs { get; init; }
        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public IEffect[]? EffectsInstances { get; init; }

        public string BindedSoundTrack { get; init; } = "";


        string? IClip.FilePath { get => null; init => throw new InvalidOperationException("Set path is not supported by this type of clip."); }

        public ushort R { get; init; }
        public ushort G { get; init; }
        public ushort B { get; init; }
        public float? A { get; init; } = null;

        public int targetWidth { get; init; } = 1920;
        public int targetHeight { get; init; } = 1080;

        public IPicture GetFrameRelativeToStartPointOfSource(uint targetFrame, int tWidth, int tHeight) => Picture.GenerateSolidColor(tWidth, tHeight, R, G, B, A);

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
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; init; }
        public float FrameTime { get; init; }
        public float SecondPerFrameRatio { get; init; }
        public MixtureMode MixtureMode { get; init; } = MixtureMode.Overlay;
        public string? filePath { get; } = null;
        public ClipMode ClipType => ClipMode.TextClip;
        public string FromPlugin => "projectFrameCut.Render.Plugins.InternalPluginBase";
        public Dictionary<string, object>? MixtureArgs { get; init; }
        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public IEffect[]? EffectsInstances { get; init; }

        public string BindedSoundTrack { get; init; } = "";


        string? IClip.FilePath { get => null; init => throw new InvalidOperationException("Set path is not supported by this type of clip."); }

        public List<TextClipEntry> TextEntries { get; init; } = new List<TextClipEntry>();

        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int targetWidth, int targetHeight, bool forceResize)
        {
            Image<Rgba64> canvas = new(targetWidth, targetHeight);

            foreach (var entry in TextEntries)
            {
                Font font;
                if (GetFont().TryGet(entry.fontFamily, out var family))
                {
                    font = family.CreateFont(entry.fontSize);
                }
                else
                {
                    Log($"Font {entry.fontFamily} not available, try fallback to HarmonyOS_Sans_SC_Regular...");
                    if (GetFont().TryGet("HarmonyOS_Sans_SC_Regular", out var defaultFamily))
                    {
                        font = defaultFamily.CreateFont(entry.fontSize);
                    }
                    else
                    {
                        Log($"Font HarmonyOS_Sans_SC_Regular not available, try fallback to OS default one...");

                        var first = GetFont().Families.FirstOrDefault();
                        if (first != default)
                            font = first.CreateFont(entry.fontSize);
                        else
                            continue;
                    }
                }

                var color = new Rgba64(entry.r, entry.g, entry.b, (ushort)((entry.a ?? 1.0f) * 65535));
                canvas.Mutate(i => i.DrawText(entry.text, font, color, new PointF(entry.x, entry.y)));
            }

            return new Picture(canvas)
            {
                ProcessStack = $"Created from text '{TextEntries.Aggregate("",(a,b) => $"{a},{b.text}")}'"
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

        }

        public void Dispose()
        {

        }

        public uint? GetClipLength() => Duration;

        public record TextClipEntry(string text, int x, int y, string fontFamily, float fontSize, ushort r, ushort g, ushort b, float? a = null);

        private FontCollection fontsCache = null!;
        public  FontCollection GetFont()
        {
            if (fontsCache is not null) return fontsCache;
            fontsCache = new FontCollection();
            fontsCache.AddSystemFonts();
            foreach (var item in Directory.GetFiles(AppContext.BaseDirectory,"*.ttf"))
            {
                fontsCache.Add(item);
            }
            return fontsCache;

        }
    }

}
