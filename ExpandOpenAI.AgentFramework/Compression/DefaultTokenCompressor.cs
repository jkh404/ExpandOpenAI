using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.AgentFramework;

/// <summary>
/// 按用户轮次压缩历史：保留最近轮次、逐轮摘要近期历史，并将更早摘要移入会话记忆。
/// </summary>
public sealed class DefaultTokenCompressor : ITokenCompressor
{
    private const string SummaryMarkerKey = "expandopenai.agent.turn_summary";
    private const string MemoryIdKey = "expandopenai.agent.memory_id";
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

        return turns.Count > _options.RecentVerbatimTurnCount + _options.RecentSummaryTurnCount
            || _tokenEstimator(messages) > _options.MaximumHistoryTokenEstimate
            || turns.Any(turn =>
                !IsExistingSummary(turn)
                && _tokenEstimator(turn.Messages) > _options.MaximumVerbatimTurnTokenEstimate);
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
        var turns = SplitTurns(context.Messages);
        if (turns.Count == 0)
        {
            return new TokenCompressionResult(Array.Empty<ChatMessage>());
        }

        var activeTurns = new List<ActiveTurn>();
        var archivedMemories = new List<MemoryEntry>();
        var verbatimStart = Math.Max(0, turns.Count - _options.RecentVerbatimTurnCount);
        var summaryStart = Math.Max(0, verbatimStart - _options.RecentSummaryTurnCount);

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
                    && _tokenEstimator(turn.Messages) > _options.MaximumVerbatimTurnTokenEstimate)
                || (context.Reason == TokenCompressionReason.ContextLengthExceeded
                    && turns.Count == 1
                    && !IsExistingSummary(turn));

            if (mustSummarize)
            {
                var summary = await SummarizeTurnAsync(turn, chatClient, cancellationToken).ConfigureAwait(false);
                activeTurns.Add(new ActiveTurn(
                    [summary.Message],
                    summary.Memory,
                    turn.Ordinal,
                    isVerbatim: false));
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
                [summary.Message],
                summary.Memory,
                activeTurns[0].Ordinal,
                isVerbatim: false);
        }

        return new TokenCompressionResult(
            Flatten(activeTurns).AsReadOnly(),
            archivedMemories.AsReadOnly());
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
        var response = await chatClient.GetResponseAsync(
        [
            new ChatMessage(ChatRole.System, _options.SummaryPrompt),
            new ChatMessage(ChatRole.User, FormatTurn(turn.Messages)),
        ],
        new ChatOptions
        {
            MaxOutputTokens = _options.SummaryMaxOutputTokens,
            Temperature = 0,
        },
        cancellationToken).ConfigureAwait(false);
        var summaryText = (response.Text ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(summaryText))
        {
            throw new InvalidOperationException("默认 TokenCompressor 未能生成有效的轮次摘要。");
        }

        var createdAt = turn.Messages
            .Select(static message => message.CreatedAt)
            .FirstOrDefault(static value => value.HasValue)
            ?? DateTimeOffset.UtcNow;
        var memory = new MemoryEntry(
            memoryId,
            summaryText,
            createdAt,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["kind"] = "conversation-turn-summary",
            });
        var message = new ChatMessage(ChatRole.User, $"[Earlier conversation turn summary]\n{summaryText}")
        {
            CreatedAt = createdAt,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [SummaryMarkerKey] = true,
                [MemoryIdKey] = memoryId,
            },
        };

        return new TurnSummary(message, memory);
    }

    private static bool TryGetExistingSummary(ConversationTurn turn, out TurnSummary summary)
    {
        if (turn.Messages.Count == 1
            && turn.Messages[0].AdditionalProperties is { } properties
            && properties.TryGetValue(SummaryMarkerKey, out var marker)
            && marker is true)
        {
            var message = CloneMessage(turn.Messages[0]);
            var content = message.Text ?? string.Empty;
            const string prefix = "[Earlier conversation turn summary]\n";
            if (content.StartsWith(prefix, StringComparison.Ordinal))
            {
                content = content.Substring(prefix.Length);
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                summary = null!;
                return false;
            }

            var memoryId = properties.TryGetValue(MemoryIdKey, out var value) && value is string id
                ? id
                : CreateMemoryId(turn.Messages, turn.Ordinal);
            var memory = new MemoryEntry(
                memoryId,
                content,
                message.CreatedAt,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["kind"] = "conversation-turn-summary",
                });
            summary = new TurnSummary(message, memory);
            return true;
        }

        summary = null!;
        return false;
    }

    private static bool IsExistingSummary(ConversationTurn turn)
    {
        return turn.Messages.Count == 1
            && turn.Messages[0].AdditionalProperties is { } properties
            && properties.TryGetValue(SummaryMarkerKey, out var marker)
            && marker is true;
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
                            .Append(" arguments=")
                            .AppendLine(SerializeValue(call.Arguments));
                        break;
                    case FunctionResultContent result:
                        builder
                            .Append("Function result: ")
                            .AppendLine(SerializeValue(result.Result));
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
            || options.MaximumVerbatimTurnTokenEstimate <= 0
            || options.SummaryMaxOutputTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Token 限制必须大于 0。");
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

    private sealed class TurnSummary(ChatMessage message, MemoryEntry memory)
    {
        public ChatMessage Message { get; } = message;

        public MemoryEntry Memory { get; } = memory;
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
