#if WINDOWS || LINUX || HEADLESS
using ILGPU;
using GpuHalf = ILGPU.Half;

namespace projectFrameCut.Render.HwAccelEngine.Effect;

internal static class ScRgbKernel
{
    internal static void Kernel(Index1D index, ArrayView<ResidentKernelPixel> source, ArrayView<GpuHalf> output,
        int hasAlpha, int hdr, float peak)
    {
        int i = index;
        var pixel = source[i];
        float r = (float)pixel.Red, g = (float)pixel.Green, b = (float)pixel.Blue, a = (float)pixel.Alpha;
        a = hasAlpha != 0 && IsFinite(a) ? Clamp(a, 0f, 1f) : 1f;
        if (hdr != 0 && peak > 300f)
        {
            r = DecodePq(r) * 125f;
            g = DecodePq(g) * 125f;
            b = DecodePq(b) * 125f;
            float sr = 1.660491f * r - 0.587641f * g - 0.072850f * b;
            float sg = -0.124550f * r + 1.132900f * g - 0.008349f * b;
            b = -0.018151f * r - 0.100579f * g + 1.118730f * b;
            r = sr;
            g = sg;
        }
        else
        {
            r = Linear(r);
            g = Linear(g);
            b = Linear(b);
        }
        if (hdr != 0)
        {
            float brightness = (float)pixel.Brightness;
            brightness = IsFinite(brightness) ? Clamp(brightness, 0f, 1f) : 0f;
            float luma = .2126f * r + .7152f * g + .0722f * b;
            float gain = luma > 1e-6f ? brightness * peak / 80f / luma : 0f;
            r *= gain;
            g *= gain;
            b *= gain;
        }
        int offset = i * 4;
        output[offset] = (GpuHalf)Clamp(r * a, -0.5f, 125f);
        output[offset + 1] = (GpuHalf)Clamp(g * a, -0.5f, 125f);
        output[offset + 2] = (GpuHalf)Clamp(b * a, -0.5f, 125f);
        output[offset + 3] = (GpuHalf)a;
    }

    private static bool IsFinite(float value) => value >= -float.MaxValue && value <= float.MaxValue;
    private static float Clamp(float value, float low, float high) => value < low ? low : value > high ? high : value;
    private static float Linear(float value) => value <= 0.04045f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);

    private static float DecodePq(float value)
    {
        float p = MathF.Pow(Clamp(value, 0f, 1f), 32f / 2523f);
        return MathF.Pow(MathF.Max(p - 3424f / 4096f, 0f) / MathF.Max(2413f / 128f - 2392f / 128f * p, 1e-7f), 16384f / 2610f);
    }
}
#endif
