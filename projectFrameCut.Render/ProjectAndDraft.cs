using projectFrameCut.VideoMakeEngine;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace projectFrameCut.Shared
{
    public class ProjectJSONStructure
    {
        public string projectName { get; set; } = "Untitled Project";
        public Dictionary<string, string> UserDefinedProperties = new();
        public int SaveSlotIndicator = -1;

        public DateTime? LastChanged { get; set; }
        public bool NormallyExited { get; set; } = false;
    }

    public class DraftStructureJSON
    {
        public uint relativeResolution { get; set; } = 1000;
        public uint targetFrameRate { get; set; } = 60;
        public object[] Clips { get; init; } = Array.Empty<string>();
        public uint Duration { get; set; } = 0;
        public DateTime SavedAt { get; set; } = DateTime.MinValue;
    }

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
        public float SecondPerFrameRatio { get; set; }
        public MixtureMode MixtureMode { get; set; } = MixtureMode.Overlay;
        public string? FilePath { get; set; }
        public long? SourceDuration { get; set; } // in frames, null for infinite length source
        public EffectAndMixtureJSONStructure[]? Effects { get; set; }

        [JsonExtensionData]
        public Dictionary<string, object>? MetaData { get; set; }

    }

    public class AssetItem
    {
        public string Name { get; set; } = string.Empty;
        public string? Path { get; set; }
        public string? SourceHash { get; set; }
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
        /// <summary>
        /// The unique identifier of this clip.
        /// </summary>
        public string Id { get; init; }
        /// <summary>
        /// The name of this clip. Mostly used for display purpose.
        /// </summary>
        public string Name { get; init; }
        /// <summary>
        /// Mode of this clip.
        /// </summary>
        public ClipMode ClipType { get; }
        /// <summary>
        /// Indicate which layer this clip is in. Higher index means upper layer.
        /// </summary>
        public uint LayerIndex { get; init; }
        /// <summary>
        /// Where this clip starts in the whole draft, in frames.
        /// </summary>
        public uint StartFrame { get; init; }
        /// <summary>
        /// The start frame within the source clip, in frames.
        /// </summary>
        public uint RelativeStartFrame { get; init; } // in-point within the source
        /// <summary>
        /// Total duration of the source clip in frames. 0 will be treated as infinite length.
        /// </summary>
        public uint Duration { get; init; }
        /// <summary>
        /// The source's frame time (1 / frame rate) of this clip, in seconds.
        /// </summary>
        public float FrameTime { get; init; }
        /// <summary>
        /// The actual frame time's ratio
        /// </summary>
        /// <remarks>
        /// The final frame time used to do any calculation is by (FrameTime * SpeedRatio)
        /// </remarks>
        public float SecondPerFrameRatio { get; init; }
        /// <summary>
        /// Get the mixture mode applied to this clip.
        /// </summary>
        public MixtureMode MixtureMode { get; init; }
        /// <summary>
        /// 
        /// </summary>
        public Dictionary<string, object>? MixtureArgs { get; init; }
        /// <summary>
        /// 
        /// </summary>
        public EffectAndMixtureJSONStructure[]? Effects { get; init; }

        public IEffect[]? EffectsInstances { get; init; }

        public static IEffect[] GetEffectsInstances(EffectAndMixtureJSONStructure[]? Effects)
        {
            if (Effects is null || Effects.Length == 0)
            {
                return Array.Empty<IEffect>();
            }
            List<IEffect> effects = new();
            foreach (var item in Effects)
            {
                effects.Add(EffectHelper.CreateFromJSONStructure(item));
            }
            return effects.ToArray();
        }

        /// <summary>
        /// Get the path of the source file for this clip. May be null for some kind of clips.
        /// </summary>
        public string? FilePath { get; init; }

        /// <summary>
        /// Get the frame index relative to the source clip for the specified target frame in the draft.
        /// </summary>
        /// <param name="targetFrame">the frame index in the whole clip you'd like to get</param>
        /// <returns>the index of frame relative to the source, or null if the frame you want is not available (probably because of little overlap caused by rounding) </returns>
        /// <exception cref="IndexOutOfRangeException">Frame is not exist in this clip.</exception>
        public uint? GetRelativeFrameIndex(uint targetFrame)
        {
            uint duration = Duration;
            uint startFrame = StartFrame;
            uint relativeStartFrame = RelativeStartFrame;
            if (SecondPerFrameRatio != 1)
            {
                duration = (uint)Math.Round(Duration * SecondPerFrameRatio);
                startFrame = (uint)Math.Round(StartFrame * SecondPerFrameRatio);
                relativeStartFrame = (uint)Math.Round(RelativeStartFrame * SecondPerFrameRatio);
            }

            long offsetFromClipStart = (long)targetFrame - startFrame;

            if (offsetFromClipStart == duration)
            {
                return null;
            }

            if (offsetFromClipStart < 0 || offsetFromClipStart >= duration)
            {
                throw new IndexOutOfRangeException($"Frame #{targetFrame} is not in clip [{startFrame}, {startFrame + duration}).");
            }

            ulong sourceIndexLong = (ulong)relativeStartFrame + (ulong)offsetFromClipStart;
            if (sourceIndexLong > uint.MaxValue)
            {
                throw new IndexOutOfRangeException($"Frame mapping overflow for frame #{targetFrame}.");
            }

            return (uint)Math.Round(sourceIndexLong / SecondPerFrameRatio);
        }

        /// <summary>
        /// Get the frame at the specified index relative to the start of the draft, and resize it to the specified width and height. Strongly recommended to use this.
        /// </summary>
        /// <param name="targetFrame">the frame in the whole clip you'd like to get</param>
        /// <returns>the target clip, or null if the frame you want is 1 frame longer than the range (probably because of little overlap caused by rounding)</returns>
        /// <exception cref="IndexOutOfRangeException">Frame is not exist in this clip.</exception>
        public Picture? GetFrame(uint targetFrame)
        {
            var relativeIndex = GetRelativeFrameIndex(targetFrame);
            if (relativeIndex is null)
            {
                return null;
            }
            return GetFrameRelativeToStartPointOfSource(relativeIndex.Value);
        }

        /// <summary>
        /// Get the frame at the specified index relative to the start of the draft, and resize it to the specified width and height. Strongly recommended to use this.
        /// </summary>
        /// <param name="targetFrame">the frame in the whole clip you'd like to get</param>
        /// <param name="targetWidth">target width, clip will be resized automatically.</param>
        /// <param name="targetHeight">target height, clip will be resized automatically.</param>
        /// <param name="forceResize">force to resize result frame</param>
        /// <returns>the target clip,or be the last frame if the frame you want is 1 frame longer than the range (probably because of little overlap caused by rounding)</returns>
        /// <remarks>you may override this method if you source can generate the frame directly with a specific size.</remarks>
        /// <exception cref="IndexOutOfRangeException">Frame is not exist in this clip.</exception>
        public virtual Picture GetFrame(uint targetFrame, int targetWidth, int targetHeight, bool forceResize = false)
            => GetFrameRelativeToStartPointOfSource(GetRelativeFrameIndex(targetFrame) ?? Duration, targetWidth, targetHeight, forceResize);

        /// <summary>
        /// Get the frame at the specified index relative to the start of the clip.
        /// </summary>
        /// <remarks>
        /// DO NOT DO ANY RANGE CHECK OR FRAME INDEX MAPPING IN THIS FUNCTION!!! <see cref="IClip"/> will help you do this, and do this in your code will cause unexpected result.
        /// </remarks>
        /// <param name="frameIndex">frame index related to the source clip</param>
        /// <returns>the result frame</returns>
        public Picture GetFrameRelativeToStartPointOfSource(uint frameIndex);

        /// <summary>
        /// Get the frame at the specified index relative to the start of the clip with the specific size.
        /// </summary>
        /// <remarks>
        /// the default implementation will call <see cref="GetFrameRelativeToStartPointOfSource(uint)"/> and resize the result.
        /// </remarks>
        /// <param name="frameIndex">frame index related to the source clip</param>
        /// <returns>the result frame</returns>
        public virtual Picture GetFrameRelativeToStartPointOfSource(uint frameIndex, int targetWidth, int targetHeight, bool forceResize)
            => GetFrameRelativeToStartPointOfSource(frameIndex).Resize(targetWidth, targetHeight, forceResize);

        /// <summary>
        /// Get the length of this clip in frames. use null for infinite length.
        /// </summary>
        /// <returns>any positive integer represented the total length of the clip, or null for infinite length</returns>
        [Obsolete("Use Duration property instead.", false)]
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
        public MixtureMode MixtureMode { get; init; } = MixtureMode.Overlay;
        public IEffect[] Effects { get; init; } = Array.Empty<IEffect>();
        public IClip ParentClip { get; init; }
        public OneFrame(uint frameNumber, IClip parent, Picture pic)
        {
            FrameNumber = frameNumber;
            ParentClip = parent;
            Clip = pic;
            LayerIndex = parent.LayerIndex;
            MixtureMode = parent.MixtureMode;
            Effects = IClip.GetEffectsInstances(parent.Effects);
        }
    }

}
