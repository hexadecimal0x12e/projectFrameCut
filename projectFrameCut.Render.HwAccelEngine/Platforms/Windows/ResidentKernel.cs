#if WINDOWS || LINUX || HEADLESS
using ILGPU;

namespace projectFrameCut.Render.HwAccelEngine.Effect;
internal static class ResidentKernel
{
    private static int I(float x) => (int)x;
    // GLSL ES requires explicit conversions between integer coordinates and floats.
    private static float F(int x) => x;
    private static float Read(ArrayView<float> data, int i) => data[i];
    private static float Clamp(float value, float low, float high)
    {
        return value < low ? low : value > high ? high : value;
    }

    private static float Sample(ArrayView<float> data, int w, int h, int c, float x, float y)
    {
        if (x < 0f || y < 0f || x >= F(w) || y >= F(h))
            return 0f;
        int x0 = I(x), y0 = I(y), x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float fx = x - F(x0), fy = y - F(y0);
        int offset = c * w * h;
        return Read(data, offset + y0 * w + x0) * (1f - fx) * (1f - fy) + Read(data, offset + y0 * w + x1) * fx * (1f - fy) + Read(data, offset + y1 * w + x0) * (1f - fx) * fy + Read(data, offset + y1 * w + x1) * fx * fy;
    }

    private static float Hue(float p, float q, float t)
    {
        if (t < 0f)
            t += 1f;
        if (t > 1f)
            t -= 1f;
        return t < 1f / 6f ? p + (q - p) * 6f * t : t < 0.5f ? q : t < 2f / 3f ? p + (q - p) * (2f / 3f - t) * 6f : p;
    }

    private static float SamplePremultiplied(ArrayView<float> data, int w, int h, int c, float x, float y)
    {
        int x0 = I(MathF.Floor(x)), y0 = I(MathF.Floor(y));
        float fx = x - F(x0), fy = y - F(y0), alpha = 0f, color = 0f;
        for (int j = 0; j < 2; j++)
        {
            for (int k = 0; k < 2; k++)
            {
                int px = x0 + k, py = y0 + j;
                if (px < 0 || py < 0 || px >= w || py >= h)
                    continue;
                int i = py * w + px;
                float weight = (k == 0 ? 1f - fx : fx) * (j == 0 ? 1f - fy : fy) * Read(data, w * h * 3 + i);
                alpha += weight;
                color += Read(data, c * w * h + i) * weight;
            }
        }

        return c == 3 ? alpha : alpha > 1e-10f ? color / alpha : 0f;
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

    internal static void Kernel(Index1D index, ArrayView<float> src, ArrayView<float> second, ArrayView<float> output, ArrayView<float> p)
    {
        int i = index;
        int op = I(p[0]), sw = I(p[1]), sh = I(p[2]), w = I(p[3]), h = I(p[4]);
        int channels = p[14] > 0f ? 5 : 4;
        float maxOut = p[6];
        int sn = sw * sh, n = w * h, tw = I(p[7]), th = I(p[8]);
        if (op == 5 || op == 6)
        {
            int lines = op == 5 ? h : w, c = i / lines, line = i % lines;
            int length = op == 5 ? w : h, step = op == 5 ? 1 : w, start = c * n + (op == 5 ? line * w : line);
            int radius = I(p[15]), count = 0;
            float sum = 0f;
            for (int k = 0; k <= radius && k < length; k++)
            {
                sum += src[start + k * step];
                count++;
            }

            for (int j = 0; j < length; j++)
            {
                float v = c == 4 ? src[start + j * step] : sum / F(count);
                output[start + j * step] = op == 6 && c < 3 ? F(I(Clamp(v, 0f, maxOut))) : v;
                if (j - radius >= 0)
                {
                    sum -= src[start + (j - radius) * step];
                    count--;
                }

                if (j + radius + 1 < length)
                {
                    sum += src[start + (j + radius + 1) * step];
                    count++;
                }
            }

            return;
        }

        int x = i % w, y = i / w;
        float fx = F(x), fy = F(y), fw = F(w), fh = F(h), fsw = F(sw), fsh = F(sh);
        for (int c = 0; c < channels; c++)
        {
            float sx = fx, sy = fy, value = 0f;
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

            if (c == 4 && p[12] < 0.5f)
                value = 1f;
            else
                value = op == 3 && p[17] > 0f ? SamplePremultiplied(src, sw, sh, c, sx, sy) : Sample(src, sw, sh, c, sx, sy);
            if ((op == 0 || op == 14) && c < 3)
                value = p[5] > maxOut ? MathF.Floor(value / 257f) : value * (maxOut / p[5]);
            if (op == 7 && c < 3)
            {
                float avg = (Sample(src, sw, sh, c, F(Math.Max(x - 1, 0)), fy) + Sample(src, sw, sh, c, F(Math.Min(x + 1, sw - 1)), fy) + Sample(src, sw, sh, c, fx, F(Math.Max(y - 1, 0))) + Sample(src, sw, sh, c, fx, F(Math.Min(y + 1, sh - 1)))) * 0.25f;
                value += p[15] * (value - avg);
            }

            if (op == 8 && c < 3)
            {
                float dx = (fx - fsw * 0.5f) / (fsw * 0.5f), dy = (fy - fsh * 0.5f) / (fsh * 0.5f);
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                if (dist > p[16])
                {
                    float t = MathF.Min((dist - p[16]) / (1f - p[16]), 1f);
                    value *= 1f - t * t * p[15];
                }
            }

            if (op == 9 && c == 3)
                value *= p[15];
            if (op == 10 && c < 3)
                value = Adjust(src[i], src[sn + i], src[sn * 2 + i], c, p);
            if (op == 10 && c == 3)
                value *= p[24];
            if (op == 11 && c < 4)
            {
                float scale = 65535f / p[5];
                bool remove = MathF.Abs(src[i] * scale - p[15]) <= p[18] && MathF.Abs(src[sn + i] * scale - p[16]) <= p[18] && MathF.Abs(src[sn * 2 + i] * scale - p[17]) <= p[18];
                if (remove || src[sn * 3 + i] == 0f)
                    value = 0f;
            }

            if (op == 12)
            {
                float tx = fx - p[15], ty = fy - p[16];
                float ba = Sample(src, sw, sh, 3, fx, fy), ta = Sample(second, tw, th, 3, tx, ty);
                float a = ta + ba * (1f - ta), bottom = value, top = c == 4 && p[13] < 0.5f ? 1f : Sample(second, tw, th, c, tx, ty);
                if (c < 3)
                {
                    bottom = F(I(bottom * maxOut / p[5]));
                    top *= maxOut / p[9];
                }

                if (c == 4)
                {
                    if (p[12] < 0.5f)
                        bottom = Clamp((0.2627f * Sample(src, sw, sh, 0, fx, fy) + 0.6780f * Sample(src, sw, sh, 1, fx, fy) + 0.0593f * Sample(src, sw, sh, 2, fx, fy)) / p[5], 0f, 1f);
                    if (p[13] < 0.5f)
                        top = Clamp((0.2627f * Sample(second, tw, th, 0, tx, ty) + 0.6780f * Sample(second, tw, th, 1, tx, ty) + 0.0593f * Sample(second, tw, th, 2, tx, ty)) / p[9], 0f, 1f);
                }

                float blend = top;
                int mode = I(p[17]);
                if (c < 3)
                {
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
                }

                value = c == 3 ? a : a > 1e-6f ? (blend * ta + bottom * ba * (1f - ta)) / a : 0f;
                if (mode == 0 && ta <= 0.05f)
                    value = c == 3 ? ba : bottom;
                if (mode == 0 && ta >= 0.999f)
                    value = c == 3 ? 1f : top;
            }

            if (op == 13)
            {
                float la = Sample(src, sw, sh, 3, fx, fy) * (1f - p[15]);
                float ra = Sample(second, tw, th, 3, fx, fy) * p[15], a = la + ra;
                float l = value, r = c == 4 && p[13] < 0.5f ? 1f : Sample(second, tw, th, c, fx, fy);
                if (c < 3)
                {
                    l *= maxOut / p[5];
                    r *= maxOut / p[9];
                }

                if (c == 4)
                {
                    l *= p[10] / p[16];
                    r *= p[11] / p[16];
                }

                value = c == 3 ? a : a > 0f ? (l * la + r * ra) / a : 0f;
            }

            if (op == 14 && c == 4)
                value = Clamp((0.2627f * src[i] + 0.6780f * src[sn + i] + 0.0593f * src[sn * 2 + i]) / p[5], 0f, 1f);
            if (c < 3)
            {
                if (op == 1 || op == 2)
                    value += 0.5f;
                if (op == 13 || op == 3 && p[17] > 0f)
                {
                    float low = MathF.Floor(value), fraction = value - low;
                    value = fraction > 0.5f || fraction == 0.5f && I(low) % 2 != 0 ? low + 1f : low;
                }

                value = F(I(Clamp(value, 0f, maxOut)));
            }

            if (c == 3)
                value = Clamp(value, 0f, 1f);
            output[c * n + i] = value;
        }
    }
}
#endif
