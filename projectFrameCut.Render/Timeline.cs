using projectFrameCut.Render.Plugins;
using projectFrameCut.Shared;
using projectFrameCut.VideoMakeEngine;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
    public class Timeline
    {
        //public static ConcurrentDictionary<string, IComputer> ComputerCache = new();
        public static ConcurrentDictionary<MixtureMode, IMixture> MixtureCache = new();
        public static Func<int, int, IPicture> FallBackImageGetter = (w, h) => Picture.GenerateSolidColor(w, h, 0, 0, 0, null);


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
                    result.Add(new OneFrame(targetFrame, clip, null!));
                }
            }

            var f = JsonSerializer.Serialize(result);

#if DEBUG
            Log($"Frame:\r\n{f}\r\n---");
#endif

            if (f == "[]") return "nullframe";

            return SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(f)).Aggregate("0x", ((b, c) => b + c.ToString("x2")));
        }


        public static IPicture MixtureLayers(IEnumerable<OneFrame> frames, uint frameIndex, int targetWidth, int targetHeight)
        {
            try
            {
                IPicture? result =null;
                foreach (var srcFrame in frames)
                {
                    var frame = srcFrame.Clip.Resize(targetWidth, targetHeight, true);
                    IPicture effected = frame;
                    foreach (var effect in srcFrame?.Effects ?? [])
                    {
                        effected = effect.Render(
                                   effected,
                                   effect.NeedComputer is not null ? PluginManager.CreateComputer(effect.NeedComputer) : null,
                                   targetWidth, targetHeight);
                    }
                    var mix = GetMixer(srcFrame.MixtureMode);

                    result = result is null ? effected :
                                    MixtureCache.GetOrAdd(srcFrame!.MixtureMode, mix)
                                    .Mix(result, effected,
                                       mix.ComputerId is not null ? PluginManager.CreateComputer(mix.ComputerId) : null)
                                    .Resize(targetWidth, targetHeight, true);

                }
                LogDiagnostic($"Result's diag info:{result?.GetDiagnosticsInfo() ?? "unknown"}");
                if (result?.Width == targetWidth && result?.Height == targetHeight)
                {
                    goto ok;
                }
                else if (result is null)
                {
                    return Picture.GenerateSolidColor(targetWidth, targetHeight, 0, 0, 0, 0);
                }
                else
                {
                    result = Placer.Render(result, null, targetWidth, targetHeight);
                }
            ok:
                result = MixtureCache.GetOrAdd(
                           MixtureMode.Overlay, GetMixer(MixtureMode.Overlay))
                               .Mix(FallBackImageGetter(targetWidth, targetHeight), result, PluginManager.CreateComputer("OverlayComputer"))
                               .Resize(targetWidth, targetHeight, true);
                return result;
            }
            catch (Exception ex)
            {
                Log(ex,$"Render frame {frameIndex}","Timeline");
                throw;
                return new Picture(Path.Combine(AppContext.BaseDirectory, "FallbackResources", "MediaNotAvailable.png")).Resize(targetHeight, targetHeight, true);
            }

        }

        private static PlaceEffect Placer = new()
        {
            StartX = 0,
            StartY = 0
        };

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

    public class OneFrame
    {
        public uint FrameNumber { get; init; }
        public IPicture Clip { get; init; }
        public uint LayerIndex { get; init; } = 0;
        public MixtureMode MixtureMode { get; init; } = MixtureMode.Overlay;
        public IEffect[] Effects { get; init; } = Array.Empty<IEffect>();
        public IClip ParentClip { get; init; }
        public OneFrame(uint frameNumber, IClip parent, IPicture pic)
        {
            FrameNumber = frameNumber;
            ParentClip = parent;
            Clip = pic;
            LayerIndex = parent.LayerIndex;
            MixtureMode = parent.MixtureMode;
            Effects = projectFrameCut.Render.VideoMakeEngine.EffectHelper.GetEffectsInstances(parent.Effects);
        }
    }
}
