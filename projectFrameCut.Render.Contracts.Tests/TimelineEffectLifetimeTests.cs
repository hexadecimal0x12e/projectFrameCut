using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Rendering;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class TimelineEffectLifetimeTests
{
    private sealed class DisposableCrop : ProgressCropper_IPicture, IContinuousEffect, IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
        IPicture IContinuousEffect.Render(IPicture source, float progress, int targetWidth, int targetHeight)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            return Render(source, progress, targetWidth, targetHeight);
        }
    }

    [TestMethod]
    public void HashingPreservesLiveEffectsAndIgnoresRuntimeIds()
    {
        using var clip = new SolidColorClip { Id = Guid.NewGuid(), Name = "crop", Duration = 10, ExtraData = new() };
        var effect = new DisposableCrop { Id = Guid.NewGuid().ToString(), Width = 2, Height = 2 };
        clip.EffectsInstances = [effect];
        var hash = Timeline.GetClipFrameHash([clip], clip, 0);
        var frameHash = Timeline.GetFrameHash([clip], 0);

        Assert.AreNotEqual("__error__", hash);
        Assert.AreNotEqual("__error__", frameHash);
        Assert.AreSame(effect, clip.EffectsInstances.Single());
        Assert.IsFalse(effect.Disposed);

        effect.Id = Guid.NewGuid().ToString();
        Assert.AreEqual(hash, Timeline.GetClipFrameHash([clip], clip, 0));
        Assert.AreEqual(frameHash, Timeline.GetFrameHash([clip], 0));
        Assert.IsFalse(effect.Disposed);
    }

    [TestMethod]
    public void HashingIncludesSavedEffectParametersAndProviderBindings()
    {
        var effect = new EffectAndMixtureJSONStructure { TypeName = "ProgressCrop", Parameters = new() { ["Width"] = 2 } };
        var provider = new EffectProviderJSONStructure { TypeName = "ProgressCrop", StaticFields = new() { ["Width"] = 2 } };
        using var clip = new SolidColorClip
        {
            Id = Guid.NewGuid(), Name = "crop", Duration = 10, ExtraData = new(), Effects = [effect], EffectProviders = [provider],
        };
        var hash = Timeline.GetClipFrameHash([clip], clip, 0);
        Assert.AreNotEqual("__error__", hash);

        effect.Parameters["Width"] = 1;
        var changedEffect = Timeline.GetClipFrameHash([clip], clip, 0);
        Assert.AreNotEqual(hash, changedEffect);
        provider.StaticFields["Width"] = 1;
        var changedProvider = Timeline.GetClipFrameHash([clip], clip, 0);
        Assert.AreNotEqual(changedEffect, changedProvider);
        provider.AnchorsBindingState["Input"] = Guid.NewGuid().ToString();
        Assert.AreNotEqual(changedProvider, Timeline.GetClipFrameHash([clip], clip, 0));
    }

    [TestMethod]
    public void PreparedTimelineFramesReuseEffectsUntilClipDisposal()
    {
        using var clip = new SolidColorClip
        {
            Id = Guid.NewGuid(), Name = "crop", Duration = 10, ExtraData = new(), OutputWidth = 2, OutputHeight = 2,
        };
        var effect = new DisposableCrop { Width = 2, Height = 2 };
        clip.EffectsInstances = [effect];
        foreach (uint frameIndex in new uint[] { 0, 1 })
        {
            var frame = Timeline.GetFramesInOneFrame([clip], frameIndex, 2, 2, initializeClips: false).Single();
            try
            {
                Assert.AreSame(effect, frame.Effects.Single());
                Assert.AreSame(effect, clip.EffectsInstances.Single());
                Assert.IsFalse(effect.Disposed);
                using var cropped = ((IContinuousEffect)frame.Effects.Single()).Render(frame.Clip, 0, 2, 2);
                Assert.AreEqual(2, cropped.Width);
                Assert.AreEqual(2, cropped.Height);
            }
            finally
            {
                frame.Clip.Dispose(true);
            }
        }
        ((IClip)clip).Dispose();
        Assert.IsTrue(effect.Disposed);
    }
}
