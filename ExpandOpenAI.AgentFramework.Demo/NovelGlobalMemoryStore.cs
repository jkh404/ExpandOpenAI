using System.Text.Json;

namespace ExpandOpenAI.AgentFramework.Demo;

/// <summary>
/// 保存可选的跨小说写作偏好与协作约定；不保存任何小说设定。
/// </summary>
internal sealed class NovelGlobalMemoryStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public NovelGlobalMemoryStore(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        StateDirectoryPath = Path.Combine(workspacePath, ".expandopenai-agent");
        StatePath = Path.Combine(StateDirectoryPath, "global-memories.json");
    }

    public string StateDirectoryPath { get; }

    public string StatePath { get; }

    public async Task<IReadOnlyList<PersistedMemoryEntry>> LoadAsync(CancellationToken cancellationToken = default)
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
        var memories = await JsonSerializer.DeserializeAsync<List<PersistedMemoryEntry>>(
            stream,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false);
        return (memories ?? [])
            .Where(static memory => memory.IsValid)
            .OrderByDescending(static memory => memory.CreatedAt)
            .ToList()
            .AsReadOnly();
    }

    public async Task SaveAsync(
        IReadOnlyList<PersistedMemoryEntry> memories,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(memories);
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
                memories,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, StatePath, overwrite: true);
    }
}
