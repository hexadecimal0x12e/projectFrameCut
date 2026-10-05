#if ANDROID
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Handlers;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using projectFrameCut.Render.HwAccelEngine.Platforms.Android;
using static projectFrameCut.Render.HwAccelEngine.Platforms.Android.GLComputeView;

namespace projectFrameCut.Render.HwAccelEngine.Effect
{
    public partial class ColorAdjustmentEffect_HwAccel
    {
        private readonly ComputeBackend backend = AndroidExecutionHelper.PreferredBackend;
        private FourChannelResult ComputeColorAdjustment(float[] r, float[] g, float[] b, float[] a, int w, int h, float brightness, float contrast, float saturation, float hue, float gamma, float vibrance, float temperature, bool invert, float grayscale, float opacity, float maxVal) => backend == ComputeBackend.Vulkan ? VulkanComputeColorAdjustment(r, g, b, a, w, h, brightness, contrast, saturation, hue, gamma, vibrance, temperature, invert, grayscale, opacity, maxVal) : OpenGLComputeColorAdjustment(r, g, b, a, w, h, brightness, contrast, saturation, hue, gamma, vibrance, temperature, invert, grayscale, opacity, maxVal);
        private FourChannelResult OpenGLComputeColorAdjustment(float[] r, float[] g, float[] b, float[] a, int w, int h, float brightness, float contrast, float saturation, float hue, float gamma, float vibrance, float temperature, bool invert, float grayscale, float opacity, float maxVal)
        {
            var rIn = r;
            var gIn = g;
            var bIn = b;
            var aIn = a;
            float brightnessValue = brightness, contrastValue = contrast;
            float saturationValue = saturation, hueValue = hue;
            float gammaValue = gamma, vibranceValue = vibrance;
            float temperatureValue = temperature, invertF = Convert.ToSingle((invert ? 1f : 0f));
            float grayscaleValue = grayscale, opacityValue = opacity;
            float maxV = maxVal;
            int srcLen = rIn.Length;
            // Pack RGBA into one buffer: R[0..N-1] G[0..N-1] B[0..N-1] A[0..N-1]
            int packedLen = srcLen * 4;
            var packed = new float[packedLen];
            Array.Copy(rIn, 0, packed, 0, srcLen);
            Array.Copy(gIn, 0, packed, srcLen, srcLen);
            Array.Copy(bIn, 0, packed, srcLen * 2, srcLen);
            Array.Copy(aIn, 0, packed, srcLen * 3, srcLen);
            float angleRad = hueValue * MathF.PI / 180f;
            string shader = $$"""
                #version 310 es
                layout(local_size_x = 256) in;
                layout(std430, binding = 0) buffer InBuffer { float inputData[]; };
                layout(std430, binding = 6) buffer OutBuffer { float outputData[]; };
                void main()
                {
                    uint i = gl_GlobalInvocationID.x;
                    if (i >= uint({{srcLen}})) { outputData[i] = 0.0; outputData[i+{{srcLen}}] = 0.0; outputData[i+{{srcLen * 2}}] = 0.0; outputData[i+{{srcLen * 3}}] = 0.0; return; }
                    float r = inputData[i], g = inputData[i+{{srcLen}}], b = inputData[i+{{srcLen * 2}}], a = inputData[i+{{srcLen * 3}}];

                    // 1. Brightness
                    float bf = {{brightnessValue}} - 1.0;
                    r = bf >= 0.0 ? r + ({{maxV}} - r) * bf : r * (1.0 + bf);
                    g = bf >= 0.0 ? g + ({{maxV}} - g) * bf : g * (1.0 + bf);
                    b = bf >= 0.0 ? b + ({{maxV}} - b) * bf : b * (1.0 + bf);

                    // 2. Contrast
                    r = ((r / {{maxV}} - 0.5) * {{contrastValue}} + 0.5) * {{maxV}};
                    g = ((g / {{maxV}} - 0.5) * {{contrastValue}} + 0.5) * {{maxV}};
                    b = ((b / {{maxV}} - 0.5) * {{contrastValue}} + 0.5) * {{maxV}};

                    // 3. Saturation
                    float gray = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                    r = gray + {{saturationValue}} * (r - gray);
                    g = gray + {{saturationValue}} * (g - gray);
                    b = gray + {{saturationValue}} * (b - gray);

                    // 4. Hue (inline RGB<->HSL)
                    float hh = 0.0, ss, ll;
                    {   float nr = r / {{maxV}}, ng = g / {{maxV}}, nb = b / {{maxV}};
                        float cMax = max(max(nr, ng), nb), cMin = min(min(nr, ng), nb);
                        float delta = cMax - cMin;
                        if (delta > 0.0) {
                            if (cMax == nr) hh = 60.0 * mod((ng - nb) / delta, 6.0);
                            else if (cMax == ng) hh = 60.0 * ((nb - nr) / delta + 2.0);
                            else hh = 60.0 * ((nr - ng) / delta + 4.0);
                            if (hh < 0.0) hh += 360.0;
                        }
                        ll = (cMax + cMin) * 0.5;
                        ss = (ll > 0.0 && ll < 1.0) ? delta / (1.0 - abs(2.0 * ll - 1.0)) : 0.0;
                    }
                    hh += {{hueValue}};
                    if (hh < 0.0) hh += 360.0; if (hh >= 360.0) hh -= 360.0;
                    if (ss < 0.000001) { r = ll * {{maxV}}; g = ll * {{maxV}}; b = ll * {{maxV}}; }
                    else {
                        float qq = ll < 0.5 ? ll * (1.0 + ss) : ll + ss - ll * ss;
                        float p = 2.0 * ll - qq;
                        float hN = hh / 360.0;
                        float Tr = hN + 0.333333; if (Tr < 0.0) Tr += 1.0; if (Tr > 1.0) Tr -= 1.0;
                        float Tg = hN; if (Tg < 0.0) Tg += 1.0; if (Tg > 1.0) Tg -= 1.0;
                        float Tb = hN - 0.333333; if (Tb < 0.0) Tb += 1.0; if (Tb > 1.0) Tb -= 1.0;
                        float h2r(float t) { return t < 0.166667 ? p + (qq - p) * 6.0 * t : t < 0.5 ? qq : t < 0.666667 ? p + (qq - p) * (0.666667 - t) * 6.0 : p; }
                        r = h2r(Tr) * {{maxV}}; g = h2r(Tg) * {{maxV}}; b = h2r(Tb) * {{maxV}};
                    }

                    // 5. Gamma
                    float invG = 1.0 / max({{gammaValue}}, 0.001);
                    r = {{maxV}} * pow(r / {{maxV}}, invG);
                    g = {{maxV}} * pow(g / {{maxV}}, invG);
                    b = {{maxV}} * pow(b / {{maxV}}, invG);

                    // 6. Vibrance
                    float vSat = 1.0 + {{vibranceValue}} * 0.5;
                    float vGray = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                    r = vGray + vSat * (r - vGray);
                    g = vGray + vSat * (g - vGray);
                    b = vGray + vSat * (b - vGray);

                    // 7. Temperature
                    r *= 1.0 + {{temperatureValue}} * 0.01;
                    b *= 1.0 - {{temperatureValue}} * 0.01;

                    // 8. Invert
                    if ({{invertF}} > 0.5) { r = {{maxV}} - r; g = {{maxV}} - g; b = {{maxV}} - b; }

                    // 9. Grayscale
                    float lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                    float gs = {{grayscaleValue}} >= 1.0 ? 1.0 : 1.0 - {{grayscaleValue}};
                    r = lum + gs * (r - lum); g = lum + gs * (g - lum); b = lum + gs * (b - lum);

                    // 10. Opacity
                    outputData[i] = r; outputData[i+{{srcLen}}] = g;
                    outputData[i+{{srcLen * 2}}] = b; outputData[i+{{srcLen * 3}}] = a * {{opacityValue}};
                }
                """;
            return AndroidExecutionHelper.EnqueueCompute(() =>
            {
                var mtTask = MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    NativeGLSurfaceView acc = new NativeGLSurfaceView
                    {
                        ShaderSource = shader,
                        Inputs = new float[][]
                        {
                            packed
                        },
                        WidthRequest = 50,
                        HeightRequest = 50,
                        JobID = "ColorAdjustmentEffect",
                        OutputElementType = GLComputeView.OutputElementType.Float32
                    };
                    using var accScope = AndroidExecutionHelper.UseView(acc);
                    var tcs = new TaskCompletionSource<NativeGLSurfaceViewHandler>(TaskCreationOptions.RunContinuationsAsynchronously);
                    void OnH(object? s, EventArgs e)
                    {
                        if (acc.Handler is NativeGLSurfaceViewHandler hh)
                        {
                            acc.HandlerChanged -= OnH;
                            tcs.TrySetResult(hh);
                        }
                    }

                    acc.HandlerChanged += OnH;
                    AndroidExecutionHelper.AddPlatformComputeViewHandler?.Invoke(acc);
                    if (acc.Handler is NativeGLSurfaceViewHandler eh)
                    {
                        acc.HandlerChanged -= OnH;
                        tcs.TrySetResult(eh);
                    }

                    if (await Task.WhenAny(tcs.Task, Task.Delay(10000)) != tcs.Task)
                        throw new TimeoutException("Handler timeout.");
                    var handler = await tcs.Task;
                    if (handler?.PlatformView is not GLComputeView glView)
                        throw new InvalidOperationException();
                    var rt = glView.WaitUntilReadyAsync();
                    if (await Task.WhenAny(rt, Task.Delay(AndroidExecutionHelper.Timeout)) != rt)
                        throw new TimeoutException("Timeout.");
                    await rt;
                    var raw = (float[])await glView.RunComputeAsync(GLComputeView.OutputElementType.Float32);
                    if (raw.Length < packedLen)
                    {
                        var tp = new float[packedLen];
                        Array.Copy(raw, 0, tp, 0, raw.Length);
                        raw = tp;
                    }

                    var rO = new float[srcLen];
                    var gO = new float[srcLen];
                    var bO = new float[srcLen];
                    var aO = new float[srcLen];
                    Array.Copy(raw, 0, rO, 0, srcLen);
                    Array.Copy(raw, srcLen, gO, 0, srcLen);
                    Array.Copy(raw, srcLen * 2, bO, 0, srcLen);
                    Array.Copy(raw, srcLen * 3, aO, 0, srcLen);
                    return new FourChannelResult(rO, gO, bO, aO);
                });
                if (!mtTask.Wait(TimeSpan.FromSeconds(60)))
                    throw new TimeoutException("ColorAdjustmentEffect timed out.");
                var result = TaskHelper.SyncWait(() => mtTask, CancellationToken.None);
                return result;
            });
        }

        private FourChannelResult VulkanComputeColorAdjustment(float[] r, float[] g, float[] b, float[] a, int w, int h, float brightness, float contrast, float saturation, float hue, float gamma, float vibrance, float temperature, bool invert, float grayscale, float opacity, float maxVal)
        {
            var rIn = r;
            var gIn = g;
            var bIn = b;
            var aIn = a;
            float brightnessValue = brightness, contrastValue = contrast;
            float saturationValue = saturation, hueValue = hue;
            float gammaValue = gamma, vibranceValue = vibrance;
            float temperatureValue = temperature, invertF = Convert.ToSingle((invert ? 1f : 0f));
            float grayscaleValue = grayscale, opacityValue = opacity;
            float maxV = maxVal;
            int srcLen = rIn.Length, packedLen = srcLen * 4;
            var packed = new float[packedLen];
            Array.Copy(rIn, 0, packed, 0, srcLen);
            Array.Copy(gIn, 0, packed, srcLen, srcLen);
            Array.Copy(bIn, 0, packed, srcLen * 2, srcLen);
            Array.Copy(aIn, 0, packed, srcLen * 3, srcLen);
            string shader = $$"""
                #version 450
                layout(local_size_x = 256) in;
                layout(set = 0, binding = 0, std430) buffer InBuf { float i[]; };
                layout(set = 0, binding = 1, std430) buffer OutBuf { float o[]; };
                void main() {
                    uint idx = gl_GlobalInvocationID.x;
                    if (idx >= {{srcLen}}) { o[idx]=0.0; o[idx+{{srcLen}}]=0.0; o[idx+{{srcLen * 2}}]=0.0; o[idx+{{srcLen * 3}}]=0.0; return; }
                    float r=i[idx], g=i[idx+{{srcLen}}], b=i[idx+{{srcLen * 2}}], a=i[idx+{{srcLen * 3}}];
                    float bf = {{brightnessValue}} - 1.0;
                    r = bf>=0.0 ? r+({{maxV}}-r)*bf : r*(1.0+bf);
                    g = bf>=0.0 ? g+({{maxV}}-g)*bf : g*(1.0+bf);
                    b = bf>=0.0 ? b+({{maxV}}-b)*bf : b*(1.0+bf);
                    r = ((r/{{maxV}}-0.5)*{{contrastValue}}+0.5)*{{maxV}};
                    g = ((g/{{maxV}}-0.5)*{{contrastValue}}+0.5)*{{maxV}};
                    b = ((b/{{maxV}}-0.5)*{{contrastValue}}+0.5)*{{maxV}};
                    float gray = 0.2126*r + 0.7152*g + 0.0722*b;
                    r = gray + {{saturationValue}}*(r-gray);
                    g = gray + {{saturationValue}}*(g-gray);
                    b = gray + {{saturationValue}}*(b-gray);
                    // Hue
                    {   float nr=r/{{maxV}}, ng=g/{{maxV}}, nb=b/{{maxV}};
                        float cmx=max(max(nr,ng),nb), cmn=min(min(nr,ng),nb), delta=cmx-cmn;
                        float hh=0.0, ss, ll;
                        if (delta>0.0) { if(cmx==nr) hh=60.0*mod((ng-nb)/delta,6.0); else if(cmx==ng) hh=60.0*((nb-nr)/delta+2.0); else hh=60.0*((nr-ng)/delta+4.0); if(hh<0.0) hh+=360.0; }
                        ll=(cmx+cmn)*0.5; ss=(ll>0.0&&ll<1.0)?delta/(1.0-abs(2.0*ll-1.0)):0.0;
                        hh+= {{hueValue}}; if(hh<0.0) hh+=360.0; if(hh>=360.0) hh-=360.0;
                        if(ss<0.000001) { r=ll*{{maxV}}; g=ll*{{maxV}}; b=ll*{{maxV}}; }
                        else { float qq=ll<0.5?ll*(1.0+ss):ll+ss-ll*ss; float p=2.0*ll-qq; float hN=hh/360.0;
                            float Tr=hN+0.333333; if(Tr<0.0)Tr+=1.0; if(Tr>1.0)Tr-=1.0;
                            float Tg=hN; if(Tg<0.0)Tg+=1.0; if(Tg>1.0)Tg-=1.0;
                            float Tb=hN-0.333333; if(Tb<0.0)Tb+=1.0; if(Tb>1.0)Tb-=1.0;
                            float h2r(float t) { return t<0.166667?p+(qq-p)*6.0*t:t<0.5?qq:t<0.666667?p+(qq-p)*(0.666667-t)*6.0:p; }
                            r=h2r(Tr)*{{maxV}}; g=h2r(Tg)*{{maxV}}; b=h2r(Tb)*{{maxV}}; }
                    }
                    float invG=1.0/max({{gammaValue}},0.001);
                    r={{maxV}}*pow(r/{{maxV}},invG); g={{maxV}}*pow(g/{{maxV}},invG); b={{maxV}}*pow(b/{{maxV}},invG);
                    float vSat=1.0+{{vibranceValue}}*0.5, vGray=0.2126*r+0.7152*g+0.0722*b;
                    r=vGray+vSat*(r-vGray); g=vGray+vSat*(g-vGray); b=vGray+vSat*(b-vGray);
                    r*=1.0+{{temperatureValue}}*0.01; b*=1.0-{{temperatureValue}}*0.01;
                    if({{invertF}}>0.5) { r={{maxV}}-r; g={{maxV}}-g; b={{maxV}}-b; }
                    float lum=0.2126*r+0.7152*g+0.0722*b, gs={{grayscaleValue}}>=1.0?1.0:1.0-{{grayscaleValue}};
                    o[idx]=lum+gs*(r-lum); o[idx+{{srcLen}}]=lum+gs*(g-lum);
                    o[idx+{{srcLen * 2}}]=lum+gs*(b-lum); o[idx+{{srcLen * 3}}]=a*{{opacityValue}};
                }
                """;
            return VulkanExecutionHelper.EnqueueCompute(async () =>
            {
                var(acc, handler, vkView) = await VulkanExecutionHelper.CreateAcceleratorAsync(shader, new float[][] { packed }, OutputElementType.Float32);
                using var accScope = AndroidExecutionHelper.UseView(acc);
                var raw = (float[])await vkView.RunComputeAsync(OutputElementType.Float32);
                if (raw.Length < packedLen)
                {
                    var tp = new float[packedLen];
                    Array.Copy(raw, 0, tp, 0, raw.Length);
                    raw = tp;
                }

                var rO = new float[srcLen];
                var gO = new float[srcLen];
                var bO = new float[srcLen];
                var aO = new float[srcLen];
                Array.Copy(raw, 0, rO, 0, srcLen);
                Array.Copy(raw, srcLen, gO, 0, srcLen);
                Array.Copy(raw, srcLen * 2, bO, 0, srcLen);
                Array.Copy(raw, srcLen * 3, aO, 0, srcLen);
                return new FourChannelResult(rO, gO, bO, aO);
            }, "VulkanColorAdjustmentEffect timed out.");
        }
    }
}
#endif
