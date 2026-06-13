using projectFrameCut.ApplicationAPIBase.DynamicPreviewProvider;
using CommunityToolkit.Maui.Views;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using System.Linq;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.Render.Effect;
using projectFrameCut.Shared;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using projectFrameCut.Render.Transform;
using IPicture = projectFrameCut.Drawing.Base.IPicture;
using RenderITransform = projectFrameCut.Render.RenderAPIBase.ClipAndTrack.ITransform;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Drawing.Base.Picture;

namespace projectFrameCut.ApplicationPluginBase.DynamicPreviewProvider;

internal abstract class InternalClipDynamicPreviewProviderBase : IClipDynamicPreviewProvider
{
    public abstract string TypeName { get; }

    public abstract bool IsAvailable(IClip target);

    public virtual bool IsPrepareGenerateDispatchable => false;

    public virtual Task<IPicture> PrepareSource(IClip target, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint targetFrame, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public abstract View Generate(IClip target, IPicture? preparedSource, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint targetFrame);

    protected static Label BuildFallbackLabel(string text)
    {
        return new Label
        {
            Text = text,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            TextColor = Colors.White,
            BackgroundColor = Color.FromArgb("#55000000"),
            Padding = new Thickness(8)
        };
    }
}

internal static class TransformClipDynamicPreviewRuntimeKeys
{
    public const string LeftClip = "__dynamicPreview_transform_left_clip";
    public const string RightClip = "__dynamicPreview_transform_right_clip";
}

internal sealed class PhotoClipDynamicPreviewProvider : InternalClipDynamicPreviewProviderBase
{
    public override string TypeName => "PhotoClip";

    public override bool IsAvailable(IClip target)
    {
        return target is PhotoClip
            && target.FromPlugin == InternalPluginBase.InternalPluginBaseID
            && !string.IsNullOrWhiteSpace(target.FilePath)
            && File.Exists(target.FilePath);
    }

    public override View Generate(IClip target, IPicture? preparedSource, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint targetFrame)
    {
        if (target is not PhotoClip clip || string.IsNullOrWhiteSpace(clip.FilePath) || !File.Exists(clip.FilePath))
        {
            return BuildFallbackLabel("Image source is unavailable.");
        }

        return new Image
        {
            Source = ImageSource.FromFile(clip.FilePath),
            Aspect = Aspect.Fill,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
        };
    }
}

internal sealed class SolidColorClipDynamicPreviewProvider : InternalClipDynamicPreviewProviderBase
{
    public override string TypeName => "SolidColorClip";

    public override bool IsAvailable(IClip target)
    {
        return target is SolidColorClip
            && target.FromPlugin == InternalPluginBase.InternalPluginBaseID;
    }

    public override View Generate(IClip target, IPicture? preparedSource, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint targetFrame)
    {
        if (target is not SolidColorClip clip)
        {
            return BuildFallbackLabel("Solid color clip is unavailable.");
        }

        var resolvedWidth = clip.TargetWidth > 0 ? clip.TargetWidth : clip.EffectiveOutputWidth;
        var resolvedHeight = clip.TargetHeight > 0 ? clip.TargetHeight : clip.EffectiveOutputHeight;

        if (targetWidth > 0)
        {
            resolvedWidth = Math.Min(resolvedWidth, targetWidth);
        }

        if (targetHeight > 0)
        {
            resolvedHeight = Math.Min(resolvedHeight, targetHeight);
        }

        var previewWidth = Math.Max(1, resolvedWidth > 0 ? resolvedWidth : (targetWidth > 0 ? targetWidth : canvasWidth));
        var previewHeight = Math.Max(1, resolvedHeight > 0 ? resolvedHeight : (targetHeight > 0 ? targetHeight : canvasHeight));
        var alpha = clip.A.HasValue ? Math.Clamp(clip.A.Value, 0f, 1f) : 1f;
        return new Grid
        {
            WidthRequest = previewWidth,
            HeightRequest = previewHeight,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start,
            Children =
            {
                new BoxView
                {
                    Color = Color.FromRgba(clip.R / 65535f, clip.G / 65535f, clip.B / 65535f, alpha),
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Fill,
                }
            }
        };
    }
}
