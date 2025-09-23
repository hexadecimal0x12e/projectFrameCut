using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace projectFrameCut.Shared
{
    public class DraftStructureJSON
    {
        public string Name { get; set; }  = "Default Project";
        public uint relativeResolution { get; set; } = 1000;
        public uint targetFrameRate { get; set; } = 60;
        public string ResourcePath { get; set; } = string.Empty;
        public object[] Clips { get; init; } = Array.Empty<string>();
    }

    public interface IClip : IDisposable
    {
        public string Id { get; init; }
        public string Name { get; init; }
        public ClipMode ClipType { get; }
        public uint LayerIndex { get; init; }
        public uint StartFrame { get; init; }
        public uint Duration { get; init; }
        public float FrameTime { get; init; }
        public RenderMode MixtureMode { get; init; }
        public string? filePath { get; init; }

        public virtual Picture GetFrame(uint targetFrame, int targetWidth, int targetHeight) => GetFrame(targetFrame - StartFrame).Resize(targetWidth, targetHeight);

        /// <summary>
        /// Get the frame at the specified index relative to the start of the clip.
        /// </summary>
        /// <param name="frameIndex"></param>
        /// <returns></returns>
        public Picture GetFrame(uint frameIndex);

        public void ReInit();
        //public object? ConvertToDestType(IClip source);

    }

    public enum ClipMode
    {
        VideoClip,
        PhotoClip,
        SolidColorClip,
        Special
    }

    public class OneFrame
    {
        public uint FrameNumber { get; init; }
        public Picture Clip { get; init; }
        public uint LayerIndex { get; init; } = 0;
        public RenderMode MixtureMode { get; init; } = RenderMode.Overlay;
        public Effects[] Effects { get; init; } = Array.Empty<Effects>();
        public IClip ParentClip { get; init; }
        public OneFrame(uint frameNumber, IClip parent, Picture pic)
        {
            FrameNumber = frameNumber;
            ParentClip = parent;
            Clip = pic;
            LayerIndex = parent.LayerIndex;
            MixtureMode = parent.MixtureMode;
        }
    }

}
