using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Plugin;
using System.Text.Json.Serialization;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Render.RenderAPIBase.Context;
using FFmpeg.AutoGen;

namespace projectFrameCut.Render.ClipsAndTracks
{
    public class VideoClip : IClip
    {
        private const int MaxDecoderPoolSize = 128;

        private static readonly object DecoderPoolRegistryLock = new();
        private static readonly Dictionary<string, VideoDecoderPool> DecoderPools = new(StringComparer.OrdinalIgnoreCase);

        private VideoDecoderPool? _decoderPool;
        private string? _decoderPoolKey;

        public required Guid Id { get; init; }
        public required string Name { get; init; }
        public uint LayerIndex { get; init; } = 0;
        public uint SubLayerIndex { get; init; }
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; set; }
        public float FrameTime { get; init; }
        public float SecondPerFrameRatio { get => 1; init { } }

        public string? FilePath { get; set; }

        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public EffectProviderJSONStructure[]? EffectProviders { get; init; }
        public IEffect[]? EffectsInstances { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public IEffectProvider[]? EffectProvidersInstances { get; set; }
        public Dictionary<string, object> ExtraData { get; set; }
        public bool ExtendToWholeDraft { get; set; }

        public bool NeedFilePath => true;

        [System.Text.Json.Serialization.JsonIgnore]
        public IVideoSource? Decoder { get; set; } = null;

        public ClipMode ClipType => ClipMode.VideoClip;
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;

        public string BindedSoundTrack { get; init; } = "";
        public int TargetWidth { get; set; }
        public int TargetHeight { get; set; }
        public int TargetX { get; set; }
        public int TargetY { get; set; }
        public int StartingX { get; set; }
        public int StartingY { get; set; }
        public ISpeedVarianceProvider? SpeedVarianceProviderInstance { get; set; }
        public IMixture? MixtureInstance { get; set; }

        public string TargetDecoder { get; set; } = string.Empty;
        public double HDRBrightnessOffset { get; set; } = 0;

        [JsonIgnore()]
        public string? DecoderName => Decoder?.TypeName;

        public ISourceReplacementEffect? AlternativeSource { get; set; }

        public VideoClip()
        {
           EffectHelper.ResolveClipEffects(this);
        }

        public IPicture GetFrameRelativeToStartPointOfSource(uint targetFrame, int targetWidth, int targetHeight, IPicture.PicturePixelMode targetPPB)
        {
            if (_decoderPool is null)
            {
                if (!File.Exists(FilePath))
                {
                    ClipInitializationFailure.Mark(this, "SourceNotFound", new FileNotFoundException($"VideoClip {Name}'s source is not available: {FilePath}.", FilePath));
                    return ClipInitializationFailure.CreateFallbackFrame(targetWidth, targetHeight, targetPPB, "SourceNotFound", ClipInitializationFailure.GetDescription(ExtraData));
                }
                else
                {
                    ClipInitializationFailure.Mark(this, "SourceReading", new InvalidOperationException($"VideoClip {Name}'s decoder is not initialized, maybe because the video file is corrupted or the decoder is not available."));
                    return ClipInitializationFailure.CreateFallbackFrame(targetWidth, targetHeight, targetPPB, "SourceReading", ClipInitializationFailure.GetDescription(ExtraData));
                }
            }

            using var decoderLease = _decoderPool.Rent(targetFrame);
            var decoder = decoderLease.Decoder;
            targetFrame = ClampFrameToDecoderRange(decoder, targetFrame);
            int sourceX = Math.Clamp(StartingX, 0, Math.Max(0, decoder.Width - 1));
            int sourceY = Math.Clamp(StartingY, 0, Math.Max(0, decoder.Height - 1));
            // targetWidth/targetHeight describe the requested output resolution. They must not
            // limit the source region, otherwise a low-resolution preview decodes only the
            // top-left corner of the video instead of scaling the complete source frame.
            // StartingX/StartingY intentionally crop the leading source area; the remaining
            // source rectangle is then scaled to the requested output dimensions by the decoder.
            int sourceWidth = Math.Max(1, decoder.Width - sourceX);
            int sourceHeight = Math.Max(1, decoder.Height - sourceY);

            IPicture result;
            if (decoder is HDRDecoderContext h)
            {
                result = h.GetHDRFrame(targetFrame, sourceX, sourceY, sourceWidth, sourceHeight,
                        targetWidth, targetHeight, hasAlpha: true)
                    .SetBrightnessOffset(HDRBrightnessOffset)
                    .ToBitPerPixel(targetPPB);
            }
            else
            {
                result = decoder.GetFrame(targetFrame, sourceX, sourceY, sourceWidth, sourceHeight,
                    targetWidth, targetHeight).ToBitPerPixel(targetPPB);
            }

            // Only publish the position after a successful decode. Decoder.Index is a request
            // counter, not a source-frame position, and must never be used for pool routing.
            decoderLease.MarkPosition(targetFrame);
            return result;
        }

        private uint ClampFrameToDecoderRange(IVideoSource? decoder, uint targetFrame)
        {
            if (decoder is null)
            {
                return targetFrame;
            }

            long totalFrames = decoder.TotalFrames;
            if (totalFrames <= 0)
            {
                return targetFrame;
            }

            uint maxFrame = (uint)Math.Max(0, totalFrames - 1);
            return targetFrame > maxFrame ? maxFrame : targetFrame;
        }

        void IClip.ReInit(IPicture.PicturePixelMode targetPPB)
        {
            if (string.IsNullOrWhiteSpace(FilePath)) throw new NullReferenceException($"VideoClip {Id}'s source path is null.");
            if (!File.Exists(FilePath))
            {
                ClipInitializationFailure.Mark(this, "SourceNotFound", new FileNotFoundException($"VideoClip {Name}'s source is not available: {FilePath}.", FilePath));
                return;
            }

            try
            {
               EffectHelper.ResolveClipEffects(this);
            }
            catch (Exception ex)
            {
                ClipInitializationFailure.Mark(this, "ResolveEffect", ex);
            }

            try
            {
                var poolKey = BuildDecoderPoolKey(FilePath, TargetDecoder);
                if (_decoderPool is not null && string.Equals(_decoderPoolKey, poolKey, StringComparison.OrdinalIgnoreCase) && !_decoderPool.Disposed)
                {
                    Decoder = _decoderPool.RepresentativeDecoder;
                   EffectHelper.ResolveClipEffects(this);
                    return;
                }

                ReleaseDecoderPool();

                var pool = AcquireDecoderPool(poolKey, CreateDecoderInstance);
                _decoderPoolKey = poolKey;
                _decoderPool = pool;
                Decoder = pool.RepresentativeDecoder;
            }
            catch (Exception ex)
            {
                ClipInitializationFailure.Mark(this, "SourceReading", ex);
            }

        }


        void IDisposable.Dispose()
        {
            ReleaseDecoderPool();
        }

        private IVideoSource CreateDecoderInstance()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(FilePath))
                {
                    throw new NullReferenceException($"VideoClip {Id}'s source path is null.");
                }

                if (!string.IsNullOrWhiteSpace(TargetDecoder) && TargetDecoder != "auto")
                {
                    var supportedPlugin = PluginManager.LoadedPlugins.Values.FirstOrDefault(p => p.VideoSourceProvider.ContainsKey(TargetDecoder)) ?? throw new NotSupportedException($"The specified video decoder '{TargetDecoder}' was not found for the file '{FilePath}'.");
                    return supportedPlugin.VideoSourceProvider[TargetDecoder](null!).CreateNew(FilePath);
                }

                return PluginManager.CreateVideoSource(FilePath);
            }
            catch (Exception ex)
            {
                ClipInitializationFailure.Mark(this, "SourceReading", ex);
                throw;
            }
        }

        private void ReleaseDecoderPool()
        {
            var pool = _decoderPool;
            var poolKey = _decoderPoolKey;
            _decoderPool = null;
            _decoderPoolKey = null;
            Decoder = null;

            if (pool is null || string.IsNullOrWhiteSpace(poolKey))
            {
                return;
            }

            lock (DecoderPoolRegistryLock)
            {
                if (!DecoderPools.TryGetValue(poolKey, out var existing) || !ReferenceEquals(existing, pool))
                {
                    return;
                }

                if (existing.ReleaseOwner())
                {
                    DecoderPools.Remove(poolKey);
                }
            }
        }

        private static VideoDecoderPool AcquireDecoderPool(string poolKey, Func<IVideoSource> decoderFactory)
        {
            lock (DecoderPoolRegistryLock)
            {
                if (!DecoderPools.TryGetValue(poolKey, out var pool))
                {
                    pool = new VideoDecoderPool(poolKey, decoderFactory);
                    DecoderPools[poolKey] = pool;
                }

                pool.AddOwner();
                return pool;
            }
        }

        private static string BuildDecoderPoolKey(string filePath, string targetDecoder)
        {
            var normalizedPath = Path.GetFullPath(filePath);
            var normalizedDecoder = string.IsNullOrWhiteSpace(targetDecoder) ? "auto" : targetDecoder.Trim();
            return $"{normalizedPath}::{normalizedDecoder}";
        }

        private sealed class VideoDecoderPool : IDisposable
        {
            private readonly string _poolKey;
            private readonly Func<IVideoSource> _decoderFactory;
            private readonly List<PooledDecoderEntry> _decoders = [];
            private readonly object _sync = new();
            private int _ownerCount;
            private bool _disposed;

            public VideoDecoderPool(string poolKey, Func<IVideoSource> decoderFactory)
            {
                _poolKey = poolKey;
                _decoderFactory = decoderFactory;

                var representative = CreateDecoder();
                _decoders.Add(new PooledDecoderEntry(representative));
            }

            public IVideoSource RepresentativeDecoder
            {
                get
                {
                    lock (_sync)
                    {
                        return _decoders[0].Decoder;
                    }
                }
            }

            public bool Disposed
            {
                get
                {
                    lock (_sync)
                    {
                        return _disposed;
                    }
                }
            }

            public void AddOwner()
            {
                lock (_sync)
                {
                    if (_disposed)
                    {
                        throw new ObjectDisposedException(nameof(VideoDecoderPool), $"Decoder pool '{_poolKey}' is already disposed.");
                    }

                    _ownerCount++;
                }
            }

            public bool ReleaseOwner()
            {
                lock (_sync)
                {
                    if (_ownerCount > 0)
                    {
                        _ownerCount--;
                    }

                    if (_ownerCount > 0)
                    {
                        return false;
                    }

                    _disposed = true;
                    foreach (var entry in _decoders)
                    {
                        if (!entry.InUse)
                        {
                            entry.Dispose();
                        }
                    }

                    return true;
                }
            }

            public DecoderLease Rent(uint targetFrame)
            {
                lock (_sync)
                {
                    if (_disposed)
                    {
                        throw new ObjectDisposedException(nameof(VideoDecoderPool), $"Decoder pool '{_poolKey}' is already disposed.");
                    }

                    var bestEntry = GetBestFreeEntry(targetFrame);
                    if (bestEntry is not null)
                    {
                        bestEntry.InUse = true;
                        return new DecoderLease(this, bestEntry);
                    }

                    if (_decoders.Count < MaxDecoderPoolSize)
                    {
                        var decoder = CreateDecoder();
                        var entry = new PooledDecoderEntry(decoder)
                        {
                            InUse = true
                        };
                        _decoders.Add(entry);
                        return new DecoderLease(this, entry);
                    }
                }

                return new DecoderLease(CreateDecoder());
            }

            private PooledDecoderEntry? GetBestFreeEntry(uint targetFrame)
            {
                PooledDecoderEntry? bestEntry = null;
                PooledDecoderEntry? unpositionedEntry = null;
                long bestDistance = long.MaxValue;

                foreach (var entry in _decoders)
                {
                    if (entry.InUse || entry.Decoder.Disposed)
                    {
                        continue;
                    }

                    if (!entry.HasSuccessfulPosition)
                    {
                        unpositionedEntry ??= entry;
                        continue;
                    }

                    long distance = Math.Abs((long)entry.LastSuccessfulFrame - targetFrame);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestEntry = entry;
                    }
                }

                return bestEntry ?? unpositionedEntry;
            }

            private IVideoSource CreateDecoder()
            {
                var decoder = _decoderFactory();
                decoder.EnableLock = true;
                return decoder;
            }

            internal void Return(PooledDecoderEntry entry)
            {
                lock (_sync)
                {
                    if (_disposed)
                    {
                        entry.InUse = false;
                        entry.Dispose();
                        return;
                    }

                    entry.InUse = false;
                }
            }

            internal void MarkPosition(PooledDecoderEntry entry, uint frameIndex)
            {
                lock (_sync)
                {
                    if (_disposed || !entry.InUse)
                        return;

                    entry.LastSuccessfulFrame = frameIndex;
                    entry.HasSuccessfulPosition = true;
                }
            }

            public void Dispose()
            {
                lock (_sync)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _disposed = true;
                    foreach (var entry in _decoders)
                    {
                        if (!entry.InUse)
                        {
                            entry.Dispose();
                        }
                    }
                }
            }

            internal sealed class PooledDecoderEntry
            {
                public PooledDecoderEntry(IVideoSource decoder)
                {
                    Decoder = decoder;
                }

                public IVideoSource Decoder { get; }
                public bool InUse { get; set; }
                public uint LastSuccessfulFrame { get; set; }
                public bool HasSuccessfulPosition { get; set; }

                public void Dispose()
                {
                    Decoder.Dispose();
                }
            }

            internal sealed class DecoderLease : IDisposable
            {
                private readonly VideoDecoderPool? _pool;
                private readonly PooledDecoderEntry? _entry;
                private readonly IVideoSource? _temporaryDecoder;
                private bool _disposed;

                internal DecoderLease(VideoDecoderPool pool, PooledDecoderEntry entry)
                {
                    _pool = pool;
                    _entry = entry;
                    Decoder = entry.Decoder;
                }

                internal DecoderLease(IVideoSource temporaryDecoder)
                {
                    _temporaryDecoder = temporaryDecoder;
                    Decoder = temporaryDecoder;
                }

                public IVideoSource Decoder { get; }

                public void MarkPosition(uint frameIndex)
                {
                    if (_entry is not null && _pool is not null)
                        _pool.MarkPosition(_entry, frameIndex);
                }

                public void Dispose()
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _disposed = true;

                    if (_entry is not null && _pool is not null)
                    {
                        _pool.Return(_entry);
                        return;
                    }

                    _temporaryDecoder?.Dispose();
                }
            }
        }
    }


    /// <summary>
    /// A video clip that uses an <see cref="IVirtualVideoSource"/> to generate frames procedurally,
    /// such as from a shader, mathematical function, or any other procedural source.
    /// Unlike <see cref="VideoClip"/>, this clip does not require a file on disk —
    /// frames are generated on-the-fly by the <see cref="VirtualSource"/>.
    /// </summary>
    public class VirtualSourceVideoClip : IClip
    {
        public required Guid Id { get; init; }
        public required string Name { get; init; }
        public uint LayerIndex { get; init; } = 0;
        public uint SubLayerIndex { get; init; }
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; set; }
        public float FrameTime { get; init; }
        public float SecondPerFrameRatio { get => 1; init { } }

        [JsonIgnore]
        public string? FilePath { get; set; }

        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public EffectProviderJSONStructure[]? EffectProviders { get; init; }
        public IEffect[]? EffectsInstances { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public IEffectProvider[]? EffectProvidersInstances { get; set; }
        public Dictionary<string, object> ExtraData { get; set; } = new();
        public bool ExtendToWholeDraft { get; set; }
        [JsonIgnore]
        public bool NeedFilePath => false;

        [JsonIgnore]
        public IVirtualVideoSource? VirtualSource { get; set; }

        public ClipMode ClipType => ClipMode.VideoClip;
        public string TypeName => "VirtualSourceVideoClip";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public string BindedSoundTrack { get; init; } = "";
        public int TargetWidth { get; set; }
        public int TargetHeight { get; set; }
        public int TargetX { get; set; }
        public int TargetY { get; set; }
        public int StartingX { get; set; }
        public int StartingY { get; set; }
        public ISpeedVarianceProvider? SpeedVarianceProviderInstance { get; set; }
        public IMixture? MixtureInstance { get; set; }
        public ISourceReplacementEffect? AlternativeSource { get; set; }

        /// <summary>
        /// The native width of the virtual source's generated frames.
        /// Used when <see cref="IVirtualVideoSource.Init"/> is called during <see cref="IClip.ReInit"/>.
        /// If 0 or negative, falls back to <see cref="TargetWidth"/>, then to 1920.
        /// </summary>
        public int VirtualWidth { get; set; }

        /// <summary>
        /// The native height of the virtual source's generated frames.
        /// Used when <see cref="IVirtualVideoSource.Init"/> is called during <see cref="IClip.ReInit"/>.
        /// If 0 or negative, falls back to <see cref="TargetHeight"/>, then to 1080.
        /// </summary>
        public int VirtualHeight { get; set; }

        /// <summary>
        /// The native frame rate of the virtual source, in frames per second.
        /// Used when <see cref="IVirtualVideoSource.Init"/> is called during <see cref="IClip.ReInit"/>.
        /// If 0 or negative, inferred from <see cref="FrameTime"/>, then defaults to 30.
        /// </summary>
        public float VirtualFps { get; set; }

        public VirtualSourceVideoClip()
        {
            (EffectsInstances, SpeedVarianceProviderInstance, MixtureInstance, AlternativeSource)
                = EffectHelper.GetEffectsInstancesSpeedVarianceAndMixture(Effects);
        }

        /// <summary>
        /// Generate a frame from the virtual source at the given source-relative index,
        /// then resize and convert to the target format.
        /// </summary>
        public IPicture GetFrameRelativeToStartPointOfSource(
            uint targetFrame,
            int targetWidth,
            int targetHeight,
            IPicture.PicturePixelMode targetPPB)
        {
            if (VirtualSource is null)
            {
                throw new NullReferenceException(
                    $"Virtual source is null for clip '{Name}' ({Id}). Set VirtualSource before rendering.");
            }

            // Clamp to valid range — virtual sources are finite (bounded by Duration)
            if (Duration > 0 && targetFrame >= Duration)
            {
                targetFrame = Duration - 1;
            }

            return VirtualSource
                .Generate(targetFrame, hasAlpha: true)
                .Resize(targetWidth, targetHeight, true)
                .ToBitPerPixel(targetPPB);
        }

        /// <summary>
        /// Initialize (or re-initialize) the virtual source with the clip's native resolution,
        /// frame rate, duration, and target pixel format.
        /// </summary>
        void IClip.ReInit(IPicture.PicturePixelMode targetPPB)
        {
            if (VirtualSource is null)
            {
                throw new NullReferenceException(
                    $"Virtual source is null for clip '{Name}' ({Id}). Cannot re-initialize.");
            }

            // Resolve native dimensions for the virtual source
            int initWidth = TargetWidth;
            int initHeight = TargetHeight;

            // Guard against degenerate or unset dimensions
            if (initWidth <= 0) initWidth = IRenderContext.Current?.TargetWidth ?? 1920;
            if (initHeight <= 0) initHeight = IRenderContext.Current?.TargetHeight ?? 1080;

            // Resolve frame rate
            int fps = (int)(VirtualFps > 0
                ? (int)Math.Ceiling(VirtualFps)
                : (FrameTime > 0 ? (int)Math.Ceiling(1f / FrameTime) : (1 / (IRenderContext.Current?.TargetSecondPerFrame ?? 30))));

            // Duration must be at least 1 frame for a valid virtual source
            uint duration = Duration > 0 ? Duration : 1;

            VirtualSource.Init(initWidth, initHeight, fps, duration, targetPPB);

            // Rebuild effect, speed-variance, and mixture instances from serialized data
            (EffectsInstances, SpeedVarianceProviderInstance, MixtureInstance, AlternativeSource)
                = EffectHelper.GetEffectsInstancesSpeedVarianceAndMixture(Effects);
        }

        void IDisposable.Dispose()
        {
            VirtualSource?.Dispose();
        }
    }
}
