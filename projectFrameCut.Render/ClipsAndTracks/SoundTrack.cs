using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Render.ClipsAndTracks
{
    public class NormalSoundTrack : ISoundTrack
    {
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;

        public TrackMode TrackType => TrackMode.NormalTrack;

        public string Id { get; init; }
        public string Name { get; init; }
        public uint LayerIndex { get; init; }
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; init; }
        public float Ratio { get; init; }
        public float Volume { get; init; }
        public IAudioSource? AudioSource { get; set; }
    }

    public class SoundTrackToClipWrapper : IClip
    {
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;

        public ClipMode ClipType => ClipMode.AudioClip;

        public string Id { get; init; }
        public string Name { get; init; }
        public string? BindedSoundTrack { get => SoundTrack?.Id; init { throw new NotSupportedException(); } }
        public uint LayerIndex { get; init; }
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; init; }
        public float FrameTime { get; init; }
        public float SecondPerFrameRatio { get; init; }
        public MixtureMode MixtureMode { get; init; }
        public Dictionary<string, object>? MixtureArgs { get; init; }
        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public IEffect[]? EffectsInstances { get; init; }
        public string? FilePath { get; set; }
        public bool NeedFilePath => true;

        public ISoundTrack SoundTrack { get; set; }
        public TrackMode TrackType => TrackMode.NormalTrack;

        public void Dispose()
        {
        }

        public uint? GetClipLength() => null;

        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex)
        {
            throw new NotSupportedException("This clip does not support getting frames relative to the start point of the source.");
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
    }
}
