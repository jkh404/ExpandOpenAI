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
    private readonly ContextCompactionTool _contextCompactionTool;
    private readonly List<ChatMessage> _history;
    private readonly object _historySync = new object();
    private readonly SemaphoreSlim _runGate = new SemaphoreSlim(1, 1);
    private int _destroyed;

    internal AgentSession(AIAgent agent, IEnumerable<ChatMessage>? initialHistory)
    {
        _agent = agent;
        _memory = new AgentMemory(agent.Options);
        _contextCompactionTool = new ContextCompactionTool();
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
            var compactionTracker = new ContextCompactionTracker();
            TokenCompressionResult? pendingModelRequestedMemory = null;
            UsageDetails? usageBeforeCompaction = null;
            var workingMessages = BuildPreparedMessages(attemptHistory, runContext.ModelUserMessage);

            while (true)
            {
                var effectiveOptions = CreateEffectiveChatOptions(chatOptions, compactionTracker);
                var toolTracker = new ToolInvocationTracker();
                compactionTracker.BeginAttempt(workingMessages);
                var effectiveClient = CreateEffectiveChatClient(
                    effectiveOptions,
                    toolTracker,
                    compactionTracker);

                try
                {
                    var response = await effectiveClient.GetResponseAsync(
                        workingMessages,
                        effectiveOptions,
                        cancellationToken).ConfigureAwait(false);

                    if (compactionTracker.PendingRequest is { } request)
                    {
                        usageBeforeCompaction = AddUsage(usageBeforeCompaction, response.Usage);
                        var outcome = await ApplyModelRequestedCompactionAsync(
                            request,
                            cancellationToken).ConfigureAwait(false);
                        workingMessages = outcome.Messages;
                        pendingModelRequestedMemory = outcome.Compression;
                        compactionTracker.MarkApplied();
                        contextLengthRetried = true;
                        continue;
                    }

                    await CommitSuccessfulRunAsync(
                        workingMessages,
                        runContext.ModelUserMessage,
                        runContext.HistoryUserMessage,
                        response.Messages,
                        pendingModelRequestedMemory,
                        cancellationToken).ConfigureAwait(false);

                    response.Usage = AddUsage(usageBeforeCompaction, response.Usage);

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
                    workingMessages = BuildPreparedMessages(attemptHistory, runContext.ModelUserMessage);
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
            var compactionTracker = new ContextCompactionTracker();
            TokenCompressionResult? pendingModelRequestedMemory = null;
            var workingMessages = BuildPreparedMessages(attemptHistory, runContext.ModelUserMessage);

            while (true)
            {
                var effectiveOptions = CreateEffectiveChatOptions(chatOptions, compactionTracker);
                var toolTracker = new ToolInvocationTracker();
                compactionTracker.BeginAttempt(workingMessages);
                var effectiveClient = CreateEffectiveChatClient(
                    effectiveOptions,
                    toolTracker,
                    compactionTracker);
                var historyUpdates = new List<ChatResponseUpdate>();
                var hasYieldedUpdate = false;
                var retry = false;

                await using var enumerator = effectiveClient
                    .GetStreamingResponseAsync(workingMessages, effectiveOptions, cancellationToken)
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
                        workingMessages = BuildPreparedMessages(attemptHistory, runContext.ModelUserMessage);
                        retry = true;
                        break;
                    }

                    historyUpdates.Add(update);
                    var visibleUpdate = compactionTracker.CreateVisibleUpdate(update);
                    if (visibleUpdate is null)
                    {
                        continue;
                    }

                    hasYieldedUpdate = true;
                    compactionTracker.ObserveVisibleUpdate(visibleUpdate);
                    yield return visibleUpdate;
                }

                if (retry)
                {
                    continue;
                }

                if (compactionTracker.PendingRequest is { } request)
                {
                    var outcome = await ApplyModelRequestedCompactionAsync(
                        request,
                        cancellationToken).ConfigureAwait(false);
                    workingMessages = outcome.Messages;
                    pendingModelRequestedMemory = outcome.Compression;
                    compactionTracker.MarkApplied();
                    contextLengthRetried = true;
                    continue;
                }

                await CommitSuccessfulRunAsync(
                    workingMessages,
                    runContext.ModelUserMessage,
                    runContext.HistoryUserMessage,
                    historyUpdates.ToChatResponse().Messages,
                    pendingModelRequestedMemory,
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
        var outcome = await CompressMessagesAsync(
            attemptHistory,
            reason,
            cancellationToken).ConfigureAwait(false);
        await _memory.StoreAsync(outcome.Compression, cancellationToken).ConfigureAwait(false);
        return outcome.Messages;
    }

    private async Task<CompressionOutcome> CompressMessagesAsync(
        IReadOnlyList<ChatMessage> messages,
        TokenCompressionReason reason,
        CancellationToken cancellationToken)
    {
        var compressor = _agent.Options.TokenCompressor
            ?? throw new InvalidOperationException("未配置 TokenCompressor，无法压缩历史。");
        var systemMessages = messages
            .Where(static message => message.Role == ChatRole.System)
            .Select(CloneMessage)
            .ToList();
        var messagesToCompress = messages
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

        var result = new List<ChatMessage>(compressed.Messages.Count + systemMessages.Count);
        result.AddRange(systemMessages);
        result.AddRange(CloneMessages(compressed.Messages));
        return new CompressionOutcome(result, compressed);
    }

    private async Task<CompressionOutcome> ApplyModelRequestedCompactionAsync(
        ContextCompactionRequest request,
        CancellationToken cancellationToken)
    {
        var messagesToCompress = RemoveContextCompactionCall(request);
        var outcome = await CompressMessagesAsync(
            messagesToCompress,
            TokenCompressionReason.ModelRequested,
            cancellationToken).ConfigureAwait(false);

        EnsureUserMessagesWerePreserved(messagesToCompress, outcome.Messages);
        outcome.Messages.Add(new ChatMessage(
            ChatRole.Assistant,
            [request.CallContent]));
        outcome.Messages.Add(new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent(
                request.CallContent.CallId,
                ContextCompactionToolResponse.AppliedRequest(request.Summary, request.Reason))]));
        return outcome;
    }

    private static List<ChatMessage> RemoveContextCompactionCall(ContextCompactionRequest request)
    {
        var result = new List<ChatMessage>(request.Messages.Count);
        var removedCallCount = 0;

        foreach (var message in request.Messages)
        {
            var contents = new List<AIContent>(message.Contents.Count);
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent call
                    && string.Equals(call.CallId, request.CallContent.CallId, StringComparison.Ordinal)
                    && string.Equals(call.Name, ContextCompactionTool.Name, StringComparison.Ordinal))
                {
                    removedCallCount++;
                    continue;
                }

                contents.Add(content);
            }

            if (contents.Count > 0)
            {
                result.Add(CloneMessage(message, contents));
            }
        }

        if (removedCallCount != 1)
        {
            throw new InvalidOperationException("未能在当前运行上下文中唯一定位主动压缩工具调用。");
        }

        return result;
    }

    private static void EnsureUserMessagesWerePreserved(
        IReadOnlyList<ChatMessage> source,
        IReadOnlyList<ChatMessage> compressed)
    {
        var compressedUsers = compressed
            .Where(static message => message.Role == ChatRole.User)
            .ToList();
        var searchStart = 0;

        foreach (var sourceUser in source.Where(static message => message.Role == ChatRole.User))
        {
            var match = -1;
            for (var index = searchStart; index < compressedUsers.Count; index++)
            {
                if (AreUserMessagesEquivalent(sourceUser, compressedUsers[index]))
                {
                    match = index;
                    break;
                }
            }

            if (match < 0)
            {
                throw new InvalidOperationException(
                    "ITokenCompressor 在模型主动压缩时必须原样、按原顺序保留所有 User 消息。");
            }

            searchStart = match + 1;
        }
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

    private static UsageDetails? AddUsage(UsageDetails? aggregate, UsageDetails? current)
    {
        if (current is null)
        {
            return aggregate;
        }

        if (aggregate is null)
        {
            return current;
        }

        aggregate.Add(current);
        return aggregate;
    }

    private async Task CommitSuccessfulRunAsync(
        IReadOnlyList<ChatMessage> workingMessages,
        ChatMessage modelUserMessage,
        ChatMessage? historyUserMessage,
        IEnumerable<ChatMessage> responseMessages,
        TokenCompressionResult? pendingModelRequestedMemory,
        CancellationToken cancellationToken)
    {
        var committedHistory = CloneMessages(workingMessages);
        var modelUserIndex = committedHistory.FindLastIndex(message =>
            message.Role == ChatRole.User
            && AreUserMessagesEquivalent(message, modelUserMessage));
        if (modelUserIndex < 0)
        {
            throw new InvalidOperationException("当前运行的 User 消息未被保留，无法提交会话历史。");
        }

        if (historyUserMessage is not null)
        {
            committedHistory[modelUserIndex] = CloneMessage(historyUserMessage);
        }
        else
        {
            committedHistory.RemoveAt(modelUserIndex);
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

        if (pendingModelRequestedMemory is not null)
        {
            await _memory.StoreAsync(
                pendingModelRequestedMemory,
                cancellationToken).ConfigureAwait(false);
        }

        lock (_historySync)
        {
            _history.Clear();
            _history.AddRange(committedHistory);
        }
    }

    private IChatClient CreateEffectiveChatClient(
        ChatOptions? chatOptions,
        ToolInvocationTracker toolTracker,
        ContextCompactionTracker compactionTracker)
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

                if (ReferenceEquals(context.Function, _contextCompactionTool.Function))
                {
                    return compactionTracker.HandleInvocation(context);
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

    private ChatOptions? CreateEffectiveChatOptions(
        ChatOptions? runOptions,
        ContextCompactionTracker compactionTracker)
    {
        var options = _agent.Options.CreateChatOptions(runOptions);
        var includeMemoryRecall = _agent.Options.EnableMemoryRecallTool;
        var includeContextCompaction = _agent.Options.EnableContextCompactionTool
            && _agent.Options.TokenCompressor is not null
            && !compactionTracker.HasApplied;
        if (!includeMemoryRecall && !includeContextCompaction)
        {
            return options;
        }

        options ??= new ChatOptions();
        options.Tools ??= new List<AITool>();

        if (includeMemoryRecall)
        {
            AddBuiltInTool(options.Tools, _memory.RecallTool, "记忆工具");
        }

        if (includeContextCompaction)
        {
            AddBuiltInTool(options.Tools, _contextCompactionTool.Function, "上下文压缩工具");
        }

        return options;
    }

    private static void AddBuiltInTool(
        IList<AITool> tools,
        AIFunction builtInTool,
        string description)
    {
        var existing = tools.FirstOrDefault(tool =>
            string.Equals(tool.Name, builtInTool.Name, StringComparison.Ordinal));
        if (existing is not null && !ReferenceEquals(existing, builtInTool))
        {
            throw new InvalidOperationException(
                $"工具名称 '{builtInTool.Name}' 由 AgentFramework 内置{description}保留。");
        }

        if (existing is null)
        {
            tools.Add(builtInTool);
        }
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
        return CloneMessage(message, message.Contents.ToList());
    }

    private static ChatMessage CloneMessage(ChatMessage message, IList<AIContent> contents)
    {
        return new ChatMessage(message.Role, contents)
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

    private static bool AreUserMessagesEquivalent(ChatMessage left, ChatMessage right)
    {
        if (left.Role != ChatRole.User
            || right.Role != ChatRole.User
            || left.Contents.Count != right.Contents.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Contents.Count; index++)
        {
            var leftContent = left.Contents[index];
            var rightContent = right.Contents[index];
            if (ReferenceEquals(leftContent, rightContent))
            {
                continue;
            }

            if (leftContent.GetType() != rightContent.GetType()
                || !string.Equals(
                    SerializeContentForComparison(leftContent),
                    SerializeContentForComparison(rightContent),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string SerializeContentForComparison(AIContent content)
    {
        try
        {
            return JsonSerializer.Serialize(content, content.GetType());
        }
        catch (Exception exception) when (exception is NotSupportedException or JsonException)
        {
            return content.ToString() ?? string.Empty;
        }
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

    private sealed class ContextCompactionTracker
    {
        private const int MaximumSummaryLength = 8_000;
        private const int MaximumReasonLength = 1_000;
        private readonly HashSet<string> _compactionCallIds = new HashSet<string>(StringComparer.Ordinal);
        private IReadOnlyList<ChatMessage> _attemptMessages = Array.Empty<ChatMessage>();
        private bool _visibleAssistantOutputStarted;

        public bool HasApplied { get; private set; }

        public ContextCompactionRequest? PendingRequest { get; private set; }

        public void BeginAttempt(IReadOnlyList<ChatMessage> messages)
        {
            _attemptMessages = messages;
            PendingRequest = null;
            _compactionCallIds.Clear();
        }

        public object HandleInvocation(FunctionInvocationContext context)
        {
            _compactionCallIds.Add(context.CallContent.CallId);

            if (HasApplied)
            {
                return ContextCompactionToolResponse.Rejected("当前运行已经执行过一次主动上下文压缩，请继续完成任务。");
            }

            if (PendingRequest is not null)
            {
                return ContextCompactionToolResponse.Rejected("当前已有待处理的上下文压缩请求。");
            }

            if (context.FunctionCount != 1)
            {
                return ContextCompactionToolResponse.Rejected(
                    "主动上下文压缩必须独占一次工具调用，请不要与其他工具并行调用。");
            }

            if (context.IsStreaming && _visibleAssistantOutputStarted)
            {
                return ContextCompactionToolResponse.Rejected(
                    "当前流式响应已经输出 Assistant 内容，不能再重写本轮上下文。请继续完成本轮回答。");
            }

            var summary = GetStringArgument(context.Arguments, "summary")?.Trim();
            if (summary is null || summary.Length == 0)
            {
                return ContextCompactionToolResponse.Rejected("summary 不能为空，请提供简洁的任务检查点摘要。");
            }

            if (summary.Length > MaximumSummaryLength)
            {
                return ContextCompactionToolResponse.Rejected(
                    $"summary 不能超过 {MaximumSummaryLength} 个字符，请提炼任务相关关键信息后重试。");
            }

            var reason = GetStringArgument(context.Arguments, "reason")?.Trim();
            if (reason?.Length > MaximumReasonLength)
            {
                return ContextCompactionToolResponse.Rejected(
                    $"reason 不能超过 {MaximumReasonLength} 个字符。");
            }

            PendingRequest = new ContextCompactionRequest(
                CaptureInvocationMessages(context.Messages),
                context.CallContent,
                summary,
                string.IsNullOrWhiteSpace(reason) ? null : reason);
            context.Terminate = true;
            return ContextCompactionToolResponse.AcceptedRequest(summary, reason);
        }

        public ChatResponseUpdate? CreateVisibleUpdate(ChatResponseUpdate update)
        {
            var filteredContents = new List<AIContent>(update.Contents.Count);
            var removedInternalContent = false;

            foreach (var content in update.Contents)
            {
                if (content is FunctionCallContent call
                    && string.Equals(call.Name, ContextCompactionTool.Name, StringComparison.Ordinal))
                {
                    _compactionCallIds.Add(call.CallId);
                    removedInternalContent = true;
                    continue;
                }

                if (content is FunctionResultContent result
                    && _compactionCallIds.Contains(result.CallId))
                {
                    removedInternalContent = true;
                    continue;
                }

                filteredContents.Add(content);
            }

            if (!removedInternalContent)
            {
                return update;
            }

            if (filteredContents.Count == 0)
            {
                return null;
            }

            return new ChatResponseUpdate
            {
                AdditionalProperties = update.AdditionalProperties,
                AuthorName = update.AuthorName,
                Contents = filteredContents,
                ConversationId = update.ConversationId,
                ContinuationToken = update.ContinuationToken,
                CreatedAt = update.CreatedAt,
                FinishReason = update.FinishReason,
                MessageId = update.MessageId,
                ModelId = update.ModelId,
                RawRepresentation = update.RawRepresentation,
                ResponseId = update.ResponseId,
                Role = update.Role,
            };
        }

        public void ObserveVisibleUpdate(ChatResponseUpdate update)
        {
            if (update.Role == ChatRole.Assistant
                && update.Contents.Any(static content =>
                    content is not FunctionCallContent and not UsageContent))
            {
                _visibleAssistantOutputStarted = true;
            }
        }

        public void MarkApplied()
        {
            HasApplied = true;
            PendingRequest = null;
        }

        private IReadOnlyList<ChatMessage> CaptureInvocationMessages(IList<ChatMessage> invocationMessages)
        {
            var invocationContainsAttempt = invocationMessages.Count >= _attemptMessages.Count;
            for (var index = 0; invocationContainsAttempt && index < _attemptMessages.Count; index++)
            {
                invocationContainsAttempt = ReferenceEquals(invocationMessages[index], _attemptMessages[index]);
            }

            if (invocationContainsAttempt)
            {
                return CloneMessages(invocationMessages).AsReadOnly();
            }

            var combined = CloneMessages(_attemptMessages);
            combined.AddRange(CloneMessages(invocationMessages));
            return combined.AsReadOnly();
        }

        private static string? GetStringArgument(AIFunctionArguments arguments, string name)
        {
            if (!arguments.TryGetValue(name, out var value))
            {
                return null;
            }

            return value switch
            {
                null => null,
                string text => text,
                JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
                JsonElement element => element.GetRawText(),
                _ => value.ToString(),
            };
        }
    }

    private sealed class ContextCompactionRequest(
        IReadOnlyList<ChatMessage> messages,
        FunctionCallContent callContent,
        string summary,
        string? reason)
    {
        public IReadOnlyList<ChatMessage> Messages { get; } = messages;

        public FunctionCallContent CallContent { get; } = callContent;

        public string Summary { get; } = summary;

        public string? Reason { get; } = reason;
    }

    private sealed class CompressionOutcome(
        List<ChatMessage> messages,
        TokenCompressionResult compression)
    {
        public List<ChatMessage> Messages { get; } = messages;

        public TokenCompressionResult Compression { get; } = compression;
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
