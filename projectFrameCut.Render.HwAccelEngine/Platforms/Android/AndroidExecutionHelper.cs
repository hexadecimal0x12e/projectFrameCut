#if ANDROID
using Microsoft.Maui.ApplicationModel;
using projectFrameCut.Shared;
using System.Collections.Concurrent;

namespace projectFrameCut.Render.HwAccelEngine.Platforms.Android;

public enum ComputeBackend
{
    OpenGL,
    Vulkan
}

public static class AndroidExecutionHelper
{
    private static readonly ConcurrentQueue<Action> queue = new();
    private static int workerRunning;
    public static int Timeout = 30000;
    public static Action<View>? AddPlatformComputeViewHandler;
    public static ComputeBackend PreferredBackend { get; private set; } = ComputeBackend.OpenGL;
    public static bool UseVulkanBackend => PreferredBackend == ComputeBackend.Vulkan;

    public static ComputeBackend ParseBackend(string? backend) => string.Equals(backend, "Vulkan", StringComparison.OrdinalIgnoreCase) ? ComputeBackend.Vulkan : ComputeBackend.OpenGL;
    public static void SetPreferredBackend(string? backend)
    {
        PreferredBackend = ParseBackend(backend);
        projectFrameCut.Render.Effect.EffectHelper.InvalidateImplementations();
    }

    public static void Init() => EnsureWorker();
    public static T EnqueueCompute<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue(() =>
        {
            try
            {
                completion.TrySetResult(work());
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "Execute Android effect", nameof(AndroidExecutionHelper));
                completion.TrySetException(ex);
            }
        });
        EnsureWorker();
        // The submitted operation owns its views until native work completes.
        return TaskHelper.SyncWait(() => completion.Task, CancellationToken.None);
    }

    private static void EnsureWorker()
    {
        if (Interlocked.CompareExchange(ref workerRunning, 1, 0) == 0)
            Task.Run(ProcessQueue);
    }

    private static void ProcessQueue()
    {
        while (true)
        {
            if (queue.TryDequeue(out var work))
                work();
            else
            {
                Interlocked.Exchange(ref workerRunning, 0);
                if (queue.IsEmpty || Interlocked.CompareExchange(ref workerRunning, 1, 0) != 0)
                    return;
            }
        }
    }

    public static IDisposable UseView(View view) => new ViewScope(view);
    private sealed class ViewScope(View view) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            void Release()
            {
                var handler = view.Handler;
                var platform = handler?.PlatformView as IDisposable;
                handler?.DisconnectHandler();
                if (view.Parent is Layout parent)
                    parent.Children.Remove(view);
                platform?.Dispose();
            }

            if (MainThread.IsMainThread)
                Release();
            else
                _ = TaskHelper.SyncWait(task: async () => { await MainThread.InvokeOnMainThreadAsync(Release); return true; }, cancellationToken: CancellationToken.None);
        }
    }
}
#endif
