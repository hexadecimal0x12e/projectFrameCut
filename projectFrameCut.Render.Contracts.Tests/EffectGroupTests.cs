using System.Text.Json;
using System.Text;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using static projectFrameCut.Render.Contracts.Tests.DynamicEffectGraphTests;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class EffectGroupTests
{
    private static Provider PictureProvider() => new(EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, c => c.Input);

    [TestMethod]
    public void ContentHashUsesOriginalBytesIncludingWhitespace()
    {
        Assert.AreEqual("44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a", EffectGroupHelper.JsonHash(Encoding.UTF8.GetBytes("{}")));
        Assert.AreNotEqual(EffectGroupHelper.JsonHash(Encoding.UTF8.GetBytes("{}")), EffectGroupHelper.JsonHash(Encoding.UTF8.GetBytes("{ }")));
    }

    [TestMethod]
    public void EveryNestedBoundaryIncludesExistingExternalBindings()
    {
        var a = PictureProvider();
        var b = PictureProvider();
        a.SetMainInputSource(IEffectProvider.InputAnchorGUID.ToString());
        b.SetMainInputSource(a.Id.ToString());
        var child = new EffectGroup { ProviderIds = [a.Id] };
        var middle = new EffectGroup { ChildGroupIds = [child.Id] };
        var root = new EffectGroup { ChildGroupIds = [middle.Id] };
        List<EffectGroup> groups = [child, middle, root];
        Dictionary<Guid, IEffectProvider> providers = new() { [a.Id] = a, [b.Id] = b };
        Assert.Throws<InvalidOperationException>(() => EffectGroupHelper.Validate(groups, providers));
        EffectGroupHelper.EnsureBoundaryPorts(groups, providers);
        EffectGroupHelper.Validate(groups, providers);
        foreach (var group in groups)
        {
            Assert.IsTrue(group.Ports.Any(p => p.IsInput && p.ProviderId == a.Id && p.FieldId == "__Input__"));
            Assert.IsTrue(group.Ports.Any(p => !p.IsInput && p.ProviderId == a.Id));
        }
        child.Ports.RemoveAll(p => !p.IsInput);
        Assert.Throws<InvalidOperationException>(() => EffectGroupHelper.Validate(groups, providers));
    }

    [TestMethod]
    public void ExportAndInstantiateKeepInternalBindingsAndRemapAllIds()
    {
        var a = PictureProvider();
        var b = PictureProvider();
        a.SetMainInputSource(IEffectProvider.InputAnchorGUID.ToString());
        b.SetMainInputSource(a.Id.ToString());
        b.SetFinalOutputSource(true);
        var child = new EffectGroup { ProviderIds = [a.Id, b.Id], Name = "child" };
        var root = new EffectGroup { ChildGroupIds = [child.Id], Name = "root" };
        List<EffectGroup> groups = [child, root];
        Dictionary<Guid, IEffectProvider> providers = new() { [a.Id] = a, [b.Id] = b };
        EffectGroupHelper.EnsureBoundaryPorts(groups, providers);
        var template = EffectGroupHelper.Export(root, groups, providers);
        var restored = JsonSerializer.Deserialize<EffectGroupTemplate>(JsonSerializer.Serialize(template))!;
        EffectGroupHelper.ValidateTemplate(restored);
        Assert.AreEqual(IEffectProvider.NoConnectionGUID.ToString(), restored.Providers.Single(p => p.Id == a.Id).AnchorsBindingState["__Input__"]);
        Assert.IsFalse(restored.Providers.Any(p => p.AnchorsBindingState.Keys.Any(EffectProviderAnchorExtensions.IsOutputBindingKey)));
        var copy = EffectGroupHelper.Instantiate(restored);
        var copyA = copy.Providers[Array.FindIndex(restored.Providers, p => p.Id == a.Id)];
        var copyB = copy.Providers[Array.FindIndex(restored.Providers, p => p.Id == b.Id)];
        Assert.AreNotEqual(a.Id, copyA.Id);
        Assert.AreNotEqual(root.Id, copy.RootGroupId);
        Assert.AreEqual(copyA.Id.ToString(), copyB.AnchorsBindingState["__Input__"]);
        Assert.IsTrue(copy.Groups.SelectMany(g => g.Ports).All(p => copy.Providers.Any(n => n.Id == p.ProviderId)));
        Assert.AreEqual(a.Id.ToString(), b.GetMainInputSource());
        copy.Groups.Single(g => g.Id == copy.RootGroupId).Name = "changed";
        Assert.AreEqual("root", restored.Groups.Single(g => g.Id == restored.RootGroupId).Name);
    }

    [TestMethod]
    public void ClipPropertiesAndCrossClipEndpointsCannotBeGrouped()
    {
        var arguments = new ClipArgumentProvider();
        var input = new ClipValueImportProvider { SourceClipId = Guid.NewGuid(), SourceEndpointId = Guid.NewGuid() };
        var consumer = PictureProvider();
        consumer.SetMainInputSource(input.Id.ToString());
        Dictionary<Guid, IEffectProvider> providers = new() { [arguments.Id] = arguments, [input.Id] = input, [consumer.Id] = consumer };
        Assert.IsFalse(EffectGroupHelper.CanGroup(arguments, providers));
        Assert.IsFalse(EffectGroupHelper.CanGroup(input, providers));
        Assert.IsFalse(EffectGroupHelper.CanGroup(consumer, providers));
        var group = new EffectGroup { ProviderIds = [consumer.Id] };
        Assert.Throws<InvalidOperationException>(() => EffectGroupHelper.Validate([group], providers));
        var producer = PictureProvider();
        var output = new ClipValueExportProvider();
        output.SetMainInputSource(producer.Id.ToString());
        providers.Add(producer.Id, producer);
        providers.Add(output.Id, output);
        Assert.IsFalse(EffectGroupHelper.CanGroup(output, providers));
        Assert.IsFalse(EffectGroupHelper.CanGroup(producer, providers));
    }

    [TestMethod]
    public void CyclesAndMultipleOwnershipAreRejected()
    {
        var p = PictureProvider();
        Dictionary<Guid, IEffectProvider> providers = new() { [p.Id] = p };
        var a = new EffectGroup { ProviderIds = [p.Id] };
        var b = new EffectGroup { ChildGroupIds = [a.Id] };
        a.ChildGroupIds.Add(b.Id);
        Assert.Throws<InvalidOperationException>(() => EffectGroupHelper.Validate([a, b], providers));
        a.ChildGroupIds.Clear();
        b.ChildGroupIds.Clear();
        b.ProviderIds.Add(p.Id);
        Assert.Throws<InvalidOperationException>(() => EffectGroupHelper.Validate([a, b], providers));
    }

    [TestMethod]
    public void InvalidTemplatesAreRejectedBeforeInstantiation()
    {
        var p = PictureProvider();
        p.DisconnectMainInput();
        var group = new EffectGroup { ProviderIds = [p.Id] };
        var t = new EffectGroupTemplate { RootGroupId = group.Id, Groups = [group], Providers = [EffectBindingHelper.SerializeProvider(p)] };
        EffectGroupHelper.ValidateTemplate(t);
        t.Version = 2;
        Assert.Throws<InvalidOperationException>(() => EffectGroupHelper.Instantiate(t));
        t.Version = 1;
        t.Providers[0].AnchorsBindingState["__Input__"] = Guid.NewGuid().ToString();
        Assert.Throws<InvalidOperationException>(() => EffectGroupHelper.Instantiate(t));
        Assert.AreEqual(p.Id, t.Providers[0].Id);
    }
}
