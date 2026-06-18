namespace projectFrameCut.AIAssistance;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;
using projectFrameCut.Setting.SettingManager;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using OpenAIChatClient = OpenAI.Chat.ChatClient;

public partial class AssistanceChatView : ContentView
{
    private const int SessionTitleMaxLength = 24;
    private readonly ObservableCollection<ChatMessageItem> _messages = [];
    private readonly List<AIChatMessage> _chatHistory = [];
    private readonly IChatClient? _chatClient;
    private readonly Guid _sessionId;
    private readonly string? _projectPath;
    private bool _isReplying;
    private CancellationTokenSource? _cts;
    private static readonly ILoggerFactory AILoggerFactory = LoggerFactory.Create(_ => { });

    public Func<IEnumerable<AIFunction>>? ToolCallFactories;

    public AssistanceChatView() : this(null, null, null)
    {
    }

    public AssistanceChatView(Guid? sessionId, Func<IEnumerable<AIFunction>>? aIFunctionsFactory = null, string? projectPath = null)
    {
        InitializeComponent();
        _projectPath = projectPath;
        ToolCallFactories = aIFunctionsFactory;
        AIChatHistoryView.ItemsSource = _messages;
        _messages.CollectionChanged += Messages_CollectionChanged;
        _chatClient = CreateChatClient();

        AssistanceChatSession session = AssistanceChatSessionStore.GetOrCreate(_projectPath, sessionId);
        _sessionId = session.SessionId;
        LoadSession(session);
        PersistSession();
    }

    private async void AISendButton_Clicked(object? sender, EventArgs e)
    {
        if (_isReplying)
        {
            _cts?.Cancel();
            return;
        }

        await SendMessageAsync();
    }

    private async void AIInputButton_Completed(object? sender, EventArgs e)
    {
        await SendMessageAsync();
    }

    private void AIClearContextButton_Clicked(object? sender, EventArgs e)
    {
        if (_isReplying)
        {
            return;
        }

        _chatHistory.Clear();
        _messages.Clear();
        AddAssistantWelcomeMessage();
        PersistSession();
    }

    private void AINewChatPageButton_Clicked(object? sender, EventArgs e)
    {
        if (_isReplying)
        {
            return;
        }

        if (GetHostWindow() is MultiWindowItem host)
        {
            host.NavigateTo(new AssistanceChatSessionsView(_projectPath));
        }
    }

    private void AddAssistantWelcomeMessage()
    {
        _messages.Add(new ChatMessageItem
        {
            Sender = "Assistant P",
            Message = _chatClient is null
                ? Localized.AIAssistant_ChatView_MissingConfig
                : Localized.AIAssistant_ChatView_WelcomeText,
            IsUser = false,
        });
    }

    private async Task SendMessageAsync()
    {
        if (_isReplying)
        {
            return;
        }

        string input = AIInputButton.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(input))
        {
            return;
        }

        if (!_chatHistory.Any())
        {
            using var fs = await FileSystem.OpenAppPackageFileAsync("AIAgent\\system.md");
            using var sr = new StreamReader(fs);
            var str = await sr.ReadToEndAsync();
            str = str.Replace("!AppBrand!", Localized.AppBrand);
            str = str.Replace("!AgentName!", "Assistant P");
            str = str.Replace("!LocateID!", Localized._LocaleId_);
            str = str.Replace("!AppVersion!", Assembly.GetExecutingAssembly()?.GetName()?.Version?.ToString() ?? "1.0.0.0");
            str = str.Replace("!ApproximateLocation!", RegionInfo.CurrentRegion.DisplayName);
            str = str.Replace("!UserName!", SettingsManager.GetSetting("UserName", Environment.UserName));
            str = str.Replace("!DeviceIdiom!", DeviceInfo.Idiom.ToString());


            _chatHistory.Add(new AIChatMessage(ChatRole.System, str));
        }

        _messages.Add(new ChatMessageItem
        {
            Sender = Localized.AIAssistant_ChatView_Me,
            Message = input,
            IsUser = true,
        });
        _chatHistory.Add(new AIChatMessage(ChatRole.User, input));

        AIInputButton.Text = string.Empty;
        _isReplying = true;
        AISendButton.Text = Localized.AIAssistant_ChatView_Stop;
        _cts = new CancellationTokenSource();
        //AIClearContextButton.IsEnabled = false;
        //AINewChatPageButton.IsEnabled = false;

        string assistantText;
        ChatMessageItem? streamingItem = null;
        StringBuilder textBuilder = new();
        StringBuilder reasoningBuilder = new();
        try
        {
            if (_chatClient is null)
            {
                assistantText = Localized.AIAssistant_ChatView_MissingConfig;
            }
            else
            {
                streamingItem = new ChatMessageItem
                {
                    Sender = "Assistant P",
                    Message = "",
                    IsUser = false,
                };
                _messages.Add(streamingItem);


                Dictionary<string, ToolCallDisplayState> toolCallsById = new(StringComparer.Ordinal);
                int anonymousToolCallCounter = 0;
                await foreach (ChatResponseUpdate update in _chatClient.GetStreamingResponseAsync(_chatHistory, new ChatOptions { Tools = BuildTool() }, _cts.Token))
                {
                    LogDiagnostic($"Chunk: {JsonSerializer.Serialize(update)}");
                    string textChunk = !string.IsNullOrEmpty(update.Text)
                        ? update.Text
                        : ExtractTextFromContents(update);
                    if (string.IsNullOrEmpty(textChunk))
                    {
                        textChunk = ExtractContentChunk(update);
                    }
                    if (!string.IsNullOrEmpty(textChunk))
                    {
                        textBuilder.Append(textChunk);
                        SetMessageText(streamingItem, textBuilder.ToString());
                    }

                    string reasoningChunk = ExtractReasoningChunk(update);
                    if (!string.IsNullOrEmpty(reasoningChunk))
                    {
                        reasoningBuilder.Append(reasoningChunk);
                        SetReasoningText(streamingItem, reasoningBuilder.ToString());
                    }

                    if (TryUpdateToolCallState(update, toolCallsById, ref anonymousToolCallCounter, out string toolCallsText))
                    {
                        SetToolCallsText(streamingItem, toolCallsText);
                    }

                }

                assistantText = textBuilder.Length == 0 ? Localized.AIAssistant_ChatView_ChatFail_NoContent : textBuilder.ToString().Trim();
                SetMessageText(streamingItem, assistantText);
            }
        }
        catch (OperationCanceledException)
        {
            assistantText = $"{textBuilder?.ToString()?.Trim()}{Environment.NewLine}{Localized.AIAssistant_ChatView_ChatFail_Cancelled}";
            if (streamingItem is not null)
            {
                SetMessageText(streamingItem, assistantText);
            }
        }
        catch (Exception ex)
        {
            Log(ex, $"Finish request '{input}'", this);
            assistantText = $"{textBuilder?.ToString()?.Trim()}{Environment.NewLine}{Environment.NewLine}---{Environment.NewLine}{Localized.AIAssistant_ChatView_ChatFail_Exception(ex)}";
            if (streamingItem is not null)
            {
                SetMessageText(streamingItem, assistantText);
            }
        }

        if (streamingItem is null)
        {
            _messages.Add(new ChatMessageItem
            {
                Sender = "Assistant P",
                Message = assistantText,
                IsUser = false,
            });
        }
        _chatHistory.Add(new AIChatMessage(ChatRole.Assistant, assistantText));
        PersistSession();

        _isReplying = false;
        AISendButton.Text = Localized.AIAssistant_ChatView_Send;
        AISendButton.IsEnabled = true;
        _cts?.Dispose();
        _cts = null;
        //AIClearContextButton.IsEnabled = true;
        //AINewChatPageButton.IsEnabled = true;
        AIInputButton.Focus();
    }

    private async void AIReplyFeedbackGoodButton_Clicked(object? sender, EventArgs e)
    {
        ChatMessageItem? message = GetFeedbackTarget(sender);
        if (message is null)
        {
            return;
        }

        await SubmitFeedbackMockAsync(message, ChatReplyFeedbackType.Good);
    }

    private async void AIReplyFeedbackBadButton_Clicked(object? sender, EventArgs e)
    {
        ChatMessageItem? message = GetFeedbackTarget(sender);
        if (message is null)
        {
            return;
        }

        await SubmitFeedbackMockAsync(message, ChatReplyFeedbackType.Bad);
    }

    private async void AIReplyFeedbackReportButton_Clicked(object? sender, EventArgs e)
    {
        ChatMessageItem? message = GetFeedbackTarget(sender);
        if (message is null)
        {
            return;
        }

        string harmful = Localized.AIAssistant_ChatView_Feedback_ReportReason_Harmful;
        string hate = Localized.AIAssistant_ChatView_Feedback_ReportReason_Hate;
        string incorrect = Localized.AIAssistant_ChatView_Feedback_ReportReason_Incorrect;
        string irrelevant = Localized.AIAssistant_ChatView_Feedback_ReportReason_Irrelevant;
        string other = Localized.AIAssistant_ChatView_Feedback_ReportReason_Other;
        string selectedReason;
        if (GetHostWindow() is MultiWindowItem host)
        {
            selectedReason = await host.DisplayActionSheetAsync(
                Localized.AIAssistant_ChatView_Feedback_ReportReason_Title,
                Localized._Cancel,
                null,
                harmful,
                hate,
                incorrect,
                irrelevant,
                other);
        }
        else if (Application.Current?.Windows?[0]?.Page is Page page)
        {
            selectedReason = await page.DisplayActionSheetAsync(
                Localized.AIAssistant_ChatView_Feedback_ReportReason_Title,
                Localized._Cancel,
                null,
                harmful,
                hate,
                incorrect,
                irrelevant,
                other);
        }
        else
        {
            LogDiagnostic("Skip feedback report: no valid dialog host page.");
            return;
        }

        if (string.IsNullOrWhiteSpace(selectedReason) || selectedReason == Localized._Cancel)
        {
            return;
        }

        string reasonCode = selectedReason switch
        {
            var x when x == harmful => "harmful_or_dangerous",
            var x when x == hate => "hateful_or_harassing",
            var x when x == incorrect => "factually_incorrect",
            var x when x == irrelevant => "irrelevant",
            _ => "other",
        };

        await SubmitFeedbackMockAsync(message, ChatReplyFeedbackType.Report, reasonCode, selectedReason);
        if (GetHostWindow() is MultiWindowItem hostWindow)
        {
            await hostWindow.DisplayAlertAsync(Localized._Done, Localized.AIAssistant_ChatView_Feedback_SubmitDone, Localized._OK);
        }
        else if (Application.Current?.Windows?[0]?.Page is Page rootPage)
        {
            await rootPage.DisplayAlertAsync(Localized._Done, Localized.AIAssistant_ChatView_Feedback_SubmitDone, Localized._OK);
        }
    }

    private static ChatMessageItem? GetFeedbackTarget(object? sender)
    {
        ChatMessageItem? message = (sender as BindableObject)?.BindingContext as ChatMessageItem;
        if (message is null)
        {
            return null;
        }

        return message.CanSubmitFeedback ? message : null;
    }

    private async Task SubmitFeedbackMockAsync(ChatMessageItem message, ChatReplyFeedbackType feedbackType, string reasonCode = "", string reasonText = "")
    {
        message.IsSubmittingFeedback = true;
        try
        {
            ChatReplyFeedbackPayload payload = new()
            {
                SessionId = _sessionId,
                Sender = message.Sender,
                Message = message.Message,
                FeedbackType = feedbackType,
                ReasonCode = reasonCode,
                ReasonText = reasonText,
                CreatedAt = DateTimeOffset.Now,
            };

            await Task.Delay(5000);
            LogDiagnostic($"Mock feedback submitted: {JsonSerializer.Serialize(payload)}");
            message.HasFeedbackSubmitted = true;
            PersistSession();
        }
        finally
        {
            message.IsSubmittingFeedback = false;
        }
    }

    private void LoadSession(AssistanceChatSession session)
    {
        _messages.Clear();
        _chatHistory.Clear();

        foreach (AssistanceChatMessageSnapshot message in session.Messages)
        {
            _messages.Add(new ChatMessageItem
            {
                Sender = message.Sender,
                Message = message.Message,
                IsUser = message.IsUser,
                ReasoningText = message.ReasoningText,
                ToolCallsText = message.ToolCallsText,
                HasFeedbackSubmitted = message.HasFeedbackSubmitted,
            });
        }

        foreach (AssistanceChatHistorySnapshot history in session.History)
        {
            _chatHistory.Add(new AIChatMessage(history.Role, history.Text));
        }

        if (_messages.Count == 0)
        {
            AddAssistantWelcomeMessage();
        }
    }

    private void PersistSession()
    {
        string title = BuildSessionTitle();
        var messages = _messages.Select(x => new AssistanceChatMessageSnapshot
        {
            Sender = x.Sender,
            Message = x.Message,
            IsUser = x.IsUser,
            ReasoningText = x.ReasoningText,
            ToolCallsText = x.ToolCallsText,
            HasFeedbackSubmitted = x.HasFeedbackSubmitted,
        }).ToList();

        var history = _chatHistory.Select(x => new AssistanceChatHistorySnapshot
        {
            Role = x.Role,
            Text = x.Text ?? string.Empty,
        }).ToList();

        AssistanceChatSessionStore.UpdateSession(_projectPath, _sessionId, title, messages, history);
    }

    private string BuildSessionTitle()
    {
        string? firstUserMessage = _messages
            .FirstOrDefault(x => x.IsUser && !string.IsNullOrWhiteSpace(x.Message))
            ?.Message
            .Trim();

        if (string.IsNullOrWhiteSpace(firstUserMessage))
        {
            return Localized.AIAssistant_ChatView_NewSession;
        }

        if (firstUserMessage.Length <= SessionTitleMaxLength)
        {
            return firstUserMessage;
        }

        return firstUserMessage[..SessionTitleMaxLength] + "…";
    }

    private MultiWindowItem? GetHostWindow()
    {
        Element? current = this;
        while (current is not null)
        {
            if (current is MultiWindowItem window)
            {
                return window;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string ExtractReasoningChunk(ChatResponseUpdate update)
    {
        string fromPayload = ExtractFieldFromPayload(update, "reasoning_content");
        if (!string.IsNullOrWhiteSpace(fromPayload))
        {
            return fromPayload;
        }

        if (update.AdditionalProperties is not null && TryFindReasoningText(update.AdditionalProperties, out string fromAdditional))
        {
            return fromAdditional;
        }

        if (update.RawRepresentation is not null && TryFindReasoningText(update.RawRepresentation, out string fromRaw))
        {
            return fromRaw;
        }

        if (update.Contents is not null)
        {
            foreach (AIContent content in update.Contents)
            {
                if (content.AdditionalProperties is not null && TryFindReasoningText(content.AdditionalProperties, out string fromContent))
                {
                    return fromContent;
                }
            }
        }

        return string.Empty;
    }

    private static string ExtractContentChunk(ChatResponseUpdate update)
    {
        string fromPayload = ExtractFieldFromPayload(update, "content");
        return string.IsNullOrWhiteSpace(fromPayload) ? string.Empty : fromPayload;
    }

    private static bool TryUpdateToolCallState(ChatResponseUpdate update, IDictionary<string, ToolCallDisplayState> toolCallsById, ref int anonymousToolCallCounter, out string displayText)
    {
        displayText = string.Empty;
        bool changed = false;

        foreach (ToolCallFragment fragment in ExtractToolCallFragments(update))
        {
            if (ApplyToolCallFragment(toolCallsById, fragment, ref anonymousToolCallCounter))
            {
                changed = true;
            }
        }

        if (!changed)
        {
            return false;
        }

        displayText = BuildToolCallDisplayText(toolCallsById.Values);
        return true;
    }

    private static IEnumerable<ToolCallFragment> ExtractToolCallFragments(ChatResponseUpdate update)
    {
        List<ToolCallFragment> fragments = [];

        if (update.Contents is not null)
        {
            foreach (AIContent content in update.Contents)
            {
                if (TryExtractToolCallFromObject(content, out ToolCallFragment contentFragment))
                {
                    fragments.Add(contentFragment);
                }

                if (content.AdditionalProperties is not null)
                {
                    fragments.AddRange(ExtractToolCallFragmentsFromPayload(content.AdditionalProperties));
                }
            }
        }

        fragments.AddRange(ExtractToolCallFragmentsFromPayload(update.AdditionalProperties));
        fragments.AddRange(ExtractToolCallFragmentsFromPayload(update.RawRepresentation));

        return fragments;
    }

    private static IEnumerable<ToolCallFragment> ExtractToolCallFragmentsFromPayload(object? source)
    {
        if (source is null)
        {
            return [];
        }

        try
        {
            if (source is JsonElement element)
            {
                return ExtractToolCallFragmentsFromJsonElement(element);
            }

            if (source is JsonDocument document)
            {
                return ExtractToolCallFragmentsFromJsonElement(document.RootElement);
            }

            if (source is string text)
            {
                string trimmed = text.TrimStart();
                if (!trimmed.StartsWith("{") && !trimmed.StartsWith("["))
                {
                    return [];
                }

                using JsonDocument parsed = JsonDocument.Parse(text);
                return ExtractToolCallFragmentsFromJsonElement(parsed.RootElement);
            }

            string serialized = JsonSerializer.Serialize(source);
            using JsonDocument json = JsonDocument.Parse(serialized);
            return ExtractToolCallFragmentsFromJsonElement(json.RootElement);
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<ToolCallFragment> ExtractToolCallFragmentsFromJsonElement(JsonElement root)
    {
        List<ToolCallFragment> result = [];
        CollectToolCallsFromJsonElement(root, result, 0);
        return result;
    }

    private static void CollectToolCallsFromJsonElement(JsonElement element, ICollection<ToolCallFragment> output, int depth)
    {
        if (depth > 8)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("tool_calls", out JsonElement toolCallsElement)
                    && toolCallsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement toolCall in toolCallsElement.EnumerateArray())
                    {
                        if (TryCreateToolCallFragment(toolCall, out ToolCallFragment fragment))
                        {
                            output.Add(fragment);
                        }
                    }
                }

                if (element.TryGetProperty("function_call", out JsonElement functionCall)
                    && TryCreateToolCallFragment(functionCall, out ToolCallFragment functionFragment))
                {
                    output.Add(functionFragment);
                }

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    CollectToolCallsFromJsonElement(property.Value, output, depth + 1);
                }
                break;

            case JsonValueKind.Array:
                foreach (JsonElement child in element.EnumerateArray())
                {
                    CollectToolCallsFromJsonElement(child, output, depth + 1);
                }
                break;
        }
    }

    private static bool TryCreateToolCallFragment(JsonElement toolCallElement, out ToolCallFragment fragment)
    {
        fragment = default;
        if (toolCallElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string callId = ReadJsonString(toolCallElement, "id", "call_id", "tool_call_id");
        string name = ReadJsonString(toolCallElement, "name", "function_name");
        string arguments = ReadJsonStringOrRawJson(toolCallElement, "arguments", "input");

        if (toolCallElement.TryGetProperty("index", out JsonElement indexElement)
            && indexElement.ValueKind == JsonValueKind.Number
            && indexElement.TryGetInt32(out int index)
            && string.IsNullOrWhiteSpace(callId))
        {
            callId = $"index-{index}";
        }

        if (toolCallElement.TryGetProperty("function", out JsonElement functionElement)
            && functionElement.ValueKind == JsonValueKind.Object)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                name = ReadJsonString(functionElement, "name", "function_name");
            }

            if (string.IsNullOrWhiteSpace(arguments))
            {
                arguments = ReadJsonStringOrRawJson(functionElement, "arguments", "input");
            }
        }

        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        fragment = new ToolCallFragment
        {
            CallId = callId,
            Name = name,
            Arguments = arguments,
        };
        return true;
    }

    private static bool TryExtractToolCallFromObject(object source, out ToolCallFragment fragment)
    {
        fragment = default;
        Type type = source.GetType();
        string typeName = type.Name;
        if (!typeName.Contains("FunctionCall", StringComparison.OrdinalIgnoreCase)
            && !typeName.Contains("ToolCall", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string callId = GetStringProperty(source, "CallId", "Id", "ToolCallId");
        string name = GetStringProperty(source, "Name", "FunctionName");

        object? argsObject = GetPropertyValue(source, "Arguments")
            ?? GetPropertyValue(source, "ArgumentsJson")
            ?? GetPropertyValue(source, "Input")
            ?? GetPropertyValue(source, "Value");

        object? functionObject = GetPropertyValue(source, "Function");
        if (functionObject is not null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                name = GetStringProperty(functionObject, "Name", "FunctionName");
            }

            argsObject ??= GetPropertyValue(functionObject, "Arguments")
                ?? GetPropertyValue(functionObject, "ArgumentsJson")
                ?? GetPropertyValue(functionObject, "Input")
                ?? GetPropertyValue(functionObject, "Value");
        }

        string arguments = ConvertObjectToDisplayText(argsObject);
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        fragment = new ToolCallFragment
        {
            CallId = callId,
            Name = name,
            Arguments = arguments,
        };
        return true;
    }

    private static bool ApplyToolCallFragment(IDictionary<string, ToolCallDisplayState> toolCallsById, ToolCallFragment fragment, ref int anonymousToolCallCounter)
    {
        string key = fragment.CallId;
        if (string.IsNullOrWhiteSpace(key))
        {
            anonymousToolCallCounter++;
            key = $"anonymous-{anonymousToolCallCounter}";
        }

        if (!toolCallsById.TryGetValue(key, out ToolCallDisplayState? state))
        {
            state = new ToolCallDisplayState
            {
                CallId = fragment.CallId,
                Order = toolCallsById.Count + 1,
            };
            toolCallsById[key] = state;
        }

        bool changed = false;
        if (!string.IsNullOrWhiteSpace(fragment.Name) && !string.Equals(state.Name, fragment.Name, StringComparison.Ordinal))
        {
            state.Name = fragment.Name;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(fragment.Arguments))
        {
            string merged = MergeToolCallArguments(state.Arguments, fragment.Arguments);
            if (!string.Equals(state.Arguments, merged, StringComparison.Ordinal))
            {
                state.Arguments = merged;
                changed = true;
            }
        }

        return changed;
    }

    private static string MergeToolCallArguments(string existing, string incoming)
    {
        if (string.IsNullOrWhiteSpace(incoming))
        {
            return existing;
        }

        if (string.IsNullOrWhiteSpace(existing))
        {
            return incoming;
        }

        if (string.Equals(existing, incoming, StringComparison.Ordinal) || existing.EndsWith(incoming, StringComparison.Ordinal))
        {
            return existing;
        }

        if (incoming.StartsWith(existing, StringComparison.Ordinal))
        {
            return incoming;
        }

        return existing + incoming;
    }

    private static string BuildToolCallDisplayText(IEnumerable<ToolCallDisplayState> states)
    {
        StringBuilder builder = new();
        foreach (ToolCallDisplayState state in states.OrderBy(x => x.Order))
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine();
            }

            string name = string.IsNullOrWhiteSpace(state.Name) ? "(unknown_tool)" : state.Name;
            builder.Append($"#{state.Order} {name}");
            if (!string.IsNullOrWhiteSpace(state.CallId))
            {
                builder.Append($"  [{state.CallId}]");
            }

            if (!string.IsNullOrWhiteSpace(state.Arguments))
            {
                builder.AppendLine();
                builder.Append(state.Arguments.Trim());
            }
        }

        return builder.ToString();
    }

    private static string ReadJsonString(JsonElement obj, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (obj.TryGetProperty(propertyName, out JsonElement value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static string ReadJsonStringOrRawJson(JsonElement obj, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (!obj.TryGetProperty(propertyName, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }

            if (value.ValueKind != JsonValueKind.Null && value.ValueKind != JsonValueKind.Undefined)
            {
                return value.GetRawText();
            }
        }

        return string.Empty;
    }

    private static object? GetPropertyValue(object source, string propertyName)
    {
        PropertyInfo? property = source.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (property is null || !property.CanRead)
        {
            return null;
        }

        try
        {
            return property.GetValue(source);
        }
        catch
        {
            return null;
        }
    }

    private static string GetStringProperty(object source, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            object? value = GetPropertyValue(source, propertyName);
            string text = ConvertObjectToDisplayText(value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return string.Empty;
    }

    private static string ConvertObjectToDisplayText(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is string text)
        {
            return text;
        }

        if (value is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.String
                ? element.GetString() ?? string.Empty
                : element.GetRawText();
        }

        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch
        {
            return value.ToString() ?? string.Empty;
        }
    }

    private static string ExtractTextFromContents(ChatResponseUpdate update)
    {
        if (update.Contents is null || update.Contents.Count == 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        foreach (AIContent content in update.Contents)
        {
            if (content is TextContent textContent && !string.IsNullOrWhiteSpace(textContent.Text))
            {
                builder.Append(textContent.Text);
            }
        }

        return builder.ToString();
    }

    private static string ExtractFieldFromPayload(ChatResponseUpdate update, string fieldName)
    {
        if (TryExtractFieldFromKnownCompatibleShape(update.RawRepresentation, fieldName, out string value))
        {
            return value;
        }

        if (update.AdditionalProperties is not null && TryExtractFieldFromKnownCompatibleShape(update.AdditionalProperties, fieldName, out value))
        {
            return value;
        }

        return string.Empty;
    }

    private static bool TryExtractFieldFromKnownCompatibleShape(object? source, string fieldName, out string value)
    {
        value = string.Empty;
        if (source is null)
        {
            return false;
        }

        if (source is JsonElement element)
        {
            return TryExtractFieldFromJsonElement(element, fieldName, out value);
        }

        if (source is JsonDocument document)
        {
            return TryExtractFieldFromJsonElement(document.RootElement, fieldName, out value);
        }

        if (source is IDictionary<string, object?> dict)
        {
            if (dict.TryGetValue(fieldName, out object? direct) && direct is string directText && !string.IsNullOrWhiteSpace(directText))
            {
                value = directText;
                return true;
            }

            if (dict.TryGetValue("choices", out object? choicesObj) && TryExtractFieldFromKnownCompatibleShape(choicesObj, fieldName, out value))
            {
                return true;
            }

            if (dict.TryGetValue("delta", out object? deltaObj) && TryExtractFieldFromKnownCompatibleShape(deltaObj, fieldName, out value))
            {
                return true;
            }
        }

        if (source is IReadOnlyDictionary<string, object?> readOnlyDict)
        {
            if (readOnlyDict.TryGetValue(fieldName, out object? direct) && direct is string directText && !string.IsNullOrWhiteSpace(directText))
            {
                value = directText;
                return true;
            }

            if (readOnlyDict.TryGetValue("choices", out object? choicesObj) && TryExtractFieldFromKnownCompatibleShape(choicesObj, fieldName, out value))
            {
                return true;
            }

            if (readOnlyDict.TryGetValue("delta", out object? deltaObj) && TryExtractFieldFromKnownCompatibleShape(deltaObj, fieldName, out value))
            {
                return true;
            }
        }

        if (source is string text)
        {
            string trimmed = text.TrimStart();
            if ((trimmed.StartsWith("{") || trimmed.StartsWith("[")) && TryExtractFieldFromJsonString(trimmed, fieldName, out value))
            {
                return true;
            }
        }

        string rawString = source.ToString() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(rawString))
        {
            string trimmed = rawString.TrimStart();
            if ((trimmed.StartsWith("{") || trimmed.StartsWith("[")) && TryExtractFieldFromJsonString(trimmed, fieldName, out value))
            {
                return true;
            }
        }

        if (source is System.Collections.IEnumerable enumerable and not string)
        {
            foreach (object? item in enumerable)
            {
                if (TryExtractFieldFromKnownCompatibleShape(item, fieldName, out value))
                {
                    return true;
                }
            }
        }

        try
        {
            string json = JsonSerializer.Serialize(source);
            if (TryExtractFieldFromJsonString(json, fieldName, out value))
            {
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool TryExtractFieldFromJsonString(string json, string fieldName, out string value)
    {
        value = string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return TryExtractFieldFromJsonElement(document.RootElement, fieldName, out value);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractFieldFromJsonElement(JsonElement element, string fieldName, out string value)
    {
        value = string.Empty;
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(fieldName, out JsonElement direct) && direct.ValueKind == JsonValueKind.String)
            {
                string? s = direct.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    value = s;
                    return true;
                }
            }

            if (element.TryGetProperty("delta", out JsonElement delta)
                && TryExtractFieldFromJsonElement(delta, fieldName, out value))
            {
                return true;
            }

            if (element.TryGetProperty("choices", out JsonElement choices)
                && TryExtractFieldFromJsonElement(choices, fieldName, out value))
            {
                return true;
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
            {
                if (TryExtractFieldFromJsonElement(child, fieldName, out value))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryFindReasoningText(object source, out string text)
    {
        return TryFindReasoningText(source, 0, out text);
    }

    private static bool TryFindReasoningText(object? source, int depth, out string text)
    {
        text = string.Empty;
        if (source is null || depth > 5)
        {
            return false;
        }

        if (source is JsonElement element)
        {
            if (TryFindReasoningInJsonElement(element, depth + 1, out text))
            {
                return true;
            }
        }

        if (source is IReadOnlyDictionary<string, object?> readOnlyDict)
        {
            if (TryFindReasoningInDictionary(readOnlyDict, depth + 1, out text))
            {
                return true;
            }
        }

        if (source is IDictionary<string, object?> dict)
        {
            if (TryFindReasoningInDictionary(dict, depth + 1, out text))
            {
                return true;
            }
        }

        if (source is System.Collections.IEnumerable enumerable and not string)
        {
            foreach (object? item in enumerable)
            {
                if (TryFindReasoningText(item, depth + 1, out text))
                {
                    return true;
                }
            }
        }

        foreach (PropertyInfo property in source.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0 || !property.CanRead)
            {
                continue;
            }

            object? value;
            try
            {
                value = property.GetValue(source);
            }
            catch
            {
                continue;
            }

            if (IsReasoningKey(property.Name) && value is string reasoning && !string.IsNullOrWhiteSpace(reasoning))
            {
                text = reasoning;
                return true;
            }

            if (TryFindReasoningText(value, depth + 1, out text))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryFindReasoningInDictionary(IEnumerable<KeyValuePair<string, object?>> dictionary, int depth, out string text)
    {
        text = string.Empty;
        foreach (KeyValuePair<string, object?> item in dictionary)
        {
            if (IsReasoningKey(item.Key) && TryExtractReasoningPayload(item.Value, out string payload))
            {
                text = payload;
                return true;
            }

            if (IsLikelyReasoningContainerKey(item.Key) && TryFindReasoningText(item.Value, depth + 1, out text))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryFindReasoningInJsonElement(JsonElement element, int depth, out string text)
    {
        text = string.Empty;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (IsReasoningKey(property.Name) && TryExtractReasoningPayload(property.Value, out string payload))
                    {
                        text = payload;
                        return true;
                    }

                    if (IsLikelyReasoningContainerKey(property.Name)
                        && TryFindReasoningInJsonElement(property.Value, depth + 1, out text))
                    {
                        return true;
                    }
                }
                break;

            case JsonValueKind.Array:
                foreach (JsonElement child in element.EnumerateArray())
                {
                    if (TryFindReasoningInJsonElement(child, depth + 1, out text))
                    {
                        return true;
                    }
                }
                break;

            case JsonValueKind.String:
                break;
        }

        return false;
    }

    private static bool TryExtractReasoningPayload(object? value, out string text)
    {
        text = string.Empty;
        if (value is null)
        {
            return false;
        }

        if (value is string s)
        {
            text = s.Trim();
            return !string.IsNullOrWhiteSpace(text);
        }

        if (value is JsonElement element)
        {
            return TryExtractReasoningPayloadFromJson(element, out text);
        }

        if (value is IReadOnlyDictionary<string, object?> readOnlyDict)
        {
            foreach (var pair in readOnlyDict)
            {
                if ((IsLikelyReasoningTextKey(pair.Key) || IsReasoningKey(pair.Key))
                    && TryExtractReasoningPayload(pair.Value, out text))
                {
                    return true;
                }
            }

            return false;
        }

        if (value is IDictionary<string, object?> dict)
        {
            foreach (var pair in dict)
            {
                if ((IsLikelyReasoningTextKey(pair.Key) || IsReasoningKey(pair.Key))
                    && TryExtractReasoningPayload(pair.Value, out text))
                {
                    return true;
                }
            }

            return false;
        }

        return false;
    }

    private static bool TryExtractReasoningPayloadFromJson(JsonElement element, out string text)
    {
        text = string.Empty;
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                text = (element.GetString() ?? string.Empty).Trim();
                return !string.IsNullOrWhiteSpace(text);

            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if ((IsLikelyReasoningTextKey(property.Name) || IsReasoningKey(property.Name))
                        && TryExtractReasoningPayloadFromJson(property.Value, out text))
                    {
                        return true;
                    }
                }
                return false;

            case JsonValueKind.Array:
                foreach (JsonElement child in element.EnumerateArray())
                {
                    if (TryExtractReasoningPayloadFromJson(child, out text))
                    {
                        return true;
                    }
                }
                return false;

            default:
                return false;
        }
    }

    private static bool IsLikelyReasoningTextKey(string key)
    {
        return key.Equals("text", StringComparison.OrdinalIgnoreCase)
            || key.Equals("content", StringComparison.OrdinalIgnoreCase)
            || key.Equals("value", StringComparison.OrdinalIgnoreCase)
            || key.Equals("summary", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyReasoningContainerKey(string key)
    {
        return IsReasoningKey(key)
            || key.Equals("additionalProperties", StringComparison.OrdinalIgnoreCase)
            || key.Equals("rawRepresentation", StringComparison.OrdinalIgnoreCase)
            || key.Equals("delta", StringComparison.OrdinalIgnoreCase)
            || key.Equals("message", StringComparison.OrdinalIgnoreCase)
            || key.Equals("choices", StringComparison.OrdinalIgnoreCase)
            || key.Equals("output", StringComparison.OrdinalIgnoreCase)
            || key.Equals("response", StringComparison.OrdinalIgnoreCase)
            || key.Equals("data", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReasoningKey(string key)
    {
        return key.Equals("reasoning", StringComparison.OrdinalIgnoreCase)
            || key.Equals("reasoning_content", StringComparison.OrdinalIgnoreCase)
            || key.Equals("reasoningContent", StringComparison.OrdinalIgnoreCase)
            || key.Equals("thinking", StringComparison.OrdinalIgnoreCase)
            || key.Equals("thought", StringComparison.OrdinalIgnoreCase);
    }

    private IList<AITool> BuildTool()
    {
        List<AITool> tools =
        [
            AIFunctionFactory.Create(() => DateTime.Now.ToString("G"), "get_datetime", "Get current date and time."),
            AIFunctionFactory.Create((string title, string cancel, string[] verbs) => (Parent as MultiWindowItem)?.DisplayActionSheetAsync(title, cancel, null, verbs) , "display_actionsheet", "Display a ActionSheet to ask user to pick from many specified items. User's input text will be presented in the result, Null or blank result means user canceled this dialogue."),
            AIFunctionFactory.Create((string title, string message, string True, string False) => (Parent as MultiWindowItem)?.DisplayAlertAsync(title, message, True, False) , "display_dialog", "Display a Dialog to ask user for True/False question (Yes/No). Null or blank result means user canceled this dialogue."),
            AIFunctionFactory.Create((string title, string message, string initialValue, string placeholder) => (Parent as MultiWindowItem)?.DisplayPromptAsync(title, message, Localized._OK, Localized._Cancel, initialValue:initialValue, placeholder:placeholder) , "display_prompt", "Display a Dialog to ask user to input a string. User's input text will be presented in the result, Null result means user clicks the cancel button."),
             .. ToolCallFactories?.Invoke() ?? [],
        ];
        LogDiagnostic($"Tools:\r\n{string.Join("\r\n", tools.Select(t => JsonSerializer.Serialize(t, new JsonSerializerOptions { WriteIndented = true })))}");
        return tools;
    }

    private static void SetMessageText(ChatMessageItem item, string text)
    {
        if (MainThread.IsMainThread)
        {
            item.Message = text;
            return;
        }

        MainThread.BeginInvokeOnMainThread(() => item.Message = text);
    }

    private static void SetReasoningText(ChatMessageItem item, string text)
    {
        if (MainThread.IsMainThread)
        {
            item.ReasoningText = text;
            return;
        }

        MainThread.BeginInvokeOnMainThread(() => item.ReasoningText = text);
    }

    private static void SetToolCallsText(ChatMessageItem item, string text)
    {
        if (MainThread.IsMainThread)
        {
            item.ToolCallsText = text;
            return;
        }

        MainThread.BeginInvokeOnMainThread(() => item.ToolCallsText = text);
    }

    public static IChatClient? CreateChatClient()
    {
        string apiKey = AIHelper.CurrentOption.Key;
        string model = AIHelper.CurrentOption.Model;
        string endpoint = AIHelper.CurrentOption.BaseAddress;
        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(model) || string.IsNullOrEmpty(endpoint))
        {
            return null;
        }

        OpenAIChatClient chatClient;
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        {
            var options = new OpenAIClientOptions
            {
                Endpoint = endpointUri,

            };
            chatClient = new OpenAIChatClient(model, new System.ClientModel.ApiKeyCredential(apiKey), options);
        }
        else
        {
            chatClient = new OpenAIChatClient(model, apiKey);
        }

        return chatClient.AsIChatClient()
            .AsBuilder()
            .UseFunctionInvocation(AILoggerFactory, invoker =>
            {
                invoker.AllowConcurrentInvocation = false;
                invoker.IncludeDetailedErrors = true;
                invoker.TerminateOnUnknownCalls = false;
                invoker.MaximumIterationsPerRequest = 8;
            })
            .Build();
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_messages.Count == 0)
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            // 等布局稳定后再滚动，避免 MeasureAllItems 重新布局导致的跳动
            await Task.Delay(30);
            AIChatHistoryView.ScrollTo(_messages[^1], position: ScrollToPosition.End, animate: true);
        });
    }

    private sealed class ToolCallDisplayState
    {
        public int Order { get; init; }

        public string CallId { get; init; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Arguments { get; set; } = string.Empty;
    }

    private readonly struct ToolCallFragment
    {
        public string CallId { get; init; }

        public string Name { get; init; }

        public string Arguments { get; init; }
    }

    public enum ChatReplyFeedbackType
    {
        Good,
        Bad,
        Report,
    }

    private sealed class ChatReplyFeedbackPayload
    {
        public Guid SessionId { get; init; }

        public string Sender { get; init; } = string.Empty;

        public string Message { get; init; } = string.Empty;

        public ChatReplyFeedbackType FeedbackType { get; init; }

        public string ReasonCode { get; init; } = string.Empty;

        public string ReasonText { get; init; } = string.Empty;

        public DateTimeOffset CreatedAt { get; init; }
    }

}


public sealed partial class ChatMessageItem : INotifyPropertyChanged
{
    public ChatMessageItem()
    {
        ToggleReasoningCommand = new Microsoft.Maui.Controls.Command(() => IsReasoningExpanded = !IsReasoningExpanded);
        ToggleToolCallsCommand = new Microsoft.Maui.Controls.Command(() => IsToolCallsExpanded = !IsToolCallsExpanded);
    }

    public required string Sender { get; init; }

    private string _message = string.Empty;

    public string Message
    {
        get => _message;
        set
        {
            if (_message == value)
            {
                return;
            }

            _message = value;
            OnPropertyChanged();
        }
    }

    private string _reasoningText = string.Empty;

    public string ReasoningText
    {
        get => _reasoningText;
        set
        {
            if (_reasoningText == value)
            {
                return;
            }

            _reasoningText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasReasoning));
        }
    }

    public bool HasReasoning => !string.IsNullOrWhiteSpace(_reasoningText);

    private string _toolCallsText = string.Empty;

    public string ToolCallsText
    {
        get => _toolCallsText;
        set
        {
            if (_toolCallsText == value)
            {
                return;
            }

            _toolCallsText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasToolCalls));
        }
    }

    public bool HasToolCalls => !string.IsNullOrWhiteSpace(_toolCallsText);

    private bool _isReasoningExpanded = true;

    public bool IsReasoningExpanded
    {
        get => _isReasoningExpanded;
        set
        {
            if (_isReasoningExpanded == value)
            {
                return;
            }

            _isReasoningExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ReasoningToggleIcon));
        }
    }

    private bool _isToolCallsExpanded = true;

    public bool IsToolCallsExpanded
    {
        get => _isToolCallsExpanded;
        set
        {
            if (_isToolCallsExpanded == value)
            {
                return;
            }

            _isToolCallsExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ToolCallsToggleIcon));
        }
    }

    public string ReasoningToggleIcon => IsReasoningExpanded ? "▼" : "▶";

    public string ToolCallsToggleIcon => IsToolCallsExpanded ? "▼" : "▶";

    /// <summary>
    /// 切换思维链展开/折叠的命令
    /// </summary>
    public System.Windows.Input.ICommand ToggleReasoningCommand { get; }

    /// <summary>
    /// 切换工具调用展开/折叠的命令
    /// </summary>
    public System.Windows.Input.ICommand ToggleToolCallsCommand { get; }

    public bool IsUser { get; init; }

    public bool IsAssistant => !IsUser;

    private bool _hasFeedbackSubmitted;

    public bool HasFeedbackSubmitted
    {
        get => _hasFeedbackSubmitted;
        set
        {
            if (_hasFeedbackSubmitted == value)
            {
                return;
            }

            _hasFeedbackSubmitted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSubmitFeedback));
        }
    }

    private bool _isSubmittingFeedback;

    public bool IsSubmittingFeedback
    {
        get => _isSubmittingFeedback;
        set
        {
            if (_isSubmittingFeedback == value)
            {
                return;
            }

            _isSubmittingFeedback = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSubmitFeedback));
        }
    }

    public bool CanSubmitFeedback => IsAssistant && !IsSubmittingFeedback; // "Products that contain generative AI must provide a means for users to report inappropriate content generated by the AI Please update the product to include this feature" -- Microsoft Store policy

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
