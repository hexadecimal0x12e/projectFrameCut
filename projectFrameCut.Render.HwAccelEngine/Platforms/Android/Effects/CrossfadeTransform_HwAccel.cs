#if ANDROID
using Microsoft.Maui.ApplicationModel;
using projectFrameCut.Render.HwAccelEngine.Platforms.Android;
using projectFrameCut.Shared;
using System.Globalization;
using static projectFrameCut.Render.HwAccelEngine.Platforms.Android.GLComputeView;

namespace projectFrameCut.Render.HwAccelEngine.Effect;

public partial class CrossfadeTransform_HwAccel
{
    private readonly ComputeBackend backend = AndroidExecutionHelper.PreferredBackend;

    private float[] ComputeBlend(float[] left, float[] right, float[] leftAlpha, float[] rightAlpha, float progress)
    {
        int n = left.Length;
        var input = new float[n * 4];
        left.CopyTo(input, 0);
        right.CopyTo(input, n);
        leftAlpha.CopyTo(input, n * 2);
        rightAlpha.CopyTo(input, n * 3);
        bool vulkan = backend == ComputeBackend.Vulkan;
        string shader = $$"""
            #version {{(vulkan ? "450" : "310 es")}}
            layout(local_size_x = 256) in;
            layout({{(vulkan ? "set = 0, " : "")}}binding = 0, std430) buffer InBuffer { float inputData[]; };
            layout({{(vulkan ? "set = 0, " : "")}}binding = {{(vulkan ? 1 : 6)}}, std430) buffer OutBuffer { float outputData[]; };
            void main()
            {
                uint i = gl_GlobalInvocationID.x;
                if (i >= uint({{n}})) { return; }
                float p = float({{progress.ToString("R", CultureInfo.InvariantCulture)}});
                float wl = inputData[{{n * 2}} + i] * (1.0 - p);
                float wr = inputData[{{n * 3}} + i] * p;
                outputData[i] = wl + wr > 0.0 ? (inputData[i] * wl + inputData[{{n}} + i] * wr) / (wl + wr) : 0.0;
            }
            """;
        float[] raw;
        if (vulkan)
        {
            raw = VulkanExecutionHelper.EnqueueCompute(async () =>
            {
                var (view, handler, accelerator) = await VulkanExecutionHelper.CreateAcceleratorAsync(shader, [input], OutputElementType.Float32);
                using var scope = AndroidExecutionHelper.UseView(view);
                return (float[])await accelerator.RunComputeAsync(OutputElementType.Float32);
            }, "Crossfade Vulkan compute timed out.");
        }
        else
        {
            raw = AndroidExecutionHelper.EnqueueCompute(() => TaskHelper.SyncWait(() => MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var view = new NativeGLSurfaceView
                {
                    ShaderSource = shader, Inputs = [input], JobID = "Crossfade",
                    WidthRequest = 50, HeightRequest = 50, OutputElementType = OutputElementType.Float32
                };
                using var scope = AndroidExecutionHelper.UseView(view);
                var ready = new TaskCompletionSource<NativeGLSurfaceViewHandler>(TaskCreationOptions.RunContinuationsAsynchronously);
                void HandlerChanged(object? sender, EventArgs e)
                {
                    if (view.Handler is NativeGLSurfaceViewHandler h)
                        ready.TrySetResult(h);
                }
                view.HandlerChanged += HandlerChanged;
                try
                {
                    AndroidExecutionHelper.AddPlatformComputeViewHandler?.Invoke(view);
                    HandlerChanged(null, EventArgs.Empty);
                    var handler = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    if (handler.PlatformView is not GLComputeView accelerator)
                        throw new InvalidOperationException("Crossfade OpenGL accelerator is unavailable.");
                    await accelerator.WaitUntilReadyAsync().WaitAsync(TimeSpan.FromMilliseconds(AndroidExecutionHelper.Timeout));
                    return (float[])await accelerator.RunComputeAsync(OutputElementType.Float32);
                }
                finally
                {
                    view.HandlerChanged -= HandlerChanged;
                }
            }), CancellationToken.None));
        }
        return raw.AsSpan(0, n).ToArray();
    }
}
#endif
