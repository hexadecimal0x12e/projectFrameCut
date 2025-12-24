using Android.Renderscripts;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Handlers;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
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

        public static Lock locker = new();

    }

    public class OverlayComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.OpenGLComputers";
        public string SupportedEffectOrMixture => "Overlay";

        public object[] Compute(object[] args)
        {
            // args: [A, B, aAlpha, bAlpha]
            var A = args[0] as float[];
            var B = args[1] as float[];
            var aAlpha = args[2] as float[];
            var bAlpha = args[3] as float[];

            // Ensure inputs are not null
            if (aAlpha == null) aAlpha = Enumerable.Repeat(1f, A.Length).ToArray();
            if (bAlpha == null) bAlpha = Enumerable.Repeat(1f, A.Length).ToArray();

            using (ShaderLibrary.locker.EnterScope())
            {
                // We need to run on MainThread because we are touching UI elements (NativeGLSurfaceView)
                var result = MainThread.InvokeOnMainThreadAsync(async () =>
                {

                    NativeGLSurfaceView accelerator = new NativeGLSurfaceView
                    {
                        ShaderSource = ShaderLibrary.Alpha,
                        Inputs = new float[][] { aAlpha, bAlpha },
                        WidthRequest = 50,
                        HeightRequest = 50,
                        JobID = "OverlayComputer"
                    };
                    ComputerHelper.AddGLViewHandler?.Invoke(accelerator);
                    var handler = accelerator.Handler as NativeGLSurfaceViewHandler;
                    if (handler?.PlatformView is not GLComputeView glView)
                        throw new InvalidOperationException("Accelerator is not ready or not attached.");

                    await glView.WaitUntilReadyAsync();
                    // Force update inputs on platform view
                    //NativeGLSurfceViewHandler.MapInputs(handler, accelerator);

                    var alphaResult = await glView.RunComputeAsync();

                    // 2. Compute Color
                    accelerator.ShaderSource = ShaderLibrary.ShaderColorSrc;
                    accelerator.Inputs = new float[][] { aAlpha, A, bAlpha, B };
                    NativeGLSurfaceViewHandler.MapInputs(handler, accelerator);

                    var colorResult = await glView.RunComputeAsync();

                    return new float[][] { colorResult, alphaResult };
                }).Result;

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

            // args: [aR, aG, aB, sourceA, [toRemoveR], [toRemoveG], [toRemoveB], [range]]
            var aR = args[0] as float[];
            var aG = args[1] as float[];
            var aB = args[2] as float[];
            var sourceA = args[3] as float[];

            var toRemoveR = (ushort)args[4];
            var toRemoveG = (ushort)args[5];
            var toRemoveB = (ushort)args[6];
            var range = (ushort)args[7];

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
                float[] result = MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    NativeGLSurfaceView accelerator = new NativeGLSurfaceView
                    {
                        ShaderSource = shader,
                        Inputs = new float[][] { aR, aG, aB, sourceA },
                        WidthRequest = 50,
                        HeightRequest = 50,
                        JobID = "RemoveColorComputer"
                    };
                    ComputerHelper.AddGLViewHandler?.Invoke(accelerator);
                    var handler = accelerator.Handler as NativeGLSurfaceViewHandler;
                    if (handler?.PlatformView is not GLComputeView glView)
                        throw new InvalidOperationException("Accelerator is not ready or not attached.");

                    await glView.WaitUntilReadyAsync();
                    return await glView.RunComputeAsync();
                }).Result;

                if (result is null)
                    throw new InvalidOperationException($"RemoveColorComputer Compute failed: accelerator returned null result.");

                return [result];
            }
        }
    }

}
