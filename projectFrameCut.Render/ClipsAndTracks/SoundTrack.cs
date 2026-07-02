using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.ClipsAndTracks
{
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
        public IEffect[]? EffectsInstances { get; set; }
        public string? FilePath { get; set; }
        public bool NeedFilePath => true;
        public Dictionary<string, object> ExtraData { get; set; }
        public bool ExtendToWholeDraft { get; set; }
        public int TargetWidth { get; set; }
        public int TargetHeight { get; set; }
        public int TargetX { get; set; }
        public int TargetY { get; set; }
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

        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int targetWidth, int targetHeight, bool forceResize)
        {
            throw new NotSupportedException("It's impossible to get a Picture for a Soundtrack.");
        }

        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int targetWidth, int targetHeight, bool forceResize, IPicture.PicturePixelMode targetPPB)
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
        }

        public void ReInit(IPicture.PicturePixelMode targetPPB)
        {
        }
    }
}
