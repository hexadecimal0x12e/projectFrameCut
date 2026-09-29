using Microsoft.Extensions.AI;
using projectFrameCut.AIContracts;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ContractMessage = projectFrameCut.AIContracts.AIChatMessage;
using ExtensionsMessage = Microsoft.Extensions.AI.ChatMessage;

namespace projectFrameCut.AIAssistance;

public sealed class AIProviderChatClient(AIProviderService service) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ExtensionsMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var contents = new List<AIContent>();
        ChatFinishReason? finishReason = null;
        UsageDetails? usage = null;
        AIChatContentPart? done = null;
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            contents.AddRange(update.Contents);
            finishReason ??= update.FinishReason;
            if (update.Contents.OfType<UsageContent>().LastOrDefault() is { } usageContent) usage = usageContent.Details;
            if (update.RawRepresentation is AIChatContentPart { Kind: AIContentPartKind.Done } part) done = part;
        }
        return new ChatResponse(new ExtensionsMessage(ChatRole.Assistant, contents)) { FinishReason = finishReason, Usage = usage, RawRepresentation = done };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ExtensionsMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = new AIChatRequest
        {
            Messages = messages.Select(ToContractMessage).ToArray(),
            Tools = options?.Tools?.OfType<AIFunction>().Select(x => new AIToolDescriptor
            {
                Name = x.Name,
                Description = x.Description,
                ParametersJsonSchema = x.JsonSchema.GetRawText(),
            }).ToArray() ?? [],
            Temperature = options?.Temperature,
            MaximumOutputTokens = options?.MaxOutputTokens,
        };

        await foreach (var item in service.StreamChatAsync(request, cancellationToken))
        {
            var update = new ChatResponseUpdate(ChatRole.Assistant, []);
            switch (item.Kind)
            {
                case AIContentPartKind.Text:
                    update.Contents.Add(new TextContent(item.Text ?? string.Empty));
                    break;
                case AIContentPartKind.Media when item.Media?.Data is { } data:
                    update.Contents.Add(new DataContent(data, item.Media.MimeType) { Name = item.Media.Name });
                    break;
                case AIContentPartKind.Media when Uri.TryCreate(item.Media?.Uri, UriKind.Absolute, out var uri):
                    update.Contents.Add(new DataContent(uri, item.Media!.MimeType) { Name = item.Media.Name });
                    break;
                case AIContentPartKind.Thinking:
                    update.Contents.Add(new TextReasoningContent(item.Text ?? string.Empty));
                    break;
                case AIContentPartKind.ToolCall:
                    update.Contents.Add(new FunctionCallContent(item.ToolCallId ?? string.Empty, item.ToolName ?? string.Empty, DeserializeArguments(item.Json)));
                    break;
                case AIContentPartKind.ToolResult:
                    update.Contents.Add(new FunctionResultContent(item.ToolCallId ?? string.Empty, item.Json));
                    break;
                case AIContentPartKind.Done:
                    if (item.Done?.Usage is { } usage)
                    {
                        update.Contents.Add(new UsageContent(new UsageDetails
                        {
                            InputTokenCount = usage.InputTokens,
                            OutputTokenCount = usage.OutputTokens,
                            TotalTokenCount = usage.InputTokens + usage.OutputTokens,
                        }) { RawRepresentation = item });
                    }
                    else update.Contents.Add(new AIContent { RawRepresentation = item });
                    update.FinishReason = string.IsNullOrWhiteSpace(item.Done?.FinishReason) ? ChatFinishReason.Stop : new(item.Done.FinishReason);
                    break;
                case AIContentPartKind.Retrying:
                case AIContentPartKind.KeepAlive:
                case AIContentPartKind.Error:
                    update.Contents.Add(new AIContent { RawRepresentation = item });
                    break;
            }
            update.RawRepresentation = item;
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }

    private static ContractMessage ToContractMessage(ExtensionsMessage message)
    {
        var contents = new List<AIChatContentPart>();
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case AIContent { RawRepresentation: AIChatContentPart part }:
                    contents.Add(part);
                    break;
                case TextContent text:
                    contents.Add(new() { Kind = AIContentPartKind.Text, Text = text.Text });
                    break;
                case TextReasoningContent reasoning:
                    contents.Add(new() { Kind = AIContentPartKind.Thinking, Text = reasoning.Text });
                    break;
                case DataContent data when !data.Data.IsEmpty:
                    contents.Add(new() { Kind = AIContentPartKind.Media, Media = new() { Kind = AIMediaReferenceKind.Inline, Data = data.Data.ToArray(), MimeType = data.MediaType, Name = data.Name } });
                    break;
                case DataContent data:
                    contents.Add(new() { Kind = AIContentPartKind.Media, Media = new() { Kind = AIMediaReferenceKind.Uri, Uri = data.Uri, MimeType = data.MediaType, Name = data.Name } });
                    break;
                case FunctionCallContent call:
                    contents.Add(new() { Kind = AIContentPartKind.ToolCall, ToolCallId = call.CallId, ToolName = call.Name, Json = JsonSerializer.Serialize(call.Arguments) });
                    break;
                case FunctionResultContent result:
                    contents.Add(new() { Kind = AIContentPartKind.ToolResult, ToolCallId = result.CallId, Json = JsonSerializer.Serialize(result.Result) });
                    break;
            }
        }
        if (contents.Count == 0 && !string.IsNullOrEmpty(message.Text)) contents.Add(new() { Kind = AIContentPartKind.Text, Text = message.Text });
        return new(ToRole(message.Role), contents);
    }

    private static AIChatRole ToRole(ChatRole role) => role == ChatRole.System ? AIChatRole.System
        : role == ChatRole.Assistant ? AIChatRole.Assistant
        : role == ChatRole.Tool ? AIChatRole.Tool
        : AIChatRole.User;

    private static Dictionary<string, object?> DeserializeArguments(string? json) => string.IsNullOrWhiteSpace(json)
        ? []
        : JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? [];
}
