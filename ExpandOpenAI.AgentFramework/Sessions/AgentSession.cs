using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExpandOpenAI.AgentFramework;

/// <summary>
/// 一段独立的 Agent 对话会话。单个会话不允许并发运行。
/// </summary>
public sealed class AgentSession : IAgentSession
{
    private readonly AIAgent _agent;
    private readonly AgentMemory _memory;
    private readonly List<ChatMessage> _history;
    private readonly object _historySync = new object();
    private readonly SemaphoreSlim _runGate = new SemaphoreSlim(1, 1);
    private int _destroyed;

    internal AgentSession(AIAgent agent, IEnumerable<ChatMessage>? initialHistory)
    {
        _agent = agent;
        _memory = new AgentMemory(agent.Options);
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
            ThrowIfDestroyed();
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
        ThrowIfDestroyed();
        if (!_runGate.Wait(0))
        {
            throw new InvalidOperationException("AgentSession 正在运行，不能清空历史。");
        }

        try
        {
            ThrowIfDestroyed();
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
    /// 清空当前会话层记忆，不影响全局记忆。
    /// </summary>
    public async ValueTask ClearMemoryAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDestroyed();
        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("AgentSession 正在运行，不能清空会话记忆。");
        }

        try
        {
            ThrowIfDestroyed();
            await _memory.ClearSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>
    /// 销毁当前会话，清空历史和会话层记忆。
    /// </summary>
    public async ValueTask DestroyAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _destroyed) != 0)
        {
            return;
        }

        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("AgentSession 正在运行，不能销毁会话。");
        }

        try
        {
            if (Volatile.Read(ref _destroyed) != 0)
            {
                return;
            }

            await _memory.ClearSessionAsync(cancellationToken).ConfigureAwait(false);
            lock (_historySync)
            {
                _history.Clear();
            }

            Volatile.Write(ref _destroyed, 1);
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
                var effectiveOptions = CreateEffectiveChatOptions(chatOptions);
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
                    attemptHistory = await CompressHistoryAsync(
                        attemptHistory,
                        TokenCompressionReason.ContextLengthExceeded,
                        cancellationToken).ConfigureAwait(false);
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
                var effectiveOptions = CreateEffectiveChatOptions(chatOptions);
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
                        attemptHistory = await CompressHistoryAsync(
                            attemptHistory,
                            TokenCompressionReason.ContextLengthExceeded,
                            cancellationToken).ConfigureAwait(false);
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
        ThrowIfDestroyed();
        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("同一个 AgentSession 不支持并发运行。请为并发对话创建独立会话。");
        }

        if (Volatile.Read(ref _destroyed) != 0)
        {
            _runGate.Release();
            ThrowIfDestroyed();
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

        if (systemPromptMessage is not null
            && !history.Any(message =>
                message.Role == ChatRole.System
                && string.Equals(message.Text, systemPromptMessage.Text, StringComparison.Ordinal)))
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
        var compressor = _agent.Options.TokenCompressor;
        if (compressor is null || !HasCompressibleHistory(attemptHistory))
        {
            return attemptHistory;
        }

        var candidateMessages = BuildPreparedMessages(attemptHistory, modelUserMessage);
        var historyWithoutSystem = attemptHistory
            .Where(static message => message.Role != ChatRole.System)
            .Select(CloneMessage)
            .ToList()
            .AsReadOnly();
        var compressionRequired = shouldCompress is null
            ? compressor.ShouldCompress(historyWithoutSystem)
            : shouldCompress(new ReadOnlyCollection<ChatMessage>(CloneMessages(candidateMessages)));

        if (!compressionRequired)
        {
            return attemptHistory;
        }

        return await CompressHistoryAsync(
            attemptHistory,
            TokenCompressionReason.Configured,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<ChatMessage>> CompressHistoryAsync(
        List<ChatMessage> attemptHistory,
        TokenCompressionReason reason,
        CancellationToken cancellationToken)
    {
        var compressor = _agent.Options.TokenCompressor
            ?? throw new InvalidOperationException("未配置 TokenCompressor，无法压缩历史。");
        var systemMessages = attemptHistory
            .Where(static message => message.Role == ChatRole.System)
            .Select(CloneMessage)
            .ToList();
        var messagesToCompress = attemptHistory
            .Where(static message => message.Role != ChatRole.System)
            .Select(CloneMessage)
            .ToList()
            .AsReadOnly();
        var compressed = await compressor.CompressAsync(
            new TokenCompressionContext(messagesToCompress, reason),
            _agent.ChatClient,
            cancellationToken).ConfigureAwait(false);

        if (compressed is null)
        {
            throw new InvalidOperationException("ITokenCompressor.CompressAsync 不能返回 null。");
        }

        if (compressed.Messages is null)
        {
            throw new InvalidOperationException("ITokenCompressor 返回的 Messages 不能为 null。");
        }

        if (compressed.Messages.Any(static message => message is null))
        {
            throw new InvalidOperationException("ITokenCompressor 返回的 Messages 不能包含 null。");
        }

        if (compressed.Messages.Any(static message => message.Role == ChatRole.System))
        {
            throw new InvalidOperationException("ITokenCompressor 返回的结果不能包含 System 消息。");
        }

        await _memory.StoreAsync(compressed, cancellationToken).ConfigureAwait(false);

        var result = new List<ChatMessage>(compressed.Messages.Count + systemMessages.Count);
        result.AddRange(systemMessages);
        result.AddRange(CloneMessages(compressed.Messages));
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
            IncludeDetailedErrors = true,
            FunctionInvoker = async (context, cancellationToken) =>
            {
                if (!TryNormalizeFunctionArguments(context.Function, context.Arguments, out var argumentError))
                {
                    return argumentError;
                }

                if (ReferenceEquals(context.Function, _memory.RecallTool))
                {
                    return await context.Function.InvokeAsync(
                        context.Arguments,
                        cancellationToken).ConfigureAwait(false);
                }

                if (!await _agent.Options.ToolApprovalAsync(context, cancellationToken).ConfigureAwait(false))
                {
                    return $"Tool '{context.Function.Name}' execution was denied by ToolApprovalAsync.";
                }

                toolTracker.MarkExecutionStarted();
                return await context.Function.InvokeAsync(context.Arguments, cancellationToken).ConfigureAwait(false);
            },
        };
    }

    private static bool TryNormalizeFunctionArguments(
        AIFunction function,
        AIFunctionArguments arguments,
        out string? error)
    {
        error = null;
        if (arguments.Count == 1 && arguments.TryGetValue("$raw", out var rawValue))
        {
            var raw = rawValue switch
            {
                string text => text,
                JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
                JsonElement element => element.GetRawText(),
                _ => rawValue?.ToString(),
            };
            var extracted = JsonRepairer.ExtractJsonText(raw);
            if (extracted is null)
            {
                error = "工具参数 JSON 无效或不完整，工具未执行。请缩短单次参数内容，确保 JSON 完整后重试。";
                return false;
            }

            Dictionary<string, object?>? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                    extracted,
                    function.JsonSerializerOptions);
            }
            catch (JsonException)
            {
                error = "工具参数 JSON 无法解析，工具未执行。请检查参数名称、引号和转义后重试。";
                return false;
            }

            arguments.Clear();
            foreach (var pair in parsed ?? [])
            {
                arguments[pair.Key] = pair.Value;
            }
        }

        NormalizeArgumentNames(function, arguments);
        return true;
    }

    private static void NormalizeArgumentNames(AIFunction function, AIFunctionArguments arguments)
    {
        var schema = function.JsonSchema;
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var canonicalNames = properties.EnumerateObject()
            .Select(static property => property.Name)
            .ToList();
        foreach (var suppliedName in arguments.Keys.ToList())
        {
            if (canonicalNames.Contains(suppliedName, StringComparer.Ordinal))
            {
                continue;
            }

            var normalizedSuppliedName = NormalizeArgumentName(suppliedName);
            var canonicalName = canonicalNames.FirstOrDefault(name =>
                string.Equals(
                    NormalizeArgumentName(name),
                    normalizedSuppliedName,
                    StringComparison.Ordinal));
            if (canonicalName is null || arguments.ContainsKey(canonicalName))
            {
                continue;
            }

            arguments[canonicalName] = arguments[suppliedName];
            arguments.Remove(suppliedName);
        }
    }

    private static string NormalizeArgumentName(string name)
    {
        return new string(name
            .Where(static character => char.IsLetterOrDigit(character))
            .Select(static character => char.ToLowerInvariant(character))
            .ToArray());
    }

    private ChatOptions? CreateEffectiveChatOptions(ChatOptions? runOptions)
    {
        var options = _agent.Options.CreateChatOptions(runOptions);
        if (!_agent.Options.EnableMemoryRecallTool)
        {
            return options;
        }

        options ??= new ChatOptions();
        options.Tools ??= new List<AITool>();

        var existing = options.Tools.FirstOrDefault(tool =>
            string.Equals(tool.Name, _memory.RecallTool.Name, StringComparison.Ordinal));
        if (existing is not null && !ReferenceEquals(existing, _memory.RecallTool))
        {
            throw new InvalidOperationException(
                $"工具名称 '{_memory.RecallTool.Name}' 由 AgentFramework 内置记忆工具保留。");
        }

        if (existing is null)
        {
            options.Tools.Add(_memory.RecallTool);
        }

        return options;
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

    private void ThrowIfDestroyed()
    {
        if (Volatile.Read(ref _destroyed) != 0)
        {
            throw new ObjectDisposedException(nameof(AgentSession), "AgentSession 已被销毁。");
        }
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
