using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.Shared
{

    [DebuggerNonUserCode()]
    public static class Logger
    {

        [DebuggerNonUserCode()]
        public static void Log(string msg) => Log(msg, "info"); //fix the vs auto completion
        //[DebuggerNonUserCode()]
        //public static void Log(Exception e) => Log(e, false);
        //[DebuggerNonUserCode()]
        //public static void Log(Exception e, bool isCritical) => Log($"{(isCritical ? "A critical " : "")}{e.GetType().Name} error: {e.Message} {e.StackTrace}", isCritical ? "Critical" : "error");
        [DebuggerNonUserCode()]
        public static void Log(Exception ex, string message = "", object? sender = null)
        {
            Log($"{sender?.GetType().Name} happends a {ex.GetType().Name} exception when trying to {message} \r\n error message: {ex.Message}", "error");
            string innerExceptionInfo = "";
            if (ex.InnerException != null)
            {
                Exception? inner = ex.InnerException;
                do
                {
                    if (inner is not null)
                    {
                        innerExceptionInfo +=
$"""
Type: {ex.InnerException.GetType().Name}                        
Message: {ex.InnerException.Message}
StackTrace:
{ex.InnerException.StackTrace}

""";
                        inner = inner.InnerException;
                    }
                } while (inner is not null);
            }
            if (string.IsNullOrWhiteSpace(innerExceptionInfo)) innerExceptionInfo = "None";
            Log(
$"""

StackTrace:
{ex.StackTrace}

From:{(ex.TargetSite is not null ? ex.TargetSite.ToString() : "unknown")}
InnerException:
{innerExceptionInfo}

Exception data:
{string.Join("\r\n", ex.Data.Cast<System.Collections.DictionaryEntry>().Select(k => $"{k.Key} : {k.Value}"))}

"""
);

        }

        [DebuggerNonUserCode()]
        public static void Log(string msg, string level = "info")
        {
#if DEBUG
            Debug.WriteLine($"log:[{level}] {msg}");
#endif
            Console.WriteLine($"[{level}] {msg}");

            MyLoggerExtensions.Announce(msg, level);

        }


    }

    public static class MyLoggerExtensions
    {
        public static event Action<string, string>? OnLog;

        public static void Announce(string msg, string level = "info")
        {
            Task.Run(() => OnLog?.Invoke(msg, level));
        }
    }
}
