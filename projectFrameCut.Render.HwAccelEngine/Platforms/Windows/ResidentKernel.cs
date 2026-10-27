#if WINDOWS || LINUX || HEADLESS
using ILGPU;
using System.Runtime.InteropServices;
using GpuHalf = ILGPU.Half;

namespace projectFrameCut.Render.HwAccelEngine.Effect;
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ResidentKernelPixel
{
    public GpuHalf Red, Green, Blue, Alpha, Brightness, Padding;
}

internal static class ResidentKernel
{
    private static int I(float x) => (int)x;
    private static float F(int x) => x;
    private static GpuHalf H(float x) => (GpuHalf)x;
    private static GpuHalf HalfParameter(ArrayView<float> p, int index) => p.Cast<GpuHalf>()[index * 2];
    private static float V(GpuHalf value) => (float)value;
    private static float Read(ArrayView<ResidentKernelPixel> data, int i, int c)
    {
        return c == 0 ? V(data[i].Red) : c == 1 ? V(data[i].Green) : V(data[i].Blue);
    }

    private static GpuHalf ReadHalf(ArrayView<ResidentKernelPixel> data, int i, int c)
    {
        return c == 3 ? data[i].Alpha : data[i].Brightness;
    }

    private static void Store(ArrayView<ResidentKernelPixel> output, int i, int c, float value)
    {
        if (c == 0)
            output[i].Red = H(value);
        else if (c == 1)
            output[i].Green = H(value);
        else
            output[i].Blue = H(value);
    }

    private static void StoreHalf(ArrayView<ResidentKernelPixel> output, int i, int c, GpuHalf value)
    {
        if (c == 3)
            output[i].Alpha = value;
        else
            output[i].Brightness = value;
    }

    private static float Clamp(float value, float low, float high) => value < low ? low : value > high ? high : value;
    private static GpuHalf ClampHalf(GpuHalf value) => value < H(0f) ? H(0f) : value > H(1f) ? H(1f) : value;
    private static float Sample(ArrayView<ResidentKernelPixel> data, int w, int h, int c, float x, float y)
    {
        if (x < 0f || y < 0f || x >= F(w) || y >= F(h))
            return 0f;
        int x0 = I(x), y0 = I(y), x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float fx = x - F(x0), fy = y - F(y0);
        return Read(data, y0 * w + x0, c) * (1f - fx) * (1f - fy) + Read(data, y0 * w + x1, c) * fx * (1f - fy) + Read(data, y1 * w + x0, c) * (1f - fx) * fy + Read(data, y1 * w + x1, c) * fx * fy;
    }

    private static GpuHalf SampleHalf(ArrayView<ResidentKernelPixel> data, int w, int h, int c, float x, float y)
    {
        if (x < 0f || y < 0f || x >= F(w) || y >= F(h))
            return H(0f);
        int x0 = I(x), y0 = I(y), x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        GpuHalf fx = H(x - F(x0)), fy = H(y - F(y0));
        return ReadHalf(data, y0 * w + x0, c) * (H(1f) - fx) * (H(1f) - fy) + ReadHalf(data, y0 * w + x1, c) * fx * (H(1f) - fy) + ReadHalf(data, y1 * w + x0, c) * (H(1f) - fx) * fy + ReadHalf(data, y1 * w + x1, c) * fx * fy;
    }

    private static float SamplePremultiplied(ArrayView<ResidentKernelPixel> data, int w, int h, int c, float x, float y)
    {
        int x0 = I(MathF.Floor(x)), y0 = I(MathF.Floor(y));
        GpuHalf fx = H(x - F(x0)), fy = H(y - F(y0)), alpha = H(0f);
        float color = 0f;
        for (int j = 0; j < 2; j++)
            for (int k = 0; k < 2; k++)
            {
                int px = x0 + k, py = y0 + j;
                if (px < 0 || py < 0 || px >= w || py >= h)
                    continue;
                int i = py * w + px;
                GpuHalf weight = (k == 0 ? H(1f) - fx : fx) * (j == 0 ? H(1f) - fy : fy) * ReadHalf(data, i, 3);
                alpha += weight;
                color += Read(data, i, c) * (float)weight;
            }

        return alpha > H(0f) ? color / (float)alpha : 0f;
    }

    private static GpuHalf SampleHalfPremultiplied(ArrayView<ResidentKernelPixel> data, int w, int h, int c, float x, float y)
    {
        int x0 = I(MathF.Floor(x)), y0 = I(MathF.Floor(y));
        GpuHalf fx = H(x - F(x0)), fy = H(y - F(y0)), alpha = H(0f), brightness = H(0f);
        for (int j = 0; j < 2; j++)
            for (int k = 0; k < 2; k++)
            {
                int px = x0 + k, py = y0 + j;
                if (px < 0 || py < 0 || px >= w || py >= h)
                    continue;
                int i = py * w + px;
                GpuHalf weight = (k == 0 ? H(1f) - fx : fx) * (j == 0 ? H(1f) - fy : fy) * ReadHalf(data, i, 3);
                alpha += weight;
                brightness += ReadHalf(data, i, 4) * weight;
            }

        return c == 3 ? alpha : alpha > H(0f) ? brightness / alpha : H(0f);
    }

    private static GpuHalf Luminance(ArrayView<ResidentKernelPixel> data, int w, int h, float x, float y, float maximum)
    {
        return ClampHalf(H(Sample(data, w, h, 0, x, y) / maximum) * H(0.2627f) + H(Sample(data, w, h, 1, x, y) / maximum) * H(0.6780f) + H(Sample(data, w, h, 2, x, y) / maximum) * H(0.0593f));
    }

    private static GpuHalf Channel(ArrayView<ResidentKernelPixel> src, ArrayView<ResidentKernelPixel> second, ArrayView<float> p, int i, int c, float sx, float sy)
    {
        int op = I(p[0]), sw = I(p[1]), sh = I(p[2]), w = I(p[3]), tw = I(p[7]), th = I(p[8]);
        float x = F(i % w), y = F(i / w);
        GpuHalf value = c == 4 && p[12] < 0.5f ? H(1f) : op == 3 && p[17] > 0f ? SampleHalfPremultiplied(src, sw, sh, c, sx, sy) : SampleHalf(src, sw, sh, c, sx, sy);
        if (op == 9 && c == 3)
            value *= HalfParameter(p, 15);
        if (op == 10 && c == 3)
            value *= HalfParameter(p, 24);
        if (op == 11 && c == 3)
        {
            float scale = 65535f / p[5];
            if (MathF.Abs(V(src[i].Red) * scale - p[15]) <= p[18] && MathF.Abs(V(src[i].Green) * scale - p[16]) <= p[18] && MathF.Abs(V(src[i].Blue) * scale - p[17]) <= p[18])
                value = H(0f);
        }

        if (op == 12)
        {
            float tx = x - p[15], ty = y - p[16];
            GpuHalf ba = SampleHalf(src, sw, sh, 3, x, y), ta = SampleHalf(second, tw, th, 3, tx, ty);
            GpuHalf weight = ba * (H(1f) - ta), alpha = ta + weight;
            GpuHalf bottom = value, top = c == 4 && p[13] < 0.5f ? H(1f) : SampleHalf(second, tw, th, c, tx, ty);
            if (c == 4)
            {
                if (p[12] < 0.5f)
                    bottom = Luminance(src, sw, sh, x, y, p[5]);
                if (p[13] < 0.5f)
                    top = Luminance(second, tw, th, tx, ty, p[9]);
            }

            if (I(p[17]) == 0 && p[18] > 0f && ta <= H(0.05f))
                value = c == 3 ? ba : bottom;
            else if (I(p[17]) == 0 && p[18] > 0f && ta >= H(0.999f))
                value = c == 3 ? H(1f) : top;
            else
                value = c == 3 ? alpha : alpha > H(0f) ? (top * ta + bottom * weight) / alpha : H(0f);
        }

        if (op == 13)
        {
            GpuHalf progress = HalfParameter(p, 15);
            GpuHalf la = SampleHalf(src, sw, sh, 3, x, y) * (H(1f) - progress), ra = SampleHalf(second, tw, th, 3, x, y) * progress;
            GpuHalf alpha = la + ra, left = value, right = c == 4 && p[13] < 0.5f ? H(1f) : SampleHalf(second, tw, th, c, x, y);
            if (c == 4)
            {
                left *= HalfParameter(p, 10) / HalfParameter(p, 16);
                right *= HalfParameter(p, 11) / HalfParameter(p, 16);
            }

            value = c == 3 ? alpha : alpha > H(0f) ? (left * la + right * ra) / alpha : H(0f);
        }

        if (op == 14 && c == 4)
            value = Luminance(src, sw, sh, x, y, p[5]);
        return c == 3 ? ClampHalf(value) : value;
    }

    private static float Hue(float p, float q, float t)
    {
        if (t < 0f)
            t += 1f;
        if (t > 1f)
            t -= 1f;
        return t < 1f / 6f ? p + (q - p) * 6f * t : t < 0.5f ? q : t < 2f / 3f ? p + (q - p) * (2f / 3f - t) * 6f : p;
    }

    private static float Adjust(float rr, float gg, float bb, int c, ArrayView<float> p)
    {
        float maxV = p[5], bf = p[15] - 1f;
        float r = bf >= 0f ? rr + (maxV - rr) * bf : rr * (1f + bf);
        float g = bf >= 0f ? gg + (maxV - gg) * bf : gg * (1f + bf);
        float b = bf >= 0f ? bb + (maxV - bb) * bf : bb * (1f + bf);
        r = ((r / maxV - 0.5f) * p[16] + 0.5f) * maxV;
        g = ((g / maxV - 0.5f) * p[16] + 0.5f) * maxV;
        b = ((b / maxV - 0.5f) * p[16] + 0.5f) * maxV;
        if (MathF.Abs(p[17] - 1f) > 1e-6f)
        {
            float gray = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            r = gray + p[17] * (r - gray);
            g = gray + p[17] * (g - gray);
            b = gray + p[17] * (b - gray);
        }

        if (MathF.Abs(p[18]) > 1e-6f)
        {
            float nr = r / maxV, ng = g / maxV, nb = b / maxV;
            float hi = MathF.Max(nr, MathF.Max(ng, nb)), lo = MathF.Min(nr, MathF.Min(ng, nb));
            float delta = hi - lo, h = 0f;
            if (delta > 1e-6f)
            {
                if (hi == nr)
                {
                    float t = (ng - nb) / delta;
                    h = 60f * (t - 6f * F(I(t / 6f)));
                }
                else if (hi == ng)
                    h = 60f * ((nb - nr) / delta + 2f);
                else
                    h = 60f * ((nr - ng) / delta + 4f);
                if (h < 0f)
                    h += 360f;
            }

            float l = (hi + lo) * 0.5f;
            float s = l > 0f && l < 1f ? delta / (1f - MathF.Abs(2f * l - 1f)) : 0f;
            h += p[18];
            if (h < 0f)
                h += 360f;
            if (h >= 360f)
                h -= 360f;
            if (s < 1e-6f)
            {
                r = l * maxV;
                g = r;
                b = r;
            }
            else
            {
                float q = l < 0.5f ? l * (1f + s) : l + s - l * s, t = 2f * l - q;
                r = Hue(t, q, h / 360f + 1f / 3f) * maxV;
                g = Hue(t, q, h / 360f) * maxV;
                b = Hue(t, q, h / 360f - 1f / 3f) * maxV;
            }
        }

        if (MathF.Abs(p[19] - 1f) > 1e-6f)
        {
            r = maxV * MathF.Pow(r / maxV, 1f / p[19]);
            g = maxV * MathF.Pow(g / maxV, 1f / p[19]);
            b = maxV * MathF.Pow(b / maxV, 1f / p[19]);
        }

        if (MathF.Abs(p[20]) > 1e-6f)
        {
            float gray = 0.2126f * r + 0.7152f * g + 0.0722f * b, sat = 1f + p[20] * 0.5f;
            r = gray + sat * (r - gray);
            g = gray + sat * (g - gray);
            b = gray + sat * (b - gray);
        }

        r *= 1f + p[21] * 0.01f;
        b *= 1f - p[21] * 0.01f;
        if (p[22] > 0.5f)
        {
            r = maxV - r;
            g = maxV - g;
            b = maxV - b;
        }

        if (p[23] > 1e-6f)
        {
            float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b, gs = p[23] >= 1f ? 1f : 1f - p[23];
            r = lum + gs * (r - lum);
            g = lum + gs * (g - lum);
            b = lum + gs * (b - lum);
        }

        return c == 0 ? r : c == 1 ? g : b;
    }

    internal static void Kernel(Index1D index, ArrayView<ResidentKernelPixel> src, ArrayView<ResidentKernelPixel> second, ArrayView<ResidentKernelPixel> output, ArrayView<float> p)
    {
        int i = index;
        int op = I(p[0]), sw = I(p[1]), sh = I(p[2]), w = I(p[3]), h = I(p[4]);
        int channels = p[14] > 0f ? 5 : 4;
        float maxOut = p[6];
        int tw = I(p[7]), th = I(p[8]);
        if (op == 5 || op == 6)
        {
            int lines = op == 5 ? h : w, c = i / lines, line = i % lines;
            int length = op == 5 ? w : h, step = op == 5 ? 1 : w, start = op == 5 ? line * w : line;
            int radius = I(p[15]), count = 0;
            if (c >= 3)
            {
                GpuHalf sum = H(0f), weight = H(1f / (2f * F(radius) + 1f));
                for (int k = 0; k <= radius && k < length; k++)
                {
                    sum += ReadHalf(src, start + k * step, c) * weight;
                    count++;
                }

                for (int j = 0; j < length; j++)
                {
                    StoreHalf(output, start + j * step, c, c == 4 ? ReadHalf(src, start + j * step, c) : ClampHalf(sum / H(F(count) / (2f * F(radius) + 1f))));
                    if (channels == 4)
                        StoreHalf(output, start + j * step, 4, H(1f));
                    if (j - radius >= 0)
                    {
                        sum -= ReadHalf(src, start + (j - radius) * step, c) * weight;
                        count--;
                    }

                    if (j + radius + 1 < length)
                    {
                        sum += ReadHalf(src, start + (j + radius + 1) * step, c) * weight;
                        count++;
                    }
                }

                return;
            }

            float total = 0f;
            for (int k = 0; k <= radius && k < length; k++)
            {
                total += Read(src, start + k * step, c);
                count++;
            }

            for (int j = 0; j < length; j++)
            {
                float value = total / F(count);
                Store(output, start + j * step, c, Clamp(value, 0f, maxOut));
                if (j - radius >= 0)
                {
                    total -= Read(src, start + (j - radius) * step, c);
                    count--;
                }

                if (j + radius + 1 < length)
                {
                    total += Read(src, start + (j + radius + 1) * step, c);
                    count++;
                }
            }

            return;
        }

        int x = i % w, y = i / w;
        float fx = F(x), fy = F(y), fw = F(w), fh = F(h), fsw = F(sw), fsh = F(sh), sx = fx, sy = fy;
        if (op == 1)
        {
            sx = Clamp((fx + 0.5f) * fsw / fw - 0.5f, 0f, fsw - 1f);
            sy = Clamp((fy + 0.5f) * fsh / fh - 0.5f, 0f, fsh - 1f);
        }

        if (op == 2)
        {
            sx = p[17] * (fx - fw / 2f) - p[18] * (fy - fh / 2f) + p[15] + fw / 2f;
            sy = p[18] * (fx - fw / 2f) + p[17] * (fy - fh / 2f) + p[16] + fh / 2f;
        }

        if (op == 3)
        {
            sx = p[15] * (fx + p[17] - fw / 2f) - p[16] * (fy + p[17] - fh / 2f) + fsw / 2f - p[17];
            sy = p[16] * (fx + p[17] - fw / 2f) + p[15] * (fy + p[17] - fh / 2f) + fsh / 2f - p[17];
        }

        if (op == 4)
        {
            sx = p[15] > 0f ? fsw - 1f - fx : fx;
            sy = p[16] > 0f ? fsh - 1f - fy : fy;
        }

        if (op == 15)
        {
            sx = fx - p[15];
            sy = fy - p[16];
        }

        for (int c = 0; c < 3; c++)
        {
            float value = op == 3 && p[17] > 0f ? SamplePremultiplied(src, sw, sh, c, sx, sy) : Sample(src, sw, sh, c, sx, sy);
            if (op == 0 || op == 14)
                value = p[5] > maxOut ? MathF.Floor(value / 257f) : value * (maxOut / p[5]);
            if (op == 7)
            {
                float average = (Sample(src, sw, sh, c, F(Math.Max(x - 1, 0)), fy) + Sample(src, sw, sh, c, F(Math.Min(x + 1, sw - 1)), fy) + Sample(src, sw, sh, c, fx, F(Math.Max(y - 1, 0))) + Sample(src, sw, sh, c, fx, F(Math.Min(y + 1, sh - 1)))) * 0.25f;
                value += p[15] * (value - average);
            }

            if (op == 8)
            {
                float dx = (fx - fsw * 0.5f) / (fsw * 0.5f), dy = (fy - fsh * 0.5f) / (fsh * 0.5f), distance = MathF.Sqrt(dx * dx + dy * dy);
                if (distance > p[16])
                {
                    float t = MathF.Min((distance - p[16]) / (1f - p[16]), 1f);
                    value *= 1f - t * t * p[15];
                }
            }

            if (op == 10)
                value = Adjust(V(src[i].Red), V(src[i].Green), V(src[i].Blue), c, p);
            if (op == 11)
            {
                float scale = 65535f / p[5];
                if (MathF.Abs(V(src[i].Red) * scale - p[15]) <= p[18] && MathF.Abs(V(src[i].Green) * scale - p[16]) <= p[18] && MathF.Abs(V(src[i].Blue) * scale - p[17]) <= p[18] || src[i].Alpha == H(0f))
                    value = 0f;
            }

            if (op == 12)
            {
                float tx = fx - p[15], ty = fy - p[16];
                GpuHalf ba = SampleHalf(src, sw, sh, 3, fx, fy), ta = SampleHalf(second, tw, th, 3, tx, ty), weight = ba * (H(1f) - ta), alpha = ta + weight;
                float bottom = value * maxOut / p[5], top = Sample(second, tw, th, c, tx, ty) * maxOut / p[9], blend = top;
                int mode = I(p[17]);
                if (mode == 1)
                    blend = MathF.Min(top + bottom, maxOut);
                if (mode == 2)
                    blend = MathF.Max(bottom - top, 0f);
                if (mode == 3)
                    blend = top * bottom / maxOut;
                if (mode == 4)
                    blend = maxOut - (maxOut - top) * (maxOut - bottom) / maxOut;
                if (mode == 5)
                    blend = bottom < maxOut * 0.5f ? 2f * top * bottom / maxOut : maxOut - 2f * (maxOut - top) * (maxOut - bottom) / maxOut;
                if (mode == 6)
                    blend = MathF.Min(top, bottom);
                if (mode == 7)
                    blend = MathF.Max(top, bottom);
                if (mode == 8)
                    blend = MathF.Abs(bottom - top);
                if (mode == 0 && p[18] > 0f && ta <= H(0.05f))
                    value = bottom;
                else if (mode == 0 && p[18] > 0f && ta >= H(0.999f))
                    value = top;
                else
                    value = alpha > H(1e-6f) ? (blend * (float)ta + bottom * (float)weight) / (float)alpha : 0f;
            }

            if (op == 13)
            {
                GpuHalf progress = HalfParameter(p, 15), la = SampleHalf(src, sw, sh, 3, fx, fy) * (H(1f) - progress), ra = SampleHalf(second, tw, th, 3, fx, fy) * progress, alpha = la + ra;
                float left = value * maxOut / p[5], right = Sample(second, tw, th, c, fx, fy) * maxOut / p[9];
                value = alpha > H(0f) ? (left * (float)la + right * (float)ra) / (float)alpha : 0f;
            }

            Store(output, i, c, Clamp(value, 0f, maxOut));
        }

        StoreHalf(output, i, 3, Channel(src, second, p, i, 3, sx, sy));
        StoreHalf(output, i, 4, channels == 5 ? Channel(src, second, p, i, 4, sx, sy) : H(1f));
    }
}
#endif
