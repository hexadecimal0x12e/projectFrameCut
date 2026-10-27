using System.Numerics;

namespace projectFrameCut.Render.Effect;

public partial class ColorAdjustmentEffect<TPicture>
{
    private static PictureFP16 ProcessFP16(IPicture<Half> source, AdjustmentParams p)
    {
        var result = FP16Conversion.FromPicture(source);
        if (p.Opacity < 1) result.EnsureAlpha();
        using var e = result.Edit();
        float brightness = p.Brightness - 1;
        for (int i = 0; i < result.Pixels; i++)
        {
            var c = new Vector3((float)e.Red[i], (float)e.Green[i], (float)e.Blue[i]);
            c = brightness >= 0 ? c + (Vector3.One - c) * brightness : c * (1 + brightness);
            c = (c - new Vector3(.5f)) * p.Contrast + new Vector3(.5f);
            float luma = Vector3.Dot(c, new(.299f, .587f, .114f));
            c = Vector3.Lerp(new(luma), c, p.Saturation * (1 + p.Vibrance * .5f) * (1 - p.Grayscale));
            if (p.Hue != 0) c = projectFrameCut.Drawing.Effect.HueRotationEffect.RotateColor(c, p.Hue);
            if (p.Gamma != 1) c = new(MathF.Pow(Math.Max(0, c.X), 1 / p.Gamma), MathF.Pow(Math.Max(0, c.Y), 1 / p.Gamma), MathF.Pow(Math.Max(0, c.Z), 1 / p.Gamma));
            c.X *= 1 + p.Temperature * .01f; c.Z *= 1 - p.Temperature * .01f;
            if (p.Invert) c = Vector3.One - c;
            e.Red[i] = PictureFP16.Unit(c.X); e.Green[i] = PictureFP16.Unit(c.Y); e.Blue[i] = PictureFP16.Unit(c.Z);
            if (!e.Alpha.IsEmpty) e.Alpha[i] = PictureFP16.Unit((float)e.Alpha[i] * (float)p.Opacity);
            if (!e.Brightness.IsEmpty && brightness != 0)
            {
                float v = (float)e.Brightness[i];
                e.Brightness[i] = PictureFP16.Unit(brightness >= 0 ? v + (1 - v) * brightness : v * (1 + brightness));
            }
        }
        return result;
    }
}
