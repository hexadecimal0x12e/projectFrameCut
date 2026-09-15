using Microsoft.Maui.Controls;

namespace projectFrameCut.ApplicationAPIBase.Helpers;

public enum PointerCursorKind
{
    Hand,
    Move,
    HorizontalResize,
    VerticalResize,
    NorthwestSoutheastResize,
    NortheastSouthwestResize
}

public static partial class PointerCursorHelper
{
    public static void SetCursor(View view, PointerCursorKind? cursorKind)
        => SetCursorCore(view, cursorKind);

    static partial void SetCursorCore(View view, PointerCursorKind? cursorKind);
}
