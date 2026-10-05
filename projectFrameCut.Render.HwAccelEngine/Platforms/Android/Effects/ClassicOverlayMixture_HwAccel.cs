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
    public partial class ClassicOverlayMixture_HwAccel
    {
        private readonly ComputeBackend backend = AndroidExecutionHelper.PreferredBackend;
        protected override (byte[] Color, float[] Alpha) Overlay8(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            using var scope = nativeLock.EnterScope();
            ObjectDisposedException.ThrowIf(disposed, this);
            var result = backend == ComputeBackend.Vulkan ? VulkanRunOverlay(top, bottom, topAlpha, bottomAlpha, 8, pixelCount) : OpenGLRunOverlay(top, bottom, topAlpha, bottomAlpha, 8, pixelCount);
            return ((byte[])result.Color, result.Alpha);
        }

        protected override (ushort[] Color, float[] Alpha) Overlay16(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            using var scope = nativeLock.EnterScope();
            ObjectDisposedException.ThrowIf(disposed, this);
            var result = backend == ComputeBackend.Vulkan ? VulkanRunOverlay(top, bottom, topAlpha, bottomAlpha, 16, pixelCount) : OpenGLRunOverlay(top, bottom, topAlpha, bottomAlpha, 16, pixelCount);
            return ((ushort[])result.Color, result.Alpha);
        }

        protected override (float[] Color, float[] Alpha) OverlayHdr(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            using var scope = nativeLock.EnterScope();
            ObjectDisposedException.ThrowIf(disposed, this);
            var result = backend == ComputeBackend.Vulkan ? VulkanRunOverlay(top, bottom, topAlpha, bottomAlpha, 0, pixelCount) : OpenGLRunOverlay(top, bottom, topAlpha, bottomAlpha, 0, pixelCount);
            return ((float[])result.Color, result.Alpha);
        }

        protected override (byte[] Color, float[] Alpha) ApproximateOverlay8(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            using var scope = nativeLock.EnterScope();
            ObjectDisposedException.ThrowIf(disposed, this);
            var result = backend == ComputeBackend.Vulkan ? VulkanApproximateRunOverlay(top, bottom, topAlpha, bottomAlpha, 8, pixelCount) : OpenGLApproximateRunOverlay(top, bottom, topAlpha, bottomAlpha, 8, pixelCount);
            return ((byte[])result.Color, result.Alpha);
        }

        protected override (ushort[] Color, float[] Alpha) ApproximateOverlay16(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            using var scope = nativeLock.EnterScope();
            ObjectDisposedException.ThrowIf(disposed, this);
            var result = backend == ComputeBackend.Vulkan ? VulkanApproximateRunOverlay(top, bottom, topAlpha, bottomAlpha, 16, pixelCount) : OpenGLApproximateRunOverlay(top, bottom, topAlpha, bottomAlpha, 16, pixelCount);
            return ((ushort[])result.Color, result.Alpha);
        }

        protected override (float[] Color, float[] Alpha) ApproximateOverlayHdr(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            using var scope = nativeLock.EnterScope();
            ObjectDisposedException.ThrowIf(disposed, this);
            var result = backend == ComputeBackend.Vulkan ? VulkanApproximateRunOverlay(top, bottom, topAlpha, bottomAlpha, 0, pixelCount) : OpenGLApproximateRunOverlay(top, bottom, topAlpha, bottomAlpha, 0, pixelCount);
            return ((float[])result.Color, result.Alpha);
        }

        private (Array Color, float[] Alpha) OpenGLRunOverlay(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int outputBpp, int pixelCount)
        {
            var A = (top) as float[];
            var B = (bottom) as float[];
            var aAlpha = ((topAlpha) as float[]);
            var bAlpha = ((bottomAlpha) as float[]);
            var outputBppValue = outputBpp;
            var actualPixels = pixelCount;
            // A and B may be larger than actualPixels due to ArrayPool.Rent
            // Trim them to the actual size to match alpha arrays
            float[] trimmedA = new float[actualPixels];
            float[] trimmedB = new float[actualPixels];
            Array.Copy(A!, 0, trimmedA, 0, actualPixels);
            Array.Copy(B!, 0, trimmedB, 0, actualPixels);
            // Ensure alpha arrays match the actual pixel count
            if (aAlpha == null)
                aAlpha = Enumerable.Repeat(1f, actualPixels).ToArray();
            if (bAlpha == null)
                bAlpha = Enumerable.Repeat(1f, actualPixels).ToArray();
            // Validate all arrays have the same length
            if (aAlpha.Length != actualPixels || bAlpha.Length != actualPixels)
            {
                // aAlpha/bAlpha may also come from pooled arrays; trim to the effective pixel count.
                float[] trimmedAAlpha = new float[actualPixels];
                float[] trimmedBAlpha = new float[actualPixels];
                Array.Copy(aAlpha, 0, trimmedAAlpha, 0, Math.Min(aAlpha.Length, actualPixels));
                Array.Copy(bAlpha, 0, trimmedBAlpha, 0, Math.Min(bAlpha.Length, actualPixels));
                aAlpha = trimmedAAlpha;
                bAlpha = trimmedBAlpha;
            }

#if DEBUG
            Logger.LogDiagnostic($"[OverlayEffect] Input lengths normalized: actualPixels={actualPixels}, A={(A?.Length ?? 0)}, B={(B?.Length ?? 0)}, trimmedA={trimmedA.Length}, trimmedB={trimmedB.Length}, aAlpha={aAlpha.Length}, bAlpha={bAlpha.Length}, outputBppValue={outputBppValue}");
#endif
            return AndroidExecutionHelper.EnqueueCompute<(Array Color, float[] Alpha)>(() =>
            {
                // We need to run on MainThread because we are touching UI elements (NativeGLSurfaceView)
                // Use GetAwaiter().GetResult() with timeout to avoid deadlock when main thread is busy
                var mainThreadTask = MainThread.InvokeOnMainThreadAsync<(Array Color, float[] Alpha)>(async () =>
                {
                    NativeGLSurfaceView accelerator = new NativeGLSurfaceView
                    {
                        ShaderSource = OpenGLAlpha,
                        Inputs = new float[][]
                        {
                            aAlpha,
                            bAlpha
                        },
                        WidthRequest = 50,
                        HeightRequest = 50,
                        JobID = "OverlayEffect",
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
                    var alphaResult = (float[])await glView.RunComputeAsync(GLComputeView.OutputElementType.Float32);
                    // 2. Compute Color
                    if (outputBppValue == 8)
                    {
                        accelerator.ShaderSource = OpenGLShaderColorSrcU8;
                        accelerator.OutputElementType = GLComputeView.OutputElementType.UInt32;
                    }
                    else if (outputBppValue == 16)
                    {
                        accelerator.ShaderSource = OpenGLShaderColorSrcU16;
                        accelerator.OutputElementType = GLComputeView.OutputElementType.UInt32;
                    }
                    else
                    {
                        accelerator.ShaderSource = OpenGLShaderColorSrc;
                        accelerator.OutputElementType = GLComputeView.OutputElementType.Float32;
                    }

                    accelerator.Inputs = new float[][]
                    {
                        aAlpha,
                        trimmedA,
                        bAlpha,
                        trimmedB
                    };
                    NativeGLSurfaceViewHandler.MapInputs(handler, accelerator);
                    var colorResult = await glView.RunComputeAsync(accelerator.OutputElementType);
                    if (outputBppValue == 8)
                    {
                        var colorU32 = (uint[])colorResult;
                        var outputU8 = new byte[colorU32.Length];
                        for (int i = 0; i < colorU32.Length; i++)
                        {
                            uint v = colorU32[i];
                            if (v > 255u)
                                v = 255u;
                            outputU8[i] = (byte)v;
                        }

                        return (outputU8, alphaResult);
                    }

                    if (outputBppValue == 16)
                    {
                        var colorU32 = (uint[])colorResult;
                        var outputU16 = new ushort[colorU32.Length];
                        for (int i = 0; i < colorU32.Length; i++)
                        {
                            uint v = colorU32[i];
                            if (v > 65535u)
                                v = 65535u;
                            outputU16[i] = (ushort)v;
                        }

                        return (outputU16, alphaResult);
                    }

                    return ((float[])colorResult, alphaResult);
                });
                // Use Task.Wait with timeout instead of .Result to detect deadlocks
                if (!mainThreadTask.Wait(TimeSpan.FromSeconds(60)))
                {
                    throw new TimeoutException($"OverlayEffect timed out after 60 seconds - likely deadlock due to main thread congestion. Consider reducing MaxThreads on Android.");
                }

                var result = TaskHelper.SyncWait(() => mainThreadTask, CancellationToken.None);
                return result;
            });
        }

        private (Array Color, float[] Alpha) OpenGLApproximateRunOverlay(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int outputBpp, int pixelCount)
        {
            var A = (top) as float[];
            var B = (bottom) as float[];
            var aAlpha = ((topAlpha) as float[]);
            var bAlpha = ((bottomAlpha) as float[]);
            var outputBppValue = outputBpp;
            var actualPixels = pixelCount;
            // A and B may be larger than actualPixels due to ArrayPool.Rent
            // Trim them to the actual size to match alpha arrays
            float[] trimmedA = new float[actualPixels];
            float[] trimmedB = new float[actualPixels];
            Array.Copy(A!, 0, trimmedA, 0, actualPixels);
            Array.Copy(B!, 0, trimmedB, 0, actualPixels);
            // Ensure alpha arrays match the actual pixel count
            if (aAlpha == null)
                aAlpha = Enumerable.Repeat(1f, actualPixels).ToArray();
            if (bAlpha == null)
                bAlpha = Enumerable.Repeat(1f, actualPixels).ToArray();
            // Validate all arrays have the same length
            if (aAlpha.Length != actualPixels || bAlpha.Length != actualPixels)
            {
                // aAlpha/bAlpha may also come from pooled arrays; trim to the effective pixel count.
                float[] trimmedAAlpha = new float[actualPixels];
                float[] trimmedBAlpha = new float[actualPixels];
                Array.Copy(aAlpha, 0, trimmedAAlpha, 0, Math.Min(aAlpha.Length, actualPixels));
                Array.Copy(bAlpha, 0, trimmedBAlpha, 0, Math.Min(bAlpha.Length, actualPixels));
                aAlpha = trimmedAAlpha;
                bAlpha = trimmedBAlpha;
            }

#if DEBUG
            Logger.LogDiagnostic($"[ApproximateEffect] Input lengths normalized: actualPixels={actualPixels}, A={(A?.Length ?? 0)}, B={(B?.Length ?? 0)}, trimmedA={trimmedA.Length}, trimmedB={trimmedB.Length}, aAlpha={aAlpha.Length}, bAlpha={bAlpha.Length}, outputBppValue={outputBppValue}");
#endif
            return AndroidExecutionHelper.EnqueueCompute<(Array Color, float[] Alpha)>(() =>
            {
                // We need to run on MainThread because we are touching UI elements (NativeGLSurfaceView)
                // Use GetAwaiter().GetResult() with timeout to avoid deadlock when main thread is busy
                var mainThreadTask = MainThread.InvokeOnMainThreadAsync<(Array Color, float[] Alpha)>(async () =>
                {
                    NativeGLSurfaceView accelerator = new NativeGLSurfaceView
                    {
                        ShaderSource = OpenGLAlpha,
                        Inputs = new float[][]
                        {
                            aAlpha,
                            bAlpha
                        },
                        WidthRequest = 50,
                        HeightRequest = 50,
                        JobID = "ApproximateEffect",
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
                    var alphaResult = (float[])await glView.RunComputeAsync(GLComputeView.OutputElementType.Float32);
                    // 2. Compute Color
                    if (outputBppValue == 8)
                    {
                        accelerator.ShaderSource = OpenGLShaderColorSrcU8;
                        accelerator.OutputElementType = GLComputeView.OutputElementType.UInt32;
                    }
                    else if (outputBppValue == 16)
                    {
                        accelerator.ShaderSource = OpenGLShaderColorSrcU16;
                        accelerator.OutputElementType = GLComputeView.OutputElementType.UInt32;
                    }
                    else
                    {
                        accelerator.ShaderSource = OpenGLShaderColorSrc;
                        accelerator.OutputElementType = GLComputeView.OutputElementType.Float32;
                    }

                    accelerator.Inputs = new float[][]
                    {
                        aAlpha,
                        trimmedA,
                        bAlpha,
                        trimmedB
                    };
                    NativeGLSurfaceViewHandler.MapInputs(handler, accelerator);
                    var colorResult = await glView.RunComputeAsync(accelerator.OutputElementType);
                    if (outputBppValue == 8)
                    {
                        var colorU32 = (uint[])colorResult;
                        var outputU8 = new byte[colorU32.Length];
                        for (int i = 0; i < colorU32.Length; i++)
                        {
                            uint v = colorU32[i];
                            if (v > 255u)
                                v = 255u;
                            outputU8[i] = (byte)v;
                        }

                        return (outputU8, alphaResult);
                    }

                    if (outputBppValue == 16)
                    {
                        var colorU32 = (uint[])colorResult;
                        var outputU16 = new ushort[colorU32.Length];
                        for (int i = 0; i < colorU32.Length; i++)
                        {
                            uint v = colorU32[i];
                            if (v > 65535u)
                                v = 65535u;
                            outputU16[i] = (ushort)v;
                        }

                        return (outputU16, alphaResult);
                    }

                    return ((float[])colorResult, alphaResult);
                });
                // Use Task.Wait with timeout instead of .Result to detect deadlocks
                if (!mainThreadTask.Wait(TimeSpan.FromSeconds(60)))
                {
                    throw new TimeoutException($"ApproximateEffect timed out after 60 seconds - likely deadlock due to main thread congestion. Consider reducing MaxThreads on Android.");
                }

                var result = TaskHelper.SyncWait(() => mainThreadTask, CancellationToken.None);
                return result;
            });
        }

        private (Array Color, float[] Alpha) VulkanRunOverlay(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int outputBpp, int pixelCount)
        {
            var a = (top) as float[];
            var b = (bottom) as float[];
            var aAlpha = ((topAlpha) as float[]);
            var bAlpha = ((bottomAlpha) as float[]);
            var outputBppValue = outputBpp;
            var actualPixels = pixelCount;
            float[] trimmedA = new float[actualPixels];
            float[] trimmedB = new float[actualPixels];
            Array.Copy(a!, 0, trimmedA, 0, actualPixels);
            Array.Copy(b!, 0, trimmedB, 0, actualPixels);
            if (aAlpha == null)
                aAlpha = Enumerable.Repeat(1f, actualPixels).ToArray();
            if (bAlpha == null)
                bAlpha = Enumerable.Repeat(1f, actualPixels).ToArray();
            if (aAlpha.Length != actualPixels || bAlpha.Length != actualPixels)
            {
                float[] trimmedAAlpha = new float[actualPixels];
                float[] trimmedBAlpha = new float[actualPixels];
                Array.Copy(aAlpha, 0, trimmedAAlpha, 0, Math.Min(aAlpha.Length, actualPixels));
                Array.Copy(bAlpha, 0, trimmedBAlpha, 0, Math.Min(bAlpha.Length, actualPixels));
                aAlpha = trimmedAAlpha;
                bAlpha = trimmedBAlpha;
            }

            return VulkanExecutionHelper.EnqueueCompute<(Array Color, float[] Alpha)>(async () =>
            {
                var(accelerator, handler, vkView) = await VulkanExecutionHelper.CreateAcceleratorAsync(VulkanOpenGLAlpha, new float[][] { aAlpha, bAlpha }, OutputElementType.Float32);
                using var viewScope = AndroidExecutionHelper.UseView(accelerator);
                var alphaResult = (float[])await vkView.RunComputeAsync(OutputElementType.Float32);
                if (outputBppValue == 8)
                {
                    accelerator.ShaderSource = VulkanOpenGLShaderColorSrcU8;
                    accelerator.OutputElementType = OutputElementType.UInt32;
                }
                else if (outputBppValue == 16)
                {
                    accelerator.ShaderSource = VulkanOpenGLShaderColorSrcU16;
                    accelerator.OutputElementType = OutputElementType.UInt32;
                }
                else
                {
                    accelerator.ShaderSource = VulkanOpenGLShaderColorSrc;
                    accelerator.OutputElementType = OutputElementType.Float32;
                }

                accelerator.Inputs = new float[][]
                {
                    aAlpha,
                    trimmedA,
                    bAlpha,
                    trimmedB
                };
                NativeVulkanSurfaceViewHandler.MapInputs(handler, accelerator);
                var colorResult = await vkView.RunComputeAsync(accelerator.OutputElementType);
                if (outputBppValue == 8)
                {
                    var colorU32 = (uint[])colorResult;
                    var outputU8 = new byte[colorU32.Length];
                    for (int i = 0; i < colorU32.Length; i++)
                    {
                        uint v = colorU32[i];
                        if (v > 255u)
                            v = 255u;
                        outputU8[i] = (byte)v;
                    }

                    return (outputU8, alphaResult);
                }

                if (outputBppValue == 16)
                {
                    var colorU32 = (uint[])colorResult;
                    var outputU16 = new ushort[colorU32.Length];
                    for (int i = 0; i < colorU32.Length; i++)
                    {
                        uint v = colorU32[i];
                        if (v > 65535u)
                            v = 65535u;
                        outputU16[i] = (ushort)v;
                    }

                    return (outputU16, alphaResult);
                }

                return ((float[])colorResult, alphaResult);
            }, "VulkanOverlayEffect timed out after 60 seconds - likely deadlock due to main thread congestion.");
        }

        private (Array Color, float[] Alpha) VulkanApproximateRunOverlay(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int outputBpp, int pixelCount)
        {
            var a = (top) as float[];
            var b = (bottom) as float[];
            var aAlpha = ((topAlpha) as float[]);
            var bAlpha = ((bottomAlpha) as float[]);
            var outputBppValue = outputBpp;
            var actualPixels = pixelCount;
            float[] trimmedA = new float[actualPixels];
            float[] trimmedB = new float[actualPixels];
            Array.Copy(a!, 0, trimmedA, 0, actualPixels);
            Array.Copy(b!, 0, trimmedB, 0, actualPixels);
            if (aAlpha == null)
                aAlpha = Enumerable.Repeat(1f, actualPixels).ToArray();
            if (bAlpha == null)
                bAlpha = Enumerable.Repeat(1f, actualPixels).ToArray();
            if (aAlpha.Length != actualPixels || bAlpha.Length != actualPixels)
            {
                float[] trimmedAAlpha = new float[actualPixels];
                float[] trimmedBAlpha = new float[actualPixels];
                Array.Copy(aAlpha, 0, trimmedAAlpha, 0, Math.Min(aAlpha.Length, actualPixels));
                Array.Copy(bAlpha, 0, trimmedBAlpha, 0, Math.Min(bAlpha.Length, actualPixels));
                aAlpha = trimmedAAlpha;
                bAlpha = trimmedBAlpha;
            }

            return VulkanExecutionHelper.EnqueueCompute<(Array Color, float[] Alpha)>(async () =>
            {
                var(accelerator, handler, vkView) = await VulkanExecutionHelper.CreateAcceleratorAsync(VulkanOpenGLAlpha, new float[][] { aAlpha, bAlpha }, OutputElementType.Float32);
                using var viewScope = AndroidExecutionHelper.UseView(accelerator);
                var alphaResult = (float[])await vkView.RunComputeAsync(OutputElementType.Float32);
                if (outputBppValue == 8)
                {
                    accelerator.ShaderSource = VulkanOpenGLShaderColorSrcU8;
                    accelerator.OutputElementType = OutputElementType.UInt32;
                }
                else if (outputBppValue == 16)
                {
                    accelerator.ShaderSource = VulkanOpenGLShaderColorSrcU16;
                    accelerator.OutputElementType = OutputElementType.UInt32;
                }
                else
                {
                    accelerator.ShaderSource = VulkanOpenGLShaderColorSrc;
                    accelerator.OutputElementType = OutputElementType.Float32;
                }

                accelerator.Inputs = new float[][]
                {
                    aAlpha,
                    trimmedA,
                    bAlpha,
                    trimmedB
                };
                NativeVulkanSurfaceViewHandler.MapInputs(handler, accelerator);
                var colorResult = await vkView.RunComputeAsync(accelerator.OutputElementType);
                if (outputBppValue == 8)
                {
                    var colorU32 = (uint[])colorResult;
                    var outputU8 = new byte[colorU32.Length];
                    for (int i = 0; i < colorU32.Length; i++)
                    {
                        uint v = colorU32[i];
                        if (v > 255u)
                            v = 255u;
                        outputU8[i] = (byte)v;
                    }

                    return (outputU8, alphaResult);
                }

                if (outputBppValue == 16)
                {
                    var colorU32 = (uint[])colorResult;
                    var outputU16 = new ushort[colorU32.Length];
                    for (int i = 0; i < colorU32.Length; i++)
                    {
                        uint v = colorU32[i];
                        if (v > 65535u)
                            v = 65535u;
                        outputU16[i] = (ushort)v;
                    }

                    return (outputU16, alphaResult);
                }

                return ((float[])colorResult, alphaResult);
            }, "VulkanApproximateEffect timed out after 60 seconds - likely deadlock due to main thread congestion.");
        }

        private static readonly string VulkanOpenGLAlpha = OpenGLAlpha.Replace("#version 310 es", "#version 450").Replace("layout(std430,", "layout(set = 0, std430,").Replace("binding = 6", "binding = 2");
        private static readonly string VulkanOpenGLShaderColorSrc = OpenGLShaderColorSrc.Replace("#version 310 es", "#version 450").Replace("layout(std430,", "layout(set = 0, std430,").Replace("binding = 6", "binding = 4");
        private static readonly string VulkanOpenGLShaderColorSrcU16 = OpenGLShaderColorSrcU16.Replace("#version 310 es", "#version 450").Replace("layout(std430,", "layout(set = 0, std430,").Replace("binding = 6", "binding = 4");
        private static readonly string VulkanOpenGLShaderColorSrcU8 = OpenGLShaderColorSrcU8.Replace("#version 310 es", "#version 450").Replace("layout(std430,", "layout(set = 0, std430,").Replace("binding = 6", "binding = 4");

        private const string OpenGLAlpha = """
            #version 310 es
            layout(local_size_x = 256) in;

            layout(std430, binding = 0) buffer AAlphaBuffer { float aAlpha[]; };
            layout(std430, binding = 1) buffer BAlphaBuffer { float bAlpha[]; };
            layout(std430, binding = 6) buffer CAlphaBuffer { float cAlpha[]; };

            void main() {
                uint i = gl_GlobalInvocationID.x;
                if (i >= uint(aAlpha.length())) return;
                float aA = aAlpha[i];
                float bA = bAlpha[i];

                if (aA == 1.0) {
                    cAlpha[i] = 1.0;
                } else if (aA <= 0.05) {
                    cAlpha[i] = bA;
                } else {
                    float outA = aA + bA * (1.0 - aA);
                    if (outA < 1e-6) {
                        cAlpha[i] = 0.0;
                    } else {
                        cAlpha[i] = outA;
                    }
                }
            }
            """;
        private const string OpenGLShaderColorSrc = """
            #version 310 es
            layout(local_size_x = 256) in;

            layout(std430, binding = 0) buffer AAlphaBuffer { float aAlpha[]; };
            layout(std430, binding = 1) buffer ABuffer { float a []; };
            layout(std430, binding = 2) buffer BAlphaBuffer { float bAlpha []; };
            layout(std430, binding = 3) buffer BBuffer { float b []; };
            layout(std430, binding = 6) buffer CAlphaBuffer { float c []; };

            void main()
            {
                uint i = gl_GlobalInvocationID.x;
                if (i >= uint(aAlpha.length())) return;
                float aA = aAlpha[i];
                float bA = bAlpha[i];

                if (aA == 1.0)
                {
                    c[i] = a[i];
                }
                else if (aA <= 0.05)
                {
                    c[i] = b[i];
                }
                else
                {
                    float outA = aA + bA * (1.0 - aA);
                    if (outA < 1e-6)
                    {
                        c[i] = 0.0;
                    }
                    else
                    {
                        float aC = a[i] * aA / outA;
                        float bC = b[i] * bA * (1.0 - aA) / outA;
                        float outC = aC + bC;
                        outC = clamp(outC, 0.0, 65535.0);
                        c[i] = outC;
                    }
                }
            }
            """;
        private const string OpenGLShaderColorSrcU16 = """
            #version 310 es
            layout(local_size_x = 256) in;

            layout(std430, binding = 0) buffer AAlphaBuffer { float aAlpha[]; };
            layout(std430, binding = 1) buffer ABuffer { float a []; };
            layout(std430, binding = 2) buffer BAlphaBuffer { float bAlpha []; };
            layout(std430, binding = 3) buffer BBuffer { float b []; };
            layout(std430, binding = 6) buffer CBuffer { uint c []; };

            void main()
            {
                uint i = gl_GlobalInvocationID.x;
                if (i >= uint(aAlpha.length())) return;
                float aA = aAlpha[i];
                float bA = bAlpha[i];

                float outC;
                if (aA == 1.0)
                {
                    outC = a[i];
                }
                else if (aA <= 0.05)
                {
                    outC = b[i];
                }
                else
                {
                    float outA = aA + bA * (1.0 - aA);
                    if (outA < 1e-6)
                    {
                        outC = 0.0;
                    }
                    else
                    {
                        float aC = a[i] * aA / outA;
                        float bC = b[i] * bA * (1.0 - aA) / outA;
                        outC = aC + bC;
                    }
                }

                outC = clamp(outC, 0.0, 65535.0);
                c[i] = uint(outC + 0.5);
            }
            """;
        private const string OpenGLShaderColorSrcU8 = """
            #version 310 es
            layout(local_size_x = 256) in;

            layout(std430, binding = 0) buffer AAlphaBuffer { float aAlpha[]; };
            layout(std430, binding = 1) buffer ABuffer { float a []; };
            layout(std430, binding = 2) buffer BAlphaBuffer { float bAlpha []; };
            layout(std430, binding = 3) buffer BBuffer { float b []; };
            layout(std430, binding = 6) buffer CBuffer { uint c []; };

            void main()
            {
                uint i = gl_GlobalInvocationID.x;
                if (i >= uint(aAlpha.length())) return;
                float aA = aAlpha[i];
                float bA = bAlpha[i];

                float outC;
                if (aA == 1.0)
                {
                    outC = a[i];
                }
                else if (aA <= 0.05)
                {
                    outC = b[i];
                }
                else
                {
                    float outA = aA + bA * (1.0 - aA);
                    if (outA < 1e-6)
                    {
                        outC = 0.0;
                    }
                    else
                    {
                        float aC = a[i] * aA / outA;
                        float bC = b[i] * bA * (1.0 - aA) / outA;
                        outC = aC + bC;
                    }
                }

                outC = clamp(outC, 0.0, 65535.0);
                float out8 = outC / 257.0;
                out8 = clamp(out8, 0.0, 255.0);
                c[i] = uint(out8 + 0.5);
            }
            """;
    }
}
#endif
