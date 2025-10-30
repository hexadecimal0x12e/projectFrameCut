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
        public uint Duration { get; set; } = 0;
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
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; set; }
        public float FrameTime { get; set; } // seconds per frame (1 / framerate)
        public RenderMode MixtureMode { get; set; } = RenderMode.Overlay;
        public string? FilePath { get; set; }
        public long? SourceDuration { get;set; } // in frames, null for infinite length source
        public float? SourceSecondPerFrame { get; set; } 

        [JsonExtensionData]
        public Dictionary<string, object>? MetaData { get; set; }

        

    }

    public class AssetItem
    {
        public string Name { get; set; } = string.Empty;
        public string? Path { get; set; }
        public projectFrameCut.Shared.ClipMode Type { get; set; }

        public long? FrameCount { get; set; }
        public float SecondPerFrame { get; set; } = float.PositiveInfinity; 
        public string? ThumbnailPath { get; set; }
        public string? AssetId { get; set; }

        [JsonIgnore]
        public object? Background { get; set; }

        [JsonIgnore]
        public bool isInfiniteLength => FrameCount == null || FrameCount <= 0 || float.IsPositiveInfinity(SecondPerFrame);

        [JsonIgnore]
        public string? Icon
        {
            get => Type switch
            {
                projectFrameCut.Shared.ClipMode.VideoClip => "📽️",
                projectFrameCut.Shared.ClipMode.PhotoClip => "🖼️",
                projectFrameCut.Shared.ClipMode.SolidColorClip => "🟦",
                _ => "❔"
            };
        }

        [JsonIgnore()]
        public DateTime AddedAt { get; set; } = DateTime.Now;
    }

    public interface IClip : IDisposable
    {
        public string Id { get; init; }
        public string Name { get; init; }
        public ClipMode ClipType { get; }
        public uint LayerIndex { get; init; }
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; } // in-point within the source
        public uint Duration { get; init; }
        public float FrameTime { get; init; }
        public RenderMode MixtureMode { get; init; }
        public string? FilePath { get; init; }

        /// <summary>
        /// Get the frame at the specified index relative to the start of the draft, and resize it to the specified width and height. Strongly recommended to use this.
        /// </summary>
        /// <param name="targetFrame">the frame in the whole clip you'd like to get</param>
        /// <param name="targetWidth">target width, clip will be resized automatically.</param>
        /// <param name="targetHeight">target height, clip will be resized automatically.</param>
        /// <param name="forceResize">force to resize result frame</param>
        /// <returns>the target clip</returns>
        /// <exception cref="IndexOutOfRangeException">Frame is not exist in this clip.</exception>
        public virtual Picture GetFrame(uint targetFrame, int targetWidth, int targetHeight, bool forceResize = false)
        {
            // Map timeline frame to source frame index: offset from clip start + in-point (RelativeStartFrame)
            long offsetFromClipStart = (long)targetFrame - StartFrame; // can be negative before the clip actually starts

            if(offsetFromClipStart == Duration)
            {
                return Picture.GenerateSolidColor(targetWidth, targetHeight, 0, 0, 0, null);
            }

            if (offsetFromClipStart < 0 || offsetFromClipStart >= Duration)
            {
                throw new IndexOutOfRangeException($"Frame #{targetFrame} is not in clip [{StartFrame}, {StartFrame + Duration}).");
            }

            ulong sourceIndexLong = (ulong)RelativeStartFrame + (ulong)offsetFromClipStart;
            if (sourceIndexLong > uint.MaxValue)
            {
                throw new IndexOutOfRangeException($"Frame mapping overflow for frame #{targetFrame}.");
            }

            uint sourceIndex = (uint)sourceIndexLong;
            return GetFrame(sourceIndex).Resize(targetWidth, targetHeight,forceResize);
        }

        /// <summary>
        /// Get the frame at the specified index relative to the start of the clip.
        /// </summary>
        /// <remarks>
        /// DO NOT DO ANY RANGE CHECK OR FRAME INDEX MAPPING IN THIS FUNCTION!!! IClip will help you do this, and do this in your code will cause unexpected result.
        /// </remarks>
        /// <param name="frameIndex">frame index related to the source clip</param>
        /// <returns>the result frame</returns>
        public Picture GetFrame(uint frameIndex);

        /// <summary>
        /// Get the length of this clip in frames. use null for infinite length.
        /// </summary>
        /// <returns>any integer represented the total length of the clip, or null for infinite length</returns>
        public uint? GetClipLength();

        /// <summary>
        /// Re-initialize the clip. Call this function when the source file is changed and you want to reload it.
        /// </summary>
        public void ReInit();

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
