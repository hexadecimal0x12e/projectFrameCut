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
    public partial class BlurEffect_HwAccel
    {
        private readonly ComputeBackend backend = AndroidExecutionHelper.PreferredBackend;
        private FourChannelResult ComputeBlur(float[] r, float[] g, float[] b, float[] a, int w, float sigma) => backend == ComputeBackend.Vulkan ? VulkanComputeBlur(r, g, b, a, w, sigma) : OpenGLComputeBlur(r, g, b, a, w, sigma);
        private FourChannelResult OpenGLComputeBlur(float[] r, float[] g, float[] b, float[] a, int w, float sigma)
        {
            var rIn = r;
            var gIn = g;
            var bIn = b;
            var aIn = a;
            int wValue = w;
            float sigmaValue = sigma;
            int radius = (int)MathF.Ceiling(sigmaValue);
            if (radius <= 0)
                radius = 1;
            int len = rIn.Length;
            int h = len / wValue;
            int glPad = len;
            float[] Pad(float[] src)
            {
                var a = new float[glPad];
                Array.Copy(src, 0, a, 0, Math.Min(src.Length, glPad));
                return a;
            }

            var rP = Pad(rIn);
            var gP = Pad(gIn);
            var bP = Pad(bIn);
            var aP = Pad(aIn);
            string horizShader = $$"""
                #version 310 es
                layout(local_size_x = 256) in;
                layout(std430, binding = 0) buffer InBuffer { float inputData[]; };
                layout(std430, binding = 6) buffer OutBuffer { float outputData[]; };
                void main()
                {
                    uint i = gl_GlobalInvocationID.x;
                    if (i >= uint({{len}})) { return; }
                    int x = int(i % uint({{wValue}}));
                    int rowStart = int(i) - x;
                    float sum = 0.0; int count = 0;
                    for (int k = x - {{radius}}; k <= x + {{radius}}; k++)
                    {
                        if (k < 0 || k >= {{wValue}}) continue;
                        int col = k;
                        sum += inputData[rowStart + col]; count++;
                    }
                    outputData[i] = sum / float(count);
                }
                """;
            string vertShader = $$"""
                #version 310 es
                layout(local_size_x = 256) in;
                layout(std430, binding = 0) buffer InBuffer { float inputData[]; };
                layout(std430, binding = 6) buffer OutBuffer { float outputData[]; };
                void main()
                {
                    uint i = gl_GlobalInvocationID.x;
                    if (i >= uint({{len}})) { return; }
                    int y = int(i / uint({{wValue}}));
                    float sum = 0.0; int count = 0;
                    for (int k = y - {{radius}}; k <= y + {{radius}}; k++)
                    {
                        if (k < 0 || k >= {{h}}) continue;
                        int row = k;
                        sum += inputData[row * {{wValue}} + int(i % uint({{wValue}}))]; count++;
                    }
                    outputData[i] = sum / float(count);
                }
                """;
            return AndroidExecutionHelper.EnqueueCompute(() =>
            {
                var mtTask = MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    NativeGLSurfaceView accH = new NativeGLSurfaceView
                    {
                        ShaderSource = horizShader,
                        Inputs = new float[][]
                        {
                            rP
                        },
                        WidthRequest = 50,
                        HeightRequest = 50,
                        JobID = "Blur-H",
                        OutputElementType = GLComputeView.OutputElementType.Float32
                    };
                    using var accHScope = AndroidExecutionHelper.UseView(accH);
                    var tcsH = new TaskCompletionSource<NativeGLSurfaceViewHandler>(TaskCreationOptions.RunContinuationsAsynchronously);
                    void OnH(object? s, EventArgs e)
                    {
                        if (accH.Handler is NativeGLSurfaceViewHandler hh)
                        {
                            accH.HandlerChanged -= OnH;
                            tcsH.TrySetResult(hh);
                        }
                    }

                    accH.HandlerChanged += OnH;
                    AndroidExecutionHelper.AddPlatformComputeViewHandler?.Invoke(accH);
                    if (accH.Handler is NativeGLSurfaceViewHandler eh)
                    {
                        accH.HandlerChanged -= OnH;
                        tcsH.TrySetResult(eh);
                    }

                    if (await Task.WhenAny(tcsH.Task, Task.Delay(10000)) != tcsH.Task)
                        throw new TimeoutException();
                    var hH = await tcsH.Task;
                    if (hH?.PlatformView is not GLComputeView gvH)
                        throw new InvalidOperationException();
                    var rtH = gvH.WaitUntilReadyAsync();
                    if (await Task.WhenAny(rtH, Task.Delay(AndroidExecutionHelper.Timeout)) != rtH)
                        throw new TimeoutException();
                    await rtH;
                    async Task<float[]> RunH(float[] ch, NativeGLSurfaceViewHandler handler)
                    {
                        accH.JobID = "Blur-H";
                        accH.Inputs = new float[][]
                        {
                            ch
                        };
                        NativeGLSurfaceViewHandler.MapInputs(handler, accH);
                        var raw = (float[])await gvH.RunComputeAsync(GLComputeView.OutputElementType.Float32);
                        if (raw.Length == len)
                            return raw;
                        var t = new float[len];
                        Array.Copy(raw, 0, t, 0, Math.Min(raw.Length, len));
                        return t;
                    }

                    var rT = await RunH(rP, hH);
                    var gT = await RunH(gP, hH);
                    var bT = await RunH(bP, hH);
                    var aT = await RunH(aP, hH);
                    NativeGLSurfaceView accV = new NativeGLSurfaceView
                    {
                        ShaderSource = vertShader,
                        Inputs = new float[][]
                        {
                            rT
                        },
                        WidthRequest = 50,
                        HeightRequest = 50,
                        JobID = "Blur-V",
                        OutputElementType = GLComputeView.OutputElementType.Float32
                    };
                    using var accVScope = AndroidExecutionHelper.UseView(accV);
                    var tcsV = new TaskCompletionSource<NativeGLSurfaceViewHandler>(TaskCreationOptions.RunContinuationsAsynchronously);
                    void OnV(object? s, EventArgs e)
                    {
                        if (accV.Handler is NativeGLSurfaceViewHandler hv)
                        {
                            accV.HandlerChanged -= OnV;
                            tcsV.TrySetResult(hv);
                        }
                    }

                    accV.HandlerChanged += OnV;
                    AndroidExecutionHelper.AddPlatformComputeViewHandler?.Invoke(accV);
                    if (accV.Handler is NativeGLSurfaceViewHandler ev)
                    {
                        accV.HandlerChanged -= OnV;
                        tcsV.TrySetResult(ev);
                    }

                    if (await Task.WhenAny(tcsV.Task, Task.Delay(10000)) != tcsV.Task)
                        throw new TimeoutException();
                    var hV = await tcsV.Task;
                    if (hV?.PlatformView is not GLComputeView gvV)
                        throw new InvalidOperationException();
                    var rtV = gvV.WaitUntilReadyAsync();
                    if (await Task.WhenAny(rtV, Task.Delay(AndroidExecutionHelper.Timeout)) != rtV)
                        throw new TimeoutException();
                    await rtV;
                    async Task<float[]> RunV(float[] ch)
                    {
                        accV.JobID = "Blur-V";
                        accV.Inputs = new float[][]
                        {
                            ch
                        };
                        NativeGLSurfaceViewHandler.MapInputs(hV, accV);
                        var raw = (float[])await gvV.RunComputeAsync(GLComputeView.OutputElementType.Float32);
                        if (raw.Length == len)
                            return raw;
                        var t = new float[len];
                        Array.Copy(raw, 0, t, 0, Math.Min(raw.Length, len));
                        return t;
                    }

                    return new FourChannelResult(await RunV(rT), await RunV(gT), await RunV(bT), await RunV(aT));
                });
                if (!mtTask.Wait(TimeSpan.FromSeconds(60)))
                    throw new TimeoutException("BlurEffect timed out.");
                return TaskHelper.SyncWait(() => mtTask, CancellationToken.None);
            });
        }

        private FourChannelResult VulkanComputeBlur(float[] r, float[] g, float[] b, float[] a, int w, float sigma)
        {
            var rIn = r;
            var gIn = g;
            var bIn = b;
            var aIn = a;
            int wValue = w, len = rIn.Length, h = len / wValue;
            int radius = Math.Max(1, (int)MathF.Ceiling(sigma));
            string hShader = $$"""
                #version 450
                layout(local_size_x = 256) in;
                layout(set = 0, binding = 0, std430) buffer InBuf { float i[]; };
                layout(set = 0, binding = 1, std430) buffer OutBuf { float o[]; };
                void main() {
                    uint idx = gl_GlobalInvocationID.x;
                    if (idx >= {{len}}) { return; }
                    int x = int(idx % {{wValue}}), rs = int(idx) - x;
                    float sum = 0.0; int cnt = 0;
                    for (int k = x - {{radius}}; k <= x + {{radius}}; k++) {
                        if (k < 0 || k >= {{wValue}}) continue;
                        int col = k;
                        sum += i[rs + col]; cnt++;
                    }
                    o[idx] = sum / float(cnt);
                }
                """;
            string vShader = $$"""
                #version 450
                layout(local_size_x = 256) in;
                layout(set = 0, binding = 0, std430) buffer InBuf { float i[]; };
                layout(set = 0, binding = 1, std430) buffer OutBuf { float o[]; };
                void main() {
                    uint idx = gl_GlobalInvocationID.x;
                    if (idx >= {{len}}) { return; }
                    int y = int(idx / {{wValue}});
                    float sum = 0.0; int cnt = 0;
                    for (int k = y - {{radius}}; k <= y + {{radius}}; k++) {
                        if (k < 0 || k >= {{h}}) continue;
                        int row = k;
                        sum += i[row * {{wValue}} + int(idx % {{wValue}})]; cnt++;
                    }
                    o[idx] = sum / float(cnt);
                }
                """;
            return VulkanExecutionHelper.EnqueueCompute(async () =>
            {
                var(acc, handler, vkView) = await VulkanExecutionHelper.CreateAcceleratorAsync(hShader, new float[][] { rIn }, OutputElementType.Float32);
                using var accScope = AndroidExecutionHelper.UseView(acc);
                async Task<float[]> RunH(float[] ch)
                {
                    acc.Inputs = new float[][]
                    {
                        ch
                    };
                    NativeVulkanSurfaceViewHandler.MapInputs(handler, acc);
                    return (float[])await vkView.RunComputeAsync(OutputElementType.Float32);
                }

                var rT = await RunH(rIn);
                var gT = await RunH(gIn);
                var bT = await RunH(bIn);
                var aT = await RunH(aIn);
                var(acc2, handler2, vkView2) = await VulkanExecutionHelper.CreateAcceleratorAsync(vShader, new float[][] { rT }, OutputElementType.Float32);
                using var acc2Scope = AndroidExecutionHelper.UseView(acc2);
                async Task<float[]> RunV(float[] ch)
                {
                    acc2.Inputs = new float[][]
                    {
                        ch
                    };
                    NativeVulkanSurfaceViewHandler.MapInputs(handler2, acc2);
                    return (float[])await vkView2.RunComputeAsync(OutputElementType.Float32);
                }

                return new FourChannelResult(await RunV(rT), await RunV(gT), await RunV(bT), await RunV(aT));
            }, "VulkanBlurEffect timed out.");
        }
    }
}
#endif
