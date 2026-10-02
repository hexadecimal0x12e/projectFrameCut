using projectFrameCut.Shared;
using projectFrameCut.ApplicationAPIBase.Project;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.Plugin;
using System.Text.Json.Nodes;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Render.VectorContent;
using projectFrameCut.Render.VectorContent.Components;
using projectFrameCut.Services;
using projectFrameCut.ApplicationAPIBase.Interaction;
using projectFrameCut.ApplicationAPIBase.VectorComponentHandler;
using System.Text.Json;
using projectFrameCut.Setting.SettingManager;

namespace projectFrameCut;

public partial class DraftPage
{
    #region componment
    private Task CombineVectorClipsAsync(List<ClipElementUI> selected)
    {
        var children = selected.Select(c => DraftImportAndExportHelper.ExportClipElementFromDraftPage(this, c))
            .OrderBy(c => c.LayerIndex).ThenBy(c => c.SubLayerIndex).ToList();
        uint start = children.Min(c => c.StartFrame);
        uint end = checked((uint)children.Max(c =>
        {
            using var instance = PluginManager.CreateClip(JsonSerializer.SerializeToElement(c));
            instance.ReInit(8);
            return (ulong)c.StartFrame + instance.GetEffectiveDuration();
        }));
        int layer = (int)children.Max(c => c.LayerIndex);
        foreach (var child in children) child.StartFrame -= start;
        var group = new ComponentGroup { Name = Localized.VectorContentEditorView_Components_GroupProperties };
        var clip = VectorClipServices.CreateClip(this, group, layer, FrameToPixel(start), end - start);
        clip.ExtraData[VectorComponentClip.ChildrenKey] = JsonSerializer.Serialize(children);
        int x = selected.Min(c => c.TargetX), y = selected.Min(c => c.TargetY);
        int width = Math.Max(1, selected.Max(c => c.TargetX + c.TargetWidth) - x);
        int height = Math.Max(1, selected.Max(c => c.TargetY + c.TargetHeight) - y);
        clip.TargetX = x; clip.TargetY = y; clip.TargetWidth = width; clip.TargetHeight = height;
        clip.ExtraData[VectorClipServices.ViewportKey] = new[] { x, y, width, height };
        group.SetInitialBounds((x + width / 2f) / ProjectInfo.RelativeWidth, (y + height / 2f) / ProjectInfo.RelativeHeight,
            width / (float)ProjectInfo.RelativeWidth, height / (float)ProjectInfo.RelativeHeight);
        group.Parameters["RelativeX"] = group.InitialRelativeX; group.Parameters["RelativeY"] = group.InitialRelativeY;
        group.Parameters["Width"] = group.InitialWidth; group.Parameters["Height"] = group.InitialHeight;
        VectorClipServices.SetDefinition(this, clip, [group], false);
        foreach (var child in selected) DeleteAClip(child, suppressClipChangedEvent: true);
        RegisterClip(clip, false);
        AddAClip(clip);
        ClearSelectionInternal();
        AddClipToSelection(clip);
        OnClipChanged?.Invoke(this, new ClipUpdateEventArgs { Reason = ClipUpdateReason.PropertyChanged, ChangedClipID = clip.Id, DetailInfo = Localized.VectorContentEditorView_History_Group });
        Log($"Merged {children.Count} vector clips into {clip.Id}, start={start}, duration={end - start}.");
        return RefreshSelectionUiAsync();
    }

    internal async Task UngroupVectorClipAsync(ClipElementUI clip)
    {
        var group = VectorComponentSerializer.Read(clip.ExtraData).OfType<ComponentGroup>().FirstOrDefault();
        if (group is null) return;
        var parent = DraftImportAndExportHelper.ExportClipElementFromDraftPage(this, clip);
        List<ClipDraftDTO> children = [];
        bool timedGroup = clip.ExtraData.TryGetValue(VectorComponentClip.ChildrenKey, out var raw);
        if (timedGroup)
            children = JsonSerializer.Deserialize<List<ClipDraftDTO>>(raw!.ToString()!) ?? [];
        if (timedGroup && (parent.RelativeStartFrame > 0 || parent.Duration < VectorComponentClip.ReadNumber(clip.ExtraData, VectorComponentClip.SourceLengthKey, parent.Duration)))
        {
            var visible = new List<ClipDraftDTO>();
            foreach (var child in children)
            {
                using var instance = PluginManager.CreateClip(JsonSerializer.SerializeToElement(child));
                instance.ReInit(8);
                uint first = Math.Max(child.StartFrame, parent.RelativeStartFrame);
                uint end = checked((uint)Math.Min((ulong)child.StartFrame + instance.GetEffectiveDuration(), (ulong)parent.RelativeStartFrame + parent.Duration));
                if (end <= first) continue;
                uint sourceStart = instance.TryGetRelativeFrameIndex(first, null) ?? child.RelativeStartFrame;
                uint sourceEnd = (instance.TryGetRelativeFrameIndex(end - 1, null) ?? sourceStart) + 1;
                var node = JsonSerializer.SerializeToNode(child)!;
                node["StartFrame"] = first - parent.RelativeStartFrame;
                node["RelativeStartFrame"] = sourceStart;
                node["Duration"] = Math.Max(1u, sourceEnd - sourceStart);
                visible.Add(node.Deserialize<ClipDraftDTO>()!);
            }
            children = visible;
        }
        var viewport = clip.ExtraData.TryGetValue(VectorClipServices.ViewportKey, out var bounds)
            ? JsonSerializer.Deserialize<int[]>(JsonSerializer.Serialize(bounds))! : new[] { 0, 0, ProjectInfo.RelativeWidth, ProjectInfo.RelativeHeight };
        float sx = clip.TargetWidth / (float)Math.Max(1, viewport[2]);
        float sy = clip.TargetHeight / (float)Math.Max(1, viewport[3]);
        if (!timedGroup)
        {
            foreach (var child in group.Children)
            {
                var source = VectorComponentSerializer.Clone(child);
                var wrapper = new VectorComponentWrapperClip(source) { ParentCanvasWidth = ProjectInfo.RelativeWidth, ParentCanvasHeight = ProjectInfo.RelativeHeight };
                wrapper.SyncFromDefinition();
                children.Add(new ClipDraftDTO
                {
                    Id = Guid.NewGuid(), FromPlugin = parent.FromPlugin, ClipType = ClipMode.VectorComponentClip,
                    TypeName = nameof(VectorComponentClip), Name = source.Name, LayerIndex = parent.LayerIndex,
                    RelativeStartFrame = parent.RelativeStartFrame, Duration = parent.Duration, FrameTime = parent.FrameTime,
                    TargetX = wrapper.TargetX, TargetY = wrapper.TargetY, TargetWidth = wrapper.TargetWidth, TargetHeight = wrapper.TargetHeight,
                    MetaData = new Dictionary<string, object>
                    {
                        [VectorComponentSerializer.ComponentsKey] = VectorComponentSerializer.Serialize([source]),
                        [VectorClipServices.ViewportKey] = new[] { wrapper.TargetX, wrapper.TargetY, wrapper.TargetWidth, wrapper.TargetHeight },
                        [VectorClipServices.CanvasWidthKey] = ProjectInfo.RelativeWidth,
                        [VectorClipServices.CanvasHeightKey] = ProjectInfo.RelativeHeight,
                        [VectorComponentClip.SourceLengthKey] = clip.ExtraData.GetValueOrDefault(VectorComponentClip.SourceLengthKey, parent.Duration)
                    }
                });
            }
        }
        if (!timedGroup)
        {
            var transform = (ComponentGroup)VectorComponentSerializer.Clone(group);
            transform.SetChildren([]);
            foreach (var child in children)
            {
                var data = child.MetaData!;
                var transforms = data.TryGetValue(VectorComponentClip.TransformsKey, out var existing)
                    ? VectorComponentSerializer.Read(new Dictionary<string, object> { [VectorComponentSerializer.ComponentsKey] = existing }) : [];
                transforms.Add(transform);
                data[VectorComponentClip.TransformsKey] = VectorComponentSerializer.Serialize(transforms);
                var ui = ClipElementUI.CreateClip(0, 1, 0);
                ui.ExtraData = data; ui.lengthInFrame = child.Duration;
                ui.TargetX = child.TargetX; ui.TargetY = child.TargetY; ui.TargetWidth = child.TargetWidth; ui.TargetHeight = child.TargetHeight;
                VectorClipServices.SetDefinition(this, ui, VectorComponentSerializer.Read(data), true);
                child.TargetX = ui.TargetX; child.TargetY = ui.TargetY; child.TargetWidth = ui.TargetWidth; child.TargetHeight = ui.TargetHeight;
            }
        }
        foreach (var child in children)
        {
            child.StartFrame = checked(parent.StartFrame + child.StartFrame);
            float x = (child.TargetX + child.TargetWidth / 2f - viewport[0] - viewport[2] / 2f) * sx;
            float y = (child.TargetY + child.TargetHeight / 2f - viewport[1] - viewport[3] / 2f) * sy;
            float rotation = timedGroup ? group.Parameters.GetFloat("Rotation", 0) + VectorComponentClip.ReadNumber(clip.ExtraData, VectorComponentClip.RotationKey, 0) : VectorComponentClip.ReadNumber(clip.ExtraData, VectorComponentClip.RotationKey, 0);
            child.TargetWidth = Math.Max(1, (int)Math.Round(child.TargetWidth * sx));
            child.TargetHeight = Math.Max(1, (int)Math.Round(child.TargetHeight * sy));
            child.TargetX = (int)Math.Round(clip.TargetX + clip.TargetWidth / 2f + x * MathF.Cos(rotation) - y * MathF.Sin(rotation) - child.TargetWidth / 2f);
            child.TargetY = (int)Math.Round(clip.TargetY + clip.TargetHeight / 2f + x * MathF.Sin(rotation) + y * MathF.Cos(rotation) - child.TargetHeight / 2f);
            child.MetaData ??= new();
            if (rotation != 0) child.MetaData[VectorComponentClip.RotationKey] = VectorComponentClip.ReadNumber(child.MetaData, VectorComponentClip.RotationKey, 0) + rotation;
        }
        if (children.Count == 0) return;
        foreach (var child in children)
            if (!Tracks.ContainsKey((int)child.LayerIndex)) AddATrack((int)child.LayerIndex);
        DeleteAClip(clip, suppressClipChangedEvent: true);
        foreach (var child in children)
        {
            if (Clips.ContainsKey(child.Id)) child.Id = Guid.NewGuid();
            var imported = DraftImportAndExportHelper.ImportFromJSON(new DraftStructureJSON { Clips = [child] }, ProjectInfo).Item1[child.Id];
            imported.Clip.TranslationX = FrameToPixel(child.StartFrame);
            imported.Clip.WidthRequest = FrameToPixel(child.Duration);
            imported.origLength = imported.Clip.WidthRequest;
            imported.origX = imported.Clip.TranslationX;
            RegisterClip(imported, false);
            AddAClip(imported);
        }
        OnClipChanged?.Invoke(this, new ClipUpdateEventArgs { Reason = ClipUpdateReason.PropertyChanged, DetailInfo = Localized.VectorContentEditorView_History_Ungroup });
        Log($"Ungrouped vector clip {clip.Id} into {children.Count} clips; group effects discarded.");
        await RefreshSelectionUiAsync();
    }

    internal void NotifyVectorAssetChanged()
        => OnClipChanged?.Invoke(this, new ClipUpdateEventArgs { Reason = ClipUpdateReason.PropertyChanged, DetailInfo = Localized.VectorContentEditorView_ExportShapes });
    #endregion

    #region handles
    private sealed class VectorHandleOrigin
    {
        public required IVectorComponent Component { get; init; }
        public required IVectorComponentHandler Handler { get; init; }
        public required string Data { get; init; }
        public required int[] Viewport { get; init; }
        public required ClipPositionTuple Bounds { get; init; }
        public float X { get; init; }
        public float Y { get; init; }
        public float A { get; init; }
        public float B { get; init; }
        public float C { get; init; }
        public float D { get; init; }
        public double DisplayW { get; init; }
        public double DisplayH { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public double TotalX { get; set; }
        public double TotalY { get; set; }
        public bool Pending { get; set; }
        public bool PreviewPending { get; set; }
        public Task? PreviewTask { get; set; }
        public Task? OverlayTask { get; set; }
        public View? PreviewView { get; set; }
        public bool LivePreviewUnavailable { get; set; }
        public bool AccelerateControlUpdates { get; init; }
        public CancellationTokenSource PreviewCancellation { get; } = new();
    }
    private readonly Dictionary<(Guid, string), VectorHandleOrigin> vectorHandleOrigins = new();
    private sealed record VectorHandleDefinition(Guid ClipId, string Data, IVectorComponent Component, IVectorComponentHandler? Handler);
    private VectorHandleDefinition? vectorHandleDefinition;
    private Microsoft.Maui.Dispatching.IDispatcherTimer? vectorHandleTimer;

    private void ConfigureVectorHandles()
    {
        ClipEditor.CustomHandleUpdatesManagedExternally = true;
        ClipEditor.ConfigureCustomHandles(GetVectorHandles, OnVectorHandleDrag);
        vectorHandleTimer ??= Dispatcher.CreateTimer();
        vectorHandleTimer.Interval = TimeSpan.FromMilliseconds(16);
        vectorHandleTimer.Tick -= UpdatePendingVectorHandles;
        vectorHandleTimer.Tick += UpdatePendingVectorHandles;
    }

    private IVectorComponent? GetEditableVectorComponent(Guid id)
    {
        if (!Clips.TryGetValue(id, out var clip) || clip.ClipType != ClipMode.VectorComponentClip || clip.ExtraData.ContainsKey(VectorComponentClip.ChildrenKey)) return null;
        string? data = clip.ExtraData.GetValueOrDefault(VectorComponentSerializer.ComponentsKey)?.ToString();
        if (data is null) return null;
        if (vectorHandleDefinition is { } definition && definition.ClipId == id && definition.Data == data)
            return definition.Component;
        var components = VectorComponentSerializer.Read(clip.ExtraData);
        if (components.Count != 1) return null;
        var component = components[0];
        if (component is ComponentGroup) return null;
        vectorHandleDefinition = new VectorHandleDefinition(id, data, component, VectorClipServices.GetHandler(component));
        return component;
    }

    private IReadOnlyList<ShapeHandleDescriptor> GetVectorHandles(Guid id)
    {
        var origin = vectorHandleOrigins.FirstOrDefault(p => p.Key.Item1 == id).Value;
        var component = origin?.Component ?? GetEditableVectorComponent(id);
        if (component is null || !Clips.TryGetValue(id, out var clip)) return [];
        var handler = origin?.Handler ?? vectorHandleDefinition?.Handler;
        if (handler is null) return [];
        clip.ShowDefaultHandles = handler.HasDefaultHandles;
        var element = component.Compute();
        var viewport = VectorComponentClip.ReadViewport(clip.ExtraData, clip.TargetWidth, clip.TargetHeight);
        var zero = VectorHandlePoint(clip, element, 0, 0, viewport);
        var x = VectorHandlePoint(clip, element, 1, 0, viewport);
        var y = VectorHandlePoint(clip, element, 0, 1, viewport);
        return handler.CreateHandles(component).Select(h =>
        {
            var point = (X: zero.X + h.NormalizedX * (x.X - zero.X) + h.NormalizedY * (y.X - zero.X),
                Y: zero.Y + h.NormalizedX * (x.Y - zero.Y) + h.NormalizedY * (y.Y - zero.Y));
            var color = h.PositionType switch
            {
                ShapeHandlePositionType.Control => Color.FromRgba(255, 235, 59, 230),
                ShapeHandlePositionType.Radius => Color.FromRgba(0, 188, 212, 230),
                ShapeHandlePositionType.Center => Color.FromRgba(244, 67, 54, 230),
                ShapeHandlePositionType.Angle => Color.FromRgba(233, 30, 99, 230),
                ShapeHandlePositionType.Corner => Color.FromRgba(0, 150, 136, 230),
                _ => Color.FromRgba(255, 152, 0, 230)
            };
            return new ShapeHandleDescriptor(h.Id, (point.X - viewport[0]) / Math.Max(1, viewport[2]),
                (point.Y - viewport[1]) / Math.Max(1, viewport[3]), color,
                h.PositionType is ShapeHandlePositionType.Anchor or ShapeHandlePositionType.Center ? 14 : 12, h.CustomHandleFactory)
            {
                PositionType = h.PositionType
            };
        }).ToArray();
    }

    private sealed class VectorHandlePointElement : VectorCanvasElement
    {
        public float X { get; init; }
        public float Y { get; init; }
        public override VectorSegment[] Draw() => [new StraightLineVectorSegment { X1 = X, Y1 = Y, X2 = X, Y2 = Y }];
    }

    private (float X, float Y) VectorHandlePoint(ClipElementUI clip, VectorCanvasElement element, float x, float y, int[] viewport)
    {
        var point = new VectorHandlePointElement
        {
            X = x, Y = y,
            RelativeX = element.RelativeX, RelativeY = element.RelativeY,
            BaseX = element.BaseX, BaseY = element.BaseY,
            Rotation = element.Rotation, UseUniformScale = element.UseUniformScale
        };
        var transformed = VectorComponentClip.ApplyInheritedTransforms(clip.ExtraData, [point]).First();
        VectorCanvasElement local = new VectorViewportElement(transformed,
            VectorComponentClip.ReadNumber(clip.ExtraData, VectorClipServices.CanvasWidthKey, ProjectInfo.RelativeWidth),
            VectorComponentClip.ReadNumber(clip.ExtraData, VectorClipServices.CanvasHeightKey, ProjectInfo.RelativeHeight),
            viewport[0], viewport[1], Math.Max(1, viewport[2]), Math.Max(1, viewport[3]));
        float rotation = VectorComponentClip.ReadNumber(clip.ExtraData, VectorComponentClip.RotationKey, 0);
        if (rotation != 0) local = VectorComponentClip.RotateViewport([local], viewport[2], viewport[3], rotation)[0];
        var segment = (StraightLineVectorSegment)local.Draw()[0];
        return (viewport[0] + (local.RelativeX + segment.X1) * viewport[2],
            viewport[1] + (local.RelativeY + segment.Y1) * viewport[3]);
    }

    private void OnVectorHandleDrag(Guid id, string handleId, PanUpdatedEventArgs e, ShapeHandleDragContext context)
    {
        var key = (id, handleId);
        if (!Clips.TryGetValue(id, out var clip))
        {
            EndVectorHandleDrag(key);
            return;
        }
        if (e.StatusType == GestureStatus.Running && (!double.IsFinite(e.TotalX) || !double.IsFinite(e.TotalY))) return;
        if (e.StatusType == GestureStatus.Started)
        {
            if (AlreadyDisappeared || IsReadonly) return;
            CancelVectorHandleDrags();
            var component = GetEditableVectorComponent(id);
            if (component is null) return;
            var handler = vectorHandleDefinition?.Handler;
            var handle = handler?.CreateHandles(component).FirstOrDefault(h => h.Id == handleId);
            if (handler is null || handle is null) return;
            var element = component.Compute();
            var viewport = VectorComponentClip.ReadViewport(clip.ExtraData, clip.TargetWidth, clip.TargetHeight);
            var zero = VectorHandlePoint(clip, element, 0, 0, viewport);
            var x = VectorHandlePoint(clip, element, 1, 0, viewport);
            var y = VectorHandlePoint(clip, element, 0, 1, viewport);
            float determinant = (x.X - zero.X) * (y.Y - zero.Y) - (y.X - zero.X) * (x.Y - zero.Y);
            if (!float.IsFinite(determinant) || MathF.Abs(determinant) < 0.000001f
                || !double.IsFinite(context.DisplayW) || !double.IsFinite(context.DisplayH)
                || context.DisplayW <= 0 || context.DisplayH <= 0)
            {
                Log($"Cannot drag vector handle {handleId} on clip {id}: invalid transform or display size.");
                return;
            }
            vectorHandleOrigins[key] = new VectorHandleOrigin
            {
                Component = component,
                Handler = handler,
                Data = VectorComponentSerializer.Serialize([component]),
                Viewport = viewport,
                Bounds = new ClipPositionTuple(clip.TargetX, clip.TargetY, clip.TargetWidth, clip.TargetHeight, false),
                X = handle.NormalizedX,
                Y = handle.NormalizedY,
                A = x.X - zero.X,
                B = y.X - zero.X,
                C = x.Y - zero.Y,
                D = y.Y - zero.Y,
                Width = viewport[2],
                Height = viewport[3],
                DisplayW = context.DisplayW,
                DisplayH = context.DisplayH,
                AccelerateControlUpdates = SettingsManager.IsBoolSettingTrue("Edit_AccelerateControlUpdates")
            };
            DynamicPreviewProvider.CancelClipWarmup(id);
            DraftImportAndExportHelper.ExportClipElementFromDraftPage(this, clip);
            CancelDynamicPreview();
            lock (_renderCtsLock) _renderOneFrameCts?.Cancel();
            vectorHandleTimer?.Start();
            UpdateVectorHandlePreview(clip, vectorHandleOrigins[key]);
            if (!UseDynamicPreview)
                vectorHandleOrigins[key].OverlayTask = PrepareVectorHandleOverlayAsync(key, vectorHandleOrigins[key]);
            Log($"Started vector handle drag: clip={id}, component={component.TypeName}, handle={handleId}, immediateUpdates={vectorHandleOrigins[key].AccelerateControlUpdates}.");
            return;
        }
        if (!vectorHandleOrigins.TryGetValue(key, out var origin)) return;
        if (e.StatusType == GestureStatus.Running)
        {
            origin.TotalX = e.TotalX;
            origin.TotalY = e.TotalY;
            origin.Pending = true;
            if (origin.AccelerateControlUpdates) UpdatePendingVectorHandles(null, EventArgs.Empty);
            return;
        }
        RestoreVectorHandleBounds(clip, origin);
        if (e.StatusType == GestureStatus.Canceled)
        {
            clip.ExtraData[VectorComponentSerializer.ComponentsKey] = origin.Data;
        }
        else
        {
            ApplyVectorHandlePosition(clip, handleId, origin, false);
        }
        EndVectorHandleDrag(key);
        OnClipChanged?.Invoke(this, new ClipUpdateEventArgs
        {
            Reason = ClipUpdateReason.PropertyChanged,
            ChangedClipID = id,
            NoSave = e.StatusType != GestureStatus.Completed,
            DetailInfo = Localized.VectorContentEditorView_Properties
        });
        if (e.StatusType is GestureStatus.Completed or GestureStatus.Canceled)
        {
            if (SelectedClip?.Id == id) RefreshPropertyPanel(clip);
            Log($"{e.StatusType} vector handle drag: clip={id}, handle={handleId}, delta=({origin.TotalX:F2}, {origin.TotalY:F2}).");
        }
    }

    private static void RestoreVectorHandleBounds(ClipElementUI clip, VectorHandleOrigin origin)
    {
        clip.TargetX = origin.Bounds.TargetX;
        clip.TargetY = origin.Bounds.TargetY;
        clip.TargetWidth = origin.Bounds.TargetWidth;
        clip.TargetHeight = origin.Bounds.TargetHeight;
        clip.ExtraData[VectorClipServices.ViewportKey] = origin.Viewport;
    }

    private void ApplyVectorHandlePosition(ClipElementUI clip, string handleId, VectorHandleOrigin origin, bool live)
    {
        RestoreVectorHandleBounds(clip, origin);
        float dx = (float)(origin.TotalX * origin.Width / origin.DisplayW);
        float dy = (float)(origin.TotalY * origin.Height / origin.DisplayH);
        float determinant = origin.A * origin.D - origin.B * origin.C;
        origin.Handler.ApplyHandleDrag(origin.Component, handleId,
            origin.X + (dx * origin.D - dy * origin.B) / determinant,
            origin.Y + (dy * origin.A - dx * origin.C) / determinant, live);
        VectorClipServices.SetDefinition(this, clip, [origin.Component], true, origin.Handler);
        origin.Pending = false;
    }

    private void UpdatePendingVectorHandles(object? sender, EventArgs e)
    {
        foreach (var (key, origin) in vectorHandleOrigins.ToArray())
        {
            Clips.TryGetValue(key.Item1, out var clip);
            if (AlreadyDisappeared || SelectedClip?.Id != key.Item1 || clip is null)
            {
                if (clip is not null) RestoreVectorHandleDefinition(clip, origin);
                EndVectorHandleDrag(key);
                if (!AlreadyDisappeared) _ = RefreshPreviewFromCurrentProviderAsync();
                continue;
            }
            if (!origin.Pending) continue;
            try
            {
                ApplyVectorHandlePosition(clip, key.Item2, origin, true);
                ClipEditor.RefreshCustomHandleVisuals(key.Item1);
                if (UpdateVectorHandlePreview(clip, origin)) continue;
                origin.PreviewPending = true;
                if (origin.PreviewTask is null || origin.PreviewTask.IsCompleted)
                    origin.PreviewTask = RefreshVectorHandlePreviewAsync(key, origin);
            }
            catch (Exception ex)
            {
                RestoreVectorHandleDefinition(clip, origin);
                EndVectorHandleDrag(key);
                Log(ex, $"Update vector handle {key.Item2} on clip {key.Item1}", this);
                _ = RefreshPreviewFromCurrentProviderAsync();
            }
        }
    }

    private bool UpdateVectorHandlePreview(ClipElementUI clip, VectorHandleOrigin origin)
    {
        if (origin.LivePreviewUnavailable) return false;
        try
        {
            var viewport = VectorComponentClip.ReadViewport(clip.ExtraData, clip.TargetWidth, clip.TargetHeight);
            var elements = VectorComponentClip.ApplyInheritedTransforms(clip.ExtraData, origin.Component.ComputeAll())
                .Select(e => (VectorCanvasElement)new VectorViewportElement(e,
                    VectorComponentClip.ReadNumber(clip.ExtraData, VectorClipServices.CanvasWidthKey, ProjectInfo.RelativeWidth),
                    VectorComponentClip.ReadNumber(clip.ExtraData, VectorClipServices.CanvasHeightKey, ProjectInfo.RelativeHeight),
                    viewport[0], viewport[1], Math.Max(1, viewport[2]), Math.Max(1, viewport[3]))).ToArray();
            float rotation = VectorComponentClip.ReadNumber(clip.ExtraData, VectorComponentClip.RotationKey, 0);
            if (rotation != 0) elements = VectorComponentClip.RotateViewport(elements, viewport[2], viewport[3], rotation).ToArray();
            origin.PreviewView = origin.Handler.GetHandlePreview(origin.Component,
                new VectorHandlePreviewContext(elements, viewport[2], viewport[3]), origin.PreviewView);
            if (origin.PreviewView is null)
            {
                origin.LivePreviewUnavailable = true;
                ClipEditor.EndCustomHandleLivePreview(clip.Id);
                Log($"Vector handler {origin.Component.TypeName} has no live view for clip {clip.Id}; using backend preview.");
                return false;
            }
            ClipEditor.ApplyCustomHandleLivePreview(clip.Id, origin.PreviewView);
            return true;
        }
        catch (Exception ex)
        {
            origin.LivePreviewUnavailable = true;
            origin.PreviewView = null;
            ClipEditor.EndCustomHandleLivePreview(clip.Id);
            Log(ex, $"Create live vector handle preview for clip {clip.Id}; using backend preview", this);
            return false;
        }
    }

    private async Task PrepareVectorHandleOverlayAsync((Guid, string) key, VectorHandleOrigin origin)
    {
        await RefreshDynamicPreviewOverlay();
        if (AlreadyDisappeared || !vectorHandleOrigins.TryGetValue(key, out var active) || !ReferenceEquals(active, origin)) return;
        ClipEditor.ShowClipPreviewOverlays = true;
        ClipEditor.SetStaticPreviewVisible(false);
        ClipEditor.SetRealtimePreviewContent(EnsureRealtimePreviewHost());
        if (Clips.TryGetValue(key.Item1, out var clip)) UpdateVectorHandlePreview(clip, origin);
    }

    private async Task RefreshVectorHandlePreviewAsync((Guid, string) key, VectorHandleOrigin origin)
    {
        var token = origin.PreviewCancellation.Token;
        try
        {
            if (origin.OverlayTask is not null) await origin.OverlayTask;
            while (origin.PreviewPending && !token.IsCancellationRequested)
            {
                if (!origin.AccelerateControlUpdates) await Task.Delay(50, token);
                if (AlreadyDisappeared || !Clips.TryGetValue(key.Item1, out var clip)) return;
                origin.PreviewPending = false;
                uint frame = (uint)Math.Max(0, _currentFrame);
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var preview = await DynamicPreviewProvider.PrepareVectorClipSnapshotAsync(
                    DraftImportAndExportHelper.ExportClipElementFromDraftPage(this, clip, rebuildEffects: false), frame,
                    Math.Max(1, previewWidth), Math.Max(1, previewHeight), token);
                if (token.IsCancellationRequested || AlreadyDisappeared || frame != (uint)Math.Max(0, _currentFrame)
                    || !vectorHandleOrigins.TryGetValue(key, out var active) || !ReferenceEquals(active, origin)) return;
                if (origin.PreviewView is null) ClipEditor.ApplyCustomHandlePreview(preview);
                LogDiagnostic($"Vector handle preview: clip={key.Item1}, elapsed={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms.");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Log(ex, $"Refresh vector handle preview for clip {key.Item1}", this); }
        finally
        {
            if (token.IsCancellationRequested) origin.PreviewCancellation.Dispose();
        }
    }

    private static void RestoreVectorHandleDefinition(ClipElementUI clip, VectorHandleOrigin origin)
    {
        RestoreVectorHandleBounds(clip, origin);
        clip.ExtraData[VectorComponentSerializer.ComponentsKey] = origin.Data;
    }

    private void EndVectorHandleDrag((Guid, string) key)
    {
        if (!vectorHandleOrigins.Remove(key, out var origin)) return;
        ClipEditor.EndCustomHandleLivePreview(key.Item1);
        vectorHandleDefinition = null;
        origin.PreviewCancellation.Cancel();
        if (origin.PreviewTask is null || origin.PreviewTask.IsCompleted) origin.PreviewCancellation.Dispose();
        if (vectorHandleOrigins.Count == 0)
        {
            vectorHandleTimer?.Stop();
            if (origin.OverlayTask is not null) CancelDynamicPreview();
            ClipEditor.ShowClipPreviewOverlays = UseDynamicPreview;
            if (!UseDynamicPreview) ClipEditor.SetStaticPreviewVisible(true);
        }
    }

    private void CancelVectorHandleDrags()
    {
        foreach (var (key, origin) in vectorHandleOrigins.ToArray())
        {
            if (Clips.TryGetValue(key.Item1, out var clip)) RestoreVectorHandleDefinition(clip, origin);
            EndVectorHandleDrag(key);
        }
    }
    #endregion
}
