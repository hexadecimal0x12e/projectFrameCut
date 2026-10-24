using projectFrameCut.Render.Contracts;
using projectFrameCut.Controls;
using projectFrameCut.Services;
using System.Diagnostics;
using System.Text.Json;

namespace projectFrameCut.LivePreview;

public partial class LivePreviewer
{
    private DynamicPreviewCache? _dynamicPreviewCache;
    private readonly SemaphoreSlim _dynamicPreviewControlGate = new(1, 1);

    internal bool HasDynamicPreview => _dynamicPreviewCache is not null;

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
                    PreferredPixelFormat = OperatingSystem.IsWindows() && DefaultOutputMode != NativePreviewOutputMode.Disabled
                        ? PreviewPixelFormat.Rgba16FloatScRgb : PreviewPixelFormat.EncodedImage,
                }, token).AsTask();
                double lastProgress = 0;
                while (!startup.IsCompleted && !token.IsCancellationRequested)
                {
                    if (client.GetDynamicPreviewClock(sessionId, playbackId)?.BufferingProgress is double progress && progress > lastProgress)
                    {
                        lastProgress = progress;
                        OnProgressChanged?.Invoke(progress, TimeSpan.Zero);
                    }
                    await Task.WhenAny(startup, Task.Delay(200, token)).ConfigureAwait(false);
                }
                var session = await startup.ConfigureAwait(false);
                _dynamicPreviewCache = new DynamicPreviewCache(client, ProjectRoot, session, mode);
                token.ThrowIfCancellationRequested();
                OnProgressChanged?.Invoke(1, TimeSpan.Zero);
                return _dynamicPreviewCache;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _dynamicPreviewCache?.Stop();
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
            cache.Stop();
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
    private readonly string _cacheRoot;
    private DynamicPreviewClock _clock;
    private volatile bool _stopped;
    private readonly object _framesGate = new();
    private readonly Dictionary<(uint Frame, Guid Clip), (Lazy<PreviewFrameSource> Source, bool Positioned, long Bytes)> _frames = new();
    private readonly long _maxPreparedBytes = 256L * 1024 * 1024;
    private readonly int _preloadWorkers;
    private long _preparedBytes;
    private uint _currentFrame;

    public IRenderClient Client { get; }
    public DynamicPreviewSession Session { get; }
    public FrameHashMode Mode { get; }
    public bool IsCompleted => _stopped || _clock.IsCompleted;
    public bool IsRunning => _clock.IsRunning;
    public bool IsBuffering => _clock.BufferingProgress.HasValue;
    public double? BufferingProgress => _clock.BufferingProgress;

    public DynamicPreviewCache(IRenderClient client, string projectRoot, DynamicPreviewSession session, FrameHashMode mode)
    {
        Mode = mode;
        Client = client;
        Session = session;
        _projectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot)) + Path.DirectorySeparatorChar;
        _cacheRoot = ResolveProjectPath(session.CacheDirectory);
        _clock = session.Clock;
        _currentFrame = session.StartFrame;
        _preloadWorkers = RenderSchedulingSettings.MaxParallelWorkers;
        var budget = RenderSchedulingSettings.MaxBufferedBytes;
        if (RenderSchedulingSettings.EnableMemoryBudget && budget > 0)
            _maxPreparedBytes = Math.Min(_maxPreparedBytes, budget);
        _ = Task.Run(PreloadFramesAsync);
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
        var entry = ReadJson<DynamicPreviewCachedClip>(Path.Combine(_cacheRoot, $"{frame:D10}_{clipId:N}.json"));
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
            var source = new Lazy<PreviewFrameSource>(() => HdrPreviewView.PrepareFrame(
                new(path, entry.Width, entry.Height, Session.PixelFormat, false)));
            _frames.Add(key, (source, entry.IsPositionedClipPreview, bytes));
            _preparedBytes += bytes;
            return source;
        }
    }

    private async Task PreloadFramesAsync()
    {
        try
        {
            while (!_stopped)
            {
                var current = Volatile.Read(ref _currentFrame);
                var end = Math.Min(uint.MaxValue, (double)current + Math.Ceiling(Session.FrameRate * 2));
                Parallel.ForEach(Directory.EnumerateFiles(_cacheRoot, "*.json").OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal),
                    new ParallelOptions { MaxDegreeOfParallelism = _preloadWorkers }, file =>
                {
                    if (_stopped) return;
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (name.Length != 43 || name[10] != '_' || !uint.TryParse(name.AsSpan(0, 10), out var frame)
                        || !Guid.TryParseExact(name.AsSpan(11), "N", out var id) || frame < current || frame > end)
                        return;
                    lock (_framesGate)
                    {
                        if (_preparedBytes >= _maxPreparedBytes || _frames.ContainsKey((frame, id))) return;
                    }
                    var entry = ReadJson<DynamicPreviewCachedClip>(file);
                    if (entry is null || entry.FrameIndex != frame || entry.ClipId != id) return;
                    try { _ = PrepareClip(entry, preload: true)?.Value; }
                    catch (Exception ex) { Log(ex, $"Preload preview for clip {entry.ClipId} at frame {frame}", this); }
                });
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            if (!_stopped) Log(ex, "Preload dynamic preview frames", this);
        }
    }

    public void Stop()
    {
        lock (_framesGate)
        {
            _stopped = true;
            _frames.Clear();
            _preparedBytes = 0;
        }
    }

    private string ResolveProjectPath(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(_projectRoot, relativePath));
        if (Path.IsPathRooted(relativePath) || !path.StartsWith(_projectRoot,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Dynamic preview cache path escapes the project root.");
        return path;
    }

    private static T? ReadJson<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(stream);
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }
}
