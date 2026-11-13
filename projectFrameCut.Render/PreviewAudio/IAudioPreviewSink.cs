namespace projectFrameCut.Render.PreviewAudio;

public readonly record struct PreviewAudioFormat(int SampleRate, int Channels);

public readonly record struct PreviewAudioSinkClock(long PlayedSamples, long BufferedSamples, bool IsRunning, bool IsPaused);

public sealed class PreviewAudioDeviceUnavailableException : Exception
{
    public PreviewAudioDeviceUnavailableException(int errorCode) : base($"Preview audio device is unavailable (0x{errorCode:X8}).")
    {
        HResult = errorCode;
    }
}

public interface IAudioPreviewSink : IAsyncDisposable
{
    ValueTask InitializeAsync(PreviewAudioFormat format, CancellationToken cancellationToken);
    ValueTask StartAsync(CancellationToken cancellationToken);
    ValueTask WriteAsync(ReadOnlyMemory<float> interleavedSamples, CancellationToken cancellationToken);
    ValueTask PauseAsync(CancellationToken cancellationToken);
    ValueTask ResumeAsync(CancellationToken cancellationToken);
    ValueTask FlushAsync(CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
    PreviewAudioSinkClock GetClock();
}

public interface IAudioPreviewSinkFactory
{
    IAudioPreviewSink Create();
}
