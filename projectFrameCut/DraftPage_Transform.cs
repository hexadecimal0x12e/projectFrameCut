using ITransform = projectFrameCut.Render.RenderAPIBase.ClipAndTrack.ITransform;
using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.ApplicationAPIBase.Views.TabbedView;
using projectFrameCut.ApplicationAPIBase.Project;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Services;
using projectFrameCut.Shared;

namespace projectFrameCut;

public partial class DraftPage
{
    internal TransformSide SelectedTransformSide { get; set; }
    public ClipElementUI? _transformMenuActivatedCenterClip;
    public string _transformMenuActivatedHandle = "none";

    internal static bool SupportsPictureTransform(ClipElementUI clip) => clip.ClipType is not
        (ClipMode.AudioClip or ClipMode.MarkingClip or ClipMode.TransformClip) && !clip.IsGhost && !clip.IsShadow;

    internal TransformClipInfo[] GetTransformClipInfos() => Clips.Values.Where(SupportsPictureTransform)
        .Select(c => new TransformClipInfo(c.Id, PixelToFrame(Math.Max(0, c.Clip.TranslationX)),
            PixelToFrame(Math.Max(0, c.Clip.WidthRequest > 0 ? c.Clip.WidthRequest : c.origLength)),
            (uint)(c.origTrack ?? 0), (uint)c.SubLayerIndex, c.ExtraData)).ToArray();

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
        if (!SupportsPictureTransform(center)) return;
        try { await ShowTransformPanel(center, left ? TransformSide.Left : TransformSide.Right); }
        catch (Exception ex) { Log(ex, $"Open transform panel for {center.Id}", this); }
    }

    public bool AddTransformBetweenSelected(string typeKey, ClipElementUI? center, bool left, bool right) =>
        center is not null && TransformServices.GetAvailableTransforms().TryGetValue(typeKey, out var factory)
        && AddTransformBetweenSelected(factory, center, left, right);

    public bool AddTransformBetweenSelected(Func<Guid, Guid, ITransform> factory, ClipElementUI center, bool left, bool right,
        Action<ClipElementUI>? ElementSetter = null)
    {
        if (left == right || !SupportsPictureTransform(center)) return false;
        var side = left ? TransformSide.Left : TransformSide.Right;
        var neighbors = FindNeighbors(center);
        var neighbor = left ? neighbors.left : neighbors.right;
        var transform = factory(left ? neighbor?.Id ?? center.Id : center.Id, left ? center.Id : neighbor?.Id ?? Guid.Empty);
        var mode = neighbor is not null && transform.Definition.HasFlag(TransformDefinition.SupportTwoInput)
            ? TransformInputMode.TwoInput : TransformInputMode.OneInput;
        if (!SetClipTransform(center, side, mode, transform, Math.Max(1u, (uint)Math.Round(ProjectInfo.TargetFrameRate / 2d)))) return false;
        ElementSetter?.Invoke(center);
        return true;
    }

    public void AddTransformToNeighbors(string type) => AddTransformBetweenSelected(type, _transformMenuActivatedCenterClip ?? _selected,
        _transformMenuActivatedHandle == "left", _transformMenuActivatedHandle == "right");

    internal bool SetClipTransform(ClipElementUI center, TransformSide side, TransformInputMode mode, ITransform transform, uint duration, bool isAI = false)
    {
        var neighbor = side == TransformSide.Left ? FindNeighbors(center).left : FindNeighbors(center).right;
        var support = mode == TransformInputMode.OneInput ? TransformDefinition.SupportOneInput : TransformDefinition.SupportTwoInput;
        if (!SupportsPictureTransform(center) || !transform.Definition.HasFlag(TransformDefinition.Clip)
            || !transform.Definition.HasFlag(support) || (mode == TransformInputMode.TwoInput && (neighbor is null || !SupportsPictureTransform(neighbor)))) return false;
        var owner = mode == TransformInputMode.TwoInput && side == TransformSide.Left ? neighbor! : center;
        var ownerSide = mode == TransformInputMode.TwoInput ? TransformSide.Right : side;
        var binding = new TransformBinding
        {
            Side = ownerSide, InputMode = mode, Duration = Math.Max(1u, duration),
            LeftClipId = owner.Id, RightClipId = mode == TransformInputMode.TwoInput ? (side == TransformSide.Left ? center.Id : neighbor!.Id) : Guid.Empty,
            IsAI = isAI
        };
        transform.BindedLeftClip = binding.LeftClipId;
        transform.BindedRightClip = binding.RightClipId;
        transform.Side = binding.Side;
        transform.Duration = binding.Duration;
        transform.Init();
        binding.Capture(transform);
        var proposed = GetTransformClipInfos().Select(c => c with { Metadata = new Dictionary<string, object>(c.Metadata) }).ToArray();
        void ClearProposed(Guid id, TransformSide edge)
        {
            var found = ClipTransforms.Find(proposed, id, edge);
            if (found is { } f) TransformBinding.Write(f.Owner.Metadata, f.Binding.Side, null);
        }
        ClearProposed(center.Id, side);
        if (mode == TransformInputMode.TwoInput) ClearProposed(neighbor!.Id, side == TransformSide.Left ? TransformSide.Right : TransformSide.Left);
        TransformBinding.Write(proposed.First(c => c.Id == owner.Id).Metadata, ownerSide, binding);
        var resolved = ClipTransforms.Resolve(proposed, center.Id, side);
        if (resolved is null || resolved.Duration == 0) return false;
        binding.Duration = resolved.Duration;
        ClearClipTransform(center, side);
        if (mode == TransformInputMode.TwoInput) ClearClipTransform(neighbor!, side == TransformSide.Left ? TransformSide.Right : TransformSide.Left);
        TransformBinding.Write(owner.ExtraData, ownerSide, binding);
        Log($"Set {mode} transform {binding.Id} ({transform.TypeName}), {binding.LeftClipId} -> {binding.RightClipId}, {binding.Duration} frames.");
        _ = NotifyTransformChanged(owner);
        return true;
    }

    internal void ClearClipTransform(ClipElementUI clip, TransformSide side)
    {
        var found = ClipTransforms.Find(GetTransformClipInfos(), clip.Id, side);
        if (found is { } f && Clips.TryGetValue(f.Owner.Id, out var owner))
            TransformBinding.Write(owner.ExtraData, f.Binding.Side, null);
    }

    internal async Task DeleteClipTransform(ClipElementUI clip, TransformSide side)
    {
        var found = ClipTransforms.Find(GetTransformClipInfos(), clip.Id, side);
        ClearClipTransform(clip, side);
        if (found is { } f && Clips.TryGetValue(f.Owner.Id, out var owner)) await NotifyTransformChanged(owner);
    }

    internal Task NotifyTransformChanged(ClipElementUI owner)
    {
        MarkHistoryPanelDirty();
        RefreshTransformShadows();
        OnClipChanged?.Invoke(this, new ClipUpdateEventArgs { SourceId = owner.Id, SourceName = owner.DisplayName,
            Reason = ClipUpdateReason.PropertyChanged, DetailInfo = "Transform", NoSave = false });
        return Task.CompletedTask;
    }

    private void RemoveClipTransformBindings(string clipId)
    {
        if (!Guid.TryParse(clipId, out var id)) return;
        foreach (var clip in Clips.Values)
        {
            foreach (var side in Enum.GetValues<TransformSide>())
            {
                var binding = TransformBinding.Read(clip.ExtraData, side);
                if (binding is not null && (binding.LeftClipId == id || binding.RightClipId == id))
                {
                    TransformBinding.Write(clip.ExtraData, side, null);
                    LogDiagnostic($"Removed transform {binding.Id} referencing deleted clip {id}.");
                }
            }
        }
    }

    private void TransferSplitTransforms(ClipElementUI left, ClipElementUI right)
    {
        TransformBinding.Write(right.ExtraData, TransformSide.Left, null);
        var binding = TransformBinding.Read(left.ExtraData, TransformSide.Right);
        if (binding is null) return;
        TransformBinding.Write(left.ExtraData, TransformSide.Right, null);
        binding.LeftClipId = right.Id;
        TransformBinding.Write(right.ExtraData, TransformSide.Right, binding);
    }

    internal void RefreshTransformShadows()
    {
        var infos = GetTransformClipInfos();
        foreach (var clip in Clips.Values)
        {
            if (clip.Clip.Content is not Grid grid) continue;
            if (!SupportsPictureTransform(clip)) continue;
            foreach (var side in Enum.GetValues<TransformSide>())
            {
                string key = side == TransformSide.Left ? "LeftTransformShadow" : "RightTransformShadow";
                var old = grid.Children.OfType<View>().FirstOrDefault(v => v.ClassId == key);
                var resolved = ClipTransforms.Resolve(infos, clip.Id, side);
                if (resolved is null || resolved.Duration == 0)
                {
                    if (old is not null) grid.Children.Remove(old);
                    continue;
                }
                uint length = resolved.Binding.InputMode == TransformInputMode.OneInput ? resolved.Duration
                    : side == TransformSide.Left ? resolved.Duration / 2 + resolved.Duration % 2 : resolved.Duration / 2;
                if (length == 0)
                {
                    if (old is not null) grid.Children.Remove(old);
                    continue;
                }
                if (old is not null)
                {
                    old.WidthRequest = FrameToPixel(length);
                    continue;
                }
                var shadow = new BoxView
                {
                    ClassId = side == TransformSide.Left ? "LeftTransformShadow" : "RightTransformShadow",
                    ZIndex = 2, WidthRequest = FrameToPixel(length), HorizontalOptions = side == TransformSide.Left ? LayoutOptions.Start : LayoutOptions.End,
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
