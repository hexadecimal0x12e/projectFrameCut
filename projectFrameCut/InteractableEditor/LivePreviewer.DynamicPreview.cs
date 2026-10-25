using projectFrameCut.Render.Contracts;
using projectFrameCut.Controls;
using projectFrameCut.Services;
using projectFrameCut.Render.Rendering;
using Microsoft.Data.Sqlite;
using System.Diagnostics;

namespace projectFrameCut.LivePreview;

public partial class LivePreviewer
{
    private DynamicPreviewCache? _dynamicPreviewCache;
    private readonly SemaphoreSlim _dynamicPreviewControlGate = new(1, 1);

    internal bool HasDynamicPreview => _dynamicPreviewCache is not null;
    internal double? DynamicPreviewBufferingFrameRate { get; private set; }

    internal async Task<DynamicPreviewCache> StartDynamicPreviewAsync(uint startFrame, int canvasWidth, int canvasHeight,
        int projectWidth, int projectHeight, double frameRate, CancellationToken token)
    {
        await _dynamicPreviewControlGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var client = RpcClient ?? RenderRpcBootstrap.Client;
            var sessionId = RenderSessionId;
            var playbackId = Guid.NewGuid();
            var mode = RenderHashSettings.Mode;
            try
            {
                DynamicPreviewBufferingFrameRate = null;
                OnProgressChanged?.Invoke(0, TimeSpan.Zero);
                var startup = client.StartDynamicPreviewAsync(new()
                {
                    FrameHashMode = mode, SessionId = sessionId, PlaybackId = playbackId, StartFrame = startFrame, CanvasWidth = canvasWidth, CanvasHeight = canvasHeight,
                    ProjectWidth = projectWidth, ProjectHeight = projectHeight, FrameRate = frameRate, StartAudio = HasAudioSources(),
                    MaxParallelWorkers = RenderSchedulingSettings.MaxParallelWorkers,
                    AllowWorkerOversubscription = RenderSchedulingSettings.AllowWorkerOversubscription,
                    RenderAheadFrames = RenderSchedulingSettings.RenderAheadFrames,
                    EnableThreadAffinity = RenderSchedulingSettings.EnableThreadAffinity,
                    WorkerCPUCoreIndexs = RenderSchedulingSettings.WorkerCPUCoreIndexs,
                    EnableMemoryBudget = RenderSchedulingSettings.EnableMemoryBudget,
                    MaxBufferedBytes = RenderSchedulingSettings.MaxBufferedBytes,
                    UsePreviewMetadata = true,
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
                _dynamicPreviewCache = new DynamicPreviewCache(client, ProjectRoot, session, mode,
                    canvasWidth, canvasHeight, projectWidth, projectHeight);
                token.ThrowIfCancellationRequested();
                OnProgressChanged?.Invoke(1, TimeSpan.Zero);
                return _dynamicPreviewCache;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (_dynamicPreviewCache is not null) await _dynamicPreviewCache.StopAsync().ConfigureAwait(false);
                _dynamicPreviewCache = null;
                try { await client.StopDynamicPreviewAsync(new() { SessionId = sessionId, PlaybackId = playbackId }).ConfigureAwait(false); }
                catch (Exception ex) { Log(ex, "Stop canceled dynamic preview startup", this); }
                throw;
            }
        }
        finally { _dynamicPreviewControlGate.Release(); }
    }

    internal async Task StopDynamicPreviewAsync(DynamicPreviewCache? cache = null)
    {
        await _dynamicPreviewControlGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (cache is not null && !ReferenceEquals(_dynamicPreviewCache, cache)) return;
            cache = _dynamicPreviewCache;
            _dynamicPreviewCache = null;
            if (cache is null) return;
            await cache.StopAsync().ConfigureAwait(false);
            await cache.Client.StopDynamicPreviewAsync(new()
            {
                SessionId = cache.Session.SessionId, PlaybackId = cache.Session.PlaybackId,
            }).ConfigureAwait(false);
        }
        finally { _dynamicPreviewControlGate.Release(); }
    }
}

internal sealed class DynamicPreviewCache
{
    private readonly string _projectRoot;
    private readonly DynamicPreviewIndex _index;
    private readonly Task _preloadTask;
    private int _indexErrorLogged;
    private DynamicPreviewClock _clock;
    private volatile bool _stopped;
    private readonly object _framesGate = new();
    private readonly Dictionary<(uint Frame, Guid Clip), (Lazy<PreviewFrameSource> Source, bool Positioned, long Bytes)> _frames = new();
    private readonly long _maxPreparedBytes = 256L * 1024 * 1024;
    private readonly int _preloadWorkers;
    private readonly int _canvasWidth, _canvasHeight, _projectWidth, _projectHeight;
    private readonly CancellationTokenSource _cancellation = new();
    private long _preparedBytes;
    private uint _currentFrame;

    public IRenderClient Client { get; }
    public DynamicPreviewSession Session { get; }
    public FrameHashMode Mode { get; }
    public bool IsCompleted => _stopped || _clock.IsCompleted;
    public bool IsRunning => _clock.IsRunning;
    public bool IsBuffering => _clock.BufferingProgress.HasValue;
    public double? BufferingProgress => _clock.BufferingProgress;
    public double? BufferingFrameRate => _clock.BufferingFrameRate;

    public DynamicPreviewCache(IRenderClient client, string projectRoot, DynamicPreviewSession session, FrameHashMode mode,
        int canvasWidth, int canvasHeight, int projectWidth, int projectHeight)
    {
        _canvasWidth = canvasWidth; _canvasHeight = canvasHeight;
        _projectWidth = projectWidth; _projectHeight = projectHeight;
        Mode = mode;
        Client = client;
        Session = session;
        _projectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot)) + Path.DirectorySeparatorChar;
        _index = new(ResolveProjectPath(session.CacheDirectory), readOnly: true);
        _clock = session.Clock;
        _currentFrame = session.StartFrame;
        _preloadWorkers = RenderSchedulingSettings.MaxParallelWorkers;
        var budget = RenderSchedulingSettings.MaxBufferedBytes;
        if (RenderSchedulingSettings.EnableMemoryBudget && budget > 0)
            _maxPreparedBytes = Math.Min(_maxPreparedBytes, budget);
        _preloadTask = Task.Run(PreloadFramesAsync);
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
        lock (_framesGate)
        {
            _currentFrame = frame;
            foreach (var key in _frames.Keys.Where(k => k.Frame < frame).ToArray())
            {
                _preparedBytes -= _frames[key].Bytes;
                _frames.Remove(key);
            }
        }
        return frame;
    }

    public bool TryGetClip(uint frame, Guid clipId, out PreviewFrameSource source, out bool positioned)
    {
        source = null!;
        positioned = false;
        Lazy<PreviewFrameSource>? prepared = null;
        lock (_framesGate)
        {
            if (_stopped) return false;
            if (_frames.TryGetValue((frame, clipId), out var cached))
            {
                prepared = cached.Source;
                positioned = cached.Positioned;
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
                entry = _index.Read(frame, clipId);
            }
        }
        catch (SqliteException ex)
        {
            LogIndexError(ex);
            return false;
        }
        if (entry is null || entry.ClipId != clipId || entry.FrameIndex != frame) return false;
        prepared = PrepareClip(entry, preload: false);
        if (prepared is null) return false;
        source = prepared.Value;
        positioned = entry.IsPositionedClipPreview;
        return true;
    }

    private Lazy<PreviewFrameSource>? PrepareClip(DynamicPreviewCachedClip entry, bool preload)
    {
        var path = ResolveProjectPath(entry.ProjectRelativePath);
        if (!File.Exists(path)) return null;
        lock (_framesGate)
        {
            if (_stopped || entry.FrameIndex < _currentFrame) return null;
            var key = (entry.FrameIndex, entry.ClipId);
            if (_frames.TryGetValue(key, out var cached)) return cached.Source;
            // Reserve decoded pixel storage before loading; the current frame must still be displayable.
            var bytes = checked((long)Math.Max(1, entry.Width) * Math.Max(1, entry.Height) * 8);
            if (preload && bytes > _maxPreparedBytes - _preparedBytes) return null;
            var source = new Lazy<PreviewFrameSource>(() => LoadFrame(entry, path));
            _frames.Add(key, (source, entry.IsPositionedClipPreview, bytes));
            _preparedBytes += bytes;
            return source;
        }
    }

    private PreviewFrameSource LoadFrame(DynamicPreviewCachedClip entry, string path)
    {
        _cancellation.Token.ThrowIfCancellationRequested();
        try
        {
            return HdrPreviewView.PrepareFrame(new(path, entry.Width, entry.Height, Session.PixelFormat, false)
                { PreviewContainer = entry.PreviewContainer, PreviewEffectHash = entry.PreviewEffectHash });
        }
        catch (Exception ex) when (!_stopped && (ex is IOException or InvalidDataException))
        {
            Log(ex, $"Regenerate cached preview for clip {entry.ClipId} at frame {entry.FrameIndex}", this);
            var artifact = Client.RenderClipPreviewAsync(new()
            {
                SessionId = Session.SessionId, ClipId = entry.ClipId, FrameIndex = entry.FrameIndex,
                CanvasWidth = _canvasWidth, CanvasHeight = _canvasHeight, ProjectWidth = _projectWidth, ProjectHeight = _projectHeight,
                FrameHashMode = Mode, PreferredPixelFormat = Session.PixelFormat,
                UsePreviewMetadata = entry.PreviewContainer == "vfd2m",
            }, _cancellation.Token).AsTask().GetAwaiter().GetResult();
            if (entry.PreviewEffectHash.Length > 0 && artifact.PreviewEffectHash != entry.PreviewEffectHash)
                throw new InvalidDataException("The playback preview no longer matches its timeline snapshot.");
            return HdrPreviewView.PrepareFrame(new(ResolveProjectPath(artifact.ProjectRelativePath), artifact.Width, artifact.Height,
                Session.PixelFormat, false) { PreviewContainer = artifact.PreviewContainer, PreviewEffectHash = artifact.PreviewEffectHash });
        }
    }

    private async Task PreloadFramesAsync()
    {
        try
        {
            while (!_stopped)
            {
                var current = Volatile.Read(ref _currentFrame);
                var end = (uint)Math.Min(uint.MaxValue, (double)current + Math.Ceiling(Session.FrameRate * 2));
                IReadOnlyList<DynamicPreviewCachedClip> entries;
                try
                {
                    lock (_framesGate)
                    {
                        if (_stopped) return;
                        entries = _index.ReadRange(current, end);
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
                        if (_preparedBytes >= _maxPreparedBytes || _frames.ContainsKey((entry.FrameIndex, entry.ClipId))) return;
                    }
                    try { _ = PrepareClip(entry, preload: true)?.Value; }
                    catch (OperationCanceledException) when (_stopped) { }
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

    public async Task StopAsync()
    {
        lock (_framesGate)
        {
            _stopped = true;
            _frames.Clear();
            _preparedBytes = 0;
        }
        _cancellation.Cancel();
        await _preloadTask.ConfigureAwait(false);
        _index.Dispose();
    }

    private string ResolveProjectPath(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(_projectRoot, relativePath));
        if (Path.IsPathRooted(relativePath) || !path.StartsWith(_projectRoot,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Dynamic preview cache path escapes the project root.");
        return path;
    }

    private void LogIndexError(SqliteException error)
    {
        if (!_stopped && Interlocked.Exchange(ref _indexErrorLogged, 1) == 0)
            Log(error, "Read dynamic preview cache index", this);
    }
}
