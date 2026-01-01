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
using System.Text.Json;
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
        public string? projectName { get; set; }
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
        public uint targetFrameRate { get; set; } = 60;
        /// <summary>
        /// Some project-wide properties defined by user.
        /// </summary>
        public Dictionary<string, string> UserDefinedProperties = new();
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
    }

    /// <summary>
    /// Represents the structure of a draft in JSON format.
    /// </summary>
    public class DraftStructureJSON
    {
        /// <summary>
        /// The relative resolution of the draft. Unused, just for backward compatibility.
        /// </summary>
        [Obsolete("Use RelativeWidth and RelativeHeight instead.")]
        [JsonIgnore]
        public uint relativeResolution { get; set; } = 1000;

        /// <summary>
        /// The target frame rate of the draft.
        /// </summary>
        public uint targetFrameRate { get; set; } = 60;

        /// <summary>
        /// All of the clips in the draft.
        /// </summary>
        public object[] Clips { get; init; } = Array.Empty<object>();
        /// <summary>
        /// All of the soundtracks in the draft.
        /// </summary>
        public object[] SoundTracks { get; init; } = Array.Empty<object>();


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
    }

    /// <summary>
    /// The data structure of a clip. Mostly used in JSON serialization/deserialization.
    /// </summary>
    public class ClipDraftDTO
    {
        public string FromPlugin { get; set; } = string.Empty;
        public ClipMode ClipType { get; set; } = ClipMode.Special;
        public string TypeName { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public uint LayerIndex { get; set; }
        public uint StartFrame { get; set; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; set; }
        public float FrameTime { get; set; } // seconds per frame (1 / framerate)
        public float SecondPerFrameRatio { get; set; }
        public MixtureMode MixtureMode { get; set; } = MixtureMode.Overlay;
        public string? FilePath { get; set; }
        public long? SourceDuration { get; set; } // in frames, null for infinite length source
        public bool IsInfiniteLength { get; set; }
        public EffectAndMixtureJSONStructure[]? Effects { get; set; }

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
    public class AssetItem
    {
        public string Name { get; set; } = string.Empty;
        public string? Path { get; set; }
        public string? SourceHash { get; set; }
        public AssetType AssetType { get; set; } = AssetType.Other;
        public ClipMode Type { get; set; }

        public long? FrameCount { get; set; }
        public float SecondPerFrame { get; set; } = -1;
        public string? ThumbnailPath { get; set; }
        public string? AssetId { get; set; }
        public DateTime CreatedAt { get; set; } 

        public int Width { get; set; }
        public int Height { get; set; }

        public ClipMode GetClipMode()
        {
            return AssetType switch
            {
                AssetType.Video => ClipMode.VideoClip,
                AssetType.Image => ClipMode.PhotoClip,
                AssetType.Audio => ClipMode.AudioClip,
                _ => Type
            };
        }

        [JsonIgnore]
        public object? Background { get; set; }

        [JsonIgnore]
        public bool isInfiniteLength => FrameCount == null || FrameCount <= 0 || SecondPerFrame <= 0;

        [JsonIgnore]
        public string? Icon
        {
            get => AssetType switch
            {
                projectFrameCut.Shared.AssetType.Video => "\ud83d\udcfd\ufe0f", //📽️
                projectFrameCut.Shared.AssetType.Image => "\ud83d\uddbc\ufe0f",//🖼️
                projectFrameCut.Shared.AssetType.Audio => "\ud83c\udfb5",//🎵
                projectFrameCut.Shared.AssetType.Font => "\ud83d\udd24",//🔤
                _ => Type switch
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
        public string DurationDisplay
        {
            get => Icon + " " + AssetType switch
            {
                AssetType.Video  => TimeSpan.FromSeconds((double)(FrameCount ?? 0 * SecondPerFrame)).ToString(),
                AssetType.Audio => TimeSpan.FromSeconds((double)(FrameCount ?? 0d)).ToString(),
                _ => ""
            };
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

        public  static string GetAssetTypeDisplayName(AssetType assetType)
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

    }

    

}
