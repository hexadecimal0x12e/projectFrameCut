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
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            contents.AddRange(update.Contents);
            finishReason ??= update.FinishReason;
            if (update.Contents.OfType<UsageContent>().LastOrDefault() is { } usageContent) usage = usageContent.Details;
        }
        return new ChatResponse(new ExtensionsMessage(ChatRole.Assistant, contents)) { FinishReason = finishReason, Usage = usage };
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
            if (item.Kind == AIChatEventKind.Error)
                throw new InvalidOperationException(item.Error?.Message ?? "The AI provider returned an error.");
            var update = new ChatResponseUpdate(ChatRole.Assistant, []);
            switch (item.Kind)
            {
                case AIChatEventKind.TextDelta:
                    update.Contents.Add(new TextContent(item.Text ?? string.Empty));
                    break;
                case AIChatEventKind.ReasoningDelta:
                    update.Contents.Add(new TextReasoningContent(item.Text ?? string.Empty));
                    break;
                case AIChatEventKind.ToolCall:
                    update.Contents.Add(new FunctionCallContent(item.ToolCallId ?? string.Empty, item.ToolName ?? string.Empty, DeserializeArguments(item.ArgumentsJson)));
                    break;
                case AIChatEventKind.Usage when item.Usage is { } usage:
                    update.Contents.Add(new UsageContent(new UsageDetails
                    {
                        InputTokenCount = usage.InputTokens,
                        OutputTokenCount = usage.OutputTokens,
                        TotalTokenCount = usage.InputTokens + usage.OutputTokens,
                    }));
                    break;
                case AIChatEventKind.Completed:
                    update.FinishReason = string.IsNullOrWhiteSpace(item.FinishReason) ? ChatFinishReason.Stop : new(item.FinishReason);
                    break;
            }
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
                case TextContent text:
                    contents.Add(new() { Kind = AIContentPartKind.Text, Text = text.Text });
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
