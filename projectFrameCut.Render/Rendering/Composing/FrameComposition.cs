using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Render.Rendering;

internal static class FrameComposition
{
    public static IPicture Compose(IEnumerable<RenderClipContent?> clips, uint frameIndex, RenderComposerOptions options, IPicture blankFrame,
        CancellationToken token, bool clipLocalOutput = false, bool keepHwAccelPicture = false)
    {
        using var diagnostic = RenderFrameDiagnostics.Current?.Measure("Compositing");
        var temporary = new HashSet<IPicture>(ReferenceEqualityComparer.Instance);
        IPicture? result = null, output = null;
        IPicture Track(IPicture picture) { temporary.Add(picture); return picture; }
        void Release(IPicture picture)
        {
            temporary.Remove(picture);
            picture.Dispose();
        }
        IPicture Replace(IPicture source, IPicture picture)
        {
            Track(picture);
            if (!ReferenceEquals(source, picture)) Release(source);
            return picture;
        }
        try
        {
            foreach (var clip in clips)
            {
                token.ThrowIfCancellationRequested();
                if (clip is null) continue;
                using var endpointClip = ClipValueFrameContext.Current?.EnterClip(clip.Clip.Id, independent: true);
                using var clipScope = ValueProviderFrameContext.PushClip(clip.Clip);
                using var frameScope = ValueProviderFrameContext.PushFrame(frameIndex,
                    Math.Clamp(((float)frameIndex - clip.Clip.StartFrame) / Math.Max(1u, clip.Clip.GetEffectiveDuration()), 0, 1), false, token);
                using var clipTiming = RenderFrameDiagnostics.Current?.Measure($"Compose clip {clip.Clip.Name} ({clip.Clip.Id})", "Compose clip");
                IPicture frame;
                using (RenderFrameDiagnostics.Current?.Measure("Copy picture"))
                    frame = Track(DynamicEffectGraph.CopyPicture(clip.Picture));
                frame = Replace(frame, EffectRuntimeDefaults.ApplyOpacity(frame, clip.Position.Opacity));
                int x = 0, y = 0;
                if (!clip.IsCanvas)
                {
                    using var layoutTiming = RenderFrameDiagnostics.Current?.Measure("Layout, resize and rotation");
                    var pos = clip.Position;
                    if (pos.TargetWidth > 0 && pos.TargetHeight > 0 && (frame.Width != pos.TargetWidth || frame.Height != pos.TargetHeight))
                        frame = Replace(frame, Resize(frame, pos.TargetWidth, pos.TargetHeight, clip.PreserveAspect));
                    x = ScaleCoordinateToTarget(pos.TargetX, options.ProjectRelativeWidth, options.TargetWidth);
                    y = ScaleCoordinateToTarget(pos.TargetY, options.ProjectRelativeHeight, options.TargetHeight);
                    if (options.AutoCenterImplicitClip && (clip.CenterImplicit ?? ShouldAutoCenterImplicitClip(clip.Clip)) && y == 0 && frame.Height < options.TargetHeight)
                        y += (options.TargetHeight - frame.Height) / 2;
                    frame = Replace(frame, VideoClipRotation.Rotate(frame, pos.Rotation));
                    if (clipLocalOutput)
                    {
                        output = keepHwAccelPicture ? frame : EffectPictureProcessing.Materialize(frame);
                        return output;
                    }
                    x += (int)Math.Round((pos.TargetWidth - frame.Width) / 2d);
                    y += (int)Math.Round((pos.TargetHeight - frame.Height) / 2d);
                }
                if (result is null && clip.Clip.MixtureInstance is not (DynamicMixtureGraph or ReferenceMixture)
                    && !frame.HasAlphaChannel
                    && x == 0 && y == 0 && frame.Width == options.TargetWidth && frame.Height == options.TargetHeight)
                    result = frame;
                else
                {
                    using var blendTiming = RenderFrameDiagnostics.Current?.Measure($"Blend ({(clip.Clip.MixtureInstance ?? ClassicOverlayMixture.Default).GetType().Name})");
                    var mixed = Track(EffectPictureProcessing.Mix(clip.Clip.MixtureInstance ?? ClassicOverlayMixture.Default,
                        result ?? blankFrame, frame, IPicture.PicturePixelMode.Float16Picture, x, y, options.TargetWidth, options.TargetHeight));
                    if (result is not null && !ReferenceEquals(result, mixed)) Release(result);
                    if (!ReferenceEquals(frame, mixed)) Release(frame);
                    result = mixed;
                }
                clip.Clip.EffectStagePlan?.Complete(clip.Clip, EffectStage.Composition, frameIndex);
            }
            if (result is not null && (result.Width != options.TargetWidth || result.Height != options.TargetHeight))
            {
                using var resizeTiming = RenderFrameDiagnostics.Current?.Measure("Resize output");
                result = Replace(result, Resize(result, options.TargetWidth, options.TargetHeight, false));
            }
            using (RenderFrameDiagnostics.Current?.Measure("Materialize output / GPU readback"))
                output = keepHwAccelPicture ? result ?? EffectPictureProcessing.Copy(blankFrame)
                    : EffectPictureProcessing.Materialize(result ?? EffectPictureProcessing.Copy(blankFrame));
            return output;
        }
        finally
        {
            foreach (var frame in temporary)
                if (!ReferenceEquals(frame, output)) frame.Dispose();
        }
    }

    private static IPicture Resize(IPicture frame, int width, int height, bool preserveAspect)
        => EffectPictureProcessing.Resize(frame, width, height, preserveAspect);

    internal static int ScaleCoordinateToTarget(int value, int relativeValue, int targetValue)
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

    internal static bool ShouldAutoCenterImplicitClip(IClip clip)
    {
        if (HasExplicitTargetRect(clip))
        {
            return false;
        }

        return !HasLegacyInternalPlaceResizeEffects(clip);
    }

    internal static bool HasExplicitTargetRect(IClip clip)
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
}
