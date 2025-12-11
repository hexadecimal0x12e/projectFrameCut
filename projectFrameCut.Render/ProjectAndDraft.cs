using projectFrameCut.Render;
using projectFrameCut.Render.Plugins;
using projectFrameCut.VideoMakeEngine;
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

namespace projectFrameCut.Shared
{
    public class ProjectJSONStructure
    {
        public string? projectName { get; set; }
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
        public EffectAndMixtureJSONStructure[]? Effects { get; set; }

        [JsonExtensionData]
        public Dictionary<string, object>? MetaData { get; set; }

    }

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
