using Microsoft.Maui.Graphics;
using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.ClipsAndTracks.Text;
using projectFrameCut.Shared;
using System.Text.Json;

namespace projectFrameCut.ApplicationPluginBase.DynamicPreviewProvider;

/// <summary>
/// Thin shim around <see cref="TextLayoutPipeline"/> that returns
/// <see cref="Microsoft.Maui.Graphics.Rect"/> values for callers that already
/// speak the MAUI graphics types.
/// </summary>
internal static class TextMeasureHelper
{
    public static Rect MeasureBounds(TextClip clip)
    {
        var entries = ResolveEntries(clip);
        if (entries.Count == 0)
            return new Rect(0, 0, 1, 1);

        float clipW = clip.TargetWidth > 0 ? clip.TargetWidth : 1920;
        float clipH = clip.TargetHeight > 0 ? clip.TargetHeight : 1080;
        return MeasureBounds(entries, clipW, clipH);
    }

    public static Rect MeasureBounds(IReadOnlyList<TextEntry> entries, float clipWidth, float clipHeight)
    {
        var ctx = TextLayoutContext.FromCanvas(clipWidth, clipHeight);
        var bounds = TextLayoutPipeline.Measure(entries, ctx);
        if (bounds.Width <= 0f && bounds.Height <= 0f)
            return new Rect(0, 0, 1, 1);
        return new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    internal static IReadOnlyList<TextEntry> ResolveEntries(TextClip clip)
    {
        if (clip.ExtraData?.TryGetValue("TextEntries", out var raw) == true)
        {
            if (raw is List<TextEntry> list && list.Count > 0) return list;
            if (raw is JsonElement je)
            {
                try
                {
                    var parsed = je.Deserialize<List<TextEntry>>();
                    if (parsed is { Count: > 0 }) { clip.ExtraData["TextEntries"] = parsed; return parsed; }
                }
                catch { }
            }
            if (raw is string json && !string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<List<TextEntry>>(json);
                    if (parsed is { Count: > 0 }) { clip.ExtraData["TextEntries"] = parsed; return parsed; }
                }
                catch { }
            }
        }
        return clip.TextEntries;
    }
}
