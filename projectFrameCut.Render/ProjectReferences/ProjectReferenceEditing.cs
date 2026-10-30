using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Project;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace projectFrameCut.Render.ProjectReferences;

public static class ProjectReferenceEditing
{
    public static void Validate(ClipDraftDTO clip, string root)
    {
        var reference = ProjectReference.Read(clip.MetaData) ?? throw new InvalidDataException("Missing project reference configuration.");
        using var runtime = new ProjectReferenceRuntime(ProjectReferenceStore.Resolve(root, reference.SnapshotPath), clip.Id,
            IPicture.PicturePixelMode.Float16Picture);
    }

    public static ClipDraftDTO Create(ProjectReference reference, ProjectJSONStructure project, string root, uint start, uint layer)
    {
        var (source, draft) = ProjectReferenceStore.Load(ProjectReferenceStore.Resolve(root, reference.SnapshotPath));
        uint duration = Math.Max(draft.Duration, draft.AudioDuration);
        foreach (var clip in draft.Clips) duration = Math.Max(duration, checked(clip.StartFrame + clip.Duration));
        foreach (var track in draft.SoundTracks) duration = Math.Max(duration, checked(track.StartFrame + track.Duration));
        duration = Math.Max(1u, ProjectReferenceRuntime.ConvertFrame(duration, source.TargetFrameRate, project.TargetFrameRate));
        if ((ulong)start + duration > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(start));
        return new()
        {
            Id = Guid.NewGuid(), Name = source.ProjectName ?? Path.GetFileName(reference.SourcePath),
            ClipType = ClipMode.ProjectReferenceClip, TypeName = nameof(ProjectReferenceClip),
            FromPlugin = InternalPluginBase.InternalPluginBaseID,
            LayerIndex = layer, StartFrame = start, Duration = duration,
            FrameTime = 1f / Math.Max(1u, project.TargetFrameRate), SecondPerFrameRatio = 1,
            FilePath = reference.SnapshotPath, SourceDuration = duration,
            MetaData = new()
            {
                [ProjectReference.MetadataKey] = reference,
                [ClipDraftDTO.ProjectFrameRateMetaKey] = project.TargetFrameRate,
                [ClipDraftDTO.FrameSemanticVersionMetaKey] = ClipDraftDTO.CurrentFrameSemanticVersion,
            },
        };
    }

    public static DraftStructureJSON Detach(ClipDraftDTO group, ProjectJSONStructure parent, string root)
    {
        var reference = ProjectReference.Read(group.MetaData) ?? throw new InvalidDataException("Missing project reference configuration.");
        var pinned = new ProjectReference
        {
            Mode = ProjectReferenceMode.Snapshot, SnapshotPath = Path.GetRelativePath(root, ProjectReferenceStore.Resolve(root, reference.SnapshotPath)),
            ProjectId = reference.ProjectId, SnapshotId = reference.SnapshotId,
        };
        using var runtime = new ProjectReferenceRuntime(ProjectReferenceStore.Resolve(root, reference.SnapshotPath), group.Id,
            IPicture.PicturePixelMode.Float16Picture);
        uint fps = Math.Max(1u, parent.TargetFrameRate), sourceFps = runtime.Project.TargetFrameRate;
        int canvasWidth = group.TargetWidth > 0 ? group.TargetWidth : parent.RelativeWidth;
        int canvasHeight = group.TargetHeight > 0 ? group.TargetHeight : parent.RelativeHeight;
        double scale = Math.Min((double)canvasWidth / runtime.Project.RelativeWidth, (double)canvasHeight / runtime.Project.RelativeHeight);
        var clips = new List<ClipDraftDTO>();
        var tracks = new List<SoundtrackDTO>();
        var originalLayers = runtime.Draft.Clips.Where(c => c.ClipType != ClipMode.MarkingClip).Select(c => c.LayerIndex)
            .Concat(runtime.Draft.SoundTracks.Select(t => t.LayerIndex)).Distinct().Order().ToArray();
        uint firstLayer = originalLayers.FirstOrDefault();
        uint RestoreLayer(uint layer) => checked(group.LayerIndex + layer - firstLayer);
        bool Window(uint start, uint duration, out uint left, out uint length)
        {
            left = Math.Max(group.RelativeStartFrame, ProjectReferenceRuntime.ConvertFrame(start, sourceFps, fps, true));
            uint right = (uint)Math.Min((ulong)group.RelativeStartFrame + group.Duration,
                ProjectReferenceRuntime.ConvertFrame((ulong)start + duration, sourceFps, fps, true));
            length = right > left ? right - left : 0;
            return length > 0;
        }
        ProjectReferencePart Part(string id, uint left, uint inPoint) => new()
        {
            Reference = pinned, InstanceId = group.Id, SourceId = id,
            SourceFrame = (double)left * sourceFps / fps,
            ParentInPoint = inPoint, ParentFrameRate = fps, ParentWidth = parent.RelativeWidth, ParentHeight = parent.RelativeHeight,
            CanvasX = group.TargetX, CanvasY = group.TargetY, CanvasWidth = canvasWidth, CanvasHeight = canvasHeight,
        };
        foreach (var original in runtime.Draft.Clips.Where(c => c.ClipType is not (ClipMode.MarkingClip or ClipMode.AudioClip)))
        {
            var instance = runtime.Clips.FirstOrDefault(c => c.Id == original.Id);
            uint duration = instance?.GetEffectiveDuration() ?? original.Duration;
            if (!Window(original.StartFrame, duration, out uint left, out uint length)) continue;
            uint inPoint = checked(ProjectReferenceRuntime.ConvertFrame(original.RelativeStartFrame, sourceFps, fps)
                + left - ProjectReferenceRuntime.ConvertFrame(original.StartFrame, sourceFps, fps));
            var node = JsonSerializer.SerializeToNode(original, ProjectReferenceStore.JsonOptions)!;
            node["StartFrame"] = checked(group.StartFrame + left - group.RelativeStartFrame);
            node["RelativeStartFrame"] = inPoint;
            node["Duration"] = length;
            node["LayerIndex"] = RestoreLayer(original.LayerIndex);
            node["ShouldDisplayInUI"] = true;
            node["SourceDuration"] = Math.Max((ulong)inPoint + length,
                (ulong)Math.Max(0, original.SourceDuration ?? 0) * fps / sourceFps);
            node["TargetX"] = (int)Math.Round(group.TargetX + original.TargetX * scale + (canvasWidth - runtime.Project.RelativeWidth * scale) / 2);
            node["TargetY"] = (int)Math.Round(group.TargetY + original.TargetY * scale + (canvasHeight - runtime.Project.RelativeHeight * scale) / 2);
            node["TargetWidth"] = (int)Math.Round((original.TargetWidth > 0 ? original.TargetWidth : runtime.Project.RelativeWidth) * scale);
            node["TargetHeight"] = (int)Math.Round((original.TargetHeight > 0 ? original.TargetHeight : runtime.Project.RelativeHeight) * scale);
            var clip = node.Deserialize<ClipDraftDTO>(ProjectReferenceStore.JsonOptions)!;
            clip.MetaData ??= new();
            clip.MetaData[ClipDraftDTO.ProjectFrameRateMetaKey] = fps;
            if (original.ClipType == ClipMode.ProjectReferenceClip)
            {
                var nested = ProjectReference.Read(clip.MetaData)!;
                nested.SnapshotPath = Path.GetRelativePath(root, ProjectReferenceStore.Resolve(
                    ProjectReferenceStore.Resolve(root, reference.SnapshotPath), nested.SnapshotPath));
                nested.Mode = ProjectReferenceMode.Snapshot;
                nested.SourcePath = "";
                clip.MetaData[ProjectReference.MetadataKey] = nested;
                clip.FrameTime = 1f / fps;
            }
            else clip.MetaData[ProjectReferencePart.MetadataKey] = Part(clip.Id.ToString(), left, inPoint);
            clips.Add(clip);
        }
        var sourceTracks = runtime.Draft.SoundTracks.Select(t => (Track: t, SourceId: t.Id)).ToList();
        foreach (var track in runtime.Tracks.Where(t => !runtime.Draft.SoundTracks.Any(d => d.Id == t.Id)))
        {
            if (SoundTrackMetadata.ReadSourceClipId(track.ExtraData) is not { } sourceId) continue;
            string id = runtime.Draft.Clips.Any(c => c.Id == sourceId && c.ClipType == ClipMode.AudioClip)
                ? sourceId.ToString() : ProjectReferenceRuntime.MapId(group.Id, "detached-audio/" + track.Id).ToString();
            sourceTracks.Add((new SoundtrackDTO
            {
                Id = id, Name = track.Name, FromPlugin = track.FromPlugin, TypeName = "NormalTrack", TrackType = TrackMode.NormalTrack,
                FilePath = track.FilePath, StartFrame = track.StartFrame, RelativeStartFrame = track.RelativeStartFrame,
                Duration = track.Duration, LayerIndex = track.LayerIndex, SubLayerIndex = track.SubLayerIndex,
                SecondPerFrameRatio = track.Ratio, Effects = track.Effects, EffectProviders = track.EffectProviders,
                MetaData = new(track.ExtraData),
            }, sourceId.ToString()));
        }
        foreach (var (original, sourceId) in sourceTracks)
        {
            uint duration = (uint)Math.Round(original.Duration * (original.SecondPerFrameRatio > 0 ? original.SecondPerFrameRatio : 1));
            if (!Window(original.StartFrame, duration, out uint left, out uint length)) continue;
            uint inPoint = ProjectReferenceRuntime.ConvertFrame(original.RelativeStartFrame, sourceFps, fps)
                + left - ProjectReferenceRuntime.ConvertFrame(original.StartFrame, sourceFps, fps);
            var node = JsonSerializer.SerializeToNode(original, ProjectReferenceStore.JsonOptions)!;
            node["StartFrame"] = checked(group.StartFrame + left - group.RelativeStartFrame);
            node["RelativeStartFrame"] = inPoint; node["Duration"] = length;
            node["LayerIndex"] = RestoreLayer(original.LayerIndex); node["SecondPerFrameRatio"] = 1;
            node["ShouldDisplayInUI"] = true;
            var track = node.Deserialize<SoundtrackDTO>(ProjectReferenceStore.JsonOptions)!;
            track.MetaData ??= new();
            track.MetaData[ProjectReferencePart.MetadataKey] = Part(sourceId, left, inPoint);
            tracks.Add(track);
        }
        foreach (var clip in clips.Where(c => c.ClipType == ClipMode.VideoClip))
            if (tracks.FirstOrDefault(t => SoundTrackMetadata.ReadSourceClipId(t.MetaData) == clip.Id) is { } audio)
                clip.BindedSoundTrack = audio.Id;
        var detached = JsonSerializer.SerializeToNode(new DraftStructureJSON { Clips = clips.ToArray(), SoundTracks = tracks.ToArray() }, ProjectReferenceStore.JsonOptions)!;
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        ProjectReferenceStore.RewritePaths(detached, value => Path.IsPathRooted(value)
            && value.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? Path.GetRelativePath(root, value) : value);
        return detached.Deserialize<DraftStructureJSON>(ProjectReferenceStore.JsonOptions)!;
    }
}
