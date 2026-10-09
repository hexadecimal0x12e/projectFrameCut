using projectFrameCut.Drawing.Vector;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using System.Text.Json;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

public static class VectorPictureEffectProcessing
{
    public static IPicture ReadFrame(IClip clip, uint frame, int width, int height, IPicture.PicturePixelMode ppb)
    {
        if (clip is not IVectorContentClip vector || vector.ProcessesVectorPictureEffects
            || !(clip.EffectsInstances ?? []).Any(e => e.Enabled && e is IVectorPictureEffect))
            return clip.GetFrameRelativeToStartPointOfSource(frame, width, height, ppb);
        var picture = Process(clip, vector.GetVectorPictureRelativeToStartPointOfSource(frame, width, height), frame);
        var raster = IVectorContentClip.GlobalDefaultRasterizer.Convert(picture, width, height, true,
            vector.ClipAntiAliasMode ?? IVectorContentClip.GlobalDefaultAntiAliasMode);
        if (raster.BitPerPixel == ppb) return raster;
        try { return raster.ToBitPerPixel(ppb); }
        finally { raster.Dispose(); }
    }

    public static bool HasNativeEffects(IClip clip) => (clip.EffectsInstances ?? []).Any(e =>
        e.Enabled && e.TypeOfEffect.GetPipeline() == projectFrameCut.Shared.EffectPipeline.NativeContent);

    public static VectorPicture Process(IClip clip, VectorPicture picture, uint frame, float? clipProgress = null)
    {
        var effects = (clip.EffectsInstances ?? []).Where(e => e.Enabled).OrderBy(e => e.Index).OfType<IVectorPictureEffect>().ToArray();
        if (effects.Length == 0) return picture;
        float progress = clipProgress ?? (clip.Duration <= 1 ? 0 : Math.Clamp(frame / (float)(clip.Duration - 1), 0, 1));
        using var context = ValueProviderFrameContext.PushFrame(frame, progress);
        picture = new VectorPicture { Elements = picture.Elements.Select(e => (VectorCanvasElement)new FrameElement(e)).ToList() };
        foreach (var effect in effects)
        {
            try { picture = effect.Process(picture, progress) ?? throw new InvalidDataException($"Vector effect {effect.TypeName} returned no picture."); }
            catch (Exception ex)
            {
                projectFrameCut.Shared.Logger.Log(ex, $"Process vector picture effect {effect.Id} of clip {clip.Id}, source frame {frame}");
                throw;
            }
        }
        return picture;
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
