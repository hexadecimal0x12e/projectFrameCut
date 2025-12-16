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
        /// Some project-wide properties defined by user.
        /// </summary>
        public Dictionary<string, string> UserDefinedProperties = new();
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
        public object[] Clips { get; init; } = Array.Empty<string>();
        /// <summary>
        /// Get the total duration of the draft in frames.
        /// </summary>
        public uint Duration { get; set; } = 0;
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

    /// <summary>
    /// Represents an asset item in the project. 
    /// </summary>
    public class AssetItem
    {
        public string Name { get; set; } = string.Empty;
        public string? Path { get; set; }
        public string? SourceHash { get; set; }
        public ClipMode Type { get; set; }

        public long? FrameCount { get; set; }
        public float SecondPerFrame { get; set; } = float.PositiveInfinity;
        public string? ThumbnailPath { get; set; }
        public string? AssetId { get; set; }

        public int Width { get; set; }
        public int Height { get; set; }

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

    

}
