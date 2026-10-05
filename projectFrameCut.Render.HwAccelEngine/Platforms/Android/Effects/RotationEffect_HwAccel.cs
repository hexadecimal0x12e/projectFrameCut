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
    public partial class RotationEffect_HwAccel
    {
        private readonly ComputeBackend backend = AndroidExecutionHelper.PreferredBackend;
        private FourChannelResult ComputeRotation(float[] r, float[] g, float[] b, float[] a, int srcW, int srcH, int dstW, int dstH, float angleDeg) => backend == ComputeBackend.Vulkan ? VulkanComputeRotation(r, g, b, a, srcW, srcH, dstW, dstH, angleDeg) : OpenGLComputeRotation(r, g, b, a, srcW, srcH, dstW, dstH, angleDeg);
        private FourChannelResult OpenGLComputeRotation(float[] r, float[] g, float[] b, float[] a, int srcW, int srcH, int dstW, int dstH, float angleDeg)
        {
            int srcWValue = srcW;
            int srcHValue = srcH;
            int outW = dstW;
            int outH = dstH;
            float angleDegValue = angleDeg;
            int srcLength = checked(srcWValue * srcHValue);
            int dstLength = checked(outW * outH);
            // This one needs different src/dst sizes, so we handle it manually
            var rIn = r;
            var gIn = g;
            var bIn = b;
            var aIn = a;
            int glPad = Math.Max(srcLength, dstLength);
            float[] PadChannel(float[] ch)
            {
                var arr = new float[glPad];
                Array.Copy(ch, 0, arr, 0, Math.Min(ch.Length, srcLength));
                return arr;
            }

            var rPad = PadChannel(rIn);
            var gPad = PadChannel(gIn);
            var bPad = PadChannel(bIn);
            var aPad = PadChannel(aIn);
            string shader = $$"""
                #version 310 es
                layout(local_size_x = 256) in;
                layout(std430, binding = 0) buffer InBuffer { float inputData[]; };
                layout(std430, binding = 6) buffer OutBuffer { float outputData[]; };
                void main()
                {
                    uint i = gl_GlobalInvocationID.x;
                    if (i >= uint({{dstLength}})) { return; }
                    int x = int(i % uint({{outW}}));
                    int y = int(i / uint({{outW}}));
                    float angleRad = {{(angleDegValue * MathF.PI / 180f).ToString("R", System.Globalization.CultureInfo.InvariantCulture)}};
                    float cosA = cos(angleRad);
                    float sinA = sin(angleRad);
                    float srcCx = float({{srcWValue}}) * 0.5;
                    float srcCy = float({{srcHValue}}) * 0.5;
                    float outCx = float({{outW}}) * 0.5;
                    float outCy = float({{outH}}) * 0.5;
                    float ox = float(x) - outCx;
                    float oy = float(y) - outCy;
                    float sx = cosA * ox - sinA * oy + srcCx;
                    float sy = sinA * ox + cosA * oy + srcCy;
                    if (sx >= 0.0 && sx < float({{srcWValue}}) && sy >= 0.0 && sy < float({{srcHValue}}))
                    {
                        int sx0 = int(sx); int sy0 = int(sy);
                        int sx1 = min(sx0 + 1, {{srcWValue}} - 1); int sy1 = min(sy0 + 1, {{srcHValue}} - 1);
                        float fx = sx - float(sx0); float fy = sy - float(sy0);
                        int i00 = sy0 * {{srcWValue}} + sx0; int i10 = sy0 * {{srcWValue}} + sx1;
                        int i01 = sy1 * {{srcWValue}} + sx0; int i11 = sy1 * {{srcWValue}} + sx1;
                        outputData[i] =
                            ((inputData[i00] * (1.0 - fx) + inputData[i10] * fx) * (1.0 - fy) +
                             (inputData[i01] * (1.0 - fx) + inputData[i11] * fx) * fy);
                    }
                    else { outputData[i] = 0.0; }
                }
                """;
            return AndroidExecutionHelper.EnqueueCompute(() =>
            {
                var mainThreadTask = MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    NativeGLSurfaceView accelerator = new NativeGLSurfaceView
                    {
                        ShaderSource = shader,
                        Inputs = new float[][]
                        {
                            rPad
                        },
                        WidthRequest = 50,
                        HeightRequest = 50,
                        JobID = "RotationEffect",
                        OutputElementType = GLComputeView.OutputElementType.Float32
                    };
                    using var viewScope = AndroidExecutionHelper.UseView(accelerator);
                    var handlerReadyTcs = new TaskCompletionSource<NativeGLSurfaceViewHandler>(TaskCreationOptions.RunContinuationsAsynchronously);
                    void OnHandlerChanged(object? sender, EventArgs e)
                    {
                        if (accelerator.Handler is NativeGLSurfaceViewHandler handler)
                        {
                            accelerator.HandlerChanged -= OnHandlerChanged;
                            handlerReadyTcs.TrySetResult(handler);
                        }
                    }

                    accelerator.HandlerChanged += OnHandlerChanged;
                    AndroidExecutionHelper.AddPlatformComputeViewHandler?.Invoke(accelerator);
                    if (accelerator.Handler is NativeGLSurfaceViewHandler existingHandler)
                    {
                        accelerator.HandlerChanged -= OnHandlerChanged;
                        handlerReadyTcs.TrySetResult(existingHandler);
                    }

                    var hTask = handlerReadyTcs.Task;
                    if (await Task.WhenAny(hTask, Task.Delay(TimeSpan.FromSeconds(10))) != hTask)
                    {
                        accelerator.HandlerChanged -= OnHandlerChanged;
                        throw new TimeoutException("Handler creation timed out.");
                    }

                    var handler = await hTask;
                    if (handler?.PlatformView is not GLComputeView glView)
                        throw new InvalidOperationException("Accelerator is not ready.");
                    var readyTask = glView.WaitUntilReadyAsync();
                    if (await Task.WhenAny(readyTask, Task.Delay(TimeSpan.FromMilliseconds(AndroidExecutionHelper.Timeout))) != readyTask)
                        throw new TimeoutException("GLComputeView timed out.");
                    await readyTask;
                    async Task<float[]> RunCh(float[] ch, string id)
                    {
                        accelerator.JobID = id;
                        accelerator.Inputs = new float[][]
                        {
                            ch
                        };
                        NativeGLSurfaceViewHandler.MapInputs(handler, accelerator);
                        var raw = (float[])await glView.RunComputeAsync(GLComputeView.OutputElementType.Float32);
                        if (raw.Length == dstLength)
                            return raw;
                        var t = new float[dstLength];
                        Array.Copy(raw, 0, t, 0, Math.Min(raw.Length, dstLength));
                        return t;
                    }

                    var rO = await RunCh(rPad, "RotationEffect-R");
                    var gO = await RunCh(gPad, "RotationEffect-G");
                    var bO = await RunCh(bPad, "RotationEffect-B");
                    var aO = await RunCh(aPad, "RotationEffect-A");
                    return new FourChannelResult(rO, gO, bO, aO);
                });
                if (!mainThreadTask.Wait(TimeSpan.FromSeconds(60)))
                    throw new TimeoutException("RotationEffect timed out.");
                var result = TaskHelper.SyncWait(() => mainThreadTask, CancellationToken.None);
                return result;
            });
        }

        private FourChannelResult VulkanComputeRotation(float[] r, float[] g, float[] b, float[] a, int srcW, int srcH, int dstW, int dstH, float angleDeg)
        {
            var rIn = r;
            var gIn = g;
            var bIn = b;
            var aIn = a;
            int srcWValue = srcW, srcHValue = srcH;
            int outW = dstW, outH = dstH;
            float angleDegValue = angleDeg;
            int srcLen = checked(srcWValue * srcHValue), dstLen = checked(outW * outH);
            int vkLen = Math.Max(srcLen, dstLen);
            float[] Pad(float[] s)
            {
                var a = new float[vkLen];
                Array.Copy(s, 0, a, 0, Math.Min(s.Length, srcLen));
                return a;
            }

            var rP = Pad(rIn);
            var gP = Pad(gIn);
            var bP = Pad(bIn);
            var aP = Pad(aIn);
            string shader = $$"""
                #version 450
                layout(local_size_x = 256) in;
                layout(set = 0, binding = 0, std430) buffer InBuf { float i[]; };
                layout(set = 0, binding = 1, std430) buffer OutBuf { float o[]; };
                void main() {
                    uint idx = gl_GlobalInvocationID.x;
                    if (idx >= {{dstLen}}) { return; }
                    int x = int(idx % {{outW}}), y = int(idx / {{outW}});
                    float ar = {{(angleDegValue * MathF.PI / 180f).ToString("R", System.Globalization.CultureInfo.InvariantCulture)}};
                    float cosA = cos(ar), sinA = sin(ar);
                    float scx = float({{srcWValue}}) * 0.5, scy = float({{srcHValue}}) * 0.5;
                    float ocx = float({{outW}}) * 0.5, ocy = float({{outH}}) * 0.5;
                    float ox = float(x) - ocx, oy = float(y) - ocy;
                    float sx = cosA * ox - sinA * oy + scx;
                    float sy = sinA * ox + cosA * oy + scy;
                    if (sx >= 0.0 && sx < float({{srcWValue}}) && sy >= 0.0 && sy < float({{srcHValue}})) {
                        int sx0 = int(sx), sy0 = int(sy), sx1 = min(sx0+1, {{srcWValue}}-1), sy1 = min(sy0+1, {{srcHValue}}-1);
                        float fx = sx - float(sx0), fy = sy - float(sy0);
                        int i00 = sy0*{{srcWValue}}+sx0, i10 = sy0*{{srcWValue}}+sx1;
                        int i01 = sy1*{{srcWValue}}+sx0, i11 = sy1*{{srcWValue}}+sx1;
                        o[idx] = ((i[i00]*(1.0-fx)+i[i10]*fx)*(1.0-fy)+(i[i01]*(1.0-fx)+i[i11]*fx)*fy);
                    } else { o[idx] = 0.0; }
                }
                """;
            return VulkanExecutionHelper.EnqueueCompute(async () =>
            {
                var(acc, handler, vkView) = await VulkanExecutionHelper.CreateAcceleratorAsync(shader, new float[][] { rP }, OutputElementType.Float32);
                using var accScope = AndroidExecutionHelper.UseView(acc);
                async Task<float[]> RunCh(float[] ch)
                {
                    acc.Inputs = new float[][]
                    {
                        ch
                    };
                    NativeVulkanSurfaceViewHandler.MapInputs(handler, acc);
                    var raw = (float[])await vkView.RunComputeAsync(OutputElementType.Float32);
                    if (raw.Length == dstLen)
                        return raw;
                    var t = new float[dstLen];
                    Array.Copy(raw, 0, t, 0, Math.Min(raw.Length, dstLen));
                    return t;
                }

                return new FourChannelResult(await RunCh(rP), await RunCh(gP), await RunCh(bP), await RunCh(aP));
            }, "VulkanRotationEffect timed out.");
        }
    }
}
#endif
