using Android.Renderscripts;
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
using Xamarin.Google.Crypto.Tink.Annotations;

namespace projectFrameCut.Render.AndroidOpenGL.Platforms.Android
{
    internal static class ShaderLibrary
    {
        public const string Alpha =
            """
            #version 310 es            
            layout(local_size_x = 256) in;

            layout(std430, binding = 0) buffer AAlphaBuffer { float aAlpha[]; };
            layout(std430, binding = 1) buffer BAlphaBuffer { float bAlpha[]; };
            layout(std430, binding = 6) buffer CAlphaBuffer { float cAlpha[]; };

            void main() {
                uint i = gl_GlobalInvocationID.x;
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

        public const string ShaderColorSrc =
            """
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

        public const string ShaderColorSrcU16 =
            """
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

        public const string ShaderColorSrcU8 =
            """
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

        public const string BlendColorSrcAdd =
            """
            #version 310 es
            layout(local_size_x = 256) in;

            layout(std430, binding = 0) buffer AAlphaBuffer { float aAlpha[]; };
            layout(std430, binding = 1) buffer ABuffer { float a []; };
            layout(std430, binding = 2) buffer BAlphaBuffer { float bAlpha []; };
            layout(std430, binding = 3) buffer BBuffer { float b []; };
            layout(std430, binding = 6) buffer CBuffer { float c []; };

            void main() {
                uint i = gl_GlobalInvocationID.x;
                float aA = aAlpha[i];
                float bA = bAlpha[i];
                float outA = aA + bA * (1.0 - aA);
                if (outA < 1e-6) {
                    c[i] = 0.0;
                } else {
                    float blended = min(a[i] + b[i], 65535.0);
                    float result = (blended * aA + b[i] * bA * (1.0 - aA)) / outA;
                    c[i] = clamp(result, 0.0, 65535.0);
                }
            }
            """;

        public const string BlendColorSrcSubtract =
            """
            #version 310 es
            layout(local_size_x = 256) in;

            layout(std430, binding = 0) buffer AAlphaBuffer { float aAlpha[]; };
            layout(std430, binding = 1) buffer ABuffer { float a []; };
            layout(std430, binding = 2) buffer BAlphaBuffer { float bAlpha []; };
            layout(std430, binding = 3) buffer BBuffer { float b []; };
            layout(std430, binding = 6) buffer CBuffer { float c []; };

            void main() {
                uint i = gl_GlobalInvocationID.x;
                float aA = aAlpha[i];
                float bA = bAlpha[i];
                float outA = aA + bA * (1.0 - aA);
                if (outA < 1e-6) {
                    c[i] = 0.0;
                } else {
                    float blended = max(b[i] - a[i], 0.0);
                    float result = (blended * aA + b[i] * bA * (1.0 - aA)) / outA;
                    c[i] = clamp(result, 0.0, 65535.0);
                }
            }
            """;

        public const string BlendColorSrcMultiply =
            """
            #version 310 es
            layout(local_size_x = 256) in;

            layout(std430, binding = 0) buffer AAlphaBuffer { float aAlpha[]; };
            layout(std430, binding = 1) buffer ABuffer { float a []; };
            layout(std430, binding = 2) buffer BAlphaBuffer { float bAlpha []; };
            layout(std430, binding = 3) buffer BBuffer { float b []; };
            layout(std430, binding = 6) buffer CBuffer { float c []; };

            void main() {
                uint i = gl_GlobalInvocationID.x;
                float aA = aAlpha[i];
                float bA = bAlpha[i];
                float outA = aA + bA * (1.0 - aA);
                if (outA < 1e-6) {
                    c[i] = 0.0;
                } else {
                    float blended = a[i] * b[i] / 65535.0;
                    float result = (blended * aA + b[i] * bA * (1.0 - aA)) / outA;
                    c[i] = clamp(result, 0.0, 65535.0);
                }
            }
            """;

        public const string BlendColorSrcScreen =
            """
            #version 310 es
            layout(local_size_x = 256) in;

            layout(std430, binding = 0) buffer AAlphaBuffer { float aAlpha[]; };
            layout(std430, binding = 1) buffer ABuffer { float a []; };
            layout(std430, binding = 2) buffer BAlphaBuffer { float bAlpha []; };
            layout(std430, binding = 3) buffer BBuffer { float b []; };
            layout(std430, binding = 6) buffer CBuffer { float c []; };

            void main() {
                uint i = gl_GlobalInvocationID.x;
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

        public const string BlendColorSrcOverlayBlend =
            """
            #version 310 es
            layout(local_size_x = 256) in;

            layout(std430, binding = 0) buffer AAlphaBuffer { float aAlpha[]; };
            layout(std430, binding = 1) buffer ABuffer { float a []; };
            layout(std430, binding = 2) buffer BAlphaBuffer { float bAlpha []; };
            layout(std430, binding = 3) buffer BBuffer { float b []; };
            layout(std430, binding = 6) buffer CBuffer { float c []; };

            void main() {
                uint i = gl_GlobalInvocationID.x;
                float aA = aAlpha[i];
                float bA = bAlpha[i];
                float outA = aA + bA * (1.0 - aA);
                if (outA < 1e-6) {
                    c[i] = 0.0;
                } else {
                    float blended;
                    if (b[i] < 32768.0)
                        blended = 2.0 * a[i] * b[i] / 65535.0;
                    else
                        blended = 65535.0 - 2.0 * (65535.0 - a[i]) * (65535.0 - b[i]) / 65535.0;
                    float result = (blended * aA + b[i] * bA * (1.0 - aA)) / outA;
                    c[i] = clamp(result, 0.0, 65535.0);
                }
            }
            """;

        public const string BlendColorSrcDarken =
            """
            #version 310 es
            layout(local_size_x = 256) in;

            layout(std430, binding = 0) buffer AAlphaBuffer { float aAlpha[]; };
            layout(std430, binding = 1) buffer ABuffer { float a []; };
            layout(std430, binding = 2) buffer BAlphaBuffer { float bAlpha []; };
            layout(std430, binding = 3) buffer BBuffer { float b []; };
            layout(std430, binding = 6) buffer CBuffer { float c []; };

            void main() {
                uint i = gl_GlobalInvocationID.x;
                float aA = aAlpha[i];
                float bA = bAlpha[i];
                float outA = aA + bA * (1.0 - aA);
                if (outA < 1e-6) {
                    c[i] = 0.0;
                } else {
                    float blended = min(a[i], b[i]);
                    float result = (blended * aA + b[i] * bA * (1.0 - aA)) / outA;
                    c[i] = clamp(result, 0.0, 65535.0);
                }
            }
            """;

        public const string BlendColorSrcLighten =
            """
            #version 310 es
            layout(local_size_x = 256) in;

            layout(std430, binding = 0) buffer AAlphaBuffer { float aAlpha[]; };
            layout(std430, binding = 1) buffer ABuffer { float a []; };
            layout(std430, binding = 2) buffer BAlphaBuffer { float bAlpha []; };
            layout(std430, binding = 3) buffer BBuffer { float b []; };
            layout(std430, binding = 6) buffer CBuffer { float c []; };

            void main() {
                uint i = gl_GlobalInvocationID.x;
                float aA = aAlpha[i];
                float bA = bAlpha[i];
                float outA = aA + bA * (1.0 - aA);
                if (outA < 1e-6) {
                    c[i] = 0.0;
                } else {
                    float blended = max(a[i], b[i]);
                    float result = (blended * aA + b[i] * bA * (1.0 - aA)) / outA;
                    c[i] = clamp(result, 0.0, 65535.0);
                }
            }
            """;

        public const string BlendColorSrcDifference =
            """
            #version 310 es
            layout(local_size_x = 256) in;

            layout(std430, binding = 0) buffer AAlphaBuffer { float aAlpha[]; };
            layout(std430, binding = 1) buffer ABuffer { float a []; };
            layout(std430, binding = 2) buffer BAlphaBuffer { float bAlpha []; };
            layout(std430, binding = 3) buffer BBuffer { float b []; };
            layout(std430, binding = 6) buffer CBuffer { float c []; };

            void main() {
                uint i = gl_GlobalInvocationID.x;
                float aA = aAlpha[i];
                float bA = bAlpha[i];
                float outA = aA + bA * (1.0 - aA);
                if (outA < 1e-6) {
                    c[i] = 0.0;
                } else {
                    float blended = abs(a[i] - b[i]);
                    float result = (blended * aA + b[i] * bA * (1.0 - aA)) / outA;
                    c[i] = clamp(result, 0.0, 65535.0);
                }
            }
            """;

        public static Lock locker = new();

    }

    public class OverlayComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.OpenGLComputers";
        public string SupportedEffectOrMixture => "Overlay";

        public object[] Compute(object[] args)
        {


            // args: [A, B, aAlpha, bAlpha, outputBpp, basePixels]
            var A = args[0] as float[];
            var B = args[1] as float[];
            var aAlpha = args.Length > 2 ? (args[2] as float[]) : null;
            var bAlpha = args.Length > 3 ? (args[3] as float[]) : null;
            var outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            var actualPixels = args.Length > 5 ? Convert.ToInt32(args[5]) : A?.Length ?? 0;

            // A and B may be larger than actualPixels due to ArrayPool.Rent
            // Trim them to the actual size to match alpha arrays
            float[] trimmedA = new float[actualPixels];
            float[] trimmedB = new float[actualPixels];
            Array.Copy(A!, 0, trimmedA, 0, actualPixels);
            Array.Copy(B!, 0, trimmedB, 0, actualPixels);

            // Ensure alpha arrays match the actual pixel count
            if (aAlpha == null) aAlpha = Enumerable.Repeat(1f, actualPixels).ToArray();
            if (bAlpha == null) bAlpha = Enumerable.Repeat(1f, actualPixels).ToArray();

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
            Logger.LogDiagnostic($"[OverlayComputer] Input lengths normalized: actualPixels={actualPixels}, A={(A?.Length ?? 0)}, B={(B?.Length ?? 0)}, trimmedA={trimmedA.Length}, trimmedB={trimmedB.Length}, aAlpha={aAlpha.Length}, bAlpha={bAlpha.Length}, outputBpp={outputBpp}");
#endif

            return ComputerHelper.EnqueueCompute(() =>
            {
                // We need to run on MainThread because we are touching UI elements (NativeGLSurfaceView)
                // Use GetAwaiter().GetResult() with timeout to avoid deadlock when main thread is busy
                var mainThreadTask = MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    NativeGLSurfaceView accelerator = new NativeGLSurfaceView
                    {
                        ShaderSource = ShaderLibrary.Alpha,
                        Inputs = new float[][] { aAlpha, bAlpha },
                        WidthRequest = 50,
                        HeightRequest = 50,
                        JobID = "OverlayComputer",
                        OutputElementType = GLComputeView.OutputElementType.Float32
                    };

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

                    ComputerHelper.AddPlatformComputeViewHandler?.Invoke(accelerator);

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
                    if (await Task.WhenAny(readyTask, Task.Delay(TimeSpan.FromMilliseconds(ComputerHelper.Timeout))) != readyTask)
                        throw new TimeoutException($"GLComputeView.WaitUntilReadyAsync timed out after {ComputerHelper.Timeout}ms.");
                    await readyTask; // Propagate any exception

                    var alphaResult = (float[])await glView.RunComputeAsync(GLComputeView.OutputElementType.Float32);

                    // 2. Compute Color
                    if (outputBpp == 8)
                    {
                        accelerator.ShaderSource = ShaderLibrary.ShaderColorSrcU8;
                        accelerator.OutputElementType = GLComputeView.OutputElementType.UInt32;
                    }
                    else if (outputBpp == 16)
                    {
                        accelerator.ShaderSource = ShaderLibrary.ShaderColorSrcU16;
                        accelerator.OutputElementType = GLComputeView.OutputElementType.UInt32;
                    }
                    else
                    {
                        accelerator.ShaderSource = ShaderLibrary.ShaderColorSrc;
                        accelerator.OutputElementType = GLComputeView.OutputElementType.Float32;
                    }
                    accelerator.Inputs = new float[][] { aAlpha, trimmedA, bAlpha, trimmedB };
                    NativeGLSurfaceViewHandler.MapInputs(handler, accelerator);

                    var colorResult = await glView.RunComputeAsync(accelerator.OutputElementType);

                    if (outputBpp == 8)
                    {
                        var colorU32 = (uint[])colorResult;
                        var outputU8 = new byte[colorU32.Length];
                        for (int i = 0; i < colorU32.Length; i++)
                        {
                            uint v = colorU32[i];
                            if (v > 255u) v = 255u;
                            outputU8[i] = (byte)v;
                        }
                        return new object[] { outputU8, alphaResult };
                    }
                    if (outputBpp == 16)
                    {
                        var colorU32 = (uint[])colorResult;
                        var outputU16 = new ushort[colorU32.Length];
                        for (int i = 0; i < colorU32.Length; i++)
                        {
                            uint v = colorU32[i];
                            if (v > 65535u) v = 65535u;
                            outputU16[i] = (ushort)v;
                        }
                        return new object[] { outputU16, alphaResult };
                    }

                    return new object[] { (float[])colorResult, alphaResult };
                });

                // Use Task.Wait with timeout instead of .Result to detect deadlocks
                if (!mainThreadTask.Wait(TimeSpan.FromSeconds(60)))
                {
                    throw new TimeoutException($"OverlayComputer.Compute timed out after 60 seconds - likely deadlock due to main thread congestion. Consider reducing MaxThreads on Android.");
                }

                var result = (object[])TaskHelper.SyncWait(() => mainThreadTask, CancellationToken.None);

                if (result is null)
                    throw new InvalidOperationException($"OverlayComputer Compute failed: accelerator returned null result.");

                return result;
            });

        }




    }

    public class ApproximateOverlayComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.OpenGLComputers";
        public string SupportedEffectOrMixture => "OverlayApproximate";

        public object[] Compute(object[] args)
        {

            // args: [A, B, aAlpha, bAlpha, outputBpp, basePixels]
            var A = args[0] as float[];
            var B = args[1] as float[];
            var aAlpha = args.Length > 2 ? (args[2] as float[]) : null;
            var bAlpha = args.Length > 3 ? (args[3] as float[]) : null;
            var outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            var actualPixels = args.Length > 5 ? Convert.ToInt32(args[5]) : A?.Length ?? 0;

            // A and B may be larger than actualPixels due to ArrayPool.Rent
            // Trim them to the actual size to match alpha arrays
            float[] trimmedA = new float[actualPixels];
            float[] trimmedB = new float[actualPixels];
            Array.Copy(A!, 0, trimmedA, 0, actualPixels);
            Array.Copy(B!, 0, trimmedB, 0, actualPixels);

            // Ensure alpha arrays match the actual pixel count
            if (aAlpha == null) aAlpha = Enumerable.Repeat(1f, actualPixels).ToArray();
            if (bAlpha == null) bAlpha = Enumerable.Repeat(1f, actualPixels).ToArray();

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
            Logger.LogDiagnostic($"[ApproximateComputer] Input lengths normalized: actualPixels={actualPixels}, A={(A?.Length ?? 0)}, B={(B?.Length ?? 0)}, trimmedA={trimmedA.Length}, trimmedB={trimmedB.Length}, aAlpha={aAlpha.Length}, bAlpha={bAlpha.Length}, outputBpp={outputBpp}");
#endif

            return ComputerHelper.EnqueueCompute(() =>
            {
                // We need to run on MainThread because we are touching UI elements (NativeGLSurfaceView)
                // Use GetAwaiter().GetResult() with timeout to avoid deadlock when main thread is busy
                var mainThreadTask = MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    NativeGLSurfaceView accelerator = new NativeGLSurfaceView
                    {
                        ShaderSource = ShaderLibrary.Alpha,
                        Inputs = new float[][] { aAlpha, bAlpha },
                        WidthRequest = 50,
                        HeightRequest = 50,
                        JobID = "ApproximateComputer",
                        OutputElementType = GLComputeView.OutputElementType.Float32
                    };

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

                    ComputerHelper.AddPlatformComputeViewHandler?.Invoke(accelerator);

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
                    if (await Task.WhenAny(readyTask, Task.Delay(TimeSpan.FromMilliseconds(ComputerHelper.Timeout))) != readyTask)
                        throw new TimeoutException($"GLComputeView.WaitUntilReadyAsync timed out after {ComputerHelper.Timeout}ms.");
                    await readyTask; // Propagate any exception

                    var alphaResult = (float[])await glView.RunComputeAsync(GLComputeView.OutputElementType.Float32);

                    // 2. Compute Color
                    if (outputBpp == 8)
                    {
                        accelerator.ShaderSource = ShaderLibrary.ShaderColorSrcU8;
                        accelerator.OutputElementType = GLComputeView.OutputElementType.UInt32;
                    }
                    else if (outputBpp == 16)
                    {
                        accelerator.ShaderSource = ShaderLibrary.ShaderColorSrcU16;
                        accelerator.OutputElementType = GLComputeView.OutputElementType.UInt32;
                    }
                    else
                    {
                        accelerator.ShaderSource = ShaderLibrary.ShaderColorSrc;
                        accelerator.OutputElementType = GLComputeView.OutputElementType.Float32;
                    }
                    accelerator.Inputs = new float[][] { aAlpha, trimmedA, bAlpha, trimmedB };
                    NativeGLSurfaceViewHandler.MapInputs(handler, accelerator);

                    var colorResult = await glView.RunComputeAsync(accelerator.OutputElementType);

                    if (outputBpp == 8)
                    {
                        var colorU32 = (uint[])colorResult;
                        var outputU8 = new byte[colorU32.Length];
                        for (int i = 0; i < colorU32.Length; i++)
                        {
                            uint v = colorU32[i];
                            if (v > 255u) v = 255u;
                            outputU8[i] = (byte)v;
                        }
                        return new object[] { outputU8, alphaResult };
                    }
                    if (outputBpp == 16)
                    {
                        var colorU32 = (uint[])colorResult;
                        var outputU16 = new ushort[colorU32.Length];
                        for (int i = 0; i < colorU32.Length; i++)
                        {
                            uint v = colorU32[i];
                            if (v > 65535u) v = 65535u;
                            outputU16[i] = (ushort)v;
                        }
                        return new object[] { outputU16, alphaResult };
                    }

                    return new object[] { (float[])colorResult, alphaResult };
                });

                // Use Task.Wait with timeout instead of .Result to detect deadlocks
                if (!mainThreadTask.Wait(TimeSpan.FromSeconds(60)))
                {
                    throw new TimeoutException($"ApproximateComputer.Compute timed out after 60 seconds - likely deadlock due to main thread congestion. Consider reducing MaxThreads on Android.");
                }

                var result = (object[])TaskHelper.SyncWait(() => mainThreadTask, CancellationToken.None);

                if (result is null)
                    throw new InvalidOperationException($"ApproximateComputer Compute failed: accelerator returned null result.");

                return result;
            });

        }



    }

    public class ResizeComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.OpenGLComputers";
        public string SupportedEffectOrMixture => "Resize";

        public object[] Compute(object[] args)
        {
            var rIn = args[0] as float[] ?? throw new ArgumentException("Invalid argument for R");
            var gIn = args[1] as float[] ?? throw new ArgumentException("Invalid argument for G");
            var bIn = args[2] as float[] ?? throw new ArgumentException("Invalid argument for B");
            var aIn = args[3] as float[] ?? throw new ArgumentException("Invalid argument for A");

            int srcW = Convert.ToInt32((float)args[4]);
            int srcH = Convert.ToInt32((float)args[5]);
            int dstW = Convert.ToInt32((float)args[6]);
            int dstH = Convert.ToInt32((float)args[7]);

            if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0)
            {
                return [Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>()];
            }

            int srcLength = checked(srcW * srcH);
            int dstLength = checked(dstW * dstH);
            int glLength = Math.Max(srcLength, dstLength);

            float[] rPad = PreparePaddedChannel(rIn, srcLength, glLength);
            float[] gPad = PreparePaddedChannel(gIn, srcLength, glLength);
            float[] bPad = PreparePaddedChannel(bIn, srcLength, glLength);
            float[] aPad = PreparePaddedChannel(aIn, srcLength, glLength);

            float ratioX = (float)srcW / dstW;
            float ratioY = (float)srcH / dstH;

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
                        outputData[i] = 0.0;
                        return;
                    }

                    int x = int(i % uint({{dstW}}));
                    int y = int(i / uint({{dstW}}));

                    int srcX = int(float(x) * {{ratioX.ToString(System.Globalization.CultureInfo.InvariantCulture)}});
                    int srcY = int(float(y) * {{ratioY.ToString(System.Globalization.CultureInfo.InvariantCulture)}});

                    if (srcX >= {{srcW}}) srcX = {{srcW - 1}};
                    if (srcY >= {{srcH}}) srcY = {{srcH - 1}};
                    if (srcX < 0) srcX = 0;
                    if (srcY < 0) srcY = 0;

                    int srcIdx = srcY * {{srcW}} + srcX;
                    outputData[i] = inputData[srcIdx];
                }
                """;

            return ComputerHelper.EnqueueCompute(() =>
            {
                var mainThreadTask = MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    NativeGLSurfaceView accelerator = new NativeGLSurfaceView
                    {
                        ShaderSource = shader,
                        Inputs = new float[][] { rPad },
                        WidthRequest = 50,
                        HeightRequest = 50,
                        JobID = "ResizeComputer",
                        OutputElementType = GLComputeView.OutputElementType.Float32
                    };

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

                    ComputerHelper.AddPlatformComputeViewHandler?.Invoke(accelerator);

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
                    if (await Task.WhenAny(readyTask, Task.Delay(TimeSpan.FromMilliseconds(ComputerHelper.Timeout))) != readyTask)
                        throw new TimeoutException($"GLComputeView.WaitUntilReadyAsync timed out after {ComputerHelper.Timeout}ms.");
                    await readyTask;

                    async Task<float[]> RunChannel(float[] channel, string jobId)
                    {
                        accelerator.JobID = jobId;
                        accelerator.Inputs = new float[][] { channel };
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

                    var rOut = await RunChannel(rPad, "ResizeComputer-R");
                    var gOut = await RunChannel(gPad, "ResizeComputer-G");
                    var bOut = await RunChannel(bPad, "ResizeComputer-B");
                    var aOut = await RunChannel(aPad, "ResizeComputer-A");

                    return new object[] { rOut, gOut, bOut, aOut };
                });

                if (!mainThreadTask.Wait(TimeSpan.FromSeconds(60)))
                {
                    throw new TimeoutException("ResizeComputer.Compute timed out after 60 seconds - likely deadlock due to main thread congestion.");
                }

                var result = (object[])TaskHelper.SyncWait(() => mainThreadTask, CancellationToken.None);
                if (result is null)
                {
                    throw new InvalidOperationException("ResizeComputer Compute failed: accelerator returned null result.");
                }

                return result;
            });
        }

        private static float[] PreparePaddedChannel(float[] source, int sourceLength, int paddedLength)
        {
            var arr = new float[paddedLength];
            Array.Copy(source, 0, arr, 0, Math.Min(Math.Min(source.Length, sourceLength), paddedLength));
            return arr;
        }
    }

    public class CropComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.OpenGLComputers";
        public string SupportedEffectOrMixture => "Crop";

        public object[] Compute(object[] args)
        {
            var rIn = args[0] as float[] ?? throw new ArgumentException("Invalid argument for R");
            var gIn = args[1] as float[] ?? throw new ArgumentException("Invalid argument for G");
            var bIn = args[2] as float[] ?? throw new ArgumentException("Invalid argument for B");
            var aIn = args[3] as float[] ?? throw new ArgumentException("Invalid argument for A");

            int srcW = Convert.ToInt32(args[4]);
            int srcH = Convert.ToInt32(args[5]);
            int startX = Convert.ToInt32(args[6]);
            int startY = Convert.ToInt32(args[7]);
            int cropW = Convert.ToInt32(args[8]);
            int cropH = Convert.ToInt32(args[9]);

            if (srcW <= 0 || srcH <= 0 || cropW <= 0 || cropH <= 0)
            {
                return [Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>()];
            }

            int srcLength = checked(srcW * srcH);
            int dstLength = checked(cropW * cropH);
            int glLength = Math.Max(srcLength, dstLength);

            float[] rPad = PreparePaddedChannel(rIn, srcLength, glLength);
            float[] gPad = PreparePaddedChannel(gIn, srcLength, glLength);
            float[] bPad = PreparePaddedChannel(bIn, srcLength, glLength);
            float[] aPad = PreparePaddedChannel(aIn, srcLength, glLength);

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
                        outputData[i] = 0.0;
                        return;
                    }

                    int x = int(i % uint({{cropW}}));
                    int y = int(i / uint({{cropW}}));
                    int srcX = {{startX}} + x;
                    int srcY = {{startY}} + y;

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

            return ComputerHelper.EnqueueCompute(() =>
            {
                var mainThreadTask = MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    NativeGLSurfaceView accelerator = new NativeGLSurfaceView
                    {
                        ShaderSource = shader,
                        Inputs = new float[][] { rPad },
                        WidthRequest = 50,
                        HeightRequest = 50,
                        JobID = "CropComputer",
                        OutputElementType = GLComputeView.OutputElementType.Float32
                    };

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

                    ComputerHelper.AddPlatformComputeViewHandler?.Invoke(accelerator);

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
                    if (await Task.WhenAny(readyTask, Task.Delay(TimeSpan.FromMilliseconds(ComputerHelper.Timeout))) != readyTask)
                        throw new TimeoutException($"GLComputeView.WaitUntilReadyAsync timed out after {ComputerHelper.Timeout}ms.");
                    await readyTask;

                    async Task<float[]> RunChannel(float[] channel, string jobId)
                    {
                        accelerator.JobID = jobId;
                        accelerator.Inputs = new float[][] { channel };
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

                    var rOut = await RunChannel(rPad, "CropComputer-R");
                    var gOut = await RunChannel(gPad, "CropComputer-G");
                    var bOut = await RunChannel(bPad, "CropComputer-B");
                    var aOut = await RunChannel(aPad, "CropComputer-A");

                    return new object[] { rOut, gOut, bOut, aOut };
                });

                if (!mainThreadTask.Wait(TimeSpan.FromSeconds(60)))
                {
                    throw new TimeoutException("CropComputer.Compute timed out after 60 seconds - likely deadlock due to main thread congestion.");
                }

                var result = (object[])TaskHelper.SyncWait(() => mainThreadTask, CancellationToken.None);
                if (result is null)
                {
                    throw new InvalidOperationException("CropComputer Compute failed: accelerator returned null result.");
                }

                return result;
            });
        }

        private static float[] PreparePaddedChannel(float[] source, int sourceLength, int paddedLength)
        {
            var arr = new float[paddedLength];
            Array.Copy(source, 0, arr, 0, Math.Min(Math.Min(source.Length, sourceLength), paddedLength));
            return arr;
        }
    }

    public class PlaceComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.OpenGLComputers";
        public string SupportedEffectOrMixture => "Place";

        public object[] Compute(object[] args)
        {
            var rIn = args[0] as float[] ?? throw new ArgumentException("Invalid argument for R");
            var gIn = args[1] as float[] ?? throw new ArgumentException("Invalid argument for G");
            var bIn = args[2] as float[] ?? throw new ArgumentException("Invalid argument for B");
            var aIn = args[3] as float[] ?? throw new ArgumentException("Invalid argument for A");

            int srcW = Convert.ToInt32(args[4]);
            int srcH = Convert.ToInt32(args[5]);
            int startX = Convert.ToInt32(args[6]);
            int startY = Convert.ToInt32(args[7]);
            int dstW = Convert.ToInt32(args[8]);
            int dstH = Convert.ToInt32(args[9]);

            if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0)
            {
                return [Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>()];
            }

            int srcLength = checked(srcW * srcH);
            int dstLength = checked(dstW * dstH);
            int glLength = Math.Max(srcLength, dstLength);

            float[] rPad = PreparePaddedChannel(rIn, srcLength, glLength);
            float[] gPad = PreparePaddedChannel(gIn, srcLength, glLength);
            float[] bPad = PreparePaddedChannel(bIn, srcLength, glLength);
            float[] aPad = PreparePaddedChannel(aIn, srcLength, glLength);

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
                        outputData[i] = 0.0;
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

            return ComputerHelper.EnqueueCompute(() =>
            {
                var mainThreadTask = MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    NativeGLSurfaceView accelerator = new NativeGLSurfaceView
                    {
                        ShaderSource = shader,
                        Inputs = new float[][] { rPad },
                        WidthRequest = 50,
                        HeightRequest = 50,
                        JobID = "PlaceComputer",
                        OutputElementType = GLComputeView.OutputElementType.Float32
                    };

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

                    ComputerHelper.AddPlatformComputeViewHandler?.Invoke(accelerator);

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
                    if (await Task.WhenAny(readyTask, Task.Delay(TimeSpan.FromMilliseconds(ComputerHelper.Timeout))) != readyTask)
                        throw new TimeoutException($"GLComputeView.WaitUntilReadyAsync timed out after {ComputerHelper.Timeout}ms.");
                    await readyTask;

                    async Task<float[]> RunChannel(float[] channel, string jobId)
                    {
                        accelerator.JobID = jobId;
                        accelerator.Inputs = new float[][] { channel };
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

                    var rOut = await RunChannel(rPad, "PlaceComputer-R");
                    var gOut = await RunChannel(gPad, "PlaceComputer-G");
                    var bOut = await RunChannel(bPad, "PlaceComputer-B");
                    var aOut = await RunChannel(aPad, "PlaceComputer-A");

                    return new object[] { rOut, gOut, bOut, aOut };
                });

                if (!mainThreadTask.Wait(TimeSpan.FromSeconds(60)))
                {
                    throw new TimeoutException("PlaceComputer.Compute timed out after 60 seconds - likely deadlock due to main thread congestion.");
                }

                var result = (object[])TaskHelper.SyncWait(() => mainThreadTask, CancellationToken.None);
                if (result is null)
                {
                    throw new InvalidOperationException("PlaceComputer Compute failed: accelerator returned null result.");
                }

                return result;
            });
        }

        private static float[] PreparePaddedChannel(float[] source, int sourceLength, int paddedLength)
        {
            var arr = new float[paddedLength];
            Array.Copy(source, 0, arr, 0, Math.Min(Math.Min(source.Length, sourceLength), paddedLength));
            return arr;
        }
    }

    public class RemoveColorComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.OpenGLComputers";
        public string SupportedEffectOrMixture => "RemoveColor";

        public object[] Compute(object[] args)
        {
            // args: [aR, aG, aB, sourceA, [toRemoveR], [toRemoveG], [toRemoveB], [range], [actualPixels]]
            var aR = args[0] as float[];
            var aG = args[1] as float[];
            var aB = args[2] as float[];
            var sourceA = args[3] as float[];

            if (aR is null || aG is null || aB is null || sourceA is null)
            {
                throw new ArgumentException("RemoveColorComputer expects four float[] channel arrays.");
            }

            var toRemoveR = (ushort)args[4];
            var toRemoveG = (ushort)args[5];
            var toRemoveB = (ushort)args[6];
            var range = (ushort)args[7];

            // Validate all input arrays have the same length
            if (aR.Length != aG.Length || aR.Length != aB.Length || aR.Length != sourceA.Length)
            {
                throw new InvalidDataException($"Input array length mismatch: aR={aR.Length}, aG={aG.Length}, aB={aB.Length}, sourceA={sourceA.Length}");
            }

            int lowR = Math.Max(0, toRemoveR - range);
            int highR = Math.Min(65535, toRemoveR + range);
            int lowG = Math.Max(0, toRemoveG - range);
            int highG = Math.Min(65535, toRemoveG + range);
            int lowB = Math.Max(0, toRemoveB - range);
            int highB = Math.Min(65535, toRemoveB + range);

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
            return ComputerHelper.EnqueueCompute(() =>
            {
                var mainThreadTask = MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    NativeGLSurfaceView accelerator = new NativeGLSurfaceView
                    {
                        ShaderSource = shader,
                        Inputs = new float[][] { aR, aG, aB, sourceA },
                        WidthRequest = 50,
                        HeightRequest = 50,
                        JobID = "RemoveColorComputer",
                        OutputElementType = GLComputeView.OutputElementType.Float32
                    };

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

                    ComputerHelper.AddPlatformComputeViewHandler?.Invoke(accelerator);

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
                    if (await Task.WhenAny(readyTask, Task.Delay(TimeSpan.FromMilliseconds(ComputerHelper.Timeout))) != readyTask)
                        throw new TimeoutException($"GLComputeView.WaitUntilReadyAsync timed out after {ComputerHelper.Timeout}ms.");
                    await readyTask; // Propagate any exception

                    return await glView.RunComputeAsync(GLComputeView.OutputElementType.Float32);
                });

                // Use Task.Wait with timeout instead of .Result to detect deadlocks
                if (!mainThreadTask.Wait(TimeSpan.FromSeconds(60)))
                {
                    throw new TimeoutException($"RemoveColorComputer.Compute timed out after 60 seconds - likely deadlock due to main thread congestion. Consider reducing MaxThreads on Android.");
                }

                var result = TaskHelper.SyncWait(() => mainThreadTask, CancellationToken.None);

                if (result is null)
                    throw new InvalidOperationException($"RemoveColorComputer Compute failed: accelerator returned null result.");

                return [result];
            });
        }
    }

    internal static class BlendModeGLHelper
    {
        public static object[] ComputeBlend(
            float[] top, float[] bottom, float[]? topAlpha, float[]? bottomAlpha,
            int outputBpp, int actualPixels,
            string colorShader)
        {
            float[] trimmedTop = new float[actualPixels];
            float[] trimmedBottom = new float[actualPixels];
            Array.Copy(top, 0, trimmedTop, 0, actualPixels);
            Array.Copy(bottom, 0, trimmedBottom, 0, actualPixels);

            if (topAlpha == null) { topAlpha = new float[actualPixels]; Array.Fill(topAlpha, 1f); }
            if (bottomAlpha == null) { bottomAlpha = new float[actualPixels]; Array.Fill(bottomAlpha, 1f); }

            if (topAlpha.Length != actualPixels || bottomAlpha.Length != actualPixels)
            {
                float[] trimmedA = new float[actualPixels];
                float[] trimmedB = new float[actualPixels];
                Array.Copy(topAlpha, 0, trimmedA, 0, Math.Min(topAlpha.Length, actualPixels));
                Array.Copy(bottomAlpha, 0, trimmedB, 0, Math.Min(bottomAlpha.Length, actualPixels));
                topAlpha = trimmedA;
                bottomAlpha = trimmedB;
            }

            return ComputerHelper.EnqueueCompute(() =>
            {
                var mainThreadTask = MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    NativeGLSurfaceView accelerator = new NativeGLSurfaceView
                    {
                        ShaderSource = ShaderLibrary.Alpha,
                        Inputs = new float[][] { topAlpha!, bottomAlpha! },
                        WidthRequest = 50,
                        HeightRequest = 50,
                        JobID = "BlendAlpha",
                        OutputElementType = GLComputeView.OutputElementType.Float32
                    };

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
                    ComputerHelper.AddPlatformComputeViewHandler?.Invoke(accelerator);

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
                    if (await Task.WhenAny(readyTask, Task.Delay(TimeSpan.FromMilliseconds(ComputerHelper.Timeout))) != readyTask)
                        throw new TimeoutException($"GLComputeView.WaitUntilReadyAsync timed out after {ComputerHelper.Timeout}ms.");
                    await readyTask;

                    var alphaResult = (float[])await glView.RunComputeAsync(GLComputeView.OutputElementType.Float32);

                    accelerator.ShaderSource = colorShader;
                    accelerator.OutputElementType = GLComputeView.OutputElementType.Float32;
                    accelerator.Inputs = new float[][] { topAlpha!, trimmedTop, bottomAlpha!, trimmedBottom };
                    NativeGLSurfaceViewHandler.MapInputs(handler, accelerator);

                    var colorResult = (float[])await glView.RunComputeAsync(GLComputeView.OutputElementType.Float32);

                    if (outputBpp == 8)
                    {
                        var outputU8 = new byte[colorResult.Length];
                        for (int i = 0; i < colorResult.Length; i++)
                        {
                            float v = colorResult[i] / 257f;
                            if (v < 0f) v = 0f; if (v > 255f) v = 255f;
                            outputU8[i] = (byte)v;
                        }
                        return new object[] { outputU8, alphaResult };
                    }
                    if (outputBpp == 16)
                    {
                        var outputU16 = new ushort[colorResult.Length];
                        for (int i = 0; i < colorResult.Length; i++)
                        {
                            float v = colorResult[i];
                            if (v < 0f) v = 0f; if (v > 65535f) v = 65535f;
                            outputU16[i] = (ushort)v;
                        }
                        return new object[] { outputU16, alphaResult };
                    }

                    return new object[] { colorResult, alphaResult };
                });

                if (!mainThreadTask.Wait(TimeSpan.FromSeconds(60)))
                    throw new TimeoutException("BlendModeGLHelper.ComputeBlend timed out after 60 seconds.");

                var result = (object[])TaskHelper.SyncWait(() => mainThreadTask, CancellationToken.None);
                if (result is null)
                    throw new InvalidOperationException("BlendModeGLHelper Compute failed: accelerator returned null result.");

                return result;
            });
        }
    }

    public class BlendAddComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.OpenGLComputers";
        public string SupportedEffectOrMixture => "AddComputer";

        public object[] Compute(object[] args)
        {
            var top = args[0] as float[] ?? throw new ArgumentException("Invalid top channel");
            var bottom = args[1] as float[] ?? throw new ArgumentException("Invalid base channel");
            var topAlpha = (float[]?)args[2];
            var bottomAlpha = (float[]?)args[3];
            int outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            int actualPixels = args.Length > 5 ? Convert.ToInt32(args[5]) : top.Length;
            return BlendModeGLHelper.ComputeBlend(top, bottom, topAlpha, bottomAlpha, outputBpp, actualPixels, ShaderLibrary.BlendColorSrcAdd);
        }
    }

    public class BlendSubtractComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.OpenGLComputers";
        public string SupportedEffectOrMixture => "SubtractComputer";

        public object[] Compute(object[] args)
        {
            var top = args[0] as float[] ?? throw new ArgumentException("Invalid top channel");
            var bottom = args[1] as float[] ?? throw new ArgumentException("Invalid base channel");
            var topAlpha = (float[]?)args[2];
            var bottomAlpha = (float[]?)args[3];
            int outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            int actualPixels = args.Length > 5 ? Convert.ToInt32(args[5]) : top.Length;
            return BlendModeGLHelper.ComputeBlend(top, bottom, topAlpha, bottomAlpha, outputBpp, actualPixels, ShaderLibrary.BlendColorSrcSubtract);
        }
    }

    public class BlendMultiplyComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.OpenGLComputers";
        public string SupportedEffectOrMixture => "MultiplyComputer";

        public object[] Compute(object[] args)
        {
            var top = args[0] as float[] ?? throw new ArgumentException("Invalid top channel");
            var bottom = args[1] as float[] ?? throw new ArgumentException("Invalid base channel");
            var topAlpha = (float[]?)args[2];
            var bottomAlpha = (float[]?)args[3];
            int outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            int actualPixels = args.Length > 5 ? Convert.ToInt32(args[5]) : top.Length;
            return BlendModeGLHelper.ComputeBlend(top, bottom, topAlpha, bottomAlpha, outputBpp, actualPixels, ShaderLibrary.BlendColorSrcMultiply);
        }
    }

    public class BlendScreenComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.OpenGLComputers";
        public string SupportedEffectOrMixture => "ScreenComputer";

        public object[] Compute(object[] args)
        {
            var top = args[0] as float[] ?? throw new ArgumentException("Invalid top channel");
            var bottom = args[1] as float[] ?? throw new ArgumentException("Invalid base channel");
            var topAlpha = (float[]?)args[2];
            var bottomAlpha = (float[]?)args[3];
            int outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            int actualPixels = args.Length > 5 ? Convert.ToInt32(args[5]) : top.Length;
            return BlendModeGLHelper.ComputeBlend(top, bottom, topAlpha, bottomAlpha, outputBpp, actualPixels, ShaderLibrary.BlendColorSrcScreen);
        }
    }

    public class BlendOverlayBlendComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.OpenGLComputers";
        public string SupportedEffectOrMixture => "OverlayBlendComputer";

        public object[] Compute(object[] args)
        {
            var top = args[0] as float[] ?? throw new ArgumentException("Invalid top channel");
            var bottom = args[1] as float[] ?? throw new ArgumentException("Invalid base channel");
            var topAlpha = (float[]?)args[2];
            var bottomAlpha = (float[]?)args[3];
            int outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            int actualPixels = args.Length > 5 ? Convert.ToInt32(args[5]) : top.Length;
            return BlendModeGLHelper.ComputeBlend(top, bottom, topAlpha, bottomAlpha, outputBpp, actualPixels, ShaderLibrary.BlendColorSrcOverlayBlend);
        }
    }

    public class BlendDarkenComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.OpenGLComputers";
        public string SupportedEffectOrMixture => "DarkenComputer";

        public object[] Compute(object[] args)
        {
            var top = args[0] as float[] ?? throw new ArgumentException("Invalid top channel");
            var bottom = args[1] as float[] ?? throw new ArgumentException("Invalid base channel");
            var topAlpha = (float[]?)args[2];
            var bottomAlpha = (float[]?)args[3];
            int outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            int actualPixels = args.Length > 5 ? Convert.ToInt32(args[5]) : top.Length;
            return BlendModeGLHelper.ComputeBlend(top, bottom, topAlpha, bottomAlpha, outputBpp, actualPixels, ShaderLibrary.BlendColorSrcDarken);
        }
    }

    public class BlendLightenComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.OpenGLComputers";
        public string SupportedEffectOrMixture => "LightenComputer";

        public object[] Compute(object[] args)
        {
            var top = args[0] as float[] ?? throw new ArgumentException("Invalid top channel");
            var bottom = args[1] as float[] ?? throw new ArgumentException("Invalid base channel");
            var topAlpha = (float[]?)args[2];
            var bottomAlpha = (float[]?)args[3];
            int outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            int actualPixels = args.Length > 5 ? Convert.ToInt32(args[5]) : top.Length;
            return BlendModeGLHelper.ComputeBlend(top, bottom, topAlpha, bottomAlpha, outputBpp, actualPixels, ShaderLibrary.BlendColorSrcLighten);
        }
    }

    public class BlendDifferenceComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.OpenGLComputers";
        public string SupportedEffectOrMixture => "DifferenceComputer";

        public object[] Compute(object[] args)
        {
            var top = args[0] as float[] ?? throw new ArgumentException("Invalid top channel");
            var bottom = args[1] as float[] ?? throw new ArgumentException("Invalid base channel");
            var topAlpha = (float[]?)args[2];
            var bottomAlpha = (float[]?)args[3];
            int outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            int actualPixels = args.Length > 5 ? Convert.ToInt32(args[5]) : top.Length;
            return BlendModeGLHelper.ComputeBlend(top, bottom, topAlpha, bottomAlpha, outputBpp, actualPixels, ShaderLibrary.BlendColorSrcDifference);
        }
    }

    internal static class OpenGLSinglePassHelper
    {
        internal static object[] ComputeSinglePass(
            object[] args,
            Func<int, int, int, string> buildShader,
            string computerName)
        {
            var rIn = args[0] as float[] ?? throw new ArgumentException("Invalid argument for R");
            var gIn = args[1] as float[] ?? throw new ArgumentException("Invalid argument for G");
            var bIn = args[2] as float[] ?? throw new ArgumentException("Invalid argument for B");
            var aIn = args[3] as float[] ?? throw new ArgumentException("Invalid argument for A");

            int srcLength = rIn.Length;
            int glLength = srcLength;

            float[] rPad = PreparePaddedChannel(rIn, srcLength, glLength);
            float[] gPad = PreparePaddedChannel(gIn, srcLength, glLength);
            float[] bPad = PreparePaddedChannel(bIn, srcLength, glLength);
            float[] aPad = PreparePaddedChannel(aIn, srcLength, glLength);

            string shader = buildShader(srcLength, glLength, srcLength);

            return ComputerHelper.EnqueueCompute(() =>
            {
                var mainThreadTask = MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    NativeGLSurfaceView accelerator = new NativeGLSurfaceView
                    {
                        ShaderSource = shader,
                        Inputs = new float[][] { rPad },
                        WidthRequest = 50,
                        HeightRequest = 50,
                        JobID = computerName,
                        OutputElementType = GLComputeView.OutputElementType.Float32
                    };

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
                    ComputerHelper.AddPlatformComputeViewHandler?.Invoke(accelerator);

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
                    if (await Task.WhenAny(readyTask, Task.Delay(TimeSpan.FromMilliseconds(ComputerHelper.Timeout))) != readyTask)
                        throw new TimeoutException($"GLComputeView.WaitUntilReadyAsync timed out.");
                    await readyTask;

                    async Task<float[]> RunChannel(float[] channel, string jobId)
                    {
                        accelerator.JobID = jobId;
                        accelerator.Inputs = new float[][] { channel };
                        NativeGLSurfaceViewHandler.MapInputs(handler, accelerator);
                        var raw = (float[])await glView.RunComputeAsync(GLComputeView.OutputElementType.Float32);
                        if (raw.Length == srcLength) return raw;
                        var trimmed = new float[srcLength];
                        Array.Copy(raw, 0, trimmed, 0, Math.Min(raw.Length, srcLength));
                        return trimmed;
                    }

                    var rOut = await RunChannel(rPad, computerName + "-R");
                    var gOut = await RunChannel(gPad, computerName + "-G");
                    var bOut = await RunChannel(bPad, computerName + "-B");
                    var aOut = await RunChannel(aPad, computerName + "-A");
                    return new object[] { rOut, gOut, bOut, aOut };
                });

                if (!mainThreadTask.Wait(TimeSpan.FromSeconds(60)))
                    throw new TimeoutException($"{computerName}.Compute timed out after 60 seconds.");

                var result = (object[])TaskHelper.SyncWait(() => mainThreadTask, CancellationToken.None);
                if (result is null)
                    throw new InvalidOperationException($"{computerName} Compute failed: accelerator returned null result.");
                return result;
            });
        }

        private static float[] PreparePaddedChannel(float[] source, int sourceLength, int paddedLength)
        {
            var arr = new float[paddedLength];
            Array.Copy(source, 0, arr, 0, Math.Min(Math.Min(source.Length, sourceLength), paddedLength));
            return arr;
        }
    }

    public class OpacityComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.OpenGLComputers";
        public string SupportedEffectOrMixture => "FadeOpacity";

        public object[] Compute(object[] args)
        {
            float opacity = Convert.ToSingle(args[4]);
            return OpenGLSinglePassHelper.ComputeSinglePass(args, (srcLen, glLen, len) => $$"""
                #version 310 es
                layout(local_size_x = 256) in;
                layout(std430, binding = 0) buffer InBuffer { float inputData[]; };
                layout(std430, binding = 6) buffer OutBuffer { float outputData[]; };
                void main()
                {
                    uint i = gl_GlobalInvocationID.x;
                    if (i >= uint({{len}})) { outputData[i] = 0.0; return; }
                    outputData[i] = inputData[i];
                }
                """, "OpacityComputer");
        }
    }

    public class VignetteComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.OpenGLComputers";
        public string SupportedEffectOrMixture => "Vignette";

        public object[] Compute(object[] args)
        {
            int w = Convert.ToInt32(args[4]);
            int h = Convert.ToInt32(args[5]);
            float strength = Convert.ToSingle(args[6]);
            float radius = Convert.ToSingle(args[7]);
            return OpenGLSinglePassHelper.ComputeSinglePass(args, (srcLen, glLen, len) => $$"""
                #version 310 es
                layout(local_size_x = 256) in;
                layout(std430, binding = 0) buffer InBuffer { float inputData[]; };
                layout(std430, binding = 6) buffer OutBuffer { float outputData[]; };
                void main()
                {
                    uint i = gl_GlobalInvocationID.x;
                    if (i >= uint({{len}})) { outputData[i] = 0.0; return; }
                    int x = int(i % uint({{w}}));
                    int y = int(i / uint({{w}}));
                    float cx = float({{w}}) * 0.5;
                    float cy = float({{h}}) * 0.5;
                    float dx = (float(x) - cx) / cx;
                    float dy = (float(y) - cy) / cy;
                    float dist = sqrt(dx * dx + dy * dy);
                    float factor = 1.0;
                    if (dist > {{radius}})
                    {
                        float t = min((dist - {{radius}}) / (1.0 - {{radius}}), 1.0);
                        factor = 1.0 - t * t * {{strength}};
                    }
                    outputData[i] = inputData[i] * factor;
                }
                """, "VignetteComputer");
        }
    }

    public class FlipComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.OpenGLComputers";
        public string SupportedEffectOrMixture => "Flip";

        public object[] Compute(object[] args)
        {
            int w = Convert.ToInt32(args[4]);
            int h = Convert.ToInt32(args[5]);
            int horizontal = Convert.ToBoolean(args[6]) ? 1 : 0;
            int vertical = Convert.ToBoolean(args[7]) ? 1 : 0;
            return OpenGLSinglePassHelper.ComputeSinglePass(args, (srcLen, glLen, len) => $$"""
                #version 310 es
                layout(local_size_x = 256) in;
                layout(std430, binding = 0) buffer InBuffer { float inputData[]; };
                layout(std430, binding = 6) buffer OutBuffer { float outputData[]; };
                void main()
                {
                    uint i = gl_GlobalInvocationID.x;
                    if (i >= uint({{len}})) { outputData[i] = 0.0; return; }
                    int x = int(i % uint({{w}}));
                    int y = int(i / uint({{w}}));
                    int srcX = ({{horizontal}} != 0) ? ({{w}} - 1 - x) : x;
                    int srcY = ({{vertical}} != 0) ? ({{h}} - 1 - y) : y;
                    int srcIdx = srcY * {{w}} + srcX;
                    outputData[i] = inputData[srcIdx];
                }
                """, "FlipComputer");
        }
    }

    public class SharpenComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.OpenGLComputers";
        public string SupportedEffectOrMixture => "Sharpen";

        public object[] Compute(object[] args)
        {
            int w = Convert.ToInt32(args[4]);
            float amount = Convert.ToSingle(args[5]);
            return OpenGLSinglePassHelper.ComputeSinglePass(args, (srcLen, glLen, len) => $$"""
                #version 310 es
                layout(local_size_x = 256) in;
                layout(std430, binding = 0) buffer InBuffer { float inputData[]; };
                layout(std430, binding = 6) buffer OutBuffer { float outputData[]; };
                void main()
                {
                    uint i = gl_GlobalInvocationID.x;
                    if (i >= uint({{len}})) { outputData[i] = 0.0; return; }
                    int x = int(i % uint({{w}}));
                    float orig = inputData[i];
                    int left = (x > 0) ? int(i) - 1 : int(i);
                    int right = (x < {{w}} - 1) ? int(i) + 1 : int(i);
                    int top = int(i) - {{w}};
                    if (top < 0) top = int(i);
                    int bottom = int(i) + {{w}};
                    if (bottom >= {{len}}) bottom = int(i);
                    float avg = (inputData[left] + inputData[right] + inputData[top] + inputData[bottom]) * 0.25;
                    outputData[i] = orig + {{amount}} * (orig - avg);
                }
                """, "SharpenComputer");
        }
    }

    public class RotationComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.OpenGLComputers";
        public string SupportedEffectOrMixture => "Rotation";
        public object[] Compute(object[] args)
        {
            int srcW = Convert.ToInt32(args[4]);
            int srcH = Convert.ToInt32(args[5]);
            int outW = Convert.ToInt32(args[6]);
            int outH = Convert.ToInt32(args[7]);
            float angleDeg = Convert.ToSingle(args[8]);
            int srcLength = checked(srcW * srcH);
            int dstLength = checked(outW * outH);

            // This one needs different src/dst sizes, so we handle it manually
            var rIn = args[0] as float[] ?? throw new ArgumentException("Invalid argument for R");
            var gIn = args[1] as float[] ?? throw new ArgumentException("Invalid argument for G");
            var bIn = args[2] as float[] ?? throw new ArgumentException("Invalid argument for B");
            var aIn = args[3] as float[] ?? throw new ArgumentException("Invalid argument for A");
            int glPad = Math.Max(srcLength, dstLength);

            float[] PadChannel(float[] ch)
            {
                var arr = new float[glPad];
                Array.Copy(ch, 0, arr, 0, Math.Min(ch.Length, srcLength));
                return arr;
            }
            var rPad = PadChannel(rIn); var gPad = PadChannel(gIn);
            var bPad = PadChannel(bIn); var aPad = PadChannel(aIn);

            string shader = $$"""
                #version 310 es
                layout(local_size_x = 256) in;
                layout(std430, binding = 0) buffer InBuffer { float inputData[]; };
                layout(std430, binding = 6) buffer OutBuffer { float outputData[]; };
                void main()
                {
                    uint i = gl_GlobalInvocationID.x;
                    if (i >= uint({{dstLength}})) { outputData[i] = 0.0; return; }
                    int x = int(i % uint({{outW}}));
                    int y = int(i / uint({{outW}}));
                    float angleRad = {{(angleDeg * MathF.PI / 180f)}};
                    float cosA = cos(-angleRad);
                    float sinA = sin(-angleRad);
                    float srcCx = float({{srcW}}) * 0.5;
                    float srcCy = float({{srcH}}) * 0.5;
                    float outCx = float({{outW}}) * 0.5;
                    float outCy = float({{outH}}) * 0.5;
                    float ox = float(x) - outCx;
                    float oy = float(y) - outCy;
                    float sx = cosA * ox - sinA * oy + srcCx;
                    float sy = sinA * ox + cosA * oy + srcCy;
                    if (sx >= 0.0 && sx < float({{srcW}} - 1) && sy >= 0.0 && sy < float({{srcH}} - 1))
                    {
                        int sx0 = int(sx); int sy0 = int(sy);
                        int sx1 = sx0 + 1; int sy1 = sy0 + 1;
                        float fx = sx - float(sx0); float fy = sy - float(sy0);
                        int i00 = sy0 * {{srcW}} + sx0; int i10 = sy0 * {{srcW}} + sx1;
                        int i01 = sy1 * {{srcW}} + sx0; int i11 = sy1 * {{srcW}} + sx1;
                        outputData[i] =
                            ((inputData[i00] * (1.0 - fx) + inputData[i10] * fx) * (1.0 - fy) +
                             (inputData[i01] * (1.0 - fx) + inputData[i11] * fx) * fy);
                    }
                    else { outputData[i] = 0.0; }
                }
                """;

            return ComputerHelper.EnqueueCompute(() =>
            {
                var mainThreadTask = MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    NativeGLSurfaceView accelerator = new NativeGLSurfaceView
                    {
                        ShaderSource = shader, Inputs = new float[][] { rPad },
                        WidthRequest = 50, HeightRequest = 50,
                        JobID = "RotationComputer",
                        OutputElementType = GLComputeView.OutputElementType.Float32
                    };
                    var handlerReadyTcs = new TaskCompletionSource<NativeGLSurfaceViewHandler>(TaskCreationOptions.RunContinuationsAsynchronously);
                    void OnHandlerChanged(object? sender, EventArgs e)
                    {
                        if (accelerator.Handler is NativeGLSurfaceViewHandler handler)
                        { accelerator.HandlerChanged -= OnHandlerChanged; handlerReadyTcs.TrySetResult(handler); }
                    }
                    accelerator.HandlerChanged += OnHandlerChanged;
                    ComputerHelper.AddPlatformComputeViewHandler?.Invoke(accelerator);
                    if (accelerator.Handler is NativeGLSurfaceViewHandler existingHandler)
                    { accelerator.HandlerChanged -= OnHandlerChanged; handlerReadyTcs.TrySetResult(existingHandler); }
                    var hTask = handlerReadyTcs.Task;
                    if (await Task.WhenAny(hTask, Task.Delay(TimeSpan.FromSeconds(10))) != hTask)
                    { accelerator.HandlerChanged -= OnHandlerChanged; throw new TimeoutException("Handler creation timed out."); }
                    var handler = await hTask;
                    if (handler?.PlatformView is not GLComputeView glView)
                        throw new InvalidOperationException("Accelerator is not ready.");
                    var readyTask = glView.WaitUntilReadyAsync();
                    if (await Task.WhenAny(readyTask, Task.Delay(TimeSpan.FromMilliseconds(ComputerHelper.Timeout))) != readyTask)
                        throw new TimeoutException("GLComputeView timed out.");
                    await readyTask;

                    async Task<float[]> RunCh(float[] ch, string id)
                    {
                        accelerator.JobID = id; accelerator.Inputs = new float[][] { ch };
                        NativeGLSurfaceViewHandler.MapInputs(handler, accelerator);
                        var raw = (float[])await glView.RunComputeAsync(GLComputeView.OutputElementType.Float32);
                        if (raw.Length == dstLength) return raw;
                        var t = new float[dstLength]; Array.Copy(raw, 0, t, 0, Math.Min(raw.Length, dstLength)); return t;
                    }
                    var rO = await RunCh(rPad, "RotationComputer-R"); var gO = await RunCh(gPad, "RotationComputer-G");
                    var bO = await RunCh(bPad, "RotationComputer-B"); var aO = await RunCh(aPad, "RotationComputer-A");
                    return new object[] { rO, gO, bO, aO };
                });
                if (!mainThreadTask.Wait(TimeSpan.FromSeconds(60)))
                    throw new TimeoutException("RotationComputer timed out.");
                var result = (object[])TaskHelper.SyncWait(() => mainThreadTask, CancellationToken.None);
                if (result is null) throw new InvalidOperationException("RotationComputer returned null.");
                return result;
            });
        }
    }

    public class BlurComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.OpenGLComputers";
        public string SupportedEffectOrMixture => "Blur";

        public object[] Compute(object[] args)
        {
            var rIn = args[0] as float[] ?? throw new ArgumentException("Invalid argument for R");
            var gIn = args[1] as float[] ?? throw new ArgumentException("Invalid argument for G");
            var bIn = args[2] as float[] ?? throw new ArgumentException("Invalid argument for B");
            var aIn = args[3] as float[] ?? throw new ArgumentException("Invalid argument for A");
            int w = Convert.ToInt32(args[4]);
            float sigma = Convert.ToSingle(args[5]);
            int radius = (int)MathF.Ceiling(sigma);
            if (radius <= 0) radius = 1;
            int len = rIn.Length;
            int h = len / w;
            int glPad = len;

            float[] Pad(float[] src)
            {
                var a = new float[glPad]; Array.Copy(src, 0, a, 0, Math.Min(src.Length, glPad)); return a;
            }
            var rP = Pad(rIn); var gP = Pad(gIn); var bP = Pad(bIn); var aP = Pad(aIn);

            string horizShader = $$"""
                #version 310 es
                layout(local_size_x = 256) in;
                layout(std430, binding = 0) buffer InBuffer { float inputData[]; };
                layout(std430, binding = 6) buffer OutBuffer { float outputData[]; };
                void main()
                {
                    uint i = gl_GlobalInvocationID.x;
                    if (i >= uint({{len}})) { outputData[i] = 0.0; return; }
                    int x = int(i % uint({{w}}));
                    int rowStart = int(i) - x;
                    float sum = 0.0; int count = 0;
                    for (int k = x - {{radius}}; k <= x + {{radius}}; k++)
                    {
                        int col = k < 0 ? 0 : (k >= {{w}} ? {{w}} - 1 : k);
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
                    if (i >= uint({{len}})) { outputData[i] = 0.0; return; }
                    int y = int(i / uint({{w}}));
                    float sum = 0.0; int count = 0;
                    for (int k = y - {{radius}}; k <= y + {{radius}}; k++)
                    {
                        int row = k < 0 ? 0 : (k >= {{h}} ? {{h}} - 1 : k);
                        sum += inputData[row * {{w}} + int(i % uint({{w}}))]; count++;
                    }
                    outputData[i] = sum / float(count);
                }
                """;

            return ComputerHelper.EnqueueCompute(() =>
            {
                var mtTask = MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    NativeGLSurfaceView accH = new NativeGLSurfaceView { ShaderSource = horizShader, Inputs = new float[][] { rP }, WidthRequest = 50, HeightRequest = 50, JobID = "Blur-H", OutputElementType = GLComputeView.OutputElementType.Float32 };
                    var tcsH = new TaskCompletionSource<NativeGLSurfaceViewHandler>(TaskCreationOptions.RunContinuationsAsynchronously);
                    void OnH(object? s, EventArgs e) { if (accH.Handler is NativeGLSurfaceViewHandler hh) { accH.HandlerChanged -= OnH; tcsH.TrySetResult(hh); } }
                    accH.HandlerChanged += OnH;
                    ComputerHelper.AddPlatformComputeViewHandler?.Invoke(accH);
                    if (accH.Handler is NativeGLSurfaceViewHandler eh) { accH.HandlerChanged -= OnH; tcsH.TrySetResult(eh); }
                    if (await Task.WhenAny(tcsH.Task, Task.Delay(10000)) != tcsH.Task) throw new TimeoutException();
                    var hH = await tcsH.Task;
                    if (hH?.PlatformView is not GLComputeView gvH) throw new InvalidOperationException();
                    var rtH = gvH.WaitUntilReadyAsync();
                    if (await Task.WhenAny(rtH, Task.Delay(ComputerHelper.Timeout)) != rtH) throw new TimeoutException();
                    await rtH;

                    async Task<float[]> RunH(float[] ch, NativeGLSurfaceViewHandler handler)
                    {
                        accH.JobID = "Blur-H"; accH.Inputs = new float[][] { ch };
                        NativeGLSurfaceViewHandler.MapInputs(handler, accH);
                        var raw = (float[])await gvH.RunComputeAsync(GLComputeView.OutputElementType.Float32);
                        if (raw.Length == len) return raw;
                        var t = new float[len]; Array.Copy(raw, 0, t, 0, Math.Min(raw.Length, len)); return t;
                    }

                    var rT = await RunH(rP, hH); var gT = await RunH(gP, hH); var bT = await RunH(bP, hH); var aT = await RunH(aP, hH);

                    NativeGLSurfaceView accV = new NativeGLSurfaceView { ShaderSource = vertShader, Inputs = new float[][] { rT }, WidthRequest = 50, HeightRequest = 50, JobID = "Blur-V", OutputElementType = GLComputeView.OutputElementType.Float32 };
                    var tcsV = new TaskCompletionSource<NativeGLSurfaceViewHandler>(TaskCreationOptions.RunContinuationsAsynchronously);
                    void OnV(object? s, EventArgs e) { if (accV.Handler is NativeGLSurfaceViewHandler hv) { accV.HandlerChanged -= OnV; tcsV.TrySetResult(hv); } }
                    accV.HandlerChanged += OnV;
                    ComputerHelper.AddPlatformComputeViewHandler?.Invoke(accV);
                    if (accV.Handler is NativeGLSurfaceViewHandler ev) { accV.HandlerChanged -= OnV; tcsV.TrySetResult(ev); }
                    if (await Task.WhenAny(tcsV.Task, Task.Delay(10000)) != tcsV.Task) throw new TimeoutException();
                    var hV = await tcsV.Task;
                    if (hV?.PlatformView is not GLComputeView gvV) throw new InvalidOperationException();
                    var rtV = gvV.WaitUntilReadyAsync();
                    if (await Task.WhenAny(rtV, Task.Delay(ComputerHelper.Timeout)) != rtV) throw new TimeoutException();
                    await rtV;

                    async Task<float[]> RunV(float[] ch)
                    {
                        accV.JobID = "Blur-V"; accV.Inputs = new float[][] { ch };
                        NativeGLSurfaceViewHandler.MapInputs(hV, accV);
                        var raw = (float[])await gvV.RunComputeAsync(GLComputeView.OutputElementType.Float32);
                        if (raw.Length == len) return raw;
                        var t = new float[len]; Array.Copy(raw, 0, t, 0, Math.Min(raw.Length, len)); return t;
                    }
                    return new object[] { await RunV(rT), await RunV(gT), await RunV(bT), await RunV(aT) };
                });
                if (!mtTask.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("BlurComputer timed out.");
                var r = (object[])TaskHelper.SyncWait(() => mtTask, CancellationToken.None);
                if (r is null) throw new InvalidOperationException("BlurComputer returned null.");
                return r;
            });
        }
    }

    public class ColorAdjustmentComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.OpenGLComputers";
        public string SupportedEffectOrMixture => "ColorAdjustment";

        public object[] Compute(object[] args)
        {
            var rIn = args[0] as float[] ?? throw new ArgumentException("Invalid argument for R");
            var gIn = args[1] as float[] ?? throw new ArgumentException("Invalid argument for G");
            var bIn = args[2] as float[] ?? throw new ArgumentException("Invalid argument for B");
            var aIn = args[3] as float[] ?? throw new ArgumentException("Invalid argument for A");
            float brightness = Convert.ToSingle(args[4]), contrast = Convert.ToSingle(args[5]);
            float saturation = Convert.ToSingle(args[6]), hue = Convert.ToSingle(args[7]);
            float gamma = Convert.ToSingle(args[8]), vibrance = Convert.ToSingle(args[9]);
            float temperature = Convert.ToSingle(args[10]), invertF = Convert.ToSingle(args[11]);
            float grayscale = Convert.ToSingle(args[12]), opacity = Convert.ToSingle(args[13]);
            float maxV = Convert.ToSingle(args[14]);

            int srcLen = rIn.Length;
            // Pack RGBA into one buffer: R[0..N-1] G[0..N-1] B[0..N-1] A[0..N-1]
            int packedLen = srcLen * 4;
            var packed = new float[packedLen];
            Array.Copy(rIn, 0, packed, 0, srcLen);
            Array.Copy(gIn, 0, packed, srcLen, srcLen);
            Array.Copy(bIn, 0, packed, srcLen * 2, srcLen);
            Array.Copy(aIn, 0, packed, srcLen * 3, srcLen);

            float angleRad = hue * MathF.PI / 180f;
            string shader = $$"""
                #version 310 es
                layout(local_size_x = 256) in;
                layout(std430, binding = 0) buffer InBuffer { float inputData[]; };
                layout(std430, binding = 6) buffer OutBuffer { float outputData[]; };
                void main()
                {
                    uint i = gl_GlobalInvocationID.x;
                    if (i >= uint({{srcLen}})) { outputData[i] = 0.0; outputData[i+{{srcLen}}] = 0.0; outputData[i+{{srcLen*2}}] = 0.0; outputData[i+{{srcLen*3}}] = 0.0; return; }
                    float r = inputData[i], g = inputData[i+{{srcLen}}], b = inputData[i+{{srcLen*2}}], a = inputData[i+{{srcLen*3}}];

                    // 1. Brightness
                    float bf = {{brightness}} - 1.0;
                    r = bf >= 0.0 ? r + ({{maxV}} - r) * bf : r * (1.0 + bf);
                    g = bf >= 0.0 ? g + ({{maxV}} - g) * bf : g * (1.0 + bf);
                    b = bf >= 0.0 ? b + ({{maxV}} - b) * bf : b * (1.0 + bf);

                    // 2. Contrast
                    r = ((r / {{maxV}} - 0.5) * {{contrast}} + 0.5) * {{maxV}};
                    g = ((g / {{maxV}} - 0.5) * {{contrast}} + 0.5) * {{maxV}};
                    b = ((b / {{maxV}} - 0.5) * {{contrast}} + 0.5) * {{maxV}};

                    // 3. Saturation
                    float gray = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                    r = gray + {{saturation}} * (r - gray);
                    g = gray + {{saturation}} * (g - gray);
                    b = gray + {{saturation}} * (b - gray);

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
                    hh += {{hue}};
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
                    float invG = 1.0 / max({{gamma}}, 0.001);
                    r = {{maxV}} * pow(r / {{maxV}}, invG);
                    g = {{maxV}} * pow(g / {{maxV}}, invG);
                    b = {{maxV}} * pow(b / {{maxV}}, invG);

                    // 6. Vibrance
                    float vSat = 1.0 + {{vibrance}} * 0.5;
                    float vGray = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                    r = vGray + vSat * (r - vGray);
                    g = vGray + vSat * (g - vGray);
                    b = vGray + vSat * (b - vGray);

                    // 7. Temperature
                    r *= 1.0 + {{temperature}} * 0.01;
                    b *= 1.0 - {{temperature}} * 0.01;

                    // 8. Invert
                    if ({{invertF}} > 0.5) { r = {{maxV}} - r; g = {{maxV}} - g; b = {{maxV}} - b; }

                    // 9. Grayscale
                    float lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                    float gs = {{grayscale}} >= 1.0 ? 1.0 : 1.0 - {{grayscale}};
                    r = lum + gs * (r - lum); g = lum + gs * (g - lum); b = lum + gs * (b - lum);

                    // 10. Opacity
                    outputData[i] = r; outputData[i+{{srcLen}}] = g;
                    outputData[i+{{srcLen*2}}] = b; outputData[i+{{srcLen*3}}] = a * {{opacity}};
                }
                """;

            return ComputerHelper.EnqueueCompute(() =>
            {
                var mtTask = MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    NativeGLSurfaceView acc = new NativeGLSurfaceView { ShaderSource = shader, Inputs = new float[][] { packed }, WidthRequest = 50, HeightRequest = 50, JobID = "ColorAdjustmentComputer", OutputElementType = GLComputeView.OutputElementType.Float32 };
                    var tcs = new TaskCompletionSource<NativeGLSurfaceViewHandler>(TaskCreationOptions.RunContinuationsAsynchronously);
                    void OnH(object? s, EventArgs e) { if (acc.Handler is NativeGLSurfaceViewHandler hh) { acc.HandlerChanged -= OnH; tcs.TrySetResult(hh); } }
                    acc.HandlerChanged += OnH; ComputerHelper.AddPlatformComputeViewHandler?.Invoke(acc);
                    if (acc.Handler is NativeGLSurfaceViewHandler eh) { acc.HandlerChanged -= OnH; tcs.TrySetResult(eh); }
                    if (await Task.WhenAny(tcs.Task, Task.Delay(10000)) != tcs.Task) throw new TimeoutException("Handler timeout.");
                    var handler = await tcs.Task;
                    if (handler?.PlatformView is not GLComputeView glView) throw new InvalidOperationException();
                    var rt = glView.WaitUntilReadyAsync();
                    if (await Task.WhenAny(rt, Task.Delay(ComputerHelper.Timeout)) != rt) throw new TimeoutException("Timeout.");
                    await rt;
                    var raw = (float[])await glView.RunComputeAsync(GLComputeView.OutputElementType.Float32);
                    if (raw.Length < packedLen) { var tp = new float[packedLen]; Array.Copy(raw, 0, tp, 0, raw.Length); raw = tp; }
                    var rO = new float[srcLen]; var gO = new float[srcLen]; var bO = new float[srcLen]; var aO = new float[srcLen];
                    Array.Copy(raw, 0, rO, 0, srcLen);
                    Array.Copy(raw, srcLen, gO, 0, srcLen);
                    Array.Copy(raw, srcLen * 2, bO, 0, srcLen);
                    Array.Copy(raw, srcLen * 3, aO, 0, srcLen);
                    return new object[] { rO, gO, bO, aO };
                });
                if (!mtTask.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("ColorAdjustmentComputer timed out.");
                var result = (object[])TaskHelper.SyncWait(() => mtTask, CancellationToken.None);
                if (result is null) throw new InvalidOperationException("ColorAdjustmentComputer returned null.");
                return result;
            });
        }
    }


}
