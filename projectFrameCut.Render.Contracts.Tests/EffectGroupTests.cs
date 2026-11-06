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
        Assert.AreEqual("44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a", EffectGroupHelper.ContentHash(Encoding.UTF8.GetBytes("{}")));
        Assert.AreNotEqual(EffectGroupHelper.ContentHash(Encoding.UTF8.GetBytes("{}")), EffectGroupHelper.ContentHash(Encoding.UTF8.GetBytes("{ }")));
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

    private static EffectGroupTemplate MermaidTemplate()
    {
        var trig = new AdvancedMathEffectProvider(1) { Name = "三角函数 [\"角度\"] #34; & <值>\nSinCos", Stage = EffectStage.Render };
        trig.SetField("Operation", "SinCos");
        trig.SetFieldBinding("A", ValueProviderFrameContext.BuiltInProgressProviderId);
        var max = new AdvancedMathEffectProvider(2) { Name = "Max", Stage = EffectStage.Render, Enabled = false };
        max.SetField("Operation", "Max");
        max.SetFieldBinding("A", EffectProviderOutputExtensions.CreateOutputSourceId(trig.Id, "Value"));
        max.SetFieldBinding("B", EffectProviderOutputExtensions.CreateOutputSourceId(trig.Id, "Cos"));
        var child = new EffectGroup { Name = "child", ProviderIds = [trig.Id], X = 12.5, Collapsed = false };
        var root = new EffectGroup { Name = "root", ProviderIds = [max.Id], ChildGroupIds = [child.Id],
            Ports = [new() { ProviderId = max.Id, FieldId = "Value", Name = "Result" }] };
        List<EffectGroup> groups = [root, child];
        Dictionary<Guid, IEffectProvider> providers = new() { [trig.Id] = trig, [max.Id] = max };
        EffectGroupHelper.EnsureBoundaryPorts(groups, providers);
        return EffectGroupHelper.Export(root, groups, providers);
    }

    [TestMethod]
    public void MermaidRoundTripPreservesPortsParametersGroupsAndEscapedNames()
    {
        var original = MermaidTemplate();
        string text = EffectGroupMermaid.Serialize(original);
        var parsed = EffectGroupMermaid.Parse(text);
        Assert.AreEqual(text, EffectGroupMermaid.Serialize(parsed));
        StringAssert.Contains(text, "group1-output-1([\"Result\"])");
        StringAssert.Contains(text, "group1-input-2 -.-> group2-input-1");
        StringAssert.Contains(text, "source1-output-__Output__ --> group1-input-2");
        StringAssert.Contains(text, "group2-output-3 --> node1-input-B");
        Assert.AreEqual(original.Providers.Single(p => p.TypeName == "AdvancedMath1").Name,
            parsed.Providers.Single(p => p.TypeName == "AdvancedMath1").Name);
        var trig = parsed.Providers.Single(p => p.TypeName == "AdvancedMath1");
        var max = parsed.Providers.Single(p => p.TypeName == "AdvancedMath2");
        Assert.IsFalse(max.Enabled);
        Assert.AreEqual("SinCos", trig.StaticFields!["Operation"].ToString());
        Assert.AreEqual(ValueProviderFrameContext.BuiltInProgressProviderId, trig.AnchorsBindingState["A"]);
        Assert.AreEqual(EffectProviderOutputExtensions.CreateOutputSourceId(trig.Id, "Cos"), max.AnchorsBindingState["B"]);
        Assert.AreEqual(12.5, parsed.Groups.Single(g => g.Name == "child").X);
        Assert.IsFalse(parsed.Groups.Single(g => g.Name == "child").Collapsed);
        CollectionAssert.AreEqual(original.Groups[0].Ports.Select(p => p.Name).ToArray(), parsed.Groups[0].Ports.Select(p => p.Name).ToArray());
        var copy = EffectGroupHelper.Instantiate(parsed);
        Assert.IsTrue(copy.DeclaredPorts.All(p => copy.Providers.Any(n => n.Id == p.ProviderId)));
    }

    private static EffectGroupTemplate VariableTemplate()
    {
        var read = new EffectVariableReadProvider { Name = "read", VariableId = Guid.NewGuid() };
        var write = new EffectVariableWriteProvider { Name = "write", VariableId = read.VariableId };
        var other = new EffectVariableReadProvider { Name = "other", VariableId = Guid.NewGuid() };
        var child = new EffectGroup { Name = "child", ProviderIds = [write.Id] };
        var root = new EffectGroup { Name = "root", ProviderIds = [read.Id, other.Id], ChildGroupIds = [child.Id] };
        List<EffectGroup> groups = [root, child];
        Dictionary<Guid, IEffectProvider> providers = new() { [read.Id] = read, [write.Id] = write, [other.Id] = other };
        return EffectGroupHelper.Export(root, groups, providers);
    }

    [TestMethod]
    public void MermaidVariableRoundTripPreservesIdentityAcrossNestedGroups()
    {
        var original = VariableTemplate();
        var originalIds = original.Providers.Select(p => p.MetaData!["VariableId"].ToString()).ToArray();
        string text = EffectGroupMermaid.Serialize(original);
        Assert.AreEqual(2, text.Split("%% @variable variable1").Length - 1);
        Assert.AreEqual(1, text.Split("%% @variable variable2").Length - 1);
        Assert.IsFalse(text.Contains("\"VariableId\"", StringComparison.Ordinal));
        CollectionAssert.AreEqual(originalIds, original.Providers.Select(p => p.MetaData!["VariableId"].ToString()).ToArray());
        var parsed = EffectGroupMermaid.Parse(text);
        var read = parsed.Providers.Single(p => p.Name == "read");
        var write = parsed.Providers.Single(p => p.Name == "write");
        var other = parsed.Providers.Single(p => p.Name == "other");
        Assert.AreEqual(read.MetaData!["VariableId"].ToString(), write.MetaData!["VariableId"].ToString());
        Assert.AreNotEqual(read.MetaData["VariableId"].ToString(), other.MetaData!["VariableId"].ToString());
        Assert.AreEqual(text, EffectGroupMermaid.Serialize(parsed));
    }

    [TestMethod]
    public void MermaidVariableAliasesAcceptArbitraryTextAndInstantiateIndependently()
    {
        string text = EffectGroupMermaid.Serialize(VariableTemplate())
            .Replace("%% @variable variable1", "%% @variable counter / 计数器")
            .Replace("%% @variable variable2", "%% @variable Counter / 计数器");
        var parsed = EffectGroupMermaid.Parse(text);
        var first = EffectGroupHelper.Instantiate(parsed);
        var second = EffectGroupHelper.Instantiate(parsed);
        var firstIds = first.Providers.Select(p => p.MetaData!["VariableId"].ToString()!).ToHashSet();
        var secondIds = second.Providers.Select(p => p.MetaData!["VariableId"].ToString()!).ToHashSet();
        Assert.AreEqual(2, firstIds.Count);
        Assert.AreEqual(2, secondIds.Count);
        Assert.IsFalse(firstIds.Overlaps(secondIds));
        Assert.IsTrue(firstIds.All(id => Guid.TryParse(id, out _)));
    }

    [TestMethod]
    public void MermaidRejectsMissingRepeatedAndMisplacedVariableAnnotations()
    {
        string text = EffectGroupMermaid.Serialize(VariableTemplate());
        foreach (var invalid in new[]
        {
            text.Replace("%% @variable variable1", "%% removed"),
            text.Replace("%% @variable variable1", "%% @variable variable1\n%% @variable variable1"),
            text.Replace("%% @variable variable1", "%% @variable "),
            text.Replace("graph TD", "%% @variable counter\ngraph TD"),
            text.Replace("subgraph group1[\"root\"]", "subgraph group1[\"root\"]\n%% @variable counter"),
        })
            foreach (bool validate in new[] { true, false })
            {
                var error = Assert.Throws<EffectGroupMermaid.ParseException>(() => EffectGroupMermaid.Parse(invalid, validate));
                Assert.IsTrue(error.LineNumber > 0);
            }
    }

    [TestMethod]
    public void MermaidRejectsDuplicateInputsUnknownPortsAndReverseConnections()
    {
        string text = EffectGroupMermaid.Serialize(MermaidTemplate());
        foreach (var connection in new[]
        {
            "node2-output-Value --> node1-input-A",
            "missing-port --> node1-input-A",
            "node1-input-A --> node2-output-Value",
        })
        {
            var error = Assert.Throws<InvalidOperationException>(() => EffectGroupMermaid.Parse(text + connection));
            StringAssert.Contains(error.Message, "Mermaid line");
        }
    }

    [TestMethod]
    public void MermaidRejectsOldJsonUnknownMetadataAndUnsupportedSyntax()
    {
        string text = EffectGroupMermaid.Serialize(MermaidTemplate());
        Assert.Throws<InvalidOperationException>(() => EffectGroupMermaid.Parse(JsonSerializer.Serialize(MermaidTemplate())));
        Assert.Throws<InvalidOperationException>(() => EffectGroupMermaid.Parse(text.Replace("%% Version 1", "%% Version 2")));
        Assert.Throws<InvalidOperationException>(() => EffectGroupMermaid.Parse(text.Replace("%% @provider", "%% @unknown")));
        Assert.Throws<InvalidOperationException>(() => EffectGroupMermaid.Parse(text + "style node1 fill:red"));
        Assert.Throws<InvalidOperationException>(() => EffectGroupMermaid.Parse(text.Replace("\"Enabled\":", "\"AnchorsBindingState\":{},\"Enabled\":")));
    }

    [TestMethod]
    public void MermaidPortSchemaUsesProviderStaticConfiguration()
    {
        var template = MermaidTemplate();
        var trig = template.Providers.Single(p => p.TypeName == "AdvancedMath1");
        var provider = new AdvancedMathEffectProvider(1) { Id = trig.Id, Stage = EffectStage.Render };
        provider.SetField("Operation", "SinCos");
        Dictionary<Guid, IEffectProvider> providers = new() { [provider.Id] = provider };
        EffectGroupMermaid.ValidatePorts(template, providers);
        provider.SetField("Operation", "Sin");
        Assert.Throws<InvalidOperationException>(() => EffectGroupMermaid.ValidatePorts(template, providers));
    }

    [TestMethod]
    public void MermaidRejectsMissingInvalidOrRepeatedInterfaceMappings()
    {
        string text = EffectGroupMermaid.Serialize(MermaidTemplate());
        Assert.Throws<InvalidOperationException>(() => EffectGroupMermaid.Parse(text.Replace("group1-input-2 -.-> group2-input-1", "%% removed")));
        foreach (var connection in new[]
        {
            "group1-input-2 -.-> group2-input-1",
            "group1-input-2 -.-> node2-input-A",
            "node2-input-A -.-> group2-input-1",
            "source1-output-__Output__ --> node2-input-A",
        })
        {
            var error = Assert.Throws<InvalidOperationException>(() => EffectGroupMermaid.Parse(text + connection));
            StringAssert.Contains(error.Message, "Mermaid line");
        }
    }

    [TestMethod]
    public void MermaidKeepsUnconnectedPublicInputsAndOutputsVisible()
    {
        var template = MermaidTemplate();
        template.Providers.Single(p => p.TypeName == "AdvancedMath1").AnchorsBindingState.Remove("A");
        string text = EffectGroupMermaid.Serialize(template);
        StringAssert.Contains(text, "group1-input-2([\"");
        StringAssert.Contains(text, "group1-output-1([\"Result\"])");
        Assert.IsFalse(text.Contains(" --> group1-input-2", StringComparison.Ordinal));
        var parsed = EffectGroupMermaid.Parse(text);
        Assert.AreEqual(2, parsed.Groups.Single(g => g.Id == parsed.RootGroupId).Ports.Count);
        Assert.IsFalse(parsed.Providers.Single(p => p.TypeName == "AdvancedMath1").AnchorsBindingState.ContainsKey("A"));
    }

    private const string CompactMermaid = """
        %% projectFrameCut Effect Binding Graph
        %% Version 1
        graph TD
            source-node --> NodeDefsource-node["test.plugin@Source"]
            effect-node --> NodeDefeffect-node["test.plugin@Effect"]
            subgraph group
                group-output-result(["Result"])
                subgraph source-node
                    source-node-output-Value-with-dash
                end
                %% @provider {"Id":"effect-node","StaticFields":{"gain":2}}
                subgraph effect-node["Effect"]
                    effect-node-input-A
                    effect-node-param-gain
                    effect-node-Output-__Output__
                end
            end
            source-node-output-Value-with-dash --> effect-node-input-A
            effect-node-Output-__Output__ -.-> group-output-result

        """;

    [TestMethod]
    public void MermaidInfersTypesPortsAndRootFromCompactDeclarations()
    {
        var template = EffectGroupMermaid.Parse(CompactMermaid);
        var source = template.Providers.Single(p => p.TypeName == "Source");
        var effect = template.Providers.Single(p => p.TypeName == "Effect");
        Assert.AreEqual("test.plugin", effect.FromPlugin);
        Assert.AreEqual(EffectStage.Render, effect.Stage);
        Assert.IsTrue(effect.Enabled);
        Assert.AreEqual("2", effect.StaticFields!["gain"].ToString());
        Assert.AreEqual(EffectProviderOutputExtensions.CreateOutputSourceId(source.Id, "Value-with-dash"), effect.AnchorsBindingState["A"]);
        Assert.AreEqual("Value-with-dash", template.DeclaredPorts.Single(p => p.ProviderId == source.Id).Name);
        Assert.AreEqual(4, template.DeclaredPorts.Count);
        Assert.AreEqual("Result", template.Groups.Single(g => g.Id == template.RootGroupId).Ports.Single().Name);
        string text = EffectGroupMermaid.Serialize(template);
        StringAssert.Contains(text, "node1 --> NodeDefnode1[\"test.plugin@Source\"]");
        Assert.IsFalse(text.Contains("%% @port", StringComparison.Ordinal));
        Assert.AreEqual(text, EffectGroupMermaid.Serialize(EffectGroupMermaid.Parse(text)));
    }

    [TestMethod]
    public void MermaidRejectsConflictingTypesAndNodeDefIdentifierCollisions()
    {
        Assert.Throws<InvalidOperationException>(() => EffectGroupMermaid.Parse(CompactMermaid.Replace("NodeDefeffect-node", "NodeDefsource-node")));
        Assert.Throws<InvalidOperationException>(() => EffectGroupMermaid.Parse(CompactMermaid.Replace("NodeDefeffect-node", "NodeDefgroup").Replace("subgraph group", "subgraph NodeDefgroup")));
        Assert.Throws<InvalidOperationException>(() => EffectGroupMermaid.Parse(CompactMermaid.Replace("test.plugin@Effect", "Effect")));
        Assert.Throws<InvalidOperationException>(() => EffectGroupMermaid.Parse(CompactMermaid.Replace("\"Id\":\"effect-node\",", "\"Id\":\"effect-node\",\"TypeName\":\"OtherEffect\",")));
        Assert.Throws<InvalidOperationException>(() => EffectGroupMermaid.Parse(CompactMermaid.Replace("effect-node-param-gain", "effect-node-side-gain")));
    }

    [TestMethod]
    public void MermaidAcceptsMatchingJsonTypesAndExplicitPortOverrides()
    {
        string text = CompactMermaid.Replace("\"Id\":\"effect-node\",", "\"Id\":\"effect-node\",\"TypeName\":\"Effect\",\"FromPlugin\":\"test.plugin\",\"Stage\":\"Composition\",")
            .Replace("effect-node-param-gain", "%% @port {\"Id\":\"custom-port\",\"Node\":\"effect-node\",\"Field\":\"gain\",\"Input\":true}\n                    custom-port");
        var template = EffectGroupMermaid.Parse(text);
        Assert.AreEqual(EffectStage.Composition, template.Providers.Single(p => p.TypeName == "Effect").Stage);
        Assert.IsTrue(template.DeclaredPorts.Any(p => p.FieldId == "gain" && p.IsInput));
    }

    [TestMethod]
    public void MermaidShowsStaticValuesAlongsideDynamicBindings()
    {
        string text = EffectGroupMermaid.Serialize(MermaidTemplate());
        StringAssert.Contains(text, "node2-value-Operation[\"#34;SinCos#34;\"]");
        StringAssert.Contains(text, "node2-value-Operation ==> node2-input-Operation");
        StringAssert.Contains(text, "node2-value-A ==> node2-input-A");
        StringAssert.Contains(text, "source1-output-__Output__ --> group1-input-2");
        Assert.IsFalse(text.Contains("\"StaticFields\":", StringComparison.Ordinal));
        var template = EffectGroupMermaid.Parse(text);
        Assert.AreEqual("SinCos", template.Providers.Single(p => p.TypeName == "AdvancedMath1").StaticFields!["Operation"].ToString());
        Assert.AreEqual(text, EffectGroupMermaid.Serialize(template));
    }

    [TestMethod]
    public void MermaidAcceptsGraphValuesWithBothProviderTypeSyntaxes()
    {
        string text = CompactMermaid.Replace("%% @provider {\"Id\":\"effect-node\",\"StaticFields\":{\"gain\":2}}", "")
            .Replace("effect-node-param-gain", "effect-node-param-gain\n                    effect-node-value-gain[\"67\"]\n                    effect-node-value-Operation[\"#34;#60;#34;\"]")
            + "effect-node-value-gain ==> effect-node-param-gain\n";
        var template = EffectGroupMermaid.Parse(text);
        var effect = template.Providers.Single(p => p.TypeName == "Effect");
        Assert.AreEqual("67", effect.StaticFields!["gain"].ToString());
        Assert.AreEqual("<", effect.StaticFields["Operation"].ToString());
        text = text.Replace("    effect-node --> NodeDefeffect-node[\"test.plugin@Effect\"]",
            "    %% @provider {\"Id\":\"effect-node\",\"TypeName\":\"Effect\",\"FromPlugin\":\"test.plugin\"}");
        Assert.AreEqual("<", EffectGroupMermaid.Parse(text).Providers.Single(p => p.TypeName == "Effect").StaticFields!["Operation"].ToString());
    }

    [TestMethod]
    public void MermaidRejectsConflictingValuesAndIncorrectValueArrows()
    {
        string text = CompactMermaid.Replace("effect-node-param-gain", "effect-node-param-gain\n                    effect-node-value-gain[\"2\"]");
        EffectGroupMermaid.Parse(text);
        Assert.Throws<InvalidOperationException>(() => EffectGroupMermaid.Parse(text.Replace("gain[\"2\"]", "gain[\"3\"]")));
        Assert.Throws<InvalidOperationException>(() => EffectGroupMermaid.Parse(text.Replace("gain[\"2\"]", "gain[\"invalid JSON\"]")));
        Assert.Throws<InvalidOperationException>(() => EffectGroupMermaid.Parse(text + "effect-node-value-gain ==> effect-node-input-A"));
        Assert.Throws<InvalidOperationException>(() => EffectGroupMermaid.Parse(text + "effect-node-value-gain --> effect-node-param-gain"));
    }

    [TestMethod]
    public void MermaidDiagnosticDiagramIncludesProjectNodesSourcesAndFinalOutput()
    {
        var effect = PictureProvider();
        effect.SetMainInputSource(IEffectProvider.InputAnchorGUID.ToString());
        effect.SetFinalOutputSource(true);
        var arguments = new ClipArgumentProvider { Stage = EffectStage.Render };
        Dictionary<Guid, IEffectProvider> providers = new() { [effect.Id] = effect, [arguments.Id] = arguments };
        string before = JsonSerializer.Serialize(providers.Values.Select(EffectBindingHelper.SerializeProvider));
        string text = EffectGroupMermaid.SerializeDiagram(providers, [], "Render",
            new Dictionary<string, string> { [IEffectProvider.InputAnchorGUID.ToString()] = "Source picture" }, "Final picture");
        StringAssert.Contains(text, "Source picture");
        StringAssert.Contains(text, "Final picture");
        StringAssert.Contains(text, arguments.TypeName);
        StringAssert.Contains(text, "source1-output-__Output__ --> node1-input-__Input__");
        StringAssert.Contains(text, "node1-output-__Output__ --> pipeline-output");
        Assert.AreEqual(before, JsonSerializer.Serialize(providers.Values.Select(EffectBindingHelper.SerializeProvider)));
        Assert.Throws<InvalidOperationException>(() => EffectGroupMermaid.Parse(text));
    }

    [TestMethod]
    public void MermaidDiagnosticDiagramShowsEmptyPipelinePassthrough()
    {
        string text = EffectGroupMermaid.SerializeDiagram(new Dictionary<Guid, IEffectProvider>(), [], "Render",
            new Dictionary<string, string> { [IEffectProvider.InputAnchorGUID.ToString()] = "Source picture" },
            "Final picture", IEffectProvider.InputAnchorGUID.ToString());
        StringAssert.Contains(text, "source1-output-__Output__ --> pipeline-output");
    }
}
