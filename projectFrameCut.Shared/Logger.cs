using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;


namespace projectFrameCut.Shared
{

    [DebuggerNonUserCode()]
    public static class Logger
    {

        [DebuggerNonUserCode()]
        public static void Log(string msg) => Log(msg, "info"); //fix the vs auto completion
        [DebuggerNonUserCode()]
        public static void Log(Exception ex, string? message = "", object? sender = null)
        {
            string senderStr = "Unknown sender";
            if (sender != null)
            {
                if(sender is string StringSender) senderStr = StringSender;
                else senderStr = sender.GetType().FullName ?? "Unknown sender";
            }
            if (string.IsNullOrWhiteSpace(message)) message = "do undefined action";
            Log($"A {ex.GetType().Name} exception happens when trying to {message} \r\nerror message: {ex.Message}\r\nFrom:{senderStr}", "error");

            MyLoggerExtensions.AnnounceException(ex);
            try
            {
                string info = "";
                if (ex is AggregateException agex)
                {
                    foreach (var item in agex.InnerExceptions)
                    {
                        try
                        {
                            info += $"AggregateException info:\r\n{GetExceptionMessages(item)}\r\n";
                        }
                        catch (Exception ex2)
                        {
                            info += $"AggregateException info:\r\n Unable to get info: {ex2.Message}\r\n";
                        }
                    }

                }

                if (ex.InnerException is AggregateException inneragex)
                {
                    foreach (var item in inneragex.InnerExceptions)
                    {
                        try
                        {
                            info += $"Inner AggregateException info:\r\n{GetExceptionMessages(item)}\r\n";
                        }
                        catch (Exception ex2)
                        {
                            info += $"Inner AggregateException info:\r\n Unable to get info: {ex2.Message}\r\n";
                        }
                    }
                }

                Log($"More exception info:\r\n{GetExceptionMessages(ex, false)}{info}", "error");


            }
            catch (Exception e)
            {
                var ex1 = new InvalidDataException($"An error occurred while trying to log the {ex.GetType().Name}'s detailed information.", new AggregateException(ex, e));
                Log(ex1, message, sender);
            }


        }

        private static string GetExceptionMessages(Exception ex, bool includeMessage = true)
        {
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
            if (!includeMessage)
            {
                return 
$"""
StackTrace:
{ex.StackTrace}

From:{(ex.TargetSite is not null ? ex.TargetSite.ToString() : "unknown")}
InnerException:
{innerExceptionInfo}

Exception data:
{string.Join("\r\n", ex.Data.Cast<System.Collections.DictionaryEntry>().Select(k => $"{k.Key} : {k.Value}"))}

""";
            }
            else
            {
                return
$"""
Message:
{ex.Message}

StackTrace:
{ex.StackTrace}

From:{(ex.TargetSite is not null ? ex.TargetSite.ToString() : "unknown")}
InnerException:
{innerExceptionInfo}

Exception data:
{string.Join("\r\n", ex.Data.Cast<System.Collections.DictionaryEntry>().Select(k => $"{k.Key} : {k.Value}"))}

""";
            }

        }

        [DebuggerNonUserCode()]
        public static void Log(string msg, string level = "info")
        {
#if DEBUG
            Debug.WriteLine($"[{level}] {msg}");
#endif

            MyLoggerExtensions.Announce(msg, level);

        }
        [DebuggerNonUserCode()]
        public static void LogDiagnostic(string msg)
        {
#if DEBUG
            Debug.WriteLine($"[Diag] {msg}");
#endif

            if (MyLoggerExtensions.LoggingDiagnosticInfo)
                MyLoggerExtensions.Announce(msg, "Diag");

        }


    }

    [DebuggerNonUserCode()]
    public static class MyLoggerExtensions
    {
        public static bool LoggingDiagnosticInfo = false;

        public static event Action<string, string>? OnLog;
        public static event Action<Exception>? OnExceptionLog;

        public static void Announce(string msg, string level = "info")
        {
            OnLog?.Invoke(msg, level);
        }
        public static void AnnounceException(Exception exc)
        {
            OnExceptionLog?.Invoke(exc);
        }
    }

    [DebuggerNonUserCode()]
    public class MyLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private readonly LogLevel _minLevel;
        private IExternalScopeProvider? _scopeProvider;

        public MyLoggerProvider(LogLevel minLevel = LogLevel.Trace)
        {
            _minLevel = minLevel;
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new MyLogger(categoryName, _minLevel, () => _scopeProvider);
        }

        public void Dispose()
        {
            // nothing to dispose
        }

        public void SetScopeProvider(IExternalScopeProvider scopeProvider)
        {
            _scopeProvider = scopeProvider;
        }

        [DebuggerNonUserCode()]
        private class MyLogger : ILogger
        {
            private readonly string _category;
            private readonly LogLevel _minLevel;
            private readonly Func<IExternalScopeProvider?> _scopeProviderFactory;

            public MyLogger(string category, LogLevel minLevel, Func<IExternalScopeProvider?> scopeProviderFactory)
            {
                _category = category;
                _minLevel = minLevel;
                _scopeProviderFactory = scopeProviderFactory;
            }

            public IDisposable BeginScope<TState>(TState state)
            {
                var provider = _scopeProviderFactory();
                return provider?.Push(state) ?? NullScope.Instance;
            }

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _minLevel;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                if (formatter == null) return;

                var message = formatter(state, exception);
                if (string.IsNullOrEmpty(message) && exception == null) return;

                var prefix = string.IsNullOrEmpty(_category) ? "" : $"[{_category}] ";
                var eventPart = eventId.Id != 0 ? $"(EventId:{eventId.Id}) " : "";

                if (exception != null)
                {
                    Logger.Log(exception, $"{prefix}{eventPart}{message}", "MAUI Logging");
                }
                else
                {
                    Logger.Log($"{prefix}{eventPart}{message}", MapLevel(logLevel));
                }
            }

            private static string MapLevel(LogLevel level) => "MAUI Logging/" + level switch
            {
                LogLevel.Trace => "info",
                LogLevel.Debug => "info",
                LogLevel.Information => "info",
                LogLevel.Warning => "warning",
                LogLevel.Error => "error",
                LogLevel.Critical => "critical",
                _ => "info",
            };
        }

        private class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new NullScope();
            private NullScope() { }
            public void Dispose() { }
        }
    }
}
