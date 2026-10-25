using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Rendering;
using System.Text.Json;

namespace projectFrameCut.Render.RPCProtocol;

public static class ClipPreviewRenderer
{
    public static IPicture? Render(IClip clip, IReadOnlyList<IClip> allClips, int canvasWidth, int canvasHeight, int projectWidth, int projectHeight, uint frameIndex, CancellationToken token, IPicture.PicturePixelMode pixelMode, bool beforeLayout = false, bool initializeClips = true)
    {
        if (FrameHashContext.Current is { } context)
            return context.ReadPicture((typeof(ClipPreviewRenderer), clip.Id, frameIndex, canvasWidth, canvasHeight,
                projectWidth, projectHeight, pixelMode.Value, beforeLayout),
                () => RenderCore(clip, allClips, canvasWidth, canvasHeight, projectWidth, projectHeight, frameIndex,
                    token, pixelMode, beforeLayout, initializeClips));
        return RenderCore(clip, allClips, canvasWidth, canvasHeight, projectWidth, projectHeight, frameIndex,
            token, pixelMode, beforeLayout, initializeClips);
    }

    private static IPicture? RenderCore(IClip clip, IReadOnlyList<IClip> allClips, int canvasWidth, int canvasHeight,
        int projectWidth, int projectHeight, uint frameIndex, CancellationToken token, IPicture.PicturePixelMode pixelMode,
        bool beforeLayout, bool initializeClips)
    {
        token.ThrowIfCancellationRequested();
        using var maskScope = MaskFrameContext.Open(allClips, frameIndex, canvasWidth, canvasHeight,
            projectWidth, projectHeight, pixelMode, token);
        using var endpoints = ClipValuePassThrough.Open(allClips, frameIndex, canvasWidth, canvasHeight,
            projectWidth, projectHeight, pixelMode, token);
        using var endpointClip = ClipValueFrameContext.Current?.EnterClip(clip.Id, independent: true);
        if (initializeClips && !ClipArgumentBinding.IsFrameCopy(clip) && !ClipArgumentBinding.TryGetFrame(clip, frameIndex, out _)
            && !ClipInitializationFailure.HasDeferredFailures(clip.ExtraData))
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

        clip = ClipArgumentBinding.InitializeFrame(clip, frameIndex);
        using var context = ValueProviderFrameContext.PushFrame(frameIndex,
            Math.Clamp(((float)frameIndex - clip.StartFrame) / Math.Max(1u, clip.GetEffectiveDuration()), 0, 1), false, token);
        var sourceWidth = ResolveDimension(clip.TargetWidth, projectWidth, canvasWidth);
        var sourceHeight = ResolveDimension(clip.TargetHeight, projectHeight, canvasHeight);
        bool transform = !beforeLayout && TransformProcessing.HasActiveTransform(clip, allClips, frameIndex);
        if (!ClipArgumentBinding.ShouldRender(clip))
            return Timeline.CreateTransparentFrame(transform ? canvasWidth : sourceWidth, transform ? canvasHeight : sourceHeight, pixelMode);
        if (transform)
            return TransformProcessing.RenderCanvas(clip, allClips, frameIndex, canvasWidth, canvasHeight, projectWidth, projectHeight, pixelMode,
                initializeClips: initializeClips);
        IPicture? frame;
        try
        {
            if (ClipInitializationFailure.IsMarked(clip))
            {
                frame = ClipInitializationFailure.CreateFallbackFrame(sourceWidth, sourceHeight, pixelMode, clip.ExtraData);
            }
            else
            {
                var actualFrame = clip.GetRelativeFrameIndex(frameIndex) ?? clip.StartFrame + clip.GetEffectiveDuration();
                frame = VideoClipRotation.ReadFrame(clip, actualFrame, sourceWidth, sourceHeight, pixelMode);
            }
        }
        catch (OperationCanceledException) { throw; }
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
            oneFrame = new OneFrame(frameIndex, clip, frame, resolveEffects: false);
        }
        catch (Exception ex)
        {
            ClipInitializationFailure.Mark(clip, "ResolveEffect", ex);
            try { frame.Dispose(); } catch { }
            frame = ClipInitializationFailure.CreateFallbackFrame(sourceWidth, sourceHeight, pixelMode, clip.ExtraData);
            oneFrame = new OneFrame(frameIndex, clip, frame, resolveEffects: false);
        }

        try
        {
            token.ThrowIfCancellationRequested();
            return Timeline.MixtureLayers([oneFrame], frameIndex, canvasWidth, canvasHeight, (int)pixelMode,
                projectRelativeWidth: projectWidth, projectRelativeHeight: projectHeight,
                transparentBackground: true, disposeIntermediateFrames: true, clipLocalOutput: true, cancellationToken: token,
                beforeLayoutOutput: beforeLayout, allClips: allClips);
        }
        catch
        {
            try { frame.Dispose(); } catch { }
            throw;
        }
    }

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
