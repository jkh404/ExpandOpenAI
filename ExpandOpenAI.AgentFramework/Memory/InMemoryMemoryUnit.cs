namespace ExpandOpenAI.AgentFramework;

/// <summary>
/// 基于进程内存的线程安全记忆体，适合作为默认会话层实现。
/// </summary>
public sealed class InMemoryMemoryUnit : IMemoryUnit
{
    private readonly Dictionary<string, MemoryEntry> _memories =
        new Dictionary<string, MemoryEntry>(StringComparer.Ordinal);
    private readonly object _sync = new object();

    /// <inheritdoc />
    public ValueTask RememberAsync(
        IReadOnlyList<MemoryEntry> memories,
        CancellationToken cancellationToken = default)
    {
        if (memories is null)
        {
            throw new ArgumentNullException(nameof(memories));
        }

        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            foreach (var memory in memories)
            {
                if (memory is null)
                {
                    throw new ArgumentException("记忆集合不能包含 null。", nameof(memories));
                }

                _memories[memory.Id] = Clone(memory);
            }
        }

        return default;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<MemoryEntry>> RecallAsync(
        MemoryRecallRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();

        List<MemoryEntry> snapshot;
        lock (_sync)
        {
            snapshot = _memories.Values.Select(Clone).ToList();
        }

        var query = request.Query.Trim();
        IEnumerable<MemoryEntry> matches;

        if (query.Length == 0)
        {
            matches = snapshot.OrderByDescending(static memory => memory.CreatedAt);
        }
        else
        {
            var terms = SplitTerms(query);
            matches = snapshot
                .Select(memory => new { Memory = memory, Score = Score(memory, query, terms) })
                .Where(static match => match.Score > 0)
                .OrderByDescending(static match => match.Score)
                .ThenByDescending(static match => match.Memory.CreatedAt)
                .Select(static match => match.Memory);
        }

        IReadOnlyList<MemoryEntry> result = matches.Take(request.MaxResults).ToList().AsReadOnly();
        return new ValueTask<IReadOnlyList<MemoryEntry>>(result);
    }

    /// <inheritdoc />
    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _memories.Clear();
        }

        return default;
    }

    /// <summary>
    /// 按标识删除一条记忆。返回是否实际删除；该能力用于宿主管理界面，不属于通用 <see cref="IMemoryUnit"/> 接口。
    /// </summary>
    public ValueTask<bool> RemoveAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("记忆 ID 不能为空。", nameof(id));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return new ValueTask<bool>(_memories.Remove(id));
        }
    }

    private static int Score(MemoryEntry memory, string query, IReadOnlyList<string> terms)
    {
        var searchable = memory.Metadata is null
            ? memory.Content
            : memory.Content + " " + string.Join(" ", memory.Metadata.Values);
        var score = searchable.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ? 100 : 0;

        foreach (var term in terms)
        {
            if (searchable.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 10;
            }
        }

        return score;
    }

    private static IReadOnlyList<string> SplitTerms(string query)
    {
        return query
            .Split(
                [' ', '\t', '\r', '\n', ',', '.', ';', ':', '，', '。', '；', '：'],
                StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    private static MemoryEntry Clone(MemoryEntry memory)
    {
        return new MemoryEntry(memory.Id, memory.Content, memory.CreatedAt, memory.Metadata);
    }
}
