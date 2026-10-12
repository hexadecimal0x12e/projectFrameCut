using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class ClipLayerOrderTests
{
    [TestMethod]
    public void AuxiliaryTracksRenderAfterAllMainTracks()
    {
        var clips = new[] { Clip(10000), Clip(0), Clip(10001), Clip(9999), Clip(1) };
        CollectionAssert.AreEqual(new uint[] { 9999, 1, 0, 10001, 10000 },
            clips.OrderBy(c => c, ClipLayerOrder.BottomToTop).Select(c => c.LayerIndex).ToArray());
    }

    [TestMethod]
    public void PreviewZIndexMatchesRenderOrderWithinAndAcrossTrackGroups()
    {
        var clips = new uint[] { 0, 1, 9999, 10000, 10001, 11000 }
            .SelectMany(layer => new uint[] { 0, 1, 9999 }.Select(sub => Clip(layer, sub))).ToArray();
        CollectionAssert.AreEqual(clips.OrderBy(c => c, ClipLayerOrder.BottomToTop).ToArray(),
            clips.OrderBy(c => ClipLayerOrder.GetPreviewZIndex(c.LayerIndex, c.SubLayerIndex)).ToArray());
    }

    [TestMethod]
    public void PreviewZIndexKeepsExtremeLayersAboveBackgroundAndInTheirTrackGroup()
    {
        Assert.IsTrue(ClipLayerOrder.GetPreviewZIndex(9999, uint.MaxValue) > int.MinValue);
        Assert.IsTrue(ClipLayerOrder.GetPreviewZIndex(uint.MaxValue, uint.MaxValue)
            > ClipLayerOrder.GetPreviewZIndex(0, 0));
    }

    private static IClip Clip(uint layer, uint sub = 0) => new TextClip
    {
        Id = Guid.NewGuid(), Name = "layer", LayerIndex = layer, SubLayerIndex = sub, ExtraData = []
    };
}
