using projectFrameCut.Shared;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.PreviewAudio;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Rendering;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace projectFrameCut.Render.RPCProtocol;

public sealed partial class RenderBackendService
{
    private readonly ConcurrentDictionary<Guid, DynamicPreviewProducer> _dynamicPreviews = new();
    private readonly Dictionary<(Guid Session, string Configuration), DynamicPreviewState> _dynamicPreviewStates = new();
    private readonly SemaphoreSlim _dynamicPreviewGate = new(1, 1);

    private async ValueTask<DynamicPreviewSession> StartDynamicPreviewAsync(StartDynamicPreviewRequest request, CancellationToken token)
    {
        await _projectLifecycleGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfClosing();
            return await StartDynamicPreviewCoreAsync(request, token).ConfigureAwait(false);
        }
        finally { _projectLifecycleGate.Release(); }
    }

    private async ValueTask<DynamicPreviewSession> StartDynamicPreviewCoreAsync(StartDynamicPreviewRequest request, CancellationToken token)
    {
        var session = GetSession(request.SessionId);
        using var operation = session.BeginOperation(token);
        await _dynamicPreviewGate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            ThrowIfClosing();
            if (request.PreferredPixelFormat is not (PreviewPixelFormat.EncodedImage or PreviewPixelFormat.Rgba16FloatScRgb))
                throw new ArgumentException("Unsupported dynamic preview pixel format.", nameof(request));
            if (_dynamicPreviews.TryRemove(session.Id, out var previous)) await previous.DisposeAsync().ConfigureAwait(false);
            request.CanvasWidth = Math.Max(1, request.CanvasWidth);
            request.CanvasHeight = Math.Max(1, request.CanvasHeight);
            request.ProjectWidth = request.ProjectWidth > 0 ? request.ProjectWidth : session.Width;
            request.ProjectHeight = request.ProjectHeight > 0 ? request.ProjectHeight : session.Height;
            request.FrameRate = double.IsFinite(request.FrameRate) && request.FrameRate > 0 ? request.FrameRate : session.FrameRate;
            using var sourceScope = ProjectExternalSourceRuntime.Use(session.ExternalSources);
            var configuration = FrameHashHelper.HashValue(new
            {
                session.SnapshotHash, session.SourceConfiguration,
                Guard = GetPreviewHashGuard(session, GetVisualClips(session.Clips)),
                Timeline.ProcessEffectFromCanvas,
                request.CanvasWidth, request.CanvasHeight, request.ProjectWidth, request.ProjectHeight, request.FrameRate,
                request.PreviewPipeName.Length, request.PreviewClipPoolMaxBytes, request.PreferredPixelFormat, request.UsePreviewMetadata, request.DisableSourceFrameCache, request.MaxParallelWorkers,
                request.AllowWorkerOversubscription, request.RenderAheadFrames, request.EnableThreadAffinity,
                request.WorkerCPUCoreIndexs, request.EnableMemoryBudget, request.MaxBufferedBytes,
            });
            var key = (session.Id, configuration);
            if (!_dynamicPreviewStates.TryGetValue(key, out var state))
            {
                state = new(this, session, $"thumbs/dynamicPlayback/{session.Id:N}/{configuration}", request.PreviewPipeName.Length == 0);
                _dynamicPreviewStates.Add(key, state);
            }
            var playbackId = request.PlaybackId == Guid.Empty ? Guid.NewGuid() : request.PlaybackId;
            var result = new DynamicPreviewSession
            {
                SessionId = session.Id, PlaybackId = playbackId, StartFrame = request.StartFrame, FrameRate = request.FrameRate,
                CacheDirectory = state.CacheDirectory, PixelFormat = request.PreferredPixelFormat,
            };
            DynamicPreviewProducer? producer = null;
            try
            {
                producer = new(this, session, request, result, state);
                await producer.StartAsync(operation.Token).ConfigureAwait(false);
                operation.Token.ThrowIfCancellationRequested();
                _dynamicPreviews[session.Id] = producer;
                LogDiagnostic($"[DynamicPreview] Started cache producer {playbackId} at frame {request.StartFrame}, {request.CanvasWidth}x{request.CanvasHeight}.");
                return result;
            }
            catch (Exception ex)
            {
                if (producer is not null) await producer.DisposeAsync().ConfigureAwait(false);
                if (ex is not OperationCanceledException || state.PackagingWorkers.Length == 0)
                {
                    _dynamicPreviewStates.Remove(key);
                    await state.DisposeAsync().ConfigureAwait(false);
                }
                throw;
            }
        }
        finally { _dynamicPreviewGate.Release(); }
    }

    private async ValueTask<EmptyResponse> StopDynamicPreviewAsync(StopDynamicPreviewRequest request, CancellationToken token)
    {
        await _dynamicPreviewGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_dynamicPreviews.TryGetValue(request.SessionId, out var producer)
                && (request.ClearCache || request.PlaybackId == Guid.Empty || request.PlaybackId == producer.PlaybackId)
                && _dynamicPreviews.TryRemove(request.SessionId, out _))
                await producer.DisposeAsync().ConfigureAwait(false);
            if (request.ClearCache)
            {
                foreach (var key in _dynamicPreviewStates.Keys.Where(k => k.Session == request.SessionId).ToArray())
                {
                    var state = _dynamicPreviewStates[key];
                    _dynamicPreviewStates.Remove(key);
                    await state.DisposeAsync().ConfigureAwait(false);
                }
                if (_sessions.TryGetValue(request.SessionId, out var session))
                {
                    session.NodeCache.Clear();
                    lock (session.PreviewHashes)
                    {
                        foreach (var hashes in session.PreviewHashes.Values) hashes.Cancel();
                        session.PreviewHashes.Clear();
                    }
                }
            }
            return new();
        }
        finally { _dynamicPreviewGate.Release(); }
    }

    private async Task StopAllDynamicPreviewsAsync()
    {
        await _dynamicPreviewGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var producer in _dynamicPreviews.Values) producer.Cancel();
            foreach (var producer in _dynamicPreviews.Values) await producer.DisposeAsync().ConfigureAwait(false);
            _dynamicPreviews.Clear();
            foreach (var state in _dynamicPreviewStates.Values) await state.DisposeAsync().ConfigureAwait(false);
            _dynamicPreviewStates.Clear();
        }
        finally { _dynamicPreviewGate.Release(); }
    }

    private sealed class DynamicPreviewState : IAsyncDisposable
    {
        private readonly string root;
        public string CacheDirectory { get; }
        public DynamicPreviewIndex? Index { get; }
        public ConcurrentDictionary<(uint Frame, Guid Clip), DynamicPreviewCachedClip> Frames { get; } = new();
        public ConcurrentBag<string> TemporaryFrames { get; } = new();
        public IClip[] Clips { get; set; } = [];
        public Dictionary<Guid, DynamicPreviewClipWorker> Workers { get; } = new();
        public Task[] PackagingWorkers { get; set; } = [];
        public BlockingCollection<DynamicPreviewPackagingJob> PackagingJobs { get; } = new();
        public HwAccelPictureSession GpuSession { get; } = new();
        public EffectRuntimeContext Effects { get; } = new();
        public object WorkerGate { get; } = new();
        public int[]? WorkerAffinity;
        public int ActiveWorkers;
        public int WorkerLimit, MaximumWorkers, RenderAheadFrames, PendingFrameLimit;

        public DynamicPreviewState(RenderBackendService owner, BackendSession session, string directory, bool disk)
        {
            CacheDirectory = directory;
            root = owner._artifacts.ResolveProjectPath(session.ProjectRoot, directory);
            if (disk)
            {
                Directory.CreateDirectory(root);
                Index = new(root);
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var worker in Workers.Values) worker.Jobs.CompleteAdding();
            await Task.WhenAll(Workers.Values.Select(w => w.Task).OfType<Task>()).ConfigureAwait(false);
            PackagingJobs.CompleteAdding();
            await Task.WhenAll(PackagingWorkers).ConfigureAwait(false);
            foreach (var worker in Workers.Values) worker.Jobs.Dispose();
            PackagingJobs.Dispose();
            DisposeResources(Clips, [], null);
            Effects.Dispose();
            GpuSession.Dispose();
            Index?.Dispose();
            Frames.Clear();
            try
            {
                foreach (var path in TemporaryFrames) File.Delete(path);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch (Exception ex) { Log(ex, $"Delete dynamic preview state {CacheDirectory}"); }
        }
    }

    private sealed class DynamicPreviewClipWorker(IClip clip)
    {
        public IClip Clip { get; } = clip;
        public BlockingCollection<DynamicPreviewRenderJob> Jobs { get; } = new();
        public Task? Task;
    }

    private sealed record DynamicPreviewRenderJob(DynamicPreviewProducer Producer, uint Frame,
        FrameHashContext Context, CancellationToken Token)
    {
        public TaskCompletionSource Rendered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public double RenderMilliseconds;
        public long PackagingQueuedAt;
        public DynamicPreviewCachedClip? CachedClip;
        public PreviewFrameData? MemoryData;
        public bool Reusable;
    }

    private sealed record DynamicPreviewPackagingJob(DynamicPreviewRenderJob Render, Guid ClipId,
        PreviewFrameCache.PendingFrame? Preview, bool Positioned, bool Reusable,
        IPicture? LegacyPicture = null, FrameHashResult LegacyHash = default) : IDisposable
    {
        public void Dispose()
        {
            try { Preview?.Dispose(); }
            finally { LegacyPicture?.Dispose(); }
        }
    }

    private sealed class DynamicPreviewFrame(uint frame, FrameHashContext context, DynamicPreviewRenderJob[] jobs)
    {
        public uint Frame { get; } = frame;
        public FrameHashContext Context { get; } = context;
        public DynamicPreviewRenderJob[] Jobs { get; } = jobs;
        public Task Rendered { get; } = Task.WhenAll(jobs.Select(j => j.Rendered.Task));
        public Task Completion { get; } = Task.WhenAll(jobs.Select(j => j.Completion.Task));
    }

    private sealed class DynamicPreviewProducer : IAsyncDisposable
    {
        private readonly RenderBackendService _owner;
        private readonly BackendSession _session;
        private readonly StartDynamicPreviewRequest _request;
        private readonly DynamicPreviewSession _playback;
        private readonly DynamicPreviewState _state;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly BackendSession.SessionOperation _operation;
        private readonly Func<DynamicPreviewClock, CancellationToken, ValueTask> _sendClock;
        private IClip[] _renderClips = [];
        private int _workerLimit;
        private int _renderAheadFrames;
        private System.IO.Pipes.NamedPipeClientStream? _previewPipe;
        private bool MemoryOnly => _request.PreviewPipeName.Length > 0;
        private readonly long _bufferFrames;
        private readonly long _minimumBufferFrames;
        private long _nextFrame;
        private readonly PreviewBufferWindow _bufferWindow;
        private bool _hasPreviewFrame;
        private long _reusedFrames;
        private long _lastSlowPackagingLog;
        private long _startedAt;
        private PreviewAudioSession? _audio;
        private long _audioGeneration;
        private Task? _run;
        private DynamicPreviewClock _clock = new();
        private string? _error;
        private int _disposed;

        public Guid PlaybackId => _playback.PlaybackId;

        public DynamicPreviewProducer(RenderBackendService owner, BackendSession session, StartDynamicPreviewRequest request, DynamicPreviewSession playback, DynamicPreviewState state)
        {
            _owner = owner;
            _session = session;
            _request = request;
            _playback = playback;
            _state = state;
            _sendClock = RenderRpcNotifications.DynamicPreviewClockSender
                ?? throw new NotSupportedException("Dynamic preview requires RPC clock notifications.");
            _operation = session.BeginOperation(_cancellation.Token);
            _bufferWindow = new(request.PreviewClipPoolMaxBytes, request.FrameRate);
            _bufferFrames = _bufferWindow.MaximumFrames;
            _minimumBufferFrames = Math.Max(1L, (long)Math.Ceiling(request.FrameRate * 0.5));
            _nextFrame = request.StartFrame;
            _clock.Frame = request.StartFrame;
        }

        public async Task StartAsync(CancellationToken token)
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token, _operation.Token);
            token = startup.Token;
            using var sourceScope = ProjectExternalSourceRuntime.Use(_session.ExternalSources);
            if (MemoryOnly) _previewPipe = await MemoryPreviewChannel.ConnectAsync(_request.PreviewPipeName, _request.PreviewPipeToken, token).ConfigureAwait(false);
            var bufferingStartedAt = Stopwatch.GetTimestamp();
            await ReportBufferingProgressAsync(0, token).ConfigureAwait(false);
            await CreateRenderWorkersAsync(token).ConfigureAwait(false);
            await RenderFramesAsync(token, bufferStart: _request.StartFrame, bufferFrames: _bufferFrames).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await ReportBufferingProgressAsync(1, token).ConfigureAwait(false);
            LogDiagnostic($"[DynamicPreview] Buffered {_nextFrame - _request.StartFrame} frames from {_request.StartFrame} with {_state.Workers.Count} clip workers in {Stopwatch.GetElapsedTime(bufferingStartedAt).TotalSeconds:F1}s for {PlaybackId}; reused {_reusedFrames} clip frames.");
            if (_request.StartAudio && _owner._previewAudioSinkFactory is not null && HasAudio(_session))
            {
                try
                {
                    _audio = _session.GetPreviewAudio(_owner._previewAudioSinkFactory);
                    var state = await _audio.StartAsync(_request.StartFrame, (int)Math.Round(_request.FrameRate), token).ConfigureAwait(false);
                    _audioGeneration = state.Generation;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _audio = null;
                    Log(ex, "Start cached dynamic preview audio; using the playback clock", this);
                }
            }
            _startedAt = Stopwatch.GetTimestamp();
            _clock = await GetClockAsync(token).ConfigureAwait(false);
            _playback.Clock = _clock;
            _run = Task.Run(RunAsync);
        }

        private async ValueTask<DynamicPreviewClock> GetClockAsync(CancellationToken token)
        {
            var clock = GetClock();
            clock.BufferFrames = _bufferWindow.FrameCount;
            if (!clock.IsCompleted && _audio is not null && _audio.Generation == _audioGeneration && _audio.DeviceError != 0)
            {
                _clock = clock;
                await _sendClock(clock, token).ConfigureAwait(false);
                if (!await RecoverAudioAsync(token).ConfigureAwait(false))
                {
                    clock.IsCompleted = true;
                    return clock;
                }
                clock = GetClock();
                clock.BufferFrames = _bufferWindow.FrameCount;
                return clock;
            }
            return clock;
        }

        private DynamicPreviewClock GetClock()
        {
            var now = Stopwatch.GetTimestamp();
            if (_audio is not null)
            {
                var audio = _audio.GetState(_audioGeneration);
                now = Stopwatch.GetTimestamp();
                if (audio.Generation != _audioGeneration)
                    return new()
                    {
                        SessionId = _session.Id, PlaybackId = PlaybackId,
                        Timestamp = now, Frequency = Stopwatch.Frequency, Frame = _clock.Frame, IsCompleted = true,
                    };
                var deviceLost = _audio.DeviceError != 0;
                if (audio.HasAudio || deviceLost)
                    return new()
                    {
                        SessionId = _session.Id, PlaybackId = PlaybackId,
                        Timestamp = now, Frequency = Stopwatch.Frequency,
                        Frame = audio.StartFrame + audio.PlayedSamples * _request.FrameRate / Math.Max(1, audio.SampleRate),
                        IsRunning = audio.IsRunning && !audio.IsPaused && !deviceLost,
                        IsCompleted = !deviceLost && audio.BufferedSamples == 0 && _audio.ProducerCompleted,
                    };
            }
            return new()
            {
                SessionId = _session.Id, PlaybackId = PlaybackId,
                Timestamp = now, Frequency = Stopwatch.Frequency,
                Frame = _request.StartFrame + Stopwatch.GetElapsedTime(_startedAt, now).TotalSeconds * _request.FrameRate,
                IsRunning = true,
            };
        }

        private async ValueTask<bool> RecoverAudioAsync(CancellationToken token)
        {
            var audio = _audio!;
            var frame = (uint)Math.Clamp(Math.Floor(_clock.Frame), 0, _session.Duration);
            Log($"[DynamicPreview] Audio device lost (0x{audio.DeviceError:X8}) for {PlaybackId}; reopening the default output at frame {frame}.", "warning");
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var state = await audio.RecoverAsync(frame, (int)Math.Round(_request.FrameRate), token).ConfigureAwait(false);
                    _audioGeneration = state.Generation;
                    while (!state.IsPaused && !state.IsRunning && state.HasAudio && audio.DeviceError == 0 && !audio.ProducerCompleted)
                    {
                        await Task.Delay(25, token).ConfigureAwait(false);
                        state = audio.GetState(_audioGeneration);
                    }
                    if (audio.DeviceError == 0 && state.HasAudio)
                    {
                        Log($"[DynamicPreview] Reopened the default audio output for {PlaybackId} at frame {frame}.");
                        return true;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log($"[DynamicPreview] Could not reopen the default audio output for {PlaybackId}: 0x{ex.HResult:X8}, {ex.Message}", "warning");
                }
                finally { _audioGeneration = audio.Generation; }
                if (attempt == 0) await Task.Delay(200, token).ConfigureAwait(false);
            }
            Log($"[DynamicPreview] Paused {PlaybackId} at frame {frame}; no usable default audio output is available.", "warning");
            return false;
        }

        private async Task RunAsync()
        {
            using var sourceScope = ProjectExternalSourceRuntime.Use(_session.ExternalSources);
            var token = _operation.Token;
            try { await RunBufferedPlaybackAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _error = ex.Message;
                Log(ex, $"Produce dynamic preview cache {PlaybackId}", this);
            }
            finally { await FinishAsync().ConfigureAwait(false); }
        }

        private async Task RunBufferedPlaybackAsync(CancellationToken token)
        {
            using var rendering = CancellationTokenSource.CreateLinkedTokenSource(token);
            // Keep one producer for the lifetime of the pipe; rebuffering must not consume and lose a send credit.
            var render = Task.Run(async () =>
            {
                try { await RenderFramesAsync(rendering.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (rendering.IsCancellationRequested) { }
                catch { Cancel(); throw; }
            });
            try
            {
                while (!token.IsCancellationRequested && !_clock.IsCompleted)
                {
                    await PublishClockAsync(token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (_clock.IsCompleted) break;

                    long first = (long)Math.Floor(_clock.Frame);
                    long buffered;
                    LogDiagnostic($"[DynamicPreview] Rebuffering {PlaybackId} at frame {_clock.Frame:F1}; producer retained; buffer frames={_bufferWindow.FrameCount}.");
                    while (true)
                    {
                        long target = Math.Min(_session.Duration, first + Math.Max(2, _bufferWindow.FrameCount));
                        buffered = Interlocked.Read(ref _nextFrame);
                        if (buffered >= target) break;
                        if (render.IsCompleted)
                        {
                            await render.ConfigureAwait(false);
                            if (Interlocked.Read(ref _nextFrame) < target)
                                throw new InvalidDataException("The preview producer stopped before the requested frame was ready.");
                            continue;
                        }
                        await ReportBufferingProgressAsync(Math.Clamp((buffered - first) / (double)Math.Max(1, target - first), 0, 1),
                            token, _clock.Frame).ConfigureAwait(false);
                        await Task.Delay(50, token).ConfigureAwait(false);
                    }
                    if (_audio is not null && _audio.Generation == _audioGeneration)
                        await _audio.ResumeAsync(token).ConfigureAwait(false);
                    _startedAt = Stopwatch.GetTimestamp() - (long)((_clock.Frame - _request.StartFrame) / _request.FrameRate * Stopwatch.Frequency);
                    LogDiagnostic($"[DynamicPreview] Resumed {PlaybackId} at frame {_clock.Frame:F1}, buffered through frame {buffered}.");
                }
            }
            finally
            {
                rendering.Cancel();
                await render.ConfigureAwait(false);
            }
        }

        private async Task PublishClockAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                _clock = await GetClockAsync(token).ConfigureAwait(false);
                if (_clock.Frame >= _session.Duration) _clock.IsCompleted = true;
                var nextFrame = Interlocked.Read(ref _nextFrame);
                long minimum = Math.Max(1, Math.Min(_minimumBufferFrames, _bufferWindow.FrameCount / 2));
                if (!_clock.IsCompleted && nextFrame < _session.Duration && nextFrame - _clock.Frame < minimum)
                {
                    if (_audio is not null && _audio.Generation == _audioGeneration)
                        await _audio.PauseAsync(token).ConfigureAwait(false);
                    _clock = await GetClockAsync(token).ConfigureAwait(false);
                    if (!_clock.IsCompleted)
                    {
                        double ready = Math.Max(_request.StartFrame, Interlocked.Read(ref _nextFrame) - 1);
                        if (_clock.Frame > ready)
                        {
                            _clock.Frame = ready;
                            if (_audio is not null && _audio.Generation == _audioGeneration)
                            {
                                var state = await _audio.RecoverAsync((uint)ready, (int)Math.Round(_request.FrameRate), token).ConfigureAwait(false);
                                _audioGeneration = state.Generation;
                            }
                        }
                    }
                    _clock.IsRunning = false;
                    _clock.Timestamp = Stopwatch.GetTimestamp();
                    if (!_clock.IsCompleted) _clock.BufferingProgress = 0;
                    await _sendClock(_clock, token).ConfigureAwait(false);
                    if (_clock.IsCompleted) Cancel();
                    return;
                }
                await _sendClock(_clock, token).ConfigureAwait(false);
                if (_clock.IsCompleted) { Cancel(); break; }
                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }

        private ValueTask ReportBufferingProgressAsync(double progress, CancellationToken token, double? frame = null, double? frameRate = null)
            => _sendClock(new()
            {
                SessionId = _session.Id, PlaybackId = PlaybackId,
                Timestamp = Stopwatch.GetTimestamp(), Frequency = Stopwatch.Frequency,
                Frame = frame ?? _request.StartFrame, BufferingProgress = progress, BufferingFrameRate = frameRate,
                BufferFrames = _bufferWindow.FrameCount,
            }, token);

        private async Task CreateRenderWorkersAsync(CancellationToken token)
        {
            if (_state.PackagingWorkers.Length > 0)
            {
                _renderClips = GetVisualClips(_state.Clips);
                _workerLimit = _state.WorkerLimit;
                _renderAheadFrames = _state.RenderAheadFrames;
                LogDiagnostic($"[DynamicPreview] Reusing {_state.Workers.Count} clip workers and {_state.Frames.Count} clip frames for {PlaybackId}.");
                return;
            }
            var draft = JsonSerializer.Deserialize<DraftStructureJSON>(_session.TimelineJson, _owner._jsonOptions)
                ?? throw new InvalidDataException("Dynamic preview timeline is invalid.");
            var clips = _session.Clips.ToDictionary(c => c.Id);
            foreach (var dto in draft.Clips)
            {
                VideoClipRotation.Migrate(dto);
                if (clips.TryGetValue(dto.Id, out var clip)) dto.FilePath = clip.FilePath;
            }

            _workerLimit = Math.Max(1, _request.MaxParallelWorkers);
            var visualClips = _session.Clips.Where(c => c.ClipType != ClipMode.AudioClip).ToArray();
            int concurrentClips = 0, maxConcurrentClips = 0;
            foreach (var change in visualClips.SelectMany(c => new[]
            {
                (Frame: c.ExtendToWholeDraft ? 0UL : c.StartFrame, Delta: 1),
                (Frame: c.ExtendToWholeDraft ? (ulong)_session.Duration : (ulong)c.StartFrame + c.GetEffectiveDuration(), Delta: -1),
            }).GroupBy(c => c.Frame).OrderBy(c => c.Key))
            {
                concurrentClips += change.Sum(c => c.Delta);
                maxConcurrentClips = Math.Max(maxConcurrentClips, concurrentClips);
            }
            int workers = _request.AllowWorkerOversubscription && _workerLimit > 1
                ? Math.Max(_workerLimit, maxConcurrentClips) : _workerLimit;
            _renderAheadFrames = _workerLimit == 1 ? 1 : _request.RenderAheadFrames > 0
                ? _request.RenderAheadFrames : Math.Clamp(_workerLimit, 2, 8);
            _state.PendingFrameLimit = _renderAheadFrames + 1;
            if (_request.EnableMemoryBudget)
            {
                var budget = _request.MaxBufferedBytes > 0 ? _request.MaxBufferedBytes : RenderMemoryBudget.DefaultBytes;
                var clipBytes = RenderMemoryBudget.EstimateClipBytes(_request.CanvasWidth, _request.CanvasHeight);
                workers = RenderMemoryBudget.LimitWorkers(workers, clipBytes, budget);
                _renderAheadFrames = RenderMemoryBudget.LimitWorkers(_renderAheadFrames,
                    clipBytes * Math.Max(1, maxConcurrentClips), budget);
                _state.PendingFrameLimit = RenderMemoryBudget.LimitWorkers(_state.PendingFrameLimit,
                    clipBytes * Math.Max(1, maxConcurrentClips), budget);
            }
            _state.WorkerLimit = _workerLimit;
            _state.MaximumWorkers = workers;
            _state.RenderAheadFrames = _renderAheadFrames;
            _state.Clips = await Task.Run(() => _owner.CreateClips(draft, _session.Assets, string.Empty,
                _session.ProjectRoot, token, null, _session.ClipSignatures), token).ConfigureAwait(false);
            _renderClips = GetVisualClips(_state.Clips);
            await Task.Run(() => _owner.InitializePreviewClips(_renderClips, PixelMode, _request.StartFrame, token), token).ConfigureAwait(false);
            foreach (var clip in _renderClips) _state.Workers.Add(clip.Id, new(clip));
            int[]? affinity = _request.WorkerCPUCoreIndexs;
            if (affinity is not { Length: > 0 } && _request.EnableThreadAffinity)
            {
                try
                {
                    affinity = ThreadAffinityHelper.GetCpuCoreGroups()
                        .OrderBy(c => (c.Capacity ?? 0) + (c.EfficiencyClass ?? 0))
                        .LastOrDefault()?.CpuIndexes.ToArray();
                }
                catch (Exception ex) { Log(ex, "Resolve dynamic preview worker affinity", this); }
            }
            var state = _state;
            state.WorkerAffinity = affinity;
            state.PackagingWorkers = Enumerable.Range(0, Math.Clamp(workers, 1, 4))
                .Select(_ => Task.Factory.StartNew(() => RunPackagingJobs(state), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            Log($"[DynamicPreview] Clip-owned workers, limit={_workerLimit}, maximum={workers}, oversubscription={_request.AllowWorkerOversubscription}, packaging workers={state.PackagingWorkers.Length}, lookahead={_renderAheadFrames}, pending frames<={state.PendingFrameLimit}, memory budget={_request.EnableMemoryBudget}. Effect implementations: {EffectHelper.GetImplementationSummary(_renderClips)}.");
        }

        private IPicture.PicturePixelMode PixelMode => IPicture.PicturePixelMode.Float16Picture;

        private static void RunClipJobs(DynamicPreviewState state, DynamicPreviewClipWorker worker)
        {
            if (state.WorkerAffinity is { Length: > 0 })
            {
                try { ThreadAffinityHelper.SetCurrentThreadAffinity(state.WorkerAffinity); }
                catch (Exception ex) { Log(ex, "Set dynamic preview worker affinity"); }
            }
            foreach (var job in worker.Jobs.GetConsumingEnumerable())
            {
                bool entered = false;
                DynamicPreviewPackagingJob? packaging = null;
                try
                {
                    lock (state.WorkerGate)
                    {
                        while (state.ActiveWorkers >= state.MaximumWorkers
                            || state.ActiveWorkers >= job.Producer._workerLimit && (!job.Producer._request.AllowWorkerOversubscription
                                || job.Producer._workerLimit == 1 || job.Frame > Interlocked.Read(ref job.Producer._nextFrame)))
                        {
                            job.Token.ThrowIfCancellationRequested();
                            Monitor.Wait(state.WorkerGate, 100);
                        }
                        job.Token.ThrowIfCancellationRequested();
                        state.ActiveWorkers++;
                        entered = true;
                    }
                    var startedAt = Stopwatch.GetTimestamp();
                    packaging = job.Producer.PrepareClipFrame(worker.Clip, job);
                    job.RenderMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
                    job.PackagingQueuedAt = Stopwatch.GetTimestamp();
                    state.PackagingJobs.Add(packaging);
                    packaging = null;
                    job.Rendered.TrySetResult();
                }
                catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
                {
                    job.Rendered.TrySetCanceled(job.Token);
                    job.Completion.TrySetCanceled(job.Token);
                }
                catch (Exception ex)
                {
                    job.Rendered.TrySetException(ex);
                    job.Producer.FailJob(job, worker.Clip.Id, ex, "Render");
                }
                finally
                {
                    if (entered)
                        lock (state.WorkerGate)
                        {
                            state.ActiveWorkers--;
                            Monitor.PulseAll(state.WorkerGate);
                        }
                    try { packaging?.Dispose(); }
                    catch (Exception ex) { Log(ex, "Release unqueued dynamic preview frame"); }
                }
            }
        }

        private static void RunPackagingJobs(DynamicPreviewState state)
        {
            foreach (var job in state.PackagingJobs.GetConsumingEnumerable())
            {
                try
                {
                    using (job) job.Render.Producer.PackageClipFrame(job);
                    job.Render.Completion.TrySetResult();
                }
                catch (OperationCanceledException) when (job.Render.Token.IsCancellationRequested)
                {
                    job.Render.Completion.TrySetCanceled(job.Render.Token);
                }
                catch (Exception ex) { job.Render.Producer.FailJob(job.Render, job.ClipId, ex, "Package"); }
            }
        }

        private void FailJob(DynamicPreviewRenderJob job, Guid clipId, Exception error, string stage)
        {
            Interlocked.CompareExchange(ref _error, error.Message, null);
            Log(error, $"{stage} dynamic preview clip {clipId} at frame {job.Frame}", this);
            Cancel();
            job.Completion.TrySetException(error);
        }

        private async Task RenderFramesAsync(CancellationToken token, long? bufferStart = null, long? bufferFrames = null)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            token = cancellation.Token;
            var bufferLength = bufferFrames ?? _bufferFrames;
            var bufferEnd = Math.Min(_session.Duration, (bufferStart ?? _request.StartFrame) + bufferLength);
            var lastProgressAt = Stopwatch.GetTimestamp();
            var lastProgressFrame = _nextFrame;
            long scheduled = _nextFrame;
            // Rendering can advance while the packaging workers finish earlier frames.
            var rendering = new Queue<DynamicPreviewFrame>();
            var pending = new Queue<DynamicPreviewFrame>();
            try
            {
                while (_nextFrame < _session.Duration || rendering.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    bool progressed = false;
                    while (rendering.TryPeek(out var rendered) && rendered.Rendered.IsCompleted)
                    {
                        await rendered.Rendered.ConfigureAwait(false);
                        rendered.Context.Dispose();
                        rendering.Dequeue();
                        progressed = true;
                    }
                    if (pending.TryPeek(out var completed) && completed.Completion.IsCompleted)
                    {
                        var jobs = new List<DynamicPreviewRenderJob>();
                        long nextFrame;
                        do
                        {
                            await completed.Completion.ConfigureAwait(false);
                            jobs.AddRange(completed.Jobs);
                            nextFrame = (long)completed.Frame + 1;
                            pending.Dequeue();
                        }
                        while (!MemoryOnly && pending.TryPeek(out completed) && completed.Completion.IsCompleted);
                        token.ThrowIfCancellationRequested();
                        foreach (var job in jobs)
                        {
                            var entry = job.CachedClip!;
                            _bufferWindow.Observe(entry.Width, entry.Height, job.MemoryData is { } data
                                ? (long)data.Pixels.Length + data.AlphaMask.Length : 0);
                        }
                        _hasPreviewFrame = true;
                        if (MemoryOnly)
                        {
                            foreach (var job in jobs)
                            {
                                var entry = job.CachedClip!;
                                await MemoryPreviewChannel.SendAsync(_previewPipe!, new(entry.ClipId, entry.FrameIndex,
                                    entry.IsPositionedClipPreview, job.MemoryData!.Info), job.MemoryData, token).ConfigureAwait(false);
                                job.MemoryData = null;
                            }
                            await projectFrameCut.Render.RPCProtocol.RenderPipeFrame.WriteAsync(_previewPipe!,
                                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(nextFrame), token).ConfigureAwait(false);
                            var ack = new byte[1];
                            await _previewPipe!.ReadExactlyAsync(ack, token).ConfigureAwait(false);
                        }
                        else _state.Index!.Write(jobs.Select(j => j.CachedClip!).ToArray());
                        foreach (var job in jobs)
                            if (job.Reusable && job.CachedClip is { } entry)
                                _state.Frames[(entry.FrameIndex, entry.ClipId)] = entry;
                        Interlocked.Exchange(ref _nextFrame, nextFrame);
                        bufferLength = Math.Min(bufferLength, _bufferWindow.FrameCount);
                        bufferEnd = Math.Min(_session.Duration, (bufferStart ?? _request.StartFrame) + bufferLength);
                        lock (_state.WorkerGate) Monitor.PulseAll(_state.WorkerGate);
                        progressed = true;
                    }
                    if (bufferStart.HasValue && _nextFrame > lastProgressFrame
                        && Stopwatch.GetElapsedTime(lastProgressAt).TotalMilliseconds >= 200)
                    {
                        var now = Stopwatch.GetTimestamp();
                        await ReportBufferingProgressAsync(Math.Clamp((_nextFrame - bufferStart.Value)
                            / (double)Math.Max(1L, bufferEnd - bufferStart.Value), 0, 1), token, _clock.Frame,
                            (_nextFrame - lastProgressFrame) / Stopwatch.GetElapsedTime(lastProgressAt, now).TotalSeconds).ConfigureAwait(false);
                        lastProgressAt = now;
                        lastProgressFrame = _nextFrame;
                    }
                    var current = bufferStart ?? (long)Math.Clamp(Math.Floor(_clock.Frame), _request.StartFrame, uint.MaxValue);
                    long count = _hasPreviewFrame ? Math.Min(bufferLength, _bufferWindow.FrameCount) : 1;
                    var end = Math.Min(_session.Duration, current + (bufferStart.HasValue ? count : Math.Max(2, count)));
                    int ahead = Math.Min(_renderAheadFrames, (int)count);
                    int pendingLimit = MemoryOnly ? 1 : Math.Min(_state.PendingFrameLimit, (int)count);
                    if (MemoryOnly && pending.Count == 0 && scheduled < end && rendering.Count < ahead)
                    {
                        var credit = new byte[1];
                        await _previewPipe!.ReadExactlyAsync(credit, token).ConfigureAwait(false);
                    }
                    while (scheduled < end && rendering.Count < Math.Min(ahead, _bufferWindow.FrameCount)
                        && pending.Count < Math.Min(pendingLimit, _bufferWindow.FrameCount))
                    {
                        var frame = QueueFrame((uint)scheduled++, token);
                        rendering.Enqueue(frame);
                        pending.Enqueue(frame);
                        progressed = true;
                    }
                    if (progressed) continue;
                    if (pending.Count == 0)
                    {
                        if (bufferStart.HasValue) return;
                        await Task.Delay(10, token).ConfigureAwait(false);
                    }
                    else if (rendering.TryPeek(out var next))
                        await Task.WhenAny(next.Rendered, pending.Peek().Completion).WaitAsync(token).ConfigureAwait(false);
                    else await pending.Peek().Completion.WaitAsync(token).ConfigureAwait(false);
                }
            }
            finally
            {
                cancellation.Cancel();
                var remaining = pending.Concat(rendering).Distinct().ToArray();
                try { await Task.WhenAll(remaining.SelectMany(f => new[] { f.Rendered, f.Completion })).ConfigureAwait(false); }
                catch { }
                foreach (var frame in remaining) frame.Context.Dispose();
            }
        }

        private DynamicPreviewFrame QueueFrame(uint frame, CancellationToken token)
        {
            var context = new FrameHashContext(new()
            {
                Width = _request.CanvasWidth, Height = _request.CanvasHeight,
                ProjectWidth = _request.ProjectWidth, ProjectHeight = _request.ProjectHeight,
                FrameRate = _session.FrameRate, PixelMode = PixelMode.Value,
                ProcessFromCanvas = Timeline.ProcessEffectFromCanvas,
            }, token, _session.NodeCache, _state.GpuSession) { Clips = _renderClips };
            var jobs = new List<(DynamicPreviewClipWorker Worker, DynamicPreviewRenderJob Job)>();
            try
            {
                foreach (var clip in _renderClips.Where(c => c.ContainsFrame(frame)))
                {
                    if (_state.Frames.TryGetValue((frame, clip.Id), out var cached)
                        && File.Exists(_owner._artifacts.ResolveProjectPath(_session.ProjectRoot, cached.ProjectRelativePath)))
                    {
                        _bufferWindow.Observe(cached.Width, cached.Height);
                        _reusedFrames++;
                        continue;
                    }
                    jobs.Add((_state.Workers[clip.Id], new(this, frame, context, token)));
                }
            }
            catch { context.Dispose(); throw; }
            var pending = new DynamicPreviewFrame(frame, context, jobs.Select(j => j.Job).ToArray());
            foreach (var (worker, job) in jobs)
            {
                try
                {
                    lock (_state.WorkerGate)
                    {
                        var state = _state;
                        worker.Task ??= Task.Factory.StartNew(() => RunClipJobs(state, worker), CancellationToken.None,
                            TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    }
                    worker.Jobs.Add(job);
                }
                catch (Exception ex)
                {
                    job.Rendered.TrySetException(ex);
                    FailJob(job, worker.Clip.Id, ex, "Queue");
                }
            }
            return pending;
        }

        private DynamicPreviewPackagingJob PrepareClipFrame(IClip clip, DynamicPreviewRenderJob job)
        {
            var token = job.Token;
            token.ThrowIfCancellationRequested();
            PreviewFrameCache.PendingFrame? pending = null;
            IPicture? legacyPicture = null;
            try
            {
                using var sourceScope = ProjectExternalSourceRuntime.Use(_session.ExternalSources);
                using var audio = EffectProcessing.OpenAudioFrame(_session.AudioSamples);
                using var effects = _state.Effects.Enter();
                using var hashScope = job.Context.Enter();
                _owner.InitializePreviewClips([clip], PixelMode, job.Frame, token);
                IClip[] dependencies = [clip, .. Timeline.CollectHashDependencies(_renderClips, clip, [clip.Id])];
                bool CanPersist() => dependencies.All(c => !ClipInitializationFailure.IsMarked(c) && Timeline.CanCacheClipSource(c));
                IPicture Render() => ClipPreviewRenderer.Render(clip, _renderClips, _request.CanvasWidth, _request.CanvasHeight,
                    _request.ProjectWidth, _request.ProjectHeight, job.Frame, token, PixelMode,
                    initializeClips: false, keepHwAccelPicture: true)
                    ?? throw new InvalidOperationException($"Clip '{clip.Id}' did not produce a preview frame.");
                bool positioned = TransformProcessing.HasActiveTransform(clip, _renderClips, job.Frame);
                if (MemoryOnly) return new(job, clip.Id, null, positioned, false, Render());
                if (!_request.UsePreviewMetadata)
                {
                    var outputHash = FrameHashHelper.GetClipOutputHash(_renderClips, clip, job.Frame, job.Context.Options, token);
                    if (!outputHash.CanReuse || !File.Exists(_owner._artifacts.ResolveProjectPath(_session.ProjectRoot,
                        GetLegacyClipPreviewPath(_session, clip.Id, outputHash, _request.ProjectWidth, _request.ProjectHeight,
                            _request.CanvasWidth, _request.CanvasHeight, _request.PreferredPixelFormat))))
                        legacyPicture = Render();
                    return new(job, clip.Id, null, positioned, CanPersist(), legacyPicture, outputHash);
                }
                var identity = FrameHashHelper.CanPersistClipHash(_renderClips, clip, job.Context.Options, token)
                    && _owner.TryGetPreparedPreviewHash(_session, clip, job.Frame, job.Context.Options, out var preparedHash)
                    ? preparedHash : FrameHashHelper.GetClipHash(_renderClips, clip, job.Frame, job.Context.Options, token);
                using var cache = new PreviewFrameCache(id => Path.Combine(ProjectCachePaths.GetRoot(_session.ProjectRoot), "thumbs", "perClip", id.ToString(), "dynamic"),
                    job.Frame, _request.CanvasWidth, _request.CanvasHeight, _request.ProjectWidth, _request.ProjectHeight,
                    _request.PreferredPixelFormat == PreviewPixelFormat.Rgba16FloatScRgb, false, token, keepHwAccelPicture: true,
                    findSource: key => _owner.FindPreviewSource(_session.ProjectRoot, key), metadataOnly: true,
                    cacheSourceFrames: !_request.DisableSourceFrameCache);
                pending = cache.Prepare(clip, _renderClips,
                    FrameHashHelper.HashValue(new { Identity = identity.Hash, _session.CacheNamespace }), identity.CanReuse,
                    _owner._artifacts.ResolveProjectPath(_session.ProjectRoot, $"thumbs/unCacheablePerClip/{_session.Id:N}/clip_{clip.Id:N}_{Guid.NewGuid():N}.vfd2m"),
                    Render, CanPersist);
                return new(job, clip.Id, pending, positioned, CanPersist());
            }
            catch
            {
                try { pending?.Dispose(); }
                finally { legacyPicture?.Dispose(); }
                throw;
            }
        }

        private void PackageClipFrame(DynamicPreviewPackagingJob job)
        {
            var token = job.Render.Token;
            token.ThrowIfCancellationRequested();
            var startedAt = Stopwatch.GetTimestamp();
            bool cacheHit;
            var entry = new DynamicPreviewCachedClip
            {
                ClipId = job.ClipId, FrameIndex = job.Render.Frame,
                IsPositionedClipPreview = job.Positioned,
            };
            if (MemoryOnly)
            {
                var data = PreviewFrameData.Create(job.LegacyPicture!, "", "", _request.PreferredPixelFormat == PreviewPixelFormat.Rgba16FloatScRgb);
                job.Render.MemoryData = data;
                entry.Width = data.Width; entry.Height = data.Height;
                cacheHit = false;
            }
            else if (job.Preview is { } preview)
            {
                var cached = preview.CompleteInfo();
                if (!cached.Persistent) _state.TemporaryFrames.Add(cached.Path);
                cacheHit = cached.CacheHit;
                entry.ProjectRelativePath = ProjectCachePaths.GetRelativePath(_session.ProjectRoot, cached.Path);
                entry.Width = cached.Info.Width; entry.Height = cached.Info.Height;
                entry.PreviewContainer = PreviewFrameContainer.GetContainer(cached.Path); entry.PreviewEffectHash = cached.Info.EffectHash;
                if (cached.Persistent)
                    _owner.TrackPreviewCacheAccess(_session.ProjectRoot, entry.ProjectRelativePath, job.ClipId, job.Render.Frame,
                        $"project:{_request.ProjectWidth}x{_request.ProjectHeight};canvas:{_request.CanvasWidth}x{_request.CanvasHeight};output:{entry.Width}x{entry.Height};format:{(cached.Info.ScRgb ? "scrgb16" : "png")}");
            }
            else
            {
                var artifact = _owner.RenderLegacyClipPreview(_session, new()
                {
                    SessionId = _session.Id, ClipId = job.ClipId, FrameIndex = job.Render.Frame,
                    PreferredPixelFormat = _request.PreferredPixelFormat,
                }, job.LegacyHash, _request.ProjectWidth, _request.ProjectHeight, _request.CanvasWidth, _request.CanvasHeight,
                    () => job.LegacyPicture is IHwAccelPicture gpu ? gpu.Download() : DynamicEffectGraph.CopyPicture(job.LegacyPicture!),
                    () => job.Reusable, false, token);
                if (artifact.ProjectRelativePath.StartsWith("thumbs/unCacheablePerClip/", StringComparison.Ordinal))
                    _state.TemporaryFrames.Add(_owner._artifacts.ResolveProjectPath(_session.ProjectRoot, artifact.ProjectRelativePath));
                cacheHit = artifact.CacheHit;
                entry.ProjectRelativePath = artifact.ProjectRelativePath;
                entry.Width = artifact.Width; entry.Height = artifact.Height;
            }
            token.ThrowIfCancellationRequested();
            job.Render.CachedClip = entry;
            job.Render.Reusable = job.Reusable;
            var now = Stopwatch.GetTimestamp();
            var elapsedMs = Stopwatch.GetElapsedTime(startedAt, now).TotalMilliseconds;
            var lastLog = Interlocked.Read(ref _lastSlowPackagingLog);
            if (job.Render.RenderMilliseconds + Stopwatch.GetElapsedTime(job.Render.PackagingQueuedAt, now).TotalMilliseconds >= 250
                && now - lastLog >= Stopwatch.Frequency * 5
                && Interlocked.CompareExchange(ref _lastSlowPackagingLog, now, lastLog) == lastLog)
                LogDiagnostic($"[DynamicPreview] Slow clip preparation: clip={job.ClipId}, frame={entry.FrameIndex}, size={entry.Width}x{entry.Height}, cacheHit={cacheHit}, render={job.Render.RenderMilliseconds:F1}ms, packageQueue={Stopwatch.GetElapsedTime(job.Render.PackagingQueuedAt, startedAt).TotalMilliseconds:F1}ms, package={elapsedMs:F1}ms.");
        }

        public void Cancel()
        {
            try { _cancellation.Cancel(); } catch (ObjectDisposedException) { }
            catch (Exception ex) { Log(ex, $"Cancel dynamic preview cache {PlaybackId}", this); }
        }

        private async Task FinishAsync()
        {
            Cancel();
            if (_audio is not null && _audio.Generation == _audioGeneration)
            {
                try { await _audio.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { Log(ex, "Stop cached dynamic preview audio", this); }
            }
            try
            {
                _clock = RenderRpcSerializer.Clone(_clock);
                _clock.IsCompleted = true;
                _clock.IsRunning = false;
                _clock.Error = _error;
                if (_clock.SessionId != Guid.Empty) await _sendClock(_clock, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (Exception ex) { Log(ex, $"Finish dynamic preview cache {PlaybackId}", this); }
            finally
            {
                _renderClips = [];
                _operation.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Cancel();
            try
            {
                if (_run is not null) await _run.ConfigureAwait(false);
                else await FinishAsync().ConfigureAwait(false);
            }
            finally
            {
                _previewPipe?.Dispose();
                _cancellation.Dispose();
            }
            LogDiagnostic($"[DynamicPreview] Stopped cache producer {PlaybackId}; preview state retained. {_state.GpuSession.GetStatistics()}");
        }
    }
}
