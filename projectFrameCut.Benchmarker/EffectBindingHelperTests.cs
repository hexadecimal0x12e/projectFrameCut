using Microsoft.VisualStudio.TestTools.UnitTesting;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Benchmarker;

[TestClass]
public sealed class EffectBindingHelperTests
{
    [TestMethod]
    public void ActivePicturePath_AllowsFanOutButUsesOnlyFinalBranch()
    {
        var root = NewBlur();
        var activeLeaf = NewBlur();
        var inactiveLeaf = NewBlur();
        root.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        activeLeaf.SetMainInputSource(root.Id);
        inactiveLeaf.SetMainInputSource(root.Id);
        activeLeaf.SetFinalOutputSource(true);
        var providers = ToMap(root, activeLeaf, inactiveLeaf);

        var active = EffectBindingHelper.GetActivePictureProviderIds(providers);

        CollectionAssert.AreEquivalent(new[] { root.Id, activeLeaf.Id }, active.ToArray());
        Assert.IsFalse(active.Contains(inactiveLeaf.Id));
    }

    [TestMethod]
    public void MultipleFinalOutputs_AreReportedAndRejectedForRendering()
    {
        var first = NewBlur();
        var second = NewBlur();
        first.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        second.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        first.SetFinalOutputSource(true);
        second.SetFinalOutputSource(true);
        var providers = ToMap(first, second);

        Assert.IsTrue(EffectBindingHelper.ValidateBindings(providers).Any(d => d.Code == "MultipleFinalOutputs"));
        Assert.Throws<InvalidOperationException>(() => EffectBindingHelper.GetActivePictureProviderIds(providers));
    }

    [TestMethod]
    public void MaterializeFields_UsesStoredBindingAndRestoresStaticFallbackOnUnbind()
    {
        var consumer = new IntOverlayEffectProvider();
        var source = new IntConstantValueProviderProvider();
        var fields = consumer.Fields;
        fields["Value"] = new StaticEffectArgumentField(42, EffectArgumentFieldType.Integer);
        consumer.Fields = fields;
        consumer.SetFieldBinding("Value", source.Id.ToString());
        var storedBindings = consumer.AnchorsBindingState.ToDictionary(binding => binding.Key, binding => binding.Value);

        EffectBindingHelper.MaterializeFields([consumer]);
        EffectBindingHelper.MaterializeFields([consumer]);

        var dynamicField = consumer.Fields["Value"] as DynamicEffectParamField;
        Assert.IsNotNull(dynamicField);
        Assert.AreEqual(source.Id.ToString(), dynamicField.BoundProviderId);
        Assert.AreEqual(42, dynamicField.StaticFallbackValue);
        CollectionAssert.AreEquivalent(storedBindings.ToArray(), consumer.AnchorsBindingState.ToArray());

        consumer.ClearFieldBinding("Value");
        EffectBindingHelper.MaterializeFields([consumer]);

        var staticField = consumer.Fields["Value"] as StaticEffectArgumentField;
        Assert.IsNotNull(staticField);
        Assert.AreEqual(42, staticField.Value);
    }

    [TestMethod]
    public void MaterializeFields_SupportsBuiltinStringSources()
    {
        var consumer = new IntOverlayEffectProvider();
        consumer.SetFieldBinding("Value", ValueProviderFrameContext.BuiltInFrameProviderId);

        EffectBindingHelper.MaterializeFields([consumer]);

        Assert.AreEqual(
            ValueProviderFrameContext.BuiltInFrameProviderId,
            ((DynamicEffectParamField)consumer.Fields["Value"]).BoundProviderId);
    }

    [TestMethod]
    public void MigrateToEffectProviders_RestoresProviderOwnedStaticFields()
    {
        var providerId = Guid.NewGuid();
        var dto = new EffectProviderJSONStructure
        {
            Id = providerId,
            TypeName = "IntOverlay",
            Name = "Restored overlay",
            AnchorsBindingState = new Dictionary<string, string>
            {
                [EffectProviderAnchorExtensions.InputKey] = IEffectProvider.InputAnchorGUID.ToString(),
                [EffectProviderAnchorExtensions.OutputKey] = IEffectProvider.OutputAnchorGUID.ToString(),
            },
            StaticFields = new Dictionary<string, object>
            {
                ["Value"] = 73,
            },
        };

        var restored = EffectBindingHelper.MigrateToEffectProviders([dto], null);

        Assert.IsTrue(restored.TryGetValue(providerId, out var provider));
        Assert.IsNotNull(provider);
        var field = provider.Fields["Value"] as StaticEffectArgumentField;
        Assert.IsNotNull(field);
        Assert.AreEqual(73, field.Value);
    }

    [TestMethod]
    public void NormalizeStoredBindings_MigratesLegacyBuiltinIdentifier()
    {
        var consumer = new IntOverlayEffectProvider();
        consumer.AnchorsBindingState["Value"] = "__Builtin_frame";

        EffectBindingHelper.NormalizeStoredBindings(ToMap(consumer));

        Assert.AreEqual(ValueProviderFrameContext.BuiltInFrameProviderId, consumer.AnchorsBindingState["Value"]);
    }

    [TestMethod]
    public void NormalizeStoredBindings_ConvertsUniqueLegacyOutputAndRemovesForeignFieldKeys()
    {
        var source = NewBlur();
        var target = NewBlur();
        source.AnchorsBindingState = new Dictionary<string, string>
        {
            [EffectProviderAnchorExtensions.InputKey] = IEffectProvider.InputAnchorGUID.ToString(),
            [EffectProviderAnchorExtensions.OutputKey] = target.Id.ToString(),
            ["ForeignField"] = Guid.NewGuid().ToString(),
        };
        target.AnchorsBindingState = new Dictionary<string, string>
        {
            [EffectProviderAnchorExtensions.InputKey] = IEffectProvider.NoConnectionGUID.ToString(),
            [EffectProviderAnchorExtensions.OutputKey] = IEffectProvider.OutputAnchorGUID.ToString(),
        };
        var providers = ToMap(source, target);

        var diagnostics = EffectBindingHelper.NormalizeStoredBindings(providers);

        Assert.AreEqual(source.Id.ToString(), target.GetMainInputSource());
        Assert.IsFalse(source.IsFinalOutputSource());
        Assert.IsTrue(target.IsFinalOutputSource());
        Assert.IsFalse(source.AnchorsBindingState.ContainsKey("ForeignField"));
        Assert.IsTrue(diagnostics.Any(d => d.Code == "UnknownBindingKey"));
    }

    [TestMethod]
    public void NormalizeStoredBindings_RemovesCopiedFieldBindingFromNonTargetProvider()
    {
        var source = new IntConstantValueProviderProvider();
        var target = new IntOverlayEffectProvider();
        var copiedTo = new IntOverlayEffectProvider();
        var sourceId = source.Id.ToString();
        target.AnchorsBindingState["Value"] = sourceId;
        copiedTo.AnchorsBindingState["Value"] = sourceId;

        var legacyFields = target.Fields;
        legacyFields["Value"] = new DynamicEffectParamField
        {
            Id = "Value",
            FieldType = EffectArgumentFieldType.Integer,
            BoundProviderId = sourceId,
            StaticFallbackValue = 0,
        };
        target.Fields = legacyFields;

        EffectBindingHelper.NormalizeStoredBindings(ToMap(source, target, copiedTo));

        Assert.IsTrue(target.TryGetFieldBinding("Value", out var retained) && retained == sourceId);
        Assert.IsFalse(copiedTo.TryGetFieldBinding("Value", out _));
    }

    private static BlurEffectProvider NewBlur() => new() { Id = Guid.NewGuid() };

    private static Dictionary<Guid, IEffectProvider> ToMap(params IEffectProvider[] providers) =>
        providers.ToDictionary(provider => provider.Id);
}
