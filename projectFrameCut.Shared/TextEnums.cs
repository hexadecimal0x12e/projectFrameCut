namespace projectFrameCut.Shared;

public enum ClipFontStyle
{
    Regular,
    Bold,
    Italic,
    BoldItalic
}

public enum ClipHorizontalAlignment
{
    Left,
    Center,
    Right
}

public enum ClipVerticalAlignment
{
    Top,
    Center,
    Bottom
}

public enum TextClipLayoutMode
{
    FillClip,
    FixedSize,
    FixedWidth,
    FixedHeight
}

public static class TextEntryExtraDataKeys
{
    public const string UseVerticalLayout = "UseVerticalLayout";
    public const string KeepNonCJKTextAsHorizontal = "KeepNonCJKTextAsHorizontal";
    public const string WrappingWidth = "WrappingWidth";
    public const string ScaleWithTarget = "ScaleWithTarget";
    public const string Dpi = "Dpi";

    public const string StyleId = "StyleId";
    public const string Language = "Language";
    public const string SampleText = "SampleText";
    public const string ShouldInSubtrack = "ShouldInSubtrack";

    /// <summary>
    /// Vertical alignment of the rendered text block within the clip box.
    /// Stored as the string form of <see cref="ClipVerticalAlignment"/>.
    /// Defaults to <see cref="ClipVerticalAlignment.Top"/> when missing.
    /// </summary>
    public const string VerticalAlignment = "VerticalAlignment";

    /// <summary>
    /// Layout mode the entry should be typeset with. Stored as the string form
    /// of <see cref="TextClipLayoutMode"/>. Defaults to
    /// <see cref="TextClipLayoutMode.FillClip"/> when missing.
    /// </summary>
    public const string LayoutMode = "LayoutMode";

    /// <summary>
    /// Fixed height in project-canvas pixels used by
    /// <see cref="TextClipLayoutMode.FixedHeight"/> mode. The pipeline shrinks
    /// or grows the font size so the rendered text block matches this height.
    /// </summary>
    public const string FixedHeightValue = "FixedHeightValue";
}

