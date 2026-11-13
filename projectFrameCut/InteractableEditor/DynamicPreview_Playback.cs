using Microsoft.Data.Sqlite;
using projectFrameCut.ApplicationAPIBase.Interaction;
using projectFrameCut.Controls;
using projectFrameCut.InteractableEditor;
using projectFrameCut.LivePreview;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.RPCProtocol;
using projectFrameCut.Services;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Shared;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace projectFrameCut.InteractableEditor
{
    public sealed partial class DynamicPreview
    {
        public uint ResolvePlaybackFrame(PreviewAudioClock clock, double frameRate, uint fallbackFrame, ref double correctedFrame)
        {
            var target = clock.HasAudio && clock.SampleRate > 0
                ? clock.StartFrame + clock.PlayedSamples * Math.Max(1d, frameRate) / clock.SampleRate
                : fallbackFrame;
            if (correctedFrame < 0 || Math.Abs(target - correctedFrame) > 2d)
                correctedFrame = target;
            else
                correctedFrame += Math.Clamp(target - correctedFrame, -0.5d, 0.5d);
            return (uint)Math.Max(0, Math.Floor(correctedFrame));
        }

        internal async Task<IReadOnlyList<PreparedPreview>> PrepareCachedFrameAsync(DynamicPreviewCache cache, uint frameIndex,
            int projectWidth, int projectHeight, CancellationToken token)
        {
            using var effectScope = effectRuntime.Enter();
            return await Task.Run<IReadOnlyList<PreparedPreview>>(() =>
            {
                var clips = AcquireClipsSnapshot();
                try
                {
                    var requests = ResolveRequests(clips, frameIndex, projectWidth, projectHeight);
                    var frameClips = new IClip[requests.Count];
                    for (var i = 0; i < requests.Count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        frameClips[i] = ClipArgumentBinding.InitializeFrame(requests[requests.Count - i - 1].Clip, frameIndex);
                    }

                    var prepared = new PreparedPreview[frameClips.Length];
                    Parallel.For(0, frameClips.Length, new ParallelOptions
                    {
                        CancellationToken = token,
                        MaxDegreeOfParallelism = RenderSchedulingSettings.MaxParallelWorkers,
                    }, i =>
                    {
                        try
                        {
                            prepared[i] = cache.TryGetClip(frameIndex, frameClips[i].Id, out var source, out var positioned)
                                ? CreatePreparedClipPreview(frameClips[i], source, positioned)
                                : new PreparedPreview(frameClips[i].Id, null,
                                    $"Preview cache is missing for clip '{frameClips[i].Name}' at frame {frameIndex}.", frameClips[i]);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            Log(ex, $"Read cached preview for clip {frameClips[i].Id} at frame {frameIndex}", this);
                            prepared[i] = new PreparedPreview(frameClips[i].Id, null, ex.Message, frameClips[i]);
                        }
                    });
                    SuppressCompositedPreviews(prepared);
                    return prepared;
                }
                finally { ReleaseClipsSnapshot(); }
            }, token).ConfigureAwait(false);
        }
    }
}

namespace projectFrameCut.LivePreview
{
    public partial class LivePreviewer
    {
        private DynamicPreviewCache? _dynamicPreviewCache;
        private MemoryPreviewCache? _retainedMemoryPreview;
        private string? _retainedMemoryPreviewKey;
        private readonly ConcurrentDictionary<string, DynamicPreviewFrameStore> _dynamicPreviewFrameStores = new(StringComparer.Ordinal);
        private string? _dynamicPreviewSnapshotHash;
        private readonly SemaphoreSlim _dynamicPreviewControlGate = new(1, 1);

        internal bool HasDynamicPreview => _dynamicPreviewCache is not null;
        internal double? DynamicPreviewBufferingFrameRate { get; private set; }

        internal async Task<DynamicPreviewCache> StartDynamicPreviewAsync(uint startFrame, int canvasWidth, int canvasHeight,
            int projectWidth, int projectHeight, double frameRate, CancellationToken token)
        {
            await _dynamicPreviewControlGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_dynamicPreviewCache is not null)
                {
                    var previous = _dynamicPreviewCache;
                    _dynamicPreviewCache = null;
                    previous.Cancel();
                    try
                    {
                        await previous.Client.StopDynamicPreviewAsync(new()
                        {
                            SessionId = previous.Session.SessionId, PlaybackId = previous.Session.PlaybackId,
                        }).ConfigureAwait(false);
                    }
                    finally { await previous.StopAsync().ConfigureAwait(false); }
                }
                var client = RpcClient ?? RenderRpcBootstrap.Client;
                var sessionId = RenderSessionId;
                var playbackId = Guid.NewGuid();
                long poolBytes = (long)Math.Clamp(SettingsManager.GetSettingAs("Edit_DynamicPreviewClipPoolMiB",
                    DynamicPreviewFrameStore.DefaultPoolMiB, DynamicPreviewFrameStore.DefaultPoolMiB), 1, 4096) * 1024 * 1024;
                var memoryKey = $"{sessionId}:{_dynamicPreviewSnapshotHash}:{canvasWidth}:{canvasHeight}:{projectWidth}:{projectHeight}:{frameRate}:{DefaultOutputMode}:{poolBytes}";
                MemoryPreviewCache? memory = DynamicPreviewSettings.MemoryOnly
                    ? new(poolBytes, startFrame, frameRate, _retainedMemoryPreviewKey == memoryKey ? _retainedMemoryPreview : null) : null;
                _retainedMemoryPreview = memory;
                _retainedMemoryPreviewKey = memoryKey;
                try
                {
                    DynamicPreviewBufferingFrameRate = null;
                    OnProgressChanged?.Invoke(0, TimeSpan.Zero);
                    var startup = client.StartDynamicPreviewAsync(new()
                    {
                        SessionId = sessionId, PlaybackId = playbackId, StartFrame = startFrame, CanvasWidth = canvasWidth, CanvasHeight = canvasHeight,
                        ProjectWidth = projectWidth, ProjectHeight = projectHeight, FrameRate = frameRate, StartAudio = HasAudioSources(),
                        MaxParallelWorkers = RenderSchedulingSettings.MaxParallelWorkers,
                        AllowWorkerOversubscription = RenderSchedulingSettings.AllowWorkerOversubscription,
                        RenderAheadFrames = RenderSchedulingSettings.RenderAheadFrames,
                        EnableThreadAffinity = RenderSchedulingSettings.EnableThreadAffinity,
                        WorkerCPUCoreIndexs = RenderSchedulingSettings.WorkerCPUCoreIndexs,
                        EnableMemoryBudget = RenderSchedulingSettings.EnableMemoryBudget,
                        MaxBufferedBytes = RenderSchedulingSettings.MaxBufferedBytes,
                        DisableGpuBufferPoolLimit = !RenderSchedulingSettings.EnableGpuBufferPoolLimit,
                        GpuBufferPoolMaxBytes = RenderSchedulingSettings.GpuBufferPoolMaxBytes,
                        PreviewPipeName = memory?.Name ?? "", PreviewPipeToken = memory?.Secret ?? "", PreviewClipPoolMaxBytes = poolBytes,
                        PreviewBufferSeconds = DynamicPreviewSettings.BufferSeconds,
                        UsePreviewMetadata = true,
                        DisableSourceFrameCache = !DynamicPreviewSettings.CacheSourceFrames,
                        PreferredPixelFormat = OperatingSystem.IsWindows() && DefaultOutputMode != NativePreviewOutputMode.Disabled
                            ? PreviewPixelFormat.Rgba16FloatScRgb : PreviewPixelFormat.EncodedImage,
                    }, token).AsTask();
                    double lastProgress = 0;
                    while (!startup.IsCompleted && !token.IsCancellationRequested)
                    {
                        var clock = client.GetDynamicPreviewClock(sessionId, playbackId);
                        if (clock?.BufferingProgress is double progress && progress > lastProgress)
                        {
                            lastProgress = progress;
                            DynamicPreviewBufferingFrameRate = clock.BufferingFrameRate ?? DynamicPreviewBufferingFrameRate;
                            OnProgressChanged?.Invoke(progress, TimeSpan.Zero);
                        }
                        await Task.WhenAny(startup, Task.Delay(200, token)).ConfigureAwait(false);
                    }
                    var session = await startup.ConfigureAwait(false);
                    if (_dynamicPreviewFrameStores.Any(p => p.Key != session.CacheDirectory
                        || !ReferenceEquals(p.Value.Client, client) || p.Value.MaxPoolBytes != poolBytes))
                        _dynamicPreviewFrameStores.Clear();
                    var frames = _dynamicPreviewFrameStores.GetOrAdd(session.CacheDirectory, _ => new(client, poolBytes));
                    _dynamicPreviewCache = new DynamicPreviewCache(client, ProjectRoot, session,
                        canvasWidth, canvasHeight, projectWidth, projectHeight, frames, memory);
                    token.ThrowIfCancellationRequested();
                    OnProgressChanged?.Invoke(1, TimeSpan.Zero);
                    return _dynamicPreviewCache;
                }
                catch
                {
                    var cache = _dynamicPreviewCache;
                    _dynamicPreviewCache = null;
                    cache?.Cancel();
                    memory?.Cancel();
                    try { await client.StopDynamicPreviewAsync(new() { SessionId = sessionId, PlaybackId = playbackId }).ConfigureAwait(false); }
                    catch (Exception ex) { Log(ex, "Stop canceled dynamic preview startup", this); }
                    finally
                    {
                        if (cache is not null) await cache.StopAsync().ConfigureAwait(false);
                        else if (memory is not null) await memory.DisposeAsync().ConfigureAwait(false);
                    }
                    throw;
                }
            }
            finally { _dynamicPreviewControlGate.Release(); }
        }

        internal async Task StopDynamicPreviewAsync(DynamicPreviewCache? cache = null, bool clearCache = false)
        {
            await _dynamicPreviewControlGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (cache is not null && !ReferenceEquals(_dynamicPreviewCache, cache)) return;
                cache = _dynamicPreviewCache;
                _dynamicPreviewCache = null;
                if (cache is null && !clearCache) return;
                cache?.Cancel();
                try
                {
                    if (cache is not null || _backendReady.Task.IsCompletedSuccessfully)
                    {
                        await (cache?.Client ?? RpcClient ?? RenderRpcBootstrap.Client).StopDynamicPreviewAsync(new()
                        {
                            SessionId = cache?.Session.SessionId ?? RenderSessionId,
                            PlaybackId = cache?.Session.PlaybackId ?? Guid.Empty, ClearCache = clearCache,
                        }).ConfigureAwait(false);
                    }
                }
                finally { if (cache is not null) await cache.StopAsync().ConfigureAwait(false); }
            }
            finally
            {
                if (clearCache)
                {
                    _dynamicPreviewFrameStores.Clear();
                    _retainedMemoryPreview = null;
                    _retainedMemoryPreviewKey = null;
                }
                _dynamicPreviewControlGate.Release();
            }
        }
    }

    internal sealed class DynamicPreviewFrameStore(IRenderClient client, long maxPoolBytes)
    {
        internal const int DefaultPoolMiB = 64;
        public IRenderClient Client { get; } = client;
        public long MaxPoolBytes { get; } = maxPoolBytes;
        public object Gate { get; } = new();
        public Dictionary<Guid, DynamicPreviewClipPool> Pools { get; } = new();
    }

    internal sealed class DynamicPreviewCache
    {
        private readonly string _projectRoot;
        private readonly DynamicPreviewIndex? _index;
        private readonly MemoryPreviewCache? _memory;
        private readonly Task _preloadTask;
        private int _indexErrorLogged;
        private DynamicPreviewClock _clock;
        private volatile bool _stopped;
        private readonly object _framesGate;
        private readonly DynamicPreviewFrameStore _retainedFrames;
        private readonly int _preloadWorkers;
        private readonly int _poolMaxFrames;
        private readonly int _canvasWidth, _canvasHeight, _projectWidth, _projectHeight;
        private readonly CancellationTokenSource _cancellation = new();
        private uint _currentFrame;

        public IRenderClient Client { get; }
        public DynamicPreviewSession Session { get; }
        public bool IsCompleted => _stopped || _clock.IsCompleted;
        public bool IsRunning => _clock.IsRunning;
        public bool IsBuffering => _clock.BufferingProgress.HasValue;
        public double? BufferingProgress => _clock.BufferingProgress;
        public double? BufferingFrameRate => _clock.BufferingFrameRate;

        public DynamicPreviewCache(IRenderClient client, string projectRoot, DynamicPreviewSession session,
            int canvasWidth, int canvasHeight, int projectWidth, int projectHeight, DynamicPreviewFrameStore retainedFrames, MemoryPreviewCache? memory)
        {
            _memory = memory;
            _retainedFrames = retainedFrames;
            _framesGate = retainedFrames.Gate;
            _canvasWidth = canvasWidth; _canvasHeight = canvasHeight;
            _projectWidth = projectWidth; _projectHeight = projectHeight;
            Client = client;
            Session = session;
            _projectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot)) + Path.DirectorySeparatorChar;
            if (memory is null) _index = new(ResolveProjectPath(session.CacheDirectory), readOnly: true);
            _clock = session.Clock;
            _currentFrame = session.StartFrame;
            _preloadWorkers = RenderSchedulingSettings.MaxParallelWorkers;
            _poolMaxFrames = new PreviewBufferWindow(retainedFrames.MaxPoolBytes, session.FrameRate).MaximumFrames;
            lock (_framesGate)
                foreach (var pool in retainedFrames.Pools.Values)
                {
                    pool.SetFrameCount(_poolMaxFrames, session.StartFrame);
                    pool.ResetWindow(session.StartFrame, (uint)Math.Min(uint.MaxValue, (double)session.StartFrame + _poolMaxFrames - 1));
                }
            LogDiagnostic($"Dynamic preview clip pools: limit={retainedFrames.MaxPoolBytes / (1024 * 1024)} MiB/clip, buffer frames={_poolMaxFrames}, disk cache={memory is null}.");
            _preloadTask = memory is null ? Task.Run(PreloadFramesAsync) : Task.CompletedTask;
        }

        public uint GetFrame()
        {
            _clock = Client.GetDynamicPreviewClock(Session.SessionId, Session.PlaybackId) ?? _clock;
            if (_clock.Error is { Length: > 0 } error) throw new InvalidOperationException(error);
            var now = Stopwatch.GetTimestamp();
            // Local processes share the monotonic clock; anchor it to the backend audio sample position.
            var elapsed = _clock.IsRunning && _clock.Frequency > 0
                ? Math.Max(0, (now - _clock.Timestamp) / (double)_clock.Frequency) : 0;
            var frame = (uint)Math.Clamp(Math.Floor(_clock.Frame + elapsed * Session.FrameRate), Session.StartFrame, uint.MaxValue);
            if (_memory is not null) frame = _memory.ClampFrame(frame);
            _memory?.Advance(frame);
            lock (_framesGate)
            {
                Volatile.Write(ref _currentFrame, frame);
                uint oldest = (uint)Math.Max(0, (double)frame - Math.Ceiling(Session.FrameRate * 2));
                foreach (var id in _retainedFrames.Pools.Where(p => p.Value.LastFrame < oldest).Select(p => p.Key).ToArray())
                    _retainedFrames.Pools.Remove(id);
            }
            return frame;
        }

        public bool TryGetClip(uint frame, Guid clipId, out PreviewFrameSource source, out bool positioned)
        {
            if (_memory is not null) return _memory.TryGet(frame, clipId, out source, out positioned);
            source = null!;
            positioned = false;
            Lazy<PreviewFrameSource>? prepared = null;
            lock (_framesGate)
            {
                if (_stopped) return false;
                if (_retainedFrames.Pools.TryGetValue(clipId, out var pool) && pool.Find(frame) is { Source: { } sourceSlot } cached)
                {
                    prepared = sourceSlot;
                    positioned = cached.Entry!.IsPositionedClipPreview;
                }
            }
            if (prepared is not null)
            {
                source = prepared.Value;
                return true;
            }
            DynamicPreviewCachedClip? entry;
            try
            {
                lock (_framesGate)
                {
                    if (_stopped) return false;
                    entry = _index!.Read(frame, clipId);
                }
            }
            catch (SqliteException ex)
            {
                LogIndexError(ex);
                return false;
            }
            if (entry is null || entry.ClipId != clipId || entry.FrameIndex != frame) return false;
            prepared = PrepareClip(entry);
            if (prepared is null) return false;
            source = prepared.Value;
            positioned = entry.IsPositionedClipPreview;
            return true;
        }

        private Lazy<PreviewFrameSource>? PrepareClip(DynamicPreviewCachedClip entry, bool preload = false)
        {
            lock (_framesGate)
            {
                if (_stopped || entry.FrameIndex < _currentFrame) return null;
                if (preload && entry.FrameIndex >= (long)_currentFrame + _poolMaxFrames) return null;
                if (!_retainedFrames.Pools.TryGetValue(entry.ClipId, out var pool))
                {
                    pool = new(_retainedFrames.MaxPoolBytes, _poolMaxFrames);
                    _retainedFrames.Pools.Add(entry.ClipId, pool);
                }
                if (pool.Find(entry.FrameIndex) is { Source: { } saved } cached
                    && cached.Entry!.ProjectRelativePath == entry.ProjectRelativePath && cached.Entry.PreviewEffectHash == entry.PreviewEffectHash)
                    return saved;
                var slot = pool.Reserve(entry, _currentFrame, preload);
                if (slot is null) return null;
                var path = ResolveProjectPath(entry.ProjectRelativePath);
                long generation = slot.Generation;
                slot.Source = new(() =>
                {
                    lock (slot.Buffer.Gate)
                    {
                        if (_stopped || Volatile.Read(ref slot.Generation) != generation)
                            throw new OperationCanceledException(_cancellation.Token);
                        Volatile.Write(ref slot.Buffer.Version, generation);
                        return LoadFrame(entry, path, slot.Buffer);
                    }
                });
                return slot.Source;
            }
        }

        private PreviewFrameSource LoadFrame(DynamicPreviewCachedClip entry, string path, PreviewFrameBuffer buffer)
        {
            _cancellation.Token.ThrowIfCancellationRequested();
            try
            {
                return HdrPreviewView.PrepareFrame(new(path, entry.Width, entry.Height, Session.PixelFormat, false)
                    { PreviewContainer = entry.PreviewContainer, PreviewEffectHash = entry.PreviewEffectHash }, buffer);
            }
            catch (Exception ex) when (!_stopped && (ex is IOException or InvalidDataException))
            {
                Log(ex, $"Regenerate cached preview for clip {entry.ClipId} at frame {entry.FrameIndex}", this);
                var artifact = Client.RenderClipPreviewAsync(new()
                {
                    SessionId = Session.SessionId, ClipId = entry.ClipId, FrameIndex = entry.FrameIndex,
                    CanvasWidth = _canvasWidth, CanvasHeight = _canvasHeight, ProjectWidth = _projectWidth, ProjectHeight = _projectHeight,
                    PreferredPixelFormat = Session.PixelFormat,
                    DisableGpuBufferPoolLimit = !RenderSchedulingSettings.EnableGpuBufferPoolLimit,
                    GpuBufferPoolMaxBytes = RenderSchedulingSettings.GpuBufferPoolMaxBytes,
                    UsePreviewMetadata = entry.PreviewContainer is "vfd2m" or PreviewFrameContainer.ViewContainer,
                    DisableSourceFrameCache = entry.PreviewContainer == PreviewFrameContainer.ViewContainer,
                }, _cancellation.Token).AsTask().GetAwaiter().GetResult();
                if (entry.PreviewEffectHash.Length > 0 && artifact.PreviewEffectHash != entry.PreviewEffectHash)
                    throw new InvalidDataException("The playback preview no longer matches its timeline snapshot.");
                return HdrPreviewView.PrepareFrame(new(ResolveProjectPath(artifact.ProjectRelativePath), artifact.Width, artifact.Height,
                    Session.PixelFormat, false) { PreviewContainer = artifact.PreviewContainer, PreviewEffectHash = artifact.PreviewEffectHash }, buffer);
            }
        }

        private async Task PreloadFramesAsync()
        {
            try
            {
                while (!_stopped)
                {
                    var current = Volatile.Read(ref _currentFrame);
                    IReadOnlyList<DynamicPreviewCachedClip> entries;
                    try
                    {
                        lock (_framesGate)
                        {
                            if (_stopped) return;
                            var end = (uint)Math.Min(uint.MaxValue, (long)current + _poolMaxFrames - 1);
                            entries = _index!.ReadRange(current, end);
                        }
                    }
                    catch (SqliteException ex)
                    {
                        LogIndexError(ex);
                        await Task.Delay(50, _cancellation.Token).ConfigureAwait(false);
                        continue;
                    }
                    Parallel.ForEach(entries, new ParallelOptions { MaxDegreeOfParallelism = _preloadWorkers }, entry =>
                    {
                        if (_stopped) return;
                        lock (_framesGate)
                        {
                            if (_retainedFrames.Pools.TryGetValue(entry.ClipId, out var pool)
                                && pool.Find(entry.FrameIndex)?.Source is not null) return;
                        }
                        try { _ = PrepareClip(entry, preload: true)?.Value; }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { Log(ex, $"Preload preview for clip {entry.ClipId} at frame {entry.FrameIndex}", this); }
                    });
                    await Task.Delay(50, _cancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_stopped) { }
            catch (Exception ex)
            {
                if (!_stopped) Log(ex, "Preload dynamic preview frames", this);
            }
        }

        public void Cancel()
        {
            lock (_framesGate)
            {
                if (_stopped) return;
                _stopped = true;
            }
            _cancellation.Cancel();
            _memory?.Cancel();
        }

        public async Task StopAsync()
        {
            Cancel();
            await _preloadTask.ConfigureAwait(false);
            lock (_framesGate)
                foreach (var pool in _retainedFrames.Pools.Values) pool.RemoveUnprepared();
            _index?.Dispose();
            if (_memory is not null) await _memory.DisposeAsync().ConfigureAwait(false);
        }

        private string ResolveProjectPath(string relativePath)
            => ProjectCachePaths.ResolvePath(_projectRoot, relativePath);

        private void LogIndexError(SqliteException error)
        {
            if (!_stopped && Interlocked.Exchange(ref _indexErrorLogged, 1) == 0)
                Log(error, "Read dynamic preview cache index", this);
        }
    }

    internal sealed class DynamicPreviewClipPool(long maxBytes, int maxFrames)
    {
        // The store guards slot metadata; each buffer guards its pixel readers and writer.
        private readonly List<Slot> slots = [];
        private long frameBytes;
        public uint LastFrame { get; private set; }

        public Slot? Find(uint frame)
        {
            foreach (var slot in slots)
                if (slot.Entry?.FrameIndex == frame) return slot;
            return null;
        }

        public Slot? Reserve(DynamicPreviewCachedClip entry, uint currentFrame, bool preload)
        {
            frameBytes = Math.Max(frameBytes, PreviewBufferWindow.GetFrameBytes(entry.Width, entry.Height));
            int capacity = (int)Math.Clamp(maxBytes / frameBytes, 1, maxFrames);
            Trim(capacity, currentFrame);

            var slot = Find(entry.FrameIndex) ?? slots.FirstOrDefault(s => s.Entry is null)
                ?? slots.Where(s => s.Entry!.FrameIndex < currentFrame).MinBy(s => s.Entry!.FrameIndex);
            if (slot is null && slots.Count < capacity)
            {
                slot = new();
                slots.Add(slot);
            }
            if (slot is null)
            {
                var last = slots.MaxBy(s => s.Entry!.FrameIndex);
                if (!preload || last!.Entry!.FrameIndex > entry.FrameIndex) slot = last;
            }
            if (slot is null) return null;
            Invalidate(slot);
            slot.Entry = entry;
            LastFrame = Math.Max(LastFrame, entry.FrameIndex);
            return slot;
        }

        public void SetFrameCount(int count, uint currentFrame)
        {
            maxFrames = Math.Max(1, count);
            Trim(maxFrames, currentFrame);
        }

        private void Trim(int capacity, uint currentFrame)
        {
            while (slots.Count > capacity)
            {
                var old = slots.FirstOrDefault(s => s.Entry is null)
                    ?? slots.Where(s => s.Entry!.FrameIndex < currentFrame).MinBy(s => s.Entry!.FrameIndex)
                    ?? slots.MaxBy(s => s.Entry!.FrameIndex)!;
                Invalidate(old);
                slots.Remove(old);
            }
        }

        public void ResetWindow(uint start, uint end)
        {
            foreach (var slot in slots)
                if (slot.Entry is { } entry && (entry.FrameIndex < start || entry.FrameIndex > end)) Invalidate(slot);
            LastFrame = Math.Max(start, slots.Select(s => s.Entry?.FrameIndex ?? 0).DefaultIfEmpty().Max());
        }

        public void RemoveUnprepared()
        {
            foreach (var slot in slots)
                if (slot.Source?.IsValueCreated != true) Invalidate(slot);
        }

        private static void Invalidate(Slot slot)
        {
            Interlocked.Increment(ref slot.Generation);
            slot.Entry = null;
            slot.Source = null;
        }

        internal sealed class Slot
        {
            public DynamicPreviewCachedClip? Entry;
            public Lazy<PreviewFrameSource>? Source;
            public PreviewFrameBuffer Buffer { get; } = new();
            public long Generation;
        }
    }

    internal sealed class MemoryPreviewCache : IAsyncDisposable
    {
        public string Name { get; } = $"projectFrameCut-preview-{Guid.NewGuid():N}";
        public string Secret { get; } = Guid.NewGuid().ToString("N");
        private readonly NamedPipeServerStream pipe;
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task receiver;
        private readonly object gate = new();
        private readonly SortedDictionary<uint, List<(MemoryPreviewHeader Header, PreviewFrameSource Source)>> frames = new();
        private readonly PreviewBufferWindow window;
        private uint current;
        private uint complete;
        private Exception? error;
        private int disposed;

        public MemoryPreviewCache(long maxClipBytes, uint start, double frameRate, MemoryPreviewCache? previous = null)
        {
            window = new(maxClipBytes, frameRate);
            current = complete = start;
            if (previous is not null)
            {
                lock (previous.gate)
                {
                    foreach (var frame in previous.frames.Where(f => f.Key >= start))
                        frames.Add(frame.Key, frame.Value);
                    previous.frames.Clear();
                }
            }
            Trim(resetWindow: true);
            pipe = MemoryPreviewChannel.CreateServer(Name);
            receiver = Task.Run(ReceiveAsync);
        }

        public uint ClampFrame(uint frame)
        {
            lock (gate) return Math.Min(frame, complete);
        }

        public void Advance(uint frame)
        {
            lock (gate)
            {
                current = frame;
                Trim();
            }
        }

        private void Trim(bool resetWindow = false)
        {
            // A one-frame pool still needs the next frame in flight to hand playback over.
            long end = (long)current + Math.Max(2, window.FrameCount) - 1;
            foreach (var frame in frames.Keys.Where(f => f < current || resetWindow && f > end).ToArray())
                frames.Remove(frame);
        }

        public bool TryGet(uint frame, Guid clip, out PreviewFrameSource source, out bool positioned)
        {
            lock (gate)
            {
                if (error is not null) throw new IOException("Preview channel failed.", error);
                source = null!;
                positioned = false;
                if (!frames.TryGetValue(frame, out var entries)) return false;
                foreach (var entry in entries)
                    if (entry.Header.ClipId == clip)
                    {
                        source = entry.Source;
                        positioned = entry.Header.Positioned;
                        return true;
                    }
                return false;
            }
        }

        private async Task ReceiveAsync()
        {
            var token = cancellation.Token;
            try
            {
                await MemoryPreviewChannel.AuthenticateAsync(pipe, Secret, token).ConfigureAwait(false);
                await pipe.WriteAsync(new byte[] { 1 }, token).ConfigureAwait(false);
                while (true)
                {
                    var metadata = await RenderPipeFrame.ReadAsync(pipe, token).ConfigureAwait(false)
                        ?? throw new EndOfStreamException();
                    if (metadata[0] != (byte)'{')
                    {
                        var next = JsonSerializer.Deserialize<long>(metadata);
                        lock (gate)
                        {
                            complete = checked((uint)(next - 1));
                            Trim();
                        }
                        // Acknowledge the complete frame before waiting for room for more previews.
                        await pipe.WriteAsync(new byte[] { 1 }, token).ConfigureAwait(false);
                        while (true)
                        {
                            lock (gate)
                            {
                                Trim();
                                if (next < (long)current + Math.Max(2, window.FrameCount)) break;
                            }
                            await Task.Delay(10, token).ConfigureAwait(false);
                        }
                        await pipe.WriteAsync(new byte[] { 1 }, token).ConfigureAwait(false);
                        continue;
                    }
                    var (header, data) = await MemoryPreviewChannel.ReceiveAsync(pipe, token, metadata).ConfigureAwait(false);
                    var source = PreviewFrameMaterializer.FromData(
                        new("", data.Width, data.Height, data.ScRgb ? PreviewPixelFormat.Rgba16FloatScRgb : PreviewPixelFormat.EncodedImage, false), data);
                    lock (gate)
                    {
                        window.Observe(data.Width, data.Height, (long)data.Pixels.Length + data.AlphaMask.Length);
                        if (!frames.TryGetValue(header.Frame, out var entries)) frames.Add(header.Frame, entries = []);
                        var old = entries.FindIndex(e => e.Header.ClipId == header.ClipId);
                        if (old >= 0) entries.RemoveAt(old);
                        entries.Add((header, source));
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    lock (gate) error = ex;
                    pipe.Dispose();
                    Log(ex, "Receive memory preview", this);
                }
            }
        }

        public void Cancel()
        {
            lock (gate)
                if (disposed == 0) cancellation.Cancel();
        }

        public async ValueTask DisposeAsync()
        {
            lock (gate)
            {
                if (disposed != 0) return;
                disposed = 1;
                cancellation.Cancel();
            }
            pipe.Dispose();
            await receiver.ConfigureAwait(false);
            cancellation.Dispose();
        }
    }
}
