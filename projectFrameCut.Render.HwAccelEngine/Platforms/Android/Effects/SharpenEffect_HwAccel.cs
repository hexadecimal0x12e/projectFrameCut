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
    public partial class SharpenEffect_HwAccel
    {
        private readonly ComputeBackend backend = AndroidExecutionHelper.PreferredBackend;
        private FourChannelResult ComputeSharpen(float[] r, float[] g, float[] b, float[] a, int w, float amount) => backend == ComputeBackend.Vulkan ? VulkanComputeSharpen(r, g, b, a, w, amount) : OpenGLComputeSharpen(r, g, b, a, w, amount);
        private FourChannelResult OpenGLComputeSharpen(float[] r, float[] g, float[] b, float[] a, int w, float amount)
        {
            int wValue = w;
            float amountValue = amount;
            return OpenGLRunSinglePass(r, g, b, a, (srcLen, glLen, len) => $$"""
                #version 310 es
                layout(local_size_x = 256) in;
                layout(std430, binding = 0) buffer InBuffer { float inputData[]; };
                layout(std430, binding = 6) buffer OutBuffer { float outputData[]; };
                void main()
                {
                    uint i = gl_GlobalInvocationID.x;
                    if (i >= uint({{len}})) { return; }
                    int x = int(i % uint({{wValue}}));
                    float orig = inputData[i];
                    int left = (x > 0) ? int(i) - 1 : int(i);
                    int right = (x < {{wValue}} - 1) ? int(i) + 1 : int(i);
                    int top = int(i) - {{wValue}};
                    if (top < 0) top = int(i);
                    int bottom = int(i) + {{wValue}};
                    if (bottom >= {{len}}) bottom = int(i);
                    float avg = (inputData[left] + inputData[right] + inputData[top] + inputData[bottom]) * 0.25;
                    outputData[i] = orig + {{amountValue}} * (orig - avg);
                }
                """, "SharpenEffect");
        }

        private static FourChannelResult OpenGLRunSinglePass(float[] rIn, float[] gIn, float[] bIn, float[] aIn, Func<int, int, int, string> buildShader, string effectName)
        {
            int srcLength = rIn.Length;
            int glLength = srcLength;
            float[] rPad = OpenGLPreparePaddedChannel(rIn, srcLength, glLength);
            float[] gPad = OpenGLPreparePaddedChannel(gIn, srcLength, glLength);
            float[] bPad = OpenGLPreparePaddedChannel(bIn, srcLength, glLength);
            float[] aPad = OpenGLPreparePaddedChannel(aIn, srcLength, glLength);
            string shader = buildShader(srcLength, glLength, srcLength);
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
                        JobID = effectName,
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
                        throw new TimeoutException($"GLComputeView.WaitUntilReadyAsync timed out.");
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
                        if (raw.Length == srcLength)
                            return raw;
                        var trimmed = new float[srcLength];
                        Array.Copy(raw, 0, trimmed, 0, Math.Min(raw.Length, srcLength));
                        return trimmed;
                    }

                    var rOut = await RunChannel(rPad, effectName + "-R");
                    var gOut = await RunChannel(gPad, effectName + "-G");
                    var bOut = await RunChannel(bPad, effectName + "-B");
                    var aOut = aIn.ToArray();
                    return new FourChannelResult(rOut, gOut, bOut, aOut);
                });
                if (!mainThreadTask.Wait(TimeSpan.FromSeconds(60)))
                    throw new TimeoutException($"{effectName}.Compute timed out after 60 seconds.");
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

        private FourChannelResult VulkanComputeSharpen(float[] r, float[] g, float[] b, float[] a, int w, float amount)
        {
            int wValue = w;
            float amountValue = amount;
            int len = ((float[])(r)).Length;
            return VulkanRunSinglePass(r, g, b, a, $$"""
                #version 450
                layout(local_size_x = 256) in;
                layout(set = 0, binding = 0, std430) buffer InBuf { float i[]; };
                layout(set = 0, binding = 1, std430) buffer OutBuf { float o[]; };
                void main() {
                    uint idx = gl_GlobalInvocationID.x;
                    if (idx >= {{len}}) { return; }
                    int x = int(idx % {{wValue}});
                    float orig = i[idx];
                    int left = x > 0 ? int(idx) - 1 : int(idx);
                    int right = x < {{wValue}} - 1 ? int(idx) + 1 : int(idx);
                    int top = int(idx) - {{wValue}}; if (top < 0) top = int(idx);
                    int bottom = int(idx) + {{wValue}}; if (bottom >= {{len}}) bottom = int(idx);
                    float avg = (i[left] + i[right] + i[top] + i[bottom]) * 0.25;
                    o[idx] = orig + {{amountValue}} * (orig - avg);
                }
                """, "VulkanSharpenEffect");
        }

        private static FourChannelResult VulkanRunSinglePass(float[] rIn, float[] gIn, float[] bIn, float[] aIn, string shader, string name)
        {
            int len = rIn.Length;
            return VulkanExecutionHelper.EnqueueCompute(async () =>
            {
                var(accelerator, handler, vkView) = await VulkanExecutionHelper.CreateAcceleratorAsync(shader, new float[][] { rIn }, OutputElementType.Float32);
                using var acceleratorScope = AndroidExecutionHelper.UseView(accelerator);
                async Task<float[]> RunChannel(float[] channel)
                {
                    accelerator.Inputs = new float[][]
                    {
                        channel
                    };
                    NativeVulkanSurfaceViewHandler.MapInputs(handler, accelerator);
                    var raw = (float[])await vkView.RunComputeAsync(OutputElementType.Float32);
                    if (raw.Length == len)
                        return raw;
                    var trimmed = new float[len];
                    Array.Copy(raw, 0, trimmed, 0, Math.Min(raw.Length, len));
                    return trimmed;
                }

                var rOut = await RunChannel(rIn);
                var gOut = await RunChannel(gIn);
                var bOut = await RunChannel(bIn);
                var aOut = aIn.ToArray();
                return new FourChannelResult(rOut, gOut, bOut, aOut);
            }, $"{name}.Compute timed out.");
        }
    }
}
#endif
