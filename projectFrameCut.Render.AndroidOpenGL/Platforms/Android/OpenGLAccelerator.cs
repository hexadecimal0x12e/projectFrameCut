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
                throw new InvalidDataException($"Alpha array length mismatch: aAlpha={aAlpha.Length}, bAlpha={bAlpha.Length}, expected={actualPixels}");
            }

            using (ShaderLibrary.locker.EnterScope())
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
                    
                    ComputerHelper.AddGLViewHandler?.Invoke(accelerator);
                    
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
                    if (await Task.WhenAny(readyTask, Task.Delay(TimeSpan.FromSeconds(30))) != readyTask)
                        throw new TimeoutException("GLComputeView.WaitUntilReadyAsync timed out after 30 seconds.");
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
            }

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

            var toRemoveR = (ushort)args[4];
            var toRemoveG = (ushort)args[5];
            var toRemoveB = (ushort)args[6];
            var range = (ushort)args[7];
            
            // Validate all input arrays have the same length
            if (aR!.Length != aG!.Length || aR.Length != aB!.Length || aR.Length != sourceA!.Length)
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
            using (ShaderLibrary.locker.EnterScope())
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
                    
                    ComputerHelper.AddGLViewHandler?.Invoke(accelerator);
                    
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
                    if (await Task.WhenAny(readyTask, Task.Delay(TimeSpan.FromSeconds(30))) != readyTask)
                        throw new TimeoutException("GLComputeView.WaitUntilReadyAsync timed out after 30 seconds.");
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
            }
        }
    }

}
