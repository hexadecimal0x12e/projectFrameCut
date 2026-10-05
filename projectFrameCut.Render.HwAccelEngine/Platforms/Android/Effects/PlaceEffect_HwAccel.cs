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
    public partial class PlaceEffect_HwAccel
    {
        private readonly ComputeBackend backend = AndroidExecutionHelper.PreferredBackend;
        private FourChannelResult ComputePlace(float[] r, float[] g, float[] b, float[] a, int srcW, int srcH, int startX, int startY, int targetW, int targetH) => backend == ComputeBackend.Vulkan ? VulkanComputePlace(r, g, b, a, srcW, srcH, startX, startY, targetW, targetH) : OpenGLComputePlace(r, g, b, a, srcW, srcH, startX, startY, targetW, targetH);
        private FourChannelResult OpenGLComputePlace(float[] r, float[] g, float[] b, float[] a, int srcW, int srcH, int startX, int startY, int targetW, int targetH)
        {
            var rIn = r;
            var gIn = g;
            var bIn = b;
            var aIn = a;
            int srcWValue = srcW;
            int srcHValue = srcH;
            int startXValue = startX;
            int startYValue = startY;
            int dstW = targetW;
            int dstH = targetH;
            if (srcWValue <= 0 || srcHValue <= 0 || dstW <= 0 || dstH <= 0)
            {
                return new FourChannelResult([], [], [], []);
            }

            int srcLength = checked(srcWValue * srcHValue);
            int dstLength = checked(dstW * dstH);
            int glLength = Math.Max(srcLength, dstLength);
            float[] rPad = OpenGLPreparePaddedChannel(rIn, srcLength, glLength);
            float[] gPad = OpenGLPreparePaddedChannel(gIn, srcLength, glLength);
            float[] bPad = OpenGLPreparePaddedChannel(bIn, srcLength, glLength);
            float[] aPad = OpenGLPreparePaddedChannel(aIn, srcLength, glLength);
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

                    int x = int(i % uint({{dstW}}));
                    int y = int(i / uint({{dstW}}));
                    int srcX = x - {{startXValue}};
                    int srcY = y - {{startYValue}};

                    if (srcX >= 0 && srcX < {{srcWValue}} && srcY >= 0 && srcY < {{srcHValue}})
                    {
                        int srcIdx = srcY * {{srcWValue}} + srcX;
                        outputData[i] = inputData[srcIdx];
                    }
                    else
                    {
                        outputData[i] = 0.0;
                    }
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
                        JobID = "PlaceEffect",
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

                    var rOut = await RunChannel(rPad, "PlaceEffect-R");
                    var gOut = await RunChannel(gPad, "PlaceEffect-G");
                    var bOut = await RunChannel(bPad, "PlaceEffect-B");
                    var aOut = await RunChannel(aPad, "PlaceEffect-A");
                    return new FourChannelResult(rOut, gOut, bOut, aOut);
                });
                if (!mainThreadTask.Wait(TimeSpan.FromSeconds(60)))
                {
                    throw new TimeoutException("PlaceEffect timed out after 60 seconds - likely deadlock due to main thread congestion.");
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

        private FourChannelResult VulkanComputePlace(float[] r, float[] g, float[] b, float[] a, int srcW, int srcH, int startX, int startY, int targetW, int targetH)
        {
            var rIn = r;
            var gIn = g;
            var bIn = b;
            var aIn = a;
            int srcWValue = srcW;
            int srcHValue = srcH;
            int startXValue = startX;
            int startYValue = startY;
            int dstW = targetW;
            int dstH = targetH;
            if (srcWValue <= 0 || srcHValue <= 0 || dstW <= 0 || dstH <= 0)
            {
                return new FourChannelResult([], [], [], []);
            }

            int srcLength = checked(srcWValue * srcHValue);
            int dstLength = checked(dstW * dstH);
            int vkLength = Math.Max(srcLength, dstLength);
            float[] rPad = VulkanPreparePaddedChannel(rIn, srcLength, vkLength);
            float[] gPad = VulkanPreparePaddedChannel(gIn, srcLength, vkLength);
            float[] bPad = VulkanPreparePaddedChannel(bIn, srcLength, vkLength);
            float[] aPad = VulkanPreparePaddedChannel(aIn, srcLength, vkLength);
            string shader = VulkanBuildPlaceShader(dstLength, dstW, srcWValue, srcHValue, startXValue, startYValue);
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
            }, "VulkanPlaceEffect timed out after 60 seconds - likely deadlock due to main thread congestion.");
        }

        private static float[] VulkanPreparePaddedChannel(float[] source, int sourceLength, int paddedLength)
        {
            var arr = new float[paddedLength];
            Array.Copy(source, 0, arr, 0, Math.Min(Math.Min(source.Length, sourceLength), paddedLength));
            return arr;
        }

        private static string VulkanBuildPlaceShader(int dstLength, int dstW, int srcW, int srcH, int startX, int startY)
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
                    int srcX = x - {{startX}};
                    int srcY = y - {{startY}};

                    if (srcX >= 0 && srcX < {{srcW}} && srcY >= 0 && srcY < {{srcH}})
                    {
                        int srcIdx = srcY * {{srcW}} + srcX;
                        outputData[i] = inputData[srcIdx];
                    }
                    else
                    {
                        outputData[i] = 0.0;
                    }
                }
                """;
        }
    }
}
#endif
