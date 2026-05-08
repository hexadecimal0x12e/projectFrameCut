using Microsoft.Maui.ApplicationModel;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using Silk.NET.Shaderc;
using System.Globalization;
using static projectFrameCut.Render.AndroidOpenGL.Platforms.Android.GLComputeView;

namespace projectFrameCut.Render.AndroidOpenGL.Platforms.Android
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

    public class VulkanOverlayComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "Overlay";

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

    public class VulkanResizeComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
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

    public class VulkanCropComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
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

    public class VulkanPlaceComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
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

    public class VulkanRemoveColorComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "RemoveColor";

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

    public class VulkanBlendAddComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "AddComputer";

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

    public class VulkanBlendSubtractComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "SubtractComputer";

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

    public class VulkanBlendMultiplyComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "MultiplyComputer";

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

    public class VulkanBlendScreenComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "ScreenComputer";

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

    public class VulkanBlendOverlayBlendComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "OverlayBlendComputer";

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

    public class VulkanBlendDarkenComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "DarkenComputer";

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

    public class VulkanBlendLightenComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "LightenComputer";

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

    public class VulkanBlendDifferenceComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.AndroidOpenGL.Platforms.Android.VulkanComputers";
        public string SupportedEffectOrMixture => "DifferenceComputer";

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
}
