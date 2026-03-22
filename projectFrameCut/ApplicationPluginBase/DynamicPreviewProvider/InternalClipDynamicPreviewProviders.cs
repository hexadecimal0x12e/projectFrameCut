using projectFrameCut.ApplicationAPIBase.DynamicPreviewProvider;
using CommunityToolkit.Maui.Views;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using System.Linq;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.Render.Effect;

namespace projectFrameCut.ApplicationPluginBase.DynamicPreviewProvider;

internal abstract class InternalClipDynamicPreviewProviderBase : IClipDynamicPreviewProvider
{
    public abstract string TypeName { get; }

    public abstract bool IsAvailable(IClip target);

    public abstract View Generate(IClip target, int canvasWidth, int canvasHeight, uint targetFrame);

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

internal sealed class VideoClipDynamicPreviewProvider : InternalClipDynamicPreviewProviderBase
{
    public override string TypeName => "VideoClip";

    public override bool IsAvailable(IClip target)
    {
        return target is VideoClip
            && target.FromPlugin == InternalPluginBase.InternalPluginBaseID
            && !string.IsNullOrWhiteSpace(target.FilePath)
            && File.Exists(target.FilePath);
    }

    public override View Generate(IClip target, int canvasWidth, int canvasHeight, uint targetFrame)
    {
        if (target is not VideoClip clip || string.IsNullOrWhiteSpace(clip.FilePath) || !File.Exists(clip.FilePath))
        {
            return BuildFallbackLabel("Video source is unavailable.");
        }

        var frame = target.GetFrame(targetFrame, canvasWidth, canvasHeight, true, 8);

        return new Image
        {
            Source = frame.ToImageSource(),
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
        };
    }
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

    public override View Generate(IClip target, int canvasWidth, int canvasHeight, uint targetFrame)
    {
        if (target is not PhotoClip clip || string.IsNullOrWhiteSpace(clip.FilePath) || !File.Exists(clip.FilePath))
        {
            return BuildFallbackLabel("Image source is unavailable.");
        }

        return new Image
        {
            Source = ImageSource.FromFile(clip.FilePath),
            Aspect = Aspect.AspectFit,
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

    public override View Generate(IClip target, int canvasWidth, int canvasHeight, uint targetFrame)
    {
        if (target is not SolidColorClip clip)
        {
            return BuildFallbackLabel("Solid color clip is unavailable.");
        }
        var alpha = clip.A.HasValue ? Math.Clamp(clip.A.Value, 0f, 1f) : 1f;
        return new BoxView
        {
            Color = Color.FromRgba((byte)(clip.R / 257), (byte)(clip.G / 257), (byte)(clip.B / 257), alpha),
            WidthRequest = canvasWidth,
            HeightRequest = canvasHeight,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
        };
    }
}

internal sealed class TextClipDynamicPreviewProvider : InternalClipDynamicPreviewProviderBase
{
    public override string TypeName => "TextClip";

    public override bool IsAvailable(IClip target)
    {
        return target is TextClip t
            && target.FromPlugin == InternalPluginBase.InternalPluginBaseID
            && !t.TextEntries.Any(c => c.UseVerticalLayout || c.applyKerning || c.strokeWidth > 0 || c.dpi is not null);
    }

    public override View Generate(IClip target, int canvasWidth, int canvasHeight, uint targetFrame)
    {
        if (target is not TextClip clip)
        {
            return BuildFallbackLabel("Text clip is unavailable.");
        }

        var entries = clip.TextEntries.Select(e => new Label
        {
            Text = e.text,
            TextColor = Color.FromRgba(e.r / 257, e.g / 257, e.b / 257, (double)(e.a ?? 1d)),
            HorizontalTextAlignment = e.horizontalAlignment switch { SixLabors.Fonts.HorizontalAlignment.Left => TextAlignment.Start, SixLabors.Fonts.HorizontalAlignment.Right => TextAlignment.End, SixLabors.Fonts.HorizontalAlignment.Center => TextAlignment.Center, _ => TextAlignment.Center },
            VerticalTextAlignment = e.verticalAlignment switch { SixLabors.Fonts.VerticalAlignment.Top => TextAlignment.Start, SixLabors.Fonts.VerticalAlignment.Bottom => TextAlignment.End, SixLabors.Fonts.VerticalAlignment.Center => TextAlignment.Center, _ => TextAlignment.Center },
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            LineBreakMode = LineBreakMode.WordWrap,
            FontAttributes = e.fontStyle switch
            {
                SixLabors.Fonts.FontStyle.Regular => FontAttributes.None,
                SixLabors.Fonts.FontStyle.Bold => FontAttributes.Bold,
                SixLabors.Fonts.FontStyle.Italic => FontAttributes.Italic,
                SixLabors.Fonts.FontStyle.BoldItalic => FontAttributes.Bold | FontAttributes.Italic,
                _ => FontAttributes.None,
            },
            Margin = new Thickness(12),
            CharacterSpacing = e.lineSpacing,
            TranslationX = e.x,
            TranslationY = e.y,
            Rotation = e.rotation,

        });
        var g = new Grid();
        foreach (var item in entries)
        {
            g.Add(item);
        }
        return g;
    }
}