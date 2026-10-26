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
        public static Func<int, int, IPicture> FallBackImageGetter = (w, h) => Picture16bpp.GenerateSolidColor(w, h, 0, 0, 0, null);
        public static bool ProcessEffectFromCanvas { get; set; } = true;

        internal static IPicture CreateTransparentFrame(int width, int height, IPicture.PicturePixelMode pixelMode) => pixelMode.Value switch
        {
            8 => Picture8bpp.GenerateSolidColor(width, height, 0, 0, 0, 0),
            16 => HDRPicture16bpp.GenerateSolidColor(width, height, 0, 0, 0, 0, 0),
            _ => throw new NotSupportedException($"Unsupported target pixel mode {pixelMode}."),
        };

        public static IEnumerable<OneFrame> GetFramesInOneFrame(
            IClip[] video,
            uint targetFrame,
            int targetWidth,
            int targetHeight,
            IPicture.PicturePixelMode? targetPPB = null,
            int projectRelativeWidth = 0,
            int projectRelativeHeight = 0,
            bool applyTransforms = true,
            bool initializeClips = true,
            ClipArgumentFrameCache? argumentFrames = null)
        {
            IEnumerable<OneFrame> Read() => ReadFramesInOneFrame(video, targetFrame, targetWidth, targetHeight,
                targetPPB, projectRelativeWidth, projectRelativeHeight, applyTransforms, initializeClips, argumentFrames);
            return new FrameSequence(video, Read, () => ClipValuePassThrough.Open(video, targetFrame, targetWidth,
                targetHeight, projectRelativeWidth, projectRelativeHeight, targetPPB ?? 8, CancellationToken.None));
        }

        private sealed class FrameSequence(IClip[] clips, Func<IEnumerable<OneFrame>> read, Func<IDisposable?> open) : IEnumerable<OneFrame>
        {
            public IClip[] Clips => clips;
            public IEnumerator<OneFrame> GetEnumerator()
            {
                using var scope = open();
                foreach (var frame in read()) yield return frame;
            }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private static IEnumerable<OneFrame> ReadFramesInOneFrame(IClip[] video, uint targetFrame,
            int targetWidth, int targetHeight, IPicture.PicturePixelMode? targetPPB, int projectRelativeWidth,
            int projectRelativeHeight, bool applyTransforms, bool initializeClips, ClipArgumentFrameCache? argumentFrames)
        {
            argumentFrames ??= new();
            var ppb = targetPPB ?? 8;
            List<OneFrame> result = new List<OneFrame>();
            foreach (var original in video)
            {
                var clip = argumentFrames.Get(original, targetFrame, c =>
                {
                    if (initializeClips && !ClipInitializationFailure.HasDeferredFailures(c.ExtraData))
                    {
                        try
                        {
                            c.ReInit(ppb);
                            ClipInitializationFailure.Clear(c);
                        }
                        catch (Exception ex)
                        {
                            ClipInitializationFailure.Mark(c, "ResolveBinding", ex);
                            Log(ex, $"Initialize clip {c.Name} ({c.Id}); using fallback", "Timeline");
                        }
                    }
                });
                if (!ClipArgumentBinding.ShouldRender(clip)) continue;
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
                        using var context = ValueProviderFrameContext.PushFrame(targetFrame,
                            Math.Clamp(((float)targetFrame - clip.StartFrame) / Math.Max(1u, clip.GetEffectiveDuration()), 0, 1));
                        if (ClipInitializationFailure.IsMarked(clip))
                        {
                            frame = ClipInitializationFailure.CreateFallbackFrame(clipTargetWidth, clipTargetHeight, ppb, clip.ExtraData);
                        }
                        else if (clip is not MaskClip)
                        {
                            frame = MaskFrameContext.Current?.ReadSource(clip, targetFrame)
                                ?? VideoClipRotation.ReadFrame(clip, actualFrame, clipTargetWidth, clipTargetHeight, ppb);
                        }
                    }
                    catch (Exception ex) when (ex is ClipValueBindingException or OperationCanceledException)
                    {
                        foreach (var item in result) item.Clip?.Dispose(true);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        ClipInitializationFailure.Mark(clip, "SourceReading", ex);
                        Log(ex, $"Read source for clip {clip.Name} ({clip.Id}); using fallback", "Timeline");
                        frame = ClipInitializationFailure.CreateFallbackFrame(clipTargetWidth, clipTargetHeight, ppb, clip.ExtraData);
                    }
                    bool isAI = false;
                    if (clip.ExtraData.TryGetValue("IsAI", out var aiMark))
                    {
                        if (aiMark is bool) isAI = (bool)aiMark;
                        else if (aiMark is string s && bool.TryParse(s, out var parsed)) isAI = parsed;
                        else if (aiMark is JsonElement je && je.ValueKind == JsonValueKind.True) isAI = true;
                    }

                    if (frame is not null || clip is MaskClip)
                    {
                        if (!PreviewSourceFrameContext.ReadingRaw && frame is not null && isAI && !TransformProcessing.IsCanvasFrame(frame))
                            frame = EffectProcessing.ProcessAIWatermark(frame, null);
                        try
                        {
                            result.Add(new OneFrame(targetFrame, clip, frame, resolveEffects: false) { ApplyTransforms = applyTransforms });
                        }
                        catch (Exception ex)
                        {
                            ClipInitializationFailure.Mark(clip, "ResolveEffect", ex);
                            Log(ex, $"Build effects for clip {clip.Name} ({clip.Id}); using checkerboard fallback", "Timeline");
                            result.Add(new OneFrame(targetFrame, clip, ClipInitializationFailure.CreateFallbackFrame(clipTargetWidth, clipTargetHeight, ppb, clip.ExtraData), resolveEffects: false));
                        }
                    }
                }
            }

            return result.OrderBy(x => x.ParentClip, ClipLayerOrder.BottomToTop);
        }

        public static string GetFrameHash(IClip[] video, uint targetFrame)
            => FrameHashHelper.GetFrameHash(video, targetFrame, new()).Hash;

        public static bool CanCacheFrame(IClip[] video, uint targetFrame)
            => FrameHashHelper.GetFrameHash(video, targetFrame, new()).CanReuse;

        public static bool CanCacheClipFrame(IClip[] video, IClip clip)
            => FrameHashHelper.CanPersistClipHash(video, clip);

        public static bool CanCacheClipSource(IClip clip)
        {
            if (ClipInitializationFailure.IsMarked(clip)) return false;
            try
            {
                return clip switch
                {
                    VirtualSourceVideoClip virtualClip => virtualClip.VirtualSource?.IsDeterministic == true,
                    MaskClip mask => Sources.MaskSources.Create(mask.SourceType).IsDeterministic,
                    VideoClip video => video.HasCurrentDecoder
                        && video.Decoder is { Disposed: false, IsDeterministic: true, AllowCachingResult: true },
                    _ => clip.FromPlugin == Plugin.InternalPluginBase.InternalPluginBaseID,
                };
            }
            catch (Exception ex)
            {
                Log(ex, $"Read cache capability for clip {clip.Name} ({clip.Id})", "Timeline");
                return false;
            }
        }

        public static string GetClipFrameHash(IClip[] video, IClip clip, uint targetFrame)
            => FrameHashHelper.GetClipHash(video, clip, targetFrame, new()).Hash;

        internal static IReadOnlyList<IClip> CollectHashDependencies(IClip[] video, IClip clip, HashSet<Guid> visited)
        {
            var result = new List<IClip>();
            var infos = video.Select(c => new TransformClipInfo(c.Id, c.StartFrame, c.Duration, c.LayerIndex, c.SubLayerIndex, c.EffectProviders ?? [])).ToArray();
            var dependencyIds = Enum.GetValues<TransformSide>()
                .Select(side => TransformProcessing.Find(infos, clip.Id, side))
                .Where(found => found is not null)
                .SelectMany(found => new[] { found!.Value.Owner.Id, TransformProcessing.ReadNextClip(found.Value.Provider.MetaData) })
                .Concat(ClipValuePassThrough.GetSourceClipIds(clip))
                .Concat(clip is MaskClip ? video.Where(c => ClipLayerOrder.BottomToTop.Compare(c, clip) < 0).Select(c => c.Id) : [])
                .Where(id => id != Guid.Empty && id != clip.Id).Distinct().ToArray();

            if (dependencyIds.Length == 0)
                return result;

            foreach (var dependencyId in dependencyIds)
            {
                if (!visited.Add(dependencyId)) continue;
                var dependency = video.FirstOrDefault(item => item.Id == dependencyId);
                if (dependency is null) continue;
                result.Add(dependency);
                result.AddRange(CollectHashDependencies(video, dependency, visited));
            }
            return result;
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
            int projectRelativeHeight = 0,
            bool transparentBackground = false,
            bool disposeIntermediateFrames = false,
            bool clipLocalOutput = false,
            CancellationToken cancellationToken = default,
            Action<IEffect, object?>? afterNodeCallback = null,
            bool beforeLayoutOutput = false, IReadOnlyList<IClip>? allClips = null, bool keepHwAccelPicture = false,
            IPicture? basePicture = null)
        {
            using var gpuScope = HwAccelPictureScope.Current is null ? new HwAccelPictureScope() : null;
            using var gpuEntry = gpuScope?.Enter();
            if (allClips is null && frames is FrameSequence sequence) allClips = sequence.Clips;
            if (allClips is null)
            {
                var materialized = frames.ToArray();
                frames = materialized;
                allClips = materialized.Select(f => f.ParentClip).DistinctBy(c => c.Id).ToArray();
            }
            using var maskScope = MaskFrameContext.Open(allClips, frameIndex, targetWidth, targetHeight,
                projectRelativeWidth, projectRelativeHeight, targetPPB, cancellationToken);
            using var endpoints = ClipValuePassThrough.Open(allClips, frameIndex, targetWidth, targetHeight,
                projectRelativeWidth, projectRelativeHeight, targetPPB, cancellationToken);
            void ObserveNode(IEffect effect, object? value)
            {
                afterNodeCallback?.Invoke(effect, value);
                if (AfterEffectCallback is not null && value is IPicture picture)
                {
                    using var downloaded = picture is IHwAccelPicture gpu ? gpu.Download() : null;
                    AfterEffectCallback(effect, downloaded ?? picture);
                }
            }
            var temporaryFrames = new HashSet<IPicture>(ReferenceEqualityComparer.Instance);
            IPicture? output = null;
            IPicture Track(IPicture picture)
            {
                if (disposeIntermediateFrames) temporaryFrames.Add(picture);
                return picture;
            }
            try
            {
                int layoutRelativeWidth = projectRelativeWidth > 0 ? projectRelativeWidth : targetWidth;
                int layoutRelativeHeight = projectRelativeHeight > 0 ? projectRelativeHeight : targetHeight;
                IPicture? result = basePicture is null ? null : Track(DynamicEffectGraph.CopyPicture(basePicture));
                ConcurrentDictionary<string, object> bindableEffectResultCache = new();
                Dictionary<string, object> bindableEffectResultCache2 = new();
                Dictionary<string, bool> producedValueTable = new();
                foreach (var original in frames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Don't resize the frame before applying effects!
                    // The ResizeEffect and PlaceEffect will handle sizing and positioning.
                    ArgumentNullException.ThrowIfNull(original, nameof(frames));
                    ArgumentNullException.ThrowIfNull(original.ParentClip, nameof(original.ParentClip));
                    var clip = ClipArgumentBinding.InitializeFrame(original.ParentClip, frameIndex);
                    if (!ClipArgumentBinding.ShouldRender(clip))
                    {
                        if (original.Clip is not null) Track(original.Clip);
                        continue;
                    }
                    if (clip is MaskClip) MaskFrameContext.Current?.Publish(clip, result);
                    var source = original.Clip;
                    if (source is null)
                    {
                        source = VideoClipRotation.ReadFrame(clip, clip.GetRelativeFrameIndex(frameIndex) ?? 0,
                            ResolveClipOutputWidth(clip, targetWidth, projectRelativeWidth),
                            ResolveClipOutputHeight(clip, targetHeight, projectRelativeHeight), targetPPB);
                        Track(source);
                    }
                    if (!PreviewSourceFrameContext.ReadingRaw && original.ApplyTransforms && TransformProcessing.TryRender(clip,
                        allClips, frameIndex, targetWidth, targetHeight,
                        projectRelativeWidth, projectRelativeHeight, targetPPB, out var transformed,
                        initializeClips: false, preparedSource: source, keepHwAccelPicture: true))
                    {
                        Track(source);
                        source = Track(transformed!);
                    }
                    var srcFrame = new OneFrame(frameIndex, clip, source, resolveEffects: false) { Effects = original.Effects };
                    using var endpointClip = ClipValueFrameContext.Current?.EnterClip(srcFrame.ParentClip.Id);
                    var clipDuration = srcFrame.ParentClip.GetEffectiveDuration();
                    var clipProgress = clipDuration > 0
                        ? Math.Clamp((float)((long)frameIndex - srcFrame.ParentClip.StartFrame) / clipDuration, 0f, 1f) : 0f;
                    using var frameContext = ValueProviderFrameContext.PushFrame(frameIndex, clipProgress, false, cancellationToken);
                    using var clipScope = ValueProviderFrameContext.PushClip(srcFrame.ParentClip);
                    using var observer = AfterEffectCallback is null && afterNodeCallback is null ? null
                        : ValueProviderFrameContext.ObserveNodes(ObserveNode);
                    if (TransformProcessing.IsCanvasFrame(srcFrame.Clip))
                    {
                        result = EffectPictureProcessing.Mix(PreviewSourceFrameContext.ReadingRaw ? ClassicOverlayMixture.Default
                                : srcFrame.ParentClip.MixtureInstance ?? ClassicOverlayMixture.Default,
                            result ?? Track(transparentBackground ? Picture16bpp.GenerateSolidColor(targetWidth, targetHeight, 0, 0, 0, 0) : FallBackImageGetter(targetWidth, targetHeight)),
                            srcFrame.Clip, targetPPB, 0, 0, targetWidth, targetHeight);
                        Track(result);
                        if (!PreviewSourceFrameContext.ReadingRaw)
                            srcFrame.ParentClip.EffectStagePlan?.Complete(srcFrame.ParentClip, Stage.Mixture, frameIndex);
                        continue;
                    }
                    IPicture effected = source;
                    IEnumerable<IEffect> effectsList = PreviewSourceFrameContext.ReadingRaw ? []
                        : srcFrame?.Effects?.OrderBy(e => e.Index) ?? (IEnumerable<IEffect>)[];
                    // TargetX/Y live in project-relative space. Width/height, however, must already
                    // be converted to the current output space before effects can adjust the rect.
                    // This mirrors Renderer.ProcessAndCompositeClip; using PositionTuple directly
                    // mixed full-resolution clip bounds with a reduced preview canvas.
                    ClipPositionTuple clipPos = new(
                        srcFrame.ParentClip.TargetX,
                        srcFrame.ParentClip.TargetY,
                        srcFrame.ParentClip.TargetWidth > 0
                            ? ScaleDimensionToTarget(srcFrame.ParentClip.TargetWidth, layoutRelativeWidth, targetWidth)
                            : targetWidth,
                        srcFrame.ParentClip.TargetHeight > 0
                            ? ScaleDimensionToTarget(srcFrame.ParentClip.TargetHeight, layoutRelativeHeight, targetHeight)
                            : targetHeight,
                        false, srcFrame.ParentClip.Rotation);
                    bool preserveAspect = true;
                    foreach (var effect in effectsList)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if ((clipLocalOutput || beforeLayoutOutput) && effect.Name is ("__Internal_Place__" or "__Internal_Resize__")) continue;
                        if (effect is DynamicEffectGraph graph)
                        {
                            var graphResult = graph.Evaluate(effected, srcFrame.ParentClip, frameIndex, targetWidth, targetHeight,
                                layoutRelativeWidth, layoutRelativeHeight, clipPos, preserveAspect, ProcessEffectFromCanvas,
                                cancellationToken, AfterEffectCallback is null && afterNodeCallback is null ? null : ObserveNode,
                                keepHwAccelPicture: true);
                            Track(effected);
                            effected = Track(graphResult.Picture);
                            clipPos = graphResult.Position;
                            preserveAspect = graphResult.PreserveAspect;
                            continue;
                        }
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
                            effected = ResizeForEffectIfNeeded(effected, effect, clipPos.TargetWidth, clipPos.TargetHeight, preserveAspect);
                            Track(effected);
                            effected = FrameHashContext.Compute(effect, srcFrame.ParentClip, frameIndex, effected, targetWidth, targetHeight, layoutRelativeWidth, layoutRelativeHeight, () => EffectPictureProcessing.Render(effect, effected, targetWidth, targetHeight, continuousProgress), continuousProgress);
                            Track(effected);
                        }
                        else if (effect is INormalEffect n)
                        {
                            effected = ResizeForEffectIfNeeded(effected, effect, clipPos.TargetWidth, clipPos.TargetHeight, preserveAspect);
                            Track(effected);
                            effected = FrameHashContext.Compute(effect, srcFrame.ParentClip, frameIndex, effected, targetWidth, targetHeight, layoutRelativeWidth, layoutRelativeHeight, () => EffectPictureProcessing.Render(effect, effected, targetWidth, targetHeight));
                            Track(effected);
                        }
                        else if (effect is IClipPositionProvider p)
                        {
                            (var x, var y, var w, var h, bool delta, float rotation) = FrameHashContext.Compute(effect, srcFrame.ParentClip, frameIndex, null, targetWidth, targetHeight, layoutRelativeWidth, layoutRelativeHeight, () => p.GetPosition(srcFrame.ParentClip, targetWidth, targetHeight));
                            if (delta)
                            {
                                clipPos = new ClipPositionTuple(clipPos.TargetX + x, clipPos.TargetY + y, clipPos.TargetWidth + w, clipPos.TargetHeight + h, false, clipPos.Rotation + rotation);
                            }
                            else
                            {
                                clipPos = new(x, y, w, h, false, rotation);
                            }
                        }
                        else if (effect is IContinuousClipPositionProvider cp)
                        {
                            (var x, var y, var w, var h, bool delta, float rotation) = FrameHashContext.Compute(effect, srcFrame.ParentClip, frameIndex, null, targetWidth, targetHeight, layoutRelativeWidth, layoutRelativeHeight, () => cp.GetPosition(srcFrame.ParentClip, frameIndex, targetWidth, targetHeight, layoutRelativeWidth, layoutRelativeHeight));
                            preserveAspect &= cp.PreserveAspectRatio;
                            if (delta)
                            {
                                clipPos = new ClipPositionTuple(clipPos.TargetX + x, clipPos.TargetY + y, clipPos.TargetWidth + w, clipPos.TargetHeight + h, false, clipPos.Rotation + rotation);
                            }
                            else
                            {
                                clipPos = new(x, y, w, h, false, rotation);
                            }
                        }
                        else if (effect is ITransform or IMixture or ISpeedVarianceProvider //these will be processed later; skip here
                                        or ITextEffect or IContinuousTextEffect or IVectorComponentEffect or IVectorPictureEffect) //these are processed inside TextClip
                        {

                        }
                        else
                        {
                            throw new NotSupportedException($"The effect ClipType {effect.TypeOfEffect} {effect.TypeName} of clip {srcFrame.ParentClip.Id} is not supported. Effect ID: {effect.Id}");
                        }

                        if (AfterEffectCallback is not null)
                        {
                            IPicture d = Track(VideoClipRotation.Rotate(effected, clipPos.Rotation));
                            int x = ScaleCoordinateToTarget(clipPos.TargetX, layoutRelativeWidth, targetWidth);
                            int y = ScaleCoordinateToTarget(clipPos.TargetY, layoutRelativeHeight, targetHeight);
                            if (autoCenterImplicitClip && ShouldAutoCenterImplicitClip(srcFrame.ParentClip) && y == 0 && effected.Height < targetHeight)
                            {
                                y += (targetHeight - effected.Height) / 2;
                            }
                            x += (int)Math.Round((clipPos.TargetWidth - d.Width) / 2d);
                            y += (int)Math.Round((clipPos.TargetHeight - d.Height) / 2d);
                            if (x != 0 || y != 0 || effected.Width != targetWidth || effected.Height != targetHeight)
                            {
                                d = Track(EffectRuntimeDefaults.Place(d, targetWidth, targetHeight, x, y));
                            }
                            using var downloaded = d is IHwAccelPicture gpu ? gpu.Download() : null;
                            AfterEffectCallback(effect, downloaded ?? d);
                        }
                    }
                    // The per-frame value-provider values are only needed during effect processing.

                    if (beforeLayoutOutput)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        output = keepHwAccelPicture ? effected : EffectPictureProcessing.Materialize(effected);
                        return output;
                    }

                    // Position providers may change the clip rectangle without changing the source
                    // frame itself. Honor the resulting Target size before compositing, just like the
                    // full Renderer does. This is essential when Target differs from ProjectRelative.
                    if (clipPos.TargetWidth > 0
                        && clipPos.TargetHeight > 0
                        && (effected.Width != clipPos.TargetWidth || effected.Height != clipPos.TargetHeight))
                    {
                        var old = effected;
                        effected = EffectPictureProcessing.Resize(effected, clipPos.TargetWidth, clipPos.TargetHeight, preserveAspect);
                        Track(effected);
                        if (!ReferenceEquals(old, effected))
                        {
                            try { old.Dispose(); } catch { }
                        }
                    }

                    if (!PreviewSourceFrameContext.ReadingRaw)
                        srcFrame.ParentClip.EffectStagePlan?.Complete(srcFrame.ParentClip, Stage.Picture, frameIndex);
                    int clipX = ScaleCoordinateToTarget(clipPos.TargetX, layoutRelativeWidth, targetWidth);
                    int clipY = ScaleCoordinateToTarget(clipPos.TargetY, layoutRelativeHeight, targetHeight);
                    if (autoCenterImplicitClip && ShouldAutoCenterImplicitClip(srcFrame.ParentClip) && clipY == 0 && effected.Height < targetHeight)
                    {
                        clipY += (targetHeight - effected.Height) / 2;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    effected = Track(VideoClipRotation.Apply(effected, clipPos.Rotation));
                    cancellationToken.ThrowIfCancellationRequested();
                    if (clipLocalOutput)
                    {
                        output = keepHwAccelPicture ? effected : EffectPictureProcessing.Materialize(effected);
                        return output;
                    }
                    clipX += (int)Math.Round((clipPos.TargetWidth - effected.Width) / 2d);
                    clipY += (int)Math.Round((clipPos.TargetHeight - effected.Height) / 2d);
                    bool needsPlacement = clipX != 0 || clipY != 0 || effected.Width != targetWidth || effected.Height != targetHeight;

                    var mixer = PreviewSourceFrameContext.ReadingRaw ? ClassicOverlayMixture.Default
                        : srcFrame.ParentClip.MixtureInstance ?? ClassicOverlayMixture.Default;
                    if (result is null)
                    {
                        if (!needsPlacement && mixer is not DynamicMixtureGraph)
                        {
                            result = effected;
                        }
                        else
                        {
                            result = EffectPictureProcessing.Mix(mixer,
                                Track(transparentBackground ? Picture16bpp.GenerateSolidColor(targetWidth, targetHeight, 0, 0, 0, 0) : FallBackImageGetter(targetWidth, targetHeight)),
                                effected,
                                targetPPB,
                                clipX,
                                clipY,
                                targetWidth,
                                targetHeight);
                        }
                    }
                    else
                    {
                        result = EffectPictureProcessing.Mix(mixer,
                            result,
                            effected,
                            targetPPB,
                            clipX,
                            clipY,
                            targetWidth,
                            targetHeight);
                    }
                    if (result is not null) Track(result);
                    if (!PreviewSourceFrameContext.ReadingRaw)
                        srcFrame.ParentClip.EffectStagePlan?.Complete(srcFrame.ParentClip, Stage.Mixture, frameIndex);
                }
                //LogDiagnostic($"Result's diag info:{result?.GetDiagnosticsInfo() ?? "unknown"}");
                if (result?.Width == targetWidth && result?.Height == targetHeight)
                {
                    goto ok;
                }
                else if (result is null)
                {
                    return CreateTransparentFrame(targetWidth, targetHeight, targetPPB);
                }
                else
                {
                    result = EffectRuntimeDefaults.Place(result, targetWidth, targetHeight);
                    Track(result);
                }
            ok:
                result = EffectPictureProcessing.Mix(ClassicOverlayMixture.Default,
                    Track(transparentBackground ? Picture16bpp.GenerateSolidColor(targetWidth, targetHeight, 0, 0, 0, 0) : FallBackImageGetter(targetWidth, targetHeight)),
                    result, targetPPB, 0, 0, targetWidth, targetHeight);
                Track(result);
                result = Track(EffectPictureProcessing.Resize(result, targetWidth, targetHeight, true));
                if (MyLoggerExtensions.SaveDiagResult)
                {
                    var opId = Guid.NewGuid();
                    File.WriteAllText(Path.Combine(MyLoggerExtensions.DiagResultPath, $"diag-render-{frameIndex}-{opId}-stacks.txt"), PictureProcessStack.FormatProcessStackForLog(result.ProcessStack, 100000));
                }
                output = keepHwAccelPicture ? result : EffectPictureProcessing.Materialize(result);
                return output;
            }
            catch (Exception ex)
            {
                Log(ex, $"Render frame {frameIndex}", "Timeline");
                throw;
            }
            finally
            {
                foreach (var picture in temporaryFrames)
                    if (!ReferenceEquals(picture, output)) picture.Dispose();
            }

        }


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

        private static IPicture ResizeForEffectIfNeeded(IPicture frame, IEffect effect, int targetWidth, int targetHeight, bool preserveAspect)
        {
            if (!ProcessEffectFromCanvas || !effect.CanProcessFromCanvas
                || targetWidth <= 0 || targetHeight <= 0
                || frame.Width == targetWidth && frame.Height == targetHeight)
            {
                return frame;
            }

            var resized = EffectPictureProcessing.Resize(frame, targetWidth, targetHeight, preserveAspect);
            if (!ReferenceEquals(frame, resized))
            {
                try { frame.Dispose(); } catch { }
            }
            return resized;
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
        public IPicture? Clip { get; init; }
        public bool ApplyTransforms { get; init; }
        public uint LayerIndex { get; init; } = 0;
        public IEffect[] Effects { get; init; } = Array.Empty<IEffect>();
        public IClip ParentClip { get; init; }
        public OneFrame(uint frameNumber, IClip parent, IPicture? pic) : this(frameNumber, parent, pic, true)
        {
        }

        public OneFrame(uint frameNumber, IClip parent, IPicture? pic, bool resolveEffects)
        {
            if (pic is null && parent is not MaskClip)
                throw new ArgumentNullException(nameof(pic));
            if (ClipArgumentBinding.IsFrameCopy(parent)) resolveEffects = false;
            FrameNumber = frameNumber;
            ParentClip = parent;
            Clip = pic;
            LayerIndex = parent.LayerIndex;

            IEffect[] effectInstances;
            if (ClipInitializationFailure.IsMarked(parent))
            {
                effectInstances = [];
            }
            else if (!resolveEffects)
            {
                effectInstances = parent.EffectsInstances ?? [];
            }
            else
            {
                try
                {
                    effectInstances = EffectHelper.GetClipEffectsInstances(parent);
                }
                catch (Exception ex)
                {
                    ClipInitializationFailure.Mark(parent, "ResolveEffect", ex);
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

            Effects = effectInstances.Where(c => c.Enabled && c.TypeOfEffect.GetPipeline() == EffectPipeline.Picture
                && c.TypeOfEffect != EffectType.SpeedVarianceProvider).ToArray();
        }
    }
}
