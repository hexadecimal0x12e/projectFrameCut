using projectFrameCut.DraftStuff;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Render.VectorContent;
using projectFrameCut.Render.VectorContent.Components;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace projectFrameCut;

public partial class DraftPage
{
    private static bool IsGuiVectorClip(ClipElementUI clip)
        => clip.ClipType is ClipMode.VectorCanvasClip or ClipMode.VectorComponentClip;

    private static Dictionary<string, Func<IVectorComponent>> GetGuiVectorComponentFactories()
    {
        var factories = VectorComponentHandlerServices.GetAvailableHandlers()
            .ToDictionary(x => x.Key, x => (Func<IVectorComponent>)(() => x.Value().Create()), StringComparer.OrdinalIgnoreCase);
        factories.TryAdd("ComponentGroup", () => new ComponentGroup());
        return factories;
    }

    private static void ApplyGuiVectorParameters(IVectorComponent component, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) throw new ArgumentException("Parameters must be an object.");
        foreach (var field in parameters.EnumerateObject())
        {
            var key = component.Parameters.Keys.FirstOrDefault(k => k.Equals(field.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Unknown vector parameter '{field.Name}' for {component.TypeName}.");
            if (key == "Children") throw new ArgumentException("Use component commands to edit group children.");
            if (field.Value.ValueKind == JsonValueKind.Null) throw new ArgumentException($"Vector parameter '{key}' cannot be null.");
            var expected = JsonSerializer.SerializeToElement(component.Parameters[key]);
            if (expected.ValueKind == JsonValueKind.Number
                && (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetSingle(out var number) || !float.IsFinite(number)))
                throw new ArgumentException($"Vector parameter '{key}' must be a finite number.");
            if ((expected.ValueKind is JsonValueKind.String or JsonValueKind.Array or JsonValueKind.Object)
                && field.Value.ValueKind != expected.ValueKind)
                throw new ArgumentException($"Invalid value type for vector parameter '{key}'.");
            if ((expected.ValueKind is JsonValueKind.True or JsonValueKind.False)
                && field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException($"Vector parameter '{key}' must be a Boolean.");
            component.Parameters[key] = field.Value.Clone();
        }
    }

    private static IVectorComponent CreateGuiVectorComponent(JsonElement definition)
    {
        if (definition.ValueKind != JsonValueKind.Object || !definition.TryGetProperty("TypeName", out var type))
            throw new ArgumentException("A component definition requires TypeName.");
        if (!GetGuiVectorComponentFactories().TryGetValue(type.GetString() ?? "", out var factory))
            throw new KeyNotFoundException("Vector component type not found.");
        var component = factory();
        if (definition.TryGetProperty("Name", out var name)) component.Name = name.GetString() ?? "";
        if (definition.TryGetProperty("Parameters", out var parameters)) ApplyGuiVectorParameters(component, parameters);
        if (definition.TryGetProperty("Children", out var children))
        {
            if (component is not ComponentGroup group || children.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Children must be an array on a ComponentGroup.");
            var list = children.EnumerateArray().Select(CreateGuiVectorComponent).ToList();
            ReindexGuiVectorComponents(list);
            group.SetChildren(list);
        }
        return component;
    }

    private static IEnumerable<(IVectorComponent Component, ComponentGroup? Parent)> EnumerateGuiVectorComponents(
        IEnumerable<IVectorComponent> components, ComponentGroup? parent = null)
    {
        foreach (var component in components)
        {
            yield return (component, parent);
            if (component is ComponentGroup group)
                foreach (var child in EnumerateGuiVectorComponents(group.Children, group)) yield return child;
        }
    }

    private static void ReindexGuiVectorComponents(List<IVectorComponent> components)
    {
        for (int i = 0; i < components.Count; i++)
        {
            components[i].Index = i;
            if (components[i].Parameters.ContainsKey("LayerIndex")) components[i].Parameters["LayerIndex"] = i;
        }
    }

    private static object GuiVectorComponentSnapshot(Guid clipId, IVectorComponent component, ComponentGroup? parent) => new
    {
        ClipId = clipId,
        ComponentId = component.Id,
        ParentComponentId = parent?.Id,
        component.TypeName,
        component.FromPlugin,
        component.Name,
        component.Index,
        Parameters = component.Parameters.Where(p => p.Key != "Children").ToDictionary(p => p.Key, p => p.Value),
    };

    private object GuiVectorClipSnapshot(ClipElementUI clip)
    {
        var dto = DraftImportAndExportHelper.ExportClipElementFromDraftPage(this, clip, false);
        return new
        {
            ClipId = dto.Id,
            dto.Name,
            Type = dto.ClipType.ToString(),
            TrackId = dto.LayerIndex,
            dto.StartFrame,
            DurationFrames = dto.Duration,
            SourceStartFrame = dto.RelativeStartFrame,
            WidthPixels = dto.TargetWidth,
            HeightPixels = dto.TargetHeight,
            XPixels = dto.TargetX,
            YPixels = dto.TargetY,
            RotationDegrees = dto.Rotation,
            CanvasWidthPixels = VectorComponentClip.ReadNumber(clip.ExtraData, VectorComponentClip.CanvasWidthKey, dto.TargetWidth),
            CanvasHeightPixels = VectorComponentClip.ReadNumber(clip.ExtraData, VectorComponentClip.CanvasHeightKey, dto.TargetHeight),
            Viewport = VectorComponentClip.ReadViewport(clip.ExtraData, dto.TargetWidth, dto.TargetHeight),
            SourceLengthFrames = VectorComponentClip.ReadNumber(clip.ExtraData, VectorComponentClip.SourceLengthKey, dto.Duration),
            AntiAliasMode = clip.ExtraData.GetValueOrDefault("VectorAntiAliasMode")?.ToString() is { Length: > 0 } aa ? aa : "Default",
            Components = EnumerateGuiVectorComponents(VectorComponentSerializer.Read(clip.ExtraData))
                .Select(c => GuiVectorComponentSnapshot(dto.Id, c.Component, c.Parent)).ToArray(),
        };
    }

    private ClipDraftDTO ApplyGuiVectorClipParameters(ClipDraftDTO dto, JsonElement parameters)
    {
        var metadata = dto.MetaData;
        var node = JsonSerializer.SerializeToNode(dto)!;
        foreach (var (source, target) in new[] { ("Name", "Name"), ("StartFrame", "StartFrame"), ("DurationFrames", "Duration"),
            ("SourceStartFrame", "RelativeStartFrame"), ("TrackId", "LayerIndex"), ("WidthPixels", "TargetWidth"),
            ("HeightPixels", "TargetHeight"), ("XPixels", "TargetX"), ("YPixels", "TargetY"), ("RotationDegrees", "Rotation") })
            if (parameters.TryGetProperty(source, out var value)) node[target] = JsonNode.Parse(value.GetRawText());
        dto = node.Deserialize<ClipDraftDTO>()!;
        if (dto.Duration == 0 || dto.LayerIndex > int.MaxValue || !Tracks.ContainsKey((int)dto.LayerIndex)
            || (ulong)dto.StartFrame + dto.Duration > int.MaxValue || !float.IsFinite(dto.Rotation))
            throw new ArgumentException("Invalid vector clip timing, track or rotation.");
        if (dto.TargetWidth <= 0 || dto.TargetHeight <= 0) throw new ArgumentException("Vector clip dimensions must be positive.");
        dto.MetaData = metadata is null ? new() : new(metadata);
        foreach (var (parameter, key) in new[] { ("CanvasWidthPixels", VectorComponentClip.CanvasWidthKey),
            ("CanvasHeightPixels", VectorComponentClip.CanvasHeightKey), ("SourceLengthFrames", VectorComponentClip.SourceLengthKey) })
        {
            if (!parameters.TryGetProperty(parameter, out var value)) continue;
            if (dto.ClipType != ClipMode.VectorComponentClip) throw new ArgumentException($"{parameter} requires a VectorComponentClip.");
            if (value.GetInt32() <= 0) throw new ArgumentException($"{parameter} must be positive.");
            dto.MetaData[key] = value.GetInt32();
            if (parameter == "SourceLengthFrames") dto.SourceDuration = value.GetInt32();
        }
        if (parameters.TryGetProperty("Viewport", out var viewport))
        {
            if (dto.ClipType != ClipMode.VectorComponentClip) throw new ArgumentException("Viewport requires a VectorComponentClip.");
            var rect = viewport.Deserialize<int[]>();
            if (rect is not { Length: 4 } || rect[2] <= 0 || rect[3] <= 0)
                throw new ArgumentException("Viewport must be [x, y, width, height] with positive dimensions.");
            dto.MetaData[VectorComponentClip.ViewportKey] = rect;
        }
        if (parameters.TryGetProperty("AntiAliasMode", out var aa))
        {
            string mode = aa.GetString() ?? "";
            string? canonical = new[] { "Default", "None", "SSAA2x", "SSAA4x", "SSAA8x" }
                .FirstOrDefault(x => x.Equals(mode, StringComparison.OrdinalIgnoreCase));
            if (canonical is null) throw new ArgumentException("Unknown vector anti-alias mode.");
            dto.MetaData["VectorAntiAliasMode"] = canonical == "Default" ? "" : canonical;
        }
        return dto;
    }

    private ClipDraftDTO SetGuiVectorDefinition(ClipDraftDTO dto, List<IVectorComponent> components, bool updateBounds)
    {
        foreach (var component in components) _ = component.ComputeAll().ToArray();
        // Work on a detached element so invalid definitions cannot partially update the timeline.
        var element = DraftImportAndExportHelper.ConvertToElement(dto);
        VectorClipServices.SetDefinition(this, element, components, updateBounds && dto.ClipType == ClipMode.VectorComponentClip);
        dto.MetaData = element.ExtraData;
        dto.TargetWidth = element.TargetWidth;
        dto.TargetHeight = element.TargetHeight;
        dto.TargetX = element.TargetX;
        dto.TargetY = element.TargetY;
        return dto;
    }

    private ClipElementUI ReplaceGuiVectorClip(ClipDraftDTO dto, List<IVectorComponent> components, CancellationToken cancellationToken)
    {
        bool showDefaultHandles = components.Count != 1 || VectorClipServices.GetHandler(components[0])?.HasDefaultHandles != false;
        cancellationToken.ThrowIfCancellationRequested();
        var clip = TimelineMcpLiveService.ReplaceClip(this, dto);
        clip.ShowDefaultHandles = showDefaultHandles;
        return clip;
    }

    private object? ExecuteGuiVectorOperation(GuiProjectOperation operation, JsonElement parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool Has(string key) => parameters.TryGetProperty(key, out _);
        string? S(string key) => Has(key) ? parameters.GetProperty(key).GetString() : null;
        Guid G(string key) => Has(key) ? parameters.GetProperty(key).GetGuid() : Guid.Empty;
        bool B(string key, bool fallback = false) => Has(key) ? parameters.GetProperty(key).GetBoolean() : fallback;
        ClipElementUI Clip()
        {
            if (!Clips.TryGetValue(G("ClipId"), out var clip)) throw new KeyNotFoundException("Clip not found.");
            return IsGuiVectorClip(clip) ? clip : throw new ArgumentException("Clip must be a VectorCanvasClip or VectorComponentClip.");
        }

        if (operation == GuiProjectOperation.GetVectorComponentType)
            return GetGuiVectorComponentFactories().Select(x => (TypeName: x.Key, Component: x.Value()))
                .Where(x => (!Has("TypeName") || x.TypeName.Equals(S("TypeName"), StringComparison.OrdinalIgnoreCase))
                    && (!Has("Name") || x.TypeName.Contains(S("Name")!, StringComparison.OrdinalIgnoreCase)
                        || x.Component.Name.Contains(S("Name")!, StringComparison.OrdinalIgnoreCase)))
                .Select(x => new { x.TypeName, x.Component.Name, x.Component.FromPlugin,
                    DefaultParameters = x.Component.Parameters.Where(p => p.Key != "Children").ToDictionary(p => p.Key, p => p.Value) }).ToArray();

        if (operation == GuiProjectOperation.GetVectorClip)
            return Clips.Values.Where(c => IsGuiVectorClip(c) && (!Has("ClipId") || c.Id == G("ClipId"))
                && (!Has("TrackId") || c.origTrack == parameters.GetProperty("TrackId").GetInt32())
                && (!Has("Name") || c.DisplayName.Contains(S("Name")!, StringComparison.OrdinalIgnoreCase)))
                .Select(GuiVectorClipSnapshot).ToArray();

        if (operation == GuiProjectOperation.GetVectorComponent)
        {
            var clip = Clip();
            return EnumerateGuiVectorComponents(VectorComponentSerializer.Read(clip.ExtraData))
                .Where(c => (!Has("ComponentId") || c.Component.Id == G("ComponentId"))
                    && (!Has("ParentComponentId") || (c.Parent?.Id ?? Guid.Empty) == G("ParentComponentId"))
                    && (!Has("TypeName") || c.Component.TypeName.Equals(S("TypeName"), StringComparison.OrdinalIgnoreCase)))
                .Select(c => GuiVectorComponentSnapshot(clip.Id, c.Component, c.Parent)).ToArray();
        }

        ClipDraftDTO dto;
        object? result;
        if (operation is GuiProjectOperation.AddVectorCanvasClip or GuiProjectOperation.AddVectorComponentClip)
        {
            bool canvas = operation == GuiProjectOperation.AddVectorCanvasClip;
            var components = new List<IVectorComponent>();
            if (canvas)
            {
                if (Has("Components"))
                {
                    if (parameters.GetProperty("Components").ValueKind != JsonValueKind.Array) throw new ArgumentException("Components must be an array.");
                    components.AddRange(parameters.GetProperty("Components").EnumerateArray().Select(CreateGuiVectorComponent));
                }
            }
            else if (Has("FilePath"))
            {
                if (Has("TypeName") || Has("Parameters")) throw new ArgumentException("Choose FilePath or TypeName and Parameters.");
                var component = VectorClipServices.Import(S("FilePath")!);
                if (component.Parameters.ContainsKey(VectorClipServices.AssetClipKey))
                    throw new ArgumentException("Import component JSON or SVG; saved timeline clips are not component definitions.");
                components.Add(component);
            }
            else components.Add(CreateGuiVectorComponent(parameters));
            ReindexGuiVectorComponents(components);
            uint duration = Has("DurationFrames") ? parameters.GetProperty("DurationFrames").GetUInt32() : 300;
            dto = new ClipDraftDTO
            {
                Id = Guid.NewGuid(), FromPlugin = InternalPluginBase.InternalPluginBaseID,
                ClipType = canvas ? ClipMode.VectorCanvasClip : ClipMode.VectorComponentClip,
                TypeName = canvas ? nameof(VectorCanvasClip) : nameof(VectorComponentClip),
                Name = S("Name") ?? (canvas ? "Vector Canvas" : components[0].Name),
                Duration = duration, SourceDuration = duration, FrameTime = 1f / Math.Max(1u, ProjectInfo.TargetFrameRate),
                SecondPerFrameRatio = 1f, TargetWidth = ProjectInfo.RelativeWidth, TargetHeight = ProjectInfo.RelativeHeight,
                MetaData = new(),
            };
            if (!canvas)
            {
                dto.MetaData[VectorComponentClip.CanvasWidthKey] = ProjectInfo.RelativeWidth;
                dto.MetaData[VectorComponentClip.CanvasHeightKey] = ProjectInfo.RelativeHeight;
                dto.MetaData[VectorComponentClip.SourceLengthKey] = duration;
            }
            dto = ApplyGuiVectorClipParameters(dto, parameters);
            dto = SetGuiVectorDefinition(dto, components, !canvas && !Has("Viewport"));
            dto = ApplyGuiVectorClipParameters(dto, parameters);
            result = GuiVectorClipSnapshot(ReplaceGuiVectorClip(dto, components, cancellationToken));
        }
        else
        {
            var clip = Clip();
            dto = DraftImportAndExportHelper.ExportClipElementFromDraftPage(this, clip, false);
            dto.MetaData ??= new();
            var components = VectorComponentSerializer.Read(dto.MetaData!);
            if (operation == GuiProjectOperation.SetVectorClip)
            {
                if (B("UpdateBounds") && Has("Viewport")) throw new ArgumentException("Choose UpdateBounds or Viewport.");
                dto = ApplyGuiVectorClipParameters(dto, parameters);
                dto = SetGuiVectorDefinition(dto, components, B("UpdateBounds"));
                dto = ApplyGuiVectorClipParameters(dto, parameters);
                result = GuiVectorClipSnapshot(ReplaceGuiVectorClip(dto, components, cancellationToken));
            }
            else
            {
                if (dto.MetaData!.ContainsKey(VectorComponentClip.ChildrenKey) && operation != GuiProjectOperation.SetVectorComponent)
                    throw new InvalidOperationException("Cannot add or remove components in a timed vector clip group.");
                IVectorComponent component;
                ComponentGroup? parent;
                List<IVectorComponent> siblings;
                if (operation == GuiProjectOperation.AddVectorComponent)
                {
                    parent = Has("ParentComponentId") && G("ParentComponentId") != Guid.Empty
                        ? EnumerateGuiVectorComponents(components).Select(c => c.Component).OfType<ComponentGroup>()
                            .FirstOrDefault(c => c.Id == G("ParentComponentId")) ?? throw new KeyNotFoundException("Parent component group not found.")
                        : null;
                    siblings = parent?.Children ?? components;
                    component = CreateGuiVectorComponent(parameters);
                    siblings.Add(component);
                }
                else
                {
                    var found = EnumerateGuiVectorComponents(components).FirstOrDefault(c => c.Component.Id == G("ComponentId"));
                    component = found.Component ?? throw new KeyNotFoundException("Vector component not found.");
                    parent = found.Parent;
                    siblings = parent?.Children ?? components;
                    if (operation == GuiProjectOperation.SetVectorComponent)
                    {
                        if (B("ResetToDefaults"))
                        {
                            IVectorComponent defaults = component is ComponentGroup ? new ComponentGroup()
                                : VectorClipServices.GetHandler(component)?.Create()
                                    ?? throw new InvalidOperationException("No defaults are available for this vector component.");
                            var children = (component as ComponentGroup)?.Children.ToArray();
                            component.Parameters.Clear();
                            foreach (var field in defaults.Parameters) component.Parameters[field.Key] = field.Value;
                            if (component is ComponentGroup group) group.SetChildren(children!);
                            if (component.Parameters.ContainsKey("LayerIndex")) component.Parameters["LayerIndex"] = component.Index;
                        }
                        if (Has("Name")) component.Name = S("Name") ?? "";
                        if (Has("Parameters")) ApplyGuiVectorParameters(component, parameters.GetProperty("Parameters"));
                    }
                    else if (operation == GuiProjectOperation.RemoveVectorComponent) siblings.Remove(component);
                    else throw new NotSupportedException("Unknown GUI vector operation.");
                }
                if (Has("Index"))
                {
                    int index = parameters.GetProperty("Index").GetInt32();
                    if (index < 0 || index >= siblings.Count) throw new ArgumentException("Index is outside the component list.");
                    siblings.Remove(component);
                    siblings.Insert(index, component);
                }
                if (operation != GuiProjectOperation.SetVectorComponent || Has("Index")) ReindexGuiVectorComponents(siblings);
                parent?.SetChildren(siblings);
                dto = SetGuiVectorDefinition(dto, components, B("UpdateBounds", dto.ClipType == ClipMode.VectorComponentClip));
                ReplaceGuiVectorClip(dto, components, cancellationToken);
                result = operation == GuiProjectOperation.RemoveVectorComponent ? null : GuiVectorComponentSnapshot(dto.Id, component, parent);
            }
        }
        Log($"GUI RPC {operation}: vector clip {dto.Id}.");
        return result;
    }
}
