using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.AgentFramework;

/// <summary>
/// 分层压缩历史：先处理超长单消息，再按用户轮次分片归并，并将更早摘要移入会话记忆。
/// </summary>
public sealed class DefaultTokenCompressor : ITokenCompressor
{
    private const int MaximumSummaryReductionRounds = 8;
    private const string SummaryMarkerKey = "expandopenai.agent.turn_summary";
    private const string MemoryIdKey = "expandopenai.agent.memory_id";
    private const string MessageSummaryMarkerKey = "expandopenai.agent.message_summary";
    private const string LegacySummaryPrefix = "[Earlier conversation turn summary]\n";
    private const string MessageSummaryPrefix = "[Earlier message summary]\n";
    private const string SummaryPrefix = "[Earlier assistant/tool summary]\n";
    private readonly DefaultTokenCompressorOptions _options;
    private readonly Func<IReadOnlyList<ChatMessage>, int> _tokenEstimator;

    /// <summary>
    /// 创建默认压缩器。
    /// </summary>
    public DefaultTokenCompressor(DefaultTokenCompressorOptions? options = null)
    {
        _options = options ?? new DefaultTokenCompressorOptions();
        ValidateOptions(_options);
        _tokenEstimator = _options.TokenEstimator ?? EstimateTokens;
    }

    /// <inheritdoc />
    public bool ShouldCompress(IReadOnlyList<ChatMessage> messages)
    {
        ValidateMessages(messages);
        var turns = SplitTurns(messages);
        var verbatimStart = Math.Max(0, turns.Count - _options.RecentVerbatimTurnCount);
        var recentTurnCompressionThreshold = GetRecentTurnCompressionThreshold();

        return messages.Any(ShouldCompressMessage)
            || turns.Count > _options.RecentVerbatimTurnCount + _options.RecentSummaryTurnCount
            || _tokenEstimator(messages) > _options.MaximumHistoryTokenEstimate
            || turns.Skip(verbatimStart).Any(turn =>
                !IsExistingSummary(turn)
                && _tokenEstimator(turn.Messages) > recentTurnCompressionThreshold);
    }

    /// <inheritdoc />
    public async ValueTask<TokenCompressionResult> CompressAsync(
        TokenCompressionContext context,
        IChatClient chatClient,
        CancellationToken cancellationToken = default)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        if (chatClient is null)
        {
            throw new ArgumentNullException(nameof(chatClient));
        }

        ValidateMessages(context.Messages);
        var preparedMessages = await CompressOversizedMessagesAsync(
            context.Messages,
            chatClient,
            cancellationToken).ConfigureAwait(false);
        var turns = SplitTurns(preparedMessages);
        if (turns.Count == 0)
        {
            return new TokenCompressionResult(Array.Empty<ChatMessage>());
        }

        var activeTurns = new List<ActiveTurn>();
        var archivedMemories = new List<MemoryEntry>();
        var verbatimStart = Math.Max(0, turns.Count - _options.RecentVerbatimTurnCount);
        var summaryStart = Math.Max(0, verbatimStart - _options.RecentSummaryTurnCount);
        var recentTurnCompressionThreshold = GetRecentTurnCompressionThreshold();

        for (var index = 0; index < turns.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var turn = turns[index];

            if (index < summaryStart)
            {
                var summary = await SummarizeTurnAsync(turn, chatClient, cancellationToken).ConfigureAwait(false);
                archivedMemories.Add(summary.Memory);
                continue;
            }

            var mustSummarize = index < verbatimStart
                || (!IsExistingSummary(turn)
                    && index >= verbatimStart
                    && _tokenEstimator(turn.Messages) > recentTurnCompressionThreshold)
                || (context.Reason == TokenCompressionReason.ModelRequested
                    && index == turns.Count - 1
                    && !IsExistingSummary(turn))
                || (context.Reason == TokenCompressionReason.ContextLengthExceeded
                    && turns.Count == 1
                    && !IsExistingSummary(turn));

            if (mustSummarize)
            {
                var summary = await SummarizeTurnAsync(turn, chatClient, cancellationToken).ConfigureAwait(false);
                activeTurns.Add(new ActiveTurn(
                    summary.Messages,
                    summary.Memory,
                    turn.Ordinal,
                    isVerbatim: !summary.HasSummary));
            }
            else
            {
                activeTurns.Add(new ActiveTurn(
                    CloneMessages(turn.Messages),
                    null,
                    turn.Ordinal,
                    isVerbatim: true));
            }
        }

        while (activeTurns.Count > 1
            && _tokenEstimator(Flatten(activeTurns)) > _options.MaximumHistoryTokenEstimate)
        {
            var oldest = activeTurns[0];
            if (oldest.Memory is null)
            {
                var summary = await SummarizeTurnAsync(
                    new ConversationTurn(oldest.Messages, oldest.Ordinal),
                    chatClient,
                    cancellationToken).ConfigureAwait(false);
                archivedMemories.Add(summary.Memory);
            }
            else
            {
                archivedMemories.Add(oldest.Memory);
            }

            activeTurns.RemoveAt(0);
        }

        if (activeTurns.Count == 1
            && activeTurns[0].IsVerbatim
            && _tokenEstimator(Flatten(activeTurns)) > _options.MaximumHistoryTokenEstimate)
        {
            var summary = await SummarizeTurnAsync(
                new ConversationTurn(activeTurns[0].Messages, activeTurns[0].Ordinal),
                chatClient,
                cancellationToken).ConfigureAwait(false);
            activeTurns[0] = new ActiveTurn(
                summary.Messages,
                summary.Memory,
                activeTurns[0].Ordinal,
                isVerbatim: !summary.HasSummary);
        }

        return new TokenCompressionResult(
            Flatten(activeTurns).AsReadOnly(),
            archivedMemories.AsReadOnly());
    }

    private async Task<List<ChatMessage>> CompressOversizedMessagesAsync(
        IReadOnlyList<ChatMessage> messages,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        if (_options.MaximumMessageTokenEstimate == 0)
        {
            return CloneMessages(messages);
        }

        var result = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(ShouldCompressMessage(message)
                ? await SummarizeMessageAsync(message, chatClient, cancellationToken).ConfigureAwait(false)
                : CloneMessage(message));
        }

        return result;
    }

    private bool ShouldCompressMessage(ChatMessage message)
    {
        return _options.MaximumMessageTokenEstimate > 0
            && IsMessageCompressionCandidate(message)
            && _tokenEstimator([message]) > _options.MaximumMessageTokenEstimate;
    }

    private static bool IsMessageCompressionCandidate(ChatMessage message)
    {
        if ((message.Role == ChatRole.System || message.Role == ChatRole.User)
            || message.AdditionalProperties is { } properties
                && properties.TryGetValue(MessageSummaryMarkerKey, out var marker)
                && marker is true)
        {
            return false;
        }

        if (message.Role == ChatRole.Assistant)
        {
            return message.Contents.Count > 0
                && message.Contents.All(static content =>
                    content is TextContent or TextReasoningContent);
        }

        return message.Role == ChatRole.Tool
            && message.Contents.Count == 1
            && message.Contents[0] is FunctionResultContent { Exception: null };
    }

    private async Task<ChatMessage> SummarizeMessageAsync(
        ChatMessage message,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        var summaryText = await GenerateHierarchicalSummaryAsync(
            _options.MessageSummaryPrompt,
            preservedContext: null,
            [FormatTurn([message])],
            chatClient,
            cancellationToken).ConfigureAwait(false);

        IList<AIContent> contents;
        if (message.Role == ChatRole.Tool)
        {
            var original = (FunctionResultContent)message.Contents[0];
            contents =
            [
                new FunctionResultContent(original.CallId, MessageSummaryPrefix + summaryText)
                {
                    Annotations = original.Annotations?.ToList() ?? [],
                    AdditionalProperties = original.AdditionalProperties is null
                        ? null
                        : new AdditionalPropertiesDictionary(original.AdditionalProperties),
                },
            ];
        }
        else
        {
            contents = [new TextContent(MessageSummaryPrefix + summaryText)];
        }

        var additionalProperties = message.AdditionalProperties is null
            ? new AdditionalPropertiesDictionary()
            : new AdditionalPropertiesDictionary(message.AdditionalProperties);
        additionalProperties[MessageSummaryMarkerKey] = true;

        return new ChatMessage(message.Role, contents)
        {
            AuthorName = message.AuthorName,
            CreatedAt = message.CreatedAt,
            MessageId = message.MessageId,
            AdditionalProperties = additionalProperties,
        };
    }

    private async Task<TurnSummary> SummarizeTurnAsync(
        ConversationTurn turn,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        if (TryGetExistingSummary(turn, out var existing))
        {
            return existing;
        }

        var memoryId = CreateMemoryId(turn.Messages, turn.Ordinal);
        var preservedUserMessages = turn.Messages
            .Where(static message => message.Role == ChatRole.User)
            .Select(CloneMessage)
            .ToList();
        var messagesToSummarize = turn.Messages
            .Where(static message => message.Role != ChatRole.User)
            .Select(CloneMessage)
            .ToList();
        var createdAt = turn.Messages
            .Select(static message => message.CreatedAt)
            .FirstOrDefault(static value => value.HasValue)
            ?? DateTimeOffset.UtcNow;

        if (messagesToSummarize.Count == 0)
        {
            return new TurnSummary(
                preservedUserMessages.AsReadOnly(),
                CreateMemory(memoryId, preservedUserMessages, null, createdAt),
                hasSummary: false);
        }

        var summaryText = await GenerateHierarchicalSummaryAsync(
            _options.SummaryPrompt,
            FormatTurn(preservedUserMessages),
            BuildInteractionBlocks(messagesToSummarize)
                .Select(FormatTurn)
                .ToList(),
            chatClient,
            cancellationToken).ConfigureAwait(false);

        var memory = CreateMemory(memoryId, preservedUserMessages, summaryText, createdAt);
        var summaryMessage = new ChatMessage(
            preservedUserMessages.Count > 0 ? ChatRole.Assistant : ChatRole.User,
            SummaryPrefix + summaryText)
        {
            CreatedAt = createdAt,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [SummaryMarkerKey] = true,
                [MemoryIdKey] = memoryId,
            },
        };
        var activeMessages = new List<ChatMessage>(preservedUserMessages.Count + 1);
        activeMessages.AddRange(preservedUserMessages);
        activeMessages.Add(summaryMessage);

        return new TurnSummary(activeMessages.AsReadOnly(), memory, hasSummary: true);
    }

    private async Task<string> GenerateHierarchicalSummaryAsync(
        string basePrompt,
        string? preservedContext,
        IReadOnlyList<string> orderedItems,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        if (orderedItems.Count == 0)
        {
            throw new ArgumentException("分层摘要至少需要一项输入。", nameof(orderedItems));
        }

        var inputThreshold = GetRecentTurnCompressionThreshold();
        var inputs = BuildSummaryInputs(preservedContext, orderedItems, inputThreshold);
        var summaries = await SummarizeInputsAsync(
            basePrompt,
            inputs,
            mergePartials: false,
            chatClient,
            cancellationToken).ConfigureAwait(false);

        for (var round = 0; summaries.Count > 1 && round < MaximumSummaryReductionRounds; round++)
        {
            var mergeItems = summaries
                .Select((summary, index) => $"[Ordered partial summary {index + 1}]\n{summary}")
                .ToList();
            var mergeInputs = BuildSummaryInputs(preservedContext, mergeItems, inputThreshold);
            if (mergeInputs.Count >= summaries.Count)
            {
                throw new InvalidOperationException(
                    "分层摘要无法在配置的 Token 阈值内继续收敛。请提高 MaximumHistoryTokenEstimate 或降低 SummaryMaxOutputTokens。");
            }

            summaries = await SummarizeInputsAsync(
                basePrompt,
                mergeInputs,
                mergePartials: true,
                chatClient,
                cancellationToken).ConfigureAwait(false);
        }

        return summaries.Count == 1
            ? summaries[0]
            : throw new InvalidOperationException("分层摘要超过最大归并轮数，未能生成单一摘要。");
    }

    private async Task<List<string>> SummarizeInputsAsync(
        string basePrompt,
        IReadOnlyList<string> inputs,
        bool mergePartials,
        IChatClient chatClient,
        CancellationToken cancellationToken)
    {
        var summaries = new List<string>(inputs.Count);
        for (var index = 0; index < inputs.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operationPrompt = mergePartials
                ? basePrompt
                    + "\n下面是按原始时间顺序排列的局部摘要。请合并、去重并提炼任务相关关键信息，"
                    + "保持事实、工具调用、结果、错误、决定和未完成事项的先后关系。"
                : inputs.Count == 1
                    ? basePrompt
                    : basePrompt
                        + $"\n这是超长输入的第 {index + 1}/{inputs.Count} 个有序片段。"
                        + "只总结当前片段，保留与前后片段衔接所需的实体、CallId、决定和未完成事项。";
            var response = await chatClient.GetResponseAsync(
            [
                new ChatMessage(ChatRole.System, operationPrompt),
                new ChatMessage(ChatRole.User, inputs[index]),
            ],
            new ChatOptions
            {
                MaxOutputTokens = _options.SummaryMaxOutputTokens,
                Temperature = 0,
            },
            cancellationToken).ConfigureAwait(false);
            var summaryText = (response.Text ?? string.Empty).Trim();
            if (summaryText.Length == 0)
            {
                throw new InvalidOperationException("默认 TokenCompressor 未能生成有效摘要。");
            }

            summaries.Add(summaryText);
        }

        return summaries;
    }

    private List<string> BuildSummaryInputs(
        string? preservedContext,
        IReadOnlyList<string> orderedItems,
        int inputThreshold)
    {
        var prefix = CreateSummaryInputPrefix(preservedContext, inputThreshold);
        var fragments = orderedItems
            .SelectMany(item => SplitTextToFit(item, prefix, inputThreshold))
            .ToList();
        var inputs = new List<string>();
        var current = new StringBuilder(prefix);
        var hasContent = false;

        foreach (var fragment in fragments)
        {
            var separator = hasContent ? "\n---\n" : string.Empty;
            var candidate = current.ToString() + separator + fragment;
            if (hasContent && EstimateSummaryInputTokens(candidate) > inputThreshold)
            {
                inputs.Add(current.ToString());
                current.Clear();
                current.Append(prefix).Append(fragment);
            }
            else
            {
                current.Append(separator).Append(fragment);
            }

            hasContent = true;
        }

        if (hasContent)
        {
            inputs.Add(current.ToString());
        }

        return inputs;
    }

    private string CreateSummaryInputPrefix(string? preservedContext, int inputThreshold)
    {
        const string contentHeader = "[Ordered content to summarize]\n";
        if (string.IsNullOrWhiteSpace(preservedContext))
        {
            return contentHeader;
        }

        var fullPrefix = "[Preserved User context]\n" + preservedContext + "\n" + contentHeader;
        if (EstimateSummaryInputTokens(fullPrefix) < inputThreshold)
        {
            return fullPrefix;
        }

        const string contextNotice =
            "[User context is preserved separately because it is too large for each summary fragment.]\n";
        return EstimateSummaryInputTokens(contextNotice + contentHeader) < inputThreshold
            ? contextNotice + contentHeader
            : string.Empty;
    }

    private IReadOnlyList<string> SplitTextToFit(
        string text,
        string prefix,
        int inputThreshold)
    {
        if (EstimateSummaryInputTokens(prefix + text) <= inputThreshold)
        {
            return [text];
        }

        var fragments = new List<string>();
        var offset = 0;
        while (offset < text.Length)
        {
            var low = 1;
            var high = text.Length - offset;
            var bestLength = 0;
            while (low <= high)
            {
                var middle = low + ((high - low) / 2);
                var candidate = text.Substring(offset, middle);
                if (EstimateSummaryInputTokens(prefix + candidate) <= inputThreshold)
                {
                    bestLength = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            if (bestLength == 0)
            {
                bestLength = 1;
            }

            if (bestLength < text.Length - offset)
            {
                var candidate = text.Substring(offset, bestLength);
                var lastLineBreak = candidate.LastIndexOf('\n');
                if (lastLineBreak >= bestLength / 2)
                {
                    bestLength = lastLineBreak + 1;
                }
            }

            fragments.Add(text.Substring(offset, bestLength));
            offset += bestLength;
        }

        return fragments;
    }

    private int EstimateSummaryInputTokens(string text)
    {
        return _tokenEstimator([new ChatMessage(ChatRole.User, text)]);
    }

    private static IReadOnlyList<IReadOnlyList<ChatMessage>> BuildInteractionBlocks(
        IReadOnlyList<ChatMessage> messages)
    {
        var blocks = new List<IReadOnlyList<ChatMessage>>();
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            var pendingCallIds = new HashSet<string>(
                message.Contents
                    .OfType<FunctionCallContent>()
                    .Select(static call => call.CallId),
                StringComparer.Ordinal);
            if (pendingCallIds.Count == 0)
            {
                blocks.Add([CloneMessage(message)]);
                continue;
            }

            var block = new List<ChatMessage> { CloneMessage(message) };
            while (index + 1 < messages.Count && pendingCallIds.Count > 0)
            {
                var candidate = messages[index + 1];
                var resultCallIds = candidate.Contents
                    .OfType<FunctionResultContent>()
                    .Select(static result => result.CallId)
                    .ToList();
                if (candidate.Role != ChatRole.Tool
                    || resultCallIds.Count == 0
                    || resultCallIds.Any(callId => !pendingCallIds.Contains(callId)))
                {
                    break;
                }

                index++;
                block.Add(CloneMessage(candidate));
                foreach (var callId in resultCallIds)
                {
                    pendingCallIds.Remove(callId);
                }
            }

            blocks.Add(block.AsReadOnly());
        }

        return blocks;
    }

    private static bool TryGetExistingSummary(ConversationTurn turn, out TurnSummary summary)
    {
        var markedMessage = turn.Messages.FirstOrDefault(static message =>
            message.AdditionalProperties is { } properties
            && properties.TryGetValue(SummaryMarkerKey, out var marker)
            && marker is true);
        if (markedMessage is not null)
        {
            var messages = CloneMessages(turn.Messages).AsReadOnly();
            var content = RemoveSummaryPrefix(markedMessage.Text ?? string.Empty);

            if (string.IsNullOrWhiteSpace(content))
            {
                summary = null!;
                return false;
            }

            var properties = markedMessage.AdditionalProperties!;
            var memoryId = properties.TryGetValue(MemoryIdKey, out var value) && value is string id
                ? id
                : CreateMemoryId(turn.Messages, turn.Ordinal);
            var preservedUserMessages = turn.Messages
                .Where(message => message.Role == ChatRole.User && !ReferenceEquals(message, markedMessage))
                .Select(CloneMessage)
                .ToList();
            var memory = CreateMemory(
                memoryId,
                preservedUserMessages,
                content,
                markedMessage.CreatedAt ?? DateTimeOffset.UtcNow);
            summary = new TurnSummary(messages, memory, hasSummary: true);
            return true;
        }

        summary = null!;
        return false;
    }

    private static bool IsExistingSummary(ConversationTurn turn)
    {
        return turn.Messages.Any(static message =>
            message.AdditionalProperties is { } properties
            && properties.TryGetValue(SummaryMarkerKey, out var marker)
            && marker is true);
    }

    private static MemoryEntry CreateMemory(
        string memoryId,
        IReadOnlyList<ChatMessage> preservedUserMessages,
        string? summaryText,
        DateTimeOffset createdAt)
    {
        var content = new StringBuilder();
        if (preservedUserMessages.Count > 0)
        {
            content.AppendLine("[User messages preserved verbatim]");
            content.Append(FormatTurn(preservedUserMessages));
        }

        if (!string.IsNullOrWhiteSpace(summaryText))
        {
            content.AppendLine("[Assistant/tool summary]");
            content.AppendLine(summaryText);
        }

        return new MemoryEntry(
            memoryId,
            content.ToString().Trim(),
            createdAt,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["kind"] = "conversation-turn-summary",
            });
    }

    private static string RemoveSummaryPrefix(string content)
    {
        if (content.StartsWith(SummaryPrefix, StringComparison.Ordinal))
        {
            return content.Substring(SummaryPrefix.Length);
        }

        return content.StartsWith(LegacySummaryPrefix, StringComparison.Ordinal)
            ? content.Substring(LegacySummaryPrefix.Length)
            : content;
    }

    private static List<ConversationTurn> SplitTurns(IReadOnlyList<ChatMessage> messages)
    {
        var turns = new List<ConversationTurn>();
        List<ChatMessage>? current = null;

        foreach (var message in messages)
        {
            if (message.Role == ChatRole.User)
            {
                if (current is { Count: > 0 })
                {
                    turns.Add(new ConversationTurn(current, turns.Count));
                }

                current = new List<ChatMessage>();
            }

            current ??= new List<ChatMessage>();
            current.Add(CloneMessage(message));
        }

        if (current is { Count: > 0 })
        {
            turns.Add(new ConversationTurn(current, turns.Count));
        }

        return turns;
    }

    private static string FormatTurn(IReadOnlyList<ChatMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            builder.Append('[').Append(message.Role).AppendLine("]");
            if (message.Contents.Count == 0)
            {
                builder.AppendLine("(empty)");
                continue;
            }

            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text:
                        builder.AppendLine(text.Text);
                        break;
                    case FunctionCallContent call:
                        builder
                            .Append("Function call: ")
                            .Append(call.Name)
                            .Append(" callId=")
                            .Append(call.CallId)
                            .Append(" arguments=")
                            .AppendLine(SerializeValue(call.Arguments));
                        if (call.Exception is not null)
                        {
                            builder.Append("Function call error: ").AppendLine(call.Exception.Message);
                        }
                        break;
                    case FunctionResultContent result:
                        builder
                            .Append("Function result: callId=")
                            .Append(result.CallId)
                            .Append(" result=")
                            .AppendLine(SerializeValue(result.Result));
                        if (result.Exception is not null)
                        {
                            builder.Append("Function result error: ").AppendLine(result.Exception.Message);
                        }
                        break;
                    default:
                        builder.AppendLine(content.ToString());
                        break;
                }
            }
        }

        return builder.ToString();
    }

    private static string SerializeValue(object? value)
    {
        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch (NotSupportedException)
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    private static string CreateMemoryId(IReadOnlyList<ChatMessage> messages, int ordinal)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(
            ordinal.ToString(CultureInfo.InvariantCulture) + "\n" + FormatTurn(messages));
        var hash = sha256.ComputeHash(bytes);
        var builder = new StringBuilder("turn-");
        foreach (var value in hash)
        {
            builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static int EstimateTokens(IReadOnlyList<ChatMessage> messages)
    {
        var text = FormatTurn(messages);
        var asciiCharacters = 0;
        var nonAsciiCharacters = 0;

        foreach (var character in text)
        {
            if (character <= 0x7f)
            {
                asciiCharacters++;
            }
            else
            {
                nonAsciiCharacters++;
            }
        }

        return Math.Max(1, ((asciiCharacters + 3) / 4) + nonAsciiCharacters + (messages.Count * 4));
    }

    private int GetRecentTurnCompressionThreshold()
    {
        return (int)(((long)_options.MaximumHistoryTokenEstimate * 2) / 3);
    }

    private static List<ChatMessage> Flatten(IReadOnlyList<ActiveTurn> turns)
    {
        return turns.SelectMany(static turn => turn.Messages).Select(CloneMessage).ToList();
    }

    private static void ValidateMessages(IReadOnlyList<ChatMessage> messages)
    {
        if (messages is null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        if (messages.Any(static message => message is null))
        {
            throw new ArgumentException("历史消息不能包含 null。", nameof(messages));
        }

        if (messages.Any(static message => message.Role == ChatRole.System))
        {
            throw new ArgumentException("TokenCompressor 的输入不能包含 System 消息。", nameof(messages));
        }
    }

    private static void ValidateOptions(DefaultTokenCompressorOptions options)
    {
        if (options.RecentVerbatimTurnCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.RecentVerbatimTurnCount),
                "最近原样轮次数必须大于 0。");
        }

        if (options.RecentSummaryTurnCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.RecentSummaryTurnCount),
                "近期摘要轮次数不能小于 0。");
        }

        if (options.MaximumHistoryTokenEstimate <= 0
            || options.MaximumMessageTokenEstimate < 0
            || options.SummaryMaxOutputTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "历史与摘要 Token 限制必须大于 0，单消息阈值可以为 0（关闭）或正数。");
        }

        if (string.IsNullOrWhiteSpace(options.MessageSummaryPrompt))
        {
            throw new ArgumentException("单消息摘要提示词不能为空。", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.SummaryPrompt))
        {
            throw new ArgumentException("摘要提示词不能为空。", nameof(options));
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

    private static List<ChatMessage> CloneMessages(IEnumerable<ChatMessage> messages)
    {
        return messages.Select(CloneMessage).ToList();
    }

    private sealed class ConversationTurn(IReadOnlyList<ChatMessage> messages, int ordinal)
    {
        public IReadOnlyList<ChatMessage> Messages { get; } = messages;

        public int Ordinal { get; } = ordinal;
    }

    private sealed class TurnSummary(
        IReadOnlyList<ChatMessage> messages,
        MemoryEntry memory,
        bool hasSummary)
    {
        public IReadOnlyList<ChatMessage> Messages { get; } = messages;

        public MemoryEntry Memory { get; } = memory;

        public bool HasSummary { get; } = hasSummary;
    }

    private sealed class ActiveTurn(
        IReadOnlyList<ChatMessage> messages,
        MemoryEntry? memory,
        int ordinal,
        bool isVerbatim)
    {
        public IReadOnlyList<ChatMessage> Messages { get; } = messages;

        public MemoryEntry? Memory { get; } = memory;

        public int Ordinal { get; } = ordinal;

        public bool IsVerbatim { get; } = isVerbatim;
    }
}
