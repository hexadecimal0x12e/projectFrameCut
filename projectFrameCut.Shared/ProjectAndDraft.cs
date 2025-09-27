using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace projectFrameCut.Shared
{
    public class ProjectJSONStructure
    {
        public string projectName { get; set; } = "Untitled Project";
        public string ResourcePath { get; set; } = string.Empty;

    }

    public class DraftStructureJSON
    {
        public string Name { get; set; }  = "Default Project";
        public uint relativeResolution { get; set; } = 1000;
        public uint targetFrameRate { get; set; } = 60;
        public object[] Clips { get; init; } = Array.Empty<string>();
    }

    // DTO used for exporting timeline clips from UI view models into DraftStructureJSON.Clips
    public class ClipDraftDTO
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public ClipMode ClipType { get; set; } = ClipMode.Special;
        public uint LayerIndex { get; set; }
        public uint StartFrame { get; set; }
        public uint Duration { get; set; }
        public float FrameTime { get; set; } // seconds per frame (1 / framerate)
        public RenderMode MixtureMode { get; set; } = RenderMode.Overlay;
        public string? FilePath { get; set; }

        [JsonExtensionData]
        public Dictionary<string, object>? MetaData { get; set; }

        

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
        public string? FilePath { get; init; }

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
