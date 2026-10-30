using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.ProjectReferences;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.RPCProtocol;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace projectFrameCut.Render.ClipsAndTracks;

public sealed class ProjectReferenceClip : MarkingClip
{
    private readonly object gate = new();
    private ProjectReferenceRuntime? runtime;
    private IClip[]? layers;
    public override ClipMode ClipType => ClipMode.ProjectReferenceClip;
    internal ProjectReferenceRuntime Runtime => runtime ?? throw new InvalidOperationException("Project reference is not initialized.");
    private uint ParentFps => ExtraData?.GetValueOrDefault(ClipDraftDTO.ProjectFrameRateMetaKey) is { } value
        && uint.TryParse(value.ToString(), out var fps) && fps > 0 ? fps
        : Math.Max(1u, (uint)Math.Round(1d / Math.Max(0.000001f, FrameTime)));

    public override void ReInit(IPicture.PicturePixelMode targetPPB)
    {
        lock (gate)
        {
            if (runtime is not null) return;
            if (ExtraData?.GetValueOrDefault("ProjectReferenceError") is { } error)
                throw new IOException(error.ToString());
            try
            {
                runtime = new ProjectReferenceRuntime(FilePath ?? throw new FileNotFoundException("Referenced snapshot path is missing."), Id, targetPPB);
                layers = runtime.Visual.OrderByDescending(c => c, ClipLayerOrder.BottomToTop)
                    .Select((clip, index) => CreateLayer(clip, (uint)index)).Where(c => c is not null).Cast<IClip>().ToArray();
            }
            catch { runtime?.Dispose(); runtime = null; throw; }
        }
    }

    private ReferenceLayerClip? CreateLayer(IClip source, uint index)
    {
        long start = source.ExtendToWholeDraft ? 0 : ProjectReferenceRuntime.ConvertFrame(source.StartFrame, Runtime.Project.TargetFrameRate, ParentFps, true);
        long end = ProjectReferenceRuntime.ConvertFrame(source.ExtendToWholeDraft ? Math.Max(Runtime.Draft.Duration, Runtime.Draft.AudioDuration)
            : (ulong)source.StartFrame + source.GetEffectiveDuration(), Runtime.Project.TargetFrameRate, ParentFps, true);
        long left = Math.Max(start, RelativeStartFrame);
        long right = Math.Min(end, (long)RelativeStartFrame + Duration);
        if (right <= left) return null;
        return new ReferenceLayerClip(Runtime, source, ParentFps)
        {
            Id = ProjectReferenceRuntime.MapId(source.Id, "layer"), Name = source.Name,
            LayerIndex = LayerIndex, SubLayerIndex = checked(SubLayerIndex + index),
            StartFrame = checked(StartFrame + (uint)(left - RelativeStartFrame)),
            RelativeStartFrame = (uint)left, Duration = (uint)(right - left),
            FrameTime = FrameTime, ExtraData = new(),
            TargetX = TargetX, TargetY = TargetY, TargetWidth = TargetWidth, TargetHeight = TargetHeight,
        };
    }

    public IClip[] GetLayers(bool ownsRuntime = false)
    {
        ReInit(IPicture.PicturePixelMode.Float16Picture);
        if (ownsRuntime) foreach (var layer in layers!.OfType<ReferenceLayerClip>()) layer.OwnsRuntime = true;
        return layers!;
    }

    public ISoundTrack[] GetSoundTracks(bool ownsRuntime = false)
    {
        ReInit(IPicture.PicturePixelMode.Float16Picture);
        return Runtime.Tracks.Length == 0 ? [] : [new ReferenceSoundTrack(Runtime, null, ownsRuntime: ownsRuntime)
        {
            Id = ProjectReferenceRuntime.MapId(Id, "audio").ToString(), Name = Name,
            LayerIndex = LayerIndex, SubLayerIndex = SubLayerIndex,
            StartFrame = StartFrame, RelativeStartFrame = RelativeStartFrame, Duration = Duration,
            ParentFrameRate = ParentFps, ExtraData = new(),
        }];
    }

    public override IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int requiredWidth, int requiredHeight, IPicture.PicturePixelMode targetPPB)
    {
        ReInit(targetPPB);
        var sourceFrame = (uint)Math.Floor((double)frameIndex * Runtime.Project.TargetFrameRate / ParentFps);
        return Runtime.Render(null, sourceFrame, requiredWidth, requiredHeight, targetPPB);
    }

    public override void Dispose()
    {
        lock (gate) { runtime?.Dispose(); runtime = null; layers = null; }
    }
}

internal sealed class ReferenceLayerClip(ProjectReferenceRuntime runtime, IClip source, uint parentFps) : MarkingClip
{
    private IMixture? mixture;
    public bool OwnsRuntime { get; set; }
    public override ClipMode ClipType => ClipMode.ProjectReferenceClip;
    public override void ReInit(IPicture.PicturePixelMode targetPPB)
        => MixtureInstance = source.MixtureInstance is null ? null : mixture ??= new ReferenceMixture(runtime, source,
            frame => (uint)Math.Floor(((double)RelativeStartFrame + (long)frame - StartFrame) * runtime.Project.TargetFrameRate / parentFps));
    public override void Dispose() { if (OwnsRuntime) runtime.Dispose(); }
    public override IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int requiredWidth, int requiredHeight, IPicture.PicturePixelMode targetPPB)
    {
        uint frame = (uint)Math.Floor((double)frameIndex * runtime.Project.TargetFrameRate / parentFps);
        ReInit(targetPPB);
        return runtime.Render(source, frame, requiredWidth, requiredHeight, targetPPB);
    }
}

// A detached native clip retains its source evaluation space for effects and keyframes.
internal sealed class ReferencePartClip : MarkingClip
{
    private readonly ClipDraftDTO dto;
    private readonly ProjectReferencePart part;
    private ProjectReferenceRuntime? runtime;
    private IClip? source;
    internal ClipDraftDTO Dto => dto;
    public ReferencePartClip(ClipDraftDTO dto, ProjectReferencePart part)
    {
        this.dto = dto; this.part = part;
        Id = dto.Id; Name = dto.Name; StartFrame = dto.StartFrame; RelativeStartFrame = dto.RelativeStartFrame;
        Duration = dto.Duration; LayerIndex = dto.LayerIndex; SubLayerIndex = dto.SubLayerIndex;
        FrameTime = dto.FrameTime; ExtraData = dto.MetaData ?? new(); FilePath = dto.FilePath;
        TargetX = part.CanvasX; TargetY = part.CanvasY;
        TargetWidth = part.CanvasWidth; TargetHeight = part.CanvasHeight;
    }
    public override ClipMode ClipType => dto.ClipType == ClipMode.AudioClip ? ClipMode.AudioClip : ClipMode.ProjectReferenceClip;
    public override void ReInit(IPicture.PicturePixelMode targetPPB)
    {
        if (runtime is not null) return;
        dto.FilePath = FilePath;
        runtime = new ProjectReferenceRuntime(part.Reference.SnapshotPath, ProjectReferenceRuntime.MapId(dto.Id, "detached"), targetPPB, dto, part);
        source = runtime.Visual.FirstOrDefault(c => c.Id.ToString() == runtime.PartSourceId);
        if (source?.MixtureInstance is not null) MixtureInstance = new ReferenceMixture(runtime, source,
            frame => (uint)Math.Clamp(part.SourceFrame + ((long)frame - StartFrame + RelativeStartFrame - part.ParentInPoint)
                * runtime.Project.TargetFrameRate / Math.Max(1u, part.ParentFrameRate), 0, uint.MaxValue));
    }
    public override IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int requiredWidth, int requiredHeight, IPicture.PicturePixelMode targetPPB)
    {
        ReInit(targetPPB);
        uint frame = (uint)Math.Clamp(part.SourceFrame + ((long)frameIndex - part.ParentInPoint)
            * runtime!.Project.TargetFrameRate / Math.Max(1u, part.ParentFrameRate), 0, uint.MaxValue);
        return runtime.Render(source ?? throw new InvalidDataException("Referenced clip is missing."), frame, requiredWidth, requiredHeight, targetPPB);
    }
    public override void Dispose() { runtime?.Dispose(); runtime = null; }
}

internal sealed class ReferenceMixture(ProjectReferenceRuntime runtime, IClip source, Func<uint, uint> mapFrame) : IMixture
{
    private IMixture Mixer => source.MixtureInstance!;
    public string FromPlugin => Mixer.FromPlugin;
    public string TypeName => "ProjectReferenceMixture";
    public bool IsDeterministic => false;
    public bool AcceptsHwAccelPictures => Mixer.AcceptsHwAccelPictures;
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "ProjectReferenceMixture";
    public EffectImplementType ImplementType => Mixer.ImplementType;
    public bool IsReorderable => false;
    public Dictionary<string, object> Parameters => Mixer.Parameters;
    public string? BindedEffectProvidingSystemID { get; set; }
    public IEffect WithParameters(Dictionary<string, object> parameters) => throw new NotSupportedException();
    public IPicture Mix(IPicture bottom, IPicture top, IPicture.PicturePixelMode ppb)
        => Mix(bottom, top, ppb, 0, 0, bottom.Width, bottom.Height);
    public IPicture Mix(IPicture bottom, IPicture top, IPicture.PicturePixelMode ppb, int x, int y, int width, int height)
    {
        uint frame = mapFrame((uint)Convert.ToSingle(ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInFrameProviderId) ?? 0f));
        using var clip = ValueProviderFrameContext.PushClip(source);
        using var context = ValueProviderFrameContext.PushFrame(frame,
            Math.Clamp((float)((long)frame - source.StartFrame) / Math.Max(1u, source.GetEffectiveDuration()), 0, 1));
        using var audio = EffectProcessing.OpenAudioFrame(runtime.Audio);
        using var endpoints = ClipValuePassThrough.Open(runtime.Visual, frame, width, height,
            runtime.Project.RelativeWidth, runtime.Project.RelativeHeight, ppb, ValueProviderFrameContext.CancellationToken, forceNew: true);
        return EffectPictureProcessing.Mix(Mixer, bottom, top, ppb, x, y, width, height);
    }
}

internal sealed class ReferenceSoundTrack(ProjectReferenceRuntime runtime, ISoundTrack? source, int sampleOffset = 0, bool ownsRuntime = false) : ISoundTrack
{
    private AudioSampleSource? audio;
    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public TrackMode TrackType => TrackMode.NormalTrack;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public uint LayerIndex { get; init; }
    public uint SubLayerIndex { get; init; }
    public uint StartFrame { get; init; }
    public uint RelativeStartFrame { get; init; }
    public uint Duration { get; init; }
    public uint ParentFrameRate { get; init; }
    public string? FilePath { get; set; }
    public bool NeedFilePath => false;
    public float Ratio { get; set; } = 1;
    public float Volume { get; set; } = 1;
    public int SamplePerSecond => 48000;
    public EffectAndMixtureJSONStructure[]? Effects { get; init; }
    public IEffect[]? EffectsInstances { get; set; }
    public Dictionary<string, object> ExtraData { get; set; } = new();
    public IReadOnlyList<Stage> SupportedStages => EffectStages.Audio;
    public IEffectStagePlan? EffectStagePlan { get; set; }
    public void ReInit() { }
    public void Dispose()
    {
        SoundTrackMetadata.ReleaseEffects(this);
        if (ownsRuntime) runtime.Dispose();
    }
    public IAudioSamples GetAudioSamplesRelatedToStartPointOfSource(uint startIndex, int length)
    {
        audio ??= source is null ? runtime.Audio : new AudioSampleSource(runtime.Clips, [source], (int)runtime.Project.TargetFrameRate);
        long start = (long)startIndex + sampleOffset;
        if (start >= 0) return audio.ReadSamples(checked((int)start), length, ValueProviderFrameContext.CancellationToken);
        var result = new FloatAudioSamples { Channels = [new float[length], new float[length]], SampleCount = length, SamplePerSecond = SamplePerSecond };
        int silence = (int)Math.Min(length, -start);
        if (silence < length)
        {
            var samples = audio.ReadSamples(0, length - silence, ValueProviderFrameContext.CancellationToken);
            for (int c = 0; c < 2; c++) samples.Channels[c].CopyTo(result.Channels[c], silence);
        }
        return result;
    }
}

public sealed class ProjectReferenceRuntime : IDisposable
{
    private int disposed;
    private static readonly AsyncLocal<HashSet<string>?> loading = new();
    private readonly IClip[] ownedClips;
    private readonly ISoundTrack[] ownedTracks;
    public ProjectJSONStructure Project { get; }
    public DraftStructureJSON Draft { get; }
    public IClip[] Clips => ownedClips;
    public IClip[] Visual { get; }
    public ISoundTrack[] Tracks { get; }
    public AudioSampleSource Audio { get; }
    internal string? PartSourceId { get; }

    internal ProjectReferenceRuntime(string root, Guid instanceId, IPicture.PicturePixelMode ppb,
        ClipDraftDTO? edited = null, ProjectReferencePart? part = null, SoundtrackDTO? editedTrack = null)
    {
        root = ProjectReferenceStore.Root(root);
        var chain = loading.Value ??= new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (!chain.Add(root)) throw new InvalidDataException("Circular project reference.");
        var clips = new List<IClip>();
        var tracks = new List<ISoundTrack>();
        try
        {
            (Project, Draft) = ProjectReferenceStore.Load(root);
            var node = JsonSerializer.SerializeToNode(Draft, ProjectReferenceStore.JsonOptions)!;
            var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Collect(JsonNode? value)
            {
                if (value is JsonObject obj)
                {
                    if (obj["Id"] is JsonValue id && id.TryGetValue<string>(out var text) && Guid.TryParse(text, out var guid) && guid != Guid.Empty)
                        ids.TryAdd(text, MapId(instanceId, text).ToString());
                    foreach (var child in obj) Collect(child.Value);
                }
                else if (value is JsonArray array) foreach (var child in array) Collect(child);
            }
            Collect(node);
            foreach (var track in Draft.SoundTracks.Where(t => !string.IsNullOrWhiteSpace(t.Id)))
                ids.TryAdd(track.Id, MapId(instanceId, track.Id).ToString());
            static string ReplaceIds(string value, IReadOnlyDictionary<string, string> replacements)
            {
                foreach (var id in replacements)
                    if (Guid.TryParse(id.Key, out var guid))
                    {
                        value = value.Replace(id.Key, id.Value, StringComparison.OrdinalIgnoreCase);
                        value = value.Replace(guid.ToString("N"), Guid.Parse(id.Value).ToString("N"), StringComparison.OrdinalIgnoreCase);
                    }
                    else if (value == id.Key) value = id.Value;
                return value;
            }
            if (part is not null)
            {
                var originalId = ids.Keys.FirstOrDefault(id => MapId(part.InstanceId, id).ToString() == part.SourceId);
                if (originalId is not null) PartSourceId = ids[originalId];
                var rebased = ids.ToDictionary(p => MapId(part.InstanceId, p.Key).ToString(), p => p.Value, StringComparer.OrdinalIgnoreCase);
                string Rebase(string value)
                {
                    return ReplaceIds(value, rebased);
                }
                if (edited is not null)
                {
                    var edit = JsonSerializer.SerializeToNode(edited, ProjectReferenceStore.JsonOptions)!;
                    ProjectReferenceStore.RewritePaths(edit, Rebase, Rebase);
                    edited = edit.Deserialize<ClipDraftDTO>(ProjectReferenceStore.JsonOptions)!;
                }
                if (editedTrack is not null)
                {
                    var edit = JsonSerializer.SerializeToNode(editedTrack, ProjectReferenceStore.JsonOptions)!;
                    ProjectReferenceStore.RewritePaths(edit, Rebase, Rebase);
                    editedTrack = edit.Deserialize<SoundtrackDTO>(ProjectReferenceStore.JsonOptions)!;
                }
            }
            ProjectReferenceStore.RewritePaths(node, value => ResolveExistingPath(root, value));
            ProjectReferenceStore.RewritePaths(node, value => ReplaceIds(value, ids), key => ReplaceIds(key, ids), preserveDetachedScopes: true);
            Draft = node.Deserialize<DraftStructureJSON>(ProjectReferenceStore.JsonOptions)!;
            EffectBindingHelper.NormalizeDraftProviders(Draft);
            foreach (var dto in Draft.Clips)
            {
                if (dto.ClipType == ClipMode.MarkingClip) continue;
                dto.MetaData ??= new();
                dto.MetaData[ClipDraftDTO.ProjectFrameRateMetaKey] = Project.TargetFrameRate;
                dto.MetaData[ClipDraftDTO.FrameSemanticVersionMetaKey] = ClipDraftDTO.CurrentFrameSemanticVersion;
                if (dto.ClipType == ClipMode.ProjectReferenceClip && ProjectReference.Read(dto.MetaData) is { } reference)
                    dto.FilePath = ProjectReferenceStore.Resolve(root, reference.SnapshotPath);
                if (dto.FilePath?.StartsWith('$') == true)
                    throw new FileNotFoundException($"Referenced asset '{dto.FilePath}' was not found.");
                BindPart(dto.MetaData, root);
                var previousPart = dto.MetaData.GetValueOrDefault(ProjectReferencePart.MetadataKey);
                if (edited is not null && part is not null && dto.Id.ToString() == PartSourceId)
                {
                    dto.Name = edited.Name; dto.FilePath = edited.FilePath;
                    dto.FrameTime = edited.FrameTime;
                    dto.Effects = edited.Effects; dto.EffectProviders = edited.EffectProviders;
                    dto.MetaData = new(edited.MetaData ?? new());
                    dto.MetaData.Remove(ProjectReferencePart.MetadataKey);
                    if (previousPart is not null) dto.MetaData[ProjectReferencePart.MetadataKey] = previousPart;
                    dto.MetaData[ClipDraftDTO.ProjectFrameRateMetaKey] = Project.TargetFrameRate;
                    dto.MetaData[ClipDraftDTO.FrameSemanticVersionMetaKey] = ClipDraftDTO.CurrentFrameSemanticVersion;
                    dto.StartingX = edited.StartingX; dto.StartingY = edited.StartingY;
                    int canvasWidth = part.CanvasWidth > 0 ? part.CanvasWidth : part.ParentWidth;
                    int canvasHeight = part.CanvasHeight > 0 ? part.CanvasHeight : part.ParentHeight;
                    double scale = Math.Min((double)canvasWidth / Project.RelativeWidth, (double)canvasHeight / Project.RelativeHeight);
                    dto.TargetX = (int)Math.Round((edited.TargetX - part.CanvasX - (canvasWidth - Project.RelativeWidth * scale) / 2) / scale);
                    dto.TargetY = (int)Math.Round((edited.TargetY - part.CanvasY - (canvasHeight - Project.RelativeHeight * scale) / 2) / scale);
                    dto.TargetWidth = edited.TargetWidth > 0 ? Math.Max(1, (int)Math.Round(edited.TargetWidth / scale)) : 0;
                    dto.TargetHeight = edited.TargetHeight > 0 ? Math.Max(1, (int)Math.Round(edited.TargetHeight / scale)) : 0;
                    dto.Rotation = edited.Rotation;
                }
                if (editedTrack is not null && dto.Id.ToString() == PartSourceId)
                {
                    dto.FilePath = editedTrack.FilePath;
                    dto.Effects = editedTrack.Effects; dto.EffectProviders = editedTrack.EffectProviders;
                    dto.MetaData = new(editedTrack.MetaData ?? new());
                    dto.MetaData.Remove(ProjectReferencePart.MetadataKey);
                    if (previousPart is not null) dto.MetaData[ProjectReferencePart.MetadataKey] = previousPart;
                    dto.MetaData[ClipDraftDTO.ProjectFrameRateMetaKey] = Project.TargetFrameRate;
                }
                var clip = PluginManager.CreateClip(JsonSerializer.SerializeToElement(dto, ProjectReferenceStore.JsonOptions));
                clips.Add(clip);
                clip.ReInit(ppb);
                clip.EffectsInstances = EffectHelper.GetClipEffectsInstances(clip);
                if (ClipInitializationFailure.IsMarked(clip))
                    throw new InvalidDataException($"Referenced clip '{clip.Name}': {ClipInitializationFailure.GetDescription(clip.ExtraData)}");
            }
            foreach (var dto in Draft.SoundTracks)
            {
                BindPart(dto.MetaData, root);
                var previousPart = dto.MetaData?.GetValueOrDefault(ProjectReferencePart.MetadataKey);
                if (editedTrack is not null && dto.Id == PartSourceId)
                {
                    dto.Effects = editedTrack.Effects; dto.EffectProviders = editedTrack.EffectProviders;
                    dto.FilePath = editedTrack.FilePath;
                    dto.MetaData = new(editedTrack.MetaData ?? new());
                    dto.MetaData.Remove(ProjectReferencePart.MetadataKey);
                    if (previousPart is not null) dto.MetaData[ProjectReferencePart.MetadataKey] = previousPart;
                }
                var track = PluginManager.CreateSoundTrack(JsonSerializer.SerializeToElement(dto, ProjectReferenceStore.JsonOptions));
                tracks.Add(track);
                track.ExtraData = dto.MetaData ?? new();
                track.Volume = editedTrack is not null && dto.Id == PartSourceId ? 1 : SoundTrackMetadata.ReadVolume(track.ExtraData);
                track.Ratio = dto.SecondPerFrameRatio > 0 ? dto.SecondPerFrameRatio : 1;
                if (SoundTrackMetadata.ReadBool(track.ExtraData, SoundTrackMetadata.EnabledKey, true)) SoundTrackMetadata.ReInit(track);
            }
            SoundTrackMetadata.AddMissingLegacyTracks(clips, tracks);
            if (editedTrack is not null)
                foreach (var track in tracks.Where(t => SoundTrackMetadata.ReadSourceClipId(t.ExtraData)?.ToString() == PartSourceId)) track.Volume = 1;
            ownedClips = clips.ToArray(); ownedTracks = tracks.ToArray();
            Visual = ExpandVisual(ownedClips);
            Tracks = ownedTracks.Concat(ownedClips.OfType<ProjectReferenceClip>().SelectMany(c => c.GetSoundTracks())).ToArray();
            Audio = new(ownedClips, Tracks, (int)Project.TargetFrameRate);
        }
        catch
        {
            foreach (var clip in clips) clip.Dispose();
            foreach (var track in tracks) track.Dispose();
            throw;
        }
        finally { chain.Remove(root); }
    }

    public static Guid MapId(Guid instance, string id) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"{instance:D}/{id}"))[..16]);
    public static uint ConvertFrame(ulong frame, uint from, uint to, bool roundUp = false)
        => checked((uint)(roundUp ? Math.Ceiling((double)frame * to / Math.Max(1u, from))
            : Math.Round((double)frame * to / Math.Max(1u, from), MidpointRounding.AwayFromZero)));

    public static void BindPart(Dictionary<string, object>? metadata, string root)
    {
        if (ProjectReferencePart.Read(metadata) is not { } part) return;
        part = JsonSerializer.SerializeToElement(part, ProjectReferenceStore.JsonOptions).Deserialize<ProjectReferencePart>(ProjectReferenceStore.JsonOptions)!;
        var node = JsonSerializer.SerializeToNode(metadata, ProjectReferenceStore.JsonOptions)!;
        ProjectReferenceStore.RewritePaths(node, value => ResolveExistingPath(root, value));
        var resolved = node.Deserialize<Dictionary<string, object>>(ProjectReferenceStore.JsonOptions)!;
        foreach (var value in resolved) metadata![value.Key] = value.Value;
        part.Reference.SnapshotPath = ProjectReferenceStore.Resolve(root, part.Reference.SnapshotPath);
        metadata![ProjectReferencePart.MetadataKey] = part;
    }

    private static string ResolveExistingPath(string root, string value)
    {
        if (value.StartsWith('#') || value.Contains("://") || string.IsNullOrWhiteSpace(value)) return value;
        try { return File.Exists(ProjectReferenceStore.Resolve(root, value)) ? ProjectReferenceStore.Resolve(root, value) : value; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return value; }
    }

    public static ISoundTrack CreateDetachedTrack(SoundtrackDTO dto, ProjectReferencePart part)
    {
        var runtime = new ProjectReferenceRuntime(part.Reference.SnapshotPath, MapId(Guid.TryParse(dto.Id, out var id) ? id : part.InstanceId, "detached"),
            IPicture.PicturePixelMode.Float16Picture, part: part, editedTrack: dto);
        var source = runtime.Tracks.FirstOrDefault(t => t.Id == runtime.PartSourceId
            || SoundTrackMetadata.ReadSourceClipId(t.ExtraData)?.ToString() == runtime.PartSourceId);
        if (source is null) { runtime.Dispose(); throw new InvalidDataException("Referenced soundtrack is missing."); }
        int offset = checked((int)Math.Round(((double)part.SourceFrame / runtime.Project.TargetFrameRate
            - (double)part.ParentInPoint / Math.Max(1u, part.ParentFrameRate)) * 48000));
        return new ReferenceSoundTrack(runtime, source, offset, true)
        {
            Id = dto.Id, Name = dto.Name, LayerIndex = dto.LayerIndex, SubLayerIndex = dto.SubLayerIndex,
            StartFrame = dto.StartFrame, RelativeStartFrame = dto.RelativeStartFrame, Duration = dto.Duration,
            ParentFrameRate = part.ParentFrameRate, ExtraData = dto.MetaData ?? new(),
        };
    }

    public static IClip[] ExpandVisual(IEnumerable<IClip> clips) => clips.Where(c => c.ClipType != ClipMode.AudioClip)
        .SelectMany(c => c is ProjectReferenceClip reference && !ClipInitializationFailure.IsMarked(c) ? reference.GetLayers() : [c]).ToArray();

    public static bool IsCanvasClip(IClip clip) => clip is ReferencePartClip or ReferenceLayerClip;

    internal IPicture Render(IClip? source, uint frame, int width, int height, IPicture.PicturePixelMode ppb)
    {
        var token = ValueProviderFrameContext.CancellationToken;
        double scale = Math.Min((double)width / Project.RelativeWidth, (double)height / Project.RelativeHeight);
        int w = Math.Max(1, (int)Math.Round(Project.RelativeWidth * scale));
        int h = Math.Max(1, (int)Math.Round(Project.RelativeHeight * scale));
        using var audio = EffectProcessing.OpenAudioFrame(Audio);
        using var clipScope = ValueProviderFrameContext.PushClip(source);
        using var context = ValueProviderFrameContext.PushFrame(frame, 0, false, token);
        using var masks = MaskFrameContext.Open(Visual, frame, w, h, Project.RelativeWidth,
            Project.RelativeHeight, ppb, token, forceNew: true);
        using var endpoints = ClipValuePassThrough.Open(Visual, frame, w, h, Project.RelativeWidth,
            Project.RelativeHeight, ppb, token, forceNew: true);
        IPicture picture;
        if (source is null)
            picture = Timeline.MixtureLayers(Timeline.GetFramesInOneFrame(Visual, frame, w, h, ppb,
                Project.RelativeWidth, Project.RelativeHeight, initializeClips: false), frame, w, h, (int)ppb,
                projectRelativeWidth: Project.RelativeWidth, projectRelativeHeight: Project.RelativeHeight,
                transparentBackground: true, disposeIntermediateFrames: true, cancellationToken: token, allClips: Visual);
        else if (!source.ContainsFrame(frame)) picture = Timeline.CreateTransparentFrame(w, h, ppb);
        else picture = ClipPreviewRenderer.Render(source, Visual, w, h, Project.RelativeWidth, Project.RelativeHeight,
            frame, token, ppb, initializeClips: false, canvasOutput: true, ignoreMixture: true)
            ?? Timeline.CreateTransparentFrame(w, h, ppb);
        if (w == width && h == height) return picture;
        using (picture)
        using (var canvas = Timeline.CreateTransparentFrame(width, height, ppb))
            return EffectPictureProcessing.Mix(ClassicOverlayMixture.Default, canvas, picture, ppb, (width - w) / 2, (height - h) / 2, width, height);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        foreach (var track in ownedTracks) track.Dispose();
        foreach (var clip in ownedClips) clip.Dispose();
    }
}
