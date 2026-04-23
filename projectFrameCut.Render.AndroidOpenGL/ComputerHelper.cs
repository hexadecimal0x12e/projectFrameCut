using projectFrameCut.Render.AndroidOpenGL.Platforms.Android;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace projectFrameCut.Render.AndroidOpenGL
{
    public enum ComputeBackend
    {
        OpenGL,
        Vulkan
    }

    public static class ComputerHelper
    {
        private sealed class ComputeWorkItem
        {
            public required Func<object[]> Work { get; init; }
            public required TaskCompletionSource<object[]> Completion { get; init; }
        }

        private static readonly ConcurrentQueue<ComputeWorkItem> ComputeQueue = new();
        private static int WorkerRunning;

        public static int Timeout = 30000;

        public static Action<View>? AddPlatformComputeViewHandler;
        public static ComputeBackend PreferredBackend { get; private set; } = ComputeBackend.OpenGL;

        public static bool UseVulkanBackend => PreferredBackend == ComputeBackend.Vulkan;

        public static void SetPreferredBackend(string? backend)
        {
            PreferredBackend = ParseBackend(backend);
        }

        public static ComputeBackend ParseBackend(string? backend)
        {
            if (string.Equals(backend, "Vulkan", StringComparison.OrdinalIgnoreCase))
            {
                return ComputeBackend.Vulkan;
            }

            return ComputeBackend.OpenGL;
        }

        public static void Init()
        {
            EnsureWorker();
        }

        public static object[] EnqueueCompute(Func<object[]> work)
        {
            ArgumentNullException.ThrowIfNull(work);

            var completion = new TaskCompletionSource<object[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            ComputeQueue.Enqueue(new ComputeWorkItem
            {
                Work = work,
                Completion = completion
            });

            EnsureWorker();
            return TaskHelper.SyncWait(() => completion.Task, Timeout, new OperationCanceledException($"Compute operation timed out for {Timeout}ms."));
        }

        private static void EnsureWorker()
        {
            if (Interlocked.CompareExchange(ref WorkerRunning, 1, 0) != 0)
            {
                return;
            }

            Task.Run(ProcessQueueLoop);
        }

        private static void ProcessQueueLoop()
        {
            while (true)
            {
                if (!ComputeQueue.TryDequeue(out var item))
                {
                    Interlocked.Exchange(ref WorkerRunning, 0);

                    // Avoid race condition between queue empty check and worker shutdown.
                    if (ComputeQueue.IsEmpty || Interlocked.CompareExchange(ref WorkerRunning, 1, 0) != 0)
                    {
                        return;
                    }

                    continue;
                }

                try
                {
                    var result = item.Work();
                    item.Completion.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    item.Completion.TrySetException(ex);
                }
            }
        }
    }
}
