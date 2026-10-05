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
    public partial class RemoveColorEffect_HwAccel
    {
        private readonly ComputeBackend backend = AndroidExecutionHelper.PreferredBackend;
        private float[] ComputeRemoveColor(float[] r, float[] g, float[] b, float[] a, float targetR, float targetG, float targetB, float range, int pixels) => backend == ComputeBackend.Vulkan ? VulkanComputeRemoveColor(r, g, b, a, targetR, targetG, targetB, range, pixels) : OpenGLComputeRemoveColor(r, g, b, a, targetR, targetG, targetB, range, pixels);
        private float[] OpenGLComputeRemoveColor(float[] r, float[] g, float[] b, float[] a, float targetR, float targetG, float targetB, float range, int pixels)
        {
            var aR = (r) as float[];
            var aG = (g) as float[];
            var aB = (b) as float[];
            var sourceA = (a) as float[];
            if (aR is null || aG is null || aB is null || sourceA is null)
            {
                throw new ArgumentException("RemoveColorEffect expects four float[] channel arrays.");
            }

            var toRemoveR = (ushort)(targetR);
            var toRemoveG = (ushort)(targetG);
            var toRemoveB = (ushort)(targetB);
            var rangeValue = (ushort)range;
            // Validate all input arrays have the same length
            if (aR.Length != aG.Length || aR.Length != aB.Length || aR.Length != sourceA.Length)
            {
                throw new InvalidDataException($"Input array length mismatch: aR={aR.Length}, aG={aG.Length}, aB={aB.Length}, sourceA={sourceA.Length}");
            }

            int lowR = Math.Max(0, toRemoveR - rangeValue);
            int highR = Math.Min(65535, toRemoveR + rangeValue);
            int lowG = Math.Max(0, toRemoveG - rangeValue);
            int highG = Math.Min(65535, toRemoveG + rangeValue);
            int lowB = Math.Max(0, toRemoveB - rangeValue);
            int highB = Math.Min(65535, toRemoveB + rangeValue);
            string shader = $$"""
                #version 310 es
                layout(local_size_x = 256) in;

                layout(std430, binding = 0) buffer RBuffer { float r[]; };
                layout(std430, binding = 1) buffer GBuffer { float g[]; };
                layout(std430, binding = 2) buffer BBuffer { float b[]; };
                layout(std430, binding = 3) buffer ABuffer { float a[]; };
                layout(std430, binding = 6) buffer OutBuffer { float outA[]; };

                void main() {
                    uint i = gl_GlobalInvocationID.x;
                    float curR = r[i];
                    float curG = g[i];
                    float curB = b[i];

                    bool matchR = (curR >= {{lowR}}.0 && curR <= {{highR}}.0);
                    bool matchG = (curG >= {{lowG}}.0 && curG <= {{highG}}.0);
                    bool matchB = (curB >= {{lowB}}.0 && curB <= {{highB}}.0);

                    if (matchR && matchG && matchB) {
                        outA[i] = 0.0;
                    } else {
                        outA[i] = a[i];
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
                            aR,
                            aG,
                            aB,
                            sourceA
                        },
                        WidthRequest = 50,
                        HeightRequest = 50,
                        JobID = "RemoveColorEffect",
                        OutputElementType = GLComputeView.OutputElementType.Float32
                    };
                    using var viewScope = AndroidExecutionHelper.UseView(accelerator);
                    // Wait for Handler to be created/attached
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
                    // Check if handler is already set (in case HandlerChanged fired before we subscribed)
                    if (accelerator.Handler is NativeGLSurfaceViewHandler existingHandler)
                    {
                        accelerator.HandlerChanged -= OnHandlerChanged;
                        handlerReadyTcs.TrySetResult(existingHandler);
                    }

                    // Wait for handler with timeout
                    var handlerWaitTask = handlerReadyTcs.Task;
                    if (await Task.WhenAny(handlerWaitTask, Task.Delay(TimeSpan.FromSeconds(10))) != handlerWaitTask)
                    {
                        accelerator.HandlerChanged -= OnHandlerChanged;
                        throw new TimeoutException("Handler creation timed out after 10 seconds.");
                    }

                    var handler = await handlerWaitTask;
                    if (handler?.PlatformView is not GLComputeView glView)
                        throw new InvalidOperationException("Accelerator is not ready or not attached.");
                    // Add timeout to WaitUntilReadyAsync to prevent infinite wait
                    var readyTask = glView.WaitUntilReadyAsync();
                    if (await Task.WhenAny(readyTask, Task.Delay(TimeSpan.FromMilliseconds(AndroidExecutionHelper.Timeout))) != readyTask)
                        throw new TimeoutException($"GLComputeView.WaitUntilReadyAsync timed out after {AndroidExecutionHelper.Timeout}ms.");
                    await readyTask; // Propagate any exception
                    return (float[])await glView.RunComputeAsync(GLComputeView.OutputElementType.Float32);
                });
                // Use Task.Wait with timeout instead of .Result to detect deadlocks
                if (!mainThreadTask.Wait(TimeSpan.FromSeconds(60)))
                {
                    throw new TimeoutException($"RemoveColorEffect timed out after 60 seconds - likely deadlock due to main thread congestion. Consider reducing MaxThreads on Android.");
                }

                var result = TaskHelper.SyncWait(() => mainThreadTask, CancellationToken.None);
                if (result is null)
                    throw new InvalidOperationException($"RemoveColorEffect Compute failed: accelerator returned null result.");
                return result;
            });
        }

        private float[] VulkanComputeRemoveColor(float[] r, float[] g, float[] b, float[] a, float targetR, float targetG, float targetB, float range, int pixels)
        {
            var aR = (r) as float[];
            var aG = (g) as float[];
            var aB = (b) as float[];
            var sourceA = (a) as float[];
            if (aR is null || aG is null || aB is null || sourceA is null)
            {
                throw new ArgumentException("VulkanRemoveColorEffect expects four float[] channel arrays.");
            }

            var toRemoveR = (ushort)(targetR);
            var toRemoveG = (ushort)(targetG);
            var toRemoveB = (ushort)(targetB);
            var rangeValue = (ushort)range;
            if (aR.Length != aG.Length || aR.Length != aB.Length || aR.Length != sourceA.Length)
            {
                throw new InvalidDataException($"Input array length mismatch: aR={aR.Length}, aG={aG.Length}, aB={aB.Length}, sourceA={sourceA.Length}");
            }

            int lowR = Math.Max(0, toRemoveR - rangeValue);
            int highR = Math.Min(65535, toRemoveR + rangeValue);
            int lowG = Math.Max(0, toRemoveG - rangeValue);
            int highG = Math.Min(65535, toRemoveG + rangeValue);
            int lowB = Math.Max(0, toRemoveB - rangeValue);
            int highB = Math.Min(65535, toRemoveB + rangeValue);
            string shader = VulkanBuildRemoveColorShader(lowR, highR, lowG, highG, lowB, highB);
            return VulkanExecutionHelper.EnqueueCompute(async () =>
            {
                var(accelerator, _, vkView) = await VulkanExecutionHelper.CreateAcceleratorAsync(shader, new float[][] { aR, aG, aB, sourceA }, OutputElementType.Float32);
                using var viewScope = AndroidExecutionHelper.UseView(accelerator);
                var result = await vkView.RunComputeAsync(OutputElementType.Float32);
                return (float[])result;
            }, "VulkanRemoveColorEffect timed out after 60 seconds - likely deadlock due to main thread congestion.");
        }

        private static string VulkanBuildRemoveColorShader(int lowR, int highR, int lowG, int highG, int lowB, int highB)
        {
            return $$"""
                #version 450
                layout(local_size_x = 256) in;

                layout(set = 0, binding = 0, std430) buffer RBuffer { float r[]; };
                layout(set = 0, binding = 1, std430) buffer GBuffer { float g[]; };
                layout(set = 0, binding = 2, std430) buffer BBuffer { float b[]; };
                layout(set = 0, binding = 3, std430) buffer ABuffer { float a[]; };
                layout(set = 0, binding = 4, std430) buffer OutBuffer { float outA[]; };

                void main() {
                    uint i = gl_GlobalInvocationID.x;
                    float curR = r[i];
                    float curG = g[i];
                    float curB = b[i];

                    bool matchR = (curR >= {{lowR}}.0 && curR <= {{highR}}.0);
                    bool matchG = (curG >= {{lowG}}.0 && curG <= {{highG}}.0);
                    bool matchB = (curB >= {{lowB}}.0 && curB <= {{highB}}.0);

                    if (matchR && matchG && matchB) {
                        outA[i] = 0.0;
                    } else {
                        outA[i] = a[i];
                    }
                }
                """;
        }
    }
}
#endif
