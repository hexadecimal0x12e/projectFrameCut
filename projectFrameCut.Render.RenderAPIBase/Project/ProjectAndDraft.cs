using projectFrameCut.Render;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace projectFrameCut.Render.RenderAPIBase.Project
{
    /// <summary>
    /// Represents the overall structure of a project in JSON format.
    /// </summary>
    public class ProjectJSONStructure
    {
        /// <summary>
        /// Name of the project.
        /// </summary>
        public string? ProjectName { get; set; }
        /// <summary>
        /// Determine the version of APIBase while the draft is saved.
        /// </summary>
        public int LastOpenAPIBaseVersion { get; set; } = 0;
        /// <summary>
        /// Determine the version of Application while the draft is saved.
        /// </summary>
        public string LastOpenAppVersion { get; set; } = "0.0.0.0";
        /// <summary>
        /// Determine what plugins used in this project.
        /// </summary>
        public List<string> PluginUsed { get; set; } = new List<string>();
        /// <summary>
        /// The relative width of the draft.
        /// </summary>
        public int RelativeWidth { get; set; } = 1920;
        /// <summary>
        /// The relative height of the draft.
        /// </summary>
        public int RelativeHeight { get; set; } = 1080;
        /// <summary>
        /// The target frame rate of the project.
        /// </summary>
        public uint TargetFrameRate { get; set; } = 60;
        /// <summary>
        /// Some project-wide properties defined by user.
        /// </summary>
        public Dictionary<string, string> UserDefinedProperties { get => field ?? new(); set; } //= new();
        /// <summary>
        /// The properties of the draft. Set and write by code only.
        /// </summary>
        public Dictionary<string, string> Properties { get => field ?? new(); set; } //= new();
        /// <summary>
        /// Gets or sets the file system path to the thumbnail image associated with the item.
        /// </summary>
        public string? ThumbPath { get; set; } = null;
        /// <summary>
        /// The save slot indicator. -1 means unknown.
        /// </summary>
        public int SaveSlotIndicator = -1;
        /// <summary>
        /// Get or set the last changed time of the project.
        /// </summary>
        public DateTime? LastChanged { get; set; }
        /// <summary>
        /// Get whether the project was normally exited.
        /// </summary>
        public bool NormallyExited { get; set; } = false;

        /// <summary>
        /// Get or set the last known draft snapshot's ID.
        /// </summary>
        public Guid LastSnapshotID { get; set; } = Guid.Empty;

        /// <summary>
        /// Get a dictionary of linked-list for a mapping between snapshot IDs and their previous/next snapshot IDs. Used in branch/edition management. 
        /// </summary>
        public Dictionary<Guid, (Guid prevoius, Guid next)> SnapshotIDMapping = new();
    }
    

    /// <summary>
    /// Represents the structure of a draft in JSON format.
    /// </summary>
    public class DraftStructureJSON
    {
        /// <summary>
        /// The unique identifier for the draft snapshot. 
        /// </summary>
        public Guid SnapshotID { get; set; } = Guid.Empty;

        /// <summary>
        /// All of the clips in the draft.
        /// </summary>
        public object[] Clips { get; set; } = Array.Empty<object>();
        /// <summary>
        /// All of the soundtracks in the draft.
        /// </summary>
        public object[] SoundTracks { get; set; } = Array.Empty<object>();


        /// <summary>
        /// Get the total duration of the draft in frames.
        /// </summary>
        public uint Duration { get; set; } = 0;
        /// <summary>
        /// Get the total duration of the audios in frames.
        /// </summary>
        public uint AudioDuration { get; set; } = 0;
        /// <summary>
        /// Get when this draft was last saved.
        /// </summary>
        public DateTime SavedAt { get; set; } = DateTime.MinValue;

        /// <summary>
        /// Indicates why the draft was changed. 
        /// Used in history management and undo/redo system.
        /// </summary>
        public string ChangeReason { get; set; } = string.Empty;
        /// <summary>
        /// The user's nickname who made the change. Used in history management and undo/redo system.
        /// </summary>
        public string ChangedByUserDisplayName { get; set; } = string.Empty;
        /// <summary>
        /// The user's ID which make this change.  Used in history management and undo/redo system.
        /// </summary>
        public Guid ChangedByUser { get; set; } = Guid.Empty;

        /// <summary>
        /// The unique identifier of the previous draft snapshot. Used in branch/edition management.
        /// </summary>
        public Guid PreviousSnapshot { get; set; } = Guid.Empty;
    }

    /// <summary>
    /// The data structure of a clip. Mostly used in JSON serialization/deserialization.
    /// </summary>
    public class ClipDraftDTO
    {
        public const string ProjectFrameRateMetaKey = "__ProjectFrameRate";
        public const string FrameSemanticVersionMetaKey = "__ClipFrameSemanticVersion";
        public const int CurrentFrameSemanticVersion = 2;

        public string FromPlugin { get; set; } = string.Empty;
        public ClipMode ClipType { get; set; } = ClipMode.Special;
        public string TypeName { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public uint LayerIndex { get; set; }
        public uint SubLayerIndex { get; set; }
        public uint StartFrame { get; set; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; set; }
        public float FrameTime { get; set; } // seconds per frame (1 / framerate)
        public float SecondPerFrameRatio { get; set; }
        public string? FilePath { get; set; }
        public long? SourceDuration { get; set; } // in frames, null for infinite length source
        public bool IsInfiniteLength { get; set; }
        public bool ShouldDisplayInUI { get; set; } = true;
        public int TargetWidth { get; set; }
        public int TargetHeight { get; set; }
        public int TargetX { get; set; }
        public int TargetY { get; set; }
        public EffectAndMixtureJSONStructure[]? Effects { get; set; }
        public EffectBundleJSONStructure[]? EffectBundles { get; set; }

        [JsonExtensionData]
        public Dictionary<string, object>? MetaData { get; set; }

    }

    public class SoundtrackDTO
    {
        public string FromPlugin { get; set; } = string.Empty;
        public TrackMode TrackType { get; set; } = TrackMode.SpecialTrack;
        public string TypeName { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? FilePath { get; set; }
        public uint LayerIndex { get; set; }
        public uint StartFrame { get; set; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; set; }
        public float SecondPerFrameRatio { get; set; }

        [JsonExtensionData]
        public Dictionary<string, object>? MetaData { get; set; }

    }

    /// <summary>
    /// Represents an asset item in the project. 
    /// </summary>
    [DebuggerDisplay("{Name}: {DurationDisplay}")]
    public record AssetItem
    {
        public string Name { get; set; } = string.Empty;
        public string? Path { get; set; }
        public string? SourceHash { get; set; }
        public AssetType AssetType { get; set; } = AssetType.Other;
        public ClipMode ClipType { get; set; }

        public long? Duration { get; set; }
        public float SecondPerFrame { get; set; } = -1;
        public string? ThumbnailPath { get; set; }
        public string? AssetId { get; set; }
        public DateTime CreatedAt { get; set; }

        public Guid CreatedBy { get; set; }
        public bool IsAIGenerated { get; set; } = false;


        public int Width { get; set; }
        public int Height { get; set; }
        public int BitPerPixel { get; set; }

        public ClipMode GetClipMode()
        {
            return AssetType switch
            {
                AssetType.Video => ClipMode.VideoClip,
                AssetType.Image => ClipMode.PhotoClip,
                AssetType.Audio => ClipMode.AudioClip,
                _ => ClipType
            };
        }

        [JsonIgnore]
        public object? Background { get; set; }

        [JsonIgnore]
        public bool isInfiniteLength => Duration == null || Duration <= 0 || SecondPerFrame <= 0;

        [JsonIgnore]
        public string? Icon
        {
            get => AssetType switch
            {
                projectFrameCut.Shared.AssetType.Video => "\ud83d\udcfd\ufe0f", //📽️
                projectFrameCut.Shared.AssetType.Image => "\ud83d\uddbc\ufe0f",//🖼️
                projectFrameCut.Shared.AssetType.Audio => "\ud83c\udfb5",//🎵
                projectFrameCut.Shared.AssetType.Font => "\ud83d\udd24",//🔤
                _ => ClipType switch
                {
                    projectFrameCut.Shared.ClipMode.VideoClip => "\ud83d\udcfd\ufe0f",//📽️
                    projectFrameCut.Shared.ClipMode.PhotoClip => "\ud83d\uddbc\ufe0f",//🖼️
                    projectFrameCut.Shared.ClipMode.AudioClip => "\ud83c\udfb5",//🎵
                    projectFrameCut.Shared.ClipMode.SolidColorClip => "\ud83d\udfe6",//🟦
                    projectFrameCut.Shared.ClipMode.SubtitleClip => "\ud83d\udcad",//💭
                    projectFrameCut.Shared.ClipMode.ExtendClip => "\ud83d\udd0c",//🔌
                    _ => "\u2754" // ❔
                }
            };
        }

        [JsonIgnore]
        public string DurationTimeDisplay
        {
            get => AssetType switch
            {
                AssetType.Video => ToTimeSpanDisplay((Duration ?? 0) * (double)SecondPerFrame),
                AssetType.Audio => ToTimeSpanDisplay(Duration ?? 0),
                _ => string.Empty
            };
        }

        [JsonIgnore]
        public string DurationDisplay
        {
            get => Icon + " " + DurationTimeDisplay;
        }

        private static string ToTimeSpanDisplay(double seconds)
        {
            return TimeSpan.FromTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond)).ToString("hh\\:mm\\:ss");
        }

        public static AssetType GetAssetType(string path)
        {
            var ext = System.IO.Path.GetExtension(path).ToLower();
            return ext switch
            {
                ".mp4" or ".mov" or ".avi" or ".mkv" or ".webm" => AssetType.Video,
                ".mp3" or ".wav" or ".aac" or ".flac" or ".ogg" => AssetType.Audio,
                ".jpg" or ".jpeg" or ".png" or ".bmp" or ".svg" or ".gif" => AssetType.Image,
                ".ttf" or ".otf" => AssetType.Font,
                _ => AssetType.Other
            };
        }

        public static string GetAssetTypeDisplayName(AssetType assetType)
        {
            return assetType switch
            {
                AssetType.Video => "Video",
                AssetType.Audio => "Audio",
                AssetType.Image => "Image",
                AssetType.Font => "Font",
                _ => "Other"
            };
        }

        public override int GetHashCode() => Guid.TryParse(AssetId, out var guid) ? guid.GetHashCode() : AssetId?.GetHashCode() ?? base.GetHashCode();
        public override string ToString() => $"{Name}: {DurationDisplay} ({AssetId})";
    }

    public class AssetItemComparer : IEqualityComparer<AssetItem>
    {
        public bool Equals(AssetItem? x, AssetItem? y)
        {
            return x?.AssetId != null && y?.AssetId != null && x.AssetId == y.AssetId;
        }

        public int GetHashCode([DisallowNull] AssetItem obj)
        {
#pragma warning disable CS8602 // already [DisallowNull]
            return obj.GetHashCode();
#pragma warning restore CS8602 // 
        }
    }



}
