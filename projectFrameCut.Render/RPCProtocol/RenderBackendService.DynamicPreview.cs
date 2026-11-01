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
                if (ex is not OperationCanceledException || state.ClipSession is null)
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
        public RenderClipSession? ClipSession { get; set; }

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

        public ValueTask DisposeAsync()
        {
            var retained = ClipSession?.Clips ?? [];
            ClipSession?.Dispose();
            DisposeResources(Clips.Where(c => !retained.Contains(c)), [], null);
            Index?.Dispose();
            Frames.Clear();
            try
            {
                foreach (var path in TemporaryFrames) File.Delete(path);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch (Exception ex) { Log(ex, $"Delete dynamic preview state {CacheDirectory}"); }
            return ValueTask.CompletedTask;
        }
    }

    private sealed record DynamicPreviewRenderJob(uint Frame, FrameHashContext Context, CancellationToken Token)
    {
        public double PrepareMilliseconds;
        public long PackagingQueuedAt;
        public DynamicPreviewCachedClip? CachedClip;
        public PreviewFrameData? MemoryData;
        public bool Reusable;
        public DynamicPreviewPackagingJob? Packaging;
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
        private System.IO.Pipes.NamedPipeClientStream? _previewPipe;
        private bool MemoryOnly => _request.PreviewPipeName.Length > 0;
        private readonly long _minimumBufferFrames;
        private long _nextFrame;
        private readonly PreviewBufferWindow _bufferWindow;
        private readonly int _diskBufferFrames;
        private int BufferFrameCount => MemoryOnly ? _bufferWindow.FrameCount : _diskBufferFrames;
        private long _reusedFrames;
        private long _lastSlowPackagingLog;
        private long _startedAt;
        private PreviewAudioSession? _audio;
        private long _audioGeneration;
        private Task? _rendering;
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
            _diskBufferFrames = (int)Math.Clamp(Math.Ceiling(request.FrameRate * Math.Max(1, request.PreviewBufferSeconds)), 1, int.MaxValue);
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
            await PrepareClipsAsync(token).ConfigureAwait(false);
            _rendering = Task.Run(async () =>
            {
                using var sources = ProjectExternalSourceRuntime.Use(_session.ExternalSources);
                try { await RenderFramesAsync(_operation.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_operation.Token.IsCancellationRequested) { }
                catch { Cancel(); throw; }
            });
            while (true)
            {
                long target = Math.Min(_session.Duration, (long)_request.StartFrame + BufferFrameCount);
                if (Interlocked.Read(ref _nextFrame) >= target) break;
                if (_rendering.IsCompleted) await _rendering.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                double elapsed = Stopwatch.GetElapsedTime(bufferingStartedAt).TotalSeconds;
                await ReportBufferingProgressAsync(Math.Clamp((_nextFrame - _request.StartFrame) / (double)Math.Max(1, target - _request.StartFrame), 0, 1),
                    token, frameRate: elapsed > 0 ? (_nextFrame - _request.StartFrame) / elapsed : null).ConfigureAwait(false);
                await Task.WhenAny(_rendering, Task.Delay(100, token)).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            await ReportBufferingProgressAsync(1, token).ConfigureAwait(false);
            LogDiagnostic($"[DynamicPreview] Buffered {_nextFrame - _request.StartFrame} frames from {_request.StartFrame} with the shared renderer in {Stopwatch.GetElapsedTime(bufferingStartedAt).TotalSeconds:F1}s for {PlaybackId}; reused {_reusedFrames} clip frames.");
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
            clock.BufferFrames = BufferFrameCount;
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
                clock.BufferFrames = BufferFrameCount;
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
            var render = _rendering!;
            try
            {
                while (!token.IsCancellationRequested && !_clock.IsCompleted)
                {
                    await PublishClockAsync(token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (_clock.IsCompleted) break;

                    long first = (long)Math.Floor(_clock.Frame);
                    long buffered;
                    LogDiagnostic($"[DynamicPreview] Rebuffering {PlaybackId} at frame {_clock.Frame:F1}; producer retained; buffer frames={BufferFrameCount}.");
                    while (true)
                    {
                        long target = Math.Min(_session.Duration, first + Math.Max(2, BufferFrameCount));
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
                Cancel();
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
                long minimum = Math.Max(1, Math.Min(_minimumBufferFrames, BufferFrameCount / 2));
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
                BufferFrames = BufferFrameCount,
            }, token);

        private async Task PrepareClipsAsync(CancellationToken token)
        {
            if (_state.ClipSession is not null)
            {
                _renderClips = _state.ClipSession.Clips;
                LogDiagnostic($"[DynamicPreview] Reusing {_renderClips.Length} clips and {_state.Frames.Count} clip frames for {PlaybackId}.");
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
            _state.Clips = await Task.Run(() => _owner.CreateClips(draft, _session.Assets, string.Empty,
                _session.ProjectRoot, token, null, _session.ClipSignatures), token).ConfigureAwait(false);
            _renderClips = GetVisualClips(_state.Clips);
            _state.ClipSession = new(_renderClips);
        }

        private async Task RenderFramesAsync(CancellationToken token)
        {
            if (_request.StartFrame >= _session.Duration) return;
            var jobs = new List<DynamicPreviewRenderJob>();
            using var composer = new PreviewRenderComposer((frame, content, ct) =>
            {
                var job = new DynamicPreviewRenderJob(frame, FrameHashContext.Current!, ct);
                if (!MemoryOnly && _state.Frames.TryGetValue((frame, content.Clip.Id), out var cached)
                    && File.Exists(_owner._artifacts.ResolveProjectPath(_session.ProjectRoot, cached.ProjectRelativePath)))
                {
                    job.CachedClip = cached;
                    job.Reusable = true;
                    _reusedFrames++;
                }
                else
                {
                    var startedAt = Stopwatch.GetTimestamp();
                    job.Packaging = PrepareClipFrame(content.Clip, job, content.Picture);
                    job.PrepareMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
                    job.PackagingQueuedAt = Stopwatch.GetTimestamp();
                }
                jobs.Add(job);
            }, (frame, ct) =>
            {
                try
                {
                    Parallel.ForEach(jobs, new ParallelOptions
                    {
                        CancellationToken = ct,
                        MaxDegreeOfParallelism = Math.Max(1, _request.MaxParallelWorkers),
                    }, job =>
                    {
                        if (job.Packaging is { } packaging) PackageClipFrame(packaging);
                    });
                    if (MemoryOnly)
                        foreach (var job in jobs)
                        {
                            var entry = job.CachedClip!;
                            _bufferWindow.Observe(entry.Width, entry.Height, job.MemoryData is { } data
                                ? (long)data.Pixels.Length + data.AlphaMask.Length : 0);
                        }
                    while (frame >= Math.Floor(_clock.Frame) + Math.Max(2, BufferFrameCount))
                        Task.Delay(10, ct).GetAwaiter().GetResult();
                    if (MemoryOnly)
                    {
                        var credit = new byte[1];
                        _previewPipe!.ReadExactlyAsync(credit, ct).AsTask().GetAwaiter().GetResult();
                        foreach (var job in jobs)
                        {
                            var entry = job.CachedClip!;
                            MemoryPreviewChannel.SendAsync(_previewPipe, new(entry.ClipId, frame,
                                entry.IsPositionedClipPreview, job.MemoryData!.Info), job.MemoryData, ct).GetAwaiter().GetResult();
                            job.MemoryData = null;
                        }
                        RenderPipeFrame.WriteAsync(_previewPipe, JsonSerializer.SerializeToUtf8Bytes((long)frame + 1), ct).GetAwaiter().GetResult();
                        _previewPipe.ReadExactlyAsync(credit, ct).AsTask().GetAwaiter().GetResult();
                    }
                    else _state.Index!.Write(jobs.Select(j => j.CachedClip!).ToArray());
                    foreach (var job in jobs)
                        if (job.Reusable) _state.Frames[(frame, job.CachedClip!.ClipId)] = job.CachedClip;
                    Interlocked.Exchange(ref _nextFrame, (long)frame + 1);
                }
                finally
                {
                    foreach (var job in jobs) job.Packaging?.Dispose();
                    jobs.Clear();
                }
            });
            var renderer = new Renderer
            {
                Clips = _renderClips, ClipSession = _state.ClipSession, Composer = composer,
                LogRenderSummary = false, UseClipInitializationFallback = true,
                AudioSamples = _session.AudioSamples, FrameRate = _request.FrameRate,
                StartFrame = _request.StartFrame, Duration = _session.Duration - _request.StartFrame,
                TargetWidth = _request.CanvasWidth, TargetHeight = _request.CanvasHeight,
                ProjectRelativeWidth = _request.ProjectWidth, ProjectRelativeHeight = _request.ProjectHeight,
                ProcessEffectFromCanvas = Timeline.ProcessEffectFromCanvas,
                MaxThreads = Math.Max(1, _request.MaxParallelWorkers),
                AllowWorkerOversubscription = _request.AllowWorkerOversubscription,
                RenderAheadFrames = _request.RenderAheadFrames,
                EnableMemoryBudget = _request.EnableMemoryBudget, MaxBufferedBytes = _request.MaxBufferedBytes,
                EnableGpuBufferPoolLimit = !_request.DisableGpuBufferPoolLimit,
                GpuBufferPoolMaxBytes = _request.GpuBufferPoolMaxBytes,
                EnableThreadAffinity = _request.EnableThreadAffinity, WorkerCPUCoreIndexs = _request.WorkerCPUCoreIndexs,
                ReuseDynamicPreviewCache = !MemoryOnly && !_request.DisableSourceFrameCache,
                DynamicPreviewCacheProjectRoot = _session.ProjectRoot,
            };
            try
            {
                renderer.PrepareRender(token);
                await renderer.GoRender(token).ConfigureAwait(false);
            }
            finally
            {
                foreach (var job in jobs) job.Packaging?.Dispose();
            }
        }

        private DynamicPreviewPackagingJob PrepareClipFrame(IClip clip, DynamicPreviewRenderJob job, IPicture picture)
        {
            var token = job.Token;
            token.ThrowIfCancellationRequested();
            PreviewFrameCache.PendingFrame? pending = null;
            IPicture? legacyPicture = null;
            try
            {
                using var sourceScope = ProjectExternalSourceRuntime.Use(_session.ExternalSources);
                using var audio = EffectProcessing.OpenAudioFrame(_session.AudioSamples);
                using var hashScope = job.Context.Enter();
                IClip[] dependencies = [clip, .. Timeline.CollectHashDependencies(_renderClips, clip, [clip.Id])];
                bool CanPersist() => dependencies.All(c => !ClipInitializationFailure.IsMarked(c) && Timeline.CanCacheClipSource(c));
                IPicture Render() => DynamicEffectGraph.CopyPicture(picture);
                bool positioned = ProjectReferenceRuntime.IsCanvasClip(clip) || TransformProcessing.HasActiveTransform(clip, _renderClips, job.Frame);
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
            if (job.Render.PrepareMilliseconds + Stopwatch.GetElapsedTime(job.Render.PackagingQueuedAt, now).TotalMilliseconds >= 250
                && now - lastLog >= Stopwatch.Frequency * 5
                && Interlocked.CompareExchange(ref _lastSlowPackagingLog, now, lastLog) == lastLog)
                LogDiagnostic($"[DynamicPreview] Slow clip preparation: clip={job.ClipId}, frame={entry.FrameIndex}, size={entry.Width}x{entry.Height}, cacheHit={cacheHit}, prepare={job.Render.PrepareMilliseconds:F1}ms, packageQueue={Stopwatch.GetElapsedTime(job.Render.PackagingQueuedAt, startedAt).TotalMilliseconds:F1}ms, package={elapsedMs:F1}ms.");
        }

        public void Cancel()
        {
            try { _cancellation.Cancel(); } catch (ObjectDisposedException) { }
            catch (Exception ex) { Log(ex, $"Cancel dynamic preview cache {PlaybackId}", this); }
        }

        private async Task FinishAsync()
        {
            Cancel();
            if (_rendering is not null)
            {
                try { await _rendering.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _error ??= ex.Message; }
            }
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
            LogDiagnostic($"[DynamicPreview] Stopped cache producer {PlaybackId}; preview state retained.");
        }
    }
}
