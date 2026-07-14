using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExpandOpenAI.AgentBase;

/// <summary>
/// 一段独立的 Agent 对话会话。单个会话不允许并发运行。
/// </summary>
public sealed class AgentSession
{
    private readonly AIAgent _agent;
    private readonly List<ChatMessage> _history;
    private readonly object _historySync = new object();
    private readonly SemaphoreSlim _runGate = new SemaphoreSlim(1, 1);

    internal AgentSession(AIAgent agent, IEnumerable<ChatMessage>? initialHistory)
    {
        _agent = agent;
        _history = initialHistory is null
            ? new List<ChatMessage>()
            : CloneMessages(initialHistory);
    }

    /// <summary>
    /// 获取当前历史的只读快照。
    /// </summary>
    public IReadOnlyList<ChatMessage> History
    {
        get
        {
            lock (_historySync)
            {
                return new ReadOnlyCollection<ChatMessage>(CloneMessages(_history));
            }
        }
    }

    /// <summary>
    /// 清空会话历史。会话运行期间调用会抛出异常。
    /// </summary>
    public void ClearHistory()
    {
        if (!_runGate.Wait(0))
        {
            throw new InvalidOperationException("AgentSession 正在运行，不能清空历史。");
        }

        try
        {
            lock (_historySync)
            {
                _history.Clear();
            }
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>
    /// 执行一次非流式对话。
    /// </summary>
    public Task<ChatResponse> RunAsync(
        string message,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        return RunAsync(
            new ChatMessage(ChatRole.User, message),
            chatOptions,
            cancellationToken);
    }

    /// <summary>
    /// 执行一次非流式对话。
    /// </summary>
    public async Task<ChatResponse> RunAsync(
        ChatMessage userMessage,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
    {
        ValidateUserMessage(userMessage);
        await EnterRunAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var runContext = await CreateRunContextAsync(userMessage, cancellationToken).ConfigureAwait(false);
            var attemptHistory = CreateHistorySnapshot(runContext.SystemPromptMessage);
            attemptHistory = await ApplyConfiguredCompressionAsync(
                attemptHistory,
                runContext.ModelUserMessage,
                cancellationToken).ConfigureAwait(false);

            var contextLengthRetried = false;

            while (true)
            {
                var effectiveOptions = _agent.Options.CreateChatOptions(chatOptions);
                var toolTracker = new ToolInvocationTracker();
                var effectiveClient = CreateEffectiveChatClient(effectiveOptions, toolTracker);
                var preparedMessages = BuildPreparedMessages(attemptHistory, runContext.ModelUserMessage);

                try
                {
                    var response = await effectiveClient.GetResponseAsync(
                        preparedMessages,
                        effectiveOptions,
                        cancellationToken).ConfigureAwait(false);

                    await CommitSuccessfulRunAsync(
                        attemptHistory,
                        runContext.HistoryUserMessage,
                        response.Messages,
                        cancellationToken).ConfigureAwait(false);

                    return response;
                }
                catch (Exception exception) when (
                    !contextLengthRetried
                    && !toolTracker.ExecutionStarted
                    && CanForceCompress(attemptHistory, exception))
                {
                    contextLengthRetried = true;
                    attemptHistory = await CompressHistoryAsync(attemptHistory, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>
    /// 执行一次流式对话。只有完整枚举结束后才会提交历史；中止枚举不会保存部分响应。
    /// </summary>
    public IAsyncEnumerable<ChatResponseUpdate> RunStreamAsync(
        string message,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        return RunStreamAsync(
            new ChatMessage(ChatRole.User, message),
            chatOptions,
            cancellationToken);
    }

    /// <summary>
    /// 执行一次流式对话。只有完整枚举结束后才会提交历史；中止枚举不会保存部分响应。
    /// </summary>
    public async IAsyncEnumerable<ChatResponseUpdate> RunStreamAsync(
        ChatMessage userMessage,
        ChatOptions? chatOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateUserMessage(userMessage);
        await EnterRunAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var runContext = await CreateRunContextAsync(userMessage, cancellationToken).ConfigureAwait(false);
            var attemptHistory = CreateHistorySnapshot(runContext.SystemPromptMessage);
            attemptHistory = await ApplyConfiguredCompressionAsync(
                attemptHistory,
                runContext.ModelUserMessage,
                cancellationToken).ConfigureAwait(false);

            var contextLengthRetried = false;

            while (true)
            {
                var effectiveOptions = _agent.Options.CreateChatOptions(chatOptions);
                var toolTracker = new ToolInvocationTracker();
                var effectiveClient = CreateEffectiveChatClient(effectiveOptions, toolTracker);
                var preparedMessages = BuildPreparedMessages(attemptHistory, runContext.ModelUserMessage);
                var updates = new List<ChatResponseUpdate>();
                var hasYieldedUpdate = false;
                var retry = false;

                await using var enumerator = effectiveClient
                    .GetStreamingResponseAsync(preparedMessages, effectiveOptions, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken);

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
                    catch (Exception exception) when (
                        !hasYieldedUpdate
                        && !contextLengthRetried
                        && !toolTracker.ExecutionStarted
                        && CanForceCompress(attemptHistory, exception))
                    {
                        contextLengthRetried = true;
                        attemptHistory = await CompressHistoryAsync(attemptHistory, cancellationToken).ConfigureAwait(false);
                        retry = true;
                        break;
                    }

                    hasYieldedUpdate = true;
                    updates.Add(update);
                    yield return update;
                }

                if (retry)
                {
                    continue;
                }

                await CommitSuccessfulRunAsync(
                    attemptHistory,
                    runContext.HistoryUserMessage,
                    updates.ToChatResponse().Messages,
                    cancellationToken).ConfigureAwait(false);
                yield break;
            }
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task EnterRunAsync(CancellationToken cancellationToken)
    {
        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("同一个 AgentSession 不支持并发运行。请为并发对话创建独立会话。");
        }
    }

    private async Task<RunContext> CreateRunContextAsync(
        ChatMessage userMessage,
        CancellationToken cancellationToken)
    {
        var systemPrompt = SystemPromptTemplateEngine.Render(_agent.Options);
        var systemPromptMessage = string.IsNullOrWhiteSpace(systemPrompt)
            ? null
            : new ChatMessage(ChatRole.System, systemPrompt);
        var originalUserMessage = CloneMessage(userMessage);
        var handler = _agent.Options.MessageHandler;

        if (handler is null)
        {
            return new RunContext(
                systemPromptMessage,
                CloneMessage(originalUserMessage),
                CloneMessage(originalUserMessage));
        }

        var modelUserMessage = await handler.PrepareUserForModelAsync(
            CloneMessage(originalUserMessage),
            cancellationToken).ConfigureAwait(false);

        if (modelUserMessage is null)
        {
            throw new InvalidOperationException("IAgentMessageHandler.PrepareUserForModelAsync 不能返回 null。");
        }

        var historyUserMessage = await handler.PrepareUserForHistoryAsync(
            CloneMessage(originalUserMessage),
            CloneMessage(modelUserMessage),
            cancellationToken).ConfigureAwait(false);

        return new RunContext(
            systemPromptMessage,
            CloneMessage(modelUserMessage),
            historyUserMessage is null ? null : CloneMessage(historyUserMessage));
    }

    private List<ChatMessage> CreateHistorySnapshot(ChatMessage? systemPromptMessage)
    {
        List<ChatMessage> history;
        lock (_historySync)
        {
            history = CloneMessages(_history);
        }

        history.RemoveAll(static message => message.Role == ChatRole.System);

        if (systemPromptMessage is not null)
        {
            history.Insert(0, CloneMessage(systemPromptMessage));
        }

        return history;
    }

    private async Task<List<ChatMessage>> ApplyConfiguredCompressionAsync(
        List<ChatMessage> attemptHistory,
        ChatMessage modelUserMessage,
        CancellationToken cancellationToken)
    {
        var shouldCompress = _agent.Options.ShouldCompressMessages;
        if (shouldCompress is null || _agent.Options.TokenCompressor is null || !HasCompressibleHistory(attemptHistory))
        {
            return attemptHistory;
        }

        var candidateMessages = BuildPreparedMessages(attemptHistory, modelUserMessage);
        if (!shouldCompress(new ReadOnlyCollection<ChatMessage>(CloneMessages(candidateMessages))))
        {
            return attemptHistory;
        }

        return await CompressHistoryAsync(attemptHistory, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<ChatMessage>> CompressHistoryAsync(
        List<ChatMessage> attemptHistory,
        CancellationToken cancellationToken)
    {
        var compressor = _agent.Options.TokenCompressor
            ?? throw new InvalidOperationException("未配置 TokenCompressor，无法压缩历史。");
        var systemPrompt = attemptHistory.FirstOrDefault(static message => message.Role == ChatRole.System);
        var messagesToCompress = attemptHistory
            .Where(static message => message.Role != ChatRole.System)
            .Select(CloneMessage)
            .ToList()
            .AsReadOnly();
        var compressed = await compressor.CompressAsync(
            messagesToCompress,
            _agent.ChatClient,
            cancellationToken).ConfigureAwait(false);

        if (compressed is null)
        {
            throw new InvalidOperationException("ITokenCompressor.CompressAsync 不能返回 null。");
        }

        if (compressed.Any(static message => message.Role == ChatRole.System))
        {
            throw new InvalidOperationException("ITokenCompressor 返回的结果不能包含 System 消息。");
        }

        var result = new List<ChatMessage>(compressed.Count + (systemPrompt is null ? 0 : 1));
        if (systemPrompt is not null)
        {
            result.Add(CloneMessage(systemPrompt));
        }

        result.AddRange(CloneMessages(compressed));
        return result;
    }

    private bool CanForceCompress(List<ChatMessage> attemptHistory, Exception exception)
    {
        return _agent.Options.TokenCompressor is not null
            && HasCompressibleHistory(attemptHistory)
            && (_agent.Options.ContextLengthExceededDetector?.Invoke(exception)
                ?? IsContextLengthExceededException(exception));
    }

    private static bool HasCompressibleHistory(IReadOnlyList<ChatMessage> history)
    {
        return history.Any(static message => message.Role != ChatRole.System);
    }

    private async Task CommitSuccessfulRunAsync(
        List<ChatMessage> attemptHistory,
        ChatMessage? historyUserMessage,
        IEnumerable<ChatMessage> responseMessages,
        CancellationToken cancellationToken)
    {
        var committedHistory = CloneMessages(attemptHistory);
        if (historyUserMessage is not null)
        {
            committedHistory.Add(CloneMessage(historyUserMessage));
        }

        var handler = _agent.Options.MessageHandler;
        foreach (var responseMessage in responseMessages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (responseMessage.Role == ChatRole.System)
            {
                continue;
            }

            if (handler is not null && responseMessage.Role == ChatRole.Assistant)
            {
                var historyMessage = await handler.PrepareAssistantForHistoryAsync(
                    CloneMessage(responseMessage),
                    cancellationToken).ConfigureAwait(false);
                if (historyMessage is not null)
                {
                    committedHistory.Add(CloneMessage(historyMessage));
                }

                continue;
            }

            committedHistory.Add(CloneMessage(responseMessage));
        }

        lock (_historySync)
        {
            _history.Clear();
            _history.AddRange(committedHistory);
        }
    }

    private IChatClient CreateEffectiveChatClient(
        ChatOptions? chatOptions,
        ToolInvocationTracker toolTracker)
    {
        if (chatOptions?.Tools is not { Count: > 0 })
        {
            return _agent.ChatClient;
        }

        return new FunctionInvokingChatClient(_agent.ChatClient, NullLoggerFactory.Instance, null)
        {
            FunctionInvoker = async (context, cancellationToken) =>
            {
                if (!await _agent.Options.ToolApprovalAsync(context, cancellationToken).ConfigureAwait(false))
                {
                    return $"Tool '{context.Function.Name}' execution was denied by ToolApprovalAsync.";
                }

                toolTracker.MarkExecutionStarted();
                return await context.Function.InvokeAsync(context.Arguments, cancellationToken).ConfigureAwait(false);
            },
        };
    }

    private static List<ChatMessage> BuildPreparedMessages(
        IEnumerable<ChatMessage> history,
        ChatMessage modelUserMessage)
    {
        var messages = CloneMessages(history);
        messages.Add(CloneMessage(modelUserMessage));
        return messages;
    }

    private static void ValidateUserMessage(ChatMessage userMessage)
    {
        if (userMessage is null)
        {
            throw new ArgumentNullException(nameof(userMessage));
        }

        if (userMessage.Role != ChatRole.User)
        {
            throw new ArgumentException("AgentSession 只接受 User 角色的输入消息。", nameof(userMessage));
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

    private sealed class ToolInvocationTracker
    {
        private int _executionStarted;

        public bool ExecutionStarted => Volatile.Read(ref _executionStarted) != 0;

        public void MarkExecutionStarted()
        {
            Interlocked.Exchange(ref _executionStarted, 1);
        }
    }

    private sealed class RunContext(
        ChatMessage? systemPromptMessage,
        ChatMessage modelUserMessage,
        ChatMessage? historyUserMessage)
    {
        public ChatMessage? SystemPromptMessage { get; } = systemPromptMessage;

        public ChatMessage ModelUserMessage { get; } = modelUserMessage;

        public ChatMessage? HistoryUserMessage { get; } = historyUserMessage;
    }
}
