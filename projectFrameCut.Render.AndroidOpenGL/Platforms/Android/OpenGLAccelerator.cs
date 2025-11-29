using Android.Renderscripts;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Handlers;
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
    public class OverlayComputer : IComputer
    {



        public float[][] Compute(float[][] args)
        {
            // args: [A, B, aAlpha, bAlpha]
            var A = args[0];
            var B = args[1];
            var aAlpha = args[2];
            var bAlpha = args[3];

            // Ensure inputs are not null
            if (aAlpha == null) aAlpha = Enumerable.Repeat(1f, A.Length).ToArray();
            if (bAlpha == null) bAlpha = Enumerable.Repeat(1f, A.Length).ToArray();

            // We need to run on MainThread because we are touching UI elements (NativeGLSurfaceView)
            return MainThread.InvokeOnMainThreadAsync(async () =>
            {
                
                NativeGLSurfaceView accelerator = new NativeGLSurfaceView
                {
                    ShaderSource = ShaderAlphaSrc,
                    Inputs = new float[][] { aAlpha, bAlpha },
                };
                ComputerHelper.AddGLViewHandler?.Invoke(accelerator);
                var handler = accelerator.Handler as NativeGLSurfaceViewHandler;
                if (handler?.PlatformView is not GLComputeView glView)
                    throw new InvalidOperationException("Accelerator is not ready or not attached.");

                await glView.WaitUntilReadyAsync();
                // Force update inputs on platform view
                //NativeGLSurfaceViewHandler.MapInputs(handler, accelerator);

                var alphaResult = await glView.RunComputeAsync();

                // 2. Compute Color
                accelerator.ShaderSource = ShaderColorSrc;
                accelerator.Inputs = new float[][] { aAlpha, A, bAlpha, B };
                NativeGLSurfaceViewHandler.MapInputs(handler, accelerator);

                var colorResult = await glView.RunComputeAsync();

                return new float[][] { colorResult, alphaResult };
            }).Result;
        }

        private const string ShaderAlphaSrc =
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

        private const string ShaderColorSrc =
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
    }

    public class RemoveColorComputer : IComputer
    {

        public float[][] Compute(float[][] args)
        {

            // args: [aR, aG, aB, sourceA, [toRemoveR], [toRemoveG], [toRemoveB], [range]]
            var aR = args[0];
            var aG = args[1];
            var aB = args[2];
            var sourceA = args[3];

            var toRemoveR = (ushort)args[4][0];
            var toRemoveG = (ushort)args[5][0];
            var toRemoveB = (ushort)args[6][0];
            var range = (ushort)args[7][0];

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

            return MainThread.InvokeOnMainThreadAsync(async () =>
            {
                NativeGLSurfaceView accelerator = new NativeGLSurfaceView
                {
                    ShaderSource = shader,
                    Inputs = new float[][] { aR, aG, aB, sourceA }
                };
                ComputerHelper.AddGLViewHandler?.Invoke(accelerator);
                var handler = accelerator.Handler as NativeGLSurfaceViewHandler;
                if (handler?.PlatformView is not GLComputeView glView)
                    throw new InvalidOperationException("Accelerator is not ready or not attached.");

                await glView.WaitUntilReadyAsync();
                //NativeGLSurfaceViewHandler.MapInputs(handler, accelerator);

                var result = await glView.RunComputeAsync();
                return new float[][] { result };
            }).Result;
        }
    }
}
