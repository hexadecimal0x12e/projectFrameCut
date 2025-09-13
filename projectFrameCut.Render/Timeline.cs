using ILGPU.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using static projectFrameCut.Render.VideoDecoder;

namespace projectFrameCut.Render
{
    public class DraftStructure
    {
        public string Name { get; set; }  = "Default Project";
        public uint relativeResolution { get; set; } = 1000;
        public uint targetFrameRate { get; set; } = 60;
        public string ResourcePath { get; set; } = string.Empty;
        public Clip[] Clips { get; init; } = Array.Empty<Clip>();
    }

    public class Clip
    {
        public string Id { get; init; }
        public string Name { get; init; }
        public uint LayerIndex { get; init; } = 0;
        public uint StartFrame { get; init; }
        public uint Duration { get; init; }
        public float FrameTime { get; init; }
        public Effects[] Effects { get; init; } = Array.Empty<Effects>();
        public RenderMode MixtureMode { get; init; } = RenderMode.Overlay;
        public string filePath { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonIgnore]
        public IDecoderContext Decoder { get; set; }

        public Clip(string id, string name, uint startFrame, uint duration, float frameTime, IDecoderContext decoder, string filePath)
        {
            Id = id;
            Name = name;
            StartFrame = startFrame;
            Duration = duration;
            FrameTime = frameTime;
            this.Decoder = decoder;
            this.filePath = filePath;
        }

        internal Picture GetFrame(uint targetFrame) => Decoder.GetFrame(targetFrame - StartFrame);
    }

    public class OneFrame
    {
        public uint FrameNumber { get; init; }
        public Picture Clip { get; init; }
        public uint LayerIndex { get; init; } = 0;
        public RenderMode MixtureMode { get; init; } = RenderMode.Overlay;
        public Effects[] Effects { get; init; } = Array.Empty<Effects>();
        public Clip ParentClip { get; init; }
        public OneFrame(uint frameNumber, Clip parent, Picture pic)
        {
            FrameNumber = frameNumber;
            ParentClip = parent;
            Clip = pic;
            LayerIndex = parent.LayerIndex;
            MixtureMode = parent.MixtureMode;
            Effects = parent.Effects;
        }
    }

    public class Timeline
    {
        public static IEnumerable<OneFrame> GetFramesInOneFrame(Clip[] video, uint targetFrame)
        {
            List<OneFrame> result = new List<OneFrame>();
            foreach (var clip in video)
            {
                if(clip.StartFrame <= targetFrame && clip.Duration >= targetFrame)
                {
                    if(result.Any((c) => c.LayerIndex == clip.LayerIndex))
                    {
                        throw new InvalidDataException($"Two or more clips ({result.Where((c) => c.LayerIndex == clip.LayerIndex).Aggregate<OneFrame,string>(clip.filePath,(a,b) => $"{a},{b.ParentClip.filePath}")}) in the same layer {clip.LayerIndex} are overlapping at frame {targetFrame}. Please fix the timeline data.");
                    }
                    result.Add(new OneFrame(targetFrame,clip, clip.GetFrame(targetFrame)));
                }
            }

            return result.OrderByDescending((c) => c.LayerIndex);
        }


        public static Picture MixtureLayers(IEnumerable<OneFrame> frames, Accelerator accelerator, uint frameIndex)
        {
            Picture? result = null;
            foreach (var frame in frames)
            {
                if (result == null)
                {
                    result = frame.Clip;
                }
                else
                {
                    Picture effected = frame.Clip;
                    if (frame.Effects.Length > 0)
                    {
                        foreach(var effect in frame.Effects)
                        {
                            effected = Effect.RenderEffect(effected,effect.Type,accelerator,frameIndex,effect.Arguments);
                        }
                    }
                    result = Mixture.MixtureFrames(accelerator, frame.MixtureMode, effected, result, 65535, true, null, frameIndex);
                }
            }

            return result ?? new Picture(0,0) { frameIndex = frameIndex  };
        }


    }
}
