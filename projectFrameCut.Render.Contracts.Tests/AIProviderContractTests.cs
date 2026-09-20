using projectFrameCut.AIContracts;
using projectFrameCut.Render.Contracts;
using System.Text.Json;

namespace projectFrameCut.Render.Contracts.Tests;

[TestClass]
public sealed class AIProviderContractTests
{
    [TestMethod]
    public void ProviderDescriptor_RoundTripsWithoutProviderSdkTypes()
    {
        var source = new AIProviderDescriptor
        {
            Id = "example",
            DisplayName = "Example",
            Capabilities = AICapability.Chat | AICapability.ImageGeneration,
            ConfigurationFields = [new() { Id = "apiKey", DisplayName = "API Key", Type = AIConfigurationFieldType.Secret, Required = true }],
            RecommendedModels = [new() { Id = "model", Capability = AICapability.Chat, InputModalities = AIModality.Text, OutputModalities = AIModality.Text }],
        };

        var result = JsonSerializer.Deserialize<AIProviderDescriptor>(JsonSerializer.Serialize(source));

        Assert.IsNotNull(result);
        Assert.AreEqual(source.Id, result.Id);
        Assert.AreEqual(source.Capabilities, result.Capabilities);
        Assert.AreEqual(AIConfigurationFieldType.Secret, result.ConfigurationFields[0].Type);
    }

    [TestMethod]
    public void IsolationDescriptor_PreservesAIProviderCatalog()
    {
        var source = new IsolationPluginDescriptor
        {
            PluginId = "example.plugin",
            AIProviders = [new() { ProviderId = "example", DescriptorJson = "{\"Id\":\"example\"}" }],
        };

        var result = RenderRpcSerializer.Deserialize<IsolationPluginDescriptor>(RenderRpcSerializer.Serialize(source));

        Assert.AreEqual(1, result.AIProviders.Count);
        Assert.AreEqual("example", result.AIProviders[0].ProviderId);
    }

    [TestMethod]
    public void AIError_DoesNotRequireRawProviderException()
    {
        var result = AIResult<AIGenerationResponse>.FromError(new(AIErrorCode.RateLimited, "Rate limited", true, "429"));

        Assert.IsFalse(result.Success);
        Assert.AreEqual(AIErrorCode.RateLimited, result.Error?.Code);
        Assert.IsTrue(result.Error?.IsRetryable);
    }
}
