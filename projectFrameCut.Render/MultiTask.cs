using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using static projectFrameCut.Shared.Logger;


namespace projectFrameCut.Render
{
    public class MultiTask<T>
    {
        public bool ThrowOnAnyError { get; set; } = false;
        public bool ThrowOnErrorHappensImmediately { get; set; } = false;
        public bool VerboseLogging { get; set; } = true;
        public bool InternalLogging { get; set; } = false;

        /// <summary>
        /// 0: GC after each new batch start (default behavior);
        /// 
        /// 1: GC before a new task run;
        /// 
        /// other positive integer: don't invoke GC by <see cref="MultiTask{T}"/>;
        /// 
        /// any negative integer: GC after each (-GCOptions) task finished;
        /// </summary>
        public int GCOptions { get; set; } = 0;
        

        public int Finished { get => finished; }
        public int Failed { get => failed; }
        public AggregateException? Exceptions { get => exceptions; }
        public Action<T>? ActionAfterExectued { get; set; }

        Action<T> action;
        int finished = 0, failed = 0;
        AggregateException? exceptions = null;

        public MultiTask(Action<T> job)
        {
            action = job;
        }

        public async Task Start(int concurrentCount, T[] stuff)
        {
            int working = 0,  i = 0, batchesBeforeGC = 0;
            finished = 0;
            failed = 0;
            ArgumentNullException.ThrowIfNull(action, nameof(action));
            List<Thread> threads = new();
            bool fulled = false, running = true;
            List<Exception> exc = new();
            if (VerboseLogging)
            {
                new Thread(() =>
                {
                    while (running)
                    {
                        int f = Volatile.Read(ref finished);
                        int w = Volatile.Read(ref working);
                        Log($"[MultiTask] Jobs state: finished {f}/{stuff.Length}, working {w}/{stuff.Length - f}, captivity used {w}/{concurrentCount}, waiting for more free space:{fulled}, memory used:{Environment.WorkingSet / 1024f / 1024f}/{MemoryHelper.GetUsedRAM() /1024f/1024f} (render used/total used) MB");
                        Thread.Sleep(5000);
                    }
                }).Start();
            }

            if (InternalLogging)
            {
                new Thread(() =>
                {
                    while (running)
                    {
                        int f = Volatile.Read(ref finished);
                        int w = Volatile.Read(ref working);
                        Console.Error.WriteLine($"@@{f},{stuff.Length}");
                        Thread.Sleep(1000);
                    }
                }).Start();
            }

            do
            {
                if (fulled)
                {
                    


                    if (Volatile.Read(ref working) <= concurrentCount * 0.6)
                    {
                        Log("This batch is now free enough. Restart job...");
						if(GCOptions == 0) GC.Collect(2,GCCollectionMode.Forced,true);
						fulled = false;
                    }
                    else
                    {
                        await Task.Delay(500);
                        continue;
                    }
                }

                if (i >= stuff.Length)
                {
                    Log("All jobs started. Waiting for all finished...");
                    while (Volatile.Read(ref finished) < stuff.Length)
                    {
                        await Task.Delay(150);
                    }
                    goto done;
                }

                if (Volatile.Read(ref working) < concurrentCount)
                {
                    T arg = stuff[i];
                    int index = i;

                    Thread thread = new Thread(o =>
                    {
                        try
                        {
                            if (GCOptions == 1) GC.Collect(2, GCCollectionMode.Forced, true);
                            action((T)o!);
                        }
                        catch (Exception ex)
                        {
                            Log($"[MultiTask] Error in task #{index}: {ex.Message}");
                            ex.Data["@MultiTask.TaskIndex"] = index;
                            if(ThrowOnErrorHappensImmediately) 
                            {
                                throw;
                            }
                            exc.Add(ex);
                            Interlocked.Increment(ref failed);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref working);
                            Interlocked.Increment(ref finished);
                            ActionAfterExectued?.Invoke((T)o!);
                            if (GCOptions < 0)
                            {
                                if(batchesBeforeGC > -GCOptions)
                                {
                                    batchesBeforeGC = 0;
                                    GC.Collect(2, GCCollectionMode.Forced, true);
                                }
                                else
                                {
                                    batchesBeforeGC++;
                                }
                            }
                        }
                    });
                    thread.Name = $"MultiTask Worker #{index}";
                    thread.Priority = ThreadPriority.Highest;
                    Interlocked.Increment(ref working);
                    threads.Add(thread);
                    thread.Start(arg);

                    i++; 
                }
                else
                {
                    Log("This batch is full. Waiting for other finish and release resources...");
                    fulled = true;
                    await Task.Delay(5);
                }
            }
            while (Volatile.Read(ref finished) < stuff.Length);

        done:
            running = false;
            if (exc.Count > 0)
            {
                exceptions = new AggregateException($"{failed} of {stuff.Length} tasks failed during this job.",exc);
                if (ThrowOnAnyError)
                {
                    throw exceptions;
                }
            }
            Log("[MultiTask] All done. returning...");

        }




    }

    public static class MemoryHelper
    {

#if WINDOWS
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        private static ulong total = 0;

        public static ulong GetTotalRAM()
        {
            if(total != 0)
            {
                return total;
            }
            // 获取系统内存信息
            MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
            memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            GlobalMemoryStatusEx(ref memStatus);
            total = memStatus.ullTotalPhys;
            return memStatus.ullTotalPhys;



        }

        public static ulong GetUsedRAM()
        {
            
            // 获取系统内存信息
            MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
            memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            GlobalMemoryStatusEx(ref memStatus);

            return (memStatus.ullTotalPhys - memStatus.ullAvailPhys);



        }
#else

        public static (ulong, ulong) GetTotalMenInfo()
        {
            return (0, 0);
        }
#endif
    }


}
