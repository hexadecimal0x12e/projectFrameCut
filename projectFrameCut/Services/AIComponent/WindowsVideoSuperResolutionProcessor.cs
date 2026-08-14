#if WINDOWS
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Base.ReadWriteConvert;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using IPicture = projectFrameCut.Drawing.Base.IPicture;
using PictureExtensions = projectFrameCut.Drawing.Base.PictureExtensions;

namespace projectFrameCut.Services.AIComponent;

internal sealed class WindowsVideoSuperResolutionProcessor : ISourceReplacementEffect, IIntegratedAIComponent
{
    private const int CacheFormatVersion = 1;
    private const int CacheLockCount = 16;
    private static readonly int[] CacheBitDepths = [8, 16];

    private readonly IAIComponentClient _client;
    private readonly Func<Dictionary<string, object>, IEffect> _effectFactory;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SemaphoreSlim[] _cacheLocks = Enumerable.Range(0, CacheLockCount)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();
    private DateTimeOffset _retryAfter;
    private long _requestCount;

    public WindowsVideoSuperResolutionProcessor(IAIComponentClient client)
    {
        _client = client;
        _effectFactory = CreateEffect;
        Log("[SystemAI/VSR] Video Super Resolution processor initialized.");
    }

    private WindowsVideoSuperResolutionProcessor(WindowsVideoSuperResolutionProcessor source, Dictionary<string, object> parameters)
    {
        _client = source._client;
        _connectionLock = source._connectionLock;
        _cacheLocks = source._cacheLocks;
        _effectFactory = CreateEffect;
        ProjectFrameRate = source.ProjectFrameRate;
        Enabled = source.Enabled;
        Name = source.Name;
        Id = source.Id;
        Parameters = parameters;
        BindedEffectProvidingSystemID = source.BindedEffectProvidingSystemID;
    }

    string IIntegratedAIComponent.Id => "windows.video.super_resolution";

    public bool IsAvailable => _client.IsSupported && DateTimeOffset.UtcNow >= _retryAfter;

    public bool IsSupported => _client.IsSupported;

    public int ProjectFrameRate { get; set; } = 30;
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "Windows Video Super Resolution";
    public string Id { get; set; } = string.Empty;
    public Dictionary<string, object> Parameters { get; private set; } = new();
    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public string TypeName => "WindowsVideoSuperResolution";
    public EffectImplementType ImplementType => EffectImplementType.IPicture;
    public string? NeedComputer => null;
    public string? BindedEffectProvidingSystemID { get; set; }

    public void Register()
    {
        var current = WindowsVideoSuperResolutionEffectProvider.EffectFactory;
        if (current is not null && !ReferenceEquals(current, _effectFactory))
        {
            throw new InvalidOperationException("A Windows Video Super Resolution effect factory is already registered.");
        }

        WindowsVideoSuperResolutionEffectProvider.EffectFactory = _effectFactory;
    }

    public void Unregister()
    {
        if (ReferenceEquals(WindowsVideoSuperResolutionEffectProvider.EffectFactory, _effectFactory))
        {
            WindowsVideoSuperResolutionEffectProvider.EffectFactory = null;
        }
    }

    public bool SupportsSourceReplacement(IClip input, int targetWidth, int targetHeight)
        => Enabled
            && input.ClipType == ClipMode.VideoClip
            && targetWidth > 0
            && targetHeight > 0
            && ProjectFrameRate is >= 15 and <= 60
            && IsAvailable;

    public IPicture Compute(
        IClip input,
        IComputer? computer,
        IPicture source,
        int targetWidth,
        int targetHeight,
        uint targetFrame,
        IPicture.PicturePixelMode targetPPB)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(source);

        if (!SupportsSourceReplacement(input, targetWidth, targetHeight))
            return source;

        try
        {
            var result = Process(source, targetWidth, targetHeight, ProjectFrameRate);
            if (result.Width != targetWidth || result.Height != targetHeight)
            {
                if (!ReferenceEquals(result, source)) result.Dispose();
                return source;
            }

            if (result.BitPerPixel != targetPPB)
            {
                var converted = result.ToBitPerPixel(targetPPB);
                if (!ReferenceEquals(converted, result)) result.Dispose();
                result = converted;
            }

            if (!ReferenceEquals(result, source)) source.Dispose();
            return result;
        }
        catch
        {
            return source;
        }
    }

    public IEffect WithParameters(Dictionary<string, object> parameters)
        => CreateEffect(parameters);

    private IEffect CreateEffect(Dictionary<string, object> parameters)
        => new WindowsVideoSuperResolutionProcessor(this, parameters ?? new Dictionary<string, object>());

    public IPicture Process(IPicture source, int targetWidth, int targetHeight, int framesPerSecond)
        => ProcessAsync(source, targetWidth, targetHeight, framesPerSecond).GetAwaiter().GetResult();

    private async Task<IPicture> ProcessAsync(IPicture source, int targetWidth, int targetHeight, int framesPerSecond)
    {
        long requestNumber = Interlocked.Increment(ref _requestCount);
        bool logSample = requestNumber <= 3 || requestNumber % 60 == 0;
        var started = System.Diagnostics.Stopwatch.StartNew();
        string? cacheKey = null;
        SemaphoreSlim? cacheLock = null;
        try
        {
            if (logSample)
            {
                Log($"[SystemAI/VSR] Request #{requestNumber}: {source.Width}x{source.Height} {source.BitPerPixel} -> {targetWidth}x{targetHeight}, {framesPerSecond} FPS.");
            }

            try
            {
                cacheKey = ComputeCacheKey(source, targetWidth, targetHeight, framesPerSecond);
                if (TryLoadCachedFrame(cacheKey, out var cached))
                {
                    if (logSample) Log($"[SystemAI/VSR] Request #{requestNumber} served from disk cache.");
                    return cached;
                }

                cacheLock = _cacheLocks[(cacheKey.GetHashCode(StringComparison.Ordinal) & int.MaxValue) % CacheLockCount];
                await cacheLock.WaitAsync().ConfigureAwait(false);
                if (TryLoadCachedFrame(cacheKey, out cached))
                {
                    if (logSample) Log($"[SystemAI/VSR] Request #{requestNumber} served from disk cache after waiting.");
                    return cached;
                }
            }
            catch (Exception ex)
            {
                cacheKey = null;
                cacheLock?.Release();
                cacheLock = null;
                Log(ex, $"prepare VSR disk cache for request #{requestNumber}", this);
            }

            await EnsureConnectedAsync().ConfigureAwait(false);
            if (!_client.Capabilities.Any(capability =>
                    string.Equals(capability.Operation, "video.super_resolution", StringComparison.OrdinalIgnoreCase)))
            {
                throw new PlatformNotSupportedException("The System AI extension does not provide Video Super Resolution.");
            }

            var result = await _client.ExecuteVideoSuperResolutionAsync(
                source,
                targetWidth,
                targetHeight,
                framesPerSecond).ConfigureAwait(false);

            if (cacheKey is not null)
            {
                try
                {
                    SaveCachedFrame(cacheKey, result);
                }
                catch (Exception ex)
                {
                    Log(ex, $"save VSR disk cache for request #{requestNumber}", this);
                }
            }

            started.Stop();
            if (logSample)
            {
                Log($"[SystemAI/VSR] Request #{requestNumber} completed in {started.Elapsed.TotalMilliseconds:F1} ms; result={result.Width}x{result.Height} {result.BitPerPixel}.");
            }
            return result;
        }
        catch (Exception ex)
        {
            _retryAfter = DateTimeOffset.UtcNow.AddSeconds(30);
            Log(ex, $"process VSR request #{requestNumber}; retry disabled until {_retryAfter:O}", this);
            throw;
        }
        finally
        {
            cacheLock?.Release();
        }
    }

    private static string CacheDirectory => Path.Combine(MauiProgram.DataPath, "RenderCache", "WindowsVideoSuperResolution");

    private static string ComputeCacheKey(IPicture source, int targetWidth, int targetHeight, int framesPerSecond)
    {
        int pixels = checked(source.Width * source.Height);
        Span<byte> metadata = stackalloc byte[28];
        BinaryPrimitives.WriteInt32LittleEndian(metadata[0..4], CacheFormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(metadata[4..8], source.Width);
        BinaryPrimitives.WriteInt32LittleEndian(metadata[8..12], source.Height);
        BinaryPrimitives.WriteInt32LittleEndian(metadata[12..16], (int)source.BitPerPixel);
        BinaryPrimitives.WriteInt32LittleEndian(metadata[16..20], source.HasAlphaChannel ? 1 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(metadata[20..24], targetWidth);
        BinaryPrimitives.WriteInt32LittleEndian(metadata[24..28], targetHeight);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(metadata);
        Span<byte> fps = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(fps, framesPerSecond);
        hash.AppendData(fps);

        if (source is IPicture<byte> picture8)
        {
            hash.AppendData(picture8.r.AsSpan(0, pixels));
            hash.AppendData(picture8.g.AsSpan(0, pixels));
            hash.AppendData(picture8.b.AsSpan(0, pixels));
            if (source.HasAlphaChannel)
                hash.AppendData(MemoryMarshal.AsBytes(picture8.a!.AsSpan(0, pixels)));
        }
        else if (source is IPicture<ushort> picture16)
        {
            hash.AppendData(MemoryMarshal.AsBytes(picture16.r.AsSpan(0, pixels)));
            hash.AppendData(MemoryMarshal.AsBytes(picture16.g.AsSpan(0, pixels)));
            hash.AppendData(MemoryMarshal.AsBytes(picture16.b.AsSpan(0, pixels)));
            if (source.HasAlphaChannel)
                hash.AppendData(MemoryMarshal.AsBytes(picture16.a!.AsSpan(0, pixels)));
        }
        else
        {
            throw new NotSupportedException($"Unsupported IPicture implementation: {source.GetType().FullName}.");
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static bool TryLoadCachedFrame(string cacheKey, out IPicture picture)
    {
        picture = null!;
        string cacheDirectory = CacheDirectory;
        foreach (int bitDepth in CacheBitDepths)
        {
            string path = Path.Combine(cacheDirectory, $"{cacheKey}.{bitDepth}.vfc");
            if (!File.Exists(path)) continue;

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
                bool loaded;
                if (bitDepth == 8)
                {
                    loaded = PictureExtensions.SharedVfdPictureDecoder.TryLoad(stream, out Picture8bpp? cached);
                    picture = cached!;
                }
                else
                {
                    loaded = PictureExtensions.SharedVfdPictureDecoder.TryLoad(stream, out Picture16bpp? cached);
                    picture = cached!;
                }

                if (loaded && picture is not null)
                {
                    try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { }
                    return true;
                }

                picture?.Dispose();
                picture = null!;
            }
            catch
            {
                picture?.Dispose();
                picture = null!;
            }

            try { File.Delete(path); } catch { }
        }

        return false;
    }

    private static void SaveCachedFrame(string cacheKey, IPicture picture)
    {
        Directory.CreateDirectory(CacheDirectory);
        int bitDepth = (int)picture.BitPerPixel;
        if (bitDepth is not (8 or 16))
            throw new NotSupportedException($"Unsupported VSR cache bit depth: {bitDepth}.");

        string path = Path.Combine(CacheDirectory, $"{cacheKey}.{bitDepth}.vfc");
        if (File.Exists(path)) return;

        string temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan))
            {
                if (picture is IPicture<byte> picture8)
                    picture8.Save(stream, PictureExtensions.SharedVfdPictureEncoder);
                else if (picture is IPicture<ushort> picture16)
                    picture16.Save(stream, PictureExtensions.SharedVfdPictureEncoder);
                else
                    throw new NotSupportedException($"Unsupported IPicture implementation: {picture.GetType().FullName}.");
            }

            File.Move(temporaryPath, path, true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private async Task EnsureConnectedAsync()
    {
        if (_client.IsConnected) return;

        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_client.IsConnected)
            {
                Log("[SystemAI/VSR] System AI extension is disconnected; starting connection.");
                var capabilities = await _client.ConnectAsync().ConfigureAwait(false);
                Log($"[SystemAI/VSR] Connected. Capabilities: {string.Join(", ", capabilities.Select(c => c.Operation))}.");
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }
}
#endif
