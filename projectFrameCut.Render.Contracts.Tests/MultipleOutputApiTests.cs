using projectFrameCut.McpCore;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Project;
using System.Reflection;
using System.Text.Json;
using static projectFrameCut.Render.Contracts.Tests.DynamicEffectGraphTests;
using static projectFrameCut.Render.Contracts.Tests.MultipleOutputEffectTests;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MultipleOutputApiTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ApiSelectsPortsAndWorkspaceReloadPreservesTheirBindings(bool any)
    {
        MultipleOutputProvider CreateSource() => new(null,
            [Port("Image", any ? EffectArgumentFieldType.Any : EffectArgumentFieldType.IPicture), Port("Value", any ? EffectArgumentFieldType.Any : EffectArgumentFieldType.Integer)],
            _ => new Dictionary<string, object?> { ["Image"] = Picture(5), ["Value"] = 2 })
        {
            TypeResolver = (key, _, _) => key == "Image" ? EffectArgumentFieldType.IPicture : EffectArgumentFieldType.Integer,
        };
        Provider CreateTarget() => new(any ? EffectArgumentFieldType.Any : EffectArgumentFieldType.IPicture, EffectArgumentFieldType.IPicture, c => c.Input,
            Port("Amount", any ? EffectArgumentFieldType.Any : EffectArgumentFieldType.Integer));
        var source = CreateSource();
        var target = CreateTarget();
        source.SetFinalOutputSource(true, "Image");
        target.SetMainInputSource(IEffectProvider.InputAnchorGUID);
        var plugin = new DynamicEffectIsolationTests.Plugin(new()
        {
            [source.TypeName] = CreateSource, [target.TypeName] = CreateTarget,
        });
        var plugins = (IDictionary<string, IPluginBase>)PluginManager.LoadedPlugins;
        plugins.TryGetValue(plugin.PluginID, out var previous);
        plugins[plugin.PluginID] = plugin;
        try
        {
            using var files = new ProjectExternalSourceTests.SourceFiles();
            string root = files.Directory("multiple-output");
            var clip = new ClipDraftDTO
            {
                Id = Guid.NewGuid(), Name = "test", Duration = 2,
                EffectProviders = [EffectBindingHelper.SerializeProvider(source), EffectBindingHelper.SerializeProvider(target)],
            };
            File.WriteAllText(Path.Combine(root, "project.json"), JsonSerializer.Serialize(new ProjectJSONStructure()));
            File.WriteAllText(Path.Combine(root, "timeline.json"), JsonSerializer.Serialize(new DraftStructureJSON { Clips = [clip] }));
            var workspace = TimelineProjectWorkspace.Load(root);
            var service = typeof(TimelineProjectWorkspace).Assembly.GetType("projectFrameCut.IntegratedAPIServer.MCP.ProjectModeEditingService", true)!;
            var edit = service.GetMethod("Edit", BindingFlags.Public | BindingFlags.Static)!;
            object Edit(object arguments) => edit.Invoke(null, [workspace, JsonSerializer.SerializeToElement(arguments)])!;
            var error = Assert.ThrowsExactly<TargetInvocationException>(() => Edit(new
                { kind = "connectEffectProviderInput", clipId = clip.Id, providerId = target.Id, source = source.Id.ToString() }));
            Assert.IsInstanceOfType<ArgumentException>(error.InnerException);
            if (any) Edit(new { kind = "connectEffectProviderInput", clipId = clip.Id, providerId = target.Id, source = source.Id.ToString(), sourceOutputId = "Value" });
            var description = JsonSerializer.SerializeToElement(Edit(new
                { kind = "connectEffectProviderInput", clipId = clip.Id, providerId = target.Id, source = source.Id.ToString(), sourceOutputId = "Image" }));
            Assert.AreEqual(1, description.GetProperty("outputPorts").GetArrayLength());
            Assert.AreEqual("IPicture", description.GetProperty("outputPorts")[0].GetProperty("resolvedFieldType").GetString());
            if (any)
            {
                Edit(new { kind = "updateEffectProvider", clipId = clip.Id, providerId = target.Id, fields = new { Amount = new { numbers = new[] { 1, 2 } } } });
                Edit(new { kind = "bindEffectProviderField", clipId = clip.Id, providerId = target.Id, fieldId = "Amount", source = source.Id.ToString(), sourceOutputId = "Image" });
            }
            Edit(new { kind = "bindEffectProviderField", clipId = clip.Id, providerId = target.Id, fieldId = "Amount", source = source.Id.ToString(), sourceOutputId = "Value" });
            Edit(new { kind = "setEffectProviderOutput", clipId = clip.Id, providerId = source.Id, outputId = "Image" });
            workspace.Save();
            var restored = EffectBindingHelper.MigrateToEffectProviders(TimelineProjectWorkspace.Load(root).Draft.Clips.Single().EffectProviders, null, out var diagnostics);
            Assert.AreEqual(0, diagnostics.Count);
            Assert.IsInstanceOfType<IMultipleOutputEffectProvider>(restored[source.Id]);
            Assert.AreEqual("Image", restored[source.Id].GetFinalOutputFieldId());
            Assert.AreEqual(EffectProviderOutputExtensions.CreateOutputSourceId(source.Id, "Image"), restored[target.Id].GetMainInputSource());
            Assert.IsTrue(restored[target.Id].TryGetFieldBinding("Amount", out var binding));
            Assert.AreEqual(EffectProviderOutputExtensions.CreateOutputSourceId(source.Id, "Value"), binding);
            if (any)
            {
                var field = (DynamicEffectParamField)restored[target.Id].Fields["Amount"];
                Assert.AreEqual(EffectArgumentFieldType.Any, field.FieldType);
                Assert.AreEqual(2, ((JsonElement)field.StaticFallbackValue!).GetProperty("numbers")[1].GetInt32());
            }
            Edit(new { kind = "setEffectProviderOutput", clipId = clip.Id, providerId = target.Id });
            Edit(new { kind = "removeEffectProvider", clipId = clip.Id, providerId = source.Id });
            restored = EffectBindingHelper.MigrateToEffectProviders(workspace.Draft.Clips.Single().EffectProviders, null);
            Assert.AreEqual(1, restored.Count);
            Assert.AreEqual(IEffectProvider.NoConnectionGUID.ToString(), restored[target.Id].GetMainInputSource());
            Assert.IsFalse(restored[target.Id].TryGetFieldBinding("Amount", out _));
        }
        finally
        {
            if (previous is null) plugins.Remove(plugin.PluginID);
            else plugins[plugin.PluginID] = previous;
        }
    }
}
