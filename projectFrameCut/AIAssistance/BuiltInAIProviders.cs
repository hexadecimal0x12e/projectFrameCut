using Microsoft.Extensions.AI;
using OpenAI;
using projectFrameCut.AIContracts;
using projectFrameCut.Drawing.Base.Picture;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ContractMessage = projectFrameCut.AIContracts.AIChatMessage;
using ExtensionsMessage = Microsoft.Extensions.AI.ChatMessage;
using OpenAIChatClient = OpenAI.Chat.ChatClient;

namespace projectFrameCut.AIAssistance;

public static class BuiltInAIProviders
{
    public const string PluginId = "projectFrameCut.InternalAI";
    public const string OpenAI = PluginId + "/openai-compatible";
    public const string Google = PluginId + "/google";
    public const string Doubao = PluginId + "/doubao";
    public const string Qwen = PluginId + "/qwen";
    public const string HappyHorse = PluginId + "/happyhorse";

    public static IEnumerable<AIProviderRegistration> CreateRegistrations()
    {
        yield return Create("openai-compatible", "OpenAI Compatible", "OpenAI", AICapability.Chat | AICapability.ImageGeneration | AICapability.VideoGeneration);
        yield return Create("google", "Google", "Google", AICapability.Chat | AICapability.ImageGeneration);
        yield return Create("doubao", "Doubao", "Doubao", AICapability.Chat | AICapability.ImageGeneration | AICapability.VideoGeneration);
        yield return Create("qwen", "Qwen", "Qwen", AICapability.Chat | AICapability.ImageGeneration | AICapability.VideoGeneration);
        yield return Create("happyhorse", "HappyHorse", "HappyHorse", AICapability.VideoGeneration);
    }

    public static string MapLegacyProvider(string provider) => provider switch
    {
        "Google" => Google,
        "Doubao" => Doubao,
        "Qwen" or "Qwen (WanX)" => Qwen,
        "HappyHorse" => HappyHorse,
        _ => OpenAI,
    };

    private static AIProviderRegistration Create(string id, string name, string legacyName, AICapability capabilities) =>
        new($"{PluginId}/{id}", PluginId, new BuiltInAIProvider(id, name, legacyName, capabilities));
}

internal sealed class BuiltInAIProvider : IAIChatProvider, IAIImageProvider, IAIVideoProvider
{
    private readonly string legacyName;

    public BuiltInAIProvider(string id, string displayName, string legacyName, AICapability capabilities)
    {
        this.legacyName = legacyName;
        var config = ModelConfig.GetBuiltInConfig();
        var models = config.GetTextModels(legacyName).Select(x => Model(x, AICapability.Chat, AIModality.Text | AIModality.Image, AIModality.Text))
            .Concat(config.GetImageModels(legacyName).Select(x => Model(x, AICapability.ImageGeneration, AIModality.Text | AIModality.Image, AIModality.Image)))
            .Concat(config.GetVideoModels(legacyName).Select(x => Model(x, AICapability.VideoGeneration, AIModality.Text | AIModality.Image, AIModality.Video)))
            .ToArray();
        Descriptor = new()
        {
            Id = id,
            DisplayName = displayName,
            Description = $"Built-in {displayName} provider",
            Capabilities = capabilities,
            ConfigurationFields =
            [
                new() { Id = AIProviderDefaults.EndpointField, DisplayName = "Endpoint", Type = AIConfigurationFieldType.Uri, Required = true, DefaultValue = DefaultEndpoint(legacyName) },
                new() { Id = AIProviderDefaults.ApiKeyField, DisplayName = "API Key", Type = AIConfigurationFieldType.Secret, Required = legacyName != "HappyHorse" },
            ],
            RecommendedModels = models,
        };
    }

    public AIProviderDescriptor Descriptor { get; }

    public async ValueTask<AIConfigurationValidationResult> ValidateConfigurationAsync(AIProviderContext context, CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string>();
        if (!context.Configuration.TryGetValue(AIProviderDefaults.EndpointField, out var endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out _))
            errors[AIProviderDefaults.EndpointField] = "A valid absolute endpoint is required.";
        if (legacyName != "HappyHorse" && string.IsNullOrWhiteSpace(await context.GetSecretAsync(AIProviderDefaults.ApiKeyField, cancellationToken)))
            errors[AIProviderDefaults.ApiKeyField] = "An API key is required.";
        return errors.Count == 0 ? AIConfigurationValidationResult.Valid : new() { IsValid = false, FieldErrors = errors };
    }

    public async ValueTask<IReadOnlyList<AIModelDescriptor>> GetModelsAsync(AIProviderContext context, AIModelQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Capability != AICapability.Chat)
            return Descriptor.RecommendedModels.Where(x => x.Capability.HasFlag(query.Capability)).ToArray();
        if (context.Configuration.TryGetValue(AIProviderDefaults.EndpointField, out var endpoint) && Uri.TryCreate(endpoint, UriKind.Absolute, out _))
        {
            if (legacyName == "Qwen" && query.Capability == AICapability.Chat && endpoint.StartsWith("https://dashscope.aliyuncs.com/api/v1", StringComparison.OrdinalIgnoreCase))
                endpoint = endpoint.Replace("/api/v1", "/compatible-mode/v1", StringComparison.OrdinalIgnoreCase);
            string key = await context.GetSecretAsync(AIProviderDefaults.ApiKeyField, cancellationToken) ?? string.Empty;
            ProviderInfo discovered;
            try
            {
                discovered = await AIHelper.GetModelProviderInfos(endpoint, key, cancellationToken);
            }
            catch (Exception ex)
            {
                Log(ex, $"AI model discovery for '{Descriptor.Id}'", this);
                return [];
            }
            if (discovered.Models.Count > 0)
                return discovered.Models.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Model(x!, query.Capability, query.InputModalities, Output(query.Capability))).ToArray();
        }
        return Descriptor.RecommendedModels.Where(x => x.Capability.HasFlag(query.Capability)).ToArray();
    }

    public async IAsyncEnumerable<AIChatEvent> StreamChatAsync(AIProviderContext context, AIChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Descriptor.Capabilities.HasFlag(AICapability.Chat))
        {
            yield return Error(AIErrorCode.UnsupportedCapability, "This provider does not support chat.");
            yield break;
        }

        using var client = await CreateChatClientAsync(context, cancellationToken);
        var options = new ChatOptions
        {
            ModelId = context.ModelId,
            Temperature = request.Temperature,
            MaxOutputTokens = request.MaximumOutputTokens,
            Tools = request.Tools.Select(x => (AITool)AIFunctionFactory.CreateDeclaration(
                x.Name,
                x.Description,
                JsonDocument.Parse(x.ParametersJsonSchema).RootElement.Clone(),
                null)).ToList(),
        };
        await foreach (var update in client.GetStreamingResponseAsync(request.Messages.Select(ToExtensionsMessage), options, cancellationToken))
        {
            foreach (var content in update.Contents)
            {
                if (content is TextReasoningContent reasoning && !string.IsNullOrEmpty(reasoning.Text))
                    yield return new() { Kind = AIChatEventKind.ReasoningDelta, Text = reasoning.Text };
                else if (content is TextContent text && !string.IsNullOrEmpty(text.Text))
                    yield return new() { Kind = AIChatEventKind.TextDelta, Text = text.Text };
                else if (content is FunctionCallContent call)
                    yield return new() { Kind = AIChatEventKind.ToolCall, ToolCallId = call.CallId, ToolName = call.Name, ArgumentsJson = JsonSerializer.Serialize(call.Arguments) };
                else if (content is UsageContent usage)
                    yield return new() { Kind = AIChatEventKind.Usage, Usage = new(usage.Details.InputTokenCount ?? 0, usage.Details.OutputTokenCount ?? 0) };
            }
            if (update.FinishReason is { } finish)
                yield return new() { Kind = AIChatEventKind.Completed, FinishReason = finish.Value };
        }
    }

    public async ValueTask<AIResult<AIGenerationResponse>> GenerateImageAsync(AIProviderContext context, AIImageGenerationRequest request, CancellationToken cancellationToken = default)
    {
        if (!Descriptor.Capabilities.HasFlag(AICapability.ImageGeneration))
            return AIResult<AIGenerationResponse>.FromError(new(AIErrorCode.UnsupportedCapability, "This provider does not support image generation."));
        try
        {
            var result = legacyName switch
            {
                "Qwen" => await AIHelper.GenerateImageWithQwen(request.Prompt, ToLegacy(request), await ToLegacyAsync(context, cancellationToken)).WaitAsync(cancellationToken),
                _ => await AIHelper.GenerateImageWithOpenAI(request.Prompt, ToLegacy(request), await ToLegacyAsync(context, cancellationToken)).WaitAsync(cancellationToken),
            };
            return ToResult(result.Success, result.ImageUrl, result.Description, null, result.ErrorMessage, "image/*");
        }
        catch (Exception ex)
        {
            return AIResult<AIGenerationResponse>.FromError(AIProviderService.ToError(ex));
        }
    }

    public async ValueTask<AIResult<AIGenerationResponse>> GenerateVideoAsync(AIProviderContext context, AIVideoGenerationRequest request, CancellationToken cancellationToken = default)
    {
        if (!Descriptor.Capabilities.HasFlag(AICapability.VideoGeneration))
            return AIResult<AIGenerationResponse>.FromError(new(AIErrorCode.UnsupportedCapability, "This provider does not support video generation."));
        string? firstPath = null;
        string? lastPath = null;
        bool deleteFirst = false;
        bool deleteLast = false;
        try
        {
            var option = await ToVideoLegacyAsync(context, cancellationToken);
            var legacyOptions = ToLegacy(request);
            VideoGenerationResult result;
            if (request.FirstFrame is not null || request.LastFrame is not null)
            {
                (firstPath, deleteFirst) = await MaterializeAsync(request.FirstFrame ?? request.LastFrame!, cancellationToken);
                (lastPath, deleteLast) = await MaterializeAsync(request.LastFrame ?? request.FirstFrame!, cancellationToken);
                using var first = new Picture8bpp(firstPath);
                using var last = new Picture8bpp(lastPath);
                result = legacyName switch
                {
                    "Qwen" => await AIHelper.GenerateVideoWithQwenFrames(first, last, request.Prompt, legacyOptions, option).WaitAsync(cancellationToken),
                    "HappyHorse" => await AIHelper.GenerateVideoWithHappyHorseFrames(first, last, request.Prompt, legacyOptions, option).WaitAsync(cancellationToken),
                    _ => new() { Success = false, ErrorMessage = "This provider does not support frame-based video generation." },
                };
            }
            else
            {
                result = legacyName switch
                {
                    "Qwen" => await AIHelper.GenerateVideoWithQwen(request.Prompt, legacyOptions, option).WaitAsync(cancellationToken),
                    "HappyHorse" => await AIHelper.GenerateVideoWithHappyHorse(request.Prompt, legacyOptions, option).WaitAsync(cancellationToken),
                    "Doubao" => await AIHelper.GenerateVideoWithDoubao(request.Prompt, legacyOptions, option).WaitAsync(cancellationToken),
                    _ => await AIHelper.GenerateVideoWithOpenAI(request.Prompt, legacyOptions, option).WaitAsync(cancellationToken),
                };
            }
            return ToResult(result.Success, result.VideoUrl, result.Description, result.TaskId, result.ErrorMessage, "video/*");
        }
        catch (Exception ex)
        {
            return AIResult<AIGenerationResponse>.FromError(AIProviderService.ToError(ex));
        }
        finally
        {
            if (deleteFirst && firstPath is not null) TryDelete(firstPath);
            if (deleteLast && lastPath is not null && !string.Equals(lastPath, firstPath, StringComparison.Ordinal)) TryDelete(lastPath);
        }
    }

    private async Task<IChatClient> CreateChatClientAsync(AIProviderContext context, CancellationToken cancellationToken)
    {
        string endpoint = context.Configuration.GetValueOrDefault(AIProviderDefaults.EndpointField) ?? string.Empty;
        if (legacyName == "Qwen" && endpoint.StartsWith("https://dashscope.aliyuncs.com/api/v1", StringComparison.OrdinalIgnoreCase))
            endpoint = endpoint.Replace("/api/v1", "/compatible-mode/v1", StringComparison.OrdinalIgnoreCase);
        string key = await context.GetSecretAsync(AIProviderDefaults.ApiKeyField, cancellationToken) ?? string.Empty;
        var client = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            ? new OpenAIChatClient(context.ModelId, new System.ClientModel.ApiKeyCredential(key), new OpenAIClientOptions { Endpoint = uri })
            : new OpenAIChatClient(context.ModelId, key);
        return client.AsIChatClient();
    }

    private static ExtensionsMessage ToExtensionsMessage(ContractMessage message)
    {
        var contents = new List<AIContent>();
        foreach (var part in message.Content)
        {
            switch (part.Kind)
            {
                case AIContentPartKind.Text:
                    contents.Add(new TextContent(part.Text ?? string.Empty));
                    break;
                case AIContentPartKind.Media when part.Media?.Data is { } data:
                    contents.Add(new DataContent(data, part.Media.MimeType) { Name = part.Media.Name });
                    break;
                case AIContentPartKind.Media when Uri.TryCreate(part.Media?.Uri, UriKind.Absolute, out var uri):
                    contents.Add(new DataContent(uri, part.Media!.MimeType) { Name = part.Media.Name });
                    break;
                case AIContentPartKind.ToolCall:
                    contents.Add(new FunctionCallContent(part.ToolCallId ?? string.Empty, part.ToolName ?? string.Empty, DeserializeArguments(part.Json)));
                    break;
                case AIContentPartKind.ToolResult:
                    contents.Add(new FunctionResultContent(part.ToolCallId ?? string.Empty, part.Json));
                    break;
            }
        }
        return new ExtensionsMessage(ToRole(message.Role), contents);
    }

    private static Microsoft.Extensions.AI.ChatRole ToRole(AIChatRole role) => role switch
    {
        AIChatRole.System => Microsoft.Extensions.AI.ChatRole.System,
        AIChatRole.Assistant => Microsoft.Extensions.AI.ChatRole.Assistant,
        AIChatRole.Tool => Microsoft.Extensions.AI.ChatRole.Tool,
        _ => Microsoft.Extensions.AI.ChatRole.User,
    };

    private static Dictionary<string, object?> DeserializeArguments(string? json) => string.IsNullOrWhiteSpace(json)
        ? []
        : JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? [];

    private static AIModelDescriptor Model(string id, AICapability capability, AIModality input, AIModality output) => new()
    {
        Id = id,
        DisplayName = id,
        Capability = capability,
        InputModalities = input,
        OutputModalities = output,
    };

    private static AIModality Output(AICapability capability) => capability switch
    {
        AICapability.ImageGeneration => AIModality.Image,
        AICapability.VideoGeneration => AIModality.Video,
        _ => AIModality.Text,
    };

    private static string DefaultEndpoint(string provider) => provider switch
    {
        "Google" => "https://generativelanguage.googleapis.com/v1beta/openai",
        "Doubao" => "https://ark.cn-beijing.volces.com/api/v3",
        "Qwen" or "HappyHorse" => "https://dashscope.aliyuncs.com/api/v1",
        _ => "https://api.openai.com/v1",
    };

    private async Task<AIOption> ToLegacyAsync(AIProviderContext context, CancellationToken cancellationToken) => new()
    {
        Provider = legacyName,
        BaseAddress = context.Configuration.GetValueOrDefault(AIProviderDefaults.EndpointField) ?? string.Empty,
        Model = context.ModelId,
        Key = await context.GetSecretAsync(AIProviderDefaults.ApiKeyField, cancellationToken) ?? string.Empty,
    };

    private async Task<VideoGenAIOption> ToVideoLegacyAsync(AIProviderContext context, CancellationToken cancellationToken) => new()
    {
        Provider = legacyName,
        BaseAddress = context.Configuration.GetValueOrDefault(AIProviderDefaults.EndpointField) ?? string.Empty,
        Text2VideoModel = context.ModelId,
        Image2VideoModel = context.ModelId,
        Key = await context.GetSecretAsync(AIProviderDefaults.ApiKeyField, cancellationToken) ?? string.Empty,
    };

    private static ImageGenerationOptions ToLegacy(AIImageGenerationRequest request) => new()
    {
        Width = request.Width,
        Height = request.Height,
        NegativePrompt = request.NegativePrompt,
        Style = ReadEnum(request.Options, "style", ImageStyle.Natural),
        Quality = ReadEnum(request.Options, "quality", ImageQuality.Standard),
    };

    private static VideoGenerationOptions ToLegacy(AIVideoGenerationRequest request) => new()
    {
        Width = request.Width,
        Height = request.Height,
        Duration = request.DurationSeconds,
        GenerateAudio = request.GenerateAudio,
        PromptExtend = Read(request.Options, "promptExtend", true),
        Watermark = Read(request.Options, "watermark", true),
        ShotType = Read(request.Options, "shotType", "multi"),
        Resolution = Read(request.Options, "resolution", "1080P"),
        Ratio = Read(request.Options, "ratio", "adaptive"),
        Seed = request.Options.TryGetValue("seed", out var seed) && seed.ValueKind == JsonValueKind.Number ? seed.GetInt32() : null,
    };

    private static T Read<T>(IReadOnlyDictionary<string, JsonElement> options, string key, T fallback)
    {
        if (!options.TryGetValue(key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return fallback;
        try
        {
            var result = value.Deserialize<T>();
            return result is null ? fallback : result;
        }
        catch { return fallback; }
    }

    private static T ReadEnum<T>(IReadOnlyDictionary<string, JsonElement> options, string key, T fallback) where T : struct, Enum =>
        options.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String && Enum.TryParse<T>(value.GetString(), true, out var result) ? result : fallback;

    private static AIResult<AIGenerationResponse> ToResult(bool success, string? uri, string? description, string? taskId, string? error, string mimeType)
    {
        if (!success || string.IsNullOrWhiteSpace(uri))
            return AIResult<AIGenerationResponse>.FromError(new(AIErrorCode.InvalidResponse, error ?? "The provider returned no generated media."));
        return AIResult<AIGenerationResponse>.FromValue(new([
            new() { Media = new() { Kind = AIMediaReferenceKind.Uri, Uri = uri, MimeType = mimeType }, Description = description, ProviderTaskId = taskId }
        ]));
    }

    private static async Task<(string Path, bool Delete)> MaterializeAsync(AIMediaReference media, CancellationToken cancellationToken)
    {
        if (media.Kind == AIMediaReferenceKind.File && !string.IsNullOrWhiteSpace(media.Locator)) return (media.Locator, false);
        string path = Path.Combine(FileSystem.CacheDirectory, $"ai-input-{Guid.NewGuid():N}");
        if (media.Data is { } data)
            await File.WriteAllBytesAsync(path, data, cancellationToken);
        else if (Uri.TryCreate(media.Uri, UriKind.Absolute, out var uri))
        {
            using var client = new HttpClient();
            await File.WriteAllBytesAsync(path, await client.GetByteArrayAsync(uri, cancellationToken), cancellationToken);
        }
        else
            throw new InvalidDataException("The AI media reference cannot be materialized.");
        return (path, true);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }

    private static AIChatEvent Error(AIErrorCode code, string message) => new() { Kind = AIChatEventKind.Error, Error = new(code, message) };
}
