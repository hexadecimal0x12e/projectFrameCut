using projectFrameCut.AIContracts;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Shared;

namespace projectFrameCut.AIAssistance;

public sealed record AIProviderRegistration(string Key, string PluginId, IAIProvider Provider);

public sealed class AIProviderRegistry
{
    private readonly Dictionary<string, AIProviderRegistration> providers = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, AIProviderRegistration> Providers => providers;

    public void Rebuild(IEnumerable<AIProviderRegistration>? builtIns = null)
    {
        providers.Clear();
        foreach (var item in builtIns ?? []) Register(item);

        foreach (var plugin in PluginManager.LoadedPlugins.Values)
        {
            if (PluginManager.ProjectPluginIds.Contains(plugin.PluginID) || plugin is not IAIProviderPlugin aiPlugin) continue;
            foreach (var item in aiPlugin.AIProviderFactories)
            {
                try
                {
                    var provider = item.Value();
                    Register(new AIProviderRegistration($"{plugin.PluginID}/{item.Key}", plugin.PluginID, provider));
                }
                catch (Exception ex)
                {
                    Logger.Log(ex, $"Create AI provider '{plugin.PluginID}/{item.Key}'", typeof(AIProviderRegistry));
                }
            }
        }
    }

    public AIProviderRegistration Get(string key) => providers.TryGetValue(key, out var value)
        ? value
        : throw new KeyNotFoundException($"AI provider '{key}' is not available.");

    public bool TryGet(string key, out AIProviderRegistration registration) => providers.TryGetValue(key, out registration!);

    private void Register(AIProviderRegistration registration)
    {
        Validate(registration);
        if (!providers.TryAdd(registration.Key, registration))
            throw new InvalidOperationException($"AI provider key '{registration.Key}' is already registered.");
        Logger.Log($"AI provider '{registration.Key}' registered with capabilities '{registration.Provider.Descriptor.Capabilities}'.");
    }

    private static void Validate(AIProviderRegistration registration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.Key);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.PluginId);
        var provider = registration.Provider ?? throw new ArgumentNullException(nameof(registration.Provider));
        if (string.IsNullOrWhiteSpace(provider.Descriptor.Id) || string.IsNullOrWhiteSpace(provider.Descriptor.DisplayName))
            throw new InvalidDataException($"AI provider '{registration.Key}' has invalid metadata.");
        if (!string.Equals(registration.Key[(registration.Key.LastIndexOf('/') + 1)..], provider.Descriptor.Id, StringComparison.Ordinal))
            throw new InvalidDataException($"AI provider key '{registration.Key}' does not match descriptor id '{provider.Descriptor.Id}'.");
        if (provider.Descriptor.Capabilities == AICapability.None)
            throw new InvalidDataException($"AI provider '{registration.Key}' does not declare a capability.");
        if (provider.Descriptor.Capabilities.HasFlag(AICapability.Chat) && provider is not IAIChatProvider ||
            provider.Descriptor.Capabilities.HasFlag(AICapability.ImageGeneration) && provider is not IAIImageProvider ||
            provider.Descriptor.Capabilities.HasFlag(AICapability.VideoGeneration) && provider is not IAIVideoProvider ||
            provider.Descriptor.Capabilities.HasFlag(AICapability.Extensions) && provider is not IAIExtensionProvider)
            throw new InvalidDataException($"AI provider '{registration.Key}' capability declaration does not match its implemented interfaces.");
    }
}
