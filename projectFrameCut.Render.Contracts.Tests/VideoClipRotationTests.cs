using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.PluginIsolation;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Shared;
using System.Text.Json;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class VideoClipRotationTests
{
    [TestMethod]
    public void SourceFrameRemainsUnrotated()
    {
        using IClip clip = new VirtualSourceVideoClip
        {
            Id = Guid.NewGuid(), Name = "rotation", Duration = 1, Rotation = 90,
            VirtualSource = new TestSource(() => new Picture8bpp(3, 2) { r = [1, 2, 3, 4, 5, 6] })
        };
        using var frame = clip.GetFrameRelativeToStartPointOfSource(0, 3, 2, IPicture.PicturePixelMode.BytePicture);
        Assert.AreEqual(3, frame.Width);
        Assert.AreEqual(2, frame.Height);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6 }, ((IPicture<byte>)frame).r);
    }

    [TestMethod]
    public void CompositingRotatesImmutableClips()
    {
        using IClip clip = new SolidColorClip
        {
            Id = Guid.NewGuid(), Name = "solid", Duration = 1, Rotation = 90,
            TargetWidth = 4, TargetHeight = 2, ExtraData = new()
        };
        using var source = clip.GetFrame(0, 4, 2, IPicture.PicturePixelMode.BytePicture);
        Assert.AreEqual(4, source.Width);
        Assert.AreEqual(2, source.Height);
        using var result = Timeline.MixtureLayers([new OneFrame(0, clip, source, resolveEffects: false)], 0, 4, 2,
            clipLocalOutput: true);
        Assert.AreEqual(2, result.Width);
        Assert.AreEqual(4, result.Height);
    }

    [TestMethod]
    public void CompositingAppliesRotationAfterPictureEffects()
    {
        using IClip clip = new VirtualSourceVideoClip
        {
            Id = Guid.NewGuid(), Name = "rotation", Duration = 1, Rotation = 90,
            TargetWidth = 3, TargetHeight = 2, ExtraData = new(),
            VirtualSource = new TestSource(() => new Picture8bpp(3, 2) { r = [1, 2, 3, 4, 5, 6] }),
            EffectsInstances = [new InspectEffect(p =>
            {
                Assert.AreEqual(3, p.Width);
                Assert.AreEqual(2, p.Height);
            })]
        };
        using var source = VideoClipRotation.ReadFrame(clip, 0, 3, 2, IPicture.PicturePixelMode.BytePicture);
        using var result = Timeline.MixtureLayers([new OneFrame(0, clip, source, resolveEffects: false)], 0, 3, 2,
            clipLocalOutput: true);
        CollectionAssert.AreEqual(new byte[] { 4, 1, 5, 2, 6, 3 }, ((IPicture<byte>)result).r);
    }

    [TestMethod]
    public void BeforeLayoutOutputKeepsPictureEffectsWithoutResizingRotatingOrPlacing()
    {
        bool effectRan = false;
        using IClip clip = new SolidColorClip
        {
            Id = Guid.NewGuid(), Name = "before layout", Duration = 1, Rotation = 90,
            TargetX = 100, TargetY = 100, TargetWidth = 8, TargetHeight = 8, ExtraData = new(),
            EffectsInstances = [new InspectEffect(p =>
            {
                effectRan = true;
                ((IPicture<byte>)p).r[0] = 42;
            })]
        };
        using var source = new Picture8bpp(3, 2) { r = [1, 2, 3, 4, 5, 6] };
        using var result = Timeline.MixtureLayers([new OneFrame(0, clip, source, resolveEffects: false)], 0, 20, 20,
            beforeLayoutOutput: true);
        Assert.IsTrue(effectRan);
        Assert.AreEqual(3, result.Width);
        Assert.AreEqual(2, result.Height);
        CollectionAssert.AreEqual(new byte[] { 42, 2, 3, 4, 5, 6 }, ((IPicture<byte>)result).r);
    }

    [TestMethod]
    public void CompositingUsesAbsoluteAndDeltaRotation()
    {
        using IClip clip = new VirtualSourceVideoClip
        {
            Id = Guid.NewGuid(), Name = "rotation", Duration = 1, Rotation = 37,
            TargetWidth = 3, TargetHeight = 2, ExtraData = new(),
            VirtualSource = new TestSource(() => new Picture8bpp(3, 2) { r = [1, 2, 3, 4, 5, 6] }),
            EffectsInstances = [new PositionEffect(new(0, 0, 3, 2, false, 90)),
                new PositionEffect(new(0, 0, 0, 0, true, 90)) { Index = 1 }]
        };
        using var source = VideoClipRotation.ReadFrame(clip, 0, 3, 2, IPicture.PicturePixelMode.BytePicture);
        using var result = Timeline.MixtureLayers([new OneFrame(0, clip, source, resolveEffects: false)], 0, 3, 2,
            clipLocalOutput: true);
        CollectionAssert.AreEqual(new byte[] { 6, 5, 4, 3, 2, 1 }, ((IPicture<byte>)result).r);
        Assert.AreEqual(37f, clip.Rotation);
    }

    [TestMethod]
    public void RotationArgumentsAndPositionSerializationKeepFractionalDegrees()
    {
        using IClip clip = new SolidColorClip { Id = Guid.NewGuid(), Name = "rotation", Rotation = 45, ExtraData = new() };
        var copy = clip.HandleArguments(new Dictionary<string, object> { [nameof(IClip.Rotation)] = 22.5f });
        Assert.AreEqual(45f, clip.Rotation);
        Assert.AreEqual(22.5f, copy.Rotation);
        var position = JsonSerializer.Deserialize<ClipPositionTuple>(JsonSerializer.Serialize(copy.PositionTuple));
        Assert.AreEqual(22.5f, position.Rotation);
        Assert.AreEqual(position, IsolationValueConverter.ToObject(IsolationValueConverter.FromObject(position)));
    }

    [TestMethod]
    public void ExpandedCanvasIsTransparentForOpaqueSources()
    {
        using var source = Picture8bpp.GenerateSolidColor(4, 2, 255, 0, 0, null);
        using var result = VideoClipRotation.Rotate(source, 45);
        Assert.AreEqual(5, result.Width);
        Assert.AreEqual(5, result.Height);
        Assert.IsTrue(result.HasAlphaChannel);
        Assert.AreEqual(0f, ((IPicture<byte>)result).a![0]);
        Assert.AreEqual(1f, ((IPicture<byte>)result).a![12]);
        var bounds = VideoClipRotation.GetBounds(45, 10, 20, 4, 2);
        Assert.AreEqual(12d, bounds.X + bounds.Width / 2, 1e-9);
        Assert.AreEqual(21d, bounds.Y + bounds.Height / 2, 1e-9);
    }

    [TestMethod]
    public void TransparentPixelsDoNotDarkenInterpolatedEdges()
    {
        using var source = new Picture8bpp(2, 1) { r = [255, 0], a = [1, 0], HasAlphaChannel = true };
        using var result = VideoClipRotation.Rotate(source, 45);
        var picture = (IPicture<byte>)result;
        Assert.IsTrue(picture.a!.Any(a => a > 0 && a < 1));
        for (int i = 0; i < result.Pixels; i++)
            if (picture.a[i] > 0) Assert.AreEqual((byte)255, picture.r[i]);
    }

    [TestMethod]
    public void RotationPreservesHdrBrightnessAndPeak()
    {
        using var source = HDRPicture16bpp.GenerateSolidColor(3, 2, 65535, 0, 0, null, maximumBrightness: 1200);
        source.Brightness = [0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f];
        using var result = VideoClipRotation.Rotate(source, 90);
        var hdr = (IHDRPicture<ushort>)result;
        Assert.AreEqual(1200f, hdr.MaximumBrightness);
        CollectionAssert.AreEqual(new float[] { 0.4f, 0.1f, 0.5f, 0.2f, 0.6f, 0.3f }, hdr.Brightness);
        Assert.IsTrue(hdr.a!.All(a => a == 1));
    }

    [TestMethod]
    public void CompositingKeepsTheTargetCenterAndExpandedSize()
    {
        using IClip clip = new VirtualSourceVideoClip
        {
            Id = Guid.NewGuid(), Name = "rotation", Duration = 1, Rotation = 90,
            TargetX = 5, TargetY = 5, TargetWidth = 4, TargetHeight = 2,
            MixtureInstance = new ClassicOverlayMixture(),
            VirtualSource = new TestSource(() => Picture8bpp.GenerateSolidColor(4, 2, 255, 0, 0, null))
        };
        using var source = clip.GetFrameRelativeToStartPointOfSource(0, 4, 2, IPicture.PicturePixelMode.BytePicture);
        using var result = Timeline.MixtureLayers([new OneFrame(0, clip, source, resolveEffects: false)], 0, 20, 20,
            projectRelativeWidth: 20, projectRelativeHeight: 20, transparentBackground: true);
        var alpha = ((IPicture<byte>)result).a;
        Assert.IsNotNull(alpha);
        Assert.AreEqual(1f, alpha[4 * 20 + 6]);
        Assert.AreEqual(1f, alpha[7 * 20 + 7]);
        Assert.AreEqual(0f, alpha[5 * 20 + 5]);
        Assert.AreEqual(0f, alpha[8 * 20 + 6]);
    }

    [TestMethod]
    public void RotationChangesFrameCacheIdentity()
    {
        using var clip = new VideoClip { Id = Guid.NewGuid(), Name = "rotation", Duration = 1, ExtraData = new() };
        string hash = Timeline.GetClipFrameHash([clip], clip, 0);
        Assert.AreNotEqual("__error__", hash);
        clip.Rotation = 90;
        Assert.AreNotEqual(hash, Timeline.GetClipFrameHash([clip], clip, 0));
    }

    [TestMethod]
    public void LegacyRotationMigratesOnceAndPreservesOtherEffects()
    {
        var custom = new EffectAndMixtureJSONStructure { Name = "custom rotation", TypeName = "Rotation" };
        var dto = new ClipDraftDTO
        {
            FromPlugin = InternalPluginBase.InternalPluginBaseID, ClipType = ClipMode.PhotoClip,
            Effects = [new() { Name = VideoClipRotation.LegacyEffectName, TypeName = "Rotation", Parameters = new() { ["Angle"] = 90f } }, custom]
        };
        Assert.IsTrue(VideoClipRotation.Migrate(dto));
        Assert.AreEqual(90f, dto.Rotation);
        Assert.AreSame(custom, dto.Effects!.Single());
        Assert.IsFalse(VideoClipRotation.Migrate(dto));
        Assert.AreEqual(90f, JsonSerializer.Deserialize<ClipDraftDTO>(JsonSerializer.Serialize(dto))!.Rotation);
    }

    private sealed class InspectEffect(Action<IPicture> inspect) : RotationEffect_IPicture, INormalEffect
    {
        IPicture INormalEffect.Render(IPicture source, int width, int height)
        {
            inspect(source);
            return source;
        }
    }

    private sealed class PositionEffect(ClipPositionTuple position) : ProgressPlacer, IContinuousClipPositionProvider
    {
        ClipPositionTuple IContinuousClipPositionProvider.GetPosition(IClip clip, uint frame, int width, int height) => position;
    }

    private sealed class TestSource(Func<IPicture> generate) : IVirtualVideoSource
    {
        public string TypeName => nameof(TestSource);
        public void Init(int width, int height, int fps, uint targetDuration, IPicture.PicturePixelMode targetPPB) { }
        public IPicture Generate(uint index, bool hasAlpha) => generate();
        public void Dispose() { }
    }
}
