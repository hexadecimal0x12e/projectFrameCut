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
    public partial class ProgressCropper_HwAccel
    {
        private readonly ComputeBackend backend = AndroidExecutionHelper.PreferredBackend;
        private FourChannelResult ComputeCrop(float[] r, float[] g, float[] b, float[] a, int srcW, int srcH, int startX, int startY, int cropW, int cropH, float angle) => backend == ComputeBackend.Vulkan ? VulkanComputeCrop(r, g, b, a, srcW, srcH, startX, startY, cropW, cropH, angle) : OpenGLComputeCrop(r, g, b, a, srcW, srcH, startX, startY, cropW, cropH, angle);
        private FourChannelResult OpenGLComputeCrop(float[] r, float[] g, float[] b, float[] a, int srcW, int srcH, int startX, int startY, int cropW, int cropH, float angle)
        {
            var rIn = r;
            var gIn = g;
            var bIn = b;
            var aIn = a;
            int srcWValue = srcW;
            int srcHValue = srcH;
            int startXValue = startX;
            int startYValue = startY;
            int cropWValue = cropW;
            int cropHValue = cropH;
            if (srcWValue <= 0 || srcHValue <= 0 || cropWValue <= 0 || cropHValue <= 0)
            {
                return new FourChannelResult([], [], [], []);
            }

            int srcLength = checked(srcWValue * srcHValue);
            int dstLength = checked(cropWValue * cropHValue);
            int glLength = Math.Max(srcLength, dstLength);
            float[] rPad = OpenGLPreparePaddedChannel(rIn, srcLength, glLength);
            float[] gPad = OpenGLPreparePaddedChannel(gIn, srcLength, glLength);
            float[] bPad = OpenGLPreparePaddedChannel(bIn, srcLength, glLength);
            float[] aPad = OpenGLPreparePaddedChannel(aIn, srcLength, glLength);
            string cosValue = MathF.Cos(angle * MathF.PI / 180f).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            string sinValue = MathF.Sin(angle * MathF.PI / 180f).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
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

                    int x = int(i % uint({{cropWValue}}));
                    int y = int(i / uint({{cropWValue}}));
                    float rx = float(x) - float({{cropWValue}}) / 2.0;
                    float ry = float(y) - float({{cropHValue}}) / 2.0;
                    float sx = float({{cosValue}}) * rx - float({{sinValue}}) * ry + float({{startXValue}}) + float({{cropWValue}}) / 2.0;
                    float sy = float({{sinValue}}) * rx + float({{cosValue}}) * ry + float({{startYValue}}) + float({{cropHValue}}) / 2.0;
                    if (sx >= 0.0 && sx < float({{srcWValue}}) && sy >= 0.0 && sy < float({{srcHValue}}))
                    {
                        int x0 = int(sx), y0 = int(sy);
                        int x1 = min(x0 + 1, {{srcWValue}} - 1), y1 = min(y0 + 1, {{srcHValue}} - 1);
                        float fx = sx - float(x0), fy = sy - float(y0);
                        outputData[i] = mix(mix(inputData[y0 * {{srcWValue}} + x0], inputData[y0 * {{srcWValue}} + x1], fx),
                            mix(inputData[y1 * {{srcWValue}} + x0], inputData[y1 * {{srcWValue}} + x1], fx), fy);
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
                        JobID = "CropEffect",
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

                    var rOut = await RunChannel(rPad, "CropEffect-R");
                    var gOut = await RunChannel(gPad, "CropEffect-G");
                    var bOut = await RunChannel(bPad, "CropEffect-B");
                    var aOut = await RunChannel(aPad, "CropEffect-A");
                    return new FourChannelResult(rOut, gOut, bOut, aOut);
                });
                if (!mainThreadTask.Wait(TimeSpan.FromSeconds(60)))
                {
                    throw new TimeoutException("CropEffect timed out after 60 seconds - likely deadlock due to main thread congestion.");
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

        private FourChannelResult VulkanComputeCrop(float[] r, float[] g, float[] b, float[] a, int srcW, int srcH, int startX, int startY, int cropW, int cropH, float angle)
        {
            var rIn = r;
            var gIn = g;
            var bIn = b;
            var aIn = a;
            int srcWValue = srcW;
            int srcHValue = srcH;
            int startXValue = startX;
            int startYValue = startY;
            int cropWValue = cropW;
            int cropHValue = cropH;
            if (srcWValue <= 0 || srcHValue <= 0 || cropWValue <= 0 || cropHValue <= 0)
            {
                return new FourChannelResult([], [], [], []);
            }

            int srcLength = checked(srcWValue * srcHValue);
            int dstLength = checked(cropWValue * cropHValue);
            int vkLength = Math.Max(srcLength, dstLength);
            float[] rPad = VulkanPreparePaddedChannel(rIn, srcLength, vkLength);
            float[] gPad = VulkanPreparePaddedChannel(gIn, srcLength, vkLength);
            float[] bPad = VulkanPreparePaddedChannel(bIn, srcLength, vkLength);
            float[] aPad = VulkanPreparePaddedChannel(aIn, srcLength, vkLength);
            string cosValue = MathF.Cos(angle * MathF.PI / 180f).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            string sinValue = MathF.Sin(angle * MathF.PI / 180f).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            string shader = VulkanBuildCropShader(dstLength, cropWValue, startXValue, startYValue, srcWValue, srcHValue, cropHValue, angle);
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
            }, "VulkanCropEffect timed out after 60 seconds - likely deadlock due to main thread congestion.");
        }

        private static float[] VulkanPreparePaddedChannel(float[] source, int sourceLength, int paddedLength)
        {
            var arr = new float[paddedLength];
            Array.Copy(source, 0, arr, 0, Math.Min(Math.Min(source.Length, sourceLength), paddedLength));
            return arr;
        }

        private static string VulkanBuildCropShader(int dstLength, int cropW, int startX, int startY, int srcW, int srcH, int cropH, float angle)
        {
            string cosA = MathF.Cos(angle * MathF.PI / 180f).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            string sinA = MathF.Sin(angle * MathF.PI / 180f).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
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

                    int x = int(i % uint({{cropW}}));
                    int y = int(i / uint({{cropW}}));
                    float rx = float(x) - float({{cropW}}) / 2.0;
                    float ry = float(y) - float({{cropH}}) / 2.0;
                    float sx = float({{cosA}}) * rx - float({{sinA}}) * ry + float({{startX}}) + float({{cropW}}) / 2.0;
                    float sy = float({{sinA}}) * rx + float({{cosA}}) * ry + float({{startY}}) + float({{cropH}}) / 2.0;
                    if (sx >= 0.0 && sx < float({{srcW}}) && sy >= 0.0 && sy < float({{srcH}}))
                    {
                        int x0 = int(sx), y0 = int(sy);
                        int x1 = min(x0 + 1, {{srcW}} - 1), y1 = min(y0 + 1, {{srcH}} - 1);
                        float fx = sx - float(x0), fy = sy - float(y0);
                        outputData[i] = mix(mix(inputData[y0 * {{srcW}} + x0], inputData[y0 * {{srcW}} + x1], fx),
                            mix(inputData[y1 * {{srcW}} + x0], inputData[y1 * {{srcW}} + x1], fx), fy);
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
