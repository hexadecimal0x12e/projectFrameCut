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
    public partial class ScreenMixture_HwAccel
    {
        private readonly ComputeBackend backend = AndroidExecutionHelper.PreferredBackend;
        protected override (ushort[] Color, float[] Alpha) ComputeBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            using var scope = nativeLock.EnterScope();
            ObjectDisposedException.ThrowIf(disposed, this);
            var result = backend == ComputeBackend.Vulkan ? VulkanRunBlend(top, bottom, topAlpha, bottomAlpha, 16, pixelCount) : OpenGLRunBlend(top, bottom, topAlpha, bottomAlpha, 16, pixelCount);
            return ((ushort[])result.Color, result.Alpha);
        }

        private (Array Color, float[] Alpha) OpenGLRunBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int outputBpp, int pixelCount)
        {
            var topValue = top;
            var bottomValue = bottom;
            var topAlphaValue = topAlpha;
            var bottomAlphaValue = bottomAlpha;
            int outputBppValue = outputBpp;
            int actualPixels = pixelCount;
            return OpenGLSubmitBlend(topValue, bottomValue, topAlphaValue, bottomAlphaValue, outputBppValue, actualPixels, OpenGLBlendColorSrcScreen);
        }

        private static (Array Color, float[] Alpha) OpenGLSubmitBlend(float[] top, float[] bottom, float[]? topAlpha, float[]? bottomAlpha, int outputBpp, int actualPixels, string colorShader)
        {
            float[] trimmedTop = new float[actualPixels];
            float[] trimmedBottom = new float[actualPixels];
            Array.Copy(top, 0, trimmedTop, 0, actualPixels);
            Array.Copy(bottom, 0, trimmedBottom, 0, actualPixels);
            if (topAlpha == null)
            {
                topAlpha = new float[actualPixels];
                Array.Fill(topAlpha, 1f);
            }

            if (bottomAlpha == null)
            {
                bottomAlpha = new float[actualPixels];
                Array.Fill(bottomAlpha, 1f);
            }

            if (topAlpha.Length != actualPixels || bottomAlpha.Length != actualPixels)
            {
                float[] trimmedA = new float[actualPixels];
                float[] trimmedB = new float[actualPixels];
                Array.Copy(topAlpha, 0, trimmedA, 0, Math.Min(topAlpha.Length, actualPixels));
                Array.Copy(bottomAlpha, 0, trimmedB, 0, Math.Min(bottomAlpha.Length, actualPixels));
                topAlpha = trimmedA;
                bottomAlpha = trimmedB;
            }

            return AndroidExecutionHelper.EnqueueCompute<(Array Color, float[] Alpha)>(() =>
            {
                var mainThreadTask = MainThread.InvokeOnMainThreadAsync<(Array Color, float[] Alpha)>(async () =>
                {
                    NativeGLSurfaceView accelerator = new NativeGLSurfaceView
                    {
                        ShaderSource = OpenGLAlpha,
                        Inputs = new float[][]
                        {
                            topAlpha!,
                            bottomAlpha!
                        },
                        WidthRequest = 50,
                        HeightRequest = 50,
                        JobID = "BlendAlpha",
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
                    var alphaResult = (float[])await glView.RunComputeAsync(GLComputeView.OutputElementType.Float32);
                    accelerator.ShaderSource = colorShader;
                    accelerator.OutputElementType = GLComputeView.OutputElementType.Float32;
                    accelerator.Inputs = new float[][]
                    {
                        topAlpha!,
                        trimmedTop,
                        bottomAlpha!,
                        trimmedBottom
                    };
                    NativeGLSurfaceViewHandler.MapInputs(handler, accelerator);
                    var colorResult = (float[])await glView.RunComputeAsync(GLComputeView.OutputElementType.Float32);
                    if (outputBpp == 8)
                    {
                        var outputU8 = new byte[colorResult.Length];
                        for (int i = 0; i < colorResult.Length; i++)
                        {
                            float v = colorResult[i] / 257f;
                            if (v < 0f)
                                v = 0f;
                            if (v > 255f)
                                v = 255f;
                            outputU8[i] = (byte)v;
                        }

                        return (outputU8, alphaResult);
                    }

                    if (outputBpp == 16)
                    {
                        var outputU16 = new ushort[colorResult.Length];
                        for (int i = 0; i < colorResult.Length; i++)
                        {
                            float v = colorResult[i];
                            if (v < 0f)
                                v = 0f;
                            if (v > 65535f)
                                v = 65535f;
                            outputU16[i] = (ushort)v;
                        }

                        return (outputU16, alphaResult);
                    }

                    return (colorResult, alphaResult);
                });
                if (!mainThreadTask.Wait(TimeSpan.FromSeconds(60)))
                    throw new TimeoutException("BlendModeGLHelper.ComputeBlend timed out after 60 seconds.");
                var result = TaskHelper.SyncWait(() => mainThreadTask, CancellationToken.None);
                return result;
            });
        }

        private (Array Color, float[] Alpha) VulkanRunBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int outputBpp, int pixelCount)
        {
            var topValue = top;
            var bottomValue = bottom;
            var topAlphaValue = topAlpha;
            var bottomAlphaValue = bottomAlpha;
            int outputBppValue = outputBpp;
            int actualPixels = pixelCount;
            return VulkanSubmitBlend(topValue, bottomValue, topAlphaValue, bottomAlphaValue, outputBppValue, actualPixels, VulkanOpenGLBlendColorSrcScreen);
        }

        private static (Array Color, float[] Alpha) VulkanSubmitBlend(float[] top, float[] bottom, float[]? topAlpha, float[]? bottomAlpha, int outputBpp, int actualPixels, string colorShader)
        {
            float[] trimmedTop = new float[actualPixels];
            float[] trimmedBottom = new float[actualPixels];
            Array.Copy(top, 0, trimmedTop, 0, actualPixels);
            Array.Copy(bottom, 0, trimmedBottom, 0, actualPixels);
            if (topAlpha == null)
            {
                topAlpha = new float[actualPixels];
                Array.Fill(topAlpha, 1f);
            }

            if (bottomAlpha == null)
            {
                bottomAlpha = new float[actualPixels];
                Array.Fill(bottomAlpha, 1f);
            }

            if (topAlpha.Length != actualPixels || bottomAlpha.Length != actualPixels)
            {
                float[] trimmedA = new float[actualPixels];
                float[] trimmedB = new float[actualPixels];
                Array.Copy(topAlpha, 0, trimmedA, 0, Math.Min(topAlpha.Length, actualPixels));
                Array.Copy(bottomAlpha, 0, trimmedB, 0, Math.Min(bottomAlpha.Length, actualPixels));
                topAlpha = trimmedA;
                bottomAlpha = trimmedB;
            }

            return VulkanExecutionHelper.EnqueueCompute<(Array Color, float[] Alpha)>(async () =>
            {
                var(accelerator, handler, vkView) = await VulkanExecutionHelper.CreateAcceleratorAsync(VulkanOpenGLAlpha, new float[][] { topAlpha!, bottomAlpha! }, OutputElementType.Float32);
                using var viewScope = AndroidExecutionHelper.UseView(accelerator);
                var alphaResult = (float[])await vkView.RunComputeAsync(OutputElementType.Float32);
                accelerator.ShaderSource = colorShader;
                accelerator.OutputElementType = OutputElementType.Float32;
                accelerator.Inputs = new float[][]
                {
                    topAlpha!,
                    trimmedTop,
                    bottomAlpha!,
                    trimmedBottom
                };
                NativeVulkanSurfaceViewHandler.MapInputs(handler, accelerator);
                var colorResult = (float[])await vkView.RunComputeAsync(OutputElementType.Float32);
                if (outputBpp == 8)
                {
                    var outputU8 = new byte[colorResult.Length];
                    for (int i = 0; i < colorResult.Length; i++)
                    {
                        float v = colorResult[i] / 257f;
                        if (v < 0f)
                            v = 0f;
                        if (v > 255f)
                            v = 255f;
                        outputU8[i] = (byte)v;
                    }

                    return (outputU8, alphaResult);
                }

                if (outputBpp == 16)
                {
                    var outputU16 = new ushort[colorResult.Length];
                    for (int i = 0; i < colorResult.Length; i++)
                    {
                        float v = colorResult[i];
                        if (v < 0f)
                            v = 0f;
                        if (v > 65535f)
                            v = 65535f;
                        outputU16[i] = (ushort)v;
                    }

                    return (outputU16, alphaResult);
                }

                return (colorResult, alphaResult);
            }, "VulkanBlendMode.ComputeBlend timed out after 60 seconds.");
        }

        private static readonly string VulkanOpenGLAlpha = OpenGLAlpha.Replace("#version 310 es", "#version 450").Replace("layout(std430,", "layout(set = 0, std430,").Replace("binding = 6", "binding = 2");
        private static readonly string VulkanOpenGLBlendColorSrcScreen = OpenGLBlendColorSrcScreen.Replace("#version 310 es", "#version 450").Replace("layout(std430,", "layout(set = 0, std430,").Replace("binding = 6", "binding = 4");

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

                float outA = aA + bA * (1.0 - aA);
                cAlpha[i] = outA < 1e-6 ? 0.0 : outA;
            }
            """;
        private const string OpenGLBlendColorSrcScreen = """
            #version 310 es
            layout(local_size_x = 256) in;

            layout(std430, binding = 0) buffer AAlphaBuffer { float aAlpha[]; };
            layout(std430, binding = 1) buffer ABuffer { float a []; };
            layout(std430, binding = 2) buffer BAlphaBuffer { float bAlpha []; };
            layout(std430, binding = 3) buffer BBuffer { float b []; };
            layout(std430, binding = 6) buffer CBuffer { float c []; };

            void main() {
                uint i = gl_GlobalInvocationID.x;
                if (i >= uint(aAlpha.length())) return;
                float aA = aAlpha[i];
                float bA = bAlpha[i];
                float outA = aA + bA * (1.0 - aA);
                if (outA < 1e-6) {
                    c[i] = 0.0;
                } else {
                    float blended = 65535.0 - (65535.0 - a[i]) * (65535.0 - b[i]) / 65535.0;
                    float result = (blended * aA + b[i] * bA * (1.0 - aA)) / outA;
                    c[i] = clamp(result, 0.0, 65535.0);
                }
            }
            """;
    }
}
#endif
