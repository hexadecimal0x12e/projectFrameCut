using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace projectFrameCut.Shared
{
    public static class TaskHelper
    {
        /// <summary>
        /// Wait for a <see cref="Task{T}"/> to complete with a timeout. If the task does not complete within the timeout, return the default value.
        /// </summary>
        public static T SyncWait<T>(Func<Task<T>> task, double timeoutMs = 10000, T? DefaultValue = default)
        {
            Stopwatch sw = Stopwatch.StartNew();
            T result = DefaultValue!;
            bool done = false;
            Exception exc;
            Thread t = new(async () =>
            {
                try
                {
                    result = await task();
                }
                catch (Exception ex)
                {
                    exc = ex;
                    exc.Data["OriginalStacktrace"] = ex.StackTrace;
                }
                finally
                {
                    done = true;
                }
            });
            t.Start();
            while (!done)
            {
                if (sw.Elapsed.TotalMilliseconds > timeoutMs)
                {
                    return DefaultValue!;
                }
                Thread.Sleep(10);
            }
            return result;

        }
        /// <summary>
        /// Wait for a <see cref="Task{T}"/> to complete with a timeout. If the task does not complete within the timeout, return the default value.
        /// </summary>
        public static T SyncWait<T>(Func<Task<T>> task, CancellationToken cancellationToken)
        {
            Stopwatch sw = Stopwatch.StartNew();
            T result = default!;
            bool done = false;
            Exception exc;
            Thread t = new(async () =>
            {
                try
                {
                    result = await task();
                }
                catch (Exception ex)
                {
                    exc = ex;
                    exc.Data["OriginalStacktrace"] = ex.StackTrace;
                }
                finally
                {
                    done = true;
                }
            });
            t.Start();
            while (!cancellationToken.IsCancellationRequested && !done)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.Sleep(10);
            }
            return result;

        }
    }
}
