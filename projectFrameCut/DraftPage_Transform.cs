using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.DraftStuff;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using projectFrameCut.ApplicationAPIBase.Project;
using projectFrameCut.Asset;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.Project;
using System.Text.Json;

namespace projectFrameCut;

public partial class DraftPage
{
    internal TransformSide SelectedTransformSide { get; set; }
    public ClipElementUI? _transformMenuActivatedCenterClip;
    public string _transformMenuActivatedHandle = "none";

    internal static bool SupportsPictureTransform(ClipElementUI clip) => clip.ClipType is not
        (ClipMode.AudioClip or ClipMode.MarkingClip or ClipMode.TransformClip) && !clip.IsGhost && !clip.IsShadow;
    internal static bool SupportsAudioTransform(ClipElementUI clip) => clip.ClipType == ClipMode.AudioClip && !clip.IsGhost && !clip.IsShadow;

    internal TransformClipInfo[] GetTransformClipInfos(bool audio = false) => Clips.Values.Where(c => audio ? SupportsAudioTransform(c) : SupportsPictureTransform(c))
        .Select(c => new TransformClipInfo(c.Id, PixelToFrame(Math.Max(0, c.Clip.TranslationX)),
            PixelToFrame(Math.Max(0, c.Clip.WidthRequest > 0 ? c.Clip.WidthRequest : c.origLength)),
            (uint)(c.origTrack ?? 0), (uint)c.SubLayerIndex,
            c.EffectProviders?.Values.Select(EffectBindingHelper.SerializeProvider).ToArray() ?? [])).ToArray();

    internal (ClipElementUI? left, ClipElementUI? right) FindTransformNeighbors(ClipElementUI clip, bool audio = false)
    {
        if (!audio) return FindNeighbors(clip);
        var infos = GetTransformClipInfos(audio: true);
        var own = infos.Single(c => c.Id == clip.Id);
        return (Find(c => c.End == own.Start), Find(c => c.Start == own.End));

        ClipElementUI? Find(Func<TransformClipInfo, bool> matches) => infos.Where(c => c.Id != own.Id && c.Layer == own.Layer &&
            c.SubLayer == own.SubLayer && matches(c)).OrderBy(c => c.Id).Select(c => Clips[c.Id]).FirstOrDefault();
    }

    internal async Task ShowTransformPanel(ClipElementUI clip, TransformSide side)
    {
        SelectedTransformSide = side;
        _transformMenuActivatedCenterClip = clip;
        _transformMenuActivatedHandle = side == TransformSide.Left ? "left" : "right";
        await SelectAClip(clip.Id);
        var panel = await BuildPropertyPanel(clip);
        panel.SelectByTag("transform");
        if (Popup.IsVisible) Popup.Content = panel;
        else RightContentBorder.Content = panel;
    }

    private async void HandleTransformAdd(ClipElementUI center, bool left, bool right)
    {
        if (!SupportsPictureTransform(center) && !SupportsAudioTransform(center)) return;
        try { await ShowTransformPanel(center, left ? TransformSide.Left : TransformSide.Right); }
        catch (Exception ex) { Log(ex, $"Open transform panel for {center.Id}", this); }
    }

    public bool AddTransformBetweenSelected(string typeKey, ClipElementUI? center, bool left, bool right) =>
        center is not null && TransformServices.GetAvailableTransforms(center.ClipType == ClipMode.AudioClip).TryGetValue(typeKey, out var factory)
        && AddTransformBetweenSelected(factory, center, left, right);

    public bool AddTransformBetweenSelected(Func<IEffectProvider> factory, ClipElementUI center, bool left, bool right,
        Action<ClipElementUI>? ElementSetter = null)
    {
        if (left == right || (!SupportsPictureTransform(center) && !SupportsAudioTransform(center))) return false;
        var side = left ? TransformSide.Left : TransformSide.Right;
        var provider = factory();
        var transform = TransformServices.Create(provider);
        try
        {
            var neighbors = FindTransformNeighbors(center, center.ClipType == ClipMode.AudioClip);
            var mode = (left ? neighbors.left : neighbors.right) is not null && transform.Definition.HasFlag(TransformDefinition.SupportTwoInput)
                ? TransformInputMode.TwoInput : TransformInputMode.OneInput;
            if (!SetClipTransform(center, side, mode, provider, Math.Max(1u, (uint)Math.Round(ProjectInfo.TargetFrameRate / 2d)))) return false;
            ElementSetter?.Invoke(center);
            return true;
        }
        finally { (transform as IDisposable)?.Dispose(); }
    }

    public void AddTransformToNeighbors(string type) => AddTransformBetweenSelected(type, _transformMenuActivatedCenterClip ?? _selected,
        _transformMenuActivatedHandle == "left", _transformMenuActivatedHandle == "right");

    internal bool SetClipTransform(ClipElementUI center, TransformSide side, TransformInputMode mode, IEffectProvider provider,
        uint duration, bool isAI = false, TransformRenderOrder? order = null)
    {
        bool audio = center.ClipType == ClipMode.AudioClip;
        var neighbors = FindTransformNeighbors(center, audio);
        var neighbor = side == TransformSide.Left ? neighbors.left : neighbors.right;
        var transform = TransformServices.Create(provider);
        try
        {
            var support = mode == TransformInputMode.OneInput ? TransformDefinition.SupportOneInput : TransformDefinition.SupportTwoInput;
            if (!(audio ? SupportsAudioTransform(center) : SupportsPictureTransform(center)) ||
                !transform.Definition.HasFlag(audio ? TransformDefinition.Audio : TransformDefinition.Clip) || !transform.Definition.HasFlag(support)
                || PixelToFrame(Math.Max(0, center.Clip.WidthRequest)) == 0 ||
                mode == TransformInputMode.TwoInput && (neighbor is null || !(audio ? SupportsAudioTransform(neighbor) : SupportsPictureTransform(neighbor)))) return false;
        }
        finally { (transform as IDisposable)?.Dispose(); }
        var owner = mode == TransformInputMode.TwoInput && side == TransformSide.Left ? neighbor! : center;
        var ownerSide = mode == TransformInputMode.TwoInput ? TransformSide.Right : side;
        TransformProcessing.Configure(provider, ownerSide, mode, Math.Max(1u, duration),
            mode == TransformInputMode.TwoInput ? (side == TransformSide.Left ? center.Id : neighbor!.Id) : Guid.Empty,
            order ?? TransformServices.DefaultRenderOrder, isAI);
        var infos = GetTransformClipInfos(audio);
        var replaced = new HashSet<Guid>();
        if (TransformProcessing.Find(infos, center.Id, side) is { } old) replaced.Add(old.Provider.Id);
        if (mode == TransformInputMode.TwoInput && TransformProcessing.Find(infos, neighbor!.Id,
            side == TransformSide.Left ? TransformSide.Right : TransformSide.Left) is { } other) replaced.Add(other.Provider.Id);
        var candidate = EffectBindingHelper.SerializeProvider(provider);
        candidate.Enabled = true;
        var proposed = infos.Select(c => c with { Providers = c.Providers.Where(p => !replaced.Contains(p.Id))
            .Concat(c.Id == owner.Id ? new[] { candidate } : []).ToArray() }).ToArray();
        var resolved = TransformProcessing.Resolve(proposed, center.Id, side);
        if (resolved is not { Duration: > 0 }) return false;
        ClearClipTransform(center, side);
        if (mode == TransformInputMode.TwoInput) ClearClipTransform(neighbor!, side == TransformSide.Left ? TransformSide.Right : TransformSide.Left);
        owner.EffectProviders ??= new();
        owner.EffectProviders[provider.Id] = provider;
        provider.MetaData[TransformProcessing.DurationKey] = resolved.Duration;
        Log($"Set {mode} transform {provider.Id} ({provider.TypeName}), {owner.Id} -> {TransformProcessing.ReadNextClip(provider.MetaData)}, {resolved.Duration} frames, {order ?? TransformServices.DefaultRenderOrder}.");
        _ = NotifyTransformChanged(owner);
        return true;
    }

    internal void ClearClipTransform(ClipElementUI clip, TransformSide side)
    {
        if (TransformProcessing.Find(GetTransformClipInfos(clip.ClipType == ClipMode.AudioClip), clip.Id, side) is { } found && Clips.TryGetValue(found.Owner.Id, out var owner))
        {
            owner.EffectProviders?.Remove(found.Provider.Id);
            ClipInfoBuilder.RebuildAllEffects(owner);
        }
    }

    internal async Task DeleteClipTransform(ClipElementUI clip, TransformSide side)
    {
        var found = TransformProcessing.Find(GetTransformClipInfos(clip.ClipType == ClipMode.AudioClip), clip.Id, side);
        ClearClipTransform(clip, side);
        if (found is { } f && Clips.TryGetValue(f.Owner.Id, out var owner)) await NotifyTransformChanged(owner);
    }

    internal Task NotifyTransformChanged(ClipElementUI owner)
    {
        ClipInfoBuilder.RebuildAllEffects(owner);
        MarkHistoryPanelDirty();
        RefreshTransformShadows();
        OnClipChanged?.Invoke(this, new ClipUpdateEventArgs { SourceId = owner.Id, SourceName = owner.DisplayName,
            Reason = ClipUpdateReason.PropertyChanged, DetailInfo = "Transform", NoSave = false });
        return Task.CompletedTask;
    }

    internal Task<string?> RenderTransformPreviewAsync(ClipElementUI center, TransformSide side, TransformInputMode mode,
        IEffectProvider provider, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var neighbor = side == TransformSide.Left ? FindNeighbors(center).left : FindNeighbors(center).right;
        if (!SupportsPictureTransform(center) || mode == TransformInputMode.TwoInput &&
            (neighbor is null || !SupportsPictureTransform(neighbor))) return Task.FromResult<string?>(null);
        var owner = mode == TransformInputMode.TwoInput && side == TransformSide.Left ? neighbor! : center;
        TransformProcessing.Configure(provider, mode == TransformInputMode.TwoInput ? TransformSide.Right : side, mode,
            Math.Max(1u, (uint)Math.Round(ProjectInfo.TargetFrameRate / 2d)),
            mode == TransformInputMode.TwoInput ? (side == TransformSide.Left ? center.Id : neighbor!.Id) : Guid.Empty,
            TransformServices.DefaultRenderOrder);
        var infos = GetTransformClipInfos();
        var replaced = new HashSet<Guid>();
        if (TransformProcessing.Find(infos, center.Id, side) is { } old) replaced.Add(old.Provider.Id);
        if (mode == TransformInputMode.TwoInput && TransformProcessing.Find(infos, neighbor!.Id,
            side == TransformSide.Left ? TransformSide.Right : TransformSide.Left) is { } other) replaced.Add(other.Provider.Id);
        var candidate = EffectBindingHelper.SerializeProvider(provider);
        candidate.Enabled = true;
        var proposed = infos.Select(c => c with { Providers = c.Providers.Where(p => !replaced.Contains(p.Id))
            .Concat(c.Id == owner.Id ? new[] { candidate } : []).ToArray() }).ToArray();
        var resolved = TransformProcessing.Resolve(proposed, center.Id, side);
        if (resolved is not { Duration: > 0 }) return Task.FromResult<string?>(null);
        candidate.MetaData[TransformProcessing.DurationKey] = resolved.Duration;
        var clips = (mode == TransformInputMode.TwoInput ? new[] { center, neighbor! } : new[] { center })
            .Select(c => DraftImportAndExportHelper.ExportClipElementFromDraftPage(this, c, false, rebuildEffects: false)).ToArray();
        foreach (var clip in clips)
            clip.EffectProviders = (clip.EffectProviders ?? []).Where(p => !replaced.Contains(p.Id))
                .Concat(clip.Id == owner.Id ? new[] { candidate } : []).ToArray();
        var request = new OpenProjectRequest
        {
            SessionId = Guid.NewGuid(),
            ProjectRoot = previewer.RenderProjectRoot ?? previewer.ProjectRoot,
            ProjectJson = JsonSerializer.Serialize(ProjectInfo, DraftJSONOption),
            TimelineJson = JsonSerializer.Serialize(new DraftStructureJSON { Clips = clips }, DraftJSONOption),
            ProxyRoot = previewer.RenderProxyRoot ?? previewer.ProxyRoot ?? string.Empty,
            ProjectWidth = Math.Max(1, ProjectInfo.RelativeWidth),
            ProjectHeight = Math.Max(1, ProjectInfo.RelativeHeight),
            FrameRate = Math.Max(1, (int)ProjectInfo.TargetFrameRate),
            Assets = previewer.RemoteAssets?.ToList() ?? AssetDatabase.Assets.Select(a => new AssetPathEntry
            {
                AssetId = a.Key, Path = a.Value.Path ?? string.Empty
            }).Where(a => !string.IsNullOrWhiteSpace(a.Path)).ToList()
        };
        request.AllowedExternalSources = ProjectExternalSourceService.GetApprovals(request.ProjectRoot);
        var client = previewer.RpcClient ?? RenderRpcBootstrap.Client;
        var resolver = previewer.ArtifactResolver;
        var localRoot = previewer.ProjectRoot;
        double scale = Math.Min(320d / request.ProjectWidth, 180d / request.ProjectHeight);
        var segment = new TimelineSegmentRequest
        {
            SessionId = request.SessionId,
            StartFrame = checked((uint)resolved.Start), Length = resolved.Duration,
            Width = Math.Max(2, (int)Math.Round(request.ProjectWidth * scale / 2) * 2),
            Height = Math.Max(2, (int)Math.Round(request.ProjectHeight * scale / 2) * 2),
            FrameRate = request.FrameRate, IncludeAudio = false
        };
        return Task.Run<string?>(async () =>
        {
            try
            {
                Log($"Render transform preview {provider.TypeName}/{side}: {owner.Id} -> {TransformProcessing.ReadNextClip(provider.MetaData)}, frames {segment.StartFrame}+{segment.Length}, {segment.Width}x{segment.Height}.");
                await client.OpenProjectAsync(request, token).ConfigureAwait(false);
                var artifact = await client.RenderTimelineSegmentAsync(segment, token).ConfigureAwait(false);
                return resolver is not null ? await resolver(artifact, token).ConfigureAwait(false)
                    : RenderRpcBootstrap.ResolveArtifactPath(localRoot, artifact);
            }
            finally
            {
                try { await client.CloseProjectAsync(request.SessionId).ConfigureAwait(false); }
                catch (Exception ex) { Log(ex, $"Close transform preview session {request.SessionId}", this); }
            }
        }, token);
    }

    private void RemoveClipTransformBindings(string clipId)
    {
        if (!Guid.TryParse(clipId, out var id)) return;
        foreach (var clip in Clips.Values)
        {
            var removed = (clip.EffectProviders?.Values ?? Enumerable.Empty<IEffectProvider>())
                .Where(p => p.TypeOfEffect == EffectType.Transform && (clip.Id == id || TransformProcessing.ReadNextClip(p.MetaData) == id)).ToArray();
            foreach (var provider in removed)
            {
                clip.EffectProviders!.Remove(provider.Id);
                LogDiagnostic($"Removed transform {provider.Id} referencing deleted clip {id}.");
            }
            if (removed.Length > 0) ClipInfoBuilder.RebuildAllEffects(clip);
        }
    }

    private void TransferSplitTransforms(ClipElementUI left, ClipElementUI right)
    {
        foreach (var provider in (right.EffectProviders?.Values ?? Enumerable.Empty<IEffectProvider>()).Where(p => p.TypeOfEffect == EffectType.Transform).ToArray())
            right.EffectProviders!.Remove(provider.Id);
        var trailing = left.EffectProviders?.Values.SingleOrDefault(p => p.TypeOfEffect == EffectType.Transform &&
            TransformProcessing.ReadEnum<TransformSide>(p.MetaData, TransformProcessing.SideKey) == TransformSide.Right);
        if (trailing is not null)
        {
            left.EffectProviders!.Remove(trailing.Id);
            right.EffectProviders ??= new();
            right.EffectProviders[trailing.Id] = trailing;
        }
        ClipInfoBuilder.RebuildAllEffects(left);
        ClipInfoBuilder.RebuildAllEffects(right);
    }

    internal void RefreshTransformShadows()
    {
#if DIAGHUB_ENABLE_TRACE_SYSTEM
        using var transformMark = new UserMarkRange("Timeline.RefreshTransformShadows", $"clips={Clips.Count}");
#endif
        var index = new TransformProcessing.Index(GetTransformClipInfos());
        var audioIndex = new TransformProcessing.Index(GetTransformClipInfos(audio: true));
        foreach (var clip in Clips.Values)
        {
            if (clip.Clip.Content is not Grid grid || (!SupportsPictureTransform(clip) && !SupportsAudioTransform(clip))) continue;
            foreach (var side in Enum.GetValues<TransformSide>())
            {
                string key = side == TransformSide.Left ? "LeftTransformShadow" : "RightTransformShadow";
                var old = grid.Children.OfType<View>().FirstOrDefault(v => v.ClassId == key);
                var resolved = (clip.ClipType == ClipMode.AudioClip ? audioIndex : index).Resolve(clip.Id, side);
                uint length = resolved is null ? 0 : TransformProcessing.ReadEnum<TransformInputMode>(resolved.Provider.MetaData, TransformProcessing.ModeKey) == TransformInputMode.OneInput
                    ? resolved.Duration : side == TransformSide.Left ? resolved.Duration / 2 + resolved.Duration % 2 : resolved.Duration / 2;
                if (length == 0)
                {
                    if (old is not null) grid.Children.Remove(old);
                    continue;
                }
                if (old is not null)
                {
                    if (Math.Abs(old.WidthRequest - FrameToPixel(length)) > 0.1) old.WidthRequest = FrameToPixel(length);
                    continue;
                }
                var shadow = new BoxView
                {
                    ClassId = key, ZIndex = 2, WidthRequest = FrameToPixel(length),
                    HorizontalOptions = side == TransformSide.Left ? LayoutOptions.Start : LayoutOptions.End,
                    VerticalOptions = LayoutOptions.Fill,
                    Background = new LinearGradientBrush
                    {
                        StartPoint = new Point(0, 0), EndPoint = new Point(1, 0),
                        GradientStops = new GradientStopCollection
                        {
                            new GradientStop(Color.FromArgb(side == TransformSide.Left ? "#99000000" : "#00000000"), 0),
                            new GradientStop(Color.FromArgb(side == TransformSide.Left ? "#00000000" : "#99000000"), 1)
                        }
                    }
                };
                var tap = new TapGestureRecognizer();
                tap.Tapped += async (_, _) => await ShowTransformPanel(clip, side);
                shadow.GestureRecognizers.Add(tap);
                Grid.SetColumnSpan(shadow, 3);
                grid.Children.Add(shadow);
            }
        }
    }
}
