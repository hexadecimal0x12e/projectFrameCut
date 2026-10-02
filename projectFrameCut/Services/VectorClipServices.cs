using projectFrameCut.Shared;
using projectFrameCut.ApplicationAPIBase.VectorComponentHandler;
using projectFrameCut.Asset;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Drawing.Vector.ImportExport;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Render.VectorContent;
using projectFrameCut.Render.VectorContent.Components;
using System.Text.Json;
using Point = projectFrameCut.Drawing.Vector.Point;

namespace projectFrameCut.Services;

public static partial class VectorClipServices
{
    public const string AssetClipKey = "VectorClip.Asset";
    public const string ViewportKey = "VectorClip.Viewport";
    public const string CanvasWidthKey = "VectorClip.CanvasWidth";
    public const string CanvasHeightKey = "VectorClip.CanvasHeight";

    public static IVectorComponentHandler? GetHandler(IVectorComponent component)
        => VectorComponentHandlerServices.GetAvailableHandlers().Values.Select(f => f())
            .FirstOrDefault(h => h.TypeName == component.TypeName && h.FromPlugin == component.FromPlugin);

    public static void SetDefinition(DraftPage page, ClipElementUI clip, IEnumerable<IVectorComponent> components, bool updateBounds, IVectorComponentHandler? handler = null)
    {
        var list = components.ToList();
        clip.ShowDefaultHandles = list.Count != 1 || (handler ?? GetHandler(list[0]))?.HasDefaultHandles != false;
        clip.ExtraData[VectorComponentSerializer.ComponentsKey] = VectorComponentSerializer.Serialize(list);
        if (!updateBounds || list.Count == 0) return;
        int[]? oldViewport = clip.ExtraData.ContainsKey(ViewportKey)
            ? VectorComponentClip.ReadViewport(clip.ExtraData, clip.TargetWidth, clip.TargetHeight) : null;
        float scaleX = oldViewport is null ? 1 : clip.TargetWidth / (float)Math.Max(1, oldViewport[2]);
        float scaleY = oldViewport is null ? 1 : clip.TargetHeight / (float)Math.Max(1, oldViewport[3]);
        using var grouped = clip.ExtraData.ContainsKey(VectorComponentClip.ChildrenKey)
            ? PluginManager.CreateClip(JsonSerializer.SerializeToElement(DraftImportAndExportHelper.ExportClipElementFromDraftPage(page, clip))) as VectorComponentClip : null;
        grouped?.ReInit(8);
        IVectorComponent At(float progress)
        {
            if (grouped is null)
                return clip.ExtraData.ContainsKey(VectorComponentClip.TransformsKey)
                    ? new EvaluatedVectorComponent(VectorComponentClip.ApplyInheritedTransforms(clip.ExtraData, list[0].ComputeAll())) : list[0];
            var viewport = grouped.ReadViewport();
            uint frame = (uint)Math.Round(progress * Math.Max(0, VectorComponentClip.ReadNumber(clip.ExtraData, VectorComponentClip.SourceLengthKey, clip.lengthInFrame) - 1));
            return new EvaluatedVectorComponent(grouped.GetVectorPictureRelativeToStartPointOfSource(frame, viewport[2], viewport[3]).Elements
                .Select(e => (VectorCanvasElement)new VectorViewportElement(e, viewport[2], viewport[3], -viewport[0], -viewport[1], page.ProjectInfo.RelativeWidth, page.ProjectInfo.RelativeHeight)));
        }
        var wrapper = new VectorComponentWrapperClip(At(0), page.ProjectInfo.RelativeWidth, page.ProjectInfo.RelativeHeight);
        int x = wrapper.TargetX, y = wrapper.TargetY, width = wrapper.TargetWidth, height = wrapper.TargetHeight;
        uint length = Math.Max(1, (uint)VectorComponentClip.ReadNumber(clip.ExtraData, VectorComponentClip.SourceLengthKey, clip.lengthInFrame));
        var times = Enumerable.Range(1, grouped is null ? 0 : 32).Select(i => i / 32f);
        if (grouped is not null)
            times = times.Concat(grouped.Children.SelectMany(c => new[] { c.StartFrame / (float)length, Math.Min(1, (c.StartFrame + c.Duration - 1) / (float)length) }));
        foreach (var time in times)
        {
            ClipPositionTuple rect;
            if (grouped is not null || clip.ExtraData.ContainsKey(VectorComponentClip.TransformsKey))
            {
                var animated = new VectorComponentWrapperClip(At(time), page.ProjectInfo.RelativeWidth, page.ProjectInfo.RelativeHeight);
                rect = new ClipPositionTuple(animated.TargetX, animated.TargetY, animated.TargetWidth, animated.TargetHeight, false);
            }
            else if (!wrapper.TryComputeFrameBounds(out rect)) continue;
            int right = Math.Max(x + width, rect.TargetX + rect.TargetWidth), bottom = Math.Max(y + height, rect.TargetY + rect.TargetHeight);
            x = Math.Min(x, rect.TargetX); y = Math.Min(y, rect.TargetY);
            width = right - x; height = bottom - y;
        }
        float rotation = VectorComponentClip.ReadNumber(clip.ExtraData, VectorComponentClip.RotationKey, 0);
        float dx = oldViewport is null ? 0 : x + width / 2f - oldViewport[0] - oldViewport[2] / 2f;
        float dy = oldViewport is null ? 0 : y + height / 2f - oldViewport[1] - oldViewport[3] / 2f;
        clip.TargetX = oldViewport is null ? x : clip.TargetX + (int)Math.Round((dx * MathF.Cos(rotation) - dy * MathF.Sin(rotation) + (oldViewport[2] - width) / 2f) * scaleX);
        clip.TargetY = oldViewport is null ? y : clip.TargetY + (int)Math.Round((dx * MathF.Sin(rotation) + dy * MathF.Cos(rotation) + (oldViewport[3] - height) / 2f) * scaleY);
        clip.TargetWidth = Math.Max(1, (int)Math.Round(width * scaleX));
        clip.TargetHeight = Math.Max(1, (int)Math.Round(height * scaleY));
        clip.ExtraData[ViewportKey] = new[] { x, y, width, height };
        clip.ExtraData[CanvasWidthKey] = page.ProjectInfo.RelativeWidth;
        clip.ExtraData[CanvasHeightKey] = page.ProjectInfo.RelativeHeight;
    }

    public static ClipElementUI CreateClip(DraftPage page, IVectorComponent component, int track, double start, uint duration)
    {
        if (component.Parameters.TryGetValue(AssetClipKey, out var saved))
        {
            var dto = JsonSerializer.Deserialize<ClipDraftDTO>(saved.ToString()!)!;
            dto.Id = Guid.NewGuid(); dto.LayerIndex = (uint)track;
            dto.StartFrame = page.PixelToFrame(start);
            dto.ClipType = ClipMode.VectorComponentClip; dto.TypeName = nameof(VectorComponentClip);
            var restored = DraftImportAndExportHelper.ImportFromJSON(new DraftStructureJSON { Clips = [dto] }, page.ProjectInfo).Item1[dto.Id];
            restored.Clip.TranslationX = start;
            restored.Clip.WidthRequest = page.FrameToPixel(dto.Duration);
            restored.origLength = restored.Clip.WidthRequest; restored.origX = start;
            return restored;
        }
        var clip = ClipElementUI.CreateClip(start, page.FrameToPixel(duration), track, labelText: component.Name, maxFrames: duration);
        clip.Clip.TranslationX = start;
        clip.ClipType = ClipMode.VectorComponentClip;
        clip.TypeName = nameof(VectorComponentClip);
        clip.FromPlugin = InternalPluginBase.InternalPluginBaseID;
        clip.sourceSecondPerFrame = 1f / Math.Max(1u, page.ProjectInfo.TargetFrameRate);
        clip.isInfiniteLength = false;
        clip.lengthInFrame = duration;
        clip.ExtraData[VectorComponentClip.SourceLengthKey] = duration;
        SetDefinition(page, clip, [component], true);
        Log($"Created vector clip {clip.Id}, component={component.TypeName}, track={track}, startFrame={page.PixelToFrame(start)}, duration={duration}.");
        return clip;
    }

    public static ClipElementUI AddClip(DraftPage page, IVectorComponent component, int track, double start, uint duration)
    {
        var clip = CreateClip(page, component, track, start, duration);
        page.RegisterClip(clip, false);
        page.AddAClip(clip);
        return clip;
    }

    public static async Task<IVectorComponent?> PickComponent(Page page)
    {
        var handlers = VectorComponentHandlerServices.GetAvailableHandlers().Values.Select(f => f()).ToList();
        var names = handlers.Select(h => $"{h.DisplayName} ({h.TypeName})").ToArray();
        var selected = await page.DisplayActionSheetAsync(Localized.VectorContentEditorView_AddShape, Localized._Cancel, null, names);
        int index = Array.IndexOf(names, selected);
        if (index < 0) return null;
        var component = handlers[index].Create();
        component.Name = handlers[index].DisplayName;
        return component;
    }

    public static async Task<string?> PickFile()
    {
        var result = await FilePicker.PickAsync(new PickOptions
        {
            PickerTitle = Localized.VectorContentEditorView_Import,
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                [DevicePlatform.WinUI] = [".svg", ".json", ".pjfcvec"],
                [DevicePlatform.macOS] = ["svg", "json", "pjfcvec"],
                [DevicePlatform.Android] = ["image/svg+xml", "application/json"],
                [DevicePlatform.iOS] = ["public.svg-image", "public.json"]
            })
        });
        return result?.FullPath;
    }

    public static IVectorComponent Import(string path)
    {
        List<IVectorComponent> components;
        if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
        {
            var handlers = VectorComponentHandlerServices.GetAvailableHandlers().Values.Select(f => f())
                .GroupBy(h => h.TypeName).ToDictionary(g => g.Key, g => g.First());
            components = [];
            foreach (var e in SVGToVectorElement.ImportFromFile(path).Elements)
            {
                foreach (var segment in e.Draw())
                {
                    var source = new ImportedElement(e, segment);
                    var component = ConvertElementToComponent(source, components.Count, handlers)
                        ?? new EvaluatedVectorComponent([source]) { Name = Path.GetFileNameWithoutExtension(path) };
                    components.Add(component);
                }
            }
        }
        else
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("ClipType", out _) && json.RootElement.TryGetProperty(VectorComponentSerializer.ComponentsKey, out _))
            {
                var dto = JsonSerializer.Deserialize<ClipDraftDTO>(json.RootElement.GetRawText())!;
                var asset = new ComponentGroup { Name = dto.Name };
                asset.Parameters[AssetClipKey] = json.RootElement.GetRawText();
                return asset;
            }
            components = json.RootElement.ValueKind == JsonValueKind.Array
                ? json.RootElement.EnumerateArray().Select(VectorComponentSerializer.Restore).ToList()
                : [VectorComponentSerializer.Restore(json.RootElement)];
        }
        if (components.Count == 0) throw new InvalidDataException("The vector file contains no components.");
        RegenerateIds(components);
        if (components.Count == 1) return components[0];
        var group = new ComponentGroup { Name = Path.GetFileNameWithoutExtension(path), IsImportedGroup = true };
        group.SetChildren(components);
        return group;
    }

    public static void RegenerateIds(IEnumerable<IVectorComponent> components)
    {
        foreach (var component in components)
        {
            component.Id = Guid.NewGuid();
            if (component is ComponentGroup group)
            {
                RegenerateIds(group.Children);
                group.SetChildren(group.Children);
            }
        }
    }

    public static IVectorComponent ToAssetComponent(DraftPage page, ClipElementUI clip)
    {
        var asset = new ComponentGroup { Name = clip.DisplayName };
        asset.Parameters[AssetClipKey] = JsonSerializer.Serialize(DraftImportAndExportHelper.ExportClipElementFromDraftPage(page, clip));
        return asset;
    }

    public static AssetItem AddAsset(DraftPage page, IVectorComponent component)
    {
        string id = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(page.WorkingPath, "vectorAssets");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, id + ".json");
        File.WriteAllText(path, VectorComponentSerializer.Serialize([component]));
        var asset = new AssetItem
        {
            AssetId = id, Name = component.Name, Path = path, AssetType = AssetType.VectorComposition,
            ClipType = ClipMode.VectorComponentClip, CreatedAt = DateTime.Now,
            Width = page.ProjectInfo.RelativeWidth, Height = page.ProjectInfo.RelativeHeight
        };
        page.Assets[id] = asset;
        page.NotifyVectorAssetChanged();
        Log($"Added vector asset {id}: {component.Name}.");
        return asset;
    }

    private sealed class ImportedElement : VectorCanvasElement
    {
        private readonly VectorSegment segment;
        public ImportedElement(VectorCanvasElement source, VectorSegment segment)
        {
            RelativeX = source.RelativeX; RelativeY = source.RelativeY;
            BaseX = source.BaseX; BaseY = source.BaseY; Rotation = source.Rotation;
            LayerIndex = source.LayerIndex; UseUniformScale = source.UseUniformScale;
            this.segment = segment;
        }
        public override VectorSegment[] Draw() => [segment];
    }
}
