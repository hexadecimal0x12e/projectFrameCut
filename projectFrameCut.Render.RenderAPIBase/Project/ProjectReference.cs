using System.Text.Json;

namespace projectFrameCut.Render.RenderAPIBase.Project;

public enum ProjectReferenceMode { Snapshot, Linked }

public sealed class ProjectReference
{
    public const string MetadataKey = "ProjectReference";
    public ProjectReferenceMode Mode { get; set; }
    public string SourcePath { get; set; } = "";
    public string SnapshotPath { get; set; } = "";
    public Guid ProjectId { get; set; }
    public Guid SnapshotId { get; set; }

    public static ProjectReference? Read(Dictionary<string, object>? metadata)
        => metadata?.GetValueOrDefault(MetadataKey) is { } value
            ? value as ProjectReference ?? JsonSerializer.Deserialize<ProjectReference>(JsonSerializer.Serialize(value)) : null;
}

// Keeps a detached clip in its original evaluation space while its timeline placement remains editable.
public sealed class ProjectReferencePart
{
    public const string MetadataKey = "__ProjectReferencePart";
    public ProjectReference Reference { get; set; } = new();
    public Guid InstanceId { get; set; }
    public string SourceId { get; set; } = "";
    public double SourceFrame { get; set; }
    public uint ParentInPoint { get; set; }
    public uint ParentFrameRate { get; set; }
    public int ParentWidth { get; set; }
    public int ParentHeight { get; set; }
    public int CanvasX { get; set; }
    public int CanvasY { get; set; }
    public int CanvasWidth { get; set; }
    public int CanvasHeight { get; set; }

    public static ProjectReferencePart? Read(Dictionary<string, object>? metadata)
        => metadata?.GetValueOrDefault(MetadataKey) is { } value
            ? value as ProjectReferencePart ?? JsonSerializer.Deserialize<ProjectReferencePart>(JsonSerializer.Serialize(value)) : null;
}
