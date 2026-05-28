using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Shared;
using System.Text.Json;

namespace projectFrameCut.ApplicationPluginBase.DynamicPreviewProvider;

internal static class TextMeasureHelper
{
    public static Rect MeasureBounds(TextClip clip)
    {
        var entries = ResolveEntries(clip);
        if (entries.Count == 0)
        {
            return new Rect(0, 0, 1, 1);
        }

        return MeasureBounds(entries);
    }

    public static Rect MeasureBounds(IEnumerable<TextClipEntry> entries)
    {
        var fontCollection = TextClip.GetFont();
        bool hasBounds = false;
        double minX = 0, minY = 0, maxX = 0, maxY = 0;

        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.text))
                continue;

            if (!fontCollection.TryGet(entry.fontFamily, out var family)
                && !fontCollection.TryGet("HarmonyOS_Sans_SC_Regular", out family))
            {
                family = fontCollection.Families.FirstOrDefault();
                if (family == default)
                    continue;
            }
            var font = family.CreateFont(entry.fontSize, entry.fontStyle);
            var dpi = entry.dpi ?? 72f;
            var strokePadding = entry.strokeWidth ?? 0f;

            double left, top, right, bottom;
            if (entry.UseVerticalLayout)
            {
                var glyphCount = entry.text.Count(c => c is not '\n' and not '\r');
                if (glyphCount <= 0) glyphCount = 1;
                var emSize = entry.fontSize * (dpi / 72f);
                var strokeExtra = strokePadding * 2f;
                var w = emSize + strokeExtra;
                var h = glyphCount * emSize * entry.lineSpacing + strokeExtra;

                double originX = entry.x;
                double originY = entry.y;

                switch (entry.horizontalAlignment)
                {
                    case SixLabors.Fonts.HorizontalAlignment.Center: originX -= w / 2d; break;
                    case SixLabors.Fonts.HorizontalAlignment.Right: originX -= w; break;
                }
                switch (entry.verticalAlignment)
                {
                    case SixLabors.Fonts.VerticalAlignment.Center: originY -= h / 2d; break;
                    case SixLabors.Fonts.VerticalAlignment.Bottom: originY -= h; break;
                }

                left = originX;
                top = originY;
                right = originX + w;
                bottom = originY + h;
            }
            else
            {
                var textOpts = new SixLabors.ImageSharp.Drawing.Processing.RichTextOptions(font)
                {
                    Dpi = dpi,
                    KerningMode = entry.applyKerning ? SixLabors.Fonts.KerningMode.Standard : SixLabors.Fonts.KerningMode.None,
                    LineSpacing = entry.lineSpacing,
                    HorizontalAlignment = entry.horizontalAlignment,
                    VerticalAlignment = entry.verticalAlignment,
                    Origin = new SixLabors.ImageSharp.PointF(entry.x, entry.y),
                };
                if (entry.wrappingWidth.HasValue)
                {
                    textOpts.WrappingLength = entry.wrappingWidth.Value;
                }

                var measured = SixLabors.Fonts.TextMeasurer.MeasureBounds(entry.text, textOpts);
                var strokeInflate = Math.Max(0f, strokePadding) * 0.5f;
                left = measured.X - strokeInflate;
                top = measured.Y - strokeInflate;
                right = measured.X + measured.Width + strokeInflate;
                bottom = measured.Y + measured.Height + strokeInflate;
            }

            if (right <= left) right = left + 1d;
            if (bottom <= top) bottom = top + 1d;

            if (Math.Abs(entry.rotation) > 0.0001f)
            {
                var radians = entry.rotation * Math.PI / 180d;
                var cos = Math.Cos(radians);
                var sin = Math.Sin(radians);
                static (double rx, double ry) Rot(double px, double py, double c, double s)
                    => (px * c - py * s, px * s + py * c);

                var centerX = (double)entry.x;
                var centerY = (double)entry.y;

                var p0 = Rot(left - centerX, top - centerY, cos, sin);
                var p1 = Rot(right - centerX, top - centerY, cos, sin);
                var p2 = Rot(left - centerX, bottom - centerY, cos, sin);
                var p3 = Rot(right - centerX, bottom - centerY, cos, sin);

                var rMinX = Math.Min(Math.Min(p0.rx, p1.rx), Math.Min(p2.rx, p3.rx));
                var rMinY = Math.Min(Math.Min(p0.ry, p1.ry), Math.Min(p2.ry, p3.ry));
                var rMaxX = Math.Max(Math.Max(p0.rx, p1.rx), Math.Max(p2.rx, p3.rx));
                var rMaxY = Math.Max(Math.Max(p0.ry, p1.ry), Math.Max(p2.ry, p3.ry));

                left = centerX + rMinX;
                top = centerY + rMinY;
                right = centerX + rMaxX;
                bottom = centerY + rMaxY;
            }

            if (!hasBounds)
            {
                minX = left; minY = top; maxX = right; maxY = bottom;
                hasBounds = true;
            }
            else
            {
                minX = Math.Min(minX, left);
                minY = Math.Min(minY, top);
                maxX = Math.Max(maxX, right);
                maxY = Math.Max(maxY, bottom);
            }
        }

        if (!hasBounds)
            return new Rect(0, 0, 1, 1);

        return new Rect(minX, minY, Math.Max(1d, maxX - minX), Math.Max(1d, maxY - minY));
    }

    internal static IReadOnlyList<TextClipEntry> ResolveEntries(TextClip clip)
    {
        if (clip.ExtraData?.TryGetValue("TextEntries", out var raw) == true)
        {
            if (raw is List<TextClipEntry> list && list.Count > 0) return list;
            if (raw is JsonElement je)
            {
                try
                {
                    var parsed = je.Deserialize<List<TextClipEntry>>();
                    if (parsed is { Count: > 0 }) { clip.ExtraData["TextEntries"] = parsed; return parsed; }
                }
                catch { }
            }
            if (raw is string json && !string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<List<TextClipEntry>>(json);
                    if (parsed is { Count: > 0 }) { clip.ExtraData["TextEntries"] = parsed; return parsed; }
                }
                catch { }
            }
        }
        return clip.TextEntries;
    }

}
