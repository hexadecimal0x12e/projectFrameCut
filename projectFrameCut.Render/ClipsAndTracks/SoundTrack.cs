using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Render.ClipsAndTracks
{
    public class NormalSoundTrack : ISoundTrack
    {
        public string FromPlugin => throw new NotImplementedException();

        public TrackMode TrackType => throw new NotImplementedException();

        public string Id { get; init; }
        public string Name { get; init; }
        public uint LayerIndex { get; init; }
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; init; }
        public float Ratio { get; init; }
        public float Volume { get; init; }
    }
}
