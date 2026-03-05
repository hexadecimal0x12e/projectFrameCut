using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Shared;
using System;
using System.Collections;
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
                        continue; //keep same behavior in Renderer

                        //throw new InvalidDataException($"Two or more clips ({result.Where((c) => c.LayerIndex == clip.LayerIndex).Aggregate<OneFrame, string>(clip.FilePath ?? "Clip@" + clip.Id, (a, b) => $"{a},{b.ParentClip.FilePath}")}) in the same layer {clip.LayerIndex} are overlapping at frame {targetFrame}. Please fix the timeline data.");
                    }
                    IPicture frame = null!;
                    if (clip is TransformContainer c)
                    {
                        if (c.Transform == null) c.ReInit();
                        var t = c.Transform;
                        if (t == null)
                        {
                            Log($"[Timeline] WARN: Transform for clip {c.Id} is null; skipping transform for frame {targetFrame}");
                            frame = null;
                        }
                        else
                        {
                            var leftClip = video.FirstOrDefault(cc => cc.Id == t.BindedLeftClip.ToString());
                            var rightClip = video.FirstOrDefault(cc => cc.Id == t.BindedRightClip.ToString());
                            if (leftClip == null || rightClip == null)
                            {
                                Log($"[Timeline] WARN: Transform inputs not found for transform {c.Id}. Skipping frame {targetFrame}");
                                frame = null;
                            }
                            else
                            {
                                frame = TransformProcessing.ProcessTransform(leftClip, rightClip, t, targetWidth, targetHeight, targetFrame);
                            }
                        }
                    }
                    else
                    {
                        frame = clip.GetFrame(targetFrame, targetWidth, targetHeight, true);
                    }
                    bool isAI = false;
                    if (clip.ExtraData.TryGetValue("IsAI", out var aiMark))
                    {
                        if (aiMark is bool) isAI = (bool)aiMark;
                        else if (aiMark is string s && bool.TryParse(s, out var parsed)) isAI = parsed;
                        else if (aiMark is JsonElement je && je.ValueKind == JsonValueKind.True) isAI = true;
                    }

                    if (frame is not null)
                    {
                        if (isAI) frame = EffectProcessing.ProcessAIWatermark(frame, null);
                        result.Add(new OneFrame(targetFrame, clip, frame));
                    }
                }
            }

            return result.OrderBy(x => x.LayerIndex >= Renderer.SubTrackOffset ? 1 : 0).ThenByDescending(x => x.LayerIndex).ThenByDescending(x => x.ParentClip.SubLayerIndex);
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
                        continue; //keep same behavior in Renderer
                        //throw new InvalidDataException($"Two or more clips ({result.Where((c) => c.LayerIndex == clip.LayerIndex).Aggregate<OneFrame, string>(clip.FilePath ?? "Clip@" + clip.Id, (a, b) => $"{a},{b.ParentClip.FilePath}")}) in the same layer {clip.LayerIndex} are overlapping at frame {targetFrame}. Please fix the timeline data.");
                    }
                    result.Add(new OneFrame(targetFrame, clip, null!));
                }
            }

            var f = JsonSerializer.Serialize(result);

#if DEBUG
            Log($"Frame:\r\n{f}\r\n---");
#endif

            if (f == "[]") return "nullframe";

            return SHA256.HashData(Encoding.UTF8.GetBytes(f)).Aggregate("0x", ((b, c) => b + c.ToString("x2")));
        }


        public static IPicture MixtureLayers(IEnumerable<OneFrame> frames, uint frameIndex, int targetWidth, int targetHeight, int targetPPB = 8, Action<IEffect, IPicture>? AfterEffect = null)
        {
            try
            {
                IPicture? result = null;
                ConcurrentDictionary<string, object> bindableEffectResultCache = new();
                Dictionary<string, object> bindableEffectResultCache2 = new();
                Dictionary<string, bool> producedValueTable = new();
                foreach (var srcFrame in frames)
                {
                    // Don't resize the frame before applying effects!
                    // The ResizeEffect and PlaceEffect will handle sizing and positioning.
                    IPicture effected = srcFrame.Clip;
                    List<IPictureProcessStep> steps = new();
                    bool lastIsProcessStep = false;
                    var effectsList = srcFrame?.Effects?.OrderBy(e => e.Index) ?? (IEnumerable<IEffect>)[];
                    foreach (var effect in effectsList)
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
                            EffectProcessing.ProcessContinuousEffect(frameIndex, srcFrame.ParentClip, PluginManager.CreateComputer(effect.NeedComputer), ref effected, steps, ref lastIsProcessStep, effect, c, targetWidth, targetHeight);
                        }
                        else if (effect is IBindableArgumentEffect b)
                        {
                            _ = EffectProcessing.ProcessBindableArgsEffect(frameIndex, ref effected, ref bindableEffectResultCache, bindableEffectResultCache2, srcFrame.ParentClip, steps, ref lastIsProcessStep, b, PluginManager.CreateComputer(effect.NeedComputer), targetWidth, targetHeight); //single frame render, no need to remove
                        }
                        else
                        {
                            EffectProcessing.ProcessEffect(ref effected, steps, ref lastIsProcessStep, effect, PluginManager.CreateComputer(effect.NeedComputer), targetWidth, targetHeight);
                        }
                        if (AfterEffect is not null)
                        {
                            if (steps.Count > 0)
                            {
                                AfterEffect?.Invoke(effect, PictureProcesser.Process(steps, effected, targetPPB));
                            }
                            else
                            {
                                AfterEffect?.Invoke(effect, effected);

                            }
                        }


                    }
                    if (steps.Count > 0)
                    {
                        effected = PictureProcesser.Process(steps, effected, targetPPB);
                        steps.Clear();
                    }

                    if (result is null) result = effected;
                    else
                    {
                        result = OverlayMixture.Mix(result, effected, PluginManager.CreateComputer("OverlayComputer"), targetPPB);
                    }
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
                result = OverlayMixture
                               .Mix(FallBackImageGetter(targetWidth, targetHeight), result, PluginManager.CreateComputer("OverlayComputer"), targetPPB)
                               .Resize(targetWidth, targetHeight, true);
                if (PictureProcesser.SaveDiagResult)
                {
                    var opId = Guid.NewGuid();
                    File.WriteAllText(Path.Combine(PictureProcesser.DiagResultPath, $"diag-render-{frameIndex}-{opId}-stacks.txt"), PictureProcessStack.FormatProcessStackForLog(result.ProcessStack, 100000));
                }
                return result;
            }
            catch (Exception ex)
            {
                Log(ex, $"Render frame {frameIndex}", "Timeline");
                throw;
            }

        }

        private static PlaceEffect_ImageSharp Placer = new()
        {
            StartX = 0,
            StartY = 0
        };



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
            Effects = EffectHelper.GetEffectsInstances(parent.Effects);
        }
    }
}