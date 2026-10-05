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
    public partial class ResizeEffect_HwAccel
    {
        private readonly ComputeBackend backend = AndroidExecutionHelper.PreferredBackend;
        private FourChannelResult ComputeResizeFloat(float[] r, float[] g, float[] b, float[] a, float srcW, float srcH, float dstW, float dstH) => backend == ComputeBackend.Vulkan ? VulkanComputeResizeFloat(r, g, b, a, srcW, srcH, dstW, dstH) : OpenGLComputeResizeFloat(r, g, b, a, srcW, srcH, dstW, dstH);
        private FourChannelResult8 ComputeResizeByte(float[] r, float[] g, float[] b, float[] a, float srcW, float srcH, float dstW, float dstH)
        {
            var result = ComputeResizeFloat(r, g, b, a, srcW, srcH, dstW, dstH);
            return new FourChannelResult8(result.R.Select(v => (byte)Math.Clamp(v + 0.5f, 0, 255)).ToArray(), result.G.Select(v => (byte)Math.Clamp(v + 0.5f, 0, 255)).ToArray(), result.B.Select(v => (byte)Math.Clamp(v + 0.5f, 0, 255)).ToArray(), result.A);
        }

        private FourChannelResult16 ComputeResizeUshort(float[] r, float[] g, float[] b, float[] a, float srcW, float srcH, float dstW, float dstH)
        {
            var result = ComputeResizeFloat(r, g, b, a, srcW, srcH, dstW, dstH);
            return new FourChannelResult16(result.R.Select(v => (ushort)Math.Clamp(v + 0.5f, 0, 65535)).ToArray(), result.G.Select(v => (ushort)Math.Clamp(v + 0.5f, 0, 65535)).ToArray(), result.B.Select(v => (ushort)Math.Clamp(v + 0.5f, 0, 65535)).ToArray(), result.A);
        }

        private FourChannelResult OpenGLComputeResizeFloat(float[] r, float[] g, float[] b, float[] a, float srcW, float srcH, float dstW, float dstH)
        {
            var rIn = r;
            var gIn = g;
            var bIn = b;
            var aIn = a;
            int srcWValue = Convert.ToInt32((float)(srcW));
            int srcHValue = Convert.ToInt32((float)(srcH));
            int dstWValue = Convert.ToInt32((float)(dstW));
            int dstHValue = Convert.ToInt32((float)(dstH));
            if (srcWValue <= 0 || srcHValue <= 0 || dstWValue <= 0 || dstHValue <= 0)
            {
                return new FourChannelResult([], [], [], []);
            }

            int srcLength = checked(srcWValue * srcHValue);
            int dstLength = checked(dstWValue * dstHValue);
            int glLength = Math.Max(srcLength, dstLength);
            float[] rPad = OpenGLPreparePaddedChannel(rIn, srcLength, glLength);
            float[] gPad = OpenGLPreparePaddedChannel(gIn, srcLength, glLength);
            float[] bPad = OpenGLPreparePaddedChannel(bIn, srcLength, glLength);
            float[] aPad = OpenGLPreparePaddedChannel(aIn, srcLength, glLength);
            float ratioX = (float)srcWValue / dstWValue;
            float ratioY = (float)srcHValue / dstHValue;
            string shader = $$"""
                #version 310 es
                layout(local_size_x = 256) in;

                layout(std430, binding = 0) buffer InBuffer { float inputData[]; };
                layout(std430, binding = 6) buffer OutBuffer { float outputData[]; };

                void main()
                {
                    uint i = gl_GlobalInvocationID.x;
                    if (i >= uint({{dstLength}}))
                    {
                        return;
                    }

                    int x = int(i % uint({{dstWValue}}));
                    int y = int(i / uint({{dstWValue}}));

                    float sx = clamp((float(x) + 0.5) * {{ratioX.ToString(System.Globalization.CultureInfo.InvariantCulture)}} - 0.5, 0.0, float({{srcWValue - 1}}));
                    float sy = clamp((float(y) + 0.5) * {{ratioY.ToString(System.Globalization.CultureInfo.InvariantCulture)}} - 0.5, 0.0, float({{srcHValue - 1}}));
                    int x0 = int(sx), y0 = int(sy), x1 = min(x0 + 1, {{srcWValue - 1}}), y1 = min(y0 + 1, {{srcHValue - 1}});
                    float fx = sx - float(x0), fy = sy - float(y0);
                    int i00 = y0 * {{srcWValue}} + x0, i10 = y0 * {{srcWValue}} + x1, i01 = y1 * {{srcWValue}} + x0, i11 = y1 * {{srcWValue}} + x1;
                    outputData[i] = inputData[i00]*(1.0-fx)*(1.0-fy) + inputData[i10]*fx*(1.0-fy) + inputData[i01]*(1.0-fx)*fy + inputData[i11]*fx*fy;
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
                        JobID = "ResizeEffect",
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

                    var handlerWaitTask = handlerReadyTcs.Task;
                    if (await Task.WhenAny(handlerWaitTask, Task.Delay(TimeSpan.FromSeconds(10))) != handlerWaitTask)
                    {
                        accelerator.HandlerChanged -= OnHandlerChanged;
                        throw new TimeoutException("Handler creation timed out after 10 seconds.");
                    }

                    var handler = await handlerWaitTask;
                    if (handler?.PlatformView is not GLComputeView glView)
                        throw new InvalidOperationException("Accelerator is not ready or not attached.");
                    var readyTask = glView.WaitUntilReadyAsync();
                    if (await Task.WhenAny(readyTask, Task.Delay(TimeSpan.FromMilliseconds(AndroidExecutionHelper.Timeout))) != readyTask)
                        throw new TimeoutException($"GLComputeView.WaitUntilReadyAsync timed out after {AndroidExecutionHelper.Timeout}ms.");
                    await readyTask;
                    async Task<float[]> RunChannel(float[] channel, string jobId)
                    {
                        accelerator.JobID = jobId;
                        accelerator.Inputs = new float[][]
                        {
                            channel
                        };
                        NativeGLSurfaceViewHandler.MapInputs(handler, accelerator);
                        var raw = (float[])await glView.RunComputeAsync(GLComputeView.OutputElementType.Float32);
                        if (raw.Length == dstLength)
                        {
                            return raw;
                        }

                        var trimmed = new float[dstLength];
                        Array.Copy(raw, 0, trimmed, 0, Math.Min(raw.Length, dstLength));
                        return trimmed;
                    }

                    var rOut = await RunChannel(rPad, "ResizeEffect-R");
                    var gOut = await RunChannel(gPad, "ResizeEffect-G");
                    var bOut = await RunChannel(bPad, "ResizeEffect-B");
                    var aOut = await RunChannel(aPad, "ResizeEffect-A");
                    return new FourChannelResult(rOut, gOut, bOut, aOut);
                });
                if (!mainThreadTask.Wait(TimeSpan.FromSeconds(60)))
                {
                    throw new TimeoutException("ResizeEffect timed out after 60 seconds - likely deadlock due to main thread congestion.");
                }

                var result = TaskHelper.SyncWait(() => mainThreadTask, CancellationToken.None);
                return result;
            });
        }

        private static float[] OpenGLPreparePaddedChannel(float[] source, int sourceLength, int paddedLength)
        {
            var arr = new float[paddedLength];
            Array.Copy(source, 0, arr, 0, Math.Min(Math.Min(source.Length, sourceLength), paddedLength));
            return arr;
        }

        private FourChannelResult VulkanComputeResizeFloat(float[] r, float[] g, float[] b, float[] a, float srcW, float srcH, float dstW, float dstH)
        {
            var rIn = r;
            var gIn = g;
            var bIn = b;
            var aIn = a;
            int srcWValue = Convert.ToInt32((float)(srcW));
            int srcHValue = Convert.ToInt32((float)(srcH));
            int dstWValue = Convert.ToInt32((float)(dstW));
            int dstHValue = Convert.ToInt32((float)(dstH));
            if (srcWValue <= 0 || srcHValue <= 0 || dstWValue <= 0 || dstHValue <= 0)
            {
                return new FourChannelResult([], [], [], []);
            }

            int srcLength = checked(srcWValue * srcHValue);
            int dstLength = checked(dstWValue * dstHValue);
            int vkLength = Math.Max(srcLength, dstLength);
            float[] rPad = VulkanPreparePaddedChannel(rIn, srcLength, vkLength);
            float[] gPad = VulkanPreparePaddedChannel(gIn, srcLength, vkLength);
            float[] bPad = VulkanPreparePaddedChannel(bIn, srcLength, vkLength);
            float[] aPad = VulkanPreparePaddedChannel(aIn, srcLength, vkLength);
            float ratioX = (float)srcWValue / dstWValue;
            float ratioY = (float)srcHValue / dstHValue;
            string shader = VulkanBuildResizeShader(dstLength, dstWValue, srcWValue, srcHValue, ratioX, ratioY);
            return VulkanExecutionHelper.EnqueueCompute(async () =>
            {
                var(accelerator, handler, vkView) = await VulkanExecutionHelper.CreateAcceleratorAsync(shader, new float[][] { rPad }, OutputElementType.Float32);
                using var viewScope = AndroidExecutionHelper.UseView(accelerator);
                async Task<float[]> RunChannel(float[] channel)
                {
                    accelerator.Inputs = new float[][]
                    {
                        channel
                    };
                    NativeVulkanSurfaceViewHandler.MapInputs(handler, accelerator);
                    var raw = (float[])await vkView.RunComputeAsync(OutputElementType.Float32);
                    if (raw.Length == dstLength)
                    {
                        return raw;
                    }

                    var trimmed = new float[dstLength];
                    Array.Copy(raw, 0, trimmed, 0, Math.Min(raw.Length, dstLength));
                    return trimmed;
                }

                var rOut = await RunChannel(rPad);
                var gOut = await RunChannel(gPad);
                var bOut = await RunChannel(bPad);
                var aOut = await RunChannel(aPad);
                return new FourChannelResult(rOut, gOut, bOut, aOut);
            }, "VulkanResizeEffect timed out after 60 seconds - likely deadlock due to main thread congestion.");
        }

        private static float[] VulkanPreparePaddedChannel(float[] source, int sourceLength, int paddedLength)
        {
            var arr = new float[paddedLength];
            Array.Copy(source, 0, arr, 0, Math.Min(Math.Min(source.Length, sourceLength), paddedLength));
            return arr;
        }

        private static string VulkanBuildResizeShader(int dstLength, int dstW, int srcW, int srcH, float ratioX, float ratioY)
        {
            return $$"""
                #version 450
                layout(local_size_x = 256) in;

                layout(set = 0, binding = 0, std430) buffer InBuffer { float inputData[]; };
                layout(set = 0, binding = 1, std430) buffer OutBuffer { float outputData[]; };

                void main()
                {
                    uint i = gl_GlobalInvocationID.x;
                    if (i >= uint({{dstLength}}))
                    {
                        return;
                    }

                    int x = int(i % uint({{dstW}}));
                    int y = int(i / uint({{dstW}}));

                    float sx = clamp((float(x) + 0.5) * {{ratioX.ToString(System.Globalization.CultureInfo.InvariantCulture)}} - 0.5, 0.0, float({{srcW - 1}}));
                    float sy = clamp((float(y) + 0.5) * {{ratioY.ToString(System.Globalization.CultureInfo.InvariantCulture)}} - 0.5, 0.0, float({{srcH - 1}}));
                    int x0 = int(sx), y0 = int(sy), x1 = min(x0 + 1, {{srcW - 1}}), y1 = min(y0 + 1, {{srcH - 1}});
                    float fx = sx - float(x0), fy = sy - float(y0);
                    int i00 = y0 * {{srcW}} + x0, i10 = y0 * {{srcW}} + x1, i01 = y1 * {{srcW}} + x0, i11 = y1 * {{srcW}} + x1;
                    outputData[i] = inputData[i00]*(1.0-fx)*(1.0-fy) + inputData[i10]*fx*(1.0-fy) + inputData[i01]*(1.0-fx)*fy + inputData[i11]*fx*fy;
                }
                """;
        }
    }
}
#endif
