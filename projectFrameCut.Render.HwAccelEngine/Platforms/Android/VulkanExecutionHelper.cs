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
using Silk.NET.Shaderc;

namespace projectFrameCut.Render.HwAccelEngine.Platforms.Android
{
    internal static class VulkanExecutionHelper
    {
        private static async Task<(NativeVulkanSurfaceView view, NativeVulkanSurfaceViewHandler handler, VulkanComputeView computeView)> CreateAndAttachAsync(string shaderSource, float[][] inputs, OutputElementType outputElementType)
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
            try
            {
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
                var addViewHandler = AndroidExecutionHelper.AddPlatformComputeViewHandler;
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
                if (await Task.WhenAny(readyTask, Task.Delay(TimeSpan.FromMilliseconds(AndroidExecutionHelper.Timeout))) != readyTask)
                {
                    throw new TimeoutException("VulkanComputeView.WaitUntilReadyAsync timed out after 30 seconds.");
                }

                await readyTask;
                return (accelerator, handler, vkView);
            }
            catch
            {
                AndroidExecutionHelper.UseView(accelerator).Dispose();
                throw;
            }
        }

        public static T EnqueueCompute<T>(Func<Task<T>> mainThreadWork, string timeoutMessage)
        {
            return AndroidExecutionHelper.EnqueueCompute(() =>
            {
                var mainThreadTask = MainThread.InvokeOnMainThreadAsync(mainThreadWork);
                if (!mainThreadTask.Wait(TimeSpan.FromSeconds(60)))
                {
                    throw new TimeoutException(timeoutMessage);
                }

                var result = TaskHelper.SyncWait(() => mainThreadTask, CancellationToken.None);
                if (result is null)
                {
                    throw new InvalidOperationException("Vulkan compute returned null result.");
                }

                return result;
            });
        }

        public static Task<(NativeVulkanSurfaceView view, NativeVulkanSurfaceViewHandler handler, VulkanComputeView computeView)> CreateAcceleratorAsync(string shaderSource, float[][] inputs, OutputElementType outputElementType) => CreateAndAttachAsync(shaderSource, inputs, outputElementType);
    }
}
#endif
