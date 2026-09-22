using projectFrameCut.Render.Compose;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.PreviewAudio;

public sealed class PreviewAudioSession(IAudioPreviewSinkFactory sinkFactory, ISoundTrack[] soundTracks, uint duration) : IAsyncDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cancellation;
    private IAudioPreviewSink? _sink;
    private Task? _producer;
    private long _generation;
    private uint _startFrame;
    private int _frameRate = 30;
    private bool _paused;
    private volatile bool _hasAudio;

    public async ValueTask<PreviewAudioState> StartAsync(uint startFrame, int frameRate, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            _generation++;
            _startFrame = startFrame;
            _frameRate = Math.Max(1, frameRate);
            _paused = false;
            if (startFrame >= duration || !soundTracks.Any(track => SoundTrackMetadata.ReadBool(track.ExtraData, SoundTrackMetadata.EnabledKey, true)))
                return GetState();

            _cancellation = new CancellationTokenSource();
            _sink = sinkFactory.Create();
            await _sink.InitializeAsync(new PreviewAudioFormat(SampleRate, Channels), cancellationToken).ConfigureAwait(false);
            _hasAudio = true;
            var generation = _generation;
            var token = _cancellation.Token;
            _producer = Task.Factory.StartNew(
                () => Produce(generation, token),
                token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            return GetState();
        }
        catch
        {
            await StopCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<PreviewAudioState> PauseAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sink is not null) await _sink.PauseAsync(cancellationToken).ConfigureAwait(false);
            _paused = true;
            return GetState();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<PreviewAudioState> ResumeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sink is not null) await _sink.ResumeAsync(cancellationToken).ConfigureAwait(false);
            _paused = false;
            return GetState();
        }
        finally { _gate.Release(); }
    }

    public ValueTask<PreviewAudioState> SeekAsync(uint startFrame, int frameRate, CancellationToken cancellationToken)
        => StartAsync(startFrame, frameRate, cancellationToken);

    public async ValueTask<PreviewAudioState> StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _generation++;
            await StopCoreAsync().ConfigureAwait(false);
            return GetState();
        }
        finally { _gate.Release(); }
    }

    public PreviewAudioState GetState(long generation = 0)
    {
        if (generation != 0 && generation != _generation)
            return new(_generation, _startFrame, SampleRate, Channels, 0, 0, false, false, false);
        var sink = _sink;
        PreviewAudioSinkClock clock;
        try { clock = sink?.GetClock() ?? default; }
        catch (ObjectDisposedException) { clock = default; }
        return new(_generation, _startFrame, SampleRate, Channels, clock.PlayedSamples, clock.BufferedSamples,
            clock.IsRunning, _paused || clock.IsPaused, sink is not null && _hasAudio);
    }

    private void Produce(long generation, CancellationToken cancellationToken)
    {
        try
        {
            try { Thread.CurrentThread.Priority = ThreadPriority.AboveNormal; } catch { }
            var remaining = duration > _startFrame ? duration - _startFrame : 0;
            if (remaining == 0 || _sink is null) return;
            using var writer = new PreviewAudioWriter(_sink, cancellationToken);
            new AudioComposer<float>
            {
                Clips = [],
                SoundTracks = soundTracks,
                Writer = writer,
                StartFrame = _startFrame,
                Duration = remaining,
            }.Compose(_frameRate, SampleRate, Channels, SampleRate, cancellationToken);
            writer.Finish();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _hasAudio = false;
            Log(ex, $"Preview audio generation {generation}", this);
        }
    }

    private async ValueTask StopCoreAsync()
    {
        _cancellation?.Cancel();
        if (_sink is not null)
        {
            try { await _sink.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        }
        if (_producer is not null)
        {
            try { await _producer.ConfigureAwait(false); } catch { }
        }
        if (_sink is not null)
        {
            try { await _sink.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        _cancellation?.Dispose();
        _cancellation = null;
        _producer = null;
        _sink = null;
        _paused = false;
        _hasAudio = false;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); _gate.Dispose(); }
    }

    private sealed class PreviewAudioWriter(IAudioPreviewSink sink, CancellationToken cancellationToken) : AudioWriterBase<float>
    {
        private const int StartupBufferFrames = 48000;
        private readonly List<float[]> _startupBuffers = [];
        private int _startupFrames;
        private bool _started;

        public override void Initialize() { }
        public override bool SupportCodec(string codecName) => true;
        public override void Write(float data, int channel) => throw new NotSupportedException();
        public override void Append(IAudioSamples<float> samples)
        {
            var interleaved = new float[samples.SampleCount * samples.channelCount];
            for (var c = 0; c < samples.channelCount; c++)
            {
                var source = samples.GetSamples(c);
                for (var i = 0; i < samples.SampleCount; i++) interleaved[i * samples.channelCount + c] = source[i];
            }
            if (_started)
            {
                sink.WriteAsync(interleaved, cancellationToken).AsTask().GetAwaiter().GetResult();
                return;
            }

            _startupBuffers.Add(interleaved);
            _startupFrames += samples.SampleCount;
            if (_startupFrames >= StartupBufferFrames) Start();
        }

        public override void Finish() => Start();
        public override void Dispose() { }

        private void Start()
        {
            if (_started || _startupBuffers.Count == 0) return;
            foreach (var buffer in _startupBuffers)
                sink.WriteAsync(buffer, cancellationToken).AsTask().GetAwaiter().GetResult();
            sink.StartAsync(cancellationToken).AsTask().GetAwaiter().GetResult();
            _startupBuffers.Clear();
            _started = true;
        }
    }
}

public readonly record struct PreviewAudioState(
    long Generation, uint StartFrame, int SampleRate, int Channels, long PlayedSamples,
    long BufferedSamples, bool IsRunning, bool IsPaused, bool HasAudio);
