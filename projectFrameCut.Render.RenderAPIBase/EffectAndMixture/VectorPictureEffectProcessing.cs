using projectFrameCut.Drawing.Vector;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using System.Text.Json;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

public static class VectorPictureEffectProcessing
{
    public static uint GetTargetFrame(IClip clip, uint sourceFrame) =>
        ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInFrameProviderId) is float current ? (uint)current
            : ClipValueFrameContext.Current?.FrameIndex ?? clip.StartFrame + sourceFrame;

    public static IPicture ReadFrameAtTarget(IClip clip, uint targetFrame, int width, int height, IPicture.PicturePixelMode ppb)
    {
        using var context = ValueProviderFrameContext.PushFrame(targetFrame,
            Math.Clamp(((float)targetFrame - clip.StartFrame) / Math.Max(1u, clip.GetEffectiveDuration()), 0, 1));
        return ReadFrame(clip, clip.GetRelativeFrameIndex(targetFrame) ?? clip.Duration, width, height, ppb);
    }

    public static IPicture ReadFrame(IClip clip, uint frame, int width, int height, IPicture.PicturePixelMode ppb)
        => PreviewSourceFrameContext.Current is { } preview
            ? preview.Read(clip, frame, width, height, ppb, () => ReadFrameCore(clip, frame, width, height, ppb))
            : ReadFrameCore(clip, frame, width, height, ppb);

    private static IPicture ReadFrameCore(IClip clip, uint frame, int width, int height, IPicture.PicturePixelMode ppb)
    {
        using var clipScope = ValueProviderFrameContext.PushClip(clip);
        if (clip is not IVectorContentClip vector || vector.ProcessesVectorPictureEffects
            || !(clip.EffectsInstances ?? []).Any(e => e.Enabled && e is IVectorPictureEffect))
            return clip.GetFrameRelativeToStartPointOfSource(frame, width, height, ppb);
        var picture = Process(clip, vector.GetVectorPictureRelativeToStartPointOfSource(frame, width, height), frame, width: width, height: height);
        var raster = IVectorContentClip.GlobalDefaultRasterizer.Convert(picture, width, height, true,
            vector.ClipAntiAliasMode ?? IVectorContentClip.GlobalDefaultAntiAliasMode);
        if (raster.BitPerPixel == ppb) return raster;
        try { return raster.ToBitPerPixel(ppb); }
        finally { raster.Dispose(); }
    }

    public static bool HasNativeEffects(IClip clip) => (clip.EffectsInstances ?? []).Any(e =>
        e.Enabled && e.TypeOfEffect.GetPipeline() == projectFrameCut.Shared.EffectPipeline.NativeContent);

    public static VectorPicture Process(IClip clip, VectorPicture picture, uint frame, float? clipProgress = null, int width = 0, int height = 0)
    {
        if (PreviewSourceFrameContext.ReadingRaw) return picture;
        var effects = (clip.EffectsInstances ?? []).Where(e => e.Enabled).OrderBy(e => e.Index).OfType<IVectorPictureEffect>().ToArray();
        if (effects.Length == 0) return picture;
        float progress = clipProgress ?? (clip.Duration <= 1 ? 0 : Math.Clamp(frame / (float)(clip.Duration - 1), 0, 1));
        uint targetFrame = GetTargetFrame(clip, frame);
        using var context = ValueProviderFrameContext.PushFrame(targetFrame, progress, reuseComputed: true);
        picture = Copy(picture);
        foreach (var effect in effects)
        {
            ValueProviderFrameContext.CancellationToken.ThrowIfCancellationRequested();
            try { picture = effect.Process(new VectorEffectFrameContext(clip, targetFrame, progress, width, height), picture)
                ?? throw new InvalidDataException($"Vector effect {effect.TypeName} returned no picture."); }
            catch (Exception ex)
            {
                projectFrameCut.Shared.Logger.Log(ex, $"Process vector picture effect {effect.Id} of clip {clip.Id}, source frame {frame}");
                throw;
            }
            ValueProviderFrameContext.NodeObserver?.Invoke(effect, picture);
        }
        return picture;
    }

    public static VectorPicture Copy(VectorPicture picture) => new()
    {
        Elements = picture.Elements.Select(e => (VectorCanvasElement)new FrameElement(e)).ToList(),
    };

    public static bool HasSameContent(VectorPicture left, VectorPicture right)
    {
        string Describe(VectorPicture picture) => JsonSerializer.Serialize(picture.Elements.Select(e => new
        {
            e.RelativeX, e.RelativeY, e.BaseX, e.BaseY, e.Rotation, e.LayerIndex, e.UseUniformScale,
            Segments = e.Draw().Select(s => new { Type = s.GetType().FullName, Data = JsonSerializer.Serialize(s, s.GetType()) }),
        }));
        return Describe(left) == Describe(right);
    }

    private sealed class FrameElement : VectorCanvasElement, IVectorClipElementTag
    {
        private readonly VectorSegment[] segments;
        public Guid SourceClipId { get; }

        public FrameElement(VectorCanvasElement source)
        {
            RelativeX = source.RelativeX; RelativeY = source.RelativeY;
            BaseX = source.BaseX; BaseY = source.BaseY;
            Rotation = source.Rotation; LayerIndex = source.LayerIndex; UseUniformScale = source.UseUniformScale;
            SourceClipId = source is IVectorClipElementTag tag ? tag.SourceClipId : Guid.Empty;
            segments = source.Draw().Select(s => (VectorSegment)JsonSerializer.Deserialize(JsonSerializer.Serialize(s, s.GetType()), s.GetType())!).ToArray();
        }

        public override VectorSegment[] Draw() => segments;
    }
}
