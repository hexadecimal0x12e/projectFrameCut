using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace projectFrameCut.AIContracts;

[Flags]
public enum AICapability
{
    None = 0,
    Chat = 1,
    ImageGeneration = 2,
    VideoGeneration = 4,
    Extensions = 8,
}

[Flags]
public enum AIModality
{
    None = 0,
    Text = 1,
    Image = 2,
    Audio = 4,
    Video = 8,
    File = 16,
}

public enum AIConfigurationFieldType
{
    String,
    Secret,
    Uri,
    Boolean,
    Number,
    Selection,
}

public sealed record AIConfigurationFieldDescriptor
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public AIConfigurationFieldType Type { get; init; }
    public bool Required { get; init; }
    public string? DefaultValue { get; init; }
    public string? Placeholder { get; init; }
    public IReadOnlyList<string> Choices { get; init; } = [];
}

public sealed record AIModelDescriptor
{
    public required string Id { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public AICapability Capability { get; init; }
    public AIModality InputModalities { get; init; }
    public AIModality OutputModalities { get; init; }
    public AIModelLimits? Limits { get; init; }
    public IReadOnlyList<string> Features { get; init; } = [];
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

public sealed record AIModelLimits
{
    public int? MaxInputTokens { get; init; }
    public int? MaxOutputTokens { get; init; }
    public int? MaxImages { get; init; }
    public int? MaxDurationSeconds { get; init; }
    public long? MaxMediaBytes { get; init; }
}

public sealed record AIProviderDescriptor
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string Description { get; init; } = string.Empty;
    public Version Version { get; init; } = new(1, 0);
    public AICapability Capabilities { get; init; }
    public IReadOnlyList<AIConfigurationFieldDescriptor> ConfigurationFields { get; init; } = [];
    public IReadOnlyList<AIModelDescriptor> RecommendedModels { get; init; } = [];
    public IReadOnlyList<string> ExtensionCapabilities { get; init; } = [];
    public IReadOnlyDictionary<string, string> ExtensionSchemas { get; init; } = new Dictionary<string, string>();
}

public sealed record AIProviderContext
{
    public required Guid ProfileId { get; init; }
    public required string ProviderKey { get; init; }
    public required string ModelId { get; init; }
    public IReadOnlyDictionary<string, string> Configuration { get; init; } = new Dictionary<string, string>();
    public required Func<string, CancellationToken, ValueTask<string?>> SecretResolver { get; init; }

    public ValueTask<string?> GetSecretAsync(string id, CancellationToken cancellationToken = default) =>
        SecretResolver(id, cancellationToken);
}

public enum AIErrorCode
{
    Unknown,
    InvalidConfiguration,
    AuthenticationFailed,
    AccessDenied,
    UnsupportedCapability,
    InvalidRequest,
    RateLimited,
    ProviderUnavailable,
    Timeout,
    Cancelled,
    ContentRejected,
    InvalidResponse,
}

public sealed record AIProviderError(
    AIErrorCode Code,
    string Message,
    bool IsRetryable = false,
    string? ProviderCode = null);

public sealed record AIResult<T>
{
    public T? Value { get; init; }
    public AIProviderError? Error { get; init; }
    public bool Success => Error is null;

    public static AIResult<T> FromValue(T value) => new() { Value = value };
    public static AIResult<T> FromError(AIProviderError error) => new() { Error = error };
}

public sealed record AIConfigurationValidationResult
{
    public bool IsValid { get; init; }
    public IReadOnlyDictionary<string, string> FieldErrors { get; init; } = new Dictionary<string, string>();
    public AIProviderError? Error { get; init; }

    public static AIConfigurationValidationResult Valid { get; } = new() { IsValid = true };
}

public sealed record AIModelQuery(AICapability Capability, AIModality InputModalities = AIModality.Text);

public interface IAIProvider
{
    AIProviderDescriptor Descriptor { get; }
    ValueTask<AIConfigurationValidationResult> ValidateConfigurationAsync(AIProviderContext context, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<AIModelDescriptor>> GetModelsAsync(AIProviderContext context, AIModelQuery query, CancellationToken cancellationToken = default);
}

public enum AIChatRole
{
    System,
    User,
    Assistant,
    Tool,
}

public enum AIContentPartKind
{
    Text,
    Media,
    ToolCall,
    ToolResult,
}

public enum AIMediaReferenceKind
{
    Inline,
    Uri,
    File,
    Transport,
}

public sealed record AIMediaReference
{
    public AIMediaReferenceKind Kind { get; init; }
    public string MimeType { get; init; } = "application/octet-stream";
    public string? Name { get; init; }
    public string? Uri { get; init; }
    public string? Locator { get; init; }
    public byte[]? Data { get; init; }
}

public sealed record AIChatContentPart
{
    public AIContentPartKind Kind { get; init; }
    public string? Text { get; init; }
    public AIMediaReference? Media { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public string? Json { get; init; }
}

public sealed record AIChatMessage(AIChatRole Role, IReadOnlyList<AIChatContentPart> Content);

public sealed record AIToolDescriptor
{
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public string ParametersJsonSchema { get; init; } = "{}";
}

public sealed record AIChatRequest
{
    public required IReadOnlyList<AIChatMessage> Messages { get; init; }
    public IReadOnlyList<AIToolDescriptor> Tools { get; init; } = [];
    public float? Temperature { get; init; }
    public int? MaximumOutputTokens { get; init; }
    public string? ResponseJsonSchema { get; init; }
    public IReadOnlyDictionary<string, JsonElement> Options { get; init; } = new Dictionary<string, JsonElement>();
}

public enum AIChatEventKind
{
    TextDelta,
    ReasoningDelta,
    ToolCall,
    Usage,
    Completed,
    Error,
}

public sealed record AIUsage(long InputTokens = 0, long OutputTokens = 0);

public sealed record AIChatEvent
{
    public AIChatEventKind Kind { get; init; }
    public string? Text { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public string? ArgumentsJson { get; init; }
    public string? FinishReason { get; init; }
    public AIUsage? Usage { get; init; }
    public AIProviderError? Error { get; init; }
}

public interface IAIChatProvider : IAIProvider
{
    IAsyncEnumerable<AIChatEvent> StreamChatAsync(AIProviderContext context, AIChatRequest request, CancellationToken cancellationToken = default);
}

public sealed record AIImageGenerationRequest
{
    public required string Prompt { get; init; }
    public int Width { get; init; } = 1024;
    public int Height { get; init; } = 1024;
    public int Count { get; init; } = 1;
    public string? NegativePrompt { get; init; }
    public IReadOnlyList<AIMediaReference> InputImages { get; init; } = [];
    public IReadOnlyDictionary<string, JsonElement> Options { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record AIVideoGenerationRequest
{
    public required string Prompt { get; init; }
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public int DurationSeconds { get; init; } = 15;
    public bool GenerateAudio { get; init; }
    public AIMediaReference? FirstFrame { get; init; }
    public AIMediaReference? LastFrame { get; init; }
    public IReadOnlyDictionary<string, JsonElement> Options { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record AIGeneratedAsset
{
    public required AIMediaReference Media { get; init; }
    public string? Description { get; init; }
    public string? ProviderTaskId { get; init; }
}

public sealed record AIGenerationResponse(IReadOnlyList<AIGeneratedAsset> Assets, AIUsage? Usage = null);

public interface IAIImageProvider : IAIProvider
{
    ValueTask<AIResult<AIGenerationResponse>> GenerateImageAsync(AIProviderContext context, AIImageGenerationRequest request, CancellationToken cancellationToken = default);
}

public interface IAIVideoProvider : IAIProvider
{
    ValueTask<AIResult<AIGenerationResponse>> GenerateVideoAsync(AIProviderContext context, AIVideoGenerationRequest request, CancellationToken cancellationToken = default);
}

public sealed record AIExtensionRequest(string CapabilityId, JsonElement Payload);
public sealed record AIExtensionResponse(JsonElement Payload, IReadOnlyList<AIGeneratedAsset>? Assets = null);

public interface IAIExtensionProvider : IAIProvider
{
    ValueTask<AIResult<AIExtensionResponse>> InvokeAsync(AIProviderContext context, AIExtensionRequest request, CancellationToken cancellationToken = default);
}

public static class AIProviderDefaults
{
    public const string EndpointField = "endpoint";
    public const string ApiKeyField = "apiKey";

    public static async IAsyncEnumerable<AIChatEvent> ErrorStream(
        AIProviderError error,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new AIChatEvent { Kind = AIChatEventKind.Error, Error = error };
        await Task.CompletedTask;
    }
}
