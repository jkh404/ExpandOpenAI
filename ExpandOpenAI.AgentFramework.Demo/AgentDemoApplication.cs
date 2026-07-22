using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExpandOpenAI;
using ExpandOpenAI.AgentFramework;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.AgentFramework.Demo;

internal sealed class AgentDemoApplication
{
    private const int MaximumInspectableMemories = 200;
    private readonly DynamicConcurrentDictionary _promptValues = new();
    private readonly List<DemoSession> _sessions = [];
    private readonly NovelSessionStore _sessionStore;
    private readonly NovelGlobalMemoryStore? _globalMemoryStore;
    private readonly InMemoryMemoryUnit? _globalMemory;
    private readonly HttpClient _workspaceHttpClient;
    private readonly string _modelId;
    private readonly int _maximumOutputTokens;
    private readonly int _compressionTokenThreshold;
    private readonly int _systemPromptVersion;
    private readonly AsyncLocal<DemoSession?> _runningSession = new();
    private readonly AIAgent _agent;
    private InMemoryMemoryUnit? _pendingSessionMemory;
    private int _activeSessionIndex;

    public AgentDemoApplication(
        IChatClient chatClient,
        OpenAICompatibleChatClientOptions clientOptions,
        string rootWorkspacePath,
        HttpClient workspaceHttpClient,
        int compressionTokenThreshold,
        int maximumOutputTokens,
        string systemPrompt,
        int systemPromptVersion,
        InMemoryMemoryUnit? globalMemory = null)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(clientOptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootWorkspacePath);
        ArgumentNullException.ThrowIfNull(workspaceHttpClient);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(compressionTokenThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOutputTokens);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(systemPromptVersion);

        RootWorkspacePath = Path.GetFullPath(rootWorkspacePath.Trim().Trim('"'));
        Directory.CreateDirectory(RootWorkspacePath);
        _workspaceHttpClient = workspaceHttpClient;
        _modelId = clientOptions.ModelId
            ?? throw new ArgumentException("Chat 模型不能为空。", nameof(clientOptions));
        _maximumOutputTokens = maximumOutputTokens;
        _compressionTokenThreshold = compressionTokenThreshold;
        _systemPromptVersion = systemPromptVersion;
        _globalMemory = globalMemory;
        _sessionStore = new NovelSessionStore(RootWorkspacePath);
        _globalMemoryStore = globalMemory is null ? null : new NovelGlobalMemoryStore(RootWorkspacePath);
        _promptValues["assistant"] = new JsonObject { ["name"] = "小说撰写智能体" };
        _promptValues["workspace"] = new JsonObject { ["root"] = "当前会话工作区" };
        _promptValues["session"] = new JsonObject { ["instructions"] = string.Empty };
        _promptValues.RegisterDynamicValue(
            "utcNow",
            static () => JsonValue.Create(DateTimeOffset.UtcNow.ToString("O")));

        _agent = new DefaultAIAgent(chatClient, new AgentOptions
        {
            SystemPromptTemplate = systemPrompt.Trim()
                + "\n\n# 当前会话补充指令\n{{session.instructions}}",
            SystemPromptTemplateValues = _promptValues,
            MissingTemplateValueBehavior = MissingTemplateValueBehavior.Throw,
            TokenCompressor = new RecordingTokenCompressor(
                new DefaultTokenCompressor(new DefaultTokenCompressorOptions
                {
                    RecentVerbatimTurnCount = 2,
                    RecentSummaryTurnCount = 12,
                    MaximumHistoryTokenEstimate = compressionTokenThreshold,
                    SummaryMaxOutputTokens = 800,
                }),
                RecordCompressionAsync),
            SessionMemoryUnitFactory = () => _pendingSessionMemory ?? new InMemoryMemoryUnit(),
            GlobalMemoryUnit = _globalMemory,
            EnableMemoryRecallTool = true,
            MemoryRecallMaxResults = 12,
            DefaultChatOptions = new ChatOptions
            {
                ModelId = _modelId,
                Temperature = 0.65f,
                MaxOutputTokens = _maximumOutputTokens,
            },
            ToolApprovalAsync = static (context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new ValueTask<bool>(IsAutoApprovedTool(context.Function.Name));
            },
        });
    }

    public string RootWorkspacePath { get; }

    public NovelWorkspace ActiveWorkspace => ActiveSession.Workspace;

    public string SessionStatePath => _sessionStore.StatePath;

    public string? GlobalMemoryStatePath => _globalMemoryStore?.StatePath;

    public string ActiveSessionId => ActiveSession.Id;

    public string ActiveSessionName => ActiveSession.Name;

    public IReadOnlyList<NovelSessionSummary> Sessions => _sessions
        .Select(session => new NovelSessionSummary(
            session.Id,
            session.Name,
            session.LastOpenedAt,
            string.Equals(session.Id, ActiveSessionId, StringComparison.Ordinal)))
        .ToList();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var globalMemories = _globalMemoryStore is null
            ? []
            : await _globalMemoryStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (globalMemories.Count > 0 && _globalMemory is not null)
        {
            await _globalMemory.RememberAsync(
                globalMemories.Select(static memory => memory.ToMemoryEntry()).ToList(),
                cancellationToken).ConfigureAwait(false);
        }

        var persisted = await _sessionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var savedSession in persisted)
        {
            await CreateSessionCoreAsync(
                savedSession.Name,
                savedSession.History.Select(static history => history.ToChatMessage()),
                savedSession.Memories.Select(static memory => memory.ToMemoryEntry()),
                savedSession.Id,
                savedSession.WorkspaceDirectoryName,
                savedSession.SessionInstructions,
                savedSession.CompressionHistory,
                savedSession.LastOpenedAt,
                cancellationToken).ConfigureAwait(false);
        }

        if (_sessions.Count == 0)
        {
            await CreateSessionCoreAsync(
                "小说企划",
                initialHistory: null,
                initialMemories: null,
                id: null,
                workspaceDirectoryName: null,
                sessionInstructions: null,
                initialCompressionHistory: null,
                lastOpenedAt: DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
            await PersistStateAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _activeSessionIndex = 0;
        UpdateActiveWorkspacePrompt();
        await PersistSessionsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<NovelSessionSummary> CreateSessionAsync(
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        var actualName = string.IsNullOrWhiteSpace(name)
            ? $"小说会话 {_sessions.Count + 1}"
            : name.Trim();
        await CreateSessionCoreAsync(
            actualName,
            initialHistory: null,
            initialMemories: null,
            id: null,
            workspaceDirectoryName: null,
            sessionInstructions: null,
            initialCompressionHistory: null,
            lastOpenedAt: DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);
        await PersistStateAsync(cancellationToken).ConfigureAwait(false);
        return ActiveSessionSummary();
    }

    public async Task SelectSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var index = _sessions.FindIndex(session => string.Equals(session.Id, sessionId, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new KeyNotFoundException("找不到要切换的小说会话。 ");
        }

        _activeSessionIndex = index;
        ActiveSession.LastOpenedAt = DateTimeOffset.UtcNow;
        UpdateActiveWorkspacePrompt();
        await PersistStateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<NovelStreamEvent> SendStreamAsync(
        string text,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var output = new StringBuilder();
        var formatter = new StreamingTranscriptFormatter();
        var message = new ChatMessage(ChatRole.User, text);
        UpdateActiveWorkspacePrompt();
        var chatOptions = CreateRunChatOptions();
        var runningSession = ActiveSession;
        _runningSession.Value = runningSession;

        try
        {
            await foreach (var update in runningSession.Session.RunStreamAsync(
                message,
                chatOptions,
                cancellationToken).ConfigureAwait(false))
            {
                while (runningSession.PendingCompressionRecords.TryDequeue(out var compression))
                {
                    yield return new NovelStreamEvent(
                        "compression",
                        $"上下文压缩完成：约 {compression.BeforeTokenEstimate:N0} → {compression.AfterTokenEstimate:N0} tokens。",
                        Compression: compression);
                }

                foreach (var call in update.Contents.OfType<FunctionCallContent>())
                {
                    yield return new NovelStreamEvent(
                        "tool_call",
                        $"正在调用 {call.Name}",
                        call.CallId,
                        call.Name,
                        SerializeToolPayload(call.Arguments, 4_000));
                }

                foreach (var result in update.Contents.OfType<FunctionResultContent>())
                {
                    yield return new NovelStreamEvent(
                        "tool_result",
                        result.Exception?.Message ?? SerializeToolPayload(result.Result, 12_000),
                        result.CallId,
                        ToolSucceeded: result.Exception is null);
                }

                var textUpdate = formatter.Format(update);
                if (textUpdate.Length == 0)
                {
                    continue;
                }

                output.Append(textUpdate);
                yield return new NovelStreamEvent("delta", textUpdate);
            }

            while (runningSession.PendingCompressionRecords.TryDequeue(out var compression))
            {
                yield return new NovelStreamEvent(
                    "compression",
                    $"上下文压缩完成：约 {compression.BeforeTokenEstimate:N0} → {compression.AfterTokenEstimate:N0} tokens。",
                    Compression: compression);
            }

            runningSession.LastOpenedAt = DateTimeOffset.UtcNow;
            await PersistStateAsync(cancellationToken).ConfigureAwait(false);
            yield return new NovelStreamEvent("complete", output.Length == 0 ? "(模型未返回文本)" : output.ToString());
        }
        finally
        {
            _runningSession.Value = null;
        }
    }

    public IReadOnlyList<NovelConversationItem> GetActiveConversation()
    {
        return ActiveSession.Session.History
            .Select(static message => new NovelConversationItem(
                message.Role == ChatRole.User ? "user" : "assistant",
                DescribeMessage(message)))
            .ToList();
    }

    public async Task<IReadOnlyList<NovelMemorySnippet>> GetSessionMemoriesAsync(
        CancellationToken cancellationToken = default)
    {
        var memories = await ActiveSession.Memory.RecallAsync(
            new MemoryRecallRequest(string.Empty, MaximumInspectableMemories),
            cancellationToken).ConfigureAwait(false);
        return memories.Select(static memory => NovelMemorySnippet.From("session", memory)).ToList();
    }

    public async Task<IReadOnlyList<NovelMemorySnippet>> GetGlobalMemoriesAsync(
        CancellationToken cancellationToken = default)
    {
        if (_globalMemory is null)
        {
            return [];
        }

        var memories = await _globalMemory.RecallAsync(
            new MemoryRecallRequest(string.Empty, MaximumInspectableMemories),
            cancellationToken).ConfigureAwait(false);
        return memories.Select(static memory => NovelMemorySnippet.From("global", memory)).ToList();
    }

    public async Task<NovelContextDiagnostics> GetContextDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        var history = ActiveSession.Session.History;
        var sessionMemories = await ActiveSession.Memory.RecallAsync(
            new MemoryRecallRequest(string.Empty, 10_000),
            cancellationToken).ConfigureAwait(false);
        var globalMemories = _globalMemory is null
            ? []
            : await _globalMemory.RecallAsync(
                new MemoryRecallRequest(string.Empty, 10_000),
                cancellationToken).ConfigureAwait(false);
        List<NovelCompressionRecord> compressions;
        lock (ActiveSession.CompressionSync)
        {
            compressions = ActiveSession.CompressionHistory
                .OrderByDescending(static compression => compression.OccurredAt)
                .Take(50)
                .ToList();
        }

        return new NovelContextDiagnostics(
            ActiveSession.Id,
            history.Count,
            NovelTokenEstimator.Estimate(history),
            _compressionTokenThreshold,
            _maximumOutputTokens,
            _systemPromptVersion,
            ActiveSession.SessionInstructions,
            sessionMemories.Count,
            globalMemories.Count,
            compressions.AsReadOnly());
    }

    public async Task UpdateSessionInstructionsAsync(
        string sessionId,
        string? instructions,
        CancellationToken cancellationToken = default)
    {
        var session = _sessions.FirstOrDefault(item => string.Equals(item.Id, sessionId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException("找不到要更新提示词的小说会话。 ");
        session.SessionInstructions = instructions?.Trim() ?? string.Empty;
        if (ReferenceEquals(session, ActiveSession))
        {
            UpdateActiveWorkspacePrompt();
        }

        await PersistSessionsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertGlobalMemoryAsync(
        string id,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        var globalMemory = _globalMemory
            ?? throw new InvalidOperationException("当前宿主未配置全局记忆体。 ");
        var normalizedId = NormalizeMemoryId(id);
        if (!normalizedId.StartsWith("preference-", StringComparison.Ordinal))
        {
            normalizedId = $"preference-{normalizedId}";
        }

        await globalMemory.RememberAsync(
        [
            new MemoryEntry(
                normalizedId,
                content.Trim(),
                metadata: new Dictionary<string, string>
                {
                    ["kind"] = "cross-novel-preference",
                    ["source"] = "user-managed",
                }),
        ], cancellationToken).ConfigureAwait(false);
        await PersistGlobalMemoryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteMemoryAsync(
        string scope,
        string id,
        CancellationToken cancellationToken = default)
    {
        var removed = scope.ToLowerInvariant() switch
        {
            "session" => await ActiveSession.Memory.RemoveAsync(id, cancellationToken).ConfigureAwait(false),
            "global" when _globalMemory is not null => await _globalMemory.RemoveAsync(id, cancellationToken).ConfigureAwait(false),
            "global" => false,
            _ => throw new ArgumentException("记忆范围只能是 session 或 global。", nameof(scope)),
        };
        if (removed)
        {
            await PersistStateAsync(cancellationToken).ConfigureAwait(false);
        }

        return removed;
    }

    public async Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        ActiveSession.Session.ClearHistory();
        await PersistStateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearSessionMemoryAsync(CancellationToken cancellationToken = default)
    {
        await ActiveSession.Session.ClearMemoryAsync(cancellationToken).ConfigureAwait(false);
        await PersistStateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DestroyActiveSessionAsync(CancellationToken cancellationToken = default)
    {
        var destroyed = ActiveSession;
        await destroyed.Session.DestroyAsync(cancellationToken).ConfigureAwait(false);
        _sessions.RemoveAt(_activeSessionIndex);

        if (_sessions.Count == 0)
        {
            await CreateSessionCoreAsync(
                "小说企划",
                initialHistory: null,
                initialMemories: null,
                id: null,
                workspaceDirectoryName: null,
                sessionInstructions: null,
                initialCompressionHistory: null,
                lastOpenedAt: DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _activeSessionIndex = Math.Min(_activeSessionIndex, _sessions.Count - 1);
            ActiveSession.LastOpenedAt = DateTimeOffset.UtcNow;
            UpdateActiveWorkspacePrompt();
        }

        await PersistStateAsync(cancellationToken).ConfigureAwait(false);
    }

    private AITool CreateGlobalMemoryTool()
    {
        return AIFunctionFactory.Create(
            (Func<string, string, CancellationToken, Task<string>>)RememberGlobalMemoryAsync,
            "remember_global_memory",
            "仅保存跨小说也成立的用户称呼、写作偏好、协作约定或格式规范。绝不能保存人物、世界观、章节、伏笔等小说内容；此工具会自动获批。 ");
    }

    private async Task<string> RememberGlobalMemoryAsync(
        [System.ComponentModel.Description("短而稳定的全局信息标题，例如 user-name、preferred-tone 或 output-format。")] string key,
        [System.ComponentModel.Description("跨小说也适用的用户称呼、写作偏好、协作约定或格式规范。不得包含任何小说设定。 ")] string content,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        var normalizedKey = new string(key.Trim().Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_').ToArray());
        var globalMemory = _globalMemory
            ?? throw new InvalidOperationException("当前宿主未配置全局记忆体。 ");
        await globalMemory.RememberAsync(
        [
            new MemoryEntry(
                $"preference-{normalizedKey}",
                content.Trim(),
                metadata: new Dictionary<string, string> { ["kind"] = "cross-novel-preference" }),
        ],
        cancellationToken).ConfigureAwait(false);
        await PersistGlobalMemoryAsync(cancellationToken).ConfigureAwait(false);
        return $"已保存为跨小说可复用的用户偏好：{key.Trim()}";
    }

    private async Task CreateSessionCoreAsync(
        string name,
        IEnumerable<ChatMessage>? initialHistory,
        IEnumerable<MemoryEntry>? initialMemories,
        string? id,
        string? workspaceDirectoryName,
        string? sessionInstructions,
        IEnumerable<NovelCompressionRecord>? initialCompressionHistory,
        DateTimeOffset lastOpenedAt,
        CancellationToken cancellationToken)
    {
        var sessionId = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("D") : id;
        var actualWorkspaceDirectoryName = ResolveWorkspaceDirectoryName(
            name,
            sessionId,
            workspaceDirectoryName);
        var workspace = new NovelWorkspace(
            Path.Combine(RootWorkspacePath, actualWorkspaceDirectoryName),
            _workspaceHttpClient);
        if ((File.GetAttributes(workspace.RootPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("会话工作区不能是符号链接或重解析点。 ");
        }
        var memory = new InMemoryMemoryUnit();
        _pendingSessionMemory = memory;
        IAgentSession session;
        try
        {
            session = _agent.CreateSession(initialHistory);
        }
        finally
        {
            _pendingSessionMemory = null;
        }

        var memories = initialMemories?.ToList() ?? [];
        if (memories.Count > 0)
        {
            await memory.RememberAsync(memories, cancellationToken).ConfigureAwait(false);
        }

        _sessions.Add(new DemoSession(
            sessionId,
            name,
            actualWorkspaceDirectoryName,
            sessionInstructions?.Trim() ?? string.Empty,
            workspace,
            session,
            memory,
            initialCompressionHistory?.ToList() ?? [],
            lastOpenedAt));
        _activeSessionIndex = _sessions.Count - 1;
        UpdateActiveWorkspacePrompt();
    }

    private async Task PersistStateAsync(CancellationToken cancellationToken)
    {
        await PersistSessionsAsync(cancellationToken).ConfigureAwait(false);
        await PersistGlobalMemoryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistSessionsAsync(CancellationToken cancellationToken)
    {
        var snapshots = new List<PersistedNovelSession>(_sessions.Count);
        foreach (var session in _sessions)
        {
            var memories = await session.Memory.RecallAsync(
                new MemoryRecallRequest(string.Empty, maxResults: 10_000),
                cancellationToken).ConfigureAwait(false);
            snapshots.Add(NovelSessionStore.CreateSnapshot(
                session.Id,
                session.Name,
                session.WorkspaceDirectoryName,
                session.SessionInstructions,
                session.LastOpenedAt,
                session.Session.History,
                memories,
                session.CompressionHistory));
        }

        await _sessionStore.SaveAsync(snapshots, cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistGlobalMemoryAsync(CancellationToken cancellationToken)
    {
        if (_globalMemory is null || _globalMemoryStore is null)
        {
            return;
        }

        var memories = await _globalMemory.RecallAsync(
            new MemoryRecallRequest(string.Empty, maxResults: 10_000),
            cancellationToken).ConfigureAwait(false);
        await _globalMemoryStore.SaveAsync(
            memories.Select(PersistedMemoryEntry.FromMemoryEntry).ToList(),
            cancellationToken).ConfigureAwait(false);
    }

    private NovelSessionSummary ActiveSessionSummary()
    {
        return new NovelSessionSummary(
            ActiveSession.Id,
            ActiveSession.Name,
            ActiveSession.LastOpenedAt,
            IsActive: true);
    }

    private DemoSession ActiveSession => _sessions[_activeSessionIndex];

    private ValueTask RecordCompressionAsync(
        TokenCompressionContext context,
        TokenCompressionResult result,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = _runningSession.Value;
        if (session is null)
        {
            return default;
        }

        var record = new NovelCompressionRecord(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            context.Reason.ToString(),
            context.Messages.Count,
            result.Messages.Count,
            NovelTokenEstimator.Estimate(context.Messages),
            NovelTokenEstimator.Estimate(result.Messages),
            result.SessionMemoriesToStore.Count,
            result.GlobalMemoriesToStore.Count);
        lock (session.CompressionSync)
        {
            session.CompressionHistory.Add(record);
            if (session.CompressionHistory.Count > 200)
            {
                session.CompressionHistory.RemoveRange(0, session.CompressionHistory.Count - 200);
            }
        }

        session.PendingCompressionRecords.Enqueue(record);
        return default;
    }

    private ChatOptions CreateRunChatOptions()
    {
        return new ChatOptions
        {
            ModelId = _modelId,
            Temperature = 0.65f,
            MaxOutputTokens = _maximumOutputTokens,
            Tools = _globalMemory is null
                ? ActiveWorkspace.CreateTools().ToList()
                : [.. ActiveWorkspace.CreateTools(), CreateGlobalMemoryTool()],
        };
    }

    private void UpdateActiveWorkspacePrompt()
    {
        if (_sessions.Count == 0)
        {
            return;
        }

        _promptValues["workspace"] = new JsonObject
        {
            ["root"] = Path.GetFileName(ActiveWorkspace.RootPath),
        };
        _promptValues["session"] = new JsonObject
        {
            ["instructions"] = ActiveSession.SessionInstructions,
        };
    }

    private static string NormalizeMemoryId(string id)
    {
        var normalized = new string(id.Trim().Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_').ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "preference" : normalized;
    }

    private string ResolveWorkspaceDirectoryName(
        string sessionName,
        string sessionId,
        string? persistedDirectoryName)
    {
        if (!string.IsNullOrWhiteSpace(persistedDirectoryName))
        {
            var candidate = persistedDirectoryName.Trim();
            if (!string.Equals(Path.GetFileName(candidate), candidate, StringComparison.Ordinal)
                || candidate is "." or ".."
                || candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidDataException($"会话工作区目录名无效：{persistedDirectoryName}");
            }

            var restoredPath = Path.GetFullPath(Path.Combine(RootWorkspacePath, candidate));
            if (!IsPathInsideRoot(restoredPath))
            {
                throw new InvalidDataException($"会话工作区不能离开根工作区：{persistedDirectoryName}");
            }

            if (_sessions.Any(session => string.Equals(
                    session.WorkspaceDirectoryName,
                    candidate,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            {
                throw new InvalidDataException($"多个会话不能共用同一个工作区目录：{persistedDirectoryName}");
            }

            return candidate;
        }

        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var safeName = new string(sessionName.Trim()
            .Select(character => invalidCharacters.Contains(character) ? '-' : character)
            .ToArray())
            .Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "novel";
        }

        if (safeName.Length > 48)
        {
            safeName = safeName[..48].TrimEnd(' ', '.');
        }

        var suffix = Guid.Parse(sessionId).ToString("N")[..12];
        return $"{safeName}-{suffix}";
    }

    private bool IsPathInsideRoot(string path)
    {
        var rootWithSeparator = RootWorkspacePath.EndsWith(Path.DirectorySeparatorChar)
            ? RootWorkspacePath
            : RootWorkspacePath + Path.DirectorySeparatorChar;
        return path.StartsWith(
            rootWithSeparator,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool IsAutoApprovedTool(string name)
    {
        return NovelWorkspace.IsToolName(name)
            || string.Equals(name, "remember_global_memory", StringComparison.Ordinal);
    }

    private static string SerializeToolPayload(object? value, int maximumCharacters)
    {
        string serialized;
        try
        {
            serialized = JsonSerializer.Serialize(value);
        }
        catch (NotSupportedException)
        {
            serialized = value?.ToString() ?? "null";
        }

        return serialized.Length <= maximumCharacters
            ? serialized
            : serialized[..maximumCharacters] + $"\n…[界面仅显示前 {maximumCharacters} 个字符]";
    }

    private static string DescribeMessage(ChatMessage message)
    {
        return string.Join("\n", message.Contents.Select(static content => content switch
        {
            TextContent text => text.Text,
            TextReasoningContent reasoning => $"[思考]\n{reasoning.Text}",
            FunctionCallContent call => $"[工具调用] {call.Name}",
            FunctionResultContent result => $"[工具结果]\n{result.Result}",
            _ => content.ToString() ?? content.GetType().Name,
        }));
    }

    private sealed class DemoSession(
        string id,
        string name,
        string workspaceDirectoryName,
        string sessionInstructions,
        NovelWorkspace workspace,
        IAgentSession session,
        InMemoryMemoryUnit memory,
        List<NovelCompressionRecord> compressionHistory,
        DateTimeOffset lastOpenedAt)
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        public string WorkspaceDirectoryName { get; } = workspaceDirectoryName;

        public string SessionInstructions { get; set; } = sessionInstructions;

        public NovelWorkspace Workspace { get; } = workspace;

        public IAgentSession Session { get; } = session;

        public InMemoryMemoryUnit Memory { get; } = memory;

        public object CompressionSync { get; } = new();

        public List<NovelCompressionRecord> CompressionHistory { get; } = compressionHistory;

        public ConcurrentQueue<NovelCompressionRecord> PendingCompressionRecords { get; } = new();

        public DateTimeOffset LastOpenedAt { get; set; } = lastOpenedAt;
    }
}

internal sealed record NovelSessionSummary(
    string Id,
    string Name,
    DateTimeOffset LastOpenedAt,
    bool IsActive);

internal sealed record NovelConversationItem(string Role, string Content);

internal sealed record NovelStreamEvent(
    string Type,
    string Content,
    string? ToolCallId = null,
    string? ToolName = null,
    string? ToolArguments = null,
    bool? ToolSucceeded = null,
    NovelCompressionRecord? Compression = null);

internal sealed record NovelMemorySnippet(
    string Scope,
    string Id,
    string Content,
    DateTimeOffset CreatedAt,
    IReadOnlyDictionary<string, string>? Metadata)
{
    public static NovelMemorySnippet From(string scope, MemoryEntry memory)
    {
        return new NovelMemorySnippet(
            scope,
            memory.Id,
            memory.Content,
            memory.CreatedAt,
            memory.Metadata);
    }
}
