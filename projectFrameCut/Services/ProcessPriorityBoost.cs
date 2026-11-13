using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static projectFrameCut.Shared.Logger;

namespace projectFrameCut.Services;

internal sealed class ProcessPriorityBoost : IDisposable
{
    private static readonly object Gate = new();
    private static readonly int[] WindowsPriorities =
    [
        (int)ProcessPriorityClass.RealTime,
        (int)ProcessPriorityClass.High,
        (int)ProcessPriorityClass.AboveNormal,
        (int)ProcessPriorityClass.Normal,
        (int)ProcessPriorityClass.BelowNormal,
        (int)ProcessPriorityClass.Idle,
    ];
    private static Process? _currentProcess;
    private static int _originalPriority;
    private static int _activeScopes;
    private bool _disposed;

    private ProcessPriorityBoost() { }

    public static ProcessPriorityBoost? Acquire()
    {
        lock (Gate)
        {
            if (_activeScopes == 0)
            {
                var process = Process.GetCurrentProcess();
                if (RaiseToHighest(process) is not int priority)
                {
                    process.Dispose();
                    return null;
                }
                _currentProcess = process;
                _originalPriority = priority;
            }
            _activeScopes++;
            return new ProcessPriorityBoost();
        }
    }

    public static int? RaiseToHighest(Process process)
    {
        try
        {
            int original = GetPriority(process);
            Exception? failure = null;
            IEnumerable<int> priorities = OperatingSystem.IsWindows() ? WindowsPriorities : Enumerable.Range(-20, 40);
            foreach (int priority in priorities)
            {
                if (priority == original) break;
                try
                {
                    SetPriority(process, priority);
                    return original;
                }
                catch (Win32Exception ex) { failure = ex; }
            }
            if (failure is not null)
                Log($"Unable to raise process {process.Id} priority: {failure.Message}", "warning");
        }
        catch (Exception ex) { Log(ex, "Raise process priority"); }
        return null;
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (--_activeScopes != 0) return;
            try { SetPriority(_currentProcess!, _originalPriority); }
            catch (Exception ex) { Log(ex, "Restore process priority"); }
            finally
            {
                _currentProcess!.Dispose();
                _currentProcess = null;
            }
        }
    }

    private static bool IsApple => OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()
        || OperatingSystem.IsIOS() || OperatingSystem.IsTvOS();

    private static int GetPriority(Process process)
    {
        if (OperatingSystem.IsWindows()) return (int)process.PriorityClass;
        int priority = IsApple ? GetApplePriority(0, process.Id) : GetUnixPriority(0, process.Id);
        int error = Marshal.GetLastPInvokeError();
        if (priority == -1 && error != 0) throw new Win32Exception(error);
        return priority;
    }

    private static void SetPriority(Process process, int priority)
    {
        if (OperatingSystem.IsWindows())
        {
            process.PriorityClass = (ProcessPriorityClass)priority;
            return;
        }
        if ((IsApple ? SetApplePriority(0, process.Id, priority) : SetUnixPriority(0, process.Id, priority)) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    [DllImport("libc", EntryPoint = "getpriority", SetLastError = true)]
    private static extern int GetUnixPriority(int which, int who);

    [DllImport("libc", EntryPoint = "setpriority", SetLastError = true)]
    private static extern int SetUnixPriority(int which, int who, int priority);

    [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "getpriority", SetLastError = true)]
    private static extern int GetApplePriority(int which, int who);

    [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "setpriority", SetLastError = true)]
    private static extern int SetApplePriority(int which, int who, int priority);
}
