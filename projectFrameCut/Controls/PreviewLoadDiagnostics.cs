using System.Diagnostics;
using System.Threading.Channels;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Shared;

namespace projectFrameCut.Controls;

internal sealed class PreviewLoadDiagnostics
{
    internal const string SettingKey = "diag_PreviewControlTiming";
    private static readonly Channel<LogEntry> _logs = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(8192)
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private static long _droppedLogs;
    private static long _nextId;
    private readonly long _id = Interlocked.Increment(ref _nextId);
    private readonly long _start = Stopwatch.GetTimestamp();
    private long _last;
    private readonly View _view;
    private readonly object _gate = new();
    private bool _finished;
    private bool _contentReady;

    static PreviewLoadDiagnostics()
    {
        _ = Task.Run(WriteLogsAsync);
    }

    private readonly record struct LogEntry(long Id, string Control, string Name, string Stage,
        double Step, double Total, int Thread, double Width, double Height);

    private static async Task WriteLogsAsync()
    {
        while (await _logs.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            try
            {
                var dropped = Interlocked.Exchange(ref _droppedLogs, 0);
                if (dropped > 0)
                    MyLoggerExtensions.Announce($"[PreviewControlTiming] Dropped {dropped} entries because the logging queue was full.", "warning");
            }
            catch (Exception ex) { Debug.WriteLine(ex); }

            for (var i = 0; i < 256 && _logs.Reader.TryRead(out var entry); i++)
            {
                try
                {
                    // Debugger output can suspend the process even when emitted from a worker.
                    MyLoggerExtensions.Announce($"[PreviewControlTiming] id={entry.Id}, control={entry.Control}, name={entry.Name}, stage={entry.Stage}, step={entry.Step:F2}ms, total={entry.Total:F2}ms, thread={entry.Thread}, size={entry.Width:F1}x{entry.Height:F1}", "Diag");
                }
                catch (Exception ex) { Debug.WriteLine(ex); }
            }
        }
    }

    private PreviewLoadDiagnostics(View view)
    {
        _view = view;
        _last = _start;
        view.Loaded += OnLoaded;
        view.SizeChanged += OnSizeChanged;
        Stage("content received");
    }

    internal static PreviewLoadDiagnostics? Begin(View view, PreviewLoadDiagnostics? previous)
    {
        previous?.Finish("superseded");
        return SettingsManager.IsBoolSettingTrue(SettingKey) ? new(view) : null;
    }

    internal void Stage(string stage)
    {
        lock (_gate)
        {
            if (_finished) return;
            var now = Stopwatch.GetTimestamp();
            if (!_logs.Writer.TryWrite(new(_id, _view.GetType().Name, _view.AutomationId, stage,
                Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds,
                Stopwatch.GetElapsedTime(_start, now).TotalMilliseconds,
                Environment.CurrentManagedThreadId, _view.Width, _view.Height)))
                Interlocked.Increment(ref _droppedLogs);
            _last = now;
        }
    }

    internal void ContentReady(string stage)
    {
        if (_finished || _contentReady) return;
        Stage(stage);
        _contentReady = true;
        CheckReady();
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        Stage("view loaded");
        CheckReady();
    }

    private void OnSizeChanged(object? sender, EventArgs e)
    {
        if (_view.Width <= 0 || _view.Height <= 0) return;
        Stage("layout available");
        CheckReady();
    }

    private void CheckReady()
    {
        // This is control readiness, not confirmation that the monitor scanned out the frame.
        if (_contentReady && _view.IsLoaded && _view.Width > 0 && _view.Height > 0)
            Finish("ready");
    }

    internal void Finish(string stage)
    {
        lock (_gate)
        {
            if (_finished) return;
            Stage(stage);
            _finished = true;
        }
        _view.Loaded -= OnLoaded;
        _view.SizeChanged -= OnSizeChanged;
    }
}
