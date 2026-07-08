using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ExpandOpenAI.AgentBase;

public  abstract class AIAgent
{
    public virtual IChatClient ChatClient { get; }
    public required virtual AgentOptions AgentOption { get; set; }
    public virtual ITokenCompressor? TokenCompressor { get; set; }

    public IList<ChatMessage> HistoryMessages { get; } = new List<ChatMessage>();

    public virtual async IAsyncEnumerable<ChatResponseUpdate> RunStreamAsync(string msg, AgentOptions? agentOptions=null)
    {
        agentOptions = agentOptions ?? AgentOption;
        var runContext = await CreateRunContextAsync(msg, agentOptions).ConfigureAwait(false);
        var configuredCompressionChecked = false;
        var forceCompressionRetryPending = false;
        var contextLengthCompressionRetried = false;
        var chatClientRetryCount = 0;

        while (true)
        {
            var preparedMessages = await PrepareMessagesForAttemptAsync(
                runContext.SystemPromptMessage,
                runContext.AIUserMessage,
                agentOptions,
                applyConfiguredCompression: !configuredCompressionChecked,
                forceCompression: forceCompressionRetryPending).ConfigureAwait(false);
            var chatOptions = CreateChatOptions(agentOptions);
            var effectiveChatClient = CreateEffectiveChatClient(agentOptions, chatOptions);

            configuredCompressionChecked = true;
            forceCompressionRetryPending = false;

            var responseUpdates = new List<ChatResponseUpdate>();
            var hasYieldedAnyUpdate = false;
            var shouldRetryCurrentAttempt = false;

            await using var enumerator = effectiveChatClient
                .GetStreamingResponseAsync(preparedMessages, chatOptions)
                .GetAsyncEnumerator();

            while (true)
            {
                ChatResponseUpdate update;

                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    update = enumerator.Current;
                }
                catch (Exception ex)
                {
                    if (hasYieldedAnyUpdate)
                    {
                        throw;
                    }

                    if (!contextLengthCompressionRetried && CanForceCompressForContextLengthExceeded(ex))
                    {
                        contextLengthCompressionRetried = true;
                        forceCompressionRetryPending = true;
                        shouldRetryCurrentAttempt = true;
                        break;
                    }

                    if (ShouldRetryChatClientException(ex, chatClientRetryCount, agentOptions))
                    {
                        chatClientRetryCount++;
                        shouldRetryCurrentAttempt = true;
                        break;
                    }

                    throw;
                }

                hasYieldedAnyUpdate = true;
                responseUpdates.Add(update);
                yield return update;
            }

            if (shouldRetryCurrentAttempt)
            {
                continue;
            }

            AppendHistoryMessage(runContext.HistoryUserMessage);
            await AppendResponseMessagesToHistoryAsync(
                responseUpdates.ToChatResponse().Messages,
                agentOptions).ConfigureAwait(false);
            yield break;
        }
    }

    public virtual async Task<ChatResponse> RunAsync(string msg, AgentOptions? agentOptions = null)
    {
        agentOptions = agentOptions ?? AgentOption;

        var runContext = await CreateRunContextAsync(msg, agentOptions).ConfigureAwait(false);
        var configuredCompressionChecked = false;
        var forceCompressionRetryPending = false;
        var contextLengthCompressionRetried = false;
        var chatClientRetryCount = 0;

        while (true)
        {
            var preparedMessages = await PrepareMessagesForAttemptAsync(
                runContext.SystemPromptMessage,
                runContext.AIUserMessage,
                agentOptions,
                applyConfiguredCompression: !configuredCompressionChecked,
                forceCompression: forceCompressionRetryPending).ConfigureAwait(false);
            var chatOptions = CreateChatOptions(agentOptions);
            var effectiveChatClient = CreateEffectiveChatClient(agentOptions, chatOptions);

            configuredCompressionChecked = true;
            forceCompressionRetryPending = false;

            try
            {
                var response = await effectiveChatClient
                    .GetResponseAsync(preparedMessages, chatOptions)
                    .ConfigureAwait(false);

                AppendHistoryMessage(runContext.HistoryUserMessage);
                await AppendResponseMessagesToHistoryAsync(response.Messages, agentOptions).ConfigureAwait(false);

                return response;
            }
            catch (Exception ex)
            {
                if (!contextLengthCompressionRetried && CanForceCompressForContextLengthExceeded(ex))
                {
                    contextLengthCompressionRetried = true;
                    forceCompressionRetryPending = true;
                    continue;
                }

                if (ShouldRetryChatClientException(ex, chatClientRetryCount, agentOptions))
                {
                    chatClientRetryCount++;
                    continue;
                }

                throw;
            }
        }
    }

    private async Task<(ChatMessage SystemPromptMessage, ChatMessage AIUserMessage, ChatMessage? HistoryUserMessage)> CreateRunContextAsync(
        string msg,
        AgentOptions agentOptions)
    {
        var systemPromptMessage = CreateSystemPromptMessage(agentOptions);
        EnsureHistorySystemPrompt(systemPromptMessage);

        var originalUserMessage = new ChatMessage(ChatRole.User, msg);
        var aiUserMessage = await HandleUserMessageEntryAIAsync(originalUserMessage, agentOptions).ConfigureAwait(false);
        var historyUserMessage = await HandleUserMessageEntryHistoryAsync(
            originalUserMessage,
            aiUserMessage,
            agentOptions).ConfigureAwait(false);

        return (systemPromptMessage, aiUserMessage, historyUserMessage);
    }

    private async Task<IList<ChatMessage>> PrepareMessagesForAttemptAsync(
        ChatMessage systemPromptMessage,
        ChatMessage aiUserMessage,
        AgentOptions agentOptions,
        bool applyConfiguredCompression,
        bool forceCompression)
    {
        EnsureHistorySystemPrompt(systemPromptMessage);

        var preparedMessages = BuildPreparedMessages(aiUserMessage, agentOptions);
        if (await CompressHistoryIfNeededAsync(
            systemPromptMessage,
            preparedMessages,
            agentOptions,
            applyConfiguredCompression,
            forceCompression).ConfigureAwait(false))
        {
            preparedMessages = BuildPreparedMessages(aiUserMessage, agentOptions);
        }

        return preparedMessages;
    }

    private async Task<ChatMessage> HandleUserMessageEntryAIAsync(ChatMessage userMessage, AgentOptions agentOptions)
    {
        var handler = agentOptions.AIMessageHandler;
        if (handler is null)
        {
            return CloneMessage(userMessage);
        }

        return await handler.UserMessageEntryAIHandle(CloneMessage(userMessage)).ConfigureAwait(false)
            ?? CloneMessage(userMessage);
    }

    private async Task<ChatMessage?> HandleUserMessageEntryHistoryAsync(
        ChatMessage userMessage,
        ChatMessage aiUserMessage,
        AgentOptions agentOptions)
    {
        var handler = agentOptions.AIMessageHandler;
        if (handler is null)
        {
            return CloneMessage(userMessage);
        }

        return await handler.UserMessageEntryHistoryMessagesHandle(
            CloneMessage(userMessage),
            CloneMessage(aiUserMessage)).ConfigureAwait(false);
    }

    private async Task<ChatMessage?> HandleAssistantMessageEntryHistoryAsync(
        ChatMessage assistantMessage,
        AgentOptions agentOptions)
    {
        var handler = agentOptions.AIMessageHandler;
        if (handler is null)
        {
            return CloneMessage(assistantMessage);
        }

        return await handler.AssistantMessageEntryHistoryMessagesHandle(CloneMessage(assistantMessage)).ConfigureAwait(false);
    }

    private async Task AppendResponseMessagesToHistoryAsync(
        IEnumerable<ChatMessage> responseMessages,
        AgentOptions agentOptions)
    {
        foreach (var responseMessage in responseMessages)
        {
            if (responseMessage.Role == ChatRole.System)
            {
                continue;
            }

            if (responseMessage.Role == ChatRole.Assistant)
            {
                AppendHistoryMessage(
                    await HandleAssistantMessageEntryHistoryAsync(responseMessage, agentOptions).ConfigureAwait(false));
                continue;
            }

            AppendHistoryMessage(CloneMessage(responseMessage));
        }
    }

    private void AppendHistoryMessage(ChatMessage? message)
    {
        if (message is not null)
        {
            HistoryMessages.Add(message);
        }
    }

    private static ChatMessage CloneMessage(ChatMessage message)
    {
        return new ChatMessage(message.Role, message.Contents.ToList())
        {
            AuthorName = message.AuthorName,
            CreatedAt = message.CreatedAt,
            MessageId = message.MessageId,
            RawRepresentation = message.RawRepresentation,
            AdditionalProperties = message.AdditionalProperties is null
                ? null
                : new AdditionalPropertiesDictionary(message.AdditionalProperties),
        };
    }

    private IList<ChatMessage> BuildPreparedMessages(ChatMessage aiUserMessage, AgentOptions agentOptions)
    {
        var messages = new List<ChatMessage>(HistoryMessages.Count + 1);
        messages.AddRange(HistoryMessages);
        messages.Add(aiUserMessage);

        return PrepareAgentMessages(messages, agentOptions);
    }

    private async Task<bool> CompressHistoryIfNeededAsync(
        ChatMessage systemPromptMessage,
        IList<ChatMessage> candidateMessages,
        AgentOptions agentOptions,
        bool applyConfiguredCompression,
        bool forceCompression)
    {
        if (TokenCompressor is null || HistoryMessages.Count <= 1)
        {
            return false;
        }

        if (!forceCompression)
        {
            if (!applyConfiguredCompression)
            {
                return false;
            }

            var shouldCompressMessages = agentOptions.ShouldCompressMessages;
            if (shouldCompressMessages is null || !shouldCompressMessages(CloneMessages(candidateMessages).AsReadOnly()))
            {
                return false;
            }
        }

        var compressedHistoryMessages = await TokenCompressor
            .CompressAsync(CloneMessages(HistoryMessages.Skip(1)), ChatClient)
            .ConfigureAwait(false);

        ReplaceHistoryMessages(systemPromptMessage, compressedHistoryMessages);
        return true;
    }

    private void ReplaceHistoryMessages(ChatMessage systemPromptMessage, IEnumerable<ChatMessage> messages)
    {
        HistoryMessages.Clear();
        HistoryMessages.Add(CloneMessage(systemPromptMessage));
        foreach (var message in CloneMessages(messages))
        {
            HistoryMessages.Add(message);
        }
    }

    private bool CanForceCompressForContextLengthExceeded(Exception exception)
    {
        return TokenCompressor is not null
            && HistoryMessages.Count > 1
            && IsContextLengthExceededException(exception);
    }

    private static bool ShouldRetryChatClientException(
        Exception exception,
        int currentRetryCount,
        AgentOptions agentOptions)
    {
        return currentRetryCount < agentOptions.ChatClientRetryCount
            && IsRetryableChatClientException(exception);
    }

    private static bool IsContextLengthExceededException(Exception exception)
    {
        var message = exception.ToString();
        return message.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase)
            || message.Contains("maximum context length", StringComparison.OrdinalIgnoreCase)
            || message.Contains("context length exceeded", StringComparison.OrdinalIgnoreCase)
            || message.Contains("context window", StringComparison.OrdinalIgnoreCase)
            || message.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase)
            || message.Contains("too many tokens", StringComparison.OrdinalIgnoreCase)
            || message.Contains("total message token length exceed model limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("message token length exceed model limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("exceed model limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("上下文长度", StringComparison.OrdinalIgnoreCase)
            || message.Contains("超出上下文", StringComparison.OrdinalIgnoreCase)
            || message.Contains("超过上下文", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRetryableChatClientException(Exception exception)
    {
        if (exception is TaskCanceledException || exception is TimeoutException || exception is IOException)
        {
            return true;
        }

        if (exception is not HttpRequestException httpRequestException)
        {
            return false;
        }

        if (IsContextLengthExceededException(httpRequestException))
        {
            return false;
        }

        var statusCode = TryExtractStatusCode(httpRequestException.Message);
        if (statusCode is null)
        {
            return true;
        }

        return statusCode == 408
            || statusCode == 429
            || statusCode == 500
            || statusCode == 502
            || statusCode == 503
            || statusCode == 504;
    }

    private static int? TryExtractStatusCode(string message)
    {
        var statusCode = TryExtractStatusCode(message, "状态码 ");
        if (statusCode is not null)
        {
            return statusCode;
        }

        statusCode = TryExtractStatusCode(message, "status code ");
        if (statusCode is not null)
        {
            return statusCode;
        }

        return TryExtractStatusCode(message, "status ");
    }

    private static int? TryExtractStatusCode(string message, string marker)
    {
        var markerIndex = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var valueStartIndex = markerIndex + marker.Length;
        var digits = new List<char>(3);

        for (var i = valueStartIndex; i < message.Length && digits.Count < 3; i++)
        {
            if (char.IsDigit(message[i]))
            {
                digits.Add(message[i]);
                continue;
            }

            if (digits.Count > 0)
            {
                break;
            }
        }

        if (digits.Count == 0)
        {
            return null;
        }

        return int.TryParse(new string([.. digits]), out var statusCode)
            ? statusCode
            : null;
    }

    private static ChatMessage CreateSystemPromptMessage(AgentOptions agentOptions)
    {
        return new ChatMessage(ChatRole.System, SystemPromptTemplateEngine.Render(agentOptions));
    }

    private void EnsureHistorySystemPrompt(ChatMessage systemPromptMessage)
    {
        if (HistoryMessages.Count == 0)
        {
            HistoryMessages.Add(CloneMessage(systemPromptMessage));
            return;
        }

        if (HistoryMessages[0].Role == ChatRole.System)
        {
            HistoryMessages[0] = CloneMessage(systemPromptMessage);
            return;
        }

        HistoryMessages.Insert(0, CloneMessage(systemPromptMessage));
    }

    private static List<ChatMessage> CloneMessages(IEnumerable<ChatMessage> messages)
    {
        var clones = new List<ChatMessage>();
        foreach (var message in messages)
        {
            clones.Add(CloneMessage(message));
        }

        return clones;
    }

    private IChatClient CreateEffectiveChatClient(AgentOptions agentOptions, ChatOptions? chatOptions)
    {
        if (chatOptions?.Tools is not { Count: > 0 })
        {
            return ChatClient;
        }

        return new FunctionInvokingChatClient(ChatClient, NullLoggerFactory.Instance, null)
        {
            FunctionInvoker = (context, cancellationToken) =>
                InvokeToolWithApprovalAsync(context, agentOptions, cancellationToken),
        };
    }

    private static ChatOptions? CreateChatOptions(AgentOptions agentOptions)
    {
        var hasTools = agentOptions.Tools.Count > 0;
        var hasRequestBody = agentOptions.RequestBody.Count > 0;

        if (!hasTools
            && agentOptions.ToolMode is null
            && agentOptions.AllowMultipleToolCalls is null
            && !hasRequestBody)
        {
            return null;
        }

        var options = new ChatOptions();

        if (hasTools)
        {
            options.Tools = agentOptions.Tools.ToList();
        }

        if (agentOptions.ToolMode is not null)
        {
            options.ToolMode = agentOptions.ToolMode;
        }

        if (agentOptions.AllowMultipleToolCalls is not null)
        {
            options.AllowMultipleToolCalls = agentOptions.AllowMultipleToolCalls;
        }

        if (hasRequestBody)
        {
            options.AdditionalProperties = new AdditionalPropertiesDictionary(
                agentOptions.RequestBody.ToDictionary(static pair => pair.Key, static pair => (object?)pair.Value));
        }

        return options;
    }

    private static ValueTask<object?> InvokeToolWithApprovalAsync(
        FunctionInvocationContext context,
        AgentOptions agentOptions,
        CancellationToken cancellationToken)
    {
        if (!agentOptions.ToolApprovalFunc(context.Function))
        {
            return new ValueTask<object?>(
                $"Tool '{context.Function.Name}' execution was denied by ToolApprovalFunc.");
        }

        return context.Function.InvokeAsync(context.Arguments, cancellationToken);
    }



    protected virtual IList<ChatMessage> PrepareAgentMessages(IList<ChatMessage> messages, AgentOptions agentOptions)
    {
        var systemPrompt = SystemPromptTemplateEngine.Render(agentOptions);
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            return [.. messages];
        }

        var preparedMessages = new List<ChatMessage>(messages.Count + 1);
        var systemPromptApplied = false;

        foreach (var message in messages)
        {
            if (!systemPromptApplied && message.Role == ChatRole.System)
            {
                preparedMessages.Add(new ChatMessage(ChatRole.System, systemPrompt));
                systemPromptApplied = true;
                continue;
            }

            preparedMessages.Add(message);
        }

        if (!systemPromptApplied)
        {
            preparedMessages.Insert(0, new ChatMessage(ChatRole.System, systemPrompt));
        }

        return preparedMessages;
    }


    public virtual async Task<string> GetFixJsonAsync(string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return json;
            var tempJson = GetJsonText(json);
            if (!string.IsNullOrWhiteSpace(tempJson)) json= tempJson!;
            var jsonNode=JsonNode.Parse(json,
                new JsonNodeOptions
                {
                    PropertyNameCaseInsensitive = true
                },
                new System.Text.Json.JsonDocumentOptions
                {
                    MaxDepth = 20,
                    AllowTrailingCommas = true,
                    AllowDuplicateProperties = false
                });
            if (jsonNode == null) return "null";
            return jsonNode.ToJsonString();
        }
        catch (Exception)
        {
            var res=await ChatClient.GetResponseAsync([
                new ChatMessage
            {
                Role = ChatRole.System,
                Contents = [new TextContent($"""
                    <指令>你专门负责修复各种错误的JSON，尽可能的将其还原为正确的JSON格式，并以修复后的JSON格式输出</指令>
                    """)]
            },
                new ChatMessage
            {
                Role = ChatRole.User,
                Contents = [new TextContent($"""
                    请帮我修复以下错误的JSON格式，并输出修复后的JSON格式：
                    <待修复的JSON>{json}</待修复的JSON>
                    """)]
            },
                ]);
            json = res.Text;
            if (string.IsNullOrWhiteSpace(json)) return json;
            var tempJson = GetJsonText(json);
            if (!string.IsNullOrWhiteSpace(tempJson)) json = tempJson!;
            return json;
        }
    }

    public static string? GetJsonText(string text)
    {
        try
        {
            var tempText = text;
            Regex jsonRegex = new Regex("""^\s*(?<content>[\[]\s*\{(?:\s*"[^"]*"\s*:\s*(?:"[^"]*"|\d+|true|false|null|\[.*?\]|\{.*?\})?\s*,?)+\}\s*[\]]\s*)$""", RegexOptions.Multiline);
            var maths = jsonRegex.Matches(text);
            if (maths.Count > 0)
            {
                // 2. MatchCollection非泛型，需Cast<Match>()才能使用LINQ扩展
                // 3. 用Groups["content"].Success替代ContainsKey，.NET Standard 2.0无ContainsKey API
                tempText = maths.Cast<Match>()
                    .Where(t => t.Groups["content"].Success)
                    .FirstOrDefault()?.Groups["content"].Value;
            }
            else if (tempText.Contains("```json"))
            {
                jsonRegex = new Regex(@"```json(\n|)(?<content>.*)(\n|)```", RegexOptions.Singleline);
                maths = jsonRegex.Matches(text);
                if (maths.Count > 0)
                {
                    tempText = maths.Cast<Match>()
                        .Where(t => t.Groups["content"].Success)
                        .FirstOrDefault()?.Groups["content"].Value;
                }
            }

            // 4. 替换default为null，避免旧编译器对default字面量的兼容问题
            if (string.IsNullOrEmpty(tempText)) return null;
            return tempText;
        }
        catch (Exception)
        {
            return null;
        }
    }
}


