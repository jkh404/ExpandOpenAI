using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.Internal;

internal sealed class OpenAICompatibleResponseParser
{
    private readonly JsonSerializerOptions _serializerOptions;

    public OpenAICompatibleResponseParser(JsonSerializerOptions serializerOptions)
    {
        _serializerOptions = serializerOptions;
    }

    public StreamingState CreateStreamingState() => new();

    public ChatResponse ParseResponse(JsonElement root)
    {
        var messages = new List<ChatMessage>();
        ChatFinishReason? finishReason = null;

        if (root.TryGetProperty("choices", out var choicesElement) && choicesElement.ValueKind == JsonValueKind.Array)
        {
            var responseId = OpenAICompatibleJsonHelpers.GetString(root, "id");

            foreach (var choice in choicesElement.EnumerateArray())
            {
                if (choice.TryGetProperty("message", out var messageElement))
                {
                    var message = ParseMessage(messageElement, ChatRole.Assistant);
                    message.MessageId ??= OpenAICompatibleJsonHelpers.CreateMessageId(responseId, choice);
                    messages.Add(message);
                }

                finishReason ??= OpenAICompatibleJsonHelpers.ParseFinishReason(choice);
            }
        }
        else
        {
            var message = ParseMessage(root, ChatRole.Assistant);
            if (message.Contents.Count > 0)
            {
                messages.Add(message);
            }

            finishReason = OpenAICompatibleJsonHelpers.ParseFinishReason(root);
        }

        return new ChatResponse(messages)
        {
            ResponseId = OpenAICompatibleJsonHelpers.GetString(root, "id"),
            ConversationId = OpenAICompatibleJsonHelpers.GetString(root, "conversation_id"),
            ModelId = OpenAICompatibleJsonHelpers.GetString(root, "model"),
            CreatedAt = OpenAICompatibleJsonHelpers.GetCreatedAt(root),
            FinishReason = finishReason,
            Usage = ParseUsage(root),
            RawRepresentation = root.Clone(),
            AdditionalProperties = OpenAICompatibleJsonHelpers.CollectAdditionalProperties(
                root,
                "id",
                "choices",
                "conversation_id",
                "model",
                "created",
                "usage"),
        };
    }

    public IEnumerable<ChatResponseUpdate> ParseStreamingEvent(
        IReadOnlyList<string> eventLines,
        StreamingState streamState)
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

        if (ParseUsage(root) is { } usage)
        {
            yield return new ChatResponseUpdate(
                role: null,
                [new UsageContent(usage) { RawRepresentation = root.Clone() }])
            {
                ResponseId = OpenAICompatibleJsonHelpers.GetString(root, "id"),
                ConversationId = OpenAICompatibleJsonHelpers.GetString(root, "conversation_id"),
                CreatedAt = OpenAICompatibleJsonHelpers.GetCreatedAt(root),
                ModelId = OpenAICompatibleJsonHelpers.GetString(root, "model"),
                RawRepresentation = root.Clone(),
            };
        }

        if (!root.TryGetProperty("choices", out var choicesElement) || choicesElement.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var responseId = OpenAICompatibleJsonHelpers.GetString(root, "id");
        var createdAt = OpenAICompatibleJsonHelpers.GetCreatedAt(root);
        var modelId = OpenAICompatibleJsonHelpers.GetString(root, "model");
        var conversationId = OpenAICompatibleJsonHelpers.GetString(root, "conversation_id");

        foreach (var choice in choicesElement.EnumerateArray())
        {
            var choiceIndex = OpenAICompatibleJsonHelpers.GetInt32(choice, "index") ?? 0;
            var choiceState = streamState.GetOrCreateChoice(choiceIndex);
            var messageId = OpenAICompatibleJsonHelpers.CreateMessageId(responseId, choice);
            var finishReason = OpenAICompatibleJsonHelpers.ParseFinishReason(choice);
            var contents = new List<AIContent>();
            ChatRole? role = null;

            if (choice.TryGetProperty("delta", out var delta))
            {
                role = OpenAICompatibleJsonHelpers.ParseRole(OpenAICompatibleJsonHelpers.GetString(delta, "role"));
                AppendReasoningContents(contents, delta, "reasoning_content");
                AppendReasoningContents(contents, delta, "reasoning");
                AppendStandardContents(contents, delta, "content");
                AccumulateToolCalls(choiceState, delta);
            }
            else if (choice.TryGetProperty("message", out var message))
            {
                role = OpenAICompatibleJsonHelpers.ParseRole(OpenAICompatibleJsonHelpers.GetString(message, "role"));
                AppendReasoningContents(contents, message, "reasoning_content");
                AppendReasoningContents(contents, message, "reasoning");
                AppendStandardContents(contents, message, "content");
                AccumulateToolCalls(choiceState, message);
            }

            if (finishReason is not null && !choiceState.ToolCallsEmitted && choiceState.ToolCalls.Count > 0)
            {
                foreach (var functionCall in FlushToolCalls(choiceState))
                {
                    contents.Add(functionCall);
                }
            }

            if (contents.Count == 0 && role is null && finishReason is null)
            {
                continue;
            }

            yield return new ChatResponseUpdate(role, contents)
            {
                ResponseId = responseId,
                MessageId = messageId ?? $"{responseId}:{choiceIndex}",
                ConversationId = conversationId,
                CreatedAt = createdAt,
                ModelId = modelId,
                FinishReason = finishReason,
                RawRepresentation = root.Clone(),
                AdditionalProperties = OpenAICompatibleJsonHelpers.CollectAdditionalProperties(
                    root,
                    "id",
                    "choices",
                    "conversation_id",
                    "model",
                    "created"),
            };
        }
    }

    public IEnumerable<ChatResponseUpdate> FlushPendingToolCalls(StreamingState streamState)
    {
        foreach (var pair in streamState.Choices)
        {
            var choiceIndex = pair.Key;
            var choiceState = pair.Value;
            if (choiceState.ToolCallsEmitted || choiceState.ToolCalls.Count == 0)
            {
                continue;
            }

            var contents = FlushToolCalls(choiceState).Cast<AIContent>().ToList();
            if (contents.Count == 0)
            {
                continue;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, contents)
            {
                MessageId = $"stream:{choiceIndex}",
            };
        }
    }

    private ChatMessage ParseMessage(JsonElement element, ChatRole defaultRole)
    {
        var role = OpenAICompatibleJsonHelpers.ParseRole(OpenAICompatibleJsonHelpers.GetString(element, "role")) ?? defaultRole;
        var contents = new List<AIContent>();

        AppendReasoningContents(contents, element, "reasoning_content");
        AppendReasoningContents(contents, element, "reasoning");
        AppendStandardContents(contents, element, "content");
        AppendToolCalls(contents, element);

        return new ChatMessage(role, contents)
        {
            AuthorName = OpenAICompatibleJsonHelpers.GetString(element, "name"),
            MessageId = OpenAICompatibleJsonHelpers.GetString(element, "id")
                ?? OpenAICompatibleJsonHelpers.GetString(element, "message_id"),
            CreatedAt = OpenAICompatibleJsonHelpers.GetCreatedAt(element),
            RawRepresentation = element.Clone(),
            AdditionalProperties = OpenAICompatibleJsonHelpers.CollectAdditionalProperties(
                element,
                "role",
                "name",
                "id",
                "message_id",
                "content",
                "reasoning_content",
                "reasoning",
                "tool_calls"),
        };
    }

    private UsageDetails? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usageElement) || usageElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var usage = new UsageDetails
        {
            InputTokenCount = OpenAICompatibleJsonHelpers.GetInt64(usageElement, "prompt_tokens"),
            OutputTokenCount = OpenAICompatibleJsonHelpers.GetInt64(usageElement, "completion_tokens"),
            TotalTokenCount = OpenAICompatibleJsonHelpers.GetInt64(usageElement, "total_tokens"),
        };

        if (usageElement.TryGetProperty("prompt_tokens_details", out var promptDetails) && promptDetails.ValueKind == JsonValueKind.Object)
        {
            usage.CachedInputTokenCount = OpenAICompatibleJsonHelpers.GetInt64(promptDetails, "cached_tokens");
        }

        if (usageElement.TryGetProperty("completion_tokens_details", out var completionDetails) && completionDetails.ValueKind == JsonValueKind.Object)
        {
            usage.ReasoningTokenCount = OpenAICompatibleJsonHelpers.GetInt64(completionDetails, "reasoning_tokens");
        }

        return usage;
    }

    private static void AppendStandardContents(List<AIContent> contents, JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return;
        }

        switch (property.ValueKind)
        {
            case JsonValueKind.String:
                var text = property.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    contents.Add(new TextContent(text));
                }

                break;

            case JsonValueKind.Array:
                foreach (var part in property.EnumerateArray())
                {
                    AppendContentPart(contents, part, reasoning: false);
                }

                break;

            case JsonValueKind.Object:
                AppendContentPart(contents, property, reasoning: false);
                break;
        }
    }

    private static void AppendReasoningContents(List<AIContent> contents, JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return;
        }

        switch (property.ValueKind)
        {
            case JsonValueKind.String:
                var text = property.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    contents.Add(new TextReasoningContent(text));
                }

                break;

            case JsonValueKind.Array:
                foreach (var part in property.EnumerateArray())
                {
                    AppendContentPart(contents, part, reasoning: true);
                }

                break;

            case JsonValueKind.Object:
                AppendContentPart(contents, property, reasoning: true);
                break;
        }
    }

    private static void AppendContentPart(List<AIContent> contents, JsonElement part, bool reasoning)
    {
        if (part.ValueKind == JsonValueKind.String)
        {
            var text = part.GetString();
            if (!string.IsNullOrEmpty(text))
            {
                contents.Add(reasoning ? new TextReasoningContent(text) : new TextContent(text));
            }

            return;
        }

        if (part.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (reasoning || string.Equals(OpenAICompatibleJsonHelpers.GetString(part, "type"), "reasoning", StringComparison.OrdinalIgnoreCase))
        {
            var reasoningText = OpenAICompatibleJsonHelpers.GetString(part, "text")
                ?? OpenAICompatibleJsonHelpers.GetString(part, "reasoning")
                ?? OpenAICompatibleJsonHelpers.GetString(part, "content");

            if (!string.IsNullOrEmpty(reasoningText))
            {
                contents.Add(new TextReasoningContent(reasoningText));
            }

            return;
        }

        var type = OpenAICompatibleJsonHelpers.GetString(part, "type");
        switch (type)
        {
            case null:
            case "text":
            case "output_text":
                var text = OpenAICompatibleJsonHelpers.GetString(part, "text")
                    ?? OpenAICompatibleJsonHelpers.GetString(part, "content");
                if (!string.IsNullOrEmpty(text))
                {
                    contents.Add(new TextContent(text));
                }

                break;

            case "image_url":
                if (part.TryGetProperty("image_url", out var imageUrl))
                {
                    var url = OpenAICompatibleJsonHelpers.GetString(imageUrl, "url");
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        contents.Add(CreateImageContent(url));
                    }
                }

                break;
        }
    }

    private static AIContent CreateImageContent(string url)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return new DataContent(url, mediaType: string.Empty);
        }

        return new UriContent(url, "image/*");
    }

    private void AppendToolCalls(List<AIContent> contents, JsonElement element)
    {
        if (!element.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var toolCall in toolCalls.EnumerateArray())
        {
            if (!string.Equals(OpenAICompatibleJsonHelpers.GetString(toolCall, "type"), "function", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var callId = OpenAICompatibleJsonHelpers.GetString(toolCall, "id") ?? Guid.NewGuid().ToString("N");
            if (!toolCall.TryGetProperty("function", out var function))
            {
                continue;
            }

            var name = OpenAICompatibleJsonHelpers.GetString(function, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            contents.Add(new FunctionCallContent(
                callId,
                name,
                ParseArguments(OpenAICompatibleJsonHelpers.GetString(function, "arguments"))));
        }
    }

    private static Dictionary<string, object?> ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json, AIJsonUtilities.DefaultOptions)
                ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["$raw"] = json,
            };
        }
    }

    private static void AccumulateToolCalls(StreamingChoiceState choiceState, JsonElement element)
    {
        if (!element.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var toolCall in toolCalls.EnumerateArray())
        {
            var callIndex = OpenAICompatibleJsonHelpers.GetInt32(toolCall, "index") ?? 0;
            var state = choiceState.GetOrCreateToolCall(callIndex);

            var callId = OpenAICompatibleJsonHelpers.GetString(toolCall, "id");
            if (!string.IsNullOrWhiteSpace(callId))
            {
                state.CallId = callId;
            }

            if (toolCall.TryGetProperty("function", out var function))
            {
                var name = OpenAICompatibleJsonHelpers.GetString(function, "name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    state.Name = name;
                }

                var arguments = OpenAICompatibleJsonHelpers.GetString(function, "arguments");
                if (!string.IsNullOrEmpty(arguments))
                {
                    state.Arguments.Append(arguments);
                }
            }
        }
    }

    private static List<FunctionCallContent> FlushToolCalls(StreamingChoiceState choiceState)
    {
        var contents = new List<FunctionCallContent>();
        foreach (var toolCall in choiceState.ToolCalls.OrderBy(pair => pair.Key))
        {
            var state = toolCall.Value;
            if (string.IsNullOrWhiteSpace(state.Name))
            {
                continue;
            }

            contents.Add(new FunctionCallContent(
                state.CallId ?? Guid.NewGuid().ToString("N"),
                state.Name,
                ParseArguments(state.Arguments.ToString())));
        }

        choiceState.ToolCallsEmitted = true;
        return contents;
    }

    internal sealed class StreamingState
    {
        public Dictionary<int, StreamingChoiceState> Choices { get; } = [];

        public StreamingChoiceState GetOrCreateChoice(int choiceIndex)
        {
            if (!Choices.TryGetValue(choiceIndex, out var state))
            {
                state = new StreamingChoiceState();
                Choices[choiceIndex] = state;
            }

            return state;
        }
    }

    internal sealed class StreamingChoiceState
    {
        public Dictionary<int, StreamingToolCallState> ToolCalls { get; } = [];

        public bool ToolCallsEmitted { get; set; }

        public StreamingToolCallState GetOrCreateToolCall(int toolCallIndex)
        {
            if (!ToolCalls.TryGetValue(toolCallIndex, out var state))
            {
                state = new StreamingToolCallState();
                ToolCalls[toolCallIndex] = state;
            }

            return state;
        }
    }

    internal sealed class StreamingToolCallState
    {
        public string? CallId { get; set; }

        public string? Name { get; set; }

        public StringBuilder Arguments { get; } = new();
    }
}
