using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.ClipsAndTracks
{
    public static class SoundTrackMetadata
    {
        public const string SourceClipIdKey = "SourceClipId";
        public const string GeneratedFromVideoKey = "GeneratedFromVideo";
        public const string EnabledKey = "Enabled";
        public const string VolumeKey = "Volume";
        public const string DetachedKey = "AudioDetached";
        public const string ProbeSourceKey = "AudioProbeSource";
        public const string ProbeHasStreamKey = "AudioProbeHasStream";

        public static bool ReadBool(IReadOnlyDictionary<string, object>? data, string key, bool fallback = false)
        {
            if (data is null || !data.TryGetValue(key, out var value) || value is null) return fallback;
            if (value is bool result) return result;
            if (value is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.True) return true;
                if (element.ValueKind == JsonValueKind.False) return false;
                if (element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out result)) return result;
            }
            return bool.TryParse(value.ToString(), out result) ? result : fallback;
        }

        public static float ReadVolume(IReadOnlyDictionary<string, object>? data, float fallback = 1f)
        {
            if (data is null || !data.TryGetValue(VolumeKey, out var value) || value is null) return fallback;
            if (value is double doubleValue) return (float)doubleValue;
            if (value is float floatValue) return floatValue;
            if (value is JsonElement element && element.TryGetDouble(out var jsonValue)) return (float)jsonValue;
            return float.TryParse(value.ToString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : fallback;
        }

        public static Guid? ReadSourceClipId(IReadOnlyDictionary<string, object>? data)
        {
            if (data is null || !data.TryGetValue(SourceClipIdKey, out var value) || value is null) return null;
            if (value is JsonElement element && element.ValueKind == JsonValueKind.String)
                return Guid.TryParse(element.GetString(), out var jsonGuid) ? jsonGuid : null;
            return Guid.TryParse(value.ToString(), out var guid) ? guid : null;
        }

        private static float ReadRatio(IClip clip)
        {
            var value = clip.GetType().GetProperty("SecondPerFrameRatio")?.GetValue(clip);
            if (value is float ratio && ratio > 0) return ratio;
            if (value is double doubleRatio && doubleRatio > 0) return (float)doubleRatio;
            return 1f;
        }

        public static NormalSoundTrack CreateLegacyTrack(IClip clip)
        {
            var data = clip.ExtraData is null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object>(clip.ExtraData);
            data[SourceClipIdKey] = clip.Id.ToString("D");
            data[GeneratedFromVideoKey] = clip.ClipType == ClipMode.VideoClip;
            data[EnabledKey] = ReadBool(data, EnabledKey, true);
            data[VolumeKey] = ReadVolume(data);
            return new NormalSoundTrack
            {
                Id = string.IsNullOrWhiteSpace(clip.BindedSoundTrack) ? $"legacy-audio-{clip.Id:N}" : clip.BindedSoundTrack,
                Name = $"{clip.Name}'s Audio",
                LayerIndex = clip.LayerIndex,
                StartFrame = clip.StartFrame,
                RelativeStartFrame = clip.RelativeStartFrame,
                Duration = clip.Duration,
                Ratio = ReadRatio(clip),
                Volume = ReadVolume(data),
                FilePath = clip.FilePath,
                Effects = clip.Effects,
                EffectsInstances = clip.EffectsInstances,
                ExtraData = data
            };
        }

        public static void ReInit(ISoundTrack track)
        {
            track.ReInit();
            track.EffectsInstances = EffectHelper.GetEffectsInstancesAndSpeedVariance(track.Effects).Effects;
        }

        public static void AddMissingLegacyTracks(IEnumerable<IClip> clips, ICollection<ISoundTrack> tracks, Action<string>? log = null)
        {
            foreach (var clip in clips.Where(c => c.ClipType is ClipMode.AudioClip or ClipMode.VideoClip))
            {
                if (ReadBool(clip.ExtraData, DetachedKey)) continue;
                if (clip.ExtraData?.ContainsKey(ProbeHasStreamKey) == true
                    && !ReadBool(clip.ExtraData, ProbeHasStreamKey)) continue;
                if ((!string.IsNullOrWhiteSpace(clip.BindedSoundTrack) && tracks.Any(t => t.Id == clip.BindedSoundTrack))
                    || tracks.Any(t => ReadSourceClipId(t.ExtraData) == clip.Id))
                    continue;

                var track = CreateLegacyTrack(clip);
                try
                {
                    ReInit(track);
                    if (track.SamplePerSecond <= 0) throw new InvalidOperationException("The media has no readable audio stream.");
                    tracks.Add(track);
                    log?.Invoke($"Migrated legacy clip audio {clip.Id} to soundtrack {track.Id}.");
                }
                catch (Exception ex)
                {
                    track.Dispose();
                    log?.Invoke($"Clip {clip.Id}/{clip.Name} has no readable audio stream: {ex.Message}");
                }
            }
        }
    }

    public class NormalSoundTrack : ISoundTrack
    {
        private bool disposedValue;

        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;

        public TrackMode TrackType => TrackMode.NormalTrack;

        public string Id { get; init; }
        public string Name { get; init; }
        public uint LayerIndex { get; init; }
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; init; }
        public float Ratio { get; set; } = 1f;
        public float Volume { get; set; } = 1f;
        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public IEffect[]? EffectsInstances { get; set; }
        public Dictionary<string, object> ExtraData { get; set; }

        public bool NeedFilePath => true;
        public string? FilePath { get; set; }

        [JsonIgnore]
        public IAudioSource? AudioSource { get; set; }

        public int SamplePerSecond => AudioSource?.SamplePerSecond ?? 0;


        public IAudioSamples GetAudioSamplesRelatedToStartPointOfSource(uint startIndex, int length) => AudioSource?.GetSample(startIndex, length) ?? throw new InvalidOperationException("AudioSource is not set.");

        public void ReInit()
        {
            AudioSource?.Dispose();
            AudioSource = null;
            AudioSource = FilePath is not null ? PluginManager.CreateAudioSource(FilePath) : null;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    AudioSource?.Dispose();
                }

                disposedValue = true;
            }
        }


        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }

    public class SoundTrackToClipWrapper : IClip
    {
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;

        public ClipMode ClipType => ClipMode.AudioClip;

        public Guid Id { get; init; }
        public string Name { get; init; }
        public string BindedSoundTrack { get; init; }
        public uint LayerIndex { get; init; }
        public uint SubLayerIndex { get; init; }
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; set; }
        public float FrameTime { get; init; }
        public float SecondPerFrameRatio { get; init; }
        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public EffectProviderJSONStructure[]? EffectProviders { get; init; }
        public IEffect[]? EffectsInstances { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public IEffectProvider[]? EffectProvidersInstances { get; set; }
        public string? FilePath { get; set; }
        public bool NeedFilePath => true;
        public Dictionary<string, object> ExtraData { get; set; }
        public bool ExtendToWholeDraft { get; set; }
        public int TargetWidth { get; set; }
        public int TargetHeight { get; set; }
        public int TargetX { get; set; }
        public int TargetY { get; set; }
        public int StartingX { get; set; }
        public int StartingY { get; set; }
        public ISpeedVarianceProvider? SpeedVarianceProviderInstance { get; set; }


        public ISoundTrack SoundTrack { get; set; }
        public TrackMode TrackType => TrackMode.NormalTrack;

        public IMixture? MixtureInstance { get; set; }
        public ISourceReplacementEffect? AlternativeSource { get; set; }

        public void Dispose()
        {
        }

        public uint? GetClipLength() => null;

        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex)
        {
            throw new NotSupportedException("It's impossible to get a Picture for a Soundtrack.");
        }

        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int requiredWidth, int requiredHeight, IPicture.PicturePixelMode targetPPB)
        {
            throw new NotSupportedException("It's impossible to get a Picture for a Soundtrack.");
        }

        public void ReInit()
        {
            SoundTrack = TrackType switch
            {
                TrackMode.NormalTrack => new NormalSoundTrack
                {
                    Id = BindedSoundTrack ?? Guid.NewGuid().ToString(),
                    Name = Name,
                    LayerIndex = LayerIndex,
                    StartFrame = StartFrame,
                    RelativeStartFrame = RelativeStartFrame,
                    Duration = Duration,
                    Ratio = SecondPerFrameRatio,
                    Volume = 1.0f,
                    AudioSource = FilePath is not null ? PluginManager.CreateAudioSource(FilePath) : null
                },
                _ => throw new NotSupportedException($"Unsupported track type {TrackType}."),
            };

           EffectHelper.ResolveClipEffects(this);
        }

        public void ReInit(IPicture.PicturePixelMode targetPPB)
        {
        }
    }
}
