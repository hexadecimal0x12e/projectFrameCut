using Microsoft.Maui.ApplicationModel;
using projectFrameCut.Render.HwAccelContracts;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using Silk.NET.Shaderc;
using System.Globalization;
using static projectFrameCut.Render.HwAccelEngine.Platforms.Android.GLComputeView;

namespace projectFrameCut.Render.HwAccelEngine.Platforms.Android
{
    internal static class VulkanShaderLibrary
    {
        public const string Alpha =
            """
            #version 450
            layout(local_size_x = 256) in;

            layout(set = 0, binding = 0, std430) buffer AAlphaBuffer { float aAlpha[]; };
            layout(set = 0, binding = 1, std430) buffer BAlphaBuffer { float bAlpha[]; };
            layout(set = 0, binding = 2, std430) buffer CAlphaBuffer { float cAlpha[]; };

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
            #version 450
            layout(local_size_x = 256) in;

            layout(set = 0, binding = 0, std430) buffer AAlphaBuffer { float aAlpha[]; };
            layout(set = 0, binding = 1, std430) buffer ABuffer { float a []; };
            layout(set = 0, binding = 2, std430) buffer BAlphaBuffer { float bAlpha []; };
            layout(set = 0, binding = 3, std430) buffer BBuffer { float b []; };
            layout(set = 0, binding = 4, std430) buffer CAlphaBuffer { float c []; };

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
            #version 450
            layout(local_size_x = 256) in;

            layout(set = 0, binding = 0, std430) buffer AAlphaBuffer { float aAlpha[]; };
            layout(set = 0, binding = 1, std430) buffer ABuffer { float a []; };
            layout(set = 0, binding = 2, std430) buffer BAlphaBuffer { float bAlpha []; };
            layout(set = 0, binding = 3, std430) buffer BBuffer { float b []; };
            layout(set = 0, binding = 4, std430) buffer CBuffer { uint c []; };

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
            #version 450
            layout(local_size_x = 256) in;

            layout(set = 0, binding = 0, std430) buffer AAlphaBuffer { float aAlpha[]; };
            layout(set = 0, binding = 1, std430) buffer ABuffer { float a []; };
            layout(set = 0, binding = 2, std430) buffer BAlphaBuffer { float bAlpha []; };
            layout(set = 0, binding = 3, std430) buffer BBuffer { float b []; };
            layout(set = 0, binding = 4, std430) buffer CBuffer { uint c []; };

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

        public static string BuildResizeShader(int dstLength, int dstW, int srcW, int srcH, float ratioX, float ratioY)
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
                        outputData[i] = 0.0;
                        return;
                    }

                    int x = int(i % uint({{dstW}}));
                    int y = int(i / uint({{dstW}}));

                    int srcX = int(float(x) * {{ratioX.ToString(CultureInfo.InvariantCulture)}});
                    int srcY = int(float(y) * {{ratioY.ToString(CultureInfo.InvariantCulture)}});

                    if (srcX >= {{srcW}}) srcX = {{srcW - 1}};
                    if (srcY >= {{srcH}}) srcY = {{srcH - 1}};
                    if (srcX < 0) srcX = 0;
                    if (srcY < 0) srcY = 0;

                    int srcIdx = srcY * {{srcW}} + srcX;
                    outputData[i] = inputData[srcIdx];
                }
                """;
        }

        public static string BuildCropShader(int dstLength, int cropW, int startX, int startY, int srcW, int srcH)
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
        }

        public static string BuildPlaceShader(int dstLength, int dstW, int srcW, int srcH, int startX, int startY)
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
        }

        public static string BuildRemoveColorShader(int lowR, int highR, int lowG, int highG, int lowB, int highB)
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

        public const string BlendColorSrcAdd =
            """
            #version 450
            layout(local_size_x = 256) in;

            layout(set = 0, binding = 0, std430) buffer AAlphaBuffer { float aAlpha[]; };
            layout(set = 0, binding = 1, std430) buffer ABuffer { float a []; };
            layout(set = 0, binding = 2, std430) buffer BAlphaBuffer { float bAlpha []; };
            layout(set = 0, binding = 3, std430) buffer BBuffer { float b []; };
            layout(set = 0, binding = 4, std430) buffer CBuffer { float c []; };

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
            #version 450
            layout(local_size_x = 256) in;

            layout(set = 0, binding = 0, std430) buffer AAlphaBuffer { float aAlpha[]; };
            layout(set = 0, binding = 1, std430) buffer ABuffer { float a []; };
            layout(set = 0, binding = 2, std430) buffer BAlphaBuffer { float bAlpha []; };
            layout(set = 0, binding = 3, std430) buffer BBuffer { float b []; };
            layout(set = 0, binding = 4, std430) buffer CBuffer { float c []; };

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
            #version 450
            layout(local_size_x = 256) in;

            layout(set = 0, binding = 0, std430) buffer AAlphaBuffer { float aAlpha[]; };
            layout(set = 0, binding = 1, std430) buffer ABuffer { float a []; };
            layout(set = 0, binding = 2, std430) buffer BAlphaBuffer { float bAlpha []; };
            layout(set = 0, binding = 3, std430) buffer BBuffer { float b []; };
            layout(set = 0, binding = 4, std430) buffer CBuffer { float c []; };

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
            #version 450
            layout(local_size_x = 256) in;

            layout(set = 0, binding = 0, std430) buffer AAlphaBuffer { float aAlpha[]; };
            layout(set = 0, binding = 1, std430) buffer ABuffer { float a []; };
            layout(set = 0, binding = 2, std430) buffer BAlphaBuffer { float bAlpha []; };
            layout(set = 0, binding = 3, std430) buffer BBuffer { float b []; };
            layout(set = 0, binding = 4, std430) buffer CBuffer { float c []; };

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
            #version 450
            layout(local_size_x = 256) in;

            layout(set = 0, binding = 0, std430) buffer AAlphaBuffer { float aAlpha[]; };
            layout(set = 0, binding = 1, std430) buffer ABuffer { float a []; };
            layout(set = 0, binding = 2, std430) buffer BAlphaBuffer { float bAlpha []; };
            layout(set = 0, binding = 3, std430) buffer BBuffer { float b []; };
            layout(set = 0, binding = 4, std430) buffer CBuffer { float c []; };

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
            #version 450
            layout(local_size_x = 256) in;

            layout(set = 0, binding = 0, std430) buffer AAlphaBuffer { float aAlpha[]; };
            layout(set = 0, binding = 1, std430) buffer ABuffer { float a []; };
            layout(set = 0, binding = 2, std430) buffer BAlphaBuffer { float bAlpha []; };
            layout(set = 0, binding = 3, std430) buffer BBuffer { float b []; };
            layout(set = 0, binding = 4, std430) buffer CBuffer { float c []; };

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
            #version 450
            layout(local_size_x = 256) in;

            layout(set = 0, binding = 0, std430) buffer AAlphaBuffer { float aAlpha[]; };
            layout(set = 0, binding = 1, std430) buffer ABuffer { float a []; };
            layout(set = 0, binding = 2, std430) buffer BAlphaBuffer { float bAlpha []; };
            layout(set = 0, binding = 3, std430) buffer BBuffer { float b []; };
            layout(set = 0, binding = 4, std430) buffer CBuffer { float c []; };

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
            #version 450
            layout(local_size_x = 256) in;

            layout(set = 0, binding = 0, std430) buffer AAlphaBuffer { float aAlpha[]; };
            layout(set = 0, binding = 1, std430) buffer ABuffer { float a []; };
            layout(set = 0, binding = 2, std430) buffer BAlphaBuffer { float bAlpha []; };
            layout(set = 0, binding = 3, std430) buffer BBuffer { float b []; };
            layout(set = 0, binding = 4, std430) buffer CBuffer { float c []; };

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
    }

    internal static class VulkanComputerRunner
    {
        private static async Task<(NativeVulkanSurfaceView view, NativeVulkanSurfaceViewHandler handler, VulkanComputeView computeView)>
            CreateAndAttachAsync(string shaderSource, float[][] inputs, OutputElementType outputElementType)
        {
            var accelerator = new NativeVulkanSurfaceView
            {
                ShaderSource = shaderSource,
                Inputs = inputs,
                WidthRequest = 50,
                HeightRequest = 50,
                WorkGroupSize = 256,
                ShaderKind = ShaderKind.ComputeShader,
                OutputElementType = outputElementType
            };

            var handlerReadyTcs = new TaskCompletionSource<NativeVulkanSurfaceViewHandler>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnHandlerChanged(object? sender, EventArgs e)
            {
                if (accelerator.Handler is NativeVulkanSurfaceViewHandler handler)
                {
                    accelerator.HandlerChanged -= OnHandlerChanged;
                    handlerReadyTcs.TrySetResult(handler);
                }
            }

            accelerator.HandlerChanged += OnHandlerChanged;

            var addViewHandler = ComputerHelper.AddPlatformComputeViewHandler;
            if (addViewHandler is null)
            {
                accelerator.HandlerChanged -= OnHandlerChanged;
                throw new InvalidOperationException("AddVulkanViewHandler is not registered.");
            }

            addViewHandler.Invoke(accelerator);

            if (accelerator.Handler is NativeVulkanSurfaceViewHandler existingHandler)
            {
                accelerator.HandlerChanged -= OnHandlerChanged;
                handlerReadyTcs.TrySetResult(existingHandler);
            }

            var handlerWaitTask = handlerReadyTcs.Task;
            if (await Task.WhenAny(handlerWaitTask, Task.Delay(TimeSpan.FromSeconds(10))) != handlerWaitTask)
            {
                accelerator.HandlerChanged -= OnHandlerChanged;
                throw new TimeoutException("Vulkan handler creation timed out after 10 seconds.");
            }

            var handler = await handlerWaitTask;
            if (handler.PlatformView is not VulkanComputeView vkView)
            {
                throw new InvalidOperationException("Vulkan accelerator is not ready or not attached.");
            }

            var readyTask = vkView.WaitUntilReadyAsync();
            if (await Task.WhenAny(readyTask, Task.Delay(TimeSpan.FromMilliseconds(ComputerHelper.Timeout))) != readyTask)
            {
                throw new TimeoutException("VulkanComputeView.WaitUntilReadyAsync timed out after 30 seconds.");
            }

            await readyTask;
            return (accelerator, handler, vkView);
        }

        public static object[] EnqueueCompute(Func<Task<object[]>> mainThreadWork, string timeoutMessage)
        {
            return ComputerHelper.EnqueueCompute(() =>
            {
                var mainThreadTask = MainThread.InvokeOnMainThreadAsync(mainThreadWork);
                if (!mainThreadTask.Wait(TimeSpan.FromSeconds(60)))
                {
                    throw new TimeoutException(timeoutMessage);
                }

                var result = (object[])TaskHelper.SyncWait(() => mainThreadTask, CancellationToken.None);
                if (result is null)
                {
                    throw new InvalidOperationException("Vulkan compute returned null result.");
                }

                return result;
            });
        }

        public static Task<(NativeVulkanSurfaceView view, NativeVulkanSurfaceViewHandler handler, VulkanComputeView computeView)>
            CreateAcceleratorAsync(string shaderSource, float[][] inputs, OutputElementType outputElementType)
            => CreateAndAttachAsync(shaderSource, inputs, outputElementType);
    }

    public class VulkanOverlayComputer : IComputer, IOverlayComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "Overlay";

        public BlendResult8 Overlay8(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var result = Compute([top, bottom, topAlpha, bottomAlpha, 8, pixelCount]);
            return new BlendResult8((byte[])result[0], (float[])result[1]);
        }

        public BlendResult16 Overlay16(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var result = Compute([top, bottom, topAlpha, bottomAlpha, 16, pixelCount]);
            return new BlendResult16((ushort[])result[0], (float[])result[1]);
        }

        public BlendResultHdr OverlayHdr(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var result = Compute([top, bottom, topAlpha, bottomAlpha, 0, pixelCount]);
            return new BlendResultHdr((float[])result[0], (float[])result[1]);
        }

        public object[] Compute(object[] args)
        {
            var a = args[0] as float[];
            var b = args[1] as float[];
            var aAlpha = args.Length > 2 ? (args[2] as float[]) : null;
            var bAlpha = args.Length > 3 ? (args[3] as float[]) : null;
            var outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            var actualPixels = args.Length > 5 ? Convert.ToInt32(args[5]) : a?.Length ?? 0;

            float[] trimmedA = new float[actualPixels];
            float[] trimmedB = new float[actualPixels];
            Array.Copy(a!, 0, trimmedA, 0, actualPixels);
            Array.Copy(b!, 0, trimmedB, 0, actualPixels);

            if (aAlpha == null) aAlpha = Enumerable.Repeat(1f, actualPixels).ToArray();
            if (bAlpha == null) bAlpha = Enumerable.Repeat(1f, actualPixels).ToArray();

            if (aAlpha.Length != actualPixels || bAlpha.Length != actualPixels)
            {
                float[] trimmedAAlpha = new float[actualPixels];
                float[] trimmedBAlpha = new float[actualPixels];
                Array.Copy(aAlpha, 0, trimmedAAlpha, 0, Math.Min(aAlpha.Length, actualPixels));
                Array.Copy(bAlpha, 0, trimmedBAlpha, 0, Math.Min(bAlpha.Length, actualPixels));
                aAlpha = trimmedAAlpha;
                bAlpha = trimmedBAlpha;
            }

            return VulkanComputerRunner.EnqueueCompute(async () =>
            {
                var (accelerator, handler, vkView) = await VulkanComputerRunner.CreateAcceleratorAsync(
                    VulkanShaderLibrary.Alpha,
                    new float[][] { aAlpha, bAlpha },
                    OutputElementType.Float32);

                var alphaResult = (float[])await vkView.RunComputeAsync(OutputElementType.Float32);

                if (outputBpp == 8)
                {
                    accelerator.ShaderSource = VulkanShaderLibrary.ShaderColorSrcU8;
                    accelerator.OutputElementType = OutputElementType.UInt32;
                }
                else if (outputBpp == 16)
                {
                    accelerator.ShaderSource = VulkanShaderLibrary.ShaderColorSrcU16;
                    accelerator.OutputElementType = OutputElementType.UInt32;
                }
                else
                {
                    accelerator.ShaderSource = VulkanShaderLibrary.ShaderColorSrc;
                    accelerator.OutputElementType = OutputElementType.Float32;
                }

                accelerator.Inputs = new float[][] { aAlpha, trimmedA, bAlpha, trimmedB };
                NativeVulkanSurfaceViewHandler.MapInputs(handler, accelerator);

                var colorResult = await vkView.RunComputeAsync(accelerator.OutputElementType);

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
            }, "VulkanOverlayComputer.Compute timed out after 60 seconds - likely deadlock due to main thread congestion.");
        }
    }

    public class VulkanApproximateOverlayComputer : IComputer, IApproximateOverlayComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "OverlayApproximate";

        public BlendResult8 ApproximateOverlay8(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var result = Compute([top, bottom, topAlpha, bottomAlpha, 8, pixelCount]);
            return new BlendResult8((byte[])result[0], (float[])result[1]);
        }

        public BlendResult16 ApproximateOverlay16(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var result = Compute([top, bottom, topAlpha, bottomAlpha, 16, pixelCount]);
            return new BlendResult16((ushort[])result[0], (float[])result[1]);
        }

        public BlendResultHdr ApproximateOverlayHdr(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var result = Compute([top, bottom, topAlpha, bottomAlpha, 0, pixelCount]);
            return new BlendResultHdr((float[])result[0], (float[])result[1]);
        }

        public object[] Compute(object[] args)
        {
            var a = args[0] as float[];
            var b = args[1] as float[];
            var aAlpha = args.Length > 2 ? (args[2] as float[]) : null;
            var bAlpha = args.Length > 3 ? (args[3] as float[]) : null;
            var outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            var actualPixels = args.Length > 5 ? Convert.ToInt32(args[5]) : a?.Length ?? 0;

            float[] trimmedA = new float[actualPixels];
            float[] trimmedB = new float[actualPixels];
            Array.Copy(a!, 0, trimmedA, 0, actualPixels);
            Array.Copy(b!, 0, trimmedB, 0, actualPixels);

            if (aAlpha == null) aAlpha = Enumerable.Repeat(1f, actualPixels).ToArray();
            if (bAlpha == null) bAlpha = Enumerable.Repeat(1f, actualPixels).ToArray();

            if (aAlpha.Length != actualPixels || bAlpha.Length != actualPixels)
            {
                float[] trimmedAAlpha = new float[actualPixels];
                float[] trimmedBAlpha = new float[actualPixels];
                Array.Copy(aAlpha, 0, trimmedAAlpha, 0, Math.Min(aAlpha.Length, actualPixels));
                Array.Copy(bAlpha, 0, trimmedBAlpha, 0, Math.Min(bAlpha.Length, actualPixels));
                aAlpha = trimmedAAlpha;
                bAlpha = trimmedBAlpha;
            }

            return VulkanComputerRunner.EnqueueCompute(async () =>
            {
                var (accelerator, handler, vkView) = await VulkanComputerRunner.CreateAcceleratorAsync(
                    VulkanShaderLibrary.Alpha,
                    new float[][] { aAlpha, bAlpha },
                    OutputElementType.Float32);

                var alphaResult = (float[])await vkView.RunComputeAsync(OutputElementType.Float32);

                if (outputBpp == 8)
                {
                    accelerator.ShaderSource = VulkanShaderLibrary.ShaderColorSrcU8;
                    accelerator.OutputElementType = OutputElementType.UInt32;
                }
                else if (outputBpp == 16)
                {
                    accelerator.ShaderSource = VulkanShaderLibrary.ShaderColorSrcU16;
                    accelerator.OutputElementType = OutputElementType.UInt32;
                }
                else
                {
                    accelerator.ShaderSource = VulkanShaderLibrary.ShaderColorSrc;
                    accelerator.OutputElementType = OutputElementType.Float32;
                }

                accelerator.Inputs = new float[][] { aAlpha, trimmedA, bAlpha, trimmedB };
                NativeVulkanSurfaceViewHandler.MapInputs(handler, accelerator);

                var colorResult = await vkView.RunComputeAsync(accelerator.OutputElementType);

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
            }, "VulkanApproximateComputer.Compute timed out after 60 seconds - likely deadlock due to main thread congestion.");
        }
    }

    public class VulkanResizeComputer : IComputer, IResizeComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "Resize";

        public FourChannelResult ComputeResizeFloat(float[] r, float[] g, float[] b, float[] a, float srcW, float srcH, float dstW, float dstH)
        {
            var result = Compute([r, g, b, a, srcW, srcH, dstW, dstH]);
            return new FourChannelResult((float[])result[0], (float[])result[1], (float[])result[2], (float[])result[3]);
        }

        public FourChannelResult8 ComputeResizeByte(float[] r, float[] g, float[] b, float[] a, float srcW, float srcH, float dstW, float dstH)
        {
            var result = Compute([r, g, b, a, srcW, srcH, dstW, dstH, 8]);
            return new FourChannelResult8((byte[])result[0], (byte[])result[1], (byte[])result[2], (float[])result[3]);
        }

        public FourChannelResult16 ComputeResizeUshort(float[] r, float[] g, float[] b, float[] a, float srcW, float srcH, float dstW, float dstH)
        {
            var result = Compute([r, g, b, a, srcW, srcH, dstW, dstH, 16]);
            return new FourChannelResult16((ushort[])result[0], (ushort[])result[1], (ushort[])result[2], (float[])result[3]);
        }

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
            int vkLength = Math.Max(srcLength, dstLength);

            float[] rPad = PreparePaddedChannel(rIn, srcLength, vkLength);
            float[] gPad = PreparePaddedChannel(gIn, srcLength, vkLength);
            float[] bPad = PreparePaddedChannel(bIn, srcLength, vkLength);
            float[] aPad = PreparePaddedChannel(aIn, srcLength, vkLength);

            float ratioX = (float)srcW / dstW;
            float ratioY = (float)srcH / dstH;
            string shader = VulkanShaderLibrary.BuildResizeShader(dstLength, dstW, srcW, srcH, ratioX, ratioY);

            return VulkanComputerRunner.EnqueueCompute(async () =>
            {
                var (accelerator, handler, vkView) = await VulkanComputerRunner.CreateAcceleratorAsync(
                    shader,
                    new float[][] { rPad },
                    OutputElementType.Float32);

                async Task<float[]> RunChannel(float[] channel)
                {
                    accelerator.Inputs = new float[][] { channel };
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

                return new object[] { rOut, gOut, bOut, aOut };
            }, "VulkanResizeComputer.Compute timed out after 60 seconds - likely deadlock due to main thread congestion.");
        }

        private static float[] PreparePaddedChannel(float[] source, int sourceLength, int paddedLength)
        {
            var arr = new float[paddedLength];
            Array.Copy(source, 0, arr, 0, Math.Min(Math.Min(source.Length, sourceLength), paddedLength));
            return arr;
        }
    }

    public class VulkanCropComputer : IComputer, ICropComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "Crop";

        public FourChannelResult ComputeCrop(float[] r, float[] g, float[] b, float[] a, int srcW, int srcH, int startX, int startY, int cropW, int cropH)
        {
            var result = Compute([r, g, b, a, srcW, srcH, startX, startY, cropW, cropH]);
            return new FourChannelResult((float[])result[0], (float[])result[1], (float[])result[2], (float[])result[3]);
        }

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
            int vkLength = Math.Max(srcLength, dstLength);

            float[] rPad = PreparePaddedChannel(rIn, srcLength, vkLength);
            float[] gPad = PreparePaddedChannel(gIn, srcLength, vkLength);
            float[] bPad = PreparePaddedChannel(bIn, srcLength, vkLength);
            float[] aPad = PreparePaddedChannel(aIn, srcLength, vkLength);

            string shader = VulkanShaderLibrary.BuildCropShader(dstLength, cropW, startX, startY, srcW, srcH);

            return VulkanComputerRunner.EnqueueCompute(async () =>
            {
                var (accelerator, handler, vkView) = await VulkanComputerRunner.CreateAcceleratorAsync(
                    shader,
                    new float[][] { rPad },
                    OutputElementType.Float32);

                async Task<float[]> RunChannel(float[] channel)
                {
                    accelerator.Inputs = new float[][] { channel };
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

                return new object[] { rOut, gOut, bOut, aOut };
            }, "VulkanCropComputer.Compute timed out after 60 seconds - likely deadlock due to main thread congestion.");
        }

        private static float[] PreparePaddedChannel(float[] source, int sourceLength, int paddedLength)
        {
            var arr = new float[paddedLength];
            Array.Copy(source, 0, arr, 0, Math.Min(Math.Min(source.Length, sourceLength), paddedLength));
            return arr;
        }
    }

    public class VulkanPlaceComputer : IComputer, IPlaceComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "Place";

        public FourChannelResult ComputePlace(float[] r, float[] g, float[] b, float[] a, int srcW, int srcH, int startX, int startY, int targetW, int targetH)
        {
            var result = Compute([r, g, b, a, srcW, srcH, startX, startY, targetW, targetH]);
            return new FourChannelResult((float[])result[0], (float[])result[1], (float[])result[2], (float[])result[3]);
        }

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
            int vkLength = Math.Max(srcLength, dstLength);

            float[] rPad = PreparePaddedChannel(rIn, srcLength, vkLength);
            float[] gPad = PreparePaddedChannel(gIn, srcLength, vkLength);
            float[] bPad = PreparePaddedChannel(bIn, srcLength, vkLength);
            float[] aPad = PreparePaddedChannel(aIn, srcLength, vkLength);

            string shader = VulkanShaderLibrary.BuildPlaceShader(dstLength, dstW, srcW, srcH, startX, startY);

            return VulkanComputerRunner.EnqueueCompute(async () =>
            {
                var (accelerator, handler, vkView) = await VulkanComputerRunner.CreateAcceleratorAsync(
                    shader,
                    new float[][] { rPad },
                    OutputElementType.Float32);

                async Task<float[]> RunChannel(float[] channel)
                {
                    accelerator.Inputs = new float[][] { channel };
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

                return new object[] { rOut, gOut, bOut, aOut };
            }, "VulkanPlaceComputer.Compute timed out after 60 seconds - likely deadlock due to main thread congestion.");
        }

        private static float[] PreparePaddedChannel(float[] source, int sourceLength, int paddedLength)
        {
            var arr = new float[paddedLength];
            Array.Copy(source, 0, arr, 0, Math.Min(Math.Min(source.Length, sourceLength), paddedLength));
            return arr;
        }
    }

    public class VulkanRemoveColorComputer : IComputer, IRemoveColorComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "RemoveColor";

        public float[] ComputeRemoveColor(float[] r, float[] g, float[] b, float[] a, float targetR, float targetG, float targetB, float range, int pixels)
        {
            var result = Compute([r, g, b, a, targetR, targetG, targetB, range, pixels]);
            return (float[])result[0];
        }

        public object[] Compute(object[] args)
        {
            var aR = args[0] as float[];
            var aG = args[1] as float[];
            var aB = args[2] as float[];
            var sourceA = args[3] as float[];

            if (aR is null || aG is null || aB is null || sourceA is null)
            {
                throw new ArgumentException("VulkanRemoveColorComputer expects four float[] channel arrays.");
            }

            var toRemoveR = (ushort)args[4];
            var toRemoveG = (ushort)args[5];
            var toRemoveB = (ushort)args[6];
            var range = (ushort)args[7];

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

            string shader = VulkanShaderLibrary.BuildRemoveColorShader(lowR, highR, lowG, highG, lowB, highB);

            return VulkanComputerRunner.EnqueueCompute(async () =>
            {
                var (_, _, vkView) = await VulkanComputerRunner.CreateAcceleratorAsync(
                    shader,
                    new float[][] { aR, aG, aB, sourceA },
                    OutputElementType.Float32);

                var result = await vkView.RunComputeAsync(OutputElementType.Float32);
                return new object[] { result };
            }, "VulkanRemoveColorComputer.Compute timed out after 60 seconds - likely deadlock due to main thread congestion.");
        }
    }

    internal static class BlendModeVulkanHelper
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

            return VulkanComputerRunner.EnqueueCompute(async () =>
            {
                var (accelerator, handler, vkView) = await VulkanComputerRunner.CreateAcceleratorAsync(
                    VulkanShaderLibrary.Alpha,
                    new float[][] { topAlpha!, bottomAlpha! },
                    OutputElementType.Float32);

                var alphaResult = (float[])await vkView.RunComputeAsync(OutputElementType.Float32);

                accelerator.ShaderSource = colorShader;
                accelerator.OutputElementType = OutputElementType.Float32;
                accelerator.Inputs = new float[][] { topAlpha!, trimmedTop, bottomAlpha!, trimmedBottom };
                NativeVulkanSurfaceViewHandler.MapInputs(handler, accelerator);

                var colorResult = (float[])await vkView.RunComputeAsync(OutputElementType.Float32);

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
            }, "VulkanBlendMode.ComputeBlend timed out after 60 seconds.");
        }
    }

    public class VulkanBlendAddComputer : IComputer, IBlendModeComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "AddComputer";

        public BlendResult16 ComputeBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var result = Compute([top, bottom, topAlpha, bottomAlpha, 16, pixelCount]);
            return new BlendResult16((ushort[])result[0], (float[])result[1]);
        }

        public object[] Compute(object[] args)
        {
            var top = args[0] as float[] ?? throw new ArgumentException("Invalid top channel");
            var bottom = args[1] as float[] ?? throw new ArgumentException("Invalid base channel");
            var topAlpha = (float[]?)args[2];
            var bottomAlpha = (float[]?)args[3];
            int outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            int actualPixels = args.Length > 5 ? Convert.ToInt32(args[5]) : top.Length;
            return BlendModeVulkanHelper.ComputeBlend(top, bottom, topAlpha, bottomAlpha, outputBpp, actualPixels, VulkanShaderLibrary.BlendColorSrcAdd);
        }
    }

    public class VulkanBlendSubtractComputer : IComputer, IBlendModeComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "SubtractComputer";

        public BlendResult16 ComputeBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var result = Compute([top, bottom, topAlpha, bottomAlpha, 16, pixelCount]);
            return new BlendResult16((ushort[])result[0], (float[])result[1]);
        }

        public object[] Compute(object[] args)
        {
            var top = args[0] as float[] ?? throw new ArgumentException("Invalid top channel");
            var bottom = args[1] as float[] ?? throw new ArgumentException("Invalid base channel");
            var topAlpha = (float[]?)args[2];
            var bottomAlpha = (float[]?)args[3];
            int outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            int actualPixels = args.Length > 5 ? Convert.ToInt32(args[5]) : top.Length;
            return BlendModeVulkanHelper.ComputeBlend(top, bottom, topAlpha, bottomAlpha, outputBpp, actualPixels, VulkanShaderLibrary.BlendColorSrcSubtract);
        }
    }

    public class VulkanBlendMultiplyComputer : IComputer, IBlendModeComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "MultiplyComputer";

        public BlendResult16 ComputeBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var result = Compute([top, bottom, topAlpha, bottomAlpha, 16, pixelCount]);
            return new BlendResult16((ushort[])result[0], (float[])result[1]);
        }

        public object[] Compute(object[] args)
        {
            var top = args[0] as float[] ?? throw new ArgumentException("Invalid top channel");
            var bottom = args[1] as float[] ?? throw new ArgumentException("Invalid base channel");
            var topAlpha = (float[]?)args[2];
            var bottomAlpha = (float[]?)args[3];
            int outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            int actualPixels = args.Length > 5 ? Convert.ToInt32(args[5]) : top.Length;
            return BlendModeVulkanHelper.ComputeBlend(top, bottom, topAlpha, bottomAlpha, outputBpp, actualPixels, VulkanShaderLibrary.BlendColorSrcMultiply);
        }
    }

    public class VulkanBlendScreenComputer : IComputer, IBlendModeComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "ScreenComputer";

        public BlendResult16 ComputeBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var result = Compute([top, bottom, topAlpha, bottomAlpha, 16, pixelCount]);
            return new BlendResult16((ushort[])result[0], (float[])result[1]);
        }

        public object[] Compute(object[] args)
        {
            var top = args[0] as float[] ?? throw new ArgumentException("Invalid top channel");
            var bottom = args[1] as float[] ?? throw new ArgumentException("Invalid base channel");
            var topAlpha = (float[]?)args[2];
            var bottomAlpha = (float[]?)args[3];
            int outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            int actualPixels = args.Length > 5 ? Convert.ToInt32(args[5]) : top.Length;
            return BlendModeVulkanHelper.ComputeBlend(top, bottom, topAlpha, bottomAlpha, outputBpp, actualPixels, VulkanShaderLibrary.BlendColorSrcScreen);
        }
    }

    public class VulkanBlendOverlayBlendComputer : IComputer, IBlendModeComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "OverlayBlendComputer";

        public BlendResult16 ComputeBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var result = Compute([top, bottom, topAlpha, bottomAlpha, 16, pixelCount]);
            return new BlendResult16((ushort[])result[0], (float[])result[1]);
        }

        public object[] Compute(object[] args)
        {
            var top = args[0] as float[] ?? throw new ArgumentException("Invalid top channel");
            var bottom = args[1] as float[] ?? throw new ArgumentException("Invalid base channel");
            var topAlpha = (float[]?)args[2];
            var bottomAlpha = (float[]?)args[3];
            int outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            int actualPixels = args.Length > 5 ? Convert.ToInt32(args[5]) : top.Length;
            return BlendModeVulkanHelper.ComputeBlend(top, bottom, topAlpha, bottomAlpha, outputBpp, actualPixels, VulkanShaderLibrary.BlendColorSrcOverlayBlend);
        }
    }

    public class VulkanBlendDarkenComputer : IComputer, IBlendModeComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "DarkenComputer";

        public BlendResult16 ComputeBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var result = Compute([top, bottom, topAlpha, bottomAlpha, 16, pixelCount]);
            return new BlendResult16((ushort[])result[0], (float[])result[1]);
        }

        public object[] Compute(object[] args)
        {
            var top = args[0] as float[] ?? throw new ArgumentException("Invalid top channel");
            var bottom = args[1] as float[] ?? throw new ArgumentException("Invalid base channel");
            var topAlpha = (float[]?)args[2];
            var bottomAlpha = (float[]?)args[3];
            int outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            int actualPixels = args.Length > 5 ? Convert.ToInt32(args[5]) : top.Length;
            return BlendModeVulkanHelper.ComputeBlend(top, bottom, topAlpha, bottomAlpha, outputBpp, actualPixels, VulkanShaderLibrary.BlendColorSrcDarken);
        }
    }

    public class VulkanBlendLightenComputer : IComputer, IBlendModeComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "LightenComputer";

        public BlendResult16 ComputeBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var result = Compute([top, bottom, topAlpha, bottomAlpha, 16, pixelCount]);
            return new BlendResult16((ushort[])result[0], (float[])result[1]);
        }

        public object[] Compute(object[] args)
        {
            var top = args[0] as float[] ?? throw new ArgumentException("Invalid top channel");
            var bottom = args[1] as float[] ?? throw new ArgumentException("Invalid base channel");
            var topAlpha = (float[]?)args[2];
            var bottomAlpha = (float[]?)args[3];
            int outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            int actualPixels = args.Length > 5 ? Convert.ToInt32(args[5]) : top.Length;
            return BlendModeVulkanHelper.ComputeBlend(top, bottom, topAlpha, bottomAlpha, outputBpp, actualPixels, VulkanShaderLibrary.BlendColorSrcLighten);
        }
    }

    public class VulkanBlendDifferenceComputer : IComputer, IBlendModeComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "DifferenceComputer";

        public BlendResult16 ComputeBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var result = Compute([top, bottom, topAlpha, bottomAlpha, 16, pixelCount]);
            return new BlendResult16((ushort[])result[0], (float[])result[1]);
        }

        public object[] Compute(object[] args)
        {
            var top = args[0] as float[] ?? throw new ArgumentException("Invalid top channel");
            var bottom = args[1] as float[] ?? throw new ArgumentException("Invalid base channel");
            var topAlpha = (float[]?)args[2];
            var bottomAlpha = (float[]?)args[3];
            int outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            int actualPixels = args.Length > 5 ? Convert.ToInt32(args[5]) : top.Length;
            return BlendModeVulkanHelper.ComputeBlend(top, bottom, topAlpha, bottomAlpha, outputBpp, actualPixels, VulkanShaderLibrary.BlendColorSrcDifference);
        }
    }

    internal static class VulkanSinglePassHelper
    {
        internal static object[] ComputeSinglePass(object[] args, string shader, string name)
        {
            var rIn = args[0] as float[] ?? throw new ArgumentException("Invalid argument for R");
            var gIn = args[1] as float[] ?? throw new ArgumentException("Invalid argument for G");
            var bIn = args[2] as float[] ?? throw new ArgumentException("Invalid argument for B");
            var aIn = args[3] as float[] ?? throw new ArgumentException("Invalid argument for A");
            int len = rIn.Length;

            return VulkanComputerRunner.EnqueueCompute(async () =>
            {
                var (accelerator, handler, vkView) = await VulkanComputerRunner.CreateAcceleratorAsync(shader, new float[][] { rIn }, OutputElementType.Float32);

                async Task<float[]> RunChannel(float[] channel)
                {
                    accelerator.Inputs = new float[][] { channel };
                    NativeVulkanSurfaceViewHandler.MapInputs(handler, accelerator);
                    var raw = (float[])await vkView.RunComputeAsync(OutputElementType.Float32);
                    if (raw.Length == len) return raw;
                    var trimmed = new float[len];
                    Array.Copy(raw, 0, trimmed, 0, Math.Min(raw.Length, len));
                    return trimmed;
                }

                var rOut = await RunChannel(rIn);
                var gOut = await RunChannel(gIn);
                var bOut = await RunChannel(bIn);
                var aOut = await RunChannel(aIn);
                return new object[] { rOut, gOut, bOut, aOut };
            }, $"{name}.Compute timed out.");
        }
    }

    public class VulkanOpacityComputer : IComputer, IOpacityComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "FadeOpacity";
        public FourChannelResult ComputeOpacity(float[] r, float[] g, float[] b, float[] a, float opacity)
        {
            var result = Compute([r, g, b, a, opacity]);
            return new FourChannelResult((float[])result[0], (float[])result[1], (float[])result[2], (float[])result[3]);
        }

        public object[] Compute(object[] args)
        {
            float opacity = Convert.ToSingle(args[4]);
            int len = ((float[])args[0]).Length;
            return VulkanSinglePassHelper.ComputeSinglePass(args, $$"""
                #version 450
                layout(local_size_x = 256) in;
                layout(set = 0, binding = 0, std430) buffer InBuf { float i[]; };
                layout(set = 0, binding = 4, std430) buffer OutBuf { float o[]; };
                void main() {
                    uint idx = gl_GlobalInvocationID.x;
                    if (idx >= {{len}}) { o[idx] = 0.0; return; }
                    o[idx] = i[idx];
                }
                """, "VulkanOpacityComputer");
        }
    }

    public class VulkanVignetteComputer : IComputer, IVignetteComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "Vignette";
        public FourChannelResult ComputeVignette(float[] r, float[] g, float[] b, float[] a, int w, int h, float strength, float radius)
        {
            var result = Compute([r, g, b, a, w, h, strength, radius]);
            return new FourChannelResult((float[])result[0], (float[])result[1], (float[])result[2], (float[])result[3]);
        }

        public object[] Compute(object[] args)
        {
            int w = Convert.ToInt32(args[4]), h = Convert.ToInt32(args[5]);
            float strength = Convert.ToSingle(args[6]), radius = Convert.ToSingle(args[7]);
            int len = ((float[])args[0]).Length;
            return VulkanSinglePassHelper.ComputeSinglePass(args, $$"""
                #version 450
                layout(local_size_x = 256) in;
                layout(set = 0, binding = 0, std430) buffer InBuf { float i[]; };
                layout(set = 0, binding = 4, std430) buffer OutBuf { float o[]; };
                void main() {
                    uint idx = gl_GlobalInvocationID.x;
                    if (idx >= {{len}}) { o[idx] = 0.0; return; }
                    int x = int(idx % {{w}}), y = int(idx / {{w}});
                    float cx = float({{w}}) * 0.5, cy = float({{h}}) * 0.5;
                    float dx = (float(x) - cx) / cx, dy = (float(y) - cy) / cy;
                    float dist = sqrt(dx*dx + dy*dy);
                    float factor = 1.0;
                    if (dist > {{radius}}) {
                        float t = min((dist - {{radius}}) / (1.0 - {{radius}}), 1.0);
                        factor = 1.0 - t*t*{{strength}};
                    }
                    o[idx] = i[idx] * factor;
                }
                """, "VulkanVignetteComputer");
        }
    }

    public class VulkanFlipComputer : IComputer, IFlipComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "Flip";
        public FourChannelResult ComputeFlip(float[] r, float[] g, float[] b, float[] a, int w, int h, bool horizontal, bool vertical)
        {
            var result = Compute([r, g, b, a, w, h, horizontal, vertical]);
            return new FourChannelResult((float[])result[0], (float[])result[1], (float[])result[2], (float[])result[3]);
        }

        public object[] Compute(object[] args)
        {
            int w = Convert.ToInt32(args[4]), h = Convert.ToInt32(args[5]);
            int horiz = Convert.ToBoolean(args[6]) ? 1 : 0, vert = Convert.ToBoolean(args[7]) ? 1 : 0;
            int len = ((float[])args[0]).Length;
            return VulkanSinglePassHelper.ComputeSinglePass(args, $$"""
                #version 450
                layout(local_size_x = 256) in;
                layout(set = 0, binding = 0, std430) buffer InBuf { float i[]; };
                layout(set = 0, binding = 4, std430) buffer OutBuf { float o[]; };
                void main() {
                    uint idx = gl_GlobalInvocationID.x;
                    if (idx >= {{len}}) { o[idx] = 0.0; return; }
                    int x = int(idx % {{w}}), y = int(idx / {{w}});
                    int sx = ({{horiz}} != 0) ? {{w}} - 1 - x : x;
                    int sy = ({{vert}} != 0) ? {{h}} - 1 - y : y;
                    o[idx] = i[sy * {{w}} + sx];
                }
                """, "VulkanFlipComputer");
        }
    }

    public class VulkanSharpenComputer : IComputer, ISharpenComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "Sharpen";
        public FourChannelResult ComputeSharpen(float[] r, float[] g, float[] b, float[] a, int w, float amount)
        {
            var result = Compute([r, g, b, a, w, amount]);
            return new FourChannelResult((float[])result[0], (float[])result[1], (float[])result[2], (float[])result[3]);
        }

        public object[] Compute(object[] args)
        {
            int w = Convert.ToInt32(args[4]);
            float amount = Convert.ToSingle(args[5]);
            int len = ((float[])args[0]).Length;
            return VulkanSinglePassHelper.ComputeSinglePass(args, $$"""
                #version 450
                layout(local_size_x = 256) in;
                layout(set = 0, binding = 0, std430) buffer InBuf { float i[]; };
                layout(set = 0, binding = 4, std430) buffer OutBuf { float o[]; };
                void main() {
                    uint idx = gl_GlobalInvocationID.x;
                    if (idx >= {{len}}) { o[idx] = 0.0; return; }
                    int x = int(idx % {{w}});
                    float orig = i[idx];
                    int left = x > 0 ? int(idx) - 1 : int(idx);
                    int right = x < {{w}} - 1 ? int(idx) + 1 : int(idx);
                    int top = int(idx) - {{w}}; if (top < 0) top = int(idx);
                    int bottom = int(idx) + {{w}}; if (bottom >= {{len}}) bottom = int(idx);
                    float avg = (i[left] + i[right] + i[top] + i[bottom]) * 0.25;
                    o[idx] = orig + {{amount}} * (orig - avg);
                }
                """, "VulkanSharpenComputer");
        }
    }

    public class VulkanRotationComputer : IComputer, IRotationComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "Rotation";
        public FourChannelResult ComputeRotation(float[] r, float[] g, float[] b, float[] a, int srcW, int srcH, int dstW, int dstH, float angleDeg)
        {
            var result = Compute([r, g, b, a, srcW, srcH, dstW, dstH, angleDeg]);
            return new FourChannelResult((float[])result[0], (float[])result[1], (float[])result[2], (float[])result[3]);
        }

        public object[] Compute(object[] args)
        {
            var rIn = args[0] as float[] ?? throw new ArgumentException();
            var gIn = args[1] as float[] ?? throw new ArgumentException();
            var bIn = args[2] as float[] ?? throw new ArgumentException();
            var aIn = args[3] as float[] ?? throw new ArgumentException();
            int srcW = Convert.ToInt32(args[4]), srcH = Convert.ToInt32(args[5]);
            int outW = Convert.ToInt32(args[6]), outH = Convert.ToInt32(args[7]);
            float angleDeg = Convert.ToSingle(args[8]);
            int srcLen = checked(srcW * srcH), dstLen = checked(outW * outH);
            int vkLen = Math.Max(srcLen, dstLen);

            float[] Pad(float[] s) { var a = new float[vkLen]; Array.Copy(s, 0, a, 0, Math.Min(s.Length, srcLen)); return a; }
            var rP = Pad(rIn); var gP = Pad(gIn); var bP = Pad(bIn); var aP = Pad(aIn);

            string shader = $$"""
                #version 450
                layout(local_size_x = 256) in;
                layout(set = 0, binding = 0, std430) buffer InBuf { float i[]; };
                layout(set = 0, binding = 4, std430) buffer OutBuf { float o[]; };
                void main() {
                    uint idx = gl_GlobalInvocationID.x;
                    if (idx >= {{dstLen}}) { o[idx] = 0.0; return; }
                    int x = int(idx % {{outW}}), y = int(idx / {{outW}});
                    float ar = {{(angleDeg * MathF.PI / 180f)}};
                    float cosA = cos(-ar), sinA = sin(-ar);
                    float scx = float({{srcW}}) * 0.5, scy = float({{srcH}}) * 0.5;
                    float ocx = float({{outW}}) * 0.5, ocy = float({{outH}}) * 0.5;
                    float ox = float(x) - ocx, oy = float(y) - ocy;
                    float sx = cosA * ox - sinA * oy + scx;
                    float sy = sinA * ox + cosA * oy + scy;
                    if (sx >= 0.0 && sx < float({{srcW}}-1) && sy >= 0.0 && sy < float({{srcH}}-1)) {
                        int sx0 = int(sx), sy0 = int(sy), sx1 = sx0+1, sy1 = sy0+1;
                        float fx = sx - float(sx0), fy = sy - float(sy0);
                        int i00 = sy0*{{srcW}}+sx0, i10 = sy0*{{srcW}}+sx1;
                        int i01 = sy1*{{srcW}}+sx0, i11 = sy1*{{srcW}}+sx1;
                        o[idx] = ((i[i00]*(1.0-fx)+i[i10]*fx)*(1.0-fy)+(i[i01]*(1.0-fx)+i[i11]*fx)*fy);
                    } else { o[idx] = 0.0; }
                }
                """;

            return VulkanComputerRunner.EnqueueCompute(async () =>
            {
                var (acc, handler, vkView) = await VulkanComputerRunner.CreateAcceleratorAsync(shader, new float[][] { rP }, OutputElementType.Float32);
                async Task<float[]> RunCh(float[] ch)
                {
                    acc.Inputs = new float[][] { ch }; NativeVulkanSurfaceViewHandler.MapInputs(handler, acc);
                    var raw = (float[])await vkView.RunComputeAsync(OutputElementType.Float32);
                    if (raw.Length == dstLen) return raw;
                    var t = new float[dstLen]; Array.Copy(raw, 0, t, 0, Math.Min(raw.Length, dstLen)); return t;
                }
                return new object[] { await RunCh(rP), await RunCh(gP), await RunCh(bP), await RunCh(aP) };
            }, "VulkanRotationComputer timed out.");
        }
    }

    public class VulkanBlurComputer : IComputer, IBlurComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "Blur";
        public FourChannelResult ComputeBlur(float[] r, float[] g, float[] b, float[] a, int w, float sigma)
        {
            var result = Compute([r, g, b, a, w, sigma]);
            return new FourChannelResult((float[])result[0], (float[])result[1], (float[])result[2], (float[])result[3]);
        }

        public object[] Compute(object[] args)
        {
            var rIn = args[0] as float[] ?? throw new ArgumentException();
            var gIn = args[1] as float[] ?? throw new ArgumentException();
            var bIn = args[2] as float[] ?? throw new ArgumentException();
            var aIn = args[3] as float[] ?? throw new ArgumentException();
            int w = Convert.ToInt32(args[4]), len = rIn.Length, h = len / w;
            int radius = Math.Max(1, (int)MathF.Ceiling(Convert.ToSingle(args[5])));

            string hShader = $$"""
                #version 450
                layout(local_size_x = 256) in;
                layout(set = 0, binding = 0, std430) buffer InBuf { float i[]; };
                layout(set = 0, binding = 4, std430) buffer OutBuf { float o[]; };
                void main() {
                    uint idx = gl_GlobalInvocationID.x;
                    if (idx >= {{len}}) { o[idx] = 0.0; return; }
                    int x = int(idx % {{w}}), rs = int(idx) - x;
                    float sum = 0.0; int cnt = 0;
                    for (int k = x - {{radius}}; k <= x + {{radius}}; k++) {
                        int col = clamp(k, 0, {{w}}-1);
                        sum += i[rs + col]; cnt++;
                    }
                    o[idx] = sum / float(cnt);
                }
                """;

            string vShader = $$"""
                #version 450
                layout(local_size_x = 256) in;
                layout(set = 0, binding = 0, std430) buffer InBuf { float i[]; };
                layout(set = 0, binding = 4, std430) buffer OutBuf { float o[]; };
                void main() {
                    uint idx = gl_GlobalInvocationID.x;
                    if (idx >= {{len}}) { o[idx] = 0.0; return; }
                    int y = int(idx / {{w}});
                    float sum = 0.0; int cnt = 0;
                    for (int k = y - {{radius}}; k <= y + {{radius}}; k++) {
                        int row = clamp(k, 0, {{h}}-1);
                        sum += i[row * {{w}} + int(idx % {{w}})]; cnt++;
                    }
                    o[idx] = sum / float(cnt);
                }
                """;

            return VulkanComputerRunner.EnqueueCompute(async () =>
            {
                var (acc, handler, vkView) = await VulkanComputerRunner.CreateAcceleratorAsync(hShader, new float[][] { rIn }, OutputElementType.Float32);
                async Task<float[]> RunH(float[] ch)
                {
                    acc.Inputs = new float[][] { ch }; NativeVulkanSurfaceViewHandler.MapInputs(handler, acc);
                    return (float[])await vkView.RunComputeAsync(OutputElementType.Float32);
                }
                var rT = await RunH(rIn); var gT = await RunH(gIn); var bT = await RunH(bIn); var aT = await RunH(aIn);

                var (acc2, handler2, vkView2) = await VulkanComputerRunner.CreateAcceleratorAsync(vShader, new float[][] { rT }, OutputElementType.Float32);
                async Task<float[]> RunV(float[] ch)
                {
                    acc2.Inputs = new float[][] { ch }; NativeVulkanSurfaceViewHandler.MapInputs(handler2, acc2);
                    return (float[])await vkView2.RunComputeAsync(OutputElementType.Float32);
                }
                return new object[] { await RunV(rT), await RunV(gT), await RunV(bT), await RunV(aT) };
            }, "VulkanBlurComputer timed out.");
        }
    }

    public class VulkanColorAdjustmentComputer : IComputer, IColorAdjustmentComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "ColorAdjustment";
        public FourChannelResult ComputeColorAdjustment(float[] r, float[] g, float[] b, float[] a, int w, int h, float brightness, float contrast, float saturation, float hue, float gamma, float vibrance, float temperature, bool invert, float grayscale, float opacity, float maxVal)
        {
            var result = Compute([r, g, b, a, brightness, contrast, saturation, hue, gamma, vibrance, temperature, invert ? 1f : 0f, grayscale, opacity, maxVal]);
            return new FourChannelResult((float[])result[0], (float[])result[1], (float[])result[2], (float[])result[3]);
        }

        public object[] Compute(object[] args)
        {
            var rIn = args[0] as float[] ?? throw new ArgumentException(); var gIn = args[1] as float[] ?? throw new ArgumentException();
            var bIn = args[2] as float[] ?? throw new ArgumentException(); var aIn = args[3] as float[] ?? throw new ArgumentException();
            float brightness = Convert.ToSingle(args[4]), contrast = Convert.ToSingle(args[5]);
            float saturation = Convert.ToSingle(args[6]), hue = Convert.ToSingle(args[7]);
            float gamma = Convert.ToSingle(args[8]), vibrance = Convert.ToSingle(args[9]);
            float temperature = Convert.ToSingle(args[10]), invertF = Convert.ToSingle(args[11]);
            float grayscale = Convert.ToSingle(args[12]), opacity = Convert.ToSingle(args[13]);
            float maxV = Convert.ToSingle(args[14]);
            int srcLen = rIn.Length, packedLen = srcLen * 4;

            var packed = new float[packedLen];
            Array.Copy(rIn, 0, packed, 0, srcLen); Array.Copy(gIn, 0, packed, srcLen, srcLen);
            Array.Copy(bIn, 0, packed, srcLen * 2, srcLen); Array.Copy(aIn, 0, packed, srcLen * 3, srcLen);

            string shader = $$"""
                #version 450
                layout(local_size_x = 256) in;
                layout(set = 0, binding = 0, std430) buffer InBuf { float i[]; };
                layout(set = 0, binding = 4, std430) buffer OutBuf { float o[]; };
                void main() {
                    uint idx = gl_GlobalInvocationID.x;
                    if (idx >= {{srcLen}}) { o[idx]=0.0; o[idx+{{srcLen}}]=0.0; o[idx+{{srcLen * 2}}]=0.0; o[idx+{{srcLen * 3}}]=0.0; return; }
                    float r=i[idx], g=i[idx+{{srcLen}}], b=i[idx+{{srcLen * 2}}], a=i[idx+{{srcLen * 3}}];
                    float bf = {{brightness}} - 1.0;
                    r = bf>=0.0 ? r+({{maxV}}-r)*bf : r*(1.0+bf);
                    g = bf>=0.0 ? g+({{maxV}}-g)*bf : g*(1.0+bf);
                    b = bf>=0.0 ? b+({{maxV}}-b)*bf : b*(1.0+bf);
                    r = ((r/{{maxV}}-0.5)*{{contrast}}+0.5)*{{maxV}};
                    g = ((g/{{maxV}}-0.5)*{{contrast}}+0.5)*{{maxV}};
                    b = ((b/{{maxV}}-0.5)*{{contrast}}+0.5)*{{maxV}};
                    float gray = 0.2126*r + 0.7152*g + 0.0722*b;
                    r = gray + {{saturation}}*(r-gray);
                    g = gray + {{saturation}}*(g-gray);
                    b = gray + {{saturation}}*(b-gray);
                    // Hue
                    {   float nr=r/{{maxV}}, ng=g/{{maxV}}, nb=b/{{maxV}};
                        float cmx=max(max(nr,ng),nb), cmn=min(min(nr,ng),nb), delta=cmx-cmn;
                        float hh=0.0, ss, ll;
                        if (delta>0.0) { if(cmx==nr) hh=60.0*mod((ng-nb)/delta,6.0); else if(cmx==ng) hh=60.0*((nb-nr)/delta+2.0); else hh=60.0*((nr-ng)/delta+4.0); if(hh<0.0) hh+=360.0; }
                        ll=(cmx+cmn)*0.5; ss=(ll>0.0&&ll<1.0)?delta/(1.0-abs(2.0*ll-1.0)):0.0;
                        hh+= {{hue}}; if(hh<0.0) hh+=360.0; if(hh>=360.0) hh-=360.0;
                        if(ss<0.000001) { r=ll*{{maxV}}; g=ll*{{maxV}}; b=ll*{{maxV}}; }
                        else { float qq=ll<0.5?ll*(1.0+ss):ll+ss-ll*ss; float p=2.0*ll-qq; float hN=hh/360.0;
                            float Tr=hN+0.333333; if(Tr<0.0)Tr+=1.0; if(Tr>1.0)Tr-=1.0;
                            float Tg=hN; if(Tg<0.0)Tg+=1.0; if(Tg>1.0)Tg-=1.0;
                            float Tb=hN-0.333333; if(Tb<0.0)Tb+=1.0; if(Tb>1.0)Tb-=1.0;
                            float h2r(float t) { return t<0.166667?p+(qq-p)*6.0*t:t<0.5?qq:t<0.666667?p+(qq-p)*(0.666667-t)*6.0:p; }
                            r=h2r(Tr)*{{maxV}}; g=h2r(Tg)*{{maxV}}; b=h2r(Tb)*{{maxV}}; }
                    }
                    float invG=1.0/max({{gamma}},0.001);
                    r={{maxV}}*pow(r/{{maxV}},invG); g={{maxV}}*pow(g/{{maxV}},invG); b={{maxV}}*pow(b/{{maxV}},invG);
                    float vSat=1.0+{{vibrance}}*0.5, vGray=0.2126*r+0.7152*g+0.0722*b;
                    r=vGray+vSat*(r-vGray); g=vGray+vSat*(g-vGray); b=vGray+vSat*(b-vGray);
                    r*=1.0+{{temperature}}*0.01; b*=1.0-{{temperature}}*0.01;
                    if({{invertF}}>0.5) { r={{maxV}}-r; g={{maxV}}-g; b={{maxV}}-b; }
                    float lum=0.2126*r+0.7152*g+0.0722*b, gs={{grayscale}}>=1.0?1.0:1.0-{{grayscale}};
                    o[idx]=lum+gs*(r-lum); o[idx+{{srcLen}}]=lum+gs*(g-lum);
                    o[idx+{{srcLen * 2}}]=lum+gs*(b-lum); o[idx+{{srcLen * 3}}]=a*{{opacity}};
                }
                """;

            return VulkanComputerRunner.EnqueueCompute(async () =>
            {
                var (acc, handler, vkView) = await VulkanComputerRunner.CreateAcceleratorAsync(shader, new float[][] { packed }, OutputElementType.Float32);
                var raw = (float[])await vkView.RunComputeAsync(OutputElementType.Float32);
                if (raw.Length < packedLen) { var tp = new float[packedLen]; Array.Copy(raw, 0, tp, 0, raw.Length); raw = tp; }
                var rO = new float[srcLen]; var gO = new float[srcLen]; var bO = new float[srcLen]; var aO = new float[srcLen];
                Array.Copy(raw, 0, rO, 0, srcLen); Array.Copy(raw, srcLen, gO, 0, srcLen);
                Array.Copy(raw, srcLen * 2, bO, 0, srcLen); Array.Copy(raw, srcLen * 3, aO, 0, srcLen);
                return new object[] { rO, gO, bO, aO };
            }, "VulkanColorAdjustmentComputer timed out.");
        }
    }
}
