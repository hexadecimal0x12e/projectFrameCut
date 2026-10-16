using projectFrameCut.Render.Contracts;
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
            try
            {
                OnProgressChanged?.Invoke(0, TimeSpan.Zero);
                var startup = client.StartDynamicPreviewAsync(new()
                {
                    SessionId = sessionId, PlaybackId = playbackId, StartFrame = startFrame, CanvasWidth = canvasWidth, CanvasHeight = canvasHeight,
                    ProjectWidth = projectWidth, ProjectHeight = projectHeight, FrameRate = frameRate, StartAudio = HasAudioSources(),
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
                _dynamicPreviewCache = new DynamicPreviewCache(client, ProjectRoot, session);
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

    public IRenderClient Client { get; }
    public DynamicPreviewSession Session { get; }
    public bool IsCompleted => _stopped || _clock.IsCompleted;
    public bool IsRunning => _clock.IsRunning;
    public bool IsBuffering => _clock.BufferingProgress.HasValue;

    public DynamicPreviewCache(IRenderClient client, string projectRoot, DynamicPreviewSession session)
    {
        Client = client;
        Session = session;
        _projectRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot)) + Path.DirectorySeparatorChar;
        _cacheRoot = ResolveProjectPath(session.CacheDirectory);
        _clock = session.Clock;
    }

    public uint GetFrame()
    {
        _clock = Client.GetDynamicPreviewClock(Session.SessionId, Session.PlaybackId) ?? _clock;
        if (_clock.Error is { Length: > 0 } error) throw new InvalidOperationException(error);
        var now = Stopwatch.GetTimestamp();
        // Local processes share the monotonic clock; anchor it to the backend audio sample position.
        var elapsed = _clock.IsRunning && _clock.Frequency > 0
            ? Math.Max(0, (now - _clock.Timestamp) / (double)_clock.Frequency) : 0;
        return (uint)Math.Clamp(Math.Floor(_clock.Frame + elapsed * Session.FrameRate), Session.StartFrame, uint.MaxValue);
    }

    public bool TryGetClip(uint frame, Guid clipId, out PreviewFrameSource source, out bool positioned)
    {
        source = null!;
        positioned = false;
        var entry = ReadJson<DynamicPreviewCachedClip>(Path.Combine(_cacheRoot, $"{frame:D10}_{clipId:N}.json"));
        if (entry is null || entry.ClipId != clipId || entry.FrameIndex != frame) return false;
        var path = ResolveProjectPath(entry.ProjectRelativePath);
        if (!File.Exists(path)) return false;
        source = new(path, entry.Width, entry.Height, Session.PixelFormat, false);
        positioned = entry.IsPositionedClipPreview;
        return true;
    }

    public void Stop() => _stopped = true;

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
