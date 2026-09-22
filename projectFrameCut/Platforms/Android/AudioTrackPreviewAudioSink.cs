#if ANDROID
using Android.Media;
using projectFrameCut.Render.PreviewAudio;
using System.Runtime.InteropServices;

namespace projectFrameCut.Platforms.Android;

internal sealed class AudioTrackPreviewAudioSink : IAudioPreviewSink
{
    private readonly ManualResetEventSlim _resume = new(true);
    private AudioTrack? _track;
    private PreviewAudioFormat _format;
    private long _writtenFrames;
    private long _headWrap;
    private uint _lastHead;
    private bool _paused;
    private bool _startRequested;

    public ValueTask InitializeAsync(PreviewAudioFormat format, CancellationToken cancellationToken)
    {
        _format = format;
        var channelMask = format.Channels == 1 ? ChannelOut.Mono : ChannelOut.Stereo;
        var minimum = AudioTrack.GetMinBufferSize(format.SampleRate, channelMask, Encoding.PcmFloat);
        _track = new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder().SetUsage(AudioUsageKind.Media).SetContentType(AudioContentType.Movie).Build())
            .SetAudioFormat(new AudioFormat.Builder().SetEncoding(Encoding.PcmFloat).SetSampleRate(format.SampleRate).SetChannelMask(channelMask).Build())
            .SetBufferSizeInBytes(Math.Max(minimum, format.SampleRate * format.Channels * sizeof(float) * 2))
            .SetTransferMode(AudioTrackMode.Stream)
            .Build();
        if (_track.State != State.Initialized) throw new InvalidOperationException("AudioTrack initialization failed.");
        return ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        _startRequested = true;
        if (_track is { } track && Interlocked.Read(ref _writtenFrames) > 0 && track.PlayState != PlayState.Playing) track.Play();
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<float> interleavedSamples, CancellationToken cancellationToken)
    {
        var track = _track ?? throw new InvalidOperationException("AudioTrack is not initialized.");
        _resume.Wait(cancellationToken);
        float[] data;
        int offset, end;
        if (MemoryMarshal.TryGetArray(interleavedSamples, out var segment) && segment.Array is not null)
        {
            data = segment.Array;
            offset = segment.Offset;
            end = offset + segment.Count;
        }
        else
        {
            data = interleavedSamples.ToArray();
            offset = 0;
            end = data.Length;
        }
        while (offset < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var written = track.Write(data, offset, end - offset, WriteMode.NonBlocking);
            if (written < 0) throw new IOException($"AudioTrack write failed: {written}.");
            if (written == 0)
            {
                if (track.PlayState != PlayState.Playing) track.Play();
                Thread.Sleep(2);
                continue;
            }
            offset += written;
        }
        Interlocked.Add(ref _writtenFrames, interleavedSamples.Length / _format.Channels);
        if (_startRequested && track.PlayState != PlayState.Playing) track.Play();
        return ValueTask.CompletedTask;
    }

    public ValueTask PauseAsync(CancellationToken cancellationToken) { _paused = true; _resume.Reset(); _track?.Pause(); return ValueTask.CompletedTask; }
    public ValueTask ResumeAsync(CancellationToken cancellationToken) { _paused = false; _track?.Play(); _resume.Set(); return ValueTask.CompletedTask; }
    public ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        _track?.Flush();
        Interlocked.Exchange(ref _writtenFrames, 0);
        _headWrap = 0;
        _lastHead = 0;
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _resume.Set();
        try { _track?.Pause(); _track?.Flush(); _track?.Stop(); } catch { }
        _paused = false;
        _startRequested = false;
        return ValueTask.CompletedTask;
    }

    public PreviewAudioSinkClock GetClock()
    {
        var head = unchecked((uint)(_track?.PlaybackHeadPosition ?? 0));
        if (head < _lastHead) _headWrap += 1L << 32;
        _lastHead = head;
        var played = _headWrap + head;
        return new(played, Math.Max(0, Interlocked.Read(ref _writtenFrames) - played), _track?.PlayState == PlayState.Playing, _paused);
    }

    public ValueTask DisposeAsync()
    {
        StopAsync(CancellationToken.None);
        _track?.Release();
        _track?.Dispose();
        _resume.Dispose();
        return ValueTask.CompletedTask;
    }
}
#endif
