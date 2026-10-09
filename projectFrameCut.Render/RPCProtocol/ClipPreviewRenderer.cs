using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Rendering;
using System.Text.Json;

namespace projectFrameCut.Render.RPCProtocol;

internal static class ClipPreviewRenderer
{
    public static IPicture? Render(IClip clip, IReadOnlyList<IClip> allClips, int canvasWidth, int canvasHeight, int projectWidth, int projectHeight, uint frameIndex, CancellationToken token, IPicture.PicturePixelMode pixelMode)
    {
        token.ThrowIfCancellationRequested();
        if (TransformProcessing.HasActiveTransform(clip, allClips, frameIndex))
            return TransformProcessing.RenderCanvas(clip, allClips, frameIndex, canvasWidth, canvasHeight, projectWidth, projectHeight, pixelMode);
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

        clip = ClipArgumentBinding.InitializeFrame(clip, frameIndex);
        var sourceWidth = ResolveDimension(clip.TargetWidth, projectWidth, canvasWidth);
        var sourceHeight = ResolveDimension(clip.TargetHeight, projectHeight, canvasHeight);
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
                transparentBackground: true, disposeIntermediateFrames: true, clipLocalOutput: true, cancellationToken: token);
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
