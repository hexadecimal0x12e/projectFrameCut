using ILGPU.Runtime;
using projectFrameCut.Render.ILGPU;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using static projectFrameCut.Render.VideoDecoder;

namespace projectFrameCut.Render.ILGpu
{
    public class VideoClip : IClip
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public uint LayerIndex { get; init; } = 0;
        public uint StartFrame { get; init; }
        public uint Duration { get; init; }
        public float FrameTime { get; init; }
        public Effects[] Effects { get; init; } = Array.Empty<Effects>();
        public RenderMode MixtureMode { get; init; } = RenderMode.Overlay;
        public string? filePath { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonIgnore]
        public IDecoderContext? Decoder { get; set; } = null;

        public ClipMode ClipType => ClipMode.VideoClip;

        public VideoClip()
        {

        }

        public Picture GetFrame(uint targetFrame) => (Decoder ?? throw new NullReferenceException("Decoder is null. Please init it.")).GetFrame(targetFrame - StartFrame);

        void IClip.ReInit()
        {
            Decoder = new VideoDecoder(filePath ?? throw new NullReferenceException($"VideoClip {Id}'s source path is null.")).Decoder;
        }


        void IDisposable.Dispose()
        {
            Decoder?.Dispose();
        }
    }

    public class PhotoClip : IClip
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public uint LayerIndex { get; init; } = 0;
        public uint StartFrame { get; init; }
        public uint Duration { get; init; }
        public float FrameTime { get; init; }
        public Effects[] Effects { get; init; } = Array.Empty<Effects>();
        public RenderMode MixtureMode { get; init; } = RenderMode.Overlay;
        public string? filePath { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonIgnore]
        public Picture? source { get; set; } = null;

        public ClipMode ClipType => ClipMode.VideoClip;

        public PhotoClip()
        {

        }


        public Picture GetFrame(uint targetFrame) => targetFrame - StartFrame > Duration ? throw new IndexOutOfRangeException($"Frame {targetFrame} is out of range for clip {Name} which starts at {StartFrame} and has duration {Duration}.") : source ?? throw new NullReferenceException("Source is null. Please init it.");

        void IClip.ReInit()
        {
            source = new Picture(filePath ?? throw new NullReferenceException($"VideoClip {Id}'s source path is null."));
        }


        void IDisposable.Dispose()
        {
            //nothing to dispose
        }
    }

    public class SolidColorClip : IClip
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public uint LayerIndex { get; init; } = 0;
        public uint StartFrame { get; init; }
        public uint Duration { get; init; }
        public float FrameTime { get; init; }
        public Effects[] Effects { get; init; } = Array.Empty<Effects>();
        public RenderMode MixtureMode { get; init; } = RenderMode.Overlay;
        public string? filePath { get; } = null;
        public ClipMode ClipType => ClipMode.Special;

        string? IClip.filePath { get => null; init => throw new InvalidOperationException("Set path is not supported by this type of clip."); }

        public ushort R { get; init; }
        public ushort G { get; init; }
        public ushort B { get; init; }
        public float? A { get; init; } = null;

        public Picture GetFrame(uint targetFrame, int targetWidth, int targetHeight) => Picture.GenerateSolidColor(targetWidth, targetHeight, R, G, B, A);

        public Picture GetFrame(uint frameIndex) => Picture.GenerateSolidColor(1920, 1080, R, G, B, A);

        public void ReInit()
        {

        }      

        public void Dispose()
        {

        }
    }



    public class Timeline
    {
        public static IEnumerable<OneFrame> GetFramesInOneFrame(IClip[] video, uint targetFrame, int targetWidth, int targetHeight)
        {
            List<OneFrame> result = new List<OneFrame>();
            foreach (var clip in video)
            {
                if(clip.StartFrame <= targetFrame && clip.Duration >= targetFrame)
                {
                    if(result.Any((c) => c.LayerIndex == clip.LayerIndex))
                    {
                        throw new InvalidDataException($"Two or more clips ({result.Where((c) => c.LayerIndex == clip.LayerIndex).Aggregate<OneFrame,string>(clip.filePath ?? "Clip@" + clip.Id,(a,b) => $"{a},{b.ParentClip.filePath}")}) in the same layer {clip.LayerIndex} are overlapping at frame {targetFrame}. Please fix the timeline data.");
                    }
                    result.Add(new OneFrame(targetFrame,clip, clip.GetFrame(targetFrame,targetWidth,targetHeight)));
                }
            }

            return result.OrderByDescending((c) => c.LayerIndex);
        }


        public static Picture MixtureLayers(IEnumerable<OneFrame> frames, Accelerator accelerator, uint frameIndex, int targetWidth, int targetHeight)
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

            return result ?? Picture.GenerateSolidColor(targetWidth,targetHeight,0,0,0,null);
        }


    }
}
