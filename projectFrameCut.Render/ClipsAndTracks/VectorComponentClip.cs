using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.VectorContent;
using projectFrameCut.Render.VectorContent.Components;
using projectFrameCut.Shared;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.ClipsAndTracks;

public class VectorComponentClip : VectorCanvasClip
{
    public const string ChildrenKey = "VectorClip.Children";
    public const string SourceLengthKey = "VectorClip.SourceLength";
    public const string ViewportKey = "VectorClip.Viewport";
    public const string CanvasWidthKey = "VectorClip.CanvasWidth";
    public const string CanvasHeightKey = "VectorClip.CanvasHeight";
    public const string RotationKey = "VectorClip.Rotation";
    public const string TransformsKey = "VectorClip.ComponentTransforms";

    public override ClipMode ClipType => ClipMode.VectorComponentClip;
    [JsonIgnore]
    public List<ClipDraftDTO> Children { get; private set; } = [];
    private readonly List<VectorComponentClip> childInstances = [];
    private List<VectorCanvasElement>? frameOverride;

    public static float ReadNumber(Dictionary<string, object> data, string key, float fallback)
        => data.TryGetValue(key, out var value) && float.TryParse(value.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var n) && float.IsFinite(n) ? n : fallback;

    public int[] ReadViewport() => ReadViewport(ExtraData, TargetWidth, TargetHeight);

    public static int[] ReadViewport(Dictionary<string, object> data, int targetWidth, int targetHeight)
    {
        if (data.TryGetValue(ViewportKey, out var value) && JsonSerializer.Deserialize<int[]>(JsonSerializer.Serialize(value)) is { Length: 4 } rect)
            return [rect[0], rect[1], Math.Max(1, rect[2]), Math.Max(1, rect[3])];
        return [0, 0, Math.Max(1, targetWidth), Math.Max(1, targetHeight)];
    }

    public override void ReInit(IPicture.PicturePixelMode targetPPB)
    {
        base.ReInit(targetPPB);
        foreach (var child in childInstances) child.Dispose();
        childInstances.Clear();
        Children = ExtraData.TryGetValue(ChildrenKey, out var raw)
            ? JsonSerializer.Deserialize<List<ClipDraftDTO>>(raw is JsonElement e
                ? e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText() : raw.ToString()!) ?? [] : [];
        var viewport = ReadViewport();
        foreach (var child in Children.OrderBy(c => c.LayerIndex).ThenBy(c => c.SubLayerIndex))
        {
            var dto = JsonSerializer.Deserialize<ClipDraftDTO>(JsonSerializer.Serialize(child))!;
            dto.TargetX -= viewport[0];
            dto.TargetY -= viewport[1];
            var instance = PluginManager.CreateClip(JsonSerializer.SerializeToElement(dto)) as VectorComponentClip
                ?? throw new InvalidDataException("A vector group contains a non-vector clip.");
            instance.ReInit(targetPPB);
            childInstances.Add(instance);
        }
    }

    private float Progress(uint frame)
    {
        float length = ReadNumber(ExtraData, SourceLengthKey, Duration);
        return length <= 1 ? 0 : Math.Clamp(frame / (length - 1), 0, 1);
    }

    public static List<VectorCanvasElement> ApplyInheritedTransforms(Dictionary<string, object> data, IEnumerable<VectorCanvasElement> elements)
    {
        var result = elements.ToList();
        if (!data.TryGetValue(TransformsKey, out var value)) return result;
        foreach (var group in VectorComponentSerializer.Read(new Dictionary<string, object> { [VectorComponentSerializer.ComponentsKey] = value }).OfType<ComponentGroup>())
        {
            group.SetChildren([new EvaluatedVectorComponent(result)]);
            result = group.ComputeAll().ToList();
        }
        return result;
    }

    private VectorPicture BuildRaw(uint frameIndex)
    {
        if (frameOverride is not null) return new VectorPicture { Elements = frameOverride };
        float progress = Progress(frameIndex);
        var effects = (EffectsInstances ?? []).Where(e => e.Enabled).OrderBy(e => e.Index).OfType<IVectorComponentEffect>().ToArray();
        var picture = new VectorPicture();
        if (Children.Count == 0)
        {
            foreach (var component in Components)
            {
                picture.Elements.AddRange(VectorComponentProcessing.Compute(component, effects, frameIndex, progress));
            }
            return new VectorPicture { Elements = ApplyInheritedTransforms(ExtraData, picture.Elements) };
        }
        var viewport = ReadViewport();
        var active = new List<IVectorComponent>();
        for (int i = 0; i < childInstances.Count; i++)
        {
            var child = childInstances[i];
            if (!((IClip)child).ContainsFrame(frameIndex)) continue;
            var elements = child.GetVectorPictureRelativeToStartPointOfSource(((IClip)child).GetRelativeFrameIndex(frameIndex) ?? 0, child.TargetWidth, child.TargetHeight).Elements.OrderBy(e => e.LayerIndex)
                .Select(e => (VectorCanvasElement)new VectorViewportElement(e, child.TargetWidth, child.TargetHeight,
                    -child.TargetX - viewport[0], -child.TargetY - viewport[1],
                    ReadNumber(ExtraData, CanvasWidthKey, viewport[2]), ReadNumber(ExtraData, CanvasHeightKey, viewport[3]))).ToList();
            foreach (var element in elements)
            {
                element.LayerIndex = i;
                if (element is VectorViewportElement tagged && tagged.SourceClipId == Guid.Empty) tagged.SourceClipId = child.Id;
            }
            active.Add(new EvaluatedVectorComponent(elements) { Id = child.Id, Index = i });
        }
        var group = Components.FirstOrDefault() is ComponentGroup original
            ? (ComponentGroup)VectorComponentSerializer.Clone(original) : new ComponentGroup();
        group.SetChildren(active);
        picture.Elements.AddRange(VectorComponentProcessing.Compute(group, effects, frameIndex, progress));
        return picture;
    }

    public override VectorPicture GetVectorPictureRelativeToStartPointOfSource(uint frameIndex, int requiredWidth, int requiredHeight)
    {
        var viewport = ReadViewport();
        float canvasWidth = ReadNumber(ExtraData, CanvasWidthKey, requiredWidth);
        float canvasHeight = ReadNumber(ExtraData, CanvasHeightKey, requiredHeight);
        var picture = new VectorPicture
        {
            Elements = BuildRaw(frameIndex).Elements.Select(e => (VectorCanvasElement)new VectorViewportElement(e,
                canvasWidth, canvasHeight, viewport[0], viewport[1], Math.Max(1, viewport[2]), Math.Max(1, viewport[3]))).ToList()
        };
        float rotation = ReadNumber(ExtraData, RotationKey, 0);
        if (rotation != 0 && frameOverride is null)
        {
            picture = new VectorPicture { Elements = RotateViewport(picture.Elements, viewport[2], viewport[3], rotation) };
        }
        return frameOverride is null ? VectorPictureEffectProcessing.Process(this, picture, frameIndex, Progress(frameIndex)) : picture;
    }

    public static List<VectorCanvasElement> RotateViewport(IEnumerable<VectorCanvasElement> elements, float width, float height, float rotation)
    {
        float cos = MathF.Cos(rotation), sin = MathF.Sin(rotation);
        return elements.Select(source =>
        {
            var local = new VectorViewportElement(source, width, height, 0, 0, width, height);
            float x = (local.RelativeX - 0.5f) * width, y = (local.RelativeY - 0.5f) * height;
            local.RelativeX = 0.5f + (x * cos - y * sin) / width;
            local.RelativeY = 0.5f + (x * sin + y * cos) / height;
            local.Rotation = rotation;
            return (VectorCanvasElement)new VectorViewportElement(local, width, height, 0, 0, width, height);
        }).ToList();
    }

    public override IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int requiredWidth, int requiredHeight, IPicture.PicturePixelMode targetPPB)
    {
        if (Children.Count == 0) return base.GetFrameRelativeToStartPointOfSource(frameIndex, requiredWidth, requiredHeight, targetPPB);
        var viewport = ReadViewport();
        var elements = GetVectorPictureRelativeToStartPointOfSource(frameIndex, viewport[2], viewport[3]).Elements
            .Select(e => (VectorCanvasElement)new VectorViewportElement(e, viewport[2], viewport[3], -viewport[0], -viewport[1],
                ReadNumber(ExtraData, CanvasWidthKey, viewport[2]), ReadNumber(ExtraData, CanvasHeightKey, viewport[3]))).ToList();
        var frameChildren = new List<VectorComponentClip>();
        try
        {
            foreach (var source in childInstances)
            {
                var child = PluginManager.CreateClip(JsonSerializer.SerializeToElement(Children.First(c => c.Id == source.Id))) as VectorComponentClip
                    ?? throw new InvalidDataException("A vector group contains a non-vector clip.");
                child.TargetX = source.TargetX;
                child.TargetY = source.TargetY;
                frameChildren.Add(child);
                child.ReInit(targetPPB);
                child.frameOverride = elements.Where(e => e is IVectorClipElementTag tag && child.ContainsSourceClip(tag.SourceClipId)).ToList();
                int x = child.TargetX + viewport[0], y = child.TargetY + viewport[1];
                int right = x + child.TargetWidth, bottom = y + child.TargetHeight;
                if (child.frameOverride.Count > 0)
                {
                    var wrapper = new VectorComponentWrapperClip(new EvaluatedVectorComponent(child.frameOverride))
                    {
                        ParentCanvasWidth = (int)ReadNumber(ExtraData, CanvasWidthKey, viewport[2]),
                        ParentCanvasHeight = (int)ReadNumber(ExtraData, CanvasHeightKey, viewport[3])
                    };
                    wrapper.SyncFromDefinition();
                    right = Math.Max(right, wrapper.TargetX + wrapper.TargetWidth);
                    bottom = Math.Max(bottom, wrapper.TargetY + wrapper.TargetHeight);
                    x = Math.Min(x, wrapper.TargetX); y = Math.Min(y, wrapper.TargetY);
                }
                child.TargetX = x - viewport[0]; child.TargetY = y - viewport[1];
                child.TargetWidth = Math.Max(1, right - x); child.TargetHeight = Math.Max(1, bottom - y);
                child.ExtraData[ViewportKey] = new[] { x, y, child.TargetWidth, child.TargetHeight };
            }
            using var added = new VectorComponentClip
            {
                Id = Guid.NewGuid(), Name = Name, Duration = uint.MaxValue,
                LayerIndex = childInstances.Count == 0 ? 0 : childInstances.Max(c => c.LayerIndex) + 1,
                TargetWidth = viewport[2], TargetHeight = viewport[3],
                ExtraData = new Dictionary<string, object>
                {
                    [ViewportKey] = viewport,
                    [CanvasWidthKey] = ReadNumber(ExtraData, CanvasWidthKey, viewport[2]),
                    [CanvasHeightKey] = ReadNumber(ExtraData, CanvasHeightKey, viewport[3]),
                    [VectorComponentSerializer.ComponentsKey] = VectorComponentSerializer.Serialize([new EvaluatedVectorComponent(
                        elements.Where(e => e is not IVectorClipElementTag tag || tag.SourceClipId == Guid.Empty))])
                }
            };
            var frames = Timeline.GetFramesInOneFrame(frameChildren.Cast<IClip>().Append(added).ToArray(), frameIndex,
                Math.Max(1, requiredWidth), Math.Max(1, requiredHeight), targetPPB, viewport[2], viewport[3]);
            return Timeline.MixtureLayers(frames, frameIndex, Math.Max(1, requiredWidth), Math.Max(1, requiredHeight), targetPPB.Value,
                projectRelativeWidth: viewport[2], projectRelativeHeight: viewport[3], transparentBackground: true);
        }
        catch (Exception ex)
        {
            Log(ex, $"Render vector group {Id}, source frame {frameIndex} at {requiredWidth}x{requiredHeight}", this);
            throw;
        }
        finally
        {
            foreach (var child in frameChildren) child.Dispose();
        }
    }

    private bool ContainsSourceClip(Guid id) => Id == id || childInstances.Any(c => c.ContainsSourceClip(id));

    public override void Dispose()
    {
        foreach (var child in childInstances) child.Dispose();
        childInstances.Clear();
        base.Dispose();
    }
}
