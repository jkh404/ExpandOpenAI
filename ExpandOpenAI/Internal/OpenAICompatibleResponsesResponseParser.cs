using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.Internal;

internal sealed class OpenAICompatibleResponsesResponseParser
{
    private readonly JsonSerializerOptions _serializerOptions;

    public OpenAICompatibleResponsesResponseParser(JsonSerializerOptions serializerOptions)
    {
        _serializerOptions = serializerOptions;
    }

    public StreamingState CreateStreamingState() => new();

    public ChatResponse ParseResponse(JsonElement root)
    {
        var contents = new List<AIContent>();
        string? messageId = null;
        var hasFunctionCalls = false;

        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                var itemType = OpenAICompatibleJsonHelpers.GetString(item, "type");
                if (messageId is null && string.Equals(itemType, "message", StringComparison.Ordinal))
                {
                    messageId = OpenAICompatibleJsonHelpers.GetString(item, "id");
                }

                hasFunctionCalls |= string.Equals(itemType, "function_call", StringComparison.Ordinal);
                AppendOutputItem(
                    contents,
                    item,
                    includeText: true,
                    includeReasoning: true,
                    includeRefusal: true);
            }
        }

        AppendResponseError(contents, root);

        var messages = contents.Count == 0
            ? new List<ChatMessage>()
            :
            [
                new ChatMessage(ChatRole.Assistant, contents)
                {
                    MessageId = messageId ?? OpenAICompatibleJsonHelpers.GetString(root, "id"),
                    CreatedAt = GetCreatedAt(root),
                    RawRepresentation = root.Clone(),
                },
            ];

        return new ChatResponse(messages)
        {
            ResponseId = OpenAICompatibleJsonHelpers.GetString(root, "id"),
            ConversationId = GetConversationId(root),
            ModelId = OpenAICompatibleJsonHelpers.GetString(root, "model"),
            CreatedAt = GetCreatedAt(root),
            FinishReason = ParseFinishReason(root, hasFunctionCalls),
            Usage = ParseUsage(root),
            RawRepresentation = root.Clone(),
            AdditionalProperties = OpenAICompatibleJsonHelpers.CollectAdditionalProperties(
                root,
                "id",
                "object",
                "created_at",
                "status",
                "error",
                "incomplete_details",
                "model",
                "output",
                "usage",
                "conversation"),
        };
    }

    public IEnumerable<ChatResponseUpdate> ParseStreamingEvent(
        IReadOnlyList<string> eventLines,
        StreamingState state)
    {
        if (eventLines.Count == 0)
        {
            yield break;
        }

        var payload = OpenAICompatibleJsonHelpers.ExtractEventPayload(eventLines);
        if (string.IsNullOrWhiteSpace(payload) || string.Equals(payload, "[DONE]", StringComparison.Ordinal))
        {
            yield break;
        }

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var eventType = OpenAICompatibleJsonHelpers.GetString(root, "type") ?? GetSseEventName(eventLines);

        if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object)
        {
            state.UpdateResponseMetadata(response);
        }
        else
        {
            state.UpdateResponseMetadata(root);
        }

        switch (eventType)
        {
            case "response.created":
            case "response.in_progress":
            case "response.queued":
            case "response.content_part.added":
            case "response.content_part.done":
            case "response.reasoning_summary_part.added":
            case "response.reasoning_summary_part.done":
            case "response.output_text.annotation.added":
                yield break;

            case "response.output_item.added":
                if (TryGetObject(root, "item", out var addedItem))
                {
                    state.RememberItem(GetItemKey(root, addedItem), addedItem);
                }

                yield break;

            case "response.output_text.delta":
                {
                    var delta = OpenAICompatibleJsonHelpers.GetString(root, "delta");
                    if (string.IsNullOrEmpty(delta))
                    {
                        yield break;
                    }

                    var key = GetItemKey(root);
                    state.TextDeltaItems.Add(key);
                    yield return CreateUpdate(
                        state,
                        root,
                        key,
                        [new TextContent(delta) { RawRepresentation = root.Clone() }]);
                    yield break;
                }

            case "response.output_text.done":
                {
                    var key = GetItemKey(root);
                    var text = OpenAICompatibleJsonHelpers.GetString(root, "text");
                    if (!state.TextDeltaItems.Contains(key) && !string.IsNullOrEmpty(text))
                    {
                        state.TextDeltaItems.Add(key);
                        yield return CreateUpdate(
                            state,
                            root,
                            key,
                            [new TextContent(text) { RawRepresentation = root.Clone() }]);
                    }

                    yield break;
                }

            case "response.reasoning_summary_text.delta":
            case "response.reasoning_text.delta":
                {
                    var delta = OpenAICompatibleJsonHelpers.GetString(root, "delta");
                    if (string.IsNullOrEmpty(delta))
                    {
                        yield break;
                    }

                    var key = GetItemKey(root);
                    state.ReasoningDeltaItems.Add(key);
                    yield return CreateUpdate(
                        state,
                        root,
                        key,
                        [new TextReasoningContent(delta) { RawRepresentation = root.Clone() }]);
                    yield break;
                }

            case "response.reasoning_summary_text.done":
            case "response.reasoning_text.done":
                {
                    var key = GetItemKey(root);
                    var text = OpenAICompatibleJsonHelpers.GetString(root, "text");
                    if (!state.ReasoningDeltaItems.Contains(key) && !string.IsNullOrEmpty(text))
                    {
                        state.ReasoningDeltaItems.Add(key);
                        yield return CreateUpdate(
                            state,
                            root,
                            key,
                            [new TextReasoningContent(text) { RawRepresentation = root.Clone() }]);
                    }

                    yield break;
                }

            case "response.refusal.delta":
                {
                    var delta = OpenAICompatibleJsonHelpers.GetString(root, "delta");
                    if (string.IsNullOrEmpty(delta))
                    {
                        yield break;
                    }

                    var key = GetItemKey(root);
                    state.RefusalDeltaItems.Add(key);
                    yield return CreateUpdate(
                        state,
                        root,
                        key,
                        [new ErrorContent(delta) { ErrorCode = "refusal", RawRepresentation = root.Clone() }]);
                    yield break;
                }

            case "response.refusal.done":
                {
                    var key = GetItemKey(root);
                    var refusal = OpenAICompatibleJsonHelpers.GetString(root, "refusal");
                    if (!state.RefusalDeltaItems.Contains(key) && !string.IsNullOrEmpty(refusal))
                    {
                        state.RefusalDeltaItems.Add(key);
                        yield return CreateUpdate(
                            state,
                            root,
                            key,
                            [new ErrorContent(refusal) { ErrorCode = "refusal", RawRepresentation = root.Clone() }]);
                    }

                    yield break;
                }

            case "response.function_call_arguments.delta":
                AccumulateFunctionCall(state, root, finalArguments: false);
                yield break;

            case "response.function_call_arguments.done":
                {
                    var callState = AccumulateFunctionCall(state, root, finalArguments: true);
                    if (callState is not null && !callState.Emitted && !string.IsNullOrWhiteSpace(callState.Name))
                    {
                        callState.Emitted = true;
                        state.CompletedItems.Add(callState.ItemKey);
                        yield return CreateUpdate(
                            state,
                            root,
                            callState.ItemKey,
                            [CreateFunctionCall(callState)]);
                    }

                    yield break;
                }

            case "response.output_item.done":
                if (TryGetObject(root, "item", out var completedItem))
                {
                    foreach (var update in ParseCompletedItem(state, root, completedItem))
                    {
                        yield return update;
                    }
                }

                yield break;

            case "response.completed":
            case "response.failed":
            case "response.incomplete":
                if (TryGetObject(root, "response", out var completedResponse))
                {
                    foreach (var update in ParseRemainingResponseItems(state, root, completedResponse))
                    {
                        yield return update;
                    }

                    var usage = ParseUsage(completedResponse);
                    var finalContents = new List<AIContent>();
                    AppendResponseError(finalContents, completedResponse);
                    if (usage is not null)
                    {
                        finalContents.Add(new UsageContent(usage) { RawRepresentation = completedResponse.Clone() });
                    }

                    yield return CreateUpdate(
                        state,
                        root,
                        state.ResponseId ?? "response",
                        finalContents,
                        ParseFinishReason(completedResponse, state.HasFunctionCalls));
                }

                yield break;

            case "error":
                throw CreateStreamingException(root);

            default:
                yield return CreateUpdate(
                    state,
                    root,
                    GetItemKey(root),
                    [],
                    finishReason: null,
                    eventType: eventType);
                yield break;
        }
    }

    public IEnumerable<ChatResponseUpdate> FlushPendingItems(StreamingState state)
    {
        foreach (var pair in state.Items.ToList())
        {
            if (state.CompletedItems.Contains(pair.Key))
            {
                continue;
            }

            foreach (var update in ParseCompletedItem(state, pair.Value, pair.Value, pair.Key))
            {
                yield return update;
            }
        }

        foreach (var functionState in state.FunctionCalls.Values)
        {
            if (functionState.Emitted || string.IsNullOrWhiteSpace(functionState.Name))
            {
                continue;
            }

            functionState.Emitted = true;
            yield return CreateUpdate(
                state,
                functionState.RawRepresentation ?? default,
                functionState.ItemKey,
                [CreateFunctionCall(functionState)]);
        }
    }

    private IEnumerable<ChatResponseUpdate> ParseRemainingResponseItems(
        StreamingState state,
        JsonElement eventRoot,
        JsonElement response)
    {
        if (!response.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var index = 0;
        foreach (var item in output.EnumerateArray())
        {
            var key = GetItemKey(item, index++);
            if (state.CompletedItems.Contains(key))
            {
                continue;
            }

            foreach (var update in ParseCompletedItem(state, eventRoot, item, key))
            {
                yield return update;
            }
        }
    }

    private IEnumerable<ChatResponseUpdate> ParseCompletedItem(
        StreamingState state,
        JsonElement eventRoot,
        JsonElement item,
        string? explicitKey = null)
    {
        var key = explicitKey ?? GetItemKey(eventRoot, item);
        if (state.CompletedItems.Contains(key))
        {
            yield break;
        }

        state.RememberItem(key, item);
        var type = OpenAICompatibleJsonHelpers.GetString(item, "type");
        if (string.Equals(type, "function_call", StringComparison.Ordinal))
        {
            state.HasFunctionCalls = true;
            var callState = state.GetOrCreateFunctionCall(key);
            callState.UpdateFromItem(item, replaceArguments: true);
            if (!callState.Emitted && !string.IsNullOrWhiteSpace(callState.Name))
            {
                callState.Emitted = true;
                yield return CreateUpdate(state, eventRoot, key, [CreateFunctionCall(callState)]);
            }

            state.CompletedItems.Add(key);
            yield break;
        }

        var contents = new List<AIContent>();
        AppendOutputItem(
            contents,
            item,
            includeText: !state.TextDeltaItems.Contains(key),
            includeReasoning: !state.ReasoningDeltaItems.Contains(key),
            includeRefusal: !state.RefusalDeltaItems.Contains(key));

        state.CompletedItems.Add(key);
        if (contents.Count > 0)
        {
            yield return CreateUpdate(state, eventRoot, key, contents);
        }
    }

    private FunctionCallState? AccumulateFunctionCall(
        StreamingState state,
        JsonElement root,
        bool finalArguments)
    {
        var key = GetItemKey(root);
        var callState = state.GetOrCreateFunctionCall(key);
        callState.RawRepresentation = root.Clone();

        var name = OpenAICompatibleJsonHelpers.GetString(root, "name");
        if (!string.IsNullOrWhiteSpace(name))
        {
            callState.Name = name;
        }

        var callId = OpenAICompatibleJsonHelpers.GetString(root, "call_id");
        if (!string.IsNullOrWhiteSpace(callId))
        {
            callState.CallId = callId;
        }

        if (state.Items.TryGetValue(key, out var item))
        {
            callState.UpdateFromItem(item, replaceArguments: false);
        }

        var arguments = OpenAICompatibleJsonHelpers.GetString(root, finalArguments ? "arguments" : "delta");
        if (arguments is not null)
        {
            if (finalArguments)
            {
                callState.Arguments.Clear();
            }

            callState.Arguments.Append(arguments);
        }

        state.HasFunctionCalls = true;
        return callState;
    }

    private void AppendOutputItem(
        List<AIContent> contents,
        JsonElement item,
        bool includeText,
        bool includeReasoning,
        bool includeRefusal)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        switch (OpenAICompatibleJsonHelpers.GetString(item, "type"))
        {
            case "message":
                AppendMessageContents(contents, item, includeText, includeRefusal);
                break;

            case "reasoning":
                if (includeReasoning)
                {
                    contents.Add(ParseReasoning(item));
                }

                break;

            case "function_call":
                contents.Add(ParseFunctionCall(item));
                break;

            case "function_call_output":
                contents.Add(new FunctionResultContent(
                    OpenAICompatibleJsonHelpers.GetString(item, "call_id") ?? Guid.NewGuid().ToString("N"),
                    ReadJsonValue(item, "output"))
                {
                    RawRepresentation = item.Clone(),
                });
                break;

            default:
                contents.Add(new OpenAIResponsesRawContent(item, isTopLevelItem: true));
                break;
        }
    }

    private void AppendMessageContents(
        List<AIContent> contents,
        JsonElement item,
        bool includeText,
        bool includeRefusal)
    {
        if (!item.TryGetProperty("content", out var content))
        {
            return;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            if (includeText && !string.IsNullOrEmpty(content.GetString()))
            {
                contents.Add(new TextContent(content.GetString()!) { RawRepresentation = item.Clone() });
            }

            return;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            switch (OpenAICompatibleJsonHelpers.GetString(part, "type"))
            {
                case "output_text":
                case "input_text":
                    var text = OpenAICompatibleJsonHelpers.GetString(part, "text");
                    if (includeText && !string.IsNullOrEmpty(text))
                    {
                        contents.Add(new TextContent(text!) { RawRepresentation = part.Clone() });
                    }

                    break;

                case "refusal":
                    var refusal = OpenAICompatibleJsonHelpers.GetString(part, "refusal")
                        ?? OpenAICompatibleJsonHelpers.GetString(part, "text")
                        ?? "模型拒绝了该请求。";
                    if (includeRefusal)
                    {
                        contents.Add(new ErrorContent(refusal)
                        {
                            ErrorCode = "refusal",
                            RawRepresentation = part.Clone(),
                        });
                    }

                    break;

                default:
                    contents.Add(new OpenAIResponsesRawContent(part, isTopLevelItem: false));
                    break;
            }
        }
    }

    private TextReasoningContent ParseReasoning(JsonElement item)
    {
        var parts = new List<string>();
        AppendTextParts(parts, item, "summary");
        AppendTextParts(parts, item, "content");

        var reasoning = new TextReasoningContent(string.Join(Environment.NewLine, parts))
        {
            ProtectedData = OpenAICompatibleJsonHelpers.GetString(item, "encrypted_content"),
            RawRepresentation = item.Clone(),
        };

        return reasoning;
    }

    private static void AppendTextParts(List<string> parts, JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value))
        {
            return;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            if (!string.IsNullOrEmpty(value.GetString()))
            {
                parts.Add(value.GetString()!);
            }

            return;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var part in value.EnumerateArray())
        {
            var text = part.ValueKind == JsonValueKind.String
                ? part.GetString()
                : part.ValueKind == JsonValueKind.Object
                    ? OpenAICompatibleJsonHelpers.GetString(part, "text")
                    : null;

            if (!string.IsNullOrEmpty(text))
            {
                parts.Add(text!);
            }
        }
    }

    private FunctionCallContent ParseFunctionCall(JsonElement item)
    {
        var callId = OpenAICompatibleJsonHelpers.GetString(item, "call_id")
            ?? OpenAICompatibleJsonHelpers.GetString(item, "id")
            ?? Guid.NewGuid().ToString("N");
        var name = OpenAICompatibleJsonHelpers.GetString(item, "name") ?? "unknown_function";
        var argumentsText = OpenAICompatibleJsonHelpers.GetString(item, "arguments") ?? "{}";
        var arguments = ParseArguments(argumentsText, out var exception);

        return new FunctionCallContent(callId, name, arguments)
        {
            Exception = exception,
            RawRepresentation = item.Clone(),
        };
    }

    private FunctionCallContent CreateFunctionCall(FunctionCallState state)
    {
        var arguments = ParseArguments(state.Arguments.ToString(), out var exception);
        return new FunctionCallContent(
            state.CallId ?? state.ItemKey,
            state.Name ?? "unknown_function",
            arguments)
        {
            Exception = exception,
            RawRepresentation = state.RawRepresentation,
        };
    }

    private Dictionary<string, object?> ParseArguments(string json, out Exception? exception)
    {
        try
        {
            exception = null;
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json, _serializerOptions)
                ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        }
        catch (JsonException error)
        {
            exception = error;
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["$raw"] = json,
            };
        }
    }

    private static void AppendResponseError(List<AIContent> contents, JsonElement root)
    {
        if (!TryGetObject(root, "error", out var error))
        {
            return;
        }

        var message = OpenAICompatibleJsonHelpers.GetString(error, "message") ?? "Responses API 请求失败。";
        contents.Add(new ErrorContent(message)
        {
            ErrorCode = OpenAICompatibleJsonHelpers.GetString(error, "code")
                ?? OpenAICompatibleJsonHelpers.GetString(error, "type"),
            Details = error.GetRawText(),
            RawRepresentation = error.Clone(),
        });
    }

    private static object? ReadJsonValue(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.Clone();
    }

    private static UsageDetails? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var details = new UsageDetails
        {
            InputTokenCount = OpenAICompatibleJsonHelpers.GetInt64(usage, "input_tokens"),
            OutputTokenCount = OpenAICompatibleJsonHelpers.GetInt64(usage, "output_tokens"),
            TotalTokenCount = OpenAICompatibleJsonHelpers.GetInt64(usage, "total_tokens"),
        };

        if (TryGetObject(usage, "input_tokens_details", out var inputDetails))
        {
            details.CachedInputTokenCount = OpenAICompatibleJsonHelpers.GetInt64(inputDetails, "cached_tokens");
        }

        if (TryGetObject(usage, "output_tokens_details", out var outputDetails))
        {
            details.ReasoningTokenCount = OpenAICompatibleJsonHelpers.GetInt64(outputDetails, "reasoning_tokens");
        }

        return details;
    }

    private static ChatFinishReason? ParseFinishReason(JsonElement response, bool hasFunctionCalls)
    {
        var status = OpenAICompatibleJsonHelpers.GetString(response, "status");
        if (string.Equals(status, "completed", StringComparison.Ordinal))
        {
            return hasFunctionCalls ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop;
        }

        if (string.Equals(status, "incomplete", StringComparison.Ordinal))
        {
            var reason = TryGetObject(response, "incomplete_details", out var details)
                ? OpenAICompatibleJsonHelpers.GetString(details, "reason")
                : null;
            return string.Equals(reason, "max_output_tokens", StringComparison.Ordinal)
                ? ChatFinishReason.Length
                : string.Equals(reason, "content_filter", StringComparison.Ordinal)
                    ? ChatFinishReason.ContentFilter
                    : new ChatFinishReason(reason ?? "incomplete");
        }

        return string.Equals(status, "failed", StringComparison.Ordinal)
            ? new ChatFinishReason("failed")
            : null;
    }

    private static ChatResponseUpdate CreateUpdate(
        StreamingState state,
        JsonElement raw,
        string messageId,
        IList<AIContent> contents,
        ChatFinishReason? finishReason = null,
        string? eventType = null)
    {
        AdditionalPropertiesDictionary? additionalProperties = null;
        if (!string.IsNullOrWhiteSpace(eventType))
        {
            additionalProperties = new AdditionalPropertiesDictionary
            {
                ["responses_event_type"] = eventType,
            };
        }

        return new ChatResponseUpdate(contents.Count == 0 ? null : ChatRole.Assistant, contents)
        {
            ResponseId = state.ResponseId,
            MessageId = messageId,
            ConversationId = state.ConversationId,
            CreatedAt = state.CreatedAt,
            ModelId = state.ModelId,
            FinishReason = finishReason,
            RawRepresentation = raw.ValueKind == JsonValueKind.Undefined ? null : raw.Clone(),
            AdditionalProperties = additionalProperties,
        };
    }

    private static InvalidOperationException CreateStreamingException(JsonElement root)
    {
        var error = TryGetObject(root, "error", out var errorObject) ? errorObject : root;
        var message = OpenAICompatibleJsonHelpers.GetString(error, "message") ?? "Responses API 流式请求失败。";
        var code = OpenAICompatibleJsonHelpers.GetString(error, "code")
            ?? OpenAICompatibleJsonHelpers.GetString(error, "type");
        return new InvalidOperationException(string.IsNullOrWhiteSpace(code) ? message : $"{code}: {message}");
    }

    private static DateTimeOffset? GetCreatedAt(JsonElement element)
    {
        if (!element.TryGetProperty("created_at", out var createdAt))
        {
            return null;
        }

        if (createdAt.ValueKind == JsonValueKind.Number && createdAt.TryGetInt64(out var unixSeconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        if (createdAt.ValueKind == JsonValueKind.String)
        {
            if (createdAt.TryGetDateTimeOffset(out var result))
            {
                return result;
            }

            if (long.TryParse(createdAt.GetString(), out unixSeconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            }
        }

        return null;
    }

    private static string? GetConversationId(JsonElement element)
    {
        if (!element.TryGetProperty("conversation", out var conversation))
        {
            return OpenAICompatibleJsonHelpers.GetString(element, "conversation_id");
        }

        return conversation.ValueKind switch
        {
            JsonValueKind.String => conversation.GetString(),
            JsonValueKind.Object => OpenAICompatibleJsonHelpers.GetString(conversation, "id"),
            _ => null,
        };
    }

    private static bool TryGetObject(JsonElement element, string propertyName, out JsonElement value)
    {
        return element.TryGetProperty(propertyName, out value) && value.ValueKind == JsonValueKind.Object;
    }

    private static string GetItemKey(JsonElement eventRoot, JsonElement? item = null)
    {
        if (item is JsonElement value)
        {
            var itemId = OpenAICompatibleJsonHelpers.GetString(value, "id");
            if (!string.IsNullOrWhiteSpace(itemId))
            {
                return itemId!;
            }
        }

        return OpenAICompatibleJsonHelpers.GetString(eventRoot, "item_id")
            ?? OpenAICompatibleJsonHelpers.GetInt32(eventRoot, "output_index")?.ToString()
            ?? "response";
    }

    private static string GetItemKey(JsonElement item, int outputIndex)
    {
        return OpenAICompatibleJsonHelpers.GetString(item, "id") ?? outputIndex.ToString();
    }

    private static string? GetSseEventName(IReadOnlyList<string> eventLines)
    {
        foreach (var line in eventLines)
        {
            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                return line.Substring(6).Trim();
            }
        }

        return null;
    }

    internal sealed class StreamingState
    {
        public string? ResponseId { get; private set; }

        public string? ConversationId { get; private set; }

        public string? ModelId { get; private set; }

        public DateTimeOffset? CreatedAt { get; private set; }

        public bool HasFunctionCalls { get; set; }

        public Dictionary<string, JsonElement> Items { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, FunctionCallState> FunctionCalls { get; } = new(StringComparer.Ordinal);

        public HashSet<string> TextDeltaItems { get; } = new(StringComparer.Ordinal);

        public HashSet<string> ReasoningDeltaItems { get; } = new(StringComparer.Ordinal);

        public HashSet<string> RefusalDeltaItems { get; } = new(StringComparer.Ordinal);

        public HashSet<string> CompletedItems { get; } = new(StringComparer.Ordinal);

        public void UpdateResponseMetadata(JsonElement response)
        {
            ResponseId ??= OpenAICompatibleJsonHelpers.GetString(response, "id");
            ConversationId ??= GetConversationId(response);
            ModelId ??= OpenAICompatibleJsonHelpers.GetString(response, "model");
            CreatedAt ??= GetCreatedAt(response);
        }

        public void RememberItem(string key, JsonElement item)
        {
            Items[key] = item.Clone();
            if (string.Equals(OpenAICompatibleJsonHelpers.GetString(item, "type"), "function_call", StringComparison.Ordinal))
            {
                HasFunctionCalls = true;
                GetOrCreateFunctionCall(key).UpdateFromItem(item, replaceArguments: false);
            }
        }

        public FunctionCallState GetOrCreateFunctionCall(string key)
        {
            if (!FunctionCalls.TryGetValue(key, out var state))
            {
                state = new FunctionCallState(key);
                FunctionCalls[key] = state;
            }

            return state;
        }
    }

    internal sealed class FunctionCallState
    {
        public FunctionCallState(string itemKey)
        {
            ItemKey = itemKey;
        }

        public string ItemKey { get; }

        public string? CallId { get; set; }

        public string? Name { get; set; }

        public StringBuilder Arguments { get; } = new();

        public bool Emitted { get; set; }

        public JsonElement? RawRepresentation { get; set; }

        public void UpdateFromItem(JsonElement item, bool replaceArguments)
        {
            CallId = OpenAICompatibleJsonHelpers.GetString(item, "call_id") ?? CallId;
            Name = OpenAICompatibleJsonHelpers.GetString(item, "name") ?? Name;
            RawRepresentation = item.Clone();

            var arguments = OpenAICompatibleJsonHelpers.GetString(item, "arguments");
            if (arguments is null || (!replaceArguments && Arguments.Length > 0))
            {
                return;
            }

            Arguments.Clear();
            Arguments.Append(arguments);
        }
    }
}
