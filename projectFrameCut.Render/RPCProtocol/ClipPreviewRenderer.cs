using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Rendering;
using System.Collections.Concurrent;
using System.Text.Json;

namespace projectFrameCut.Render.RPCProtocol;

internal static class ClipPreviewRenderer
{
    public static IPicture? Render(IClip clip, IReadOnlyList<IClip> allClips, int canvasWidth, int canvasHeight, int projectWidth, int projectHeight, uint frameIndex, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var sourceWidth = ResolveDimension(clip.TargetWidth, projectWidth, canvasWidth);
        var sourceHeight = ResolveDimension(clip.TargetHeight, projectHeight, canvasHeight);
        var pixelMode = IPicture.PicturePixelMode.BytePicture;

        if (!ClipInitializationFailure.HasDeferredFailures(clip.ExtraData))
        {
            try
            {
                clip.ReInit(pixelMode);
                ClipInitializationFailure.Clear(clip);
            }
            catch (Exception ex)
            {
                ClipInitializationFailure.Mark(clip, "ResolveBinding", ex);
            }
        }

        IPicture? frame;
        try
        {
            if (ClipInitializationFailure.IsMarked(clip))
            {
                frame = ClipInitializationFailure.CreateFallbackFrame(sourceWidth, sourceHeight, pixelMode, clip.ExtraData);
            }
            else if (clip is TransformContainer transformClip)
            {
                frame = ReadTransformSource(transformClip, allClips, sourceWidth, sourceHeight, frameIndex, pixelMode);
            }
            else
            {
                var actualFrame = clip.GetRelativeFrameIndex(frameIndex) ?? clip.StartFrame + clip.GetEffectiveDuration();
                if (clip.AlternativeSource is ISourceReplacementEffect replacement
                    && replacement.SupportsSourceReplacement(clip, sourceWidth, sourceHeight))
                {
                    frame = replacement.Compute(
                        clip,
                        PluginManager.CreateComputer(replacement.NeedComputer),
                        clip.GetFrame(frameIndex, sourceWidth, sourceHeight, pixelMode),
                        sourceWidth,
                        sourceHeight,
                        actualFrame,
                        pixelMode);
                }
                else
                {
                    frame = clip.GetFrameRelativeToStartPointOfSource(actualFrame, sourceWidth, sourceHeight, pixelMode);
                }
            }
        }
        catch (Exception ex)
        {
            ClipInitializationFailure.Mark(clip, "SourceReading", ex);
            frame = ClipInitializationFailure.CreateFallbackFrame(sourceWidth, sourceHeight, pixelMode, clip.ExtraData);
        }

        if (frame is null) return null;
        if (IsAiGeneratedClip(clip)) frame = EffectProcessing.ProcessAIWatermark(frame, frameIndex);

        OneFrame oneFrame;
        try
        {
            oneFrame = new OneFrame(frameIndex, clip, frame);
        }
        catch (Exception ex)
        {
            ClipInitializationFailure.Mark(clip, "ResolveEffect", ex);
            try { frame.Dispose(); } catch { }
            return ClipInitializationFailure.CreateFallbackFrame(sourceWidth, sourceHeight, pixelMode, clip.ExtraData);
        }

        try
        {
            return RenderEffectsWithoutLayout(oneFrame, canvasWidth, canvasHeight, frameIndex, token);
        }
        catch
        {
            try { frame.Dispose(); } catch { }
            throw;
        }
    }

    private static IPicture? ReadTransformSource(TransformContainer transformClip, IReadOnlyList<IClip> allClips, int width, int height, uint frameIndex, IPicture.PicturePixelMode pixelMode)
    {
        var transform = transformClip.Transform;
        if (transform is null)
        {
            transformClip.ReInit(pixelMode);
            transform = transformClip.Transform;
        }
        if (transform is null) return null;

        var left = allClips.FirstOrDefault(candidate => candidate.Id == transform.BindedLeftClip);
        var right = allClips.FirstOrDefault(candidate => candidate.Id == transform.BindedRightClip);
        return left is null || right is null
            ? null
            : TransformProcessing.ProcessTransform(left, right, transform, width, height, frameIndex, pixelMode);
    }

    private static IPicture RenderEffectsWithoutLayout(OneFrame source, int targetWidth, int targetHeight, uint frameIndex, CancellationToken token)
    {
        var effected = source.Clip;
        var globalBindableCache = new ConcurrentDictionary<string, object>();
        var frameBindableCache = new Dictionary<string, object>();
        var duration = source.ParentClip.GetEffectiveDuration();
        var clipProgress = duration > 0
            ? Math.Clamp((float)((long)frameIndex - source.ParentClip.StartFrame) / duration, 0f, 1f)
            : 0f;

        ValueProviderFrameContext.BeginFrame(frameIndex, clipProgress);
        try
        {
            foreach (var effect in source.Effects.OrderBy(effect => effect.Index))
            {
                token.ThrowIfCancellationRequested();
                if (effect is IClipPositionProvider or IContinuousClipPositionProvider || IsLegacyLayoutEffect(effect)) continue;
                if (effect is IValueProviderEffect valueProvider)
                    throw new InvalidOperationException($"Effect {valueProvider.Name} of clip {source.ParentClip.Id} should have been inlined by the binding pipeline.");

                if (effect is IContinuousEffect continuous)
                {
                    var scopedStart = continuous.IsScoped ? continuous.StartPoint : (int)source.ParentClip.StartFrame;
                    var scopedEnd = continuous.IsScoped ? continuous.EndPoint : (int)(source.ParentClip.StartFrame + source.ParentClip.GetEffectiveDuration());
                    if (scopedEnd <= scopedStart || frameIndex < scopedStart || frameIndex >= scopedEnd) continue;
                    var progress = Math.Clamp((float)(frameIndex - scopedStart) / (scopedEnd - scopedStart), 0f, 1f);
                    effected = continuous.Render(effected, progress, PluginManager.CreateComputer(effect.NeedComputer), targetWidth, targetHeight);
                }
                else if (effect is INormalEffect normal)
                {
                    effected = normal.Render(effected, PluginManager.CreateComputer(effect.NeedComputer), targetWidth, targetHeight);
                }
                else if (effect is IBindableArgumentEffect bindable)
                {
                    _ = EffectProcessing.ProcessBindableArgsEffect(frameIndex, ref effected, ref globalBindableCache, frameBindableCache, source.ParentClip, bindable, PluginManager.CreateComputer(effect.NeedComputer), targetWidth, targetHeight);
                }
                else if (effect is not (IMixture or ISpeedVarianceProvider or ITextEffect or IContinuousTextEffect))
                {
                    throw new NotSupportedException($"Effect {effect.TypeName}/{effect.Name} is not supported by the clip preview pipeline.");
                }
            }
            return effected;
        }
        finally
        {
            ValueProviderFrameContext.EndFrame();
        }
    }

    private static bool IsLegacyLayoutEffect(IEffect effect)
        => string.Equals(effect.Name, "__Internal_Place__", StringComparison.Ordinal)
           || string.Equals(effect.Name, "__Internal_Resize__", StringComparison.Ordinal)
           || (string.IsNullOrWhiteSpace(effect.Name)
               && (string.Equals(effect.TypeName, "Place", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(effect.TypeName, "Resize", StringComparison.OrdinalIgnoreCase)));

    private static int ResolveDimension(int clipDimension, int projectDimension, int canvasDimension)
        => clipDimension <= 0 || projectDimension <= 0
            ? Math.Max(1, canvasDimension)
            : Math.Max(1, (int)Math.Round((double)clipDimension * canvasDimension / projectDimension, MidpointRounding.AwayFromZero));

    private static bool IsAiGeneratedClip(IClip clip)
    {
        if (clip.ExtraData is null || !clip.ExtraData.TryGetValue("IsAI", out var raw)) return false;
        return raw switch
        {
            bool value => value,
            string value when bool.TryParse(value, out var parsed) => parsed,
            JsonElement value => value.ValueKind == JsonValueKind.True,
            _ => false,
        };
    }
}
