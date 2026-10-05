using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Drawing.Vector.ImportExport;

namespace projectFrameCut.Render.RenderAPIBase.VectorContent;

public static class VectorPictureRasterization
{
    public static VectorPicture ScaleStrokes(VectorPicture picture, float scale) => new()
    {
        Elements = picture.Elements.Select(e => (VectorCanvasElement)new StrokeScaledElement(e, scale)).ToList()
    };

    private sealed class StrokeScaledElement : VectorCanvasElement
    {
        private readonly VectorCanvasElement source;
        private readonly float scale;

        public StrokeScaledElement(VectorCanvasElement source, float scale)
        {
            this.source = source;
            this.scale = scale;
            RelativeX = source.RelativeX;
            RelativeY = source.RelativeY;
            BaseX = source.BaseX;
            BaseY = source.BaseY;
            UseUniformScale = source.UseUniformScale;
            Rotation = source.Rotation;
            LayerIndex = source.LayerIndex;
        }

        public override VectorSegment[] Draw() => source.Draw().Select(s => s with { Thickness = s.Thickness * scale }).ToArray();
    }

    public static IPicture Downsample(Picture16bpp source, int width, int height, int scale, CancellationToken ct = default)
    {
        int pixels = width * height;
        var r = new ushort[pixels];
        var g = new ushort[pixels];
        var b = new ushort[pixels];
        var a = new float[pixels];
        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            for (int x = 0; x < width; x++)
            {
                double sr = 0, sg = 0, sb = 0, sa = 0;
                for (int sy = 0; sy < scale; sy++)
                for (int sx = 0; sx < scale; sx++)
                {
                    int i = (y * scale + sy) * source.Width + x * scale + sx;
                    float alpha = source.HasAlphaChannel ? source.a[i] : 1;
                    sr += source.r[i] * alpha;
                    sg += source.g[i] * alpha;
                    sb += source.b[i] * alpha;
                    sa += alpha;
                }
                int o = y * width + x;
                // Average premultiplied colors, then restore Picture16bpp's straight alpha.
                if (sa > 0)
                {
                    r[o] = (ushort)Math.Clamp(Math.Round(sr / sa), 0, ushort.MaxValue);
                    g[o] = (ushort)Math.Clamp(Math.Round(sg / sa), 0, ushort.MaxValue);
                    b[o] = (ushort)Math.Clamp(Math.Round(sb / sa), 0, ushort.MaxValue);
                }
                a[o] = (float)(sa / (scale * scale));
            }
        });
        bool hasAlpha = a.Any(alpha => alpha < 1);
        return new Picture16bpp(width, height) { r = r, g = g, b = b, a = hasAlpha ? a : null, HasAlphaChannel = hasAlpha };
    }
}

public sealed class CpuVectorPictureRasterizer : IVectorPictureRasterizer
{
    public IPicture Convert(VectorPicture canvas, int width, int height, bool transparentBackground = false,
        AntiAliasMode aaMode = AntiAliasMode.None, CancellationToken cancellationToken = default)
    {
        int scale = aaMode == AntiAliasMode.None ? 1 : (int)aaMode;
        if (width <= 0 || height <= 0 || scale < 1 || (scale > 1 && scale % 2 != 0))
            throw new ArgumentOutOfRangeException(nameof(aaMode), $"Invalid raster size {width}x{height} or antialias mode {aaMode}.");
        if (scale == 1)
            return new CPUVectorPictureRasterizer().Convert(canvas, width, height, transparentBackground, AntiAliasMode.None, cancellationToken);
        using var source = (Picture16bpp)new CPUVectorPictureRasterizer().Convert(
            VectorPictureRasterization.ScaleStrokes(canvas, scale), checked(width * scale), checked(height * scale),
            transparentBackground, AntiAliasMode.None, cancellationToken);
        Logger.LogDiagnostic($"CPU vector rasterization at {width}x{height}, SSAA {scale}x.");
        return VectorPictureRasterization.Downsample(source, width, height, scale, cancellationToken);
    }
}
