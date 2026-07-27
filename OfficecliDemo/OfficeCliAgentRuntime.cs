using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using ExpandOpenAI;
using ExpandOpenAI.AgentFramework;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace OfficecliDemo;

/// <summary>
/// Owns one agent's MCP process, model client, session, compressor, memory and context journal.
/// A runtime instance is never shared between pipeline agents.
/// </summary>
internal sealed class OfficeCliAgentRuntime : IAsyncDisposable
{
    private readonly DemoOptions _options;
    private readonly string _agentName;
    private readonly string _memoryKind;
    private readonly McpClient _mcpClient;
    private readonly OpenAICompatibleChatClient _chatClient;
    private readonly IAgentSession _session;
    private readonly IMemoryUnit _memory;
    private readonly AgentContextJournal _contextJournal;
    private readonly OfficeCliToolApproval _toolApproval;
    private readonly ILogger _logger;
    private int _agentTaskCount;
    private bool _disposed;

    private OfficeCliAgentRuntime(
        DemoOptions options,
        string agentName,
        string memoryKind,
        McpClient mcpClient,
        OpenAICompatibleChatClient chatClient,
        IAgentSession session,
        IMemoryUnit memory,
        AgentContextJournal contextJournal,
        OfficeCliToolApproval toolApproval,
        ILogger logger)
    {
        _options = options;
        _agentName = agentName;
        _memoryKind = memoryKind;
        _mcpClient = mcpClient;
        _chatClient = chatClient;
        _session = session;
        _memory = memory;
        _contextJournal = contextJournal;
        _toolApproval = toolApproval;
        _logger = logger;
    }

    public string ContextFilePath => _contextJournal.FilePath;

    public static async Task<OfficeCliAgentRuntime> CreateAsync(
        DemoOptions options,
        string agentName,
        string systemPromptTemplate,
        IReadOnlyDictionary<string, JsonNode?> systemPromptTemplateValues,
        string contextFileName,
        string memoryKind,
        string messageSummaryPrompt,
        string summaryPrompt,
        ILogger logger,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPromptTemplate);
        ArgumentNullException.ThrowIfNull(systemPromptTemplateValues);
        ArgumentException.ThrowIfNullOrWhiteSpace(contextFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(memoryKind);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        logger.LogInformation(
            "[MCP][{AgentName}] 启动独立 officecli MCP：{Command} mcp",
            agentName,
            options.OfficeCliCommand);
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = options.OfficeCliCommand,
            Arguments = ["mcp"],
        });
        var mcpClient = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var mcpTools = await mcpClient.ListToolsAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var tools = mcpTools.Cast<AITool>().ToList();
            logger.LogInformation(
                "[MCP][{AgentName}] 工具加载完成：仅注册 officecli MCP 工具 {ToolCount} 个；工具：{ToolNames}",
                agentName,
                mcpTools.Count,
                string.Join(", ", tools.Select(static tool => tool.Name)));
            var approval = new OfficeCliToolApproval(
                mcpTools.Select(static tool => tool.Name),
                options.SourceDocumentPath,
                options.MaximumToolCalls,
                options.ShowToolArguments,
                options.MaximumLogTextLength,
                loggerFactory.CreateLogger<OfficeCliToolApproval>());

            IMemoryUnit memory = new LoggingMemoryUnit(
                new InMemoryMemoryUnit(),
                loggerFactory.CreateLogger<LoggingMemoryUnit>());
            var clientOptions = new OpenAICompatibleChatClientOptions
            {
                ModelId = options.ModelId,
                ApiKey = options.ApiKey,
                Endpoint = options.Endpoint,
                RequestPath = options.RequestPath,
                RequestBody = new Dictionary<string, object?>
                {
                    ["enable_thinking"] = options.EnableThinking,
                },
                ConfigureRequestBody = static (body, _, _, stream) =>
                {
                    if (stream)
                    {
                        body["stream_options"] = new JsonObject
                        {
                            ["include_usage"] = true,
                        };
                    }
                },
                RetryOptions = new OpenAICompatibleHttpRetryOptions
                {
                    MaxRetryAttempts = 4,
                    InitialDelay = TimeSpan.FromSeconds(1),
                    MaxDelay = TimeSpan.FromSeconds(10),
                },
            };
            var chatClient = new OpenAICompatibleChatClient(
                new HttpClientHandler(),
                clientOptions,
                timeout: options.RequestTimeout);
            var contextJournal = new AgentContextJournal(
                options.OutputDirectory,
                contextFileName,
                loggerFactory.CreateLogger<AgentContextJournal>());
            var speedLoggingChatClient = new TokenSpeedLoggingChatClient(
                chatClient,
                contextJournal,
                loggerFactory.CreateLogger<TokenSpeedLoggingChatClient>());
            var contextChatClient = new ContextSnapshotChatClient(
                speedLoggingChatClient,
                contextJournal);
            AIAgent agent = new DefaultAIAgent(contextChatClient, new AgentOptions
            {
                SystemPromptTemplate = systemPromptTemplate,
                SystemPromptTemplateValues = systemPromptTemplateValues,
                MissingTemplateValueBehavior = MissingTemplateValueBehavior.Throw,
                DefaultChatOptions = new ChatOptions
                {
                    Tools = tools,
                    ToolMode = ChatToolMode.Auto,
                    AllowMultipleToolCalls = false,
                    Temperature = 0,
                    MaxOutputTokens = 32_000,
                },
                TokenCompressor = new LoggingTokenCompressor(
                    new DefaultTokenCompressor(new DefaultTokenCompressorOptions
                    {
                        RecentVerbatimTurnCount = 1,
                        RecentSummaryTurnCount = options.RecentSummaryTurnCount,
                        MaximumHistoryTokenEstimate = options.MaximumHistoryTokenEstimate,
                        MaximumMessageTokenEstimate = options.MaximumMessageTokenEstimate,
                        SummaryMaxOutputTokens = options.SummaryMaxOutputTokens,
                        MessageSummaryPrompt = messageSummaryPrompt,
                        SummaryPrompt = summaryPrompt,
                    }),
                    options.MaximumHistoryTokenEstimate,
                    options.MaximumMessageTokenEstimate,
                    contextJournal,
                    loggerFactory.CreateLogger<LoggingTokenCompressor>()),
                SessionMemoryUnitFactory = () => memory,
                EnableMemoryRecallTool = true,
                MemoryRecallMaxResults = options.MemoryRecallMaxResults,
                EnableContextCompactionTool = options.EnableContextCompactionTool,
                ToolApprovalAsync = approval.ApproveAsync,
            });

            var session = agent.CreateSession();
            await contextJournal.WriteAsync(
                "session-created",
                session.History,
                new Dictionary<string, object?>
                {
                    ["agent"] = agentName,
                    ["document"] = options.SourceDocumentPath,
                },
                cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "[Context][{AgentName}] 独立上下文实时写入：{Path}",
                agentName,
                contextJournal.FilePath);
            return new OfficeCliAgentRuntime(
                options,
                agentName,
                memoryKind,
                mcpClient,
                chatClient,
                session,
                memory,
                contextJournal,
                approval,
                logger);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "[Startup] 创建 {AgentName} 失败", agentName);
            await mcpClient.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static IReadOnlyDictionary<string, JsonNode?> CreateSystemPromptTemplateValues(
        string sourceDocumentPath,
        int textBatchSize,
        int? pageCount = null,
        int? bodyChildElementCount = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDocumentPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(textBatchSize);
        if (pageCount is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageCount), "页数必须大于 0。 ");
        }

        if (bodyChildElementCount is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bodyChildElementCount),
                "Body.ChildElements 数量必须大于 0。 ");
        }

        var values = new Dictionary<string, JsonNode?>
        {
            ["sourceDocumentPath"] = JsonValue.Create(Path.GetFullPath(sourceDocumentPath)),
            ["textBatchSize"] = JsonValue.Create(textBatchSize),
            ["textBatchEndOffset"] = JsonValue.Create(textBatchSize - 1),
            ["secondTextBatchEndIndex"] = JsonValue.Create(checked(textBatchSize * 2 - 1)),
            ["annotatedBatchSize"] = JsonValue.Create(20),
            ["annotatedBatchEndOffset"] = JsonValue.Create(19),
        };
        if (pageCount is { } pages)
        {
            values["pageCount"] = JsonValue.Create(pages);
        }

        if (bodyChildElementCount is { } bodyElements)
        {
            values["bodyChildElementCount"] = JsonValue.Create(bodyElements);
            values["lastBodyIndex"] = JsonValue.Create(bodyElements - 1);
        }

        return values;
    }

    internal static IEnumerable<int> EnumerateCorrectionValidationPasses(
        int maximumCorrectionAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCorrectionAttempts);
        return Enumerable.Range(0, maximumCorrectionAttempts + 1);
    }

    public async Task<ChatResponse> RunAsync(
        string stage,
        string prompt,
        OfficeCliCommandPermissions allowedOfficeCliCommands,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _toolApproval.SetStagePermissions(allowedOfficeCliCommands);
        var taskNumber = Interlocked.Increment(ref _agentTaskCount);
        _contextJournal.SetStage(stage);
        var history = _session.History;
        var promptEstimate = LoggingTokenCompressor.Estimate(
        [
            new ChatMessage(ChatRole.User, prompt),
        ]);
        using var scope = _logger.BeginScope(
            "智能体={AgentName} / 任务 #{TaskNumber} / {Stage}",
            _agentName,
            taskNumber,
            stage);
        _logger.LogInformation(
            "[Agent] 任务开始：智能体={AgentName}，任务编号={TaskNumber}，阶段={Stage}；Prompt 字符数={PromptCharacters}，估算={PromptTokens} tokens；当前历史={HistoryCount} 条，估算={HistoryTokens} tokens",
            _agentName,
            taskNumber,
            stage,
            prompt.Length,
            promptEstimate,
            history.Count,
            LoggingTokenCompressor.Estimate(history));
        if (_options.ShowPrompts)
        {
            _logger.LogInformation(
                "[AI] Prompt：\n{Prompt}",
                DemoLogFormatter.Limit(prompt, _options.MaximumLogTextLength));
        }

        var stopwatch = Stopwatch.StartNew();
        var streamWriter = new LiveAgentOutputWriter(
            _logger,
            Console.Out,
            _options.ShowAiReasoning,
            _options.ShowAiOutput,
            _agentName,
            taskNumber,
            stage);
        try
        {
            await _contextJournal.WriteAsync(
                "agent-run-start",
                history,
                new Dictionary<string, object?>
                {
                    ["agent"] = _agentName,
                    ["taskNumber"] = taskNumber,
                    ["prompt"] = prompt,
                },
                cancellationToken).ConfigureAwait(false);
            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in _session.RunStreamAsync(
                               prompt,
                               cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                updates.Add(update);
                streamWriter.Observe(update);
            }

            streamWriter.Complete(stopwatch.Elapsed);
            var response = updates.ToChatResponse();
            stopwatch.Stop();
            LogAgentResponse(taskNumber, stage, response, stopwatch.Elapsed);
            await _contextJournal.WriteAsync(
                "agent-run-complete",
                _session.History,
                new Dictionary<string, object?>
                {
                    ["agent"] = _agentName,
                    ["taskNumber"] = taskNumber,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                },
                cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            streamWriter.Complete(stopwatch.Elapsed, failed: true);
            _logger.LogError(
                exception,
                "[Agent] 任务失败：智能体={AgentName}，任务编号={TaskNumber}，阶段={Stage}，耗时={ElapsedMs} ms",
                _agentName,
                taskNumber,
                stage,
                stopwatch.ElapsedMilliseconds);
            await _contextJournal.WriteAsync(
                "agent-run-error",
                _session.History,
                new Dictionary<string, object?>
                {
                    ["agent"] = _agentName,
                    ["taskNumber"] = taskNumber,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                    ["exception"] = exception.ToString(),
                },
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task RememberAsync(
        string id,
        string content,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var safeContent = content.Length <= 24_000 ? content : content[..24_000];
        if (safeContent.Length != content.Length)
        {
            _logger.LogWarning(
                "[Memory] 阶段记忆 {Id} 从 {OriginalLength} 字符截断为 {StoredLength} 字符",
                id,
                content.Length,
                safeContent.Length);
        }

        await _memory.RememberAsync(
        [
            new MemoryEntry(
                id,
                safeContent,
                metadata: new Dictionary<string, string>
                {
                    ["agent"] = _agentName,
                    ["document"] = _options.SourceDocumentPath,
                    ["kind"] = _memoryKind,
                }),
        ], cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearHistoryAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var history = _session.History;
        _logger.LogInformation(
            "[History] 清空 {AgentName} 会话历史。原因：{Reason}；清空前 {MessageCount} 条，估算 {TokenEstimate} tokens。该智能体的长期记忆不会被清空",
            _agentName,
            reason,
            history.Count,
            LoggingTokenCompressor.Estimate(history));
        _session.ClearHistory();
        _logger.LogInformation(
            "[History] {AgentName} 会话历史已清空，当前 {MessageCount} 条",
            _agentName,
            _session.History.Count);
        await _contextJournal.WriteAsync(
            "history-cleared",
            _session.History,
            new Dictionary<string, object?>
            {
                ["agent"] = _agentName,
                ["reason"] = reason,
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task CloseOfficeCliResidentAsync(
        CancellationToken cancellationToken = default,
        bool warnOnFailure = true)
    {
        try
        {
            _logger.LogDebug(
                "[OfficeCli][{AgentName}] 尝试释放 resident：{Document}",
                _agentName,
                _options.SourceDocumentPath);
            await OfficeCliProcess.RunAsync(
                _options.OfficeCliCommand,
                ["close", _options.SourceDocumentPath],
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (warnOnFailure)
            {
                _logger.LogWarning(
                    exception,
                    "[OfficeCli][{AgentName}] 释放 resident 失败：{Document}",
                    _agentName,
                    _options.SourceDocumentPath);
            }
            else
            {
                _logger.LogDebug(
                    exception,
                    "[OfficeCli][{AgentName}] 退出时释放 resident 未成功：{Document}",
                    _agentName,
                    _options.SourceDocumentPath);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _logger.LogInformation(
            "[Shutdown] 正在释放 {AgentName} 的 AI 客户端、独立会话和 officecli MCP",
            _agentName);
        _chatClient.Dispose();
        try
        {
            await _mcpClient.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await CloseOfficeCliResidentAsync(
                CancellationToken.None,
                warnOnFailure: false).ConfigureAwait(false);
        }

        _logger.LogInformation("[Shutdown] {AgentName} 资源已释放", _agentName);
    }

    private void LogAgentResponse(
        int taskNumber,
        string stage,
        ChatResponse response,
        TimeSpan elapsed)
    {
        var callsById = new Dictionary<string, FunctionCallContent>(StringComparer.Ordinal);
        var results = new List<FunctionResultContent>();
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call:
                        callsById[call.CallId] = call;
                        if (call.Exception is not null)
                        {
                            _logger.LogError(
                                call.Exception,
                                "[Tool] AI 工具参数解析异常：阶段={Stage}，CallId={CallId}，工具={ToolName}",
                                stage,
                                call.CallId,
                                call.Name);
                        }
                        else
                        {
                            _logger.LogInformation(
                                "[Tool] 模型发起工具调用：阶段={Stage}，CallId={CallId}，工具={ToolName}",
                                stage,
                                call.CallId,
                                call.Name);
                        }

                        break;
                    case FunctionResultContent result:
                        results.Add(result);
                        break;
                }
            }
        }

        foreach (var result in results)
        {
            var toolName = callsById.TryGetValue(result.CallId, out var call)
                ? call.Name
                : "<unknown>";
            if (result.Exception is not null)
            {
                _logger.LogError(
                    result.Exception,
                    "[Tool] 执行失败：阶段={Stage}，CallId={CallId}，工具={ToolName}，返回={Result}",
                    stage,
                    result.CallId,
                    toolName,
                    DemoLogFormatter.Serialize(result.Result, _options.MaximumLogTextLength));
                continue;
            }

            if (_options.ShowToolResults)
            {
                _logger.LogInformation(
                    "[Tool] 执行结果：阶段={Stage}，CallId={CallId}，工具={ToolName}\n{Result}",
                    stage,
                    result.CallId,
                    toolName,
                    DemoLogFormatter.Serialize(result.Result, _options.MaximumLogTextLength));
            }
            else
            {
                _logger.LogInformation(
                    "[Tool] 执行完成：阶段={Stage}，CallId={CallId}，工具={ToolName}（结果内容打印已关闭）",
                    stage,
                    result.CallId,
                    toolName);
            }
        }

        var usage = response.Usage;
        var averageOutputTokensPerSecond = usage?.OutputTokenCount is { } outputTokens
            && elapsed.TotalSeconds > 0
                ? outputTokens / elapsed.TotalSeconds
                : (double?)null;
        _logger.LogInformation(
            "[Agent] 任务完成：智能体={AgentName}，任务编号={TaskNumber}，阶段={Stage}，耗时={ElapsedMs} ms，响应消息={MessageCount}，工具调用={ToolCallCount}，累计InputTokens={InputTokens}，累计OutputTokens={OutputTokens}，累计ReasoningTokens={ReasoningTokens}，任务平均输出速度={TokensPerSecond} tokens/s，FinishReason={FinishReason}，提交后历史={HistoryCount} 条",
            _agentName,
            taskNumber,
            stage,
            (long)elapsed.TotalMilliseconds,
            response.Messages.Count,
            callsById.Count,
            usage?.InputTokenCount,
            usage?.OutputTokenCount,
            usage?.ReasoningTokenCount,
            averageOutputTokensPerSecond?.ToString("0.00") ?? "usage-missing",
            response.FinishReason,
            _session.History.Count);

    }

    internal sealed class LiveAgentOutputWriter
    {
        private readonly ILogger _logger;
        private readonly TextWriter _console;
        private readonly bool _showReasoning;
        private readonly bool _showOutput;
        private readonly string _agentName;
        private readonly int _taskNumber;
        private readonly string _stage;
        private long _reasoningCharacters;
        private long _outputCharacters;
        private int _updateCount;
        private OutputSection _currentSection;

        public LiveAgentOutputWriter(
            ILogger logger,
            TextWriter console,
            bool showReasoning,
            bool showOutput,
            string agentName,
            int taskNumber,
            string stage)
        {
            _logger = logger;
            _console = console;
            _showReasoning = showReasoning;
            _showOutput = showOutput;
            _agentName = agentName;
            _taskNumber = taskNumber;
            _stage = stage;
        }

        public void Observe(ChatResponseUpdate update)
        {
            _updateCount++;
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                        _reasoningCharacters += reasoning.Text.Length;
                        if (_showReasoning)
                        {
                            Write(OutputSection.Reasoning, reasoning.Text);
                        }

                        break;
                    case TextContent text when !string.IsNullOrEmpty(text.Text):
                        _outputCharacters += text.Text.Length;
                        if (_showOutput)
                        {
                            Write(OutputSection.Output, text.Text);
                        }

                        break;
                    case FunctionCallContent or UsageContent:
                        EndCurrentSection();
                        break;
                }
            }
        }

        public void Complete(TimeSpan elapsed, bool failed = false)
        {
            EndCurrentSection();
            _logger.LogInformation(
                "[AI][流式总结] 智能体={AgentName}，任务编号={TaskNumber}，阶段={Stage}，{Status}；已接收更新={UpdateCount}，思考字符={ReasoningCharacters}，普通输出字符={OutputCharacters}，耗时={ElapsedMs} ms",
                _agentName,
                _taskNumber,
                _stage,
                failed ? "流式输出中断" : "流式输出完成",
                _updateCount,
                _reasoningCharacters,
                _outputCharacters,
                (long)elapsed.TotalMilliseconds);
        }

        private void Write(OutputSection section, string text)
        {
            if (_currentSection != section)
            {
                EndCurrentSection();
                _console.WriteLine();
                _console.WriteLine(
                    section == OutputSection.Reasoning
                        ? $"========== AI 思考流 | {_agentName} | 任务 #{_taskNumber} | {_stage} =========="
                        : $"========== AI 普通输出流 | {_agentName} | 任务 #{_taskNumber} | {_stage} ==========");
                _currentSection = section;
            }

            _console.Write(text);
            _console.Flush();
        }

        private void EndCurrentSection()
        {
            if (_currentSection == OutputSection.None)
            {
                return;
            }

            _console.WriteLine();
            _console.Flush();
            _currentSection = OutputSection.None;
        }

        private enum OutputSection
        {
            None,
            Reasoning,
            Output,
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
