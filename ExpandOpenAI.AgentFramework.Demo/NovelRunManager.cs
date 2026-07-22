using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace ExpandOpenAI.AgentFramework.Demo;

internal interface INovelRunExecutor
{
    Task<string> GetRunStateDirectoryAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<NovelStreamEvent> ExecuteRunAsync(
        string sessionId,
        string message,
        CancellationToken cancellationToken = default);
}

internal sealed class NovelRunManager(
    INovelRunExecutor executor,
    ILogger<NovelRunManager> logger) : IAsyncDisposable
{
    private readonly Dictionary<string, RunEntry> _runs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _storageGate = new(1, 1);
    private readonly object _sync = new();
    private string? _stateDirectoryPath;
    private bool _disposed;

    public async Task<NovelRunSummary> StartAsync(
        string sessionId,
        string message,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        await EnsureStorageAsync(cancellationToken).ConfigureAwait(false);

        RunEntry entry;
        lock (_sync)
        {
            var active = _runs.Values.FirstOrDefault(static run => !run.IsTerminal);
            if (active is not null)
            {
                throw new NovelRunConflictException(active.CreateSummary());
            }

            var id = Guid.NewGuid().ToString("N");
            entry = new RunEntry(
                id,
                sessionId,
                message.Trim(),
                DateTimeOffset.UtcNow,
                GetMetadataPath(id),
                GetEventsPath(id));
            _runs.Add(id, entry);
        }

        await SaveMetadataAsync(entry, cancellationToken).ConfigureAwait(false);
        entry.Execution = ExecuteAsync(entry);
        return entry.CreateSummary();
    }

    public async Task<NovelRunSummary?> GetAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        await EnsureStorageAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            return _runs.TryGetValue(runId, out var entry) ? entry.CreateSummary() : null;
        }
    }

    public async Task<NovelRunSummary?> GetActiveAsync(
        string? sessionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureStorageAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            return _runs.Values
                .Where(run => !run.IsTerminal
                    && (string.IsNullOrWhiteSpace(sessionId)
                        || string.Equals(run.SessionId, sessionId, StringComparison.Ordinal)))
                .OrderByDescending(static run => run.StartedAt)
                .Select(static run => run.CreateSummary())
                .FirstOrDefault();
        }
    }

    public async Task<IReadOnlyList<NovelRunSummary>> GetRecentAsync(
        string sessionId,
        int maximumResults = 20,
        CancellationToken cancellationToken = default)
    {
        await EnsureStorageAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            return _runs.Values
                .Where(run => string.Equals(run.SessionId, sessionId, StringComparison.Ordinal))
                .OrderByDescending(static run => run.StartedAt)
                .Take(Math.Clamp(maximumResults, 1, 100))
                .Select(static run => run.CreateSummary())
                .ToList()
                .AsReadOnly();
        }
    }

    public async Task<bool> CancelAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        await EnsureStorageAsync(cancellationToken).ConfigureAwait(false);
        RunEntry? entry;
        lock (_sync)
        {
            _runs.TryGetValue(runId, out entry);
            if (entry is null || entry.IsTerminal)
            {
                return entry is not null;
            }

            entry.Status = NovelRunStatuses.Stopping;
            entry.Cancellation.Cancel();
            entry.Pulse();
        }

        await SaveMetadataAsync(entry, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async IAsyncEnumerable<NovelRunEvent> SubscribeAsync(
        string runId,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureStorageAsync(cancellationToken).ConfigureAwait(false);
        RunEntry entry;
        lock (_sync)
        {
            if (!_runs.TryGetValue(runId, out entry!))
            {
                throw new KeyNotFoundException("找不到指定的创作任务。 ");
            }
        }

        var cursor = Math.Max(0, afterSequence);
        while (true)
        {
            IReadOnlyList<NovelRunEvent> batch;
            Task changed;
            bool terminal;
            lock (entry.Sync)
            {
                batch = entry.Events.Where(runEvent => runEvent.Sequence > cursor).ToList();
                terminal = entry.IsTerminal;
                changed = entry.Changed.Task;
            }

            foreach (var runEvent in batch)
            {
                cursor = runEvent.Sequence;
                yield return runEvent;
            }

            if (terminal)
            {
                yield break;
            }

            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        List<Task> executions;
        lock (_sync)
        {
            foreach (var run in _runs.Values.Where(static run => !run.IsTerminal))
            {
                run.Cancellation.Cancel();
            }

            executions = _runs.Values
                .Select(static run => run.Execution)
                .Where(static execution => execution is not null)
                .Cast<Task>()
                .ToList();
        }

        try
        {
            await Task.WhenAll(executions).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
        }

        foreach (var run in _runs.Values)
        {
            run.Cancellation.Dispose();
            run.PersistenceGate.Dispose();
        }

        _storageGate.Dispose();
    }

    private async Task ExecuteAsync(RunEntry entry)
    {
        await AppendAsync(entry, new NovelStreamEvent(
            "status",
            "后台任务已创建。即使页面刷新，创作仍会继续运行。"), CancellationToken.None).ConfigureAwait(false);

        try
        {
            await foreach (var update in executor.ExecuteRunAsync(
                entry.SessionId,
                entry.Command,
                entry.Cancellation.Token).ConfigureAwait(false))
            {
                await AppendAsync(entry, update, CancellationToken.None).ConfigureAwait(false);
                if (string.Equals(update.Type, "error", StringComparison.Ordinal))
                {
                    entry.Status = NovelRunStatuses.Failed;
                    entry.Error = update.Content;
                }
                else if (string.Equals(update.Type, "complete", StringComparison.Ordinal))
                {
                    entry.Status = NovelRunStatuses.Completed;
                }
            }

            if (!entry.IsTerminal)
            {
                entry.Status = NovelRunStatuses.Completed;
                await AppendAsync(entry, new NovelStreamEvent("complete", "任务已完成。"), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested)
        {
            entry.Status = NovelRunStatuses.Stopped;
            await AppendAsync(entry, new NovelStreamEvent("stopped", "任务已由用户停止。"), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Novel run {RunId} failed", entry.Id);
            entry.Status = NovelRunStatuses.Failed;
            entry.Error = exception.Message;
            await AppendAsync(entry, new NovelStreamEvent(
                "error",
                $"{exception.GetType().Name}: {exception.Message}"), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            entry.CompletedAt = DateTimeOffset.UtcNow;
            entry.Pulse();
            await SaveMetadataAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task AppendAsync(
        RunEntry entry,
        NovelStreamEvent update,
        CancellationToken cancellationToken)
    {
        NovelRunEvent runEvent;
        lock (entry.Sync)
        {
            runEvent = NovelRunEvent.From(entry.Id, ++entry.LastSequence, update);
            entry.Events.Add(runEvent);
            entry.Pulse();
        }

        var line = JsonSerializer.Serialize(runEvent, NovelJson.StreamOptions) + Environment.NewLine;
        await File.AppendAllTextAsync(
            entry.EventsPath,
            line,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureStorageAsync(CancellationToken cancellationToken)
    {
        var directory = await executor.GetRunStateDirectoryAsync(cancellationToken).ConfigureAwait(false);
        directory = Path.GetFullPath(directory);
        if (string.Equals(directory, _stateDirectoryPath, PathComparison))
        {
            return;
        }

        await _storageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.Equals(directory, _stateDirectoryPath, PathComparison))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            var loaded = new Dictionary<string, RunEntry>(StringComparer.Ordinal);
            foreach (var metadataPath in Directory.EnumerateFiles(directory, "*.meta.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var metadata = JsonSerializer.Deserialize<PersistedNovelRun>(
                    await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false),
                    NovelJson.StreamOptions);
                if (metadata is null || !metadata.IsValid)
                {
                    continue;
                }

                var entry = RunEntry.FromPersisted(
                    metadata,
                    metadataPath,
                    Path.Combine(directory, $"{metadata.Id}.events.ndjson"));
                if (File.Exists(entry.EventsPath))
                {
                    foreach (var line in File.ReadLines(entry.EventsPath))
                    {
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        var runEvent = JsonSerializer.Deserialize<NovelRunEvent>(line, NovelJson.StreamOptions);
                        if (runEvent is not null)
                        {
                            entry.Events.Add(runEvent);
                            entry.LastSequence = Math.Max(entry.LastSequence, runEvent.Sequence);
                        }
                    }
                }

                loaded[entry.Id] = entry;
            }

            lock (_sync)
            {
                _runs.Clear();
                foreach (var pair in loaded)
                {
                    _runs.Add(pair.Key, pair.Value);
                }

                _stateDirectoryPath = directory;
            }

            foreach (var interrupted in loaded.Values.Where(static run => !run.IsTerminal))
            {
                interrupted.Status = NovelRunStatuses.Interrupted;
                interrupted.Error = "服务曾在任务运行时退出，任务无法自动续跑。";
                interrupted.CompletedAt = DateTimeOffset.UtcNow;
                await AppendAsync(interrupted, new NovelStreamEvent(
                    "error",
                    interrupted.Error), cancellationToken).ConfigureAwait(false);
                await SaveMetadataAsync(interrupted, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _storageGate.Release();
        }
    }

    private async Task SaveMetadataAsync(RunEntry entry, CancellationToken cancellationToken)
    {
        await entry.PersistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var metadata = entry.CreatePersisted();
            var temporaryPath = entry.MetadataPath + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(metadata, NovelJson.StreamOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, entry.MetadataPath, overwrite: true);
        }
        finally
        {
            entry.PersistenceGate.Release();
        }
    }

    private string GetMetadataPath(string id) => Path.Combine(
        _stateDirectoryPath ?? throw new InvalidOperationException("任务存储尚未初始化。"),
        $"{id}.meta.json");

    private string GetEventsPath(string id) => Path.Combine(
        _stateDirectoryPath ?? throw new InvalidOperationException("任务存储尚未初始化。"),
        $"{id}.events.ndjson");

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed class RunEntry(
        string id,
        string sessionId,
        string command,
        DateTimeOffset startedAt,
        string metadataPath,
        string eventsPath)
    {
        public object Sync { get; } = new();
        public string Id { get; } = id;
        public string SessionId { get; } = sessionId;
        public string Command { get; } = command;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public string MetadataPath { get; } = metadataPath;
        public string EventsPath { get; } = eventsPath;
        public CancellationTokenSource Cancellation { get; } = new();
        public SemaphoreSlim PersistenceGate { get; } = new(1, 1);
        public List<NovelRunEvent> Events { get; } = [];
        public TaskCompletionSource Changed { get; private set; } = CreateSignal();
        public Task? Execution { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string Status { get; set; } = NovelRunStatuses.Running;
        public string? Error { get; set; }
        public long LastSequence { get; set; }
        public bool IsTerminal => NovelRunStatuses.IsTerminal(Status);

        public void Pulse()
        {
            lock (Sync)
            {
                var previous = Changed;
                Changed = CreateSignal();
                previous.TrySetResult();
            }
        }

        public NovelRunSummary CreateSummary() => new(
            Id,
            SessionId,
            Command,
            Status,
            StartedAt,
            CompletedAt,
            LastSequence,
            Error);

        public PersistedNovelRun CreatePersisted() => new()
        {
            Id = Id,
            SessionId = SessionId,
            Command = Command,
            Status = Status,
            StartedAt = StartedAt,
            CompletedAt = CompletedAt,
            LastSequence = LastSequence,
            Error = Error,
        };

        public static RunEntry FromPersisted(
            PersistedNovelRun persisted,
            string metadataPath,
            string eventsPath)
        {
            return new RunEntry(
                persisted.Id,
                persisted.SessionId,
                persisted.Command,
                persisted.StartedAt,
                metadataPath,
                eventsPath)
            {
                Status = persisted.Status,
                CompletedAt = persisted.CompletedAt,
                LastSequence = persisted.LastSequence,
                Error = persisted.Error,
            };
        }

        private static TaskCompletionSource CreateSignal() => new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal sealed class NovelRunConflictException(NovelRunSummary activeRun)
    : InvalidOperationException("已有创作任务正在运行。")
{
    public NovelRunSummary ActiveRun { get; } = activeRun;
}

internal static class NovelRunStatuses
{
    public const string Running = "running";
    public const string Stopping = "stopping";
    public const string Completed = "completed";
    public const string Stopped = "stopped";
    public const string Failed = "failed";
    public const string Interrupted = "interrupted";

    public static bool IsTerminal(string status) => status is Completed or Stopped or Failed or Interrupted;
}

internal sealed record NovelRunSummary(
    string Id,
    string SessionId,
    string Command,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    long LastSequence,
    string? Error)
{
    public bool IsTerminal => NovelRunStatuses.IsTerminal(Status);
}

internal sealed record NovelRunEvent(
    string RunId,
    long Sequence,
    DateTimeOffset OccurredAt,
    string Type,
    string Content,
    string? ToolCallId = null,
    string? ToolName = null,
    string? ToolArguments = null,
    bool? ToolSucceeded = null,
    NovelCompressionRecord? Compression = null)
{
    public static NovelRunEvent From(string runId, long sequence, NovelStreamEvent update) => new(
        runId,
        sequence,
        DateTimeOffset.UtcNow,
        update.Type,
        update.Content,
        update.ToolCallId,
        update.ToolName,
        update.ToolArguments,
        update.ToolSucceeded,
        update.Compression);
}

internal sealed class PersistedNovelRun
{
    public string Id { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string Status { get; set; } = NovelRunStatuses.Running;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long LastSequence { get; set; }
    public string? Error { get; set; }
    public bool IsValid => Guid.TryParseExact(Id, "N", out _)
        && Guid.TryParse(SessionId, out _)
        && !string.IsNullOrWhiteSpace(Command)
        && StartedAt != default;
}
