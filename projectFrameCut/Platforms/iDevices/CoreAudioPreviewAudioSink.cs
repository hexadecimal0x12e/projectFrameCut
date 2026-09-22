using AVFoundation;
using Foundation;
using projectFrameCut.Render.PreviewAudio;
using System.Runtime.InteropServices;

namespace projectFrameCut.Platforms.iDevices;

internal sealed class CoreAudioPreviewAudioSink : IAudioPreviewSink
{
    private readonly ManualResetEventSlim _resume = new(true);
    private AVAudioEngine? _engine;
    private AVAudioPlayerNode? _player;
    private AVAudioFormat? _audioFormat;
    private PreviewAudioFormat _format;
    private long _scheduledFrames;
    private bool _paused;
    private bool _startRequested;

    public ValueTask InitializeAsync(PreviewAudioFormat format, CancellationToken cancellationToken)
    {
        _format = format;
        _engine = new AVAudioEngine();
        _player = new AVAudioPlayerNode();
        _audioFormat = new AVAudioFormat(AVAudioCommonFormat.PCMFloat32, format.SampleRate, (uint)format.Channels, false);
        _engine.AttachNode(_player);
        _engine.Connect(_player, _engine.MainMixerNode, _audioFormat);
        _engine.Prepare();
        if (!_engine.StartAndReturnError(out var error)) throw new InvalidOperationException(error?.LocalizedDescription ?? "AVAudioEngine failed to start.");
        return ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        _startRequested = true;
        if (_player is { } player && Interlocked.Read(ref _scheduledFrames) > 0 && !player.Playing) player.Play();
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<float> interleavedSamples, CancellationToken cancellationToken)
    {
        var player = _player ?? throw new InvalidOperationException("CoreAudio is not initialized.");
        _resume.Wait(cancellationToken);
        while (Interlocked.Read(ref _scheduledFrames) - GetPlayedFrames() > _format.SampleRate * 2)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(4);
        }

        var frameCount = interleavedSamples.Length / _format.Channels;
        var buffer = new AVAudioPcmBuffer(_audioFormat!, (uint)frameCount) { FrameLength = (uint)frameCount };
        var input = interleavedSamples.ToArray();
        try
        {
            for (var channel = 0; channel < _format.Channels; channel++)
            {
                var channelData = Marshal.ReadIntPtr(buffer.FloatChannelData, channel * IntPtr.Size);
                var planar = new float[frameCount];
                for (var i = 0; i < frameCount; i++) planar[i] = input[i * _format.Channels + channel];
                Marshal.Copy(planar, 0, channelData, frameCount);
            }
            player.ScheduleBuffer(buffer, () => buffer.Dispose());
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
        Interlocked.Add(ref _scheduledFrames, frameCount);
        if (_startRequested && !player.Playing) player.Play();
        return ValueTask.CompletedTask;
    }

    public ValueTask PauseAsync(CancellationToken cancellationToken) { _paused = true; _resume.Reset(); _player?.Pause(); return ValueTask.CompletedTask; }
    public ValueTask ResumeAsync(CancellationToken cancellationToken) { _paused = false; _player?.Play(); _resume.Set(); return ValueTask.CompletedTask; }
    public ValueTask FlushAsync(CancellationToken cancellationToken) { _player?.Stop(); Interlocked.Exchange(ref _scheduledFrames, 0); return ValueTask.CompletedTask; }
    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _resume.Set();
        _player?.Stop();
        _engine?.Stop();
        _paused = false;
        _startRequested = false;
        return ValueTask.CompletedTask;
    }

    public PreviewAudioSinkClock GetClock()
    {
        var played = GetPlayedFrames();
        return new(played, Math.Max(0, Interlocked.Read(ref _scheduledFrames) - played), _player?.Playing == true, _paused);
    }

    private long GetPlayedFrames()
    {
        var nodeTime = _player?.LastRenderTime;
        var playerTime = nodeTime is null ? null : _player?.GetPlayerTimeFromNodeTime(nodeTime);
        return Math.Max(0, playerTime?.SampleTime ?? 0);
    }

    public ValueTask DisposeAsync()
    {
        StopAsync(CancellationToken.None);
        _player?.Dispose();
        _engine?.Dispose();
        _audioFormat?.Dispose();
        _resume.Dispose();
        return ValueTask.CompletedTask;
    }
}
