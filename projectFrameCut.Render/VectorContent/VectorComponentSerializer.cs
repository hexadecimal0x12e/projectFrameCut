using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.VectorContent.Components;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using System.Text.Json;

namespace projectFrameCut.Render.VectorContent;

public static class VectorComponentSerializer
{
    public const string ComponentsKey = "VectorCanvas.Components";

    public static string Serialize(IEnumerable<IVectorComponent> components)
    {
        var list = components.ToArray();
        foreach (var group in list.OfType<ComponentGroup>())
        {
            Serialize(group.Children);
            group.SetChildren(group.Children);
        }
        return JsonSerializer.Serialize(list);
    }

    public static IVectorComponent Restore(JsonElement element)
    {
        var pluginId = element.TryGetProperty("FromPlugin", out var p) ? p.GetString() : InternalPluginBase.InternalPluginBaseID;
        if (string.IsNullOrEmpty(pluginId) || !PluginManager.LoadedPlugins.TryGetValue(pluginId, out var plugin))
            throw new InvalidDataException($"Vector component plugin is unavailable: {pluginId}");
        return plugin.VectComponentCreator(element);
    }

    public static IVectorComponent Clone(IVectorComponent component)
        => Restore(JsonSerializer.SerializeToElement(component));

    public static List<IVectorComponent> Read(Dictionary<string, object> data)
    {
        if (!data.TryGetValue(ComponentsKey, out var raw)) return [];
        using var doc = JsonDocument.Parse(raw is JsonElement e
            ? e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText()
            : raw.ToString()!);
        return doc.RootElement.EnumerateArray().Select(Restore).ToList();
    }
}
