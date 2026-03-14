using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.RenderAPIBase.ClipAndTrack
{
    public interface IClip : IDisposable
    {
        /// <summary>
        /// Gets the ID of the plugin that provided this value.
        /// </summary>
        public string FromPlugin { get; }
        /// <summary>
        /// Mode of this clip. Mostly for compatibility purpose.
        /// </summary>
        public ClipMode ClipType { get; }
        /// <summary>
        /// The type name of this clip. You must override it when you're creating a new clip type in plugin.
        /// </summary>
        public virtual string TypeName => ClipType != ClipMode.ExtendClip ? ClipType.ToString() : throw new InvalidOperationException("ClipType is ExtendClip, and you must override it when you're creating a new clip type in plugin.");

        /// <summary>
        /// The unique identifier of this clip.
        /// </summary>
        public string Id { get; init; }

        /// <summary>
        /// The name of this clip. Mostly used for display purpose.
        /// </summary>
        public string Name { get; init; }

        /// <summary>
        /// Represent which sound track's id is binded to this clip.
        /// </summary>
        public string BindedSoundTrack { get; init; }

        /// <summary>
        /// Indicate which layer this clip is in. Higher index means upper layer.
        /// </summary>
        public uint LayerIndex { get; init; }
        /// <summary>
        /// Indicate which sub-layer this clip is in. Higher index means upper sub-layer.
        /// </summary>
        public uint SubLayerIndex { get; init; }

        /// <summary>
        /// Where this clip starts in the whole draft, in frames.
        /// </summary>
        public uint StartFrame { get; init; }
        /// <summary>
        /// The start frame within the source clip, in frames.
        /// </summary>
        public uint RelativeStartFrame { get; init; } // in-point within the source
        /// <summary>
        /// The original (in 1x speed ratio) duration of this clip in the draft.
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
        /// Set which this clip should be extended to the whole draft. 
        /// </summary>
        /// <remarks>
        /// If this property is true, the system will ignore the StartFrame and Duration properties, and treat this clip as if it starts at frame 0 and ends at the last frame of the draft. 
        /// Also when this property is true, the system will always place this clip at the top.
        /// This option is only available to clips in SubTrack (<see cref="LayerIndex"/> >= 10000).
        /// </remarks>
        public bool ExtendToWholeDraft { get; set; }

        /// <summary>
        /// The effects applied to this clip's Data.
        /// Used in serialization and deserialization.
        /// </summary>
        public EffectAndMixtureJSONStructure[]? Effects { get; init; }

        [JsonIgnore]
        /// <summary>
        /// The actual effects applied to this clip.
        /// </summary>
        public IEffect[]? EffectsInstances { get; init; }

        /// <summary>
        /// Get the path of the source file for this clip. May be null for some kind of clips.
        /// </summary>
        public string? FilePath { get; set; }

        /// <summary>
        /// Indicates whether this clip need a source file path to work. If this property is false, the system will not check the file path and directly call the GetFrame function. Otherwise, the system will check the file path before calling GetFrame, and if the file path is null or invalid, it will throw an exception instead of calling GetFrame.
        /// </summary>
        public bool NeedFilePath { get; }


        /// <summary>
        /// Get the frame at the specified index relative to the start of the clip's source with the specific size.
        /// This is the ONLY method you need to implement.
        /// </summary>
        /// <remarks>
        /// <b>DON'T DO any frame index mapping, AND PLEASE MAKE SURE result <see cref="IPicture"/> has the correct size defined in parameters.</b>
        /// If you don't do these, it may cause an unexcepted result.
        /// </remarks>
        /// <param name="frameIndex">frame index related to the source.</param>
        /// <param name="forceResize">Try force resize. Mostly used in <see cref="IPicture.Resize(int, int, bool)"/>'s param preserveAspect.</param>
        /// <returns>the frame (<paramref name="frameIndex"/>) in <b>SOURCE, WITH SPECIFIC SIZE IN <paramref name="targetWidth"/> * <paramref name="targetHeight"/>.</b></returns>
        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int targetWidth, int targetHeight, bool forceResize, IPicture.PicturePixelMode targetPPB);



        /// <summary>
        /// Get the frame index relative to the source clip for the specified target frame in the draft.
        /// </summary>
        /// <param name="targetFrame">the frame index in the whole clip you'd like to get</param>
        /// <returns>the index of frame relative to the source, or null if the frame you want is not available (probably because of little overlap caused by rounding) </returns>
        /// <exception cref="IndexOutOfRangeException">Frame is not exist in this clip.</exception>
        [DebuggerNonUserCode()]
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
        /// Get the frame at the specified index relative to the start of the clip's source with the specific size.
        /// </summary>
        /// <remarks>
        /// <b>DON'T DO any frame index mapping, AND PLEASE MAKE SURE result <see cref="IPicture"/> has the correct size defined in parameters.</b>
        /// If you don't do these, it may cause an unexcepted result.
        /// </remarks>
        /// <param name="frameIndex">frame index related to the source.</param>
        /// <param name="forceResize">Try force resize. Mostly used in <see cref="IPicture.Resize(int, int, bool)"/>'s param preserveAspect.</param>
        /// <returns>the frame (<paramref name="frameIndex"/>) in <b>SOURCE, WITH SPECIFIC SIZE IN <paramref name="targetWidth"/> * <paramref name="targetHeight"/>.</b></returns>
        [Obsolete("Consider to use GetFrameRelativeToStartPointOfSource(uint, int, int, bool, IPicture.PicturePixelMode) instead to avoid any unnecessary format converting. This method will be never called, and it'll be removed in future.", false)]
        public virtual IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int targetWidth, int targetHeight, bool forceResize) => GetFrameRelativeToStartPointOfSource(frameIndex, targetWidth, targetHeight, forceResize, IPicture.PicturePixelMode.BytePicture);

        /// <summary>
        /// Get the frame at the specified index relative to the start of the draft, with specified size.
        /// </summary>
        /// <param name="targetFrame">the frame in the whole clip you'd like to get</param>
        /// <param name="targetWidth">target width, clip will be resized automatically.</param>
        /// <param name="targetHeight">target height, clip will be resized automatically.</param>
        /// <param name="forceResize">force to resize result frame</param>
        /// <returns>the target clip,or be the last frame if the frame you want is 1 frame longer than the range (probably because of little overlap caused by rounding)</returns>
        /// <remarks>you may override this method if the source is infinite length, avoiding unnecessary calculation.</remarks>
        /// <exception cref="IndexOutOfRangeException">Frame is not exist in this clip.</exception>
        public virtual IPicture GetFrame(uint targetFrame, int targetWidth, int targetHeight, bool forceResize, IPicture.PicturePixelMode targetPPB)
            => GetFrameRelativeToStartPointOfSource(GetRelativeFrameIndex(targetFrame) ?? Duration, targetWidth, targetHeight, forceResize, targetPPB);

        [Obsolete("Consider to use GetFrame(uint, int, int, bool, IPicture.PicturePixelMode) instead to avoid any unnecessary format converting.", false)]
        public virtual IPicture GetFrame(uint targetFrame, int targetWidth, int targetHeight, bool forceResize = false)
            => GetFrame(targetFrame, targetWidth, (int)targetHeight, forceResize, IPicture.PicturePixelMode.BytePicture);


        /// <summary>
        /// Re-initialize the clip. Call this function when the source file is changed and you want to reload it.
        /// </summary>
        public void ReInit(IPicture.PicturePixelMode targetPPB);

        /// <summary>
        /// Re-initialize the clip. Call this function when the source file is changed and you want to reload it.
        /// </summary>
        [Obsolete("Consider to use ReInit(IPicture.PicturePixelMode) instead to avoid any unnecessary format converting.", false)]
        public virtual void ReInit() => ReInit(IPicture.PicturePixelMode.BytePicture);

        /// <summary>
        /// The ExtraData/Metadata from the <see cref="projectFrameCut.Render.RenderAPIBase.Project.ClipDraftDTO.MetaData"/>
        /// </summary>
        public Dictionary<string, object> ExtraData { get; set; }


    }

    public class ClipEquabilityComparer : IEqualityComparer<IClip>
    {
        public bool Equals(IClip? x, IClip? y) => x?.Id == y?.Id;

        public int GetHashCode([DisallowNull] IClip obj)
        {
            return obj.Id.GetHashCode();
        }
    }

    /// <summary>
    /// Represents a group of clips.
    /// </summary>
    public class ClipGroup
    {
        public string Id { get; set; }

        public string GroupDisplayName { get; set; }

        public string[] ChildrenClips { get; set; }

        public string[] ChildrenSoundTracks { get; set; }
    }
}
