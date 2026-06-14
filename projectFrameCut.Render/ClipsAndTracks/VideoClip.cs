using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Plugin;
using System.Text.Json.Serialization;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Drawing.Processing.Resizing;

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
        public IEffect[]? EffectsInstances { get; set; }
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
        public ISpeedVarianceProvider? SpeedVarianceProviderInstance { get; set; }
        public IMixture? MixtureInstance { get; set; }

        public string TargetDecoder { get; set; } = string.Empty;
        public double HDRBrightnessOffset { get; set; } = 0;

        [JsonIgnore()]
        public string? DecoderName => Decoder?.TypeName;


        public VideoClip()
        {
            (EffectsInstances, SpeedVarianceProviderInstance, MixtureInstance) = EffectHelper.GetEffectsInstancesSpeedVarianceAndMixture(Effects);
        }

        public IPicture GetFrameRelativeToStartPointOfSource(uint targetFrame, int targetWidth, int targetHeight, bool forceResize, IPicture.PicturePixelMode targetPPB)
        {
            if (_decoderPool is null)
            {
                throw new NullReferenceException("Decoder is null. Please init it.");
            }

            using var decoderLease = _decoderPool.Rent(targetFrame);
            var decoder = decoderLease.Decoder;
            targetFrame = ClampFrameToDecoderRange(decoder, targetFrame);

            if (decoder is HDRDecoderContext h)
            {
                return ((HDRPicture16bpp)h.GetHDRFrame(targetFrame, hasAlpha: true).Resize(targetWidth, targetHeight, forceResize)).SetBrightnessOffset(HDRBrightnessOffset).ToBitPerPixel(targetPPB);
            }

            return decoder.GetFrame(targetFrame).Resize(targetWidth, targetHeight, forceResize).ToBitPerPixel(targetPPB);
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

            var poolKey = BuildDecoderPoolKey(FilePath, TargetDecoder);
            if (_decoderPool is not null && string.Equals(_decoderPoolKey, poolKey, StringComparison.OrdinalIgnoreCase) && !_decoderPool.Disposed)
            {
                Decoder = _decoderPool.RepresentativeDecoder;
                (EffectsInstances, SpeedVarianceProviderInstance, MixtureInstance) = EffectHelper.GetEffectsInstancesSpeedVarianceAndMixture(Effects);
                return;
            }

            ReleaseDecoderPool();

            var pool = AcquireDecoderPool(poolKey, CreateDecoderInstance);
            _decoderPoolKey = poolKey;
            _decoderPool = pool;
            Decoder = pool.RepresentativeDecoder;
            (EffectsInstances, SpeedVarianceProviderInstance, MixtureInstance) = EffectHelper.GetEffectsInstancesSpeedVarianceAndMixture(Effects);

        }


        void IDisposable.Dispose()
        {
            ReleaseDecoderPool();
        }

        private IVideoSource CreateDecoderInstance()
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
                long bestDistance = long.MaxValue;

                foreach (var entry in _decoders)
                {
                    if (entry.InUse || entry.Decoder.Disposed)
                    {
                        continue;
                    }

                    long distance = Math.Abs((long)entry.Decoder.Index - targetFrame);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestEntry = entry;
                    }
                }

                return bestEntry;
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

}
