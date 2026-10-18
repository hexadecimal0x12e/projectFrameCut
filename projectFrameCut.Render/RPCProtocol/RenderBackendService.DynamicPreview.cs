using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.PreviewAudio;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Rendering;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace projectFrameCut.Render.RPCProtocol;

public sealed partial class RenderBackendService
{
    private readonly ConcurrentDictionary<Guid, DynamicPreviewProducer> _dynamicPreviews = new();
    private readonly SemaphoreSlim _dynamicPreviewGate = new(1, 1);

    private async ValueTask<DynamicPreviewSession> StartDynamicPreviewAsync(StartDynamicPreviewRequest request, CancellationToken token)
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
            var playbackId = request.PlaybackId == Guid.Empty ? Guid.NewGuid() : request.PlaybackId;
            var result = new DynamicPreviewSession
            {
                SessionId = session.Id, PlaybackId = playbackId, StartFrame = request.StartFrame, FrameRate = request.FrameRate,
                CacheDirectory = $"thumbs/dynamicPlayback/{session.Id:N}/{playbackId:N}", PixelFormat = request.PreferredPixelFormat,
            };
            var producer = new DynamicPreviewProducer(this, session, request, result);
            try
            {
                await producer.StartAsync(operation.Token).ConfigureAwait(false);
                operation.Token.ThrowIfCancellationRequested();
                _dynamicPreviews[session.Id] = producer;
                LogDiagnostic($"[DynamicPreview] Started cache producer {playbackId} at frame {request.StartFrame}, {request.CanvasWidth}x{request.CanvasHeight}.");
                return result;
            }
            catch
            {
                await producer.DisposeAsync().ConfigureAwait(false);
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
                && (request.PlaybackId == Guid.Empty || request.PlaybackId == producer.PlaybackId)
                && _dynamicPreviews.TryRemove(request.SessionId, out _))
                await producer.DisposeAsync().ConfigureAwait(false);
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
        }
        finally { _dynamicPreviewGate.Release(); }
    }

    private static void WriteDynamicPreviewJson<T>(string path, T value)
    {
        var temporaryPath = path + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value));
            File.Move(temporaryPath, path, true);
        }
        finally { try { File.Delete(temporaryPath); } catch { } }
    }

    private sealed class DynamicPreviewProducer : IAsyncDisposable
    {
        private readonly RenderBackendService _owner;
        private readonly BackendSession _session;
        private readonly StartDynamicPreviewRequest _request;
        private readonly DynamicPreviewSession _playback;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly BackendSession.SessionOperation _operation;
        private readonly string _cacheRoot;
        private readonly Func<DynamicPreviewClock, CancellationToken, ValueTask> _sendClock;
        private readonly ConcurrentQueue<(uint Frame, string Index, string? TemporaryFrame)> _entries = new();
        private IClip[][] _renderClips = [];
        private readonly long _bufferFrames;
        private readonly long _minimumBufferFrames;
        private long _nextFrame;
        private long _startedAt;
        private PreviewAudioSession? _audio;
        private long _audioGeneration;
        private Task? _run;
        private DynamicPreviewClock _clock = new();
        private string? _error;
        private int _disposed;

        public Guid PlaybackId => _playback.PlaybackId;

        public DynamicPreviewProducer(RenderBackendService owner, BackendSession session, StartDynamicPreviewRequest request, DynamicPreviewSession playback)
        {
            _owner = owner;
            _session = session;
            _request = request;
            _playback = playback;
            _sendClock = RenderRpcNotifications.DynamicPreviewClockSender
                ?? throw new NotSupportedException("Dynamic preview requires RPC clock notifications.");
            _cacheRoot = owner._artifacts.ResolveProjectPath(session.ProjectRoot, playback.CacheDirectory);
            _operation = session.BeginOperation(_cancellation.Token);
            _bufferFrames = Math.Max(1L, (long)Math.Ceiling(request.FrameRate * 10));
            _minimumBufferFrames = Math.Max(1L, (long)Math.Ceiling(request.FrameRate * 5));
            _nextFrame = request.StartFrame;
            _clock.Frame = request.StartFrame;
        }

        public async Task StartAsync(CancellationToken token)
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token, _operation.Token);
            token = startup.Token;
            Directory.CreateDirectory(_cacheRoot);
            using var sourceScope = ProjectExternalSourceRuntime.Use(_session.ExternalSources);
            var bufferingStartedAt = Stopwatch.GetTimestamp();
            await ReportBufferingProgressAsync(0, token).ConfigureAwait(false);
            await CreateRenderWorkersAsync(token).ConfigureAwait(false);
            await RenderFramesAsync(token, bufferStart: _request.StartFrame).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await ReportBufferingProgressAsync(1, token).ConfigureAwait(false);
            LogDiagnostic($"[DynamicPreview] Buffered {_nextFrame - _request.StartFrame} frames from {_request.StartFrame} with {_renderClips.Length} workers in {Stopwatch.GetElapsedTime(bufferingStartedAt).TotalSeconds:F1}s for {PlaybackId}.");
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
            _clock = GetClock();
            _playback.Clock = _clock;
            _run = Task.Run(RunAsync);
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
                if (audio.HasAudio)
                    return new()
                    {
                        SessionId = _session.Id, PlaybackId = PlaybackId,
                        Timestamp = now, Frequency = Stopwatch.Frequency,
                        Frame = _request.StartFrame + audio.PlayedSamples * _request.FrameRate / Math.Max(1, audio.SampleRate),
                        IsRunning = audio.IsRunning && !audio.IsPaused,
                        IsCompleted = audio.BufferedSamples == 0 && _audio.ProducerCompleted,
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

        private async Task RunAsync()
        {
            using var sourceScope = ProjectExternalSourceRuntime.Use(_session.ExternalSources);
            var token = _operation.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    using var rendering = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var render = Task.Run(async () =>
                    {
                        try { await RenderFramesAsync(rendering.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (rendering.IsCancellationRequested) { }
                        catch { Cancel(); throw; }
                    });
                    try { await PublishClockAsync(token).ConfigureAwait(false); }
                    finally
                    {
                        rendering.Cancel();
                        await render.ConfigureAwait(false);
                    }
                    token.ThrowIfCancellationRequested();

                    var pausedAt = _clock.Timestamp;
                    LogDiagnostic($"[DynamicPreview] Rebuffering {PlaybackId} at frame {_clock.Frame:F1} with {Math.Max(0, _nextFrame - _clock.Frame) / _request.FrameRate:F1}s cached.");
                    await RenderFramesAsync(token, bufferStart: (long)Math.Ceiling(_clock.Frame)).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (_audio is not null && _audio.Generation == _audioGeneration)
                        await _audio.ResumeAsync(token).ConfigureAwait(false);
                    _startedAt += Stopwatch.GetTimestamp() - pausedAt;
                    LogDiagnostic($"[DynamicPreview] Resumed {PlaybackId} at frame {_clock.Frame:F1}, buffered through frame {_nextFrame}.");
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _error = ex.Message;
                Log(ex, $"Produce dynamic preview cache {PlaybackId}", this);
            }
            finally { await FinishAsync().ConfigureAwait(false); }
        }

        private async Task PublishClockAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                _clock = GetClock();
                if (_clock.Frame >= _session.Duration) _clock.IsCompleted = true;
                var nextFrame = Interlocked.Read(ref _nextFrame);
                if (!_clock.IsCompleted && nextFrame < _session.Duration && nextFrame - _clock.Frame < _minimumBufferFrames)
                {
                    if (_audio is not null && _audio.Generation == _audioGeneration)
                        await _audio.PauseAsync(token).ConfigureAwait(false);
                    _clock = GetClock();
                    _clock.IsRunning = false;
                    _clock.BufferingProgress = 0;
                    await _sendClock(_clock, token).ConfigureAwait(false);
                    return;
                }
                await _sendClock(_clock, token).ConfigureAwait(false);
                if (_clock.IsCompleted) { Cancel(); break; }
                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }

        private ValueTask ReportBufferingProgressAsync(double progress, CancellationToken token, double? frame = null)
            => _sendClock(new()
            {
                SessionId = _session.Id, PlaybackId = PlaybackId,
                Timestamp = Stopwatch.GetTimestamp(), Frequency = Stopwatch.Frequency,
                Frame = frame ?? _request.StartFrame, BufferingProgress = progress,
            }, token);

        private async Task CreateRenderWorkersAsync(CancellationToken token)
        {
            var draft = JsonSerializer.Deserialize<DraftStructureJSON>(_session.TimelineJson, _owner._jsonOptions)
                ?? throw new InvalidDataException("Dynamic preview timeline is invalid.");
            var clips = _session.Clips.ToDictionary(c => c.Id);
            foreach (var dto in draft.Clips)
            {
                VideoClipRotation.Migrate(dto);
                if (clips.TryGetValue(dto.Id, out var clip)) dto.FilePath = clip.FilePath;
            }

            _renderClips = new IClip[RenderMemoryBudget.LimitWorkers(Math.Clamp(Environment.ProcessorCount, 1, 4),
                RenderMemoryBudget.EstimateClipBytes(_request.CanvasWidth, _request.CanvasHeight), RenderMemoryBudget.DefaultBytes)][];
            await Task.WhenAll(Enumerable.Range(0, _renderClips.Length).Select(i => Task.Run(() =>
            {
                try
                {
                    _renderClips[i] = _owner.CreateClips(draft, _session.Assets, string.Empty, _session.ProjectRoot,
                        token, null, _session.ClipSignatures);
                }
                catch { Cancel(); throw; }
            }, token))).ConfigureAwait(false);
            Log($"[DynamicPreview] Using {_renderClips.Length} render workers. Effect implementations: {Effect.EffectHelper.GetImplementationSummary(_renderClips[0])}.");
        }

        private async Task RenderFramesAsync(CancellationToken token, long? bufferStart = null)
        {
            var bufferEnd = Math.Min(_session.Duration, (bufferStart ?? _request.StartFrame) + _bufferFrames);
            var lastProgressAt = Stopwatch.GetTimestamp();
            while (_nextFrame < _session.Duration)
            {
                token.ThrowIfCancellationRequested();
                var current = bufferStart ?? (long)Math.Clamp(Math.Floor(GetClock().Frame), _request.StartFrame, uint.MaxValue);
                if (_nextFrame >= current + _bufferFrames)
                {
                    if (bufferStart.HasValue) return;
                    await Task.Delay(10, token).ConfigureAwait(false);
                    continue;
                }
                int count = (int)Math.Min(_renderClips.Length,
                    Math.Min(_session.Duration, current + _bufferFrames) - _nextFrame);
                var tasks = new Task[count];
                for (var i = 0; i < count; i++)
                {
                    var clips = _renderClips[i];
                    var frame = (uint)(_nextFrame + i);
                    tasks[i] = Task.Run(async () =>
                    {
                        try { await RenderFrameAsync(clips, frame, token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch { Cancel(); throw; }
                    }, token);
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
                while (_entries.TryPeek(out var entry) && (long)entry.Frame + _bufferFrames < current)
                {
                    _entries.TryDequeue(out _);
                    try { File.Delete(entry.Index); if (entry.TemporaryFrame is not null) File.Delete(entry.TemporaryFrame); }
                    catch (IOException) { }
                }
                Interlocked.Add(ref _nextFrame, count);
                if (bufferStart.HasValue && Stopwatch.GetElapsedTime(lastProgressAt).TotalMilliseconds >= 200)
                {
                    lastProgressAt = Stopwatch.GetTimestamp();
                    await ReportBufferingProgressAsync((_nextFrame - bufferStart.Value)
                        / (double)Math.Max(1L, bufferEnd - bufferStart.Value), token, _clock.Frame).ConfigureAwait(false);
                }
            }
        }

        private async Task RenderFrameAsync(IClip[] clips, uint frame, CancellationToken token)
        {
            using var context = new FrameHashContext(new()
            {
                Mode = _request.FrameHashMode, Width = _request.CanvasWidth, Height = _request.CanvasHeight,
                ProjectWidth = _request.ProjectWidth, ProjectHeight = _request.ProjectHeight,
                FrameRate = _session.FrameRate,
                PixelMode = _request.PreferredPixelFormat == PreviewPixelFormat.Rgba16FloatScRgb ? 16 : 8,
            }, token, _session.NodeCache);
            using var hashScope = context.Enter();
            foreach (var clip in clips.Where(c => c.ClipType != ClipMode.AudioClip && c.ContainsFrame(frame)))
            {
                token.ThrowIfCancellationRequested();
                var artifact = await _owner.RenderClipPreviewAsync(new()
                {
                    SessionId = _session.Id, ClipId = clip.Id, FrameIndex = frame,
                    CanvasWidth = _request.CanvasWidth, CanvasHeight = _request.CanvasHeight,
                    ProjectWidth = _request.ProjectWidth, ProjectHeight = _request.ProjectHeight,
                    PreferredPixelFormat = _request.PreferredPixelFormat, FrameHashMode = _request.FrameHashMode,
                }, token, registerArtifact: false, previewSession: _session, previewClips: clips).ConfigureAwait(false);
                var path = Path.Combine(_cacheRoot, $"{frame:D10}_{clip.Id:N}.json");
                _entries.Enqueue((frame, path, artifact.ProjectRelativePath.StartsWith("thumbs/unCacheablePerClip/", StringComparison.Ordinal)
                    ? _owner._artifacts.ResolveProjectPath(_session.ProjectRoot, artifact.ProjectRelativePath) : null));
                token.ThrowIfCancellationRequested();
                WriteDynamicPreviewJson(path, new DynamicPreviewCachedClip
                {
                    ClipId = clip.Id, FrameIndex = frame, ProjectRelativePath = artifact.ProjectRelativePath,
                    Width = artifact.Width, Height = artifact.Height,
                    IsPositionedClipPreview = TransformProcessing.HasActiveTransform(clip, clips, frame),
                });
            }
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
                foreach (var clips in _renderClips.OfType<IClip[]>()) DisposeResources(clips, [], null);
                _renderClips = [];
                _operation.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Cancel();
            if (_run is not null) await _run.ConfigureAwait(false);
            else await FinishAsync().ConfigureAwait(false);
            _cancellation.Dispose();
            try
            {
                foreach (var entry in _entries)
                    if (entry.TemporaryFrame is not null) File.Delete(entry.TemporaryFrame);
                if (Directory.Exists(_cacheRoot)) Directory.Delete(_cacheRoot, true);
            }
            catch (Exception ex) { Log(ex, $"Delete dynamic preview cache {PlaybackId}", this); }
            LogDiagnostic($"[DynamicPreview] Stopped cache producer {PlaybackId}.");
        }
    }
}
