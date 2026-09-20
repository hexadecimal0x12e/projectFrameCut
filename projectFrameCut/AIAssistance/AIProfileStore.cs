using projectFrameCut.AIContracts;
using projectFrameCut.Shared;
using System.Text.Json;

namespace projectFrameCut.AIAssistance;

public static class AISelectionKeys
{
    public const string Chat = "chat";
    public const string Image = "image";
    public const string TextToVideo = "video.text";
    public const string FrameToVideo = "video.frame";
}

public sealed record AIConnectionProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public required string ProviderKey { get; init; }
    public Dictionary<string, string> Configuration { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> SecretFieldIds { get; init; } = new(StringComparer.OrdinalIgnoreCase) { AIProviderDefaults.ApiKeyField };
}

public sealed record AIModelSelection
{
    public required Guid ProfileId { get; init; }
    public required string ModelId { get; init; }
}

public sealed class AIProfileDocument
{
    public int Version { get; set; } = 1;
    public bool LegacyMigrationComplete { get; set; }
    public List<AIConnectionProfile> Profiles { get; set; } = [];
    public Dictionary<string, AIModelSelection> Defaults { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public interface IAISecretStore
{
    ValueTask<string?> GetAsync(Guid profileId, string fieldId);
    ValueTask SetAsync(Guid profileId, string fieldId, string value);
    bool Remove(Guid profileId, string fieldId);
}

public sealed class MauiAISecretStore : IAISecretStore
{
    private static string Key(Guid profileId, string fieldId) => $"ai_profile_{profileId:N}_{fieldId}";
    public async ValueTask<string?> GetAsync(Guid profileId, string fieldId) => await SecureStorage.Default.GetAsync(Key(profileId, fieldId));
    public async ValueTask SetAsync(Guid profileId, string fieldId, string value) => await SecureStorage.Default.SetAsync(Key(profileId, fieldId), value);
    public bool Remove(Guid profileId, string fieldId) => SecureStorage.Default.Remove(Key(profileId, fieldId));
}

public sealed class AIProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string path;
    private readonly IAISecretStore secrets;

    private AIProfileStore(string dataRoot, IAISecretStore secrets)
    {
        path = Path.Combine(dataRoot, "ai_profiles.json");
        this.secrets = secrets;
    }

    public AIProfileDocument Document { get; private set; } = new();
    public Exception? LoadError { get; private set; }

    public static AIProfileStore CreateEmpty(string dataRoot, IAISecretStore secrets, Exception? error = null) => new(dataRoot, secrets) { LoadError = error };

    public static async Task<AIProfileStore> LoadAsync(string dataRoot, IAISecretStore secrets, CancellationToken cancellationToken = default)
    {
        var store = new AIProfileStore(dataRoot, secrets);
        if (File.Exists(store.path))
        {
            store.Document = JsonSerializer.Deserialize<AIProfileDocument>(await File.ReadAllTextAsync(store.path, cancellationToken), JsonOptions) ?? new();
            if (await store.ProtectEmbeddedSecretsAsync(cancellationToken)) await store.SaveAsync(cancellationToken);
            if (store.Document.LegacyMigrationComplete) store.DeleteLegacyFiles(dataRoot);
            return store;
        }

        await store.MigrateLegacyAsync(dataRoot, cancellationToken);
        return store;
    }

    public AIConnectionProfile GetProfile(Guid id) => Document.Profiles.FirstOrDefault(x => x.Id == id)
        ?? throw new KeyNotFoundException($"AI profile '{id}' was not found.");

    public (AIConnectionProfile Profile, AIModelSelection Selection) GetDefault(string selectionKey)
    {
        if (!Document.Defaults.TryGetValue(selectionKey, out var selection))
            throw new InvalidOperationException($"No default AI model is configured for '{selectionKey}'.");
        return (GetProfile(selection.ProfileId), selection);
    }

    public AIProviderContext CreateContext(AIConnectionProfile profile, string modelId) => new()
    {
        ProfileId = profile.Id,
        ProviderKey = profile.ProviderKey,
        ModelId = modelId,
        Configuration = profile.Configuration,
        SecretResolver = (id, _) => secrets.GetAsync(profile.Id, id),
    };

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        _ = await ProtectEmbeddedSecretsAsync(cancellationToken);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(Document, JsonOptions), cancellationToken);
        File.Move(temp, path, true);
    }

    public async Task SetSecretAsync(Guid profileId, string fieldId, string value) => await secrets.SetAsync(profileId, fieldId, value);
    public ValueTask<string?> GetSecretAsync(Guid profileId, string fieldId) => secrets.GetAsync(profileId, fieldId);
    public bool RemoveSecret(Guid profileId, string fieldId) => secrets.Remove(profileId, fieldId);

    private async Task MigrateLegacyAsync(string dataRoot, CancellationToken cancellationToken)
    {
        string textPath = Path.Combine(dataRoot, "ai_settings_text.json");
        string imagePath = Path.Combine(dataRoot, "ai_settings_image.json");
        string videoPath = Path.Combine(dataRoot, "ai_settings_video.json");
        if (!File.Exists(textPath) && !File.Exists(imagePath) && !File.Exists(videoPath)) return;

        var writtenSecrets = new List<(Guid ProfileId, string FieldId)>();
        try
        {
            AIOption? text = await ReadAsync<AIOption>(textPath, cancellationToken);
            AIOption? image = await ReadAsync<AIOption>(imagePath, cancellationToken);
            VideoGenAIOption? video = await ReadAsync<VideoGenAIOption>(videoPath, cancellationToken);
            var profileKeys = new Dictionary<string, AIConnectionProfile>(StringComparer.Ordinal);

            async Task<AIConnectionProfile?> AddAsync(string provider, string endpoint, string key)
            {
                if (string.IsNullOrWhiteSpace(provider) && string.IsNullOrWhiteSpace(endpoint)) return null;
                string providerKey = BuiltInAIProviders.MapLegacyProvider(provider);
                string profileKey = $"{providerKey}\n{endpoint}\n{key}";
                if (profileKeys.TryGetValue(profileKey, out var existing)) return existing;
                var profile = new AIConnectionProfile
                {
                    Name = string.IsNullOrWhiteSpace(provider) ? "AI Provider" : provider,
                    ProviderKey = providerKey,
                    Configuration = new() { [AIProviderDefaults.EndpointField] = endpoint },
                    SecretFieldIds = [AIProviderDefaults.ApiKeyField],
                };
                Document.Profiles.Add(profile);
                profileKeys.Add(profileKey, profile);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    await secrets.SetAsync(profile.Id, AIProviderDefaults.ApiKeyField, key);
                    writtenSecrets.Add((profile.Id, AIProviderDefaults.ApiKeyField));
                    if (!string.Equals(await secrets.GetAsync(profile.Id, AIProviderDefaults.ApiKeyField), key, StringComparison.Ordinal))
                        throw new InvalidOperationException($"Failed to verify the migrated credential for profile '{profile.Name}'.");
                }
                return profile;
            }

            if (text is not null && await AddAsync(text.Provider, text.BaseAddress, text.Key) is { } tp && !string.IsNullOrWhiteSpace(text.Model))
                Document.Defaults[AISelectionKeys.Chat] = new() { ProfileId = tp.Id, ModelId = text.Model };
            if (image is not null && await AddAsync(image.Provider, image.BaseAddress, image.Key) is { } ip && !string.IsNullOrWhiteSpace(image.Model))
                Document.Defaults[AISelectionKeys.Image] = new() { ProfileId = ip.Id, ModelId = image.Model };
            if (video is not null && await AddAsync(video.Provider, video.BaseAddress, video.Key) is { } vp)
            {
                if (!string.IsNullOrWhiteSpace(video.Text2VideoModel)) Document.Defaults[AISelectionKeys.TextToVideo] = new() { ProfileId = vp.Id, ModelId = video.Text2VideoModel };
                if (!string.IsNullOrWhiteSpace(video.Image2VideoModel)) Document.Defaults[AISelectionKeys.FrameToVideo] = new() { ProfileId = vp.Id, ModelId = video.Image2VideoModel };
            }

            Document.LegacyMigrationComplete = true;
            await SaveAsync(cancellationToken);
            DeleteLegacyFiles(dataRoot);
            Logger.Log($"Migrated {Document.Profiles.Count} AI connection profiles to protected storage.");
        }
        catch (Exception ex)
        {
            foreach (var item in writtenSecrets) secrets.Remove(item.ProfileId, item.FieldId);
            try { File.Delete(path + ".tmp"); } catch { }
            Document = new();
            Logger.Log(ex, "Migrate legacy AI settings", typeof(AIProfileStore));
            throw;
        }
    }

    private static async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken) => File.Exists(path)
        ? JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, cancellationToken))
        : default;

    private async Task<bool> ProtectEmbeddedSecretsAsync(CancellationToken cancellationToken)
    {
        bool changed = false;
        foreach (var profile in Document.Profiles)
        {
            foreach (string fieldId in profile.SecretFieldIds)
            {
                if (!profile.Configuration.TryGetValue(fieldId, out var value)) continue;
                cancellationToken.ThrowIfCancellationRequested();
                await secrets.SetAsync(profile.Id, fieldId, value);
                if (!string.Equals(await secrets.GetAsync(profile.Id, fieldId), value, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Failed to protect credential '{fieldId}' for profile '{profile.Name}'.");
                profile.Configuration.Remove(fieldId);
                changed = true;
            }
        }
        return changed;
    }

    private void DeleteLegacyFiles(string dataRoot)
    {
        try
        {
            foreach (string name in new[] { "ai_settings_text.json", "ai_settings_image.json", "ai_settings_video.json" })
            {
                string legacy = Path.Combine(dataRoot, name);
                if (File.Exists(legacy)) File.Delete(legacy);
            }
        }
        catch (Exception ex)
        {
            LoadError = ex;
            Logger.Log($"Legacy AI configuration cleanup failed with {ex.GetType().FullName}.", "error");
        }
    }
}
