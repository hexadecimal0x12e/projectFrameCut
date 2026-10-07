#if WINDOWS && DEBUG
using System.Diagnostics;
using System.Runtime.InteropServices;
using projectFrameCut.Shared;

namespace projectFrameCut.Services
{
    public partial class UIThreadWatchdogService
    {
        private volatile uint _uiThreadId;
        private int _stackCaptureInProgress;

        private void CaptureThreadStacks()
        {
            if (Interlocked.CompareExchange(ref _stackCaptureInProgress, 1, 0) != 0)
            {
                Logger.Log("UI freeze thread stack capture is already in progress.", "warning");
                return;
            }

            try
            {
                new Thread(LogThreadStacks)
                {
                    IsBackground = true,
                    Name = "UI watchdog stack capture"
                }.Start();
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _stackCaptureInProgress, 0);
                Logger.Log(ex, "start UI freeze thread stack capture", this);
            }
        }

        private void LogThreadStacks()
        {
            try
            {
                uint uiThreadId = _uiThreadId;
                Logger.Log($"Capturing UI freeze thread stacks. PID={Environment.ProcessId}, UI thread={uiThreadId}.", "warning");
                using var process = Process.GetCurrentProcess();
                var stacks = ProcessThreadStackCapture.Capture(process, "UI freeze", uiThreadId);

                Logger.Log(stacks.MainStack ?? $"UI thread {uiThreadId} stack was not captured.", "warning");
                try
                {
                    string path = ProcessThreadStackCapture.SaveToLogDirectory(process.Id, "ui-freeze", stacks.AllStacks);
                    Logger.Log($"All thread stacks for this UI freeze saved to {path}", "warning");
                }
                catch (Exception ex)
                {
                    Logger.Log(ex, "save UI freeze thread stacks", this);
                }
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "capture UI freeze thread stacks", this);
            }
            finally
            {
                Volatile.Write(ref _stackCaptureInProgress, 0);
            }
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}
#endif
