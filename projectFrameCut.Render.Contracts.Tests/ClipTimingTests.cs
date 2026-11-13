using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Transform;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class ClipTimingTests
{
    [TestMethod]
    public void FrameProjectionSurvivesRepeatedZooms()
    {
        uint start = 101, duration = 749;
        foreach (double scale in new[] { .01, .083, 1.2, 100, .7, 1 })
        {
            for (int i = 0; i < 100; i++)
            {
                Assert.AreEqual(start, ClipTiming.RoundFrame(start / scale * scale));
                Assert.AreEqual(duration, ClipTiming.RoundFrame(duration / scale * scale));
                Assert.AreEqual(start + duration, ClipTiming.RoundFrame((start + duration) / scale * scale));
                Assert.AreEqual(uint.MaxValue, ClipTiming.RoundFrame(uint.MaxValue / scale * scale));
            }
        }
        Assert.AreEqual(101u, ClipTiming.RoundFrame(100.5));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ClipTiming.RoundFrame(double.NaN));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ClipTiming.RoundFrame((double)uint.MaxValue + 1));
    }

    [TestMethod]
    public void AvoidanceJoinsExactFramesAndEnablesTheBoundTransform()
    {
        var left = new TransformClipInfo(Guid.NewGuid(), 0, 750, 0, 0, []);
        var right = new TransformClipInfo(Guid.NewGuid(), 747, 300, 0, 0, []);
        var provider = new CrossfadeTransformProvider();
        TransformProcessing.Configure(provider, TransformSide.Right, TransformInputMode.TwoInput, 10, right.Id, TransformRenderOrder.AfterEffects);
        left = left with { Providers = [EffectBindingHelper.SerializeProvider(provider)] };
        Assert.IsNull(TransformProcessing.Resolve([left, right], left.Id, TransformSide.Right));
        right = right with { Start = (uint)ClipTiming.NearestPosition(right.Start, 0, uint.MaxValue - right.Duration,
            [((long)left.Start - right.Duration + 1, (long)left.End - 1)]) };
        Assert.AreEqual(left.End, (ulong)right.Start);
        Assert.IsNotNull(TransformProcessing.Resolve([left, right], left.Id, TransformSide.Right));
        Assert.IsNull(TransformProcessing.Resolve([left, right with { Start = right.Start + 1 }], left.Id, TransformSide.Right));
        Assert.IsNull(TransformProcessing.Resolve([left, right with { SubLayer = 1 }], left.Id, TransformSide.Right));
    }

    [TestMethod]
    public void AvoidancePreservesGapsInsideAMovingGroup()
    {
        // Two moving clips [0,10), [20,30) fit around the stationary [10,20).
        Assert.AreEqual(0L, ClipTiming.NearestPosition(0, 0, 100, [(1, 19), (-19, -1)]));
        Assert.AreEqual(21L, ClipTiming.NearestPosition(10, -100, 100, [(-10, 10), (11, 20)]));
        Assert.AreEqual(6L, ClipTiming.NearestPosition(5, 0, 100, [(5, 5)]));
        Assert.AreEqual(10L, ClipTiming.NearestPosition(-1, 0, 100, [(0, 9)]));
        Assert.AreEqual((long)uint.MaxValue - 10, ClipTiming.NearestPosition(uint.MaxValue, 0, (long)uint.MaxValue - 10, []));
        Assert.ThrowsExactly<InvalidOperationException>(() => ClipTiming.NearestPosition(5, 0, 10, [(0, 10)]));
    }

    [TestMethod]
    public void TrimmedSpeedDurationMatchesTheRendererAndKeepsTheStart()
    {
        foreach (float ratio in new[] { .5f, 2f })
        {
            var speed = new ClassicSpeedVarianceProvider { Ratio = ratio };
            IClip clip = new VideoClip { Id = Guid.NewGuid(), Name = "timing", StartFrame = 100, Duration = 101, RelativeStartFrame = 20,
                SpeedVarianceProviderInstance = speed };
            Assert.AreEqual(clip.GetEffectiveDuration(), ClipTiming.EffectiveDuration(101, speed));
            var range = TransformClipInfo.FromClip(clip);
            Assert.AreEqual(100u, range.Start);
            Assert.AreEqual((ulong)range.Start + clip.GetEffectiveDuration(), range.End);
            uint source = ClipTiming.SourceOffset(10, clip.Duration, speed);
            Assert.AreEqual(source + clip.RelativeStartFrame, clip.TryGetRelativeFrameIndex(110, null)!.Value);
            Assert.AreEqual(clip.Duration, ClipTiming.SourceOffset(clip.GetEffectiveDuration(), clip.Duration, speed));
        }
    }
}
