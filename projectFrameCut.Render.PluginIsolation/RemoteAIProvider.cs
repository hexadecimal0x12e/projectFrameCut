using projectFrameCut.AIContracts;
using projectFrameCut.Render.Contracts;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace projectFrameCut.Render.PluginIsolation;

internal sealed class RemoteAIProvider : IAIChatProvider, IAIImageProvider, IAIVideoProvider, IAIExtensionProvider
{
    private readonly IPluginIsolationSession session;
    private readonly string id;

    public RemoteAIProvider(IPluginIsolationSession session, IsolationAIProviderDescriptor descriptor)
    {
        this.session = session;
        id = descriptor.ProviderId;
        Descriptor = JsonSerializer.Deserialize<AIProviderDescriptor>(descriptor.DescriptorJson)
            ?? throw new InvalidDataException($"The isolated AI provider descriptor '{id}' is invalid.");
    }

    public AIProviderDescriptor Descriptor { get; }

    public async ValueTask<AIConfigurationValidationResult> ValidateConfigurationAsync(AIProviderContext context, CancellationToken cancellationToken = default)
    {
        var response = await session.InvokeAsync<IsolationAIInvokeRequest, IsolationAIJsonResponse>(RenderOperation.IsolationAIValidateConfiguration,
            await RequestAsync(context, string.Empty, string.Empty, cancellationToken), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<AIConfigurationValidationResult>(response.Json)
            ?? throw new InvalidDataException("The isolated AI configuration result is invalid.");
    }

    public async ValueTask<IReadOnlyList<AIModelDescriptor>> GetModelsAsync(AIProviderContext context, AIModelQuery query, CancellationToken cancellationToken = default)
    {
        var response = await session.InvokeAsync<IsolationAIInvokeRequest, IsolationAIJsonResponse>(RenderOperation.IsolationAIListModels,
            await RequestAsync(context, string.Empty, JsonSerializer.Serialize(query), cancellationToken), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<List<AIModelDescriptor>>(response.Json)
            ?? throw new InvalidDataException("The isolated AI model list is invalid.");
    }

    public async IAsyncEnumerable<AIChatEvent> StreamChatAsync(AIProviderContext context, AIChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var json in RunAsync(await RequestAsync(context, "chat", JsonSerializer.Serialize(request), cancellationToken), cancellationToken))
            yield return JsonSerializer.Deserialize<AIChatEvent>(json) ?? throw new InvalidDataException("The isolated AI chat event is invalid.");
    }

    public async ValueTask<AIResult<AIGenerationResponse>> GenerateImageAsync(AIProviderContext context, AIImageGenerationRequest request, CancellationToken cancellationToken = default) =>
        await ReadSingleAsync<AIGenerationResponse>(await RequestAsync(context, "image", JsonSerializer.Serialize(request), cancellationToken), cancellationToken);

    public async ValueTask<AIResult<AIGenerationResponse>> GenerateVideoAsync(AIProviderContext context, AIVideoGenerationRequest request, CancellationToken cancellationToken = default) =>
        await ReadSingleAsync<AIGenerationResponse>(await RequestAsync(context, "video", JsonSerializer.Serialize(request), cancellationToken), cancellationToken);

    public async ValueTask<AIResult<AIExtensionResponse>> InvokeAsync(AIProviderContext context, AIExtensionRequest request, CancellationToken cancellationToken = default) =>
        await ReadSingleAsync<AIExtensionResponse>(await RequestAsync(context, "extension", JsonSerializer.Serialize(request), cancellationToken), cancellationToken);

    private async ValueTask<IsolationAIInvokeRequest> RequestAsync(AIProviderContext context, string kind, string json, CancellationToken cancellationToken)
    {
        var secrets = new Dictionary<string, string>();
        foreach (var field in Descriptor.ConfigurationFields.Where(x => x.Type == AIConfigurationFieldType.Secret))
            if (await context.GetSecretAsync(field.Id, cancellationToken).ConfigureAwait(false) is { } secret) secrets[field.Id] = secret;
        var result = new IsolationAIInvokeRequest
        {
            ProviderId = id,
            OperationKind = kind,
            RequestJson = json,
            Context = new()
            {
                ProfileId = context.ProfileId.ToString("N"),
                ProviderKey = context.ProviderKey,
                ModelId = context.ModelId,
                Configuration = context.Configuration.ToDictionary(x => x.Key, x => x.Value),
                Secrets = secrets,
            },
        };
        if (!string.IsNullOrWhiteSpace(json)) result.RequestJson = await ExternalizeMediaAsync(json, result.Payloads, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<AIResult<T>> ReadSingleAsync<T>(IsolationAIInvokeRequest request, CancellationToken cancellationToken)
    {
        await foreach (var json in RunAsync(request, cancellationToken))
            return JsonSerializer.Deserialize<AIResult<T>>(json) ?? throw new InvalidDataException("The isolated AI result is invalid.");
        throw new EndOfStreamException("The isolated AI operation completed without a result.");
    }

    private async IAsyncEnumerable<string> RunAsync(IsolationAIInvokeRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IsolationAIOperationRequest? operation = null;
        CancellationTokenRegistration cancellation = default;
        try
        {
            var begin = await session.InvokeAsync<IsolationAIInvokeRequest, IsolationAIBeginResponse>(RenderOperation.IsolationAIBeginOperation, request, cancellationToken).ConfigureAwait(false);
            operation = new IsolationAIOperationRequest { OperationId = begin.OperationId };
            cancellation = cancellationToken.Register(() => _ = CancelAsync(operation));
            while (true)
            {
                var response = await session.InvokeAsync<IsolationAIOperationRequest, IsolationAIPollResponse>(RenderOperation.IsolationAIPollOperation, operation, cancellationToken).ConfigureAwait(false);
                foreach (var item in response.Events) yield return await HydrateMediaAsync(item, cancellationToken).ConfigureAwait(false);
                if (response.Completed) break;
            }
        }
        finally
        {
            cancellation.Dispose();
            if (operation is not null)
                try { await session.InvokeAsync<IsolationAIOperationRequest, EmptyResponse>(RenderOperation.IsolationAIReleaseOperation, operation, CancellationToken.None).ConfigureAwait(false); }
                catch { }
            foreach (var payload in request.Payloads)
                try { await session.Payloads.ReleaseAsync(payload).ConfigureAwait(false); } catch { }
        }
    }

    private async ValueTask<string> ExternalizeMediaAsync(string json, List<IsolationPayloadReference> payloads, CancellationToken cancellationToken)
    {
        var root = JsonNode.Parse(json) ?? throw new InvalidDataException("The AI request JSON is invalid.");
        await VisitAsync(root, async obj =>
        {
            IsolationPayloadLease? lease = null;
            if (obj["Kind"]?.GetValue<int>() == (int)AIMediaReferenceKind.Inline && obj["Data"] is JsonValue dataNode && dataNode.TryGetValue<string>(out var base64) && !string.IsNullOrEmpty(base64))
                lease = await session.Payloads.PublishAsync(Convert.FromBase64String(base64), session.PreferredPayloadKind, cancellationToken).ConfigureAwait(false);
            else if (obj["Kind"]?.GetValue<int>() == (int)AIMediaReferenceKind.File && obj["Locator"] is JsonValue locatorNode && locatorNode.TryGetValue<string>(out var path) && File.Exists(path))
                lease = await session.Resources.BrokerReadOnlyFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (lease is null) return;
            int index = payloads.Count;
            payloads.Add(lease.Reference);
            obj["Kind"] = (int)AIMediaReferenceKind.Transport;
            obj["Locator"] = index.ToString();
            obj["Data"] = null;
        }).ConfigureAwait(false);
        return root.ToJsonString();
    }

    private async ValueTask<string> HydrateMediaAsync(IsolationAIJsonResponse response, CancellationToken cancellationToken)
    {
        if (response.Payloads.Count == 0) return response.Json;
        var root = JsonNode.Parse(response.Json) ?? throw new InvalidDataException("The AI response JSON is invalid.");
        try
        {
            await VisitAsync(root, async obj =>
            {
                if (obj["Kind"]?.GetValue<int>() != (int)AIMediaReferenceKind.Transport ||
                    !int.TryParse(obj["Locator"]?.GetValue<string>(), out int index) || index < 0 || index >= response.Payloads.Count) return;
                await using var stream = await session.Payloads.OpenReadAsync(response.Payloads[index], cancellationToken).ConfigureAwait(false);
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
                obj["Kind"] = (int)AIMediaReferenceKind.Inline;
                obj["Data"] = Convert.ToBase64String(memory.ToArray());
                obj["Locator"] = null;
            }).ConfigureAwait(false);
            return root.ToJsonString();
        }
        finally
        {
            foreach (var payload in response.Payloads)
                try { await session.Payloads.ReleaseAsync(payload).ConfigureAwait(false); } catch { }
        }
    }

    private static async ValueTask VisitAsync(JsonNode node, Func<JsonObject, ValueTask> visitor)
    {
        if (node is JsonObject obj)
        {
            await visitor(obj).ConfigureAwait(false);
            foreach (var child in obj.ToArray()) if (child.Value is not null) await VisitAsync(child.Value, visitor).ConfigureAwait(false);
        }
        else if (node is JsonArray array)
            foreach (var child in array) if (child is not null) await VisitAsync(child, visitor).ConfigureAwait(false);
    }

    private async Task CancelAsync(IsolationAIOperationRequest operation)
    {
        try { await session.InvokeAsync<IsolationAIOperationRequest, EmptyResponse>(RenderOperation.IsolationAICancelOperation, operation, CancellationToken.None).ConfigureAwait(false); }
        catch { }
    }
}
