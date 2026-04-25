using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
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
        /// The unique identifier of this clip. <b>SHOULD BE A GUID.</b>
        /// </summary>
        /// <remarks>
        /// Starting from API V5, this property will be changed to <see cref="System.Guid"/> and the <see cref="IdAsGUID"/> property will be removed at that time.
        /// </remarks>
        public string Id { get; init; }

        /// <summary>
        /// The unique identifier of this clip. 
        /// </summary>
        public Guid IdAsGUID { get; init; }

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
        public uint Duration { get; set; }

        /// <summary>
        /// The target width of this clip. Related to <see cref="Project.ProjectJSONStructure.RelativeWidth"/>.
        /// </summary>
        public int TargetWidth { get; set; }
        /// <summary>
        /// The target height of this clip. Related to <see cref="Project.ProjectJSONStructure.RelativeHeight"/>.
        /// </summary>
        public int TargetHeight { get; set; }
        /// <summary>
        /// The target X-axis position of this clip in left-top corner. Related to <see cref="Project.ProjectJSONStructure.RelativeWidth"/>.
        /// </summary>
        public int TargetX { get; set; }
        /// <summary>
        /// The target Y-axis position of this clip in left-top corner. Related to <see cref="Project.ProjectJSONStructure.RelativeHeight"/>.
        /// </summary>
        public int TargetY { get; set; }

        /// <summary>
        /// The source's frame time (1 / frame rate) of this clip, in seconds.
        /// </summary>
        public float FrameTime { get; init; }
        /// <summary>
        /// <b>Use <see cref="SpeedVarianceProviderInstance"/>. This property is not used, always return 1 and will be removed in API V5.</b>
        /// The actual frame time's ratio.
        /// </summary>
        /// <remarks>
        /// The final frame time used to do any calculation is by (FrameTime * SpeedRatio)
        /// </remarks>
        [Obsolete("Use SpeedVarianceProviderInstance. This property is not used, always return 1 and will be removed in API V5.", false)]
        public float SecondPerFrameRatio { get; init; }

        /// <summary>
        /// The ISpeedVarianceProvider for this clip. If this property is not null, the system will use it to get the actual speed ratio for each frame instead of using the SecondPerFrameRatio property. This allows more flexible speed variance effect, such as variable speed or speed ramping.
        /// </summary>
        [JsonIgnore]
        public ISpeedVarianceProvider? SpeedVarianceProviderInstance { get; set; }

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

        /// <summary>
        /// The actual effects applied to this clip.
        /// </summary>
        [JsonIgnore]
        public IEffect[]? EffectsInstances { get; set; }

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
        /// <b>DON'T DO any frame index mapping, AND PLEASE MAKE SURE result <see cref="IPicture"/> has the correct size and format defined in parameters.</b>
        /// If you don't do these, it may cause an unexcepted result.
        /// </remarks>
        /// <param name="frameIndex">frame index related to the source.</param>
        /// <returns>the frame (<paramref name="frameIndex"/>) in <b>SOURCE, WITH SPECIFIC SIZE IN <paramref name="requiredWidth"/> * <paramref name="requiredHeight"/>.</b></returns>
        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int requiredWidth, int requiredHeight, bool forceResize, IPicture.PicturePixelMode targetPPB);

        /// <summary>
        /// Gets a frame relative to source start point. Kept for compatibility.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int requiredWidth, int requiredHeight, IPicture.PicturePixelMode targetPPB)
            => GetFrameRelativeToStartPointOfSource(frameIndex, requiredWidth, requiredHeight, true, targetPPB);

        /// <summary>
        /// Gets a frame at draft-global frame index.
        /// </summary>
        [DebuggerNonUserCode()]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IPicture GetFrame(uint targetFrame, int targetWidth, int targetHeight, bool forceResize, IPicture.PicturePixelMode targetPPB)
            => GetFrameRelativeToStartPointOfSource(GetRelativeFrameIndex(targetFrame) ?? Duration, targetWidth, targetHeight, forceResize, targetPPB);

        /// <summary>
        /// Gets the effective timeline duration for this clip after applying speed ratio/profile.
        /// </summary>
        [DebuggerNonUserCode()]
        public uint GetEffectiveDuration() => SpeedVarianceMapCache.GetOrBuild(this).EffectiveDurationFrames;

        /// <summary>
        /// Returns true if the given draft-global frame is inside this clip's visible range.
        /// </summary>
        [DebuggerNonUserCode()]
        public bool ContainsFrame(uint targetFrame)
        {
            if(ExtendToWholeDraft)
            {
                return true;
            }

            if (targetFrame < StartFrame)
            {
                return false;
            }

            uint effectiveDuration = GetEffectiveDuration();
            if (effectiveDuration == 0)
            {
                return false;
            }

            ulong endExclusive = (ulong)StartFrame + effectiveDuration;
            return (ulong)targetFrame < endExclusive;
        }


        /// <summary>
        /// Get the frame index relative to the source clip for the specified target frame in the draft.
        /// </summary>
        /// <param name="targetFrame">the frame index in the whole clip you'd like to get</param>
        /// <returns>the index of frame relative to the source, or null if the frame you want is not available (probably because of little overlap caused by rounding) </returns>
        /// <exception cref="IndexOutOfRangeException">Frame is not exist in this clip.</exception>
        [DebuggerNonUserCode()]
        public uint? GetRelativeFrameIndex(uint targetFrame)
        {
            long offsetFromClipStart = (long)targetFrame - StartFrame;
            var profile = SpeedVarianceMapCache.GetOrBuild(this);
            uint effectiveDuration = profile.EffectiveDurationFrames;

            if (offsetFromClipStart == effectiveDuration)
            {
                return null;
            }

            if (offsetFromClipStart < 0 || offsetFromClipStart >= effectiveDuration)
            {
                ulong endExclusive = (ulong)StartFrame + effectiveDuration;
                throw new IndexOutOfRangeException($"Frame #{targetFrame} is not in clip [{StartFrame}, {endExclusive}).");
            }

            ulong mappedOffset = profile.MapTimelineOffsetToSourceOffset((uint)offsetFromClipStart);
            ulong sourceIndexLong = (ulong)RelativeStartFrame + mappedOffset;
            if (sourceIndexLong > uint.MaxValue)
            {
                throw new IndexOutOfRangeException($"Frame mapping overflow for frame #{targetFrame}.");
            }

            return (uint)sourceIndexLong;
        }

        /// <summary>
        /// Re-initialize the clip. Call this function when the source file is changed and you want to reload it.
        /// </summary>
        public void ReInit(IPicture.PicturePixelMode targetPPB);


        /// <summary>
        /// The ExtraData/Metadata from the <see cref="projectFrameCut.Render.RenderAPIBase.Project.ClipDraftDTO.MetaData"/>
        /// </summary>
        public Dictionary<string, object> ExtraData { get; set; }


    }

    internal sealed class SpeedVarianceProfile
    {
        public required uint Duration { get; init; }
        public required ISpeedVarianceProvider? Provider { get; init; }
        public required uint EffectiveDurationFrames { get; init; }

        public ulong MapTimelineOffsetToSourceOffset(uint timelineOffset)
        {
            if (Duration == 0)
            {
                return 0;
            }

            if (Provider is null)
            {
                return Math.Min(timelineOffset, Duration - 1);
            }

            return MapTimelineOffsetWithProvider(timelineOffset);
        }

        private ulong MapTimelineOffsetWithProvider(uint timelineOffset)
        {
            ulong maxSourceOffset = Duration - 1;
            ulong left = 0;
            ulong right = maxSourceOffset;
            ulong best = 0;

            while (left <= right)
            {
                ulong mid = left + ((right - left) / 2);
                uint mappedTarget = ResolveTargetFrameForSourceOffset((uint)mid);

                if (mappedTarget <= timelineOffset)
                {
                    best = mid;
                    if (mid == maxSourceOffset)
                    {
                        break;
                    }

                    left = mid + 1;
                    continue;
                }

                if (mid == 0)
                {
                    break;
                }

                right = mid - 1;
            }

            return best;
        }

        private uint ResolveTargetFrameForSourceOffset(uint sourceOffset)
        {
            try
            {
                return Provider!.GetTargetFrame(sourceOffset);
            }
            catch
            {
                return sourceOffset;
            }
        }
    }

    internal static class SpeedVarianceMapCache
    {
        private static readonly ConditionalWeakTable<IClip, SpeedVarianceProfile> Cache = new();

        public static SpeedVarianceProfile GetOrBuild(IClip clip)
        {
            if (Cache.TryGetValue(clip, out var cached)
                && cached.Duration == clip.Duration
                && ReferenceEquals(cached.Provider, clip.SpeedVarianceProviderInstance))
            {
                return cached;
            }

            Cache.Remove(clip);
            var rebuilt = Build(clip);
            Cache.Add(clip, rebuilt);
            return rebuilt;
        }

        private static SpeedVarianceProfile Build(IClip clip)
        {
            uint duration = clip.Duration;
            var provider = clip.SpeedVarianceProviderInstance;

            if (duration == 0)
            {
                return new SpeedVarianceProfile
                {
                    Duration = 0,
                    Provider = provider,
                    EffectiveDurationFrames = 0,
                };
            }

            if (provider is null)
            {
                return new SpeedVarianceProfile
                {
                    Duration = duration,
                    Provider = null,
                    EffectiveDurationFrames = duration,
                };
            }

            uint effectiveDuration = ResolveEffectiveDuration(provider, duration);
            return new SpeedVarianceProfile
            {
                Duration = duration,
                Provider = provider,
                EffectiveDurationFrames = effectiveDuration,
            };
        }

        private static uint ResolveEffectiveDuration(ISpeedVarianceProvider provider, uint duration)
        {
            try
            {
                uint effective = provider.GetEffectiveLength(duration);
                if (effective > 0)
                {
                    return effective;
                }
            }
            catch
            {
            }

            return duration;
        }
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
