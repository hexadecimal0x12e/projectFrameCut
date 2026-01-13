using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.VideoMakeEngine;
using projectFrameCut.Shared;
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

namespace projectFrameCut.Render.Rendering
{
    public static class Timeline
    {
        //public static ConcurrentDictionary<string, IComputer> ComputerCache = new();
        public static ConcurrentDictionary<MixtureMode, IMixture> MixtureCache = new();
        public static Func<int, int, IPicture> FallBackImageGetter = (w, h) => Picture.GenerateSolidColor(w, h, 0, 0, 0, null);


        public static IEnumerable<OneFrame> GetFramesInOneFrame(IClip[] video, uint targetFrame, int targetWidth, int targetHeight, bool forceResize = false)
        {
            List<OneFrame> result = new List<OneFrame>();
            foreach (var clip in video)
            {
                if (clip.StartFrame <= targetFrame && clip.Duration * clip.SecondPerFrameRatio + clip.StartFrame >= targetFrame)
                {
                    if (result.Any((c) => c.LayerIndex == clip.LayerIndex))
                    {
                        throw new InvalidDataException($"Two or more clips ({result.Where((c) => c.LayerIndex == clip.LayerIndex).Aggregate<OneFrame, string>(clip.FilePath ?? "Clip@" + clip.Id, (a, b) => $"{a},{b.ParentClip.FilePath}")}) in the same layer {clip.LayerIndex} are overlapping at frame {targetFrame}. Please fix the timeline data.");
                    }
                    var frame = clip.GetFrame(targetFrame, targetWidth,targetHeight);
                    if (frame is not null)
                    {
                        result.Add(new OneFrame(targetFrame, clip, frame));
                    }
                }
            }

            return result.OrderBy((c) => c.LayerIndex);
        }

        public static string GetFrameHash(IClip[] video, uint targetFrame)
        {
            List<OneFrame> result = new List<OneFrame>();
            foreach (var clip in video)
            {
                if (clip.StartFrame <= targetFrame && clip.Duration * clip.SecondPerFrameRatio + clip.StartFrame >= targetFrame)
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


        public static IPicture MixtureLayers(IEnumerable<OneFrame> frames, uint frameIndex, int targetWidth, int targetHeight, int targetPPB = 8)
        {
            try
            {
                IPicture result = Picture.GenerateSolidColor(targetWidth, targetHeight, 0, 0, 0, 0);
                foreach (var srcFrame in frames)
                {
                    // Don't resize the frame before applying effects!
                    // The ResizeEffect and PlaceEffect will handle sizing and positioning.
                    IPicture effected = srcFrame.Clip;
                    List<IPictureProcessStep> steps = new();
                    bool lastIsProcessStep = false;
                    foreach (var effect in srcFrame?.Effects?.OrderBy(e => e.Index)?.ToList() ?? new List<IEffect>())
                    {
                        if (effect.YieldProcessStep != lastIsProcessStep)
                        {
                            if (steps.Count > 0)
                            {
                                effected = PictureProcesser.Process(steps, effected, targetPPB);
                                steps.Clear();
                            }
                            lastIsProcessStep = effect.YieldProcessStep;
                        }

                        if (effect is IContinuousEffect c)
                        {
                            ProcessContinuousEffect(frameIndex, srcFrame.ParentClip, PluginManager.CreateComputer(effect.NeedComputer), ref effected, steps, ref lastIsProcessStep, effect, c, targetWidth, targetHeight);
                        }
                        else
                        {
                            ProcessEffect(ref effected, steps, ref lastIsProcessStep, effect, PluginManager.CreateComputer(effect.NeedComputer), targetWidth, targetHeight);
                        }
                    }
                    if (steps.Count > 0)
                    {
                        effected = PictureProcesser.Process(steps, effected, targetPPB);
                        steps.Clear();
                    }
                    var mix = GetMixer(srcFrame.MixtureMode);

                    result = MixtureCache.GetOrAdd(srcFrame!.MixtureMode, mix)
                                    .Mix(result, effected,
                                       mix.ComputerId is not null ? PluginManager.CreateComputer(mix.ComputerId) : null);

                }
                //LogDiagnostic($"Result's diag info:{result?.GetDiagnosticsInfo() ?? "unknown"}");
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
                Log(ex, $"Render frame {frameIndex}", "Timeline");
                throw;
                return new Picture(Path.Combine(AppContext.BaseDirectory, "FallbackResources", "MediaNotAvailable.png")).Resize(targetWidth, targetHeight, true);
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

        private static void ProcessEffect(ref IPicture frame, List<IPictureProcessStep> steps, ref bool lastIsProcessStep, IEffect item, IComputer? computer, int width, int height)
        {
            if (item.YieldProcessStep)
            {
                lastIsProcessStep = true;
                try
                {
                    var step = item.GetStep(frame, width, height);
                    steps.Add(step);
                    if (IPicture.DiagImagePath is not null) LogDiagnostic($"Process step for effect {item.Name}({item.TypeName}) : {step.GetProcessStack()}");
                }
                catch (Exception ex)
                {
                    Log($"[Render] WARN: Failed to get process steps for effect {item.Name}: {ex}");
                    lastIsProcessStep = false;
                    frame = item.Render(frame, computer, width, height);
                }
            }
            else
            {
                frame = item.Render(frame, computer, width, height);
            }
        }

        private static void ProcessContinuousEffect(uint targetFrame, IClip clip, IComputer? computer, ref IPicture frame, List<IPictureProcessStep> steps, ref bool lastIsProcessStep, IEffect item, IContinuousEffect c, int width, int height)
        {
            if (c.EndPoint == 0 && c.EndPoint == 0)
            {
                c.StartPoint = (int)(clip.StartFrame);
                c.EndPoint = (int)(c.StartPoint + clip.Duration * clip.SecondPerFrameRatio);
            }
            if (c.YieldProcessStep)
            {
                lastIsProcessStep = true;
                try
                {
                    var step = c.GetStep(frame, targetFrame, width, height);
                    steps.Add(step);
                    if (IPicture.DiagImagePath is not null) LogDiagnostic($"Process step for effect {c.Name}({c.TypeName}) : {step.GetProcessStack()}");

                }
                catch (Exception ex)
                {
                    Log($"[Render] WARN: Failed to get process steps for continuous effect {c.Name}: {ex}");
                    lastIsProcessStep = false;
                    frame = c.Render(frame, targetFrame, computer, width, height);
                }

            }
            else
            {
                frame = c.Render(frame, targetFrame, computer, width, height);
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