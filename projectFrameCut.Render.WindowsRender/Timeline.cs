using ILGPU.Runtime;
using projectFrameCut.Render.ILGPU;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using static projectFrameCut.Render.Video;

namespace projectFrameCut.Render.ILGpu
{
    public class VideoClip : IClip
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public uint LayerIndex { get; init; } = 0;
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; init; }
        public float FrameTime { get; init; }
        public projectFrameCut.Shared.Effects[] Effects { get; init; } = Array.Empty<projectFrameCut.Shared.Effects>();
        public RenderMode MixtureMode { get; init; } = RenderMode.Overlay;
        public string? FilePath { get; init; }

        [System.Text.Json.Serialization.JsonIgnore]
        public IDecoderContext? Decoder { get; set; } = null;

        public ClipMode ClipType => ClipMode.VideoClip;


        public VideoClip()
        {

        }

        public Picture GetFrame(uint targetFrame) => (Decoder ?? throw new NullReferenceException("Decoder is null. Please init it.")).GetFrame(targetFrame);

        void IClip.ReInit()
        {
            Decoder = new Video(FilePath ?? throw new NullReferenceException($"VideoClip {Id}'s source path is null.")).Decoder;
        }


        void IDisposable.Dispose()
        {
            Decoder?.Dispose();
        }

        public uint? GetClipLength() => null;
    }

    public class PhotoClip : IClip
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public uint LayerIndex { get; init; } = 0;
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; init; }
        public float FrameTime { get; init; }
        public projectFrameCut.Shared.Effects[] Effects { get; init; } = Array.Empty<projectFrameCut.Shared.Effects>();
        public RenderMode MixtureMode { get; init; } = RenderMode.Overlay;
        public string? FilePath { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonIgnore]
        public Picture? source { get; set; } = null;

        public ClipMode ClipType => ClipMode.PhotoClip;

        public PhotoClip()
        {

        }


        public Picture GetFrame(uint targetFrame) => source ?? throw new NullReferenceException("Source is null. Please init it.");

        void IClip.ReInit()
        {
            source = new Picture(FilePath ?? throw new NullReferenceException($"VideoClip {Id}'s source path is null."));
        }


        void IDisposable.Dispose()
        {
            //nothing to dispose
        }

        public uint? GetClipLength() => Duration;
    }

    public class SolidColorClip : IClip
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public uint LayerIndex { get; init; } = 0;
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; init; }
        public float FrameTime { get; init; }
        public projectFrameCut.Shared.Effects[] Effects { get; init; } = Array.Empty<projectFrameCut.Shared.Effects>();
        public RenderMode MixtureMode { get; init; } = RenderMode.Overlay;
        public string? filePath { get; } = null;
        public ClipMode ClipType => ClipMode.Special;

        string? IClip.FilePath { get => null; init => throw new InvalidOperationException("Set path is not supported by this type of clip."); }

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

        public uint? GetClipLength() => Duration;
    }



    public class Timeline
    {
        public static IEnumerable<OneFrame> GetFramesInOneFrame(IClip[] video, uint targetFrame, int targetWidth, int targetHeight, bool forceResize = false)
        {
            List<OneFrame> result = new List<OneFrame>();
            foreach (var clip in video)
            {
                if(clip.StartFrame <= targetFrame && clip.Duration + clip.StartFrame >= targetFrame)
                {
                    if(result.Any((c) => c.LayerIndex == clip.LayerIndex))
                    {
                        throw new InvalidDataException($"Two or more clips ({result.Where((c) => c.LayerIndex == clip.LayerIndex).Aggregate<OneFrame,string>(clip.FilePath ?? "Clip@" + clip.Id,(a,b) => $"{a},{b.ParentClip.FilePath}")}) in the same layer {clip.LayerIndex} are overlapping at frame {targetFrame}. Please fix the timeline data.");
                    }
                    result.Add(new OneFrame(targetFrame,clip, clip.GetFrame(targetFrame,targetWidth,targetHeight,forceResize)));
                }
            }

            return result.OrderByDescending((c) => c.LayerIndex);
        }

        public static string GetFrameHash(IClip[] video, uint targetFrame)
        {
            List<OneFrame> result = new List<OneFrame>();
            foreach (var clip in video)
            {
                if (clip.StartFrame <= targetFrame && clip.Duration + clip.StartFrame >= targetFrame)
                {
                    if (result.Any((c) => c.LayerIndex == clip.LayerIndex))
                    {
                        throw new InvalidDataException($"Two or more clips ({result.Where((c) => c.LayerIndex == clip.LayerIndex).Aggregate<OneFrame, string>(clip.FilePath ?? "Clip@" + clip.Id, (a, b) => $"{a},{b.ParentClip.FilePath}")}) in the same layer {clip.LayerIndex} are overlapping at frame {targetFrame}. Please fix the timeline data.");
                    }
                    result.Add(new OneFrame(targetFrame, clip, null));
                }
            }

            var f = JsonSerializer.Serialize(result);

#if DEBUG
            Log($"Frame:\r\n{f}\r\n---");
#endif

            if (f == "[]") return "nullframe";

            return SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(f)).Aggregate("0x", ((b,c) => b + c.ToString("x2")));
        }


        public static Picture MixtureLayers(IEnumerable<OneFrame> frames, Accelerator accelerator, uint frameIndex, int targetWidth, int targetHeight)
        {
            try
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
                            foreach (var effect in frame.Effects)
                            {
                                effected = Effect.RenderEffect(effected, effect.Type, accelerator, frameIndex, effect.Arguments);
                            }
                        }
                        result = Mixture.MixtureFrames(accelerator, frame.MixtureMode, effected, result, 65535, true, null, frameIndex);
                    }
                }

                return result ?? Picture.GenerateSolidColor(targetWidth, targetHeight, 0, 0, 0, null);
            }
            catch (Exception ex)
            {
                Log(ex);
                return new Picture(Path.Combine(AppContext.BaseDirectory, "FallbackResources", "MediaNotAvailable.png")).Resize(targetHeight, targetHeight, true);
            }
            
        }


    }
}
