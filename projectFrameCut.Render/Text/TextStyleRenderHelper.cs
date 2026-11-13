using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Drawing.Text.FontHelper;
using projectFrameCut.Render.ClipsAndTracks.Text;
using System.Globalization;
using System.Text.Json;

namespace projectFrameCut.Render.Text;

internal static class TextStyleRenderHelper
{
    public static TextBounds MeasureBounds(IReadOnlyList<TextEntry> entries, float width, float height) =>
        TextLayoutPipeline.Measure(entries, TextLayoutContext.FromCanvas(width, height));

    public static FontFace? ResolveFont(string name)
    {
        if (TextClipFontRegistry.TryGetFont(name, out var face)) return face;
        return TextClipFontRegistry.FallbackFamilyName is { } fallback && TextClipFontRegistry.TryGetFont(fallback, out face)
            ? face : null;
    }
}

internal readonly record struct TextStyleColor(float Red, float Green, float Blue, Half Alpha)
{
    public static TextStyleColor FromArgb(string value)
    {
        if (value.StartsWith('{'))
        {
            using var json = JsonDocument.Parse(value);
            var colorValue = json.RootElement;
            return new(colorValue.GetProperty("r").GetUInt16() / 65535f,
                colorValue.GetProperty("g").GetUInt16() / 65535f,
                colorValue.GetProperty("b").GetUInt16() / 65535f,
                colorValue.TryGetProperty("a", out var alpha) ? (Half)alpha.GetSingle() : Half.One);
        }
        string hex = value.TrimStart('#');
        if (hex.Length is not (6 or 8)) throw new FormatException("Expected an RGB or ARGB color.");
        uint color = uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new((color >> 16 & 255) / 255f, (color >> 8 & 255) / 255f, (color & 255) / 255f,
            hex.Length == 8 ? (Half)((color >> 24) / 255f) : Half.One);
    }
}

internal static class TextStyleColors
{
    public static TextStyleColor White => new(1, 1, 1, Half.One);
    public static TextStyleColor Black => new(0, 0, 0, Half.One);
    public static TextStyleColor LightGray => TextStyleColor.FromArgb("#CFCFCF");
}
