using System.Text.Json;
using ExpandOpenAI.AgentFramework;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.AgentFramework.Demo;

/// <summary>
/// 将某个小说工作区的会话、压缩后的活动历史和会话长期记忆保存到工作区私有状态目录。
/// </summary>
internal sealed class NovelSessionStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public NovelSessionStore(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        StateDirectoryPath = Path.Combine(workspacePath, ".expandopenai-agent");
        StatePath = Path.Combine(StateDirectoryPath, "novel-sessions.json");
    }

    public string StateDirectoryPath { get; }

    public string StatePath { get; }

    public async Task<IReadOnlyList<PersistedNovelSession>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(StatePath))
        {
            return [];
        }

        await using var stream = new FileStream(
            StatePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16_384,
            useAsync: true);
        var persisted = await JsonSerializer.DeserializeAsync<List<PersistedNovelSession>>(
            stream,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false);
        return (persisted ?? [])
            .Where(static session => session.IsValid)
            .OrderByDescending(static session => session.LastOpenedAt)
            .ToList()
            .AsReadOnly();
    }

    public async Task SaveAsync(
        IReadOnlyList<PersistedNovelSession> sessions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        Directory.CreateDirectory(StateDirectoryPath);

        var temporaryPath = StatePath + ".tmp";
        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16_384,
            useAsync: true))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                sessions,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, StatePath, overwrite: true);
    }

    public static PersistedNovelSession CreateSnapshot(
        string id,
        string name,
        string workspaceDirectoryName,
        string sessionInstructions,
        DateTimeOffset lastOpenedAt,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<MemoryEntry> memories,
        IReadOnlyList<NovelCompressionRecord> compressionHistory)
    {
        return new PersistedNovelSession
        {
            Id = id,
            Name = name,
            WorkspaceDirectoryName = workspaceDirectoryName,
            SessionInstructions = sessionInstructions,
            LastOpenedAt = lastOpenedAt,
            History = history.Select(PersistedChatMessage.FromChatMessage).ToList(),
            Memories = memories.Select(PersistedMemoryEntry.FromMemoryEntry).ToList(),
            CompressionHistory = compressionHistory.ToList(),
        };
    }
}

internal sealed class PersistedNovelSession
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 此会话在根工作区下的专属子目录。旧版状态没有该字段时，启动时会自动生成并回写。
    /// </summary>
    public string WorkspaceDirectoryName { get; set; } = string.Empty;

    public string SessionInstructions { get; set; } = string.Empty;

    public DateTimeOffset LastOpenedAt { get; set; }

    public List<PersistedChatMessage> History { get; set; } = [];

    public List<PersistedMemoryEntry> Memories { get; set; } = [];

    public List<NovelCompressionRecord> CompressionHistory { get; set; } = [];

    public bool IsValid => Guid.TryParse(Id, out _)
        && !string.IsNullOrWhiteSpace(Name)
        && History.All(static message => message.IsValid)
        && Memories.All(static memory => memory.IsValid);
}

internal sealed class PersistedChatMessage
{
    private const string SummaryMarker = "[Earlier conversation turn summary]\n";

    public string Role { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public DateTimeOffset? CreatedAt { get; set; }

    public bool IsCompressedTurnSummary { get; set; }

    public string? SummaryMemoryId { get; set; }

    public bool IsValid => ParseRole(Role) is not null;

    public static PersistedChatMessage FromChatMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var content = DescribeContents(message);
        var isSummary = content.StartsWith(SummaryMarker, StringComparison.Ordinal);
        return new PersistedChatMessage
        {
            Role = message.Role.ToString(),
            Content = content,
            CreatedAt = message.CreatedAt,
            IsCompressedTurnSummary = isSummary,
            SummaryMemoryId = isSummary ? CreateSummaryMemoryId(message) : null,
        };
    }

    public ChatMessage ToChatMessage()
    {
        var role = ParseRole(Role)
            ?? throw new InvalidOperationException($"无法还原保存的会话角色：{Role}");
        var message = new ChatMessage(role, Content)
        {
            CreatedAt = CreatedAt,
        };

        if (IsCompressedTurnSummary)
        {
            message.AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["expandopenai.agent.turn_summary"] = true,
                ["expandopenai.agent.memory_id"] = SummaryMemoryId
                    ?? $"restored-summary-{Guid.NewGuid():N}",
            };
        }

        return message;
    }

    private static string DescribeContents(ChatMessage message)
    {
        if (message.Contents.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            "\n",
            message.Contents.Select(static content => content switch
            {
                TextContent text => text.Text,
                TextReasoningContent reasoning => $"[思考]\n{reasoning.Text}",
                FunctionCallContent call => $"[工具调用] {call.Name}",
                FunctionResultContent result => $"[工具结果]\n{result.Result}",
                _ => content.ToString() ?? content.GetType().Name,
            }));
    }

    private static string? CreateSummaryMemoryId(ChatMessage message)
    {
        return message.AdditionalProperties?.TryGetValue(
            "expandopenai.agent.memory_id",
            out var value) == true
            ? value as string
            : null;
    }

    private static ChatRole? ParseRole(string role)
    {
        return role.Trim().ToLowerInvariant() switch
        {
            "system" => ChatRole.System,
            "user" => ChatRole.User,
            "assistant" => ChatRole.Assistant,
            "tool" => ChatRole.Tool,
            _ => null,
        };
    }
}

internal sealed class PersistedMemoryEntry
{
    public string Id { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public Dictionary<string, string>? Metadata { get; set; }

    public bool IsValid => !string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(Content);

    public static PersistedMemoryEntry FromMemoryEntry(MemoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new PersistedMemoryEntry
        {
            Id = entry.Id,
            Content = entry.Content,
            CreatedAt = entry.CreatedAt,
            Metadata = entry.Metadata?.ToDictionary(static pair => pair.Key, static pair => pair.Value),
        };
    }

    public MemoryEntry ToMemoryEntry()
    {
        return new MemoryEntry(Id, Content, CreatedAt, Metadata);
    }
}
