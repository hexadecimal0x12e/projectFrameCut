using projectFrameCut.Shared;
using projectFrameCut.VideoMakeEngine;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using static projectFrameCut.Render.Video;

namespace projectFrameCut.Render
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
        public IDecoderContext? Decoder { get; set; } = null;

        public ClipMode ClipType => ClipMode.VideoClip;


        public VideoClip()
        {
            EffectsInstances = IClip.GetEffectsInstances(Effects);

        }

        public Picture GetFrameRelativeToStartPointOfSource(uint targetFrame) => (Decoder ?? throw new NullReferenceException("Decoder is null. Please init it.")).GetFrame(targetFrame);

        void IClip.ReInit()
        {
            Decoder = new Video(FilePath ?? throw new NullReferenceException($"VideoClip {Id}'s source path is null.")).Decoder;
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


        public Dictionary<string, object>? MixtureArgs { get; init; }
        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public IEffect[]? EffectsInstances { get; init; }

        public PhotoClip()
        {
            EffectsInstances = IClip.GetEffectsInstances(Effects);

        }


        public Picture GetFrameRelativeToStartPointOfSource(uint targetFrame) => source ?? throw new NullReferenceException("Source is null. Please init it.");

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
        public ClipMode ClipType => ClipMode.Special;
        public Dictionary<string, object>? MixtureArgs { get; init; }
        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public IEffect[]? EffectsInstances { get; init; }

        string? IClip.FilePath { get => null; init => throw new InvalidOperationException("Set path is not supported by this type of clip."); }

        public ushort R { get; init; }
        public ushort G { get; init; }
        public ushort B { get; init; }
        public float? A { get; init; } = null;

        public int targetWidth { get; init; } = 1920;
        public int targetHeight { get; init; } = 1080;

        public Picture GetFrameRelativeToStartPointOfSource(uint targetFrame, int tWidth, int tHeight) => Picture.GenerateSolidColor(tWidth, tHeight, R, G, B, A);

        public Picture GetFrameRelativeToStartPointOfSource(uint frameIndex) => Picture.GenerateSolidColor(targetWidth, targetHeight, R, G, B, A);

        public SolidColorClip()
        {
            EffectsInstances = IClip.GetEffectsInstances(Effects);
        }

        public void ReInit()
        {

        }

        public void Dispose()
        {

        }

        public uint? GetClipLength() => Duration;
    }



    public class Timeline
    {
        public static ConcurrentDictionary<string,IComputer> ComputerCache = new();
        public static ConcurrentDictionary<MixtureMode,IMixture> MixtureCache = new();


        public static IEnumerable<OneFrame> GetFramesInOneFrame(IClip[] video, uint targetFrame, int targetWidth, int targetHeight, bool forceResize = false)
        {
            List<OneFrame> result = new List<OneFrame>();
            foreach (var clip in video)
            {
                if (clip.StartFrame * clip.SecondPerFrameRatio <= targetFrame && clip.Duration * clip.SecondPerFrameRatio + clip.StartFrame * clip.SecondPerFrameRatio >= targetFrame)
                {
                    if (result.Any((c) => c.LayerIndex == clip.LayerIndex))
                    {
                        throw new InvalidDataException($"Two or more clips ({result.Where((c) => c.LayerIndex == clip.LayerIndex).Aggregate<OneFrame, string>(clip.FilePath ?? "Clip@" + clip.Id, (a, b) => $"{a},{b.ParentClip.FilePath}")}) in the same layer {clip.LayerIndex} are overlapping at frame {targetFrame}. Please fix the timeline data.");
                    }
                    result.Add(new OneFrame(targetFrame, clip, clip.GetFrame(targetFrame, targetWidth, targetHeight, forceResize)));
                }
            }

            return result.OrderByDescending((c) => c.LayerIndex);
        }

        public static string GetFrameHash(IClip[] video, uint targetFrame)
        {
            List<OneFrame> result = new List<OneFrame>();
            foreach (var clip in video)
            {
                if (clip.StartFrame * clip.SecondPerFrameRatio <= targetFrame && clip.Duration * clip.SecondPerFrameRatio + clip.StartFrame * clip.SecondPerFrameRatio >= targetFrame)
                {
                    if (result.Any((c) => c.LayerIndex == clip.LayerIndex))
                    {
                        throw new InvalidDataException($"Two or more clips ({result.Where((c) => c.LayerIndex == clip.LayerIndex).Aggregate<OneFrame, string>(clip.FilePath ?? "Clip@" + clip.Id, (a, b) => $"{a},{b.ParentClip.FilePath}")}) in the same layer {clip.LayerIndex} are overlapping at frame {targetFrame}. Please fix the timeline data.");
                    }
                    result.Add(new OneFrame(targetFrame, clip, null));
                }
            }

            var f = JsonSerializer.Serialize(result);

#if DEBUG
            Log($"Frame:\r\n{f}\r\n---");
#endif

            if (f == "[]") return "nullframe";

            return SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(f)).Aggregate("0x", ((b, c) => b + c.ToString("x2")));
        }


        public static Picture MixtureLayers(IEnumerable<OneFrame> frames, uint frameIndex, int targetWidth, int targetHeight)
        {
            try
            {
                Picture? result = null;
                foreach (var srcFrame in frames)
                {
                    var frame = srcFrame.Clip.Resize(targetWidth, targetHeight, true);
                    Picture effected = frame;
                    foreach (var effect in srcFrame?.Effects ?? [])
                    {
                        effected = effect.Render(
                            effected,
                            ComputerCache.GetOrAdd(
                                effect.TypeName,
                                AcceleratedComputerBridge.RequireAComputer?.Invoke(effect.TypeName)
                                    is IComputer c1 ? c1 :
                                    throw new NotSupportedException($"Mixture mode {srcFrame.MixtureMode} is not supported in accelerated computer bridge.")));
                    }

                    if (result == null)
                    {
                        result = effected;
                    }
                    else
                    {
                        result = MixtureCache.GetOrAdd(
                            srcFrame!.MixtureMode, GetMixer(srcFrame.MixtureMode))
                                .Mix(effected, result, 
                                    ComputerCache.GetOrAdd(srcFrame.MixtureMode.ToString(), 
                                        AcceleratedComputerBridge.RequireAComputer?.Invoke(srcFrame.MixtureMode.ToString()) 
                                            is IComputer c ? c : 
                                            throw new NotSupportedException($"Mixture mode {srcFrame.MixtureMode} is not supported in accelerated computer bridge.")))
                                .Resize(targetWidth, targetHeight, true);
                    }
                }

                return result ?? Picture.GenerateSolidColor(targetWidth, targetHeight, 0, 0, 0, null);
            }
            catch (Exception ex)
            {
                Log(ex);
                return new Picture(Path.Combine(AppContext.BaseDirectory, "FallbackResources", "MediaNotAvailable.png")).Resize(targetHeight, targetHeight, true);
            }

        }

        private static IMixture GetMixer(MixtureMode mixtureMode)
        {
            switch (mixtureMode)
            {
                case MixtureMode.Overlay:
                    return new OverlayMixture();
                default:
                    throw new NotSupportedException($"Mixture mode {mixtureMode} is not supported.");
            }
        }



        public static List<OverlapInfo> FindOverlaps(IEnumerable<ClipDraftDTO>? clips, uint allowedOverlapFrames = 5)
        {
            var result = new List<OverlapInfo>();
            if (clips == null) return result;

            var groups = clips
                .Where(c => c != null)
                .GroupBy(c => c.LayerIndex);

            foreach (var group in groups)
            {
                var ordered = group.OrderBy(c => c.StartFrame).ToList();
                int n = ordered.Count;
                for (int i = 0; i < n; i++)
                {
                    var a = ordered[i];
                    long aStart = (long)a.StartFrame;
                    long aEnd = aStart + (long)a.Duration;

                    for (int j = i + 1; j < n; j++)
                    {
                        var b = ordered[j];
                        long bStart = (long)b.StartFrame;

                        if (bStart >= aEnd)
                        {
                            break;
                        }

                        long overlap = aEnd - bStart;
                        if (overlap > (long)allowedOverlapFrames)
                        {
                            result.Add(new OverlapInfo($"{a.Id ?? "unknown ID"} ({a.Name ?? "unknown Name"})", $"{b.Id ?? "unknown ID"} ({b.Name ?? "unknown Name"})", overlap, a.LayerIndex));
                        }
                    }
                }
            }

            return result;
        }

        public static bool HasOverlap(IEnumerable<ClipDraftDTO>? clips, uint allowedOverlapFrames = 5)
            => FindOverlaps(clips, allowedOverlapFrames).Count > 0;





        public class OverlapInfo
        {
            public required string ClipAId { get; set; }
            public required string ClipBId { get; set; }
            public required long OverlapFrames { get; set; }
            public required uint LayerIndex { get; set; }

            [SetsRequiredMembers]
            public OverlapInfo(string clipAId, string clipBId, long overlapFrames, uint layerIndex)
            {
                ClipAId = clipAId;
                ClipBId = clipBId;
                OverlapFrames = overlapFrames;
                LayerIndex = layerIndex;
            }


        }
    }
}
