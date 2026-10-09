#if WINDOWS || LINUX || HEADLESS
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.OpenCL;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Device = ILGPU.Runtime.Device;
using projectFrameCut.Render.WindowsRender;

namespace projectFrameCut.Render.HwAccelEngine.Effect
{
    public partial class ColorAdjustmentEffect_HwAccel
    {
        private int accelIdx = 0;
        // 11 params packed as: brightness, contrast, saturation, hue, gamma,
        //                       vibrance, temperature, invertF, grayscale, opacity, maxV
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>>> KernelCache = new();
        private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>> GetKernel(Accelerator accelerator)
        {
            return KernelCache.GetValue(accelerator, static acc => acc.LoadAutoGroupedStreamKernel((Index1D i, ArrayView<float> rOut, ArrayView<float> gOut, ArrayView<float> bOut, ArrayView<float> aOut, ArrayView<float> rIn, ArrayView<float> gIn, ArrayView<float> bIn, ArrayView<float> aIn, ArrayView<float> p) =>
            {
                float r = rIn[i], g = gIn[i], b = bIn[i], a = aIn[i];
                float brightness = p[0], contrast = p[1], saturation = p[2], hue = p[3], gamma = p[4];
                float vibrance = p[5], temperature = p[6], invertF = p[7], grayscale = p[8], opacity = p[9], maxV = p[10];
                // 1. Brightness
                float bf = brightness - 1f;
                r = bf >= 0f ? r + (maxV - r) * bf : r * (1f + bf);
                g = bf >= 0f ? g + (maxV - g) * bf : g * (1f + bf);
                b = bf >= 0f ? b + (maxV - b) * bf : b * (1f + bf);
                // 2. Contrast
                r = ((r / maxV - 0.5f) * contrast + 0.5f) * maxV;
                g = ((g / maxV - 0.5f) * contrast + 0.5f) * maxV;
                b = ((b / maxV - 0.5f) * contrast + 0.5f) * maxV;
                // 3. Saturation (skip when == 1.0 to avoid redundant luminance calc)
                if (MathF.Abs(saturation - 1f) > 1e-6f)
                {
                    float gray = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    r = gray + saturation * (r - gray);
                    g = gray + saturation * (g - gray);
                    b = gray + saturation * (b - gray);
                }

                // 4. Hue (RGB->HSL, rotate H, HSL->RGB all inline)
                if (MathF.Abs(hue) > 1e-6f)
                {
                    float nr = r / maxV, ng = g / maxV, nb = b / maxV;
                    float cMax = MathF.Max(nr, MathF.Max(ng, nb));
                    float cMin = MathF.Min(nr, MathF.Min(ng, nb));
                    float delta = cMax - cMin;
                    float h = 0f;
                    if (delta > 1e-6f)
                    {
                        if (cMax == nr)
                        {
                            float t = (ng - nb) / delta;
                            h = 60f * (t - 6f * (int)(t / 6f));
                        }
                        else if (cMax == ng)
                            h = 60f * (((nb - nr) / delta) + 2f);
                        else
                            h = 60f * (((nr - ng) / delta) + 4f);
                        if (h < 0f)
                            h += 360f;
                    }

                    float l = (cMax + cMin) * 0.5f;
                    float s = (l > 0f && l < 1f) ? delta / (1f - MathF.Abs(2f * l - 1f)) : 0f;
                    h += hue;
                    if (h < 0f)
                        h += 360f;
                    if (h >= 360f)
                        h -= 360f;
                    if (s < 1e-6f)
                    {
                        r = l * maxV;
                        g = l * maxV;
                        b = l * maxV;
                    }
                    else
                    {
                        float qq = l < 0.5f ? l * (1f + s) : l + s - l * s;
                        float pp = 2f * l - qq;
                        float hN = h / 360f;
                        float Tr = hN + 1f / 3f;
                        if (Tr < 0f)
                            Tr += 1f;
                        if (Tr > 1f)
                            Tr -= 1f;
                        float Tg = hN;
                        if (Tg < 0f)
                            Tg += 1f;
                        if (Tg > 1f)
                            Tg -= 1f;
                        float Tb = hN - 1f / 3f;
                        if (Tb < 0f)
                            Tb += 1f;
                        if (Tb > 1f)
                            Tb -= 1f;
                        r = (Tr < 1f / 6f ? pp + (qq - pp) * 6f * Tr : Tr < 1f / 2f ? qq : Tr < 2f / 3f ? pp + (qq - pp) * (2f / 3f - Tr) * 6f : pp) * maxV;
                        g = (Tg < 1f / 6f ? pp + (qq - pp) * 6f * Tg : Tg < 1f / 2f ? qq : Tg < 2f / 3f ? pp + (qq - pp) * (2f / 3f - Tg) * 6f : pp) * maxV;
                        b = (Tb < 1f / 6f ? pp + (qq - pp) * 6f * Tb : Tb < 1f / 2f ? qq : Tb < 2f / 3f ? pp + (qq - pp) * (2f / 3f - Tb) * 6f : pp) * maxV;
                    }
                }

                // 5. Gamma
                if (MathF.Abs(gamma - 1f) > 1e-6f)
                {
                    float invGamma = 1f / gamma;
                    r = maxV * MathF.Pow(r / maxV, invGamma);
                    g = maxV * MathF.Pow(g / maxV, invGamma);
                    b = maxV * MathF.Pow(b / maxV, invGamma);
                }

                // 6. Vibrance
                if (MathF.Abs(vibrance) > 1e-6f)
                {
                    float vSat = 1f + vibrance * 0.5f;
                    float vGray = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    r = vGray + vSat * (r - vGray);
                    g = vGray + vSat * (g - vGray);
                    b = vGray + vSat * (b - vGray);
                }

                // 7. Temperature
                if (MathF.Abs(temperature) > 1e-6f)
                {
                    r *= 1f + temperature * 0.01f;
                    b *= 1f - temperature * 0.01f;
                }

                // 8. Invert
                if (invertF > 0.5f)
                {
                    r = maxV - r;
                    g = maxV - g;
                    b = maxV - b;
                }

                // 9. Grayscale
                if (grayscale > 1e-6f)
                {
                    float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    float gs = grayscale >= 1f ? 1f : 1f - grayscale;
                    r = lum + gs * (r - lum);
                    g = lum + gs * (g - lum);
                    b = lum + gs * (b - lum);
                }

                // 10. Opacity
                a *= opacity;
                rOut[i] = r;
                gOut[i] = g;
                bOut[i] = b;
                aOut[i] = a;
            }));
        }

        private FourChannelResult ComputeColorAdjustment(float[] r, float[] g, float[] b, float[] a, int width, int height, float brightness, float contrast, float saturation, float hue, float gamma, float vibrance, float temperature, bool invert, float grayscale, float opacity, float maxVal)
        {
            using var deviceLease = AcceleratorsManager.AcquireExecution();
            var accelerator = ILGPUExecutionHelper.SelectAccelerator(deviceLease.Accelerators, ref accelIdx);
            int length = r.Length;
            float invertF = invert ? 1f : 0f;
            float[] paramArr = [brightness, contrast, saturation, hue, gamma, vibrance, temperature, invertF, grayscale, opacity, maxVal];
            using var rBufIn = accelerator.Allocate1D(r);
            using var gBufIn = accelerator.Allocate1D(g);
            using var bBufIn = accelerator.Allocate1D(b);
            using var aBufIn = accelerator.Allocate1D(a);
            using var paramBuf = accelerator.Allocate1D(paramArr);
            using var rBufOut = accelerator.Allocate1D<float>(length);
            using var gBufOut = accelerator.Allocate1D<float>(length);
            using var bBufOut = accelerator.Allocate1D<float>(length);
            using var aBufOut = accelerator.Allocate1D<float>(length);
            var kernel = GetKernel(accelerator);
            if (ILGPUExecutionHelper.SyncOverride ?? accelerator.AcceleratorType == AcceleratorType.OpenCL)
            {
                using (ILGPUExecutionHelper.locker.EnterScope())
                {
                    kernel(length, rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View, rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View, paramBuf.View);
                    accelerator.Synchronize();
                }
            }
            else
            {
                kernel(length, rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View, rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View, paramBuf.View);
            }

            return new FourChannelResult(rBufOut.GetAsArray1D(), gBufOut.GetAsArray1D(), bBufOut.GetAsArray1D(), aBufOut.GetAsArray1D());
        }
    }
}
#endif
