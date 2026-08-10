using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.Shared;
using IPicture = projectFrameCut.Drawing.Base.IPicture;

namespace projectFrameCut.ApplicationPluginBase.DynamicPreviewProvider;

internal sealed class TextClipDynamicPreviewProvider : InternalClipDynamicPreviewProviderBase
{
    public override string TypeName => "TextClip";

    public override bool IsPrepareGenerateDispatchable => true;

    public override bool IsAvailable(IClip target)
    {
        return target is TextClip
            && target.FromPlugin == InternalPluginBase.InternalPluginBaseID;
    }

    public override Task<IPicture> PrepareSource(IClip target, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint targetFrame, CancellationToken cancellationToken)
    {
        if (target is not TextClip clip)
            throw new InvalidOperationException("Text clip is unavailable.");

        cancellationToken.ThrowIfCancellationRequested();

        var renderW = targetWidth > 0 ? targetWidth : canvasWidth;
        var renderH = targetHeight > 0 ? targetHeight : canvasHeight;
        var context = DynamicPreviewRenderContext.Current;
        var projectW = context is { ProjectRelativeWidth: > 0 } ? context.Value.ProjectRelativeWidth : renderW;
        var projectH = context is { ProjectRelativeHeight: > 0 } ? context.Value.ProjectRelativeHeight : renderH;
        var clipW = target.TargetWidth > 0 ? Math.Max(1, target.TargetWidth) : Math.Max(1, projectW);
        var clipH = target.TargetHeight > 0 ? Math.Max(1, target.TargetHeight) : Math.Max(1, projectH);

        cancellationToken.ThrowIfCancellationRequested();

        var vect = clip.GetVectorPictureRelativeToStartPointOfSource(0, clipW, clipH);
        // Rasterize at clip dimensions so the target matches the layout canvas
        // dimensions. DynamicPreview.ResolveRequests shifts text entries into
        // clip-local space when it sets TargetWidth/TargetHeight, so the canvas
        // and entries are in the same coordinate system.
        var picture = IVectorContentClip.GlobalDefaultRasterizer.Convert(vect, clipW, clipH, true, IVectorContentClip.GlobalDefaultAntiAliasMode, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        Logger.LogDiagnostic($"TextClip {clip.Name}: target {target.TargetWidth}*{target.TargetHeight}, resolved: {renderW}*{renderH}, text: {TextMeasureHelper.MeasureBounds(clip)}");

        return Task.FromResult(picture);
    }

    public override View Generate(IClip target, IPicture? preparedSource, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint targetFrame)
    {
        if (preparedSource != null)
        {
            return new Image
            {
                Source = preparedSource.ToImageSource(),
                Aspect = Aspect.AspectFit,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill,
            };
        }

        if (target is not TextClip clip)
        {
            return BuildFallbackLabel("Text clip is unavailable.");
        }

        var renderW = targetWidth > 0 ? targetWidth : canvasWidth;
        var renderH = targetHeight > 0 ? targetHeight : canvasHeight;
        var context = DynamicPreviewRenderContext.Current;
        var projectW = context is { ProjectRelativeWidth: > 0 } ? context.Value.ProjectRelativeWidth : renderW;
        var projectH = context is { ProjectRelativeHeight: > 0 } ? context.Value.ProjectRelativeHeight : renderH;
        // DynamicPreview.ResolveRequests shifts text entries into clip-local space
        // when it sets TargetWidth/TargetHeight, so the clip's bounding box
        // dimensions are the correct layout canvas and render target.
        var clipW = target.TargetWidth > 0 ? Math.Max(1, target.TargetWidth) : Math.Max(1, projectW);
        var clipH = target.TargetHeight > 0 ? Math.Max(1, target.TargetHeight) : Math.Max(1, projectH);

        var frame = clip.GetFrameRelativeToStartPointOfSource(0, clipW, clipH, IPicture.PicturePixelMode.BytePicture);

        return new Image
        {
            Source = frame.ToImageSource(),
            Aspect = Aspect.AspectFit,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
        };
    }
}
