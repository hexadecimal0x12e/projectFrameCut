namespace projectFrameCut.Render.RenderAPIBase.ClipAndTrack;

public static class ClipLayerOrder
{
    public const int SubTrackOffset = 10000;

    public static IComparer<IClip> BottomToTop { get; } = Comparer<IClip>.Create(static (left, right) =>
        Compare(left.LayerIndex, left.SubLayerIndex, right.LayerIndex, right.SubLayerIndex));

    public static int Compare(uint leftLayer, uint leftSubLayer, uint rightLayer, uint rightSubLayer)
    {
        int group = (leftLayer >= SubTrackOffset).CompareTo(rightLayer >= SubTrackOffset);
        if (group != 0) return group;
        int layer = rightLayer.CompareTo(leftLayer);
        return layer != 0 ? layer : rightSubLayer.CompareTo(leftSubLayer);
    }

    public static int GetPreviewZIndex(uint layer, uint subLayer)
    {
        bool auxiliary = layer >= SubTrackOffset;
        long index = (long)(auxiliary ? layer - SubTrackOffset : layer) * 10000 + Math.Min(9999u, subLayer);
        return auxiliary
            ? int.MaxValue - (int)Math.Min(index, int.MaxValue)
            : -1 - (int)Math.Min(index, int.MaxValue - 1L);
    }
}
