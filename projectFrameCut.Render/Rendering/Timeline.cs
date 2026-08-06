using projectFrameCut.Drawing.Effect;
using projectFrameCut.Drawing.Processing.Resizing;
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
        public static Func<int, int, IPicture> FallBackImageGetter = (w, h) => Picture16bpp.GenerateSolidColor(w, h, 0, 0, 0, null);


        public static IEnumerable<OneFrame> GetFramesInOneFrame(
            IClip[] video,
            uint targetFrame,
            int targetWidth,
            int targetHeight,
            bool forceResize = false,
            IPicture.PicturePixelMode? targetPPB = null,
            int projectRelativeWidth = 0,
            int projectRelativeHeight = 0)
        {
            var ppb = targetPPB ?? 8;
            List<OneFrame> result = new List<OneFrame>();
            foreach (var clip in video)
            {
                if (!ClipInitializationFailure.HasDeferredFailures(clip.ExtraData))
                {
                    try
                    {
                        clip.ReInit(ppb);
                        ClipInitializationFailure.Clear(clip);
                    }
                    catch (Exception ex)
                    {
                        ClipInitializationFailure.Mark(clip, "Source or effect initialization", ex);
                        Log(ex, $"Initialize clip {clip.Name} ({clip.Id}); using checkerboard fallback", "Timeline");
                    }
                }
                if (IsFrameInClipRange(clip, targetFrame))
                {
                    var endPoint = clip.StartFrame + clip.GetEffectiveDuration();
                    var actualFrame = clip.GetRelativeFrameIndex(targetFrame) ?? endPoint;
                    //LogDiagnostic($"Clip {clip.Name}, ID {clip.Id}, Start {clip.StartFrame}, End {endPoint}, Duration {clip.Duration}, EffectiveDuration {clip.GetEffectiveDuration()}, GetRelativeFrameIndex for target frame {targetFrame} is {actualFrame}");
                    IPicture frame = null!;
                    int clipTargetWidth = ResolveClipOutputWidth(clip, targetWidth, projectRelativeWidth);
                    int clipTargetHeight = ResolveClipOutputHeight(clip, targetHeight, projectRelativeHeight);
                    try
                    {
                        if (ClipInitializationFailure.IsMarked(clip))
                        {
                            frame = ClipInitializationFailure.CreateFallbackFrame(clipTargetWidth, clipTargetHeight, ppb);
                        }
                        else if (clip is TransformContainer c)
                        {
                            if (c.Transform == null) c.ReInit(ppb);
                            var t = c.Transform;
                            if (t == null)
                            {
                                Log($"[Timeline] WARN: Transform for clip {c.Id} is null; skipping transform for frame {targetFrame}");
                                frame = null;
                            }
                            else
                            {
                                var leftClip = video.FirstOrDefault(cc => cc.Id == t.BindedLeftClip);
                                var rightClip = video.FirstOrDefault(cc => cc.Id == t.BindedRightClip);
                                if (leftClip == null || rightClip == null)
                                {
                                    Log($"[Timeline] WARN: Transform inputs not found for transform {c.Id}. Skipping frame {targetFrame}");
                                    frame = null;
                                }
                                else
                                {
                                    frame = TransformProcessing.ProcessTransform(leftClip, rightClip, t, clipTargetWidth, clipTargetHeight, targetFrame, ppb);
                                }
                            }
                        }
                        else if (clip.AlternativeSource is ISourceReplacementEffect sre && sre.SupportsSourceReplacement(clip, clipTargetWidth, clipTargetHeight))
                        {
                            frame = sre.Compute(clip, PluginManager.CreateComputer(sre.NeedComputer), clipTargetWidth, clipTargetHeight, actualFrame, ppb);
                        }
                        else
                        {
                            frame = clip.GetFrameRelativeToStartPointOfSource(actualFrame, clipTargetWidth, clipTargetHeight, forceResize, ppb);
                        }
                    }
                    catch (Exception ex)
                    {
                        ClipInitializationFailure.Mark(clip, "Source reading", ex);
                        Log(ex, $"Read source for clip {clip.Name} ({clip.Id}); using checkerboard fallback", "Timeline");
                        frame = ClipInitializationFailure.CreateFallbackFrame(clipTargetWidth, clipTargetHeight, ppb);
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
                        try
                        {
                            result.Add(new OneFrame(targetFrame, clip, frame));
                        }
                        catch (Exception ex)
                        {
                            ClipInitializationFailure.Mark(clip, "Effect initialization", ex);
                            Log(ex, $"Build effects for clip {clip.Name} ({clip.Id}); using checkerboard fallback", "Timeline");
                            result.Add(new OneFrame(targetFrame, clip, ClipInitializationFailure.CreateFallbackFrame(clipTargetWidth, clipTargetHeight, ppb)));
                        }
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

                if (IsFrameInClipRange(clip, targetFrame) || (clip.ExtendToWholeDraft && clip.LayerIndex > Renderer.SubTrackOffset))
                {
                    if (result.Any((c) => c.LayerIndex == clip.LayerIndex))
                    {
                        continue; //keep same behavior in Renderer
                        //throw new InvalidDataException($"Two or more clips ({result.Where((c) => c.LayerIndex == clip.LayerIndex).Aggregate<OneFrame, string>(clip.FilePath ?? "Clip@" + clip.Id, (a, b) => $"{a},{b.ParentClip.FilePath}")}) in the same layer {clip.LayerIndex} are overlapping at frame {targetFrame}. Please fix the timeline data.");
                    }
                    result.Add(new OneFrame(targetFrame, clip, null!));
                }
            }

            try
            {
                var f = JsonSerializer.Serialize(result);
                if (f == "[]") return "nullframe";
                return SHA256.HashData(Encoding.UTF8.GetBytes(f)).Aggregate("0x", ((b, c) => b + c.ToString("x2")));

            }
            catch
            {
                Log($"[Timeline] WARN: Failed to serialize frame {targetFrame} for hash computation. Returning fallback hash.");
                return "__error__";
            }

        }


        public static IPicture MixtureLayers(
            IEnumerable<OneFrame> frames,
            uint frameIndex,
            int targetWidth,
            int targetHeight,
            int targetPPB = 8,
            Action<IEffect, IPicture>? AfterEffectCallback = null,
            bool autoCenterImplicitClip = false,
            int projectRelativeWidth = 0,
            int projectRelativeHeight = 0)
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
                    ArgumentNullException.ThrowIfNull(srcFrame, nameof(srcFrame));
                    ArgumentNullException.ThrowIfNull(srcFrame.ParentClip, nameof(srcFrame.ParentClip));
                    IPicture effected = srcFrame.Clip;
                    var effectsList = srcFrame?.Effects?.OrderBy(e => e.Index) ?? (IEnumerable<IEffect>)[];
                    ClipPositionTuple clipPos = srcFrame.ParentClip.PositionTuple;
                    // Begin the per-frame value-provider context for this clip: pre-fills the built-in
                    // frame/progress sources and clears provider values.
                    var clipDuration = srcFrame.ParentClip.GetEffectiveDuration();
                    var clipProgress = clipDuration > 0
                        ? Math.Clamp((float)((long)frameIndex - (long)srcFrame.ParentClip.StartFrame) / clipDuration, 0f, 1f)
                        : 0f;
                    ValueProviderFrameContext.BeginFrame(frameIndex, clipProgress);
                    foreach (var effect in effectsList)
                    {
                        if (effect is IValueProviderEffect vp)
                        {
                            throw new InvalidOperationException($"Effect {vp.Name} ({srcFrame.ParentClip.Id}) of clip {srcFrame.ParentClip.Id} is a IValueProviderEffect and should have been handled in the EffectBindingHelper.RebuildAllEffects. This indicates a logic error.");
                        }
                        if (effect is IContinuousEffect c)
                        {
                            int scopedStart = c.IsScoped ? c.StartPoint : (int)srcFrame.ParentClip.StartFrame;
                            int scopedEnd = c.IsScoped ? c.EndPoint : (int)(srcFrame.ParentClip.StartFrame + srcFrame.ParentClip.GetEffectiveDuration());
                            if (scopedEnd <= scopedStart || frameIndex < scopedStart || frameIndex >= scopedEnd) continue;
                            float continuousProgress = Math.Clamp((float)(frameIndex - scopedStart) / (scopedEnd - scopedStart), 0f, 1f);
                            effected = c.Render(effected, continuousProgress, PluginManager.CreateComputer(effect.NeedComputer), targetWidth, targetHeight);
                        }
                        else if (effect is INormalEffect n)
                        {
                            effected = n.Render(effected, PluginManager.CreateComputer(effect.NeedComputer), targetWidth, targetHeight);
                        }
                        else if (effect is IBindableArgumentEffect b)
                        {
                            _ = EffectProcessing.ProcessBindableArgsEffect(frameIndex, ref effected, ref bindableEffectResultCache, bindableEffectResultCache2, srcFrame.ParentClip, b, PluginManager.CreateComputer(effect.NeedComputer), targetWidth, targetHeight); //single frame render, no need to remove
                        }
                        else if (effect is IClipPositionProvider p)
                        {
                            (var x, var y, var w, var h, bool delta) = p.GetPosition(srcFrame.ParentClip, targetWidth, targetHeight);
                            if (delta)
                            {
                                clipPos = new ClipPositionTuple(clipPos.TargetX + x, clipPos.TargetY + y, clipPos.TargetWidth + w, clipPos.TargetHeight + h, false);
                            }
                            else
                            {
                                clipPos = new(x, y, w, h, false);
                            }
                        }
                        else if (effect is IContinuousClipPositionProvider cp)
                        {
                            (var x, var y, var w, var h, bool delta) = cp.GetPosition(srcFrame.ParentClip, frameIndex, targetWidth, targetHeight);
                            if (delta)
                            {
                                clipPos = new ClipPositionTuple(clipPos.TargetX + x, clipPos.TargetY + y, clipPos.TargetWidth + w, clipPos.TargetHeight + h, false);
                            }
                            else
                            {
                                clipPos = new(x, y, w, h, false);
                            }
                        }
                        else if (effect is IMixture or ISpeedVarianceProvider //these will be processed later; skip here
                                        or ITextEffect or IContinuousTextEffect) //these are processed inside TextClip
                        {

                        }
                        else
                        {
                            throw new NotSupportedException($"The effect ClipType {effect.TypeOfEffect} {effect.TypeName} of clip {srcFrame.ParentClip.Id} is not supported. Effect ID: {effect.Id}");
                        }

                        if (AfterEffectCallback is not null)
                        {
                            IPicture d = effected;
                            int x = ScaleCoordinateToTarget(clipPos.TargetX, projectRelativeWidth, targetWidth);
                            int y = ScaleCoordinateToTarget(clipPos.TargetY, projectRelativeHeight, targetHeight);
                            if (autoCenterImplicitClip && ShouldAutoCenterImplicitClip(srcFrame.ParentClip) && y == 0 && effected.Height < targetHeight)
                            {
                                y += (targetHeight - effected.Height) / 2;
                            }
                            if (x != 0 || y != 0 || effected.Width != targetWidth || effected.Height != targetHeight)
                            {
                                d = PlaceEffect.Process(d, x, y, targetWidth, targetHeight);
                            }
                            AfterEffectCallback(effect, d);
                        }
                    }
                    // The per-frame value-provider values are only needed during effect processing.
                    ValueProviderFrameContext.EndFrame();

                    int clipX = ScaleCoordinateToTarget(clipPos.TargetX, projectRelativeWidth, targetWidth);
                    int clipY = ScaleCoordinateToTarget(clipPos.TargetY, projectRelativeHeight, targetHeight);
                    if (autoCenterImplicitClip && ShouldAutoCenterImplicitClip(srcFrame.ParentClip) && clipY == 0 && effected.Height < targetHeight)
                    {
                        clipY += (targetHeight - effected.Height) / 2;
                    }
                    bool needsPlacement = clipX != 0 || clipY != 0 || effected.Width != targetWidth || effected.Height != targetHeight;
                    LogDiagnostic($"Clip {srcFrame.ParentClip.Name}: {clipX},{clipY} in ({targetWidth}*{targetHeight})");

                    var mixer = srcFrame.ParentClip.MixtureInstance ?? ClassicOverlayMixture.Default;
                    var computerId = mixer.NeedComputer ?? ClassicOverlayMixture.ComputerId;
                    if (result is null)
                    {
                        if (!needsPlacement)
                        {
                            result = effected;
                        }
                        else
                        {
                            result = mixer.Mix(
                                FallBackImageGetter(targetWidth, targetHeight),
                                effected,
                                PluginManager.CreateComputer(computerId),
                                targetPPB,
                                clipX,
                                clipY,
                                targetWidth,
                                targetHeight);
                        }
                    }
                    else
                    {
                        result = mixer.Mix(
                            result,
                            effected,
                            PluginManager.CreateComputer(computerId),
                            targetPPB,
                            clipX,
                            clipY,
                            targetWidth,
                            targetHeight);
                    }
                }
                //LogDiagnostic($"Result's diag info:{result?.GetDiagnosticsInfo() ?? "unknown"}");
                if (result?.Width == targetWidth && result?.Height == targetHeight)
                {
                    goto ok;
                }
                else if (result is null)
                {
                    return Picture16bpp.GenerateSolidColor(targetWidth, targetHeight, 0, 0, 0, 0);
                }
                else
                {
                    result = Placer.Render(result, null, targetWidth, targetHeight);
                }
            ok:
                result = ClassicOverlayMixture.Default
                               .Mix(FallBackImageGetter(targetWidth, targetHeight), result, PluginManager.CreateComputer(ClassicOverlayMixture.ComputerId), targetPPB)
                               .Resize(targetWidth, targetHeight, true);
                if (MyLoggerExtensions.SaveDiagResult)
                {
                    var opId = Guid.NewGuid();
                    File.WriteAllText(Path.Combine(MyLoggerExtensions.DiagResultPath, $"diag-render-{frameIndex}-{opId}-stacks.txt"), PictureProcessStack.FormatProcessStackForLog(result.ProcessStack, 100000));
                }
                return result;
            }
            catch (Exception ex)
            {
                Log(ex, $"Render frame {frameIndex}", "Timeline");
                throw;
            }

        }

        private static PlaceEffect_HwAccel Placer = new()
        {
            StartX = 0,
            StartY = 0
        };

        private static bool IsFrameInClipRange(IClip clip, uint targetFrame)
            => clip.ContainsFrame(targetFrame);

        private static int ResolveClipOutputWidth(IClip clip, int fallbackWidth, int projectRelativeWidth)
        {
            if (clip.TargetWidth > 0)
            {
                return ScaleDimensionToTarget(clip.TargetWidth, projectRelativeWidth, fallbackWidth);
            }

            return Math.Max(1, fallbackWidth);
        }

        private static int ResolveClipOutputHeight(IClip clip, int fallbackHeight, int projectRelativeHeight)
        {
            if (clip.TargetHeight > 0)
            {
                return ScaleDimensionToTarget(clip.TargetHeight, projectRelativeHeight, fallbackHeight);
            }

            return Math.Max(1, fallbackHeight);
        }

        private static int ResolveClipOutputX(IClip clip, int targetWidth, int projectRelativeWidth)
            => ScaleCoordinateToTarget(clip.TargetX, projectRelativeWidth, targetWidth);

        private static int ResolveClipOutputY(IClip clip, int targetHeight, int projectRelativeHeight)
            => ScaleCoordinateToTarget(clip.TargetY, projectRelativeHeight, targetHeight);

        private static int ScaleDimensionToTarget(int value, int relativeValue, int targetValue)
        {
            if (value <= 0)
            {
                return 0;
            }

            if (relativeValue > 0 && targetValue > 0 && relativeValue != targetValue)
            {
                return Math.Max(1, (int)Math.Round((double)value * targetValue / relativeValue, MidpointRounding.AwayFromZero));
            }

            return Math.Max(1, value);
        }

        private static int ScaleCoordinateToTarget(int value, int relativeValue, int targetValue)
        {
            if (value == 0)
            {
                return 0;
            }

            if (relativeValue > 0 && targetValue > 0 && relativeValue != targetValue)
            {
                return (int)Math.Round((double)value * targetValue / relativeValue, MidpointRounding.AwayFromZero);
            }

            return value;
        }

        private static bool ShouldAutoCenterImplicitClip(IClip clip)
        {
            if (HasExplicitTargetRect(clip))
            {
                return false;
            }

            return !HasLegacyInternalPlaceResizeEffects(clip);
        }

        private static bool HasExplicitTargetRect(IClip clip)
            => clip.TargetX != 0 || clip.TargetY != 0 || clip.TargetWidth > 0 || clip.TargetHeight > 0;

        private static bool HasLegacyInternalPlaceResizeEffects(IClip clip)
        {
            if (clip.Effects is null || clip.Effects.Length == 0)
            {
                return false;
            }

            return clip.Effects.Any(effect => effect is not null
                && (string.Equals(effect.Name, "__Internal_Place__", StringComparison.Ordinal)
                    || string.Equals(effect.Name, "__Internal_Resize__", StringComparison.Ordinal)
                    || (string.IsNullOrWhiteSpace(effect.Name)
                        && (string.Equals(effect.TypeName, "Place", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(effect.TypeName, "Resize", StringComparison.OrdinalIgnoreCase)))));
        }

        private static bool IsLegacyInternalLayoutEffect(IEffect effect)
        {
            if (string.Equals(effect.Name, "__Internal_Place__", StringComparison.Ordinal)
                || string.Equals(effect.Name, "__Internal_Resize__", StringComparison.Ordinal))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(effect.Name)
                && (string.Equals(effect.TypeName, "Place", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(effect.TypeName, "Resize", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return false;
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
                            result.Add(new OverlapInfo($"{a.Id} ({a.Name ?? "unknown Name"})", $"{b.Id} ({b.Name ?? "unknown Name"})", overlap, a.LayerIndex));
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
        public IEffect[] Effects { get; init; } = Array.Empty<IEffect>();
        public IClip ParentClip { get; init; }
        public OneFrame(uint frameNumber, IClip parent, IPicture pic)
        {
            FrameNumber = frameNumber;
            ParentClip = parent;
            Clip = pic;
            LayerIndex = parent.LayerIndex;

            IEffect[] effectInstances;
            if (ClipInitializationFailure.IsMarked(parent))
            {
                effectInstances = [];
            }
            else
            {
                try
                {
                    effectInstances = EffectHelper.GetClipEffectsInstances(parent);
                }
                catch (Exception ex)
                {
                    ClipInitializationFailure.Mark(parent, "Effect initialization", ex);
                    effectInstances = [];
                }
            }
            if (parent.TargetX != 0 || parent.TargetY != 0 || parent.TargetWidth > 0 || parent.TargetHeight > 0)
            {
                effectInstances = effectInstances
                    .Where(effect => effect is not null
                        && !string.Equals(effect.Name, "__Internal_Place__", StringComparison.Ordinal)
                        && !string.Equals(effect.Name, "__Internal_Resize__", StringComparison.Ordinal)
                        && !(string.IsNullOrWhiteSpace(effect.Name)
                            && (string.Equals(effect.TypeName, "Place", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(effect.TypeName, "Resize", StringComparison.OrdinalIgnoreCase))))
                    .ToArray();
            }

            Effects = effectInstances.Where(c => c.Enabled && c.TypeOfEffect != EffectType.SpeedVarianceProvider).ToArray();
        }
    }
}
