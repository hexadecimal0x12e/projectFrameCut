namespace projectFrameCut.Render.ClipsAndTracks.Text;

/// <summary>
/// A float-precision rectangle in project-canvas pixel coordinates, as returned by
/// <see cref="TextLayoutPipeline.Measure"/>.
/// </summary>
public readonly record struct TextBounds(float X, float Y, float Width, float Height)
{
    public static readonly TextBounds Empty = new(0f, 0f, 0f, 0f);

    public float Right => X + Width;
    public float Bottom => Y + Height;

    public TextBounds Union(TextBounds other)
    {
        if (Width <= 0f && Height <= 0f) return other;
        if (other.Width <= 0f && other.Height <= 0f) return this;
        var x = MathF.Min(X, other.X);
        var y = MathF.Min(Y, other.Y);
        var r = MathF.Max(Right, other.Right);
        var b = MathF.Max(Bottom, other.Bottom);
        return new TextBounds(x, y, r - x, b - y);
    }
}
