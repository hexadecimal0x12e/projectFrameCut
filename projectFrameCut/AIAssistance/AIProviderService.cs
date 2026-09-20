using projectFrameCut.AIContracts;
using projectFrameCut.Shared;

namespace projectFrameCut.AIAssistance;

public sealed class AIProviderService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(15);
    public static AIProviderService? Current { get; private set; }

    public AIProviderService(AIProviderRegistry registry, AIProfileStore profiles)
    {
        Registry = registry;
        Profiles = profiles;
    }

    public AIProviderRegistry Registry { get; }
    public AIProfileStore Profiles { get; }

    public static void Initialize(AIProviderRegistry registry, AIProfileStore profiles) => Current = new(registry, profiles);

    public (IAIProvider Provider, AIProviderContext Context) Resolve(string selectionKey)
    {
        var (profile, selection) = Profiles.GetDefault(selectionKey);
        return (Registry.Get(profile.ProviderKey).Provider, Profiles.CreateContext(profile, selection.ModelId));
    }

    public async ValueTask<IReadOnlyList<AIModelDescriptor>> GetModelsAsync(Guid profileId, AIModelQuery query, CancellationToken cancellationToken = default)
    {
        var profile = Profiles.GetProfile(profileId);
        var provider = Registry.Get(profile.ProviderKey).Provider;
        return await provider.GetModelsAsync(Profiles.CreateContext(profile, string.Empty), query, cancellationToken);
    }

    public async ValueTask<AIConfigurationValidationResult> ValidateAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var profile = Profiles.GetProfile(profileId);
        var provider = Registry.Get(profile.ProviderKey).Provider;
        return await provider.ValidateConfigurationAsync(Profiles.CreateContext(profile, string.Empty), cancellationToken);
    }

    public IAsyncEnumerable<AIChatEvent> StreamChatAsync(AIChatRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var (provider, context) = Resolve(AISelectionKeys.Chat);
            return provider is IAIChatProvider chat
                ? StreamWithTimeoutAsync(chat, context, request, cancellationToken)
                : AIProviderDefaults.ErrorStream(new(AIErrorCode.UnsupportedCapability, $"Provider '{context.ProviderKey}' does not support chat."), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogSafe("Resolve chat AI provider", ex);
            return AIProviderDefaults.ErrorStream(ToError(ex), cancellationToken);
        }
    }

    public async ValueTask<AIResult<AIGenerationResponse>> GenerateImageAsync(AIImageGenerationRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var (provider, context) = Resolve(AISelectionKeys.Image);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DefaultTimeout);
            var result = provider is IAIImageProvider image
                ? await image.GenerateImageAsync(context, request, timeout.Token)
                : AIResult<AIGenerationResponse>.FromError(new(AIErrorCode.UnsupportedCapability, $"Provider '{context.ProviderKey}' does not support image generation."));
            return TimedOut(result, timeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogSafe("Generate image with AI provider", ex);
            return AIResult<AIGenerationResponse>.FromError(ToError(ex));
        }
    }

    public async ValueTask<AIResult<AIGenerationResponse>> GenerateVideoAsync(AIVideoGenerationRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            string key = request.FirstFrame is null && request.LastFrame is null ? AISelectionKeys.TextToVideo : AISelectionKeys.FrameToVideo;
            var (provider, context) = Resolve(key);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DefaultTimeout);
            var result = provider is IAIVideoProvider video
                ? await video.GenerateVideoAsync(context, request, timeout.Token)
                : AIResult<AIGenerationResponse>.FromError(new(AIErrorCode.UnsupportedCapability, $"Provider '{context.ProviderKey}' does not support video generation."));
            return TimedOut(result, timeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogSafe("Generate video with AI provider", ex);
            return AIResult<AIGenerationResponse>.FromError(ToError(ex));
        }
    }

    public async ValueTask<string> MaterializeAssetAsync(AIGeneratedAsset asset, string? destinationDirectory = null, CancellationToken cancellationToken = default)
    {
        if (asset.Media.Kind == AIMediaReferenceKind.Uri && !string.IsNullOrWhiteSpace(asset.Media.Uri)) return asset.Media.Uri;
        if (asset.Media.Kind == AIMediaReferenceKind.File && !string.IsNullOrWhiteSpace(asset.Media.Locator)) return asset.Media.Locator;
        if (asset.Media.Data is not { } data) throw new InvalidDataException("The generated AI asset has no readable content.");
        destinationDirectory ??= FileSystem.CacheDirectory;
        Directory.CreateDirectory(destinationDirectory);
        string extension = asset.Media.MimeType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "video/mp4" => ".mp4",
            "audio/wav" => ".wav",
            _ => ".bin",
        };
        string path = Path.Combine(destinationDirectory, $"ai-{Guid.NewGuid():N}{extension}");
        await File.WriteAllBytesAsync(path, data, cancellationToken);
        return path;
    }

    public static AIProviderError ToError(Exception ex) => ex switch
    {
        OperationCanceledException => new(AIErrorCode.Cancelled, "The AI request was cancelled."),
        TimeoutException => new(AIErrorCode.Timeout, "The AI provider timed out.", true),
        UnauthorizedAccessException => new(AIErrorCode.AuthenticationFailed, "The AI provider rejected the configured credential."),
        HttpRequestException => new(AIErrorCode.ProviderUnavailable, "The AI provider is unavailable.", true),
        _ => new(AIErrorCode.Unknown, "The AI provider request failed."),
    };

    private static void LogSafe(string operation, Exception ex) =>
        Logger.Log($"{operation} failed with {ex.GetType().FullName}.", "error");

    private static AIResult<AIGenerationResponse> TimedOut(AIResult<AIGenerationResponse> result, CancellationTokenSource timeout, CancellationToken cancellationToken) =>
        timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested && result.Error?.Code == AIErrorCode.Cancelled
            ? AIResult<AIGenerationResponse>.FromError(new(AIErrorCode.Timeout, "The AI provider timed out.", true))
            : result;

    private static async IAsyncEnumerable<AIChatEvent> StreamWithTimeoutAsync(
        IAIChatProvider provider,
        AIProviderContext context,
        AIChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DefaultTimeout);
        await using var enumerator = provider.StreamChatAsync(context, request, timeout.Token).GetAsyncEnumerator(timeout.Token);
        while (true)
        {
            AIProviderError? error = null;
            bool hasNext = false;
            try
            {
                hasNext = await enumerator.MoveNextAsync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                error = new(AIErrorCode.Timeout, "The AI provider timed out.", true);
            }
            catch (Exception ex)
            {
                LogSafe("Stream chat with AI provider", ex);
                error = ToError(ex);
            }

            if (error is not null)
            {
                yield return new() { Kind = AIChatEventKind.Error, Error = error };
                yield break;
            }
            if (!hasNext) yield break;
            yield return enumerator.Current;
        }
    }
}
