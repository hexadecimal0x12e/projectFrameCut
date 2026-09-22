using projectFrameCut.Render.PreviewAudio;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace projectFrameCut.Services;

internal sealed class PreviewAudioSinkFactory : IAudioPreviewSinkFactory
{
    public static PreviewAudioSinkFactory Instance { get; } = new();

    public IAudioPreviewSink Create()
    {
#if WINDOWS
        return new Platforms.Windows.WasapiPreviewAudioSink();
#elif ANDROID
        return new Platforms.Android.AudioTrackPreviewAudioSink();
#elif IOS || MACCATALYST
        return new Platforms.iDevices.CoreAudioPreviewAudioSink();
#else
        return new FfplayPreviewAudioSink();
#endif
    }
}

internal sealed class FfplayPreviewAudioSink : IAudioPreviewSink
{
    private readonly ManualResetEventSlim _resume = new(true);
    private Process? _process;
    private Stream? _input;
    private Task? _errorReader;
    private readonly List<byte[]> _startupBuffers = [];
    private Stopwatch _clock = new();
    private PreviewAudioFormat _format;
    private long _writtenFrames;
    private bool _paused;
    private bool _startRequested;

    public ValueTask InitializeAsync(PreviewAudioFormat format, CancellationToken cancellationToken)
    {
        _format = format;
        var info = new ProcessStartInfo("ffplay")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-nodisp", "-autoexit", "-loglevel", "error", "-f", "f32le", "-ar", format.SampleRate.ToString(), "-ac", format.Channels.ToString(), "-i", "pipe:0" })
            info.ArgumentList.Add(arg);
        _process = Process.Start(info) ?? throw new InvalidOperationException("Unable to start ffplay.");
        _input = _process.StandardInput.BaseStream;
        var process = _process;
        _errorReader = Task.Run(async () =>
        {
            try
            {
                var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(error)) Log(error, "ffplay preview audio");
            }
            catch (ObjectDisposedException) { }
        });
        return ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        _startRequested = true;
        if (_input is null) throw new InvalidOperationException("ffplay is not initialized.");
        foreach (var buffer in _startupBuffers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _input.Write(buffer);
        }
        _startupBuffers.Clear();
        if (Interlocked.Read(ref _writtenFrames) > 0 && !_clock.IsRunning) _clock.Start();
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<float> interleavedSamples, CancellationToken cancellationToken)
    {
        if (_input is null) throw new InvalidOperationException("ffplay is not initialized.");
        _resume.Wait(cancellationToken);
        var bytes = MemoryMarshal.AsBytes(interleavedSamples.Span).ToArray();
        Interlocked.Add(ref _writtenFrames, interleavedSamples.Length / _format.Channels);
        if (!_startRequested)
        {
            _startupBuffers.Add(bytes);
            return ValueTask.CompletedTask;
        }
        if (!_clock.IsRunning) _clock.Start();
        cancellationToken.ThrowIfCancellationRequested();
        _input.Write(bytes);
        return ValueTask.CompletedTask;
    }

    public ValueTask PauseAsync(CancellationToken cancellationToken)
    {
        _paused = true;
        _resume.Reset();
        if (_process is { HasExited: false }) kill(_process.Id, 19);
        _clock.Stop();
        return ValueTask.CompletedTask;
    }

    public ValueTask ResumeAsync(CancellationToken cancellationToken)
    {
        _paused = false;
        if (_process is { HasExited: false }) kill(_process.Id, 18);
        if (_startRequested && Interlocked.Read(ref _writtenFrames) > 0) _clock.Start();
        _resume.Set();
        return ValueTask.CompletedTask;
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _resume.Set();
        if (_input is not null) { try { await _input.FlushAsync(cancellationToken).ConfigureAwait(false); } catch { } try { _input.Dispose(); } catch { } }
        if (_process is { HasExited: false }) { try { _process.Kill(entireProcessTree: true); } catch { } }
        _clock.Stop();
        _paused = false;
        _startRequested = false;
        _startupBuffers.Clear();
    }

    public PreviewAudioSinkClock GetClock()
    {
        var played = Math.Min(Interlocked.Read(ref _writtenFrames), (long)(_clock.Elapsed.TotalSeconds * _format.SampleRate));
        return new(played, Math.Max(0, Interlocked.Read(ref _writtenFrames) - played), _process is { HasExited: false }, _paused);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        if (_errorReader is not null) { try { await _errorReader.ConfigureAwait(false); } catch { } }
        _process?.Dispose();
        _resume.Dispose();
    }

#if LINUX
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
#elif WINDOWS
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    public static void kill(int pid, int signal)
    {
        if (signal == 18) // SIGCONT
        {
            GenerateConsoleCtrlEvent(0, (uint)pid);
        }
        else if (signal == 19) // SIGSTOP
        {
            GenerateConsoleCtrlEvent(1, (uint)pid);
        }
        else if (signal == 15 || signal == 9) // SIGTERM or SIGKILL
        {
            var process = Process.GetProcessById(pid);
            if (process != null && !process.HasExited)
            {
                process.Kill();
            }
        }
        else
        {
            throw new InvalidOperationException("Signal handling is not fully implemented on Windows. Only SIGCONT, SIGSTOP, SIGTERM and SIGKILL are supported.");
        }
    }
#else
    private static void kill(int pid, int signal)
    {
        throw new PlatformNotSupportedException("Signal handling is not supported on this platform.");
    }
#endif
}
