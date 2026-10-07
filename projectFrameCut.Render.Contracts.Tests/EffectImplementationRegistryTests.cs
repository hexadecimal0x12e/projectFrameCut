using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class EffectImplementationRegistryTests
{
    private sealed class TestEffect(string typeName = "Test", EffectImplementType type = EffectImplementType.IPicture) : INormalEffect, IDisposable
    {
        public string TypeName => typeName;
        public EffectImplementType ImplementType => type;
        public string FromPlugin => "test";
        public string Name { get; set; } = "Test";
        public string Id { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public bool IsReorderable => true;
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public string? BindedEffectProvidingSystemID { get; set; }
        public Dictionary<string, object> Parameters { get; private set; } = [];
        public Func<Dictionary<string, object>, IEffect>? Clone { get; init; }
        public bool Disposed { get; private set; }

        public IEffect WithParameters(Dictionary<string, object> parameters) => Clone?.Invoke(parameters) ?? new TestEffect(typeName, type) { Parameters = parameters };
        public IPicture Render(IPicture source, int targetWidth, int targetHeight) => source;
        public void Dispose() => Disposed = true;
    }

    private static readonly EffectImplementationKey key = new("Test", EffectImplementType.IPicture);

    [TestMethod]
    public void TransformsUseTheEffectRegistryAndRejectMissingHardware()
    {
        var registry = new EffectImplementationRegistry();
        registry.Register("cpu", new InternalPluginBase().EffectImplementationProvider);
        foreach (var name in new[] { "Fade", "Crossfade" })
        {
            var effect = registry.Create(name, EffectImplementType.IPicture, EffectImplementType.IPicture, []);
            Assert.IsInstanceOfType<ITransform>(effect);
            Assert.IsFalse(effect.IsReorderable);
            Assert.ThrowsExactly<NotSupportedException>(() => registry.Create(name, EffectImplementType.HwAcceleration, EffectImplementType.IPicture, []));
        }
    }

    [TestMethod]
    [DataRow("Accurate")]
    [DataRow("Approximate")]
    public void CpuOverlayKeepsEightBitRangeAndHdrBrightness(string accuracy)
    {
        var registry = new EffectImplementationRegistry();
        registry.Register("cpu", new InternalPluginBase().EffectImplementationProvider);
        var mixture = (IMixture)registry.Create("ClassicOverlayMixture", EffectImplementType.IPicture, EffectImplementType.HwAcceleration,
            new() { ["AccuracyMode"] = accuracy });
        var bottom = new Picture8bpp(1, 1) { r = [100], g = [0], b = [0] };
        var top = new Picture8bpp(1, 1) { r = [200], g = [0], b = [0], a = [0.5f], HasAlphaChannel = true };
        var output = (IPicture<byte>)mixture.Mix(bottom, top, IPicture.PicturePixelMode.BytePicture);
        Assert.AreEqual((byte)150, output.r[0]);
        Assert.AreEqual(1f, output.a![0]);
        var hdrBottom = HDRPicture16bpp.GenerateSolidColor(1, 1, 10000, 0, 0, 1, 0.8f, 1000);
        var hdrTop = HDRPicture16bpp.GenerateSolidColor(1, 1, 20000, 0, 0, 0.5f, 0.2f, 1000);
        var hdrOutput = (IHDRPicture<ushort>)mixture.Mix(hdrBottom, hdrTop, IPicture.PicturePixelMode.UShortPicture);
        Assert.AreEqual(0.5f, hdrOutput.Brightness[0], 0.00001f);
    }

    [TestMethod]
    public void CpuMixturesRespectNegativeOffsets()
    {
        var registry = new EffectImplementationRegistry();
        registry.Register("cpu", new InternalPluginBase().EffectImplementationProvider);
        var bottom = new Picture8bpp(2, 1) { r = [100, 80], g = [0, 0], b = [0, 0] };
        var top = new Picture8bpp(2, 1) { r = [255, 0], g = [0, 0], b = [0, 0], a = [1, 0], HasAlphaChannel = true };
        foreach (var type in new[] { "Add", "Subtract", "Multiply", "Screen", "OverlayBlend", "Darken", "Lighten", "Difference" })
        {
            var mixture = (IMixture)registry.Create(type + "Mixture", EffectImplementType.IPicture, EffectImplementType.HwAcceleration, []);
            var output = (IPicture<byte>)mixture.Mix(bottom, top, IPicture.PicturePixelMode.BytePicture, -1, 0, 2, 1);
            CollectionAssert.AreEqual(bottom.r, output.r);
        }
    }

    [TestMethod]
    public void SingleCandidateClonesParametersAndReleasesPrototype()
    {
        var registry = new EffectImplementationRegistry();
        var prototype = new TestEffect();
        registry.Register("one", new Dictionary<EffectImplementationKey, Func<IEffect>> { [key] = () => prototype });
        var parameters = new Dictionary<string, object> { ["value"] = 42 };
        var effect = registry.Create("Test", EffectImplementType.IPicture, EffectImplementType.HwAcceleration, parameters);
        Assert.AreSame(parameters, effect.Parameters);
        Assert.IsTrue(prototype.Disposed);
        Assert.IsFalse(((TestEffect)effect).Disposed);
    }

    [TestMethod]
    public void OnlyNotSpecifiedUsesDefault()
    {
        var registry = new EffectImplementationRegistry();
        registry.Register("one", new Dictionary<EffectImplementationKey, Func<IEffect>> { [key] = () => new TestEffect() });
        Assert.AreEqual(EffectImplementType.IPicture, registry.Create("Test", EffectImplementType.NotSpecified, EffectImplementType.IPicture, []).ImplementType);
        Assert.ThrowsExactly<NotSupportedException>(() => registry.Create("Test", EffectImplementType.HwAcceleration, EffectImplementType.IPicture, []));
    }

    [TestMethod]
    public void MultipleCandidatesRequireValidPreference()
    {
        var registry = new EffectImplementationRegistry();
        registry.Register("one", new Dictionary<EffectImplementationKey, Func<IEffect>> { [key] = () => new TestEffect() });
        registry.Register("two", new Dictionary<EffectImplementationKey, Func<IEffect>> { [key] = () => new TestEffect { Clone = p => new TestEffect { Name = "second" } } });
        Assert.ThrowsExactly<InvalidOperationException>(() => registry.Create("Test", EffectImplementType.IPicture, EffectImplementType.IPicture, []));
        registry.PreferredPlugins[key] = "missing";
        Assert.ThrowsExactly<InvalidOperationException>(() => registry.Create("Test", EffectImplementType.IPicture, EffectImplementType.IPicture, []));
        registry.PreferredPlugins[key] = "two";
        Assert.AreEqual("second", registry.Create("Test", EffectImplementType.IPicture, EffectImplementType.IPicture, []).Name);
    }

    [TestMethod]
    public void FactoryAndCloneMustMatchRequestedContract()
    {
        var registry = new EffectImplementationRegistry();
        var wrongPrototype = new TestEffect("wrong");
        registry.Register("one", new Dictionary<EffectImplementationKey, Func<IEffect>> { [key] = () => wrongPrototype });
        Assert.ThrowsExactly<InvalidOperationException>(() => registry.Create("Test", EffectImplementType.IPicture, EffectImplementType.IPicture, []));
        Assert.IsTrue(wrongPrototype.Disposed);
        var wrongClone = new TestEffect(type: EffectImplementType.HwAcceleration);
        var prototype = new TestEffect { Clone = _ => wrongClone };
        registry.Register("one", new Dictionary<EffectImplementationKey, Func<IEffect>> { [key] = () => prototype });
        Assert.ThrowsExactly<InvalidOperationException>(() => registry.Create("Test", EffectImplementType.IPicture, EffectImplementType.IPicture, []));
        Assert.IsTrue(wrongClone.Disposed);
        Assert.IsTrue(prototype.Disposed);
    }

    [TestMethod]
    public void UnregisterInvalidatesDirectoryAndReloadUsesNewFactory()
    {
        var registry = new EffectImplementationRegistry();
        registry.Register("one", new Dictionary<EffectImplementationKey, Func<IEffect>> { [key] = () => new TestEffect() });
        var revision = registry.Revision;
        registry.Unregister("one");
        Assert.IsTrue(registry.Revision > revision);
        Assert.ThrowsExactly<NotSupportedException>(() => registry.Create("Test", EffectImplementType.NotSpecified, EffectImplementType.IPicture, []));
        registry.Register("one", new Dictionary<EffectImplementationKey, Func<IEffect>> { [key] = () => new TestEffect { Clone = _ => new TestEffect { Name = "reloaded" } } });
        Assert.AreEqual("reloaded", registry.Create("Test", EffectImplementType.IPicture, EffectImplementType.IPicture, []).Name);
    }

    [TestMethod]
    public void CpuDirectoryIncludesPlacementKeyingAndLegacyMixtureAlias()
    {
        var registry = new EffectImplementationRegistry();
        registry.Register("cpu", new InternalPluginBase().EffectImplementationProvider);
        var place = (INormalEffect)registry.Create("Place", EffectImplementType.IPicture, EffectImplementType.HwAcceleration, new() { ["StartX"] = 1, ["StartY"] = 0 });
        var source = HDRPicture16bpp.GenerateSolidColor(1, 1, 12000, 0, 0, 0.5f, 0.8f, 1000);
        var output = (IHDRPicture<ushort>)place.Render(source, 2, 1);
        Assert.AreEqual(0f, output.a![0]);
        Assert.AreEqual(0.5f, output.a[1]);
        Assert.AreEqual((ushort)12000, output.r[1]);
        Assert.AreEqual(0.8f, output.Brightness[1]);
        Assert.AreEqual("DifferenceMixture", registry.Create("BlendModeMixture", EffectImplementType.IPicture, EffectImplementType.HwAcceleration, new() { ["MixtureType"] = "Difference" }).TypeName);
        Assert.IsInstanceOfType<RemoveColorEffect_IPicture>(registry.Create("RemoveColor", EffectImplementType.IPicture, EffectImplementType.HwAcceleration,
            new() { ["R"] = (ushort)0, ["G"] = (ushort)0, ["B"] = (ushort)0, ["A"] = (ushort)65535, ["Tolerance"] = (ushort)0 }));
    }
}
