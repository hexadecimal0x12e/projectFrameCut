using System.Text.Json;
using projectFrameCut.Drawing.Text.Entry;

namespace projectFrameCut.Shared;

public static class TextEntryExtensions
{
    public static bool GetUseVerticalLayout(this TextEntry entry)
        => entry.ExtraData.TryGetValue(TextEntryExtraDataKeys.UseVerticalLayout, out var v) && v is true;

    public static void SetUseVerticalLayout(this TextEntry entry, bool value)
        => entry.ExtraData[TextEntryExtraDataKeys.UseVerticalLayout] = value;

    public static bool GetKeepNonCJKTextAsHorizontal(this TextEntry entry)
        => entry.ExtraData.TryGetValue(TextEntryExtraDataKeys.KeepNonCJKTextAsHorizontal, out var v) && v is true;

    public static void SetKeepNonCJKTextAsHorizontal(this TextEntry entry, bool value)
        => entry.ExtraData[TextEntryExtraDataKeys.KeepNonCJKTextAsHorizontal] = value;

    public static float? GetWrappingWidth(this TextEntry entry)
    {
        if (entry.ExtraData.TryGetValue(TextEntryExtraDataKeys.WrappingWidth, out var v))
        {
            if (v is float f) return f;
            if (v is double d) return (float)d;
            if (v is JsonElement je && je.ValueKind == JsonValueKind.Number) return je.GetSingle();
        }
        return null;
    }

    public static void SetWrappingWidth(this TextEntry entry, float? value)
    {
        if (value.HasValue)
            entry.ExtraData[TextEntryExtraDataKeys.WrappingWidth] = value.Value;
        else
            entry.ExtraData.Remove(TextEntryExtraDataKeys.WrappingWidth);
    }

    public static bool GetScaleWithTarget(this TextEntry entry)
        => !entry.ExtraData.TryGetValue(TextEntryExtraDataKeys.ScaleWithTarget, out var v) || v is true;

    public static void SetScaleWithTarget(this TextEntry entry, bool value)
        => entry.ExtraData[TextEntryExtraDataKeys.ScaleWithTarget] = value;

    public static float? GetDpi(this TextEntry entry)
    {
        if (entry.ExtraData.TryGetValue(TextEntryExtraDataKeys.Dpi, out var v))
        {
            if (v is float f) return f;
            if (v is double d) return (float)d;
            if (v is JsonElement je && je.ValueKind == JsonValueKind.Number) return je.GetSingle();
        }
        return null;
    }

    public static void SetDpi(this TextEntry entry, float? value)
    {
        if (value.HasValue)
            entry.ExtraData[TextEntryExtraDataKeys.Dpi] = value.Value;
        else
            entry.ExtraData.Remove(TextEntryExtraDataKeys.Dpi);
    }

    public static string GetStyleId(this TextEntry entry)
        => entry.ExtraData.TryGetValue(TextEntryExtraDataKeys.StyleId, out var v) && v is string s ? s : "";

    public static void SetStyleId(this TextEntry entry, string value)
        => entry.ExtraData[TextEntryExtraDataKeys.StyleId] = value;

    public static string? GetSampleText(this TextEntry entry)
        => entry.ExtraData.TryGetValue(TextEntryExtraDataKeys.SampleText, out var v) && v is string s ? s : null;

    public static void SetSampleText(this TextEntry entry, string? value)
    {
        if (value is not null)
            entry.ExtraData[TextEntryExtraDataKeys.SampleText] = value;
        else
            entry.ExtraData.Remove(TextEntryExtraDataKeys.SampleText);
    }

    public static TextLanguage GetLanguage(this TextEntry entry)
    {
        if (entry.ExtraData.TryGetValue(TextEntryExtraDataKeys.Language, out var v))
        {
            if (v is TextLanguage tl) return tl;
            if (v is string s && Enum.TryParse<TextLanguage>(s, out var parsed)) return parsed;
            if (v is JsonElement je && je.ValueKind == JsonValueKind.String && Enum.TryParse<TextLanguage>(je.GetString(), out var jp)) return jp;
        }
        return TextLanguage.Unknown;
    }

    public static void SetLanguage(this TextEntry entry, TextLanguage value)
        => entry.ExtraData[TextEntryExtraDataKeys.Language] = value.ToString();

    public static bool GetShouldInSubtrack(this TextEntry entry)
        => entry.ExtraData.TryGetValue(TextEntryExtraDataKeys.ShouldInSubtrack, out var v) && v is true;

    public static void SetShouldInSubtrack(this TextEntry entry, bool value)
        => entry.ExtraData[TextEntryExtraDataKeys.ShouldInSubtrack] = value;

    public static ClipFontStyle GetFontStyleEnum(this TextEntry entry)
        => Enum.TryParse<ClipFontStyle>(entry.FontStyle, true, out var fs) ? fs : ClipFontStyle.Regular;

    public static void SetFontStyleEnum(this TextEntry entry, ClipFontStyle style)
        => entry.FontStyle = style.ToString();

    public static ClipVerticalAlignment GetVerticalAlignment(this TextEntry entry)
    {
        if (entry.ExtraData.TryGetValue(TextEntryExtraDataKeys.VerticalAlignment, out var v))
        {
            if (v is ClipVerticalAlignment va) return va;
            if (v is string s && Enum.TryParse<ClipVerticalAlignment>(s, true, out var parsed)) return parsed;
            if (v is JsonElement je && je.ValueKind == JsonValueKind.String
                && Enum.TryParse<ClipVerticalAlignment>(je.GetString(), true, out var jp)) return jp;
        }
        return ClipVerticalAlignment.Top;
    }

    public static void SetVerticalAlignment(this TextEntry entry, ClipVerticalAlignment value)
        => entry.ExtraData[TextEntryExtraDataKeys.VerticalAlignment] = value.ToString();

    public static TextClipLayoutMode GetLayoutMode(this TextEntry entry)
    {
        if (entry.ExtraData.TryGetValue(TextEntryExtraDataKeys.LayoutMode, out var v))
        {
            if (v is TextClipLayoutMode lm) return lm;
            if (v is string s && Enum.TryParse<TextClipLayoutMode>(s, true, out var parsed)) return parsed;
            if (v is JsonElement je && je.ValueKind == JsonValueKind.String
                && Enum.TryParse<TextClipLayoutMode>(je.GetString(), true, out var jp)) return jp;
        }
        return TextClipLayoutMode.FillClip;
    }

    public static void SetLayoutMode(this TextEntry entry, TextClipLayoutMode value)
        => entry.ExtraData[TextEntryExtraDataKeys.LayoutMode] = value.ToString();

    public static float? GetFixedHeightValue(this TextEntry entry)
    {
        if (entry.ExtraData.TryGetValue(TextEntryExtraDataKeys.FixedHeightValue, out var v))
        {
            if (v is float f) return f;
            if (v is double d) return (float)d;
            if (v is JsonElement je && je.ValueKind == JsonValueKind.Number) return je.GetSingle();
        }
        return null;
    }

    public static void SetFixedHeightValue(this TextEntry entry, float? value)
    {
        if (value.HasValue)
            entry.ExtraData[TextEntryExtraDataKeys.FixedHeightValue] = value.Value;
        else
            entry.ExtraData.Remove(TextEntryExtraDataKeys.FixedHeightValue);
    }
}

/// <summary>
/// One-shot helper for migrating <see cref="TextEntry"/> instances that were
/// serialised under the legacy "mixed-normalised" coordinate convention into
/// the new pixel-space convention.
///
/// <para>
/// Background — the previous renderer normalised at use-time via
/// <c>TextEntryHelper.NormalizeForTypesetting</c>, dividing <c>X</c> by canvas
/// width but <c>Y</c>, <c>FontSize</c>, <c>StrokeThickness</c> and
/// <c>WrappingWidth</c> by canvas height. Projects saved by older builds may
/// contain <see cref="TextEntry"/> values that already carry pre-normalised
/// numbers (≪ 1) instead of pixel-space numbers. Loading such a project under
/// the new pipeline would render text as a tiny speck.
/// </para>
///
/// <para>
/// This helper is <b>not</b> applied automatically. Migration from the
/// legacy <c>TextClipEntry</c> record continues to be handled by
/// <see cref="TextEntryHelper.MigrateFromTextClipEntry"/> (those entries were
/// already in pixel space). Use the methods below from a one-off project
/// upgrade command when you know the entries were saved in the legacy
/// mixed-normalised form.
/// </para>
/// </summary>
public static class TextEntryMigration
{
    /// <summary>
    /// Convert a legacy <see cref="TextClipEntry"/> (project-pixel coordinates,
    /// degrees, 1.0-baseline line spacing) into a new <see cref="TextEntry"/>
    /// using the new pipeline's pixel-space convention.
    /// <para>
    /// No coordinate normalisation is applied: <c>TextClipEntry.x/y/fontSize/etc.</c>
    /// were already in project pixels and remain so on the migrated entry.
    /// Rotation is converted to radians; line spacing is rebased from
    /// "1.0 = single" (legacy) to "0.0 = single" (new).
    /// </para>
    /// </summary>
    public static TextEntry MigrateFromTextClipEntry(TextClipEntry old)
    {
        var entry = new TextEntry
        {
            Text = old.text ?? string.Empty,
            FontName = old.fontFamily ?? string.Empty,
            FontSize = old.fontSize,
            X = old.x,
            Y = old.y,
            FontStyle = old.fontStyle.ToString(),
            FillR = old.r,
            FillG = old.g,
            FillB = old.b,
            FillA = old.a ?? 1f,
            StrokeR = old.strokeR,
            StrokeG = old.strokeG,
            StrokeB = old.strokeB,
            StrokeThickness = old.strokeWidth ?? 0f,
            StrokeA = (old.strokeWidth ?? 0f) > 0f ? 1f : 0f,
            LineSpacing = old.lineSpacing - 1f,
            Rotation = old.rotation * MathF.PI / 180f,
            Alignment = old.horizontalAlignment switch
            {
                ClipHorizontalAlignment.Center => TextAlignment.Center,
                ClipHorizontalAlignment.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            },
        };
        entry.SetUseVerticalLayout(old.UseVerticalLayout);
        entry.SetKeepNonCJKTextAsHorizontal(old.KeepNonCJKTextAsHorizontal);
        entry.SetWrappingWidth(old.wrappingWidth);
        entry.SetScaleWithTarget(old.ScaleWithTarget);
        entry.SetDpi(old.dpi);
        entry.SetStyleId(old.StyleId);
        entry.SetSampleText(old.SampleText);
        entry.SetLanguage(old.Language);
        entry.SetShouldInSubtrack(old.ShouldInSubtrack);
        entry.SetVerticalAlignment(old.verticalAlignment);
        return entry;
    }

    /// <summary>List overload of <see cref="MigrateFromTextClipEntry"/>.</summary>
    public static List<TextEntry> MigrateFromTextClipEntries(IReadOnlyList<TextClipEntry> oldEntries)
    {
        var result = new List<TextEntry>(oldEntries.Count);
        foreach (var old in oldEntries)
        {
            result.Add(MigrateFromTextClipEntry(old));
        }
        return result;
    }

    /// <summary>
    /// Convert a single entry from the legacy mixed-normalised form to
    /// pixel space, assuming it was saved against a canvas of
    /// <paramref name="assumedCanvasWidth"/> × <paramref name="assumedCanvasHeight"/>.
    /// </summary>
    public static TextEntry RebaseFromLegacyNormalizedSpace(
        TextEntry legacyEntry,
        float assumedCanvasWidth,
        float assumedCanvasHeight)
    {
        if (legacyEntry is null) throw new ArgumentNullException(nameof(legacyEntry));
        var w = assumedCanvasWidth > 0 ? assumedCanvasWidth : 1920f;
        var h = assumedCanvasHeight > 0 ? assumedCanvasHeight : 1080f;

        var migrated = legacyEntry with
        {
            X = legacyEntry.X * w,
            Y = legacyEntry.Y * h,
            FontSize = legacyEntry.FontSize * h,
            StrokeThickness = legacyEntry.StrokeThickness * h,
            CharacterSpacing = legacyEntry.CharacterSpacing * h,
            WordSpacing = legacyEntry.WordSpacing * h,
            ExtraData = new Dictionary<string, object>(legacyEntry.ExtraData),
        };

        var ww = legacyEntry.GetWrappingWidth();
        if (ww.HasValue && ww.Value > 0f)
            migrated.SetWrappingWidth(ww.Value * h);

        return migrated;
    }

    /// <summary>List overload of
    /// <see cref="RebaseFromLegacyNormalizedSpace(TextEntry, float, float)"/>.</summary>
    public static List<TextEntry> RebaseFromLegacyNormalizedSpace(
        IReadOnlyList<TextEntry> legacyEntries,
        float assumedCanvasWidth,
        float assumedCanvasHeight)
    {
        if (legacyEntries is null) throw new ArgumentNullException(nameof(legacyEntries));
        var result = new List<TextEntry>(legacyEntries.Count);
        foreach (var entry in legacyEntries)
            result.Add(RebaseFromLegacyNormalizedSpace(entry, assumedCanvasWidth, assumedCanvasHeight));
        return result;
    }
}

