using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace OfficecliDemo;

internal sealed class AgentContextJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        MaxDepth = 32,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _filePath;
    private readonly ILogger<AgentContextJournal> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private string _stage = "startup";

    public AgentContextJournal(
        string outputDirectory,
        string fileName,
        ILogger<AgentContextJournal> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        Directory.CreateDirectory(outputDirectory);
        _filePath = Path.Combine(outputDirectory, fileName);
        _logger = logger;
    }

    public string FilePath => _filePath;

    public string CurrentStage => _stage;

    public void SetStage(string stage)
    {
        _stage = string.IsNullOrWhiteSpace(stage) ? "unknown" : stage;
    }

    public async ValueTask WriteAsync(
        string eventName,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyDictionary<string, object?>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = new
        {
            schemaVersion = 1,
            updatedAtUtc = DateTimeOffset.UtcNow,
            stage = CurrentStage,
            @event = eventName,
            messageCount = messages.Count,
            estimatedTokens = LoggingTokenCompressor.Estimate(messages),
            metadata,
            messages = messages.Select(CreateMessageSnapshot).ToList(),
        };

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaryPath = _filePath + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            await File.WriteAllTextAsync(
                temporaryPath,
                json,
                Encoding.UTF8,
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "[Context] 实时上下文写入失败：{Path}",
                _filePath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            _writeLock.Release();
        }
    }

    private static object CreateMessageSnapshot(ChatMessage message, int index)
    {
        return new
        {
            index,
            role = message.Role.ToString(),
            message.AuthorName,
            message.MessageId,
            message.CreatedAt,
            contents = message.Contents.Select(CreateContentSnapshot).ToList(),
        };
    }

    private static object CreateContentSnapshot(AIContent content)
    {
        return content switch
        {
            TextContent text => new
            {
                type = "text",
                text = text.Text,
            },
            TextReasoningContent reasoning => new
            {
                type = "reasoning",
                text = reasoning.Text,
            },
            FunctionCallContent call => new
            {
                type = "function_call",
                callId = call.CallId,
                name = call.Name,
                arguments = SafeSerialize(call.Arguments),
                exception = call.Exception?.ToString(),
            },
            FunctionResultContent result => new
            {
                type = "function_result",
                callId = result.CallId,
                result = SafeSerialize(result.Result),
                exception = result.Exception?.ToString(),
            },
            _ => new
            {
                type = content.GetType().Name,
                text = content.ToString(),
            },
        };
    }

    private static string SafeSerialize(object? value)
    {
        try
        {
            return value switch
            {
                null => "null",
                string text => text,
                JsonElement element => element.GetRawText(),
                _ => JsonSerializer.Serialize(value, value.GetType()),
            };
        }
        catch
        {
            return value?.ToString() ?? "null";
        }
    }
}

internal sealed class ContextSnapshotChatClient(
    IChatClient inner,
    AgentContextJournal journal) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var requestMessages = messages.ToList().AsReadOnly();
        await journal.WriteAsync(
            "model-request",
            requestMessages,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        try
        {
            var response = await inner.GetResponseAsync(
                requestMessages,
                options,
                cancellationToken).ConfigureAwait(false);
            await journal.WriteAsync(
                "model-response",
                requestMessages.Concat(response.Messages).ToList().AsReadOnly(),
                new Dictionary<string, object?>
                {
                    ["finishReason"] = response.FinishReason?.ToString(),
                    ["inputTokens"] = response.Usage?.InputTokenCount,
                    ["outputTokens"] = response.Usage?.OutputTokenCount,
                },
                cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch (Exception exception)
        {
            await journal.WriteAsync(
                "model-error",
                requestMessages,
                new Dictionary<string, object?>
                {
                    ["exception"] = exception.ToString(),
                },
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var requestMessages = messages.ToList().AsReadOnly();
        var updates = new List<ChatResponseUpdate>();
        var stopwatch = Stopwatch.StartNew();
        var lastProgressWrite = TimeSpan.Zero;
        await journal.WriteAsync(
            "model-stream-request",
            requestMessages,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var completed = false;
        try
        {
            await foreach (var update in inner.GetStreamingResponseAsync(
                               requestMessages,
                               options,
                               cancellationToken).ConfigureAwait(false))
            {
                updates.Add(update);
                if (stopwatch.Elapsed - lastProgressWrite >= TimeSpan.FromSeconds(5))
                {
                    await journal.WriteAsync(
                        "model-stream-progress",
                        requestMessages.Concat(updates.ToChatResponse().Messages).ToList().AsReadOnly(),
                        new Dictionary<string, object?>
                        {
                            ["updateCount"] = updates.Count,
                            ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                        },
                        cancellationToken).ConfigureAwait(false);
                    lastProgressWrite = stopwatch.Elapsed;
                }

                yield return update;
            }
            completed = true;
        }
        finally
        {
            await journal.WriteAsync(
                completed ? "model-stream-response" : "model-stream-interrupted",
                requestMessages.Concat(updates.ToChatResponse().Messages).ToList().AsReadOnly(),
                new Dictionary<string, object?>
                {
                    ["updateCount"] = updates.Count,
                    ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                },
                completed ? cancellationToken : CancellationToken.None).ConfigureAwait(false);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return serviceType.IsInstanceOfType(this)
            ? this
            : inner.GetService(serviceType, serviceKey);
    }

    public void Dispose()
    {
        // inner 由所属 OfficeCliAgentRuntime 统一释放。
    }
}

internal sealed class TokenSpeedLoggingChatClient(
    IChatClient inner,
    AgentContextJournal journal,
    ILogger<TokenSpeedLoggingChatClient> logger) : IChatClient
{
    private int _requestCount;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var requestNumber = Interlocked.Increment(ref _requestCount);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await inner.GetResponseAsync(
                messages,
                options,
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            LogSpeed(requestNumber, response.Usage, stopwatch.Elapsed);
            return response;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            logger.LogError(
                exception,
                "[Model][TokenSpeed] 模型请求 #{RequestNumber} 失败：阶段={Stage}，耗时={ElapsedMs} ms",
                requestNumber,
                journal.CurrentStage,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var requestNumber = Interlocked.Increment(ref _requestCount);
        var stopwatch = Stopwatch.StartNew();
        UsageDetails? usage = null;
        var progress = new StreamingRequestProgress();
        var completed = false;
        try
        {
            await foreach (var update in inner.GetStreamingResponseAsync(
                               messages,
                               options,
                               cancellationToken).ConfigureAwait(false))
            {
                progress.Observe();
                if (update.Contents.OfType<UsageContent>().LastOrDefault()?.Details is { } details)
                {
                    usage = details;
                }

                yield return update;
            }
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                stopwatch.Stop();
                Console.WriteLine();
                logger.LogWarning(
                    "[Model][Streaming] 模型请求 #{RequestNumber} 流式输出中断：阶段={Stage}，耗时={ElapsedMs} ms，已接收更新={UpdateCount}",
                    requestNumber,
                    journal.CurrentStage,
                    stopwatch.ElapsedMilliseconds,
                    progress.UpdateCount);
            }
        }

        stopwatch.Stop();
        Console.WriteLine();
        LogSpeed(requestNumber, usage, stopwatch.Elapsed);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return serviceType.IsInstanceOfType(this)
            ? this
            : inner.GetService(serviceType, serviceKey);
    }

    public void Dispose()
    {
        // inner 由所属 OfficeCliAgentRuntime 统一释放。
    }

    private void LogSpeed(int requestNumber, UsageDetails? usage, TimeSpan elapsed)
    {
        if (usage is null)
        {
            logger.LogInformation(
                "[Model][TokenSpeed] 模型请求 #{RequestNumber} 完成：阶段={Stage}，耗时={ElapsedMs} ms，usage=missing",
                requestNumber,
                journal.CurrentStage,
                (long)elapsed.TotalMilliseconds);
            return;
        }

        var outputTokens = usage.OutputTokenCount ?? 0;
        var tokensPerSecond = elapsed.TotalSeconds > 0
            ? outputTokens / elapsed.TotalSeconds
            : 0d;
        logger.LogInformation(
            "[Model][TokenSpeed] 模型请求 #{RequestNumber} 完成：阶段={Stage}，耗时={ElapsedMs} ms，InputTokens={InputTokens}，OutputTokens={OutputTokens}，ReasoningTokens={ReasoningTokens}，平均输出速度={TokensPerSecond:F2} tokens/s",
            requestNumber,
            journal.CurrentStage,
            (long)elapsed.TotalMilliseconds,
            usage.InputTokenCount,
            usage.OutputTokenCount,
            usage.ReasoningTokenCount,
            tokensPerSecond);
    }

    private sealed class StreamingRequestProgress
    {
        private int _updateCount;

        public int UpdateCount => Volatile.Read(ref _updateCount);

        public void Observe()
        {
            Interlocked.Increment(ref _updateCount);
        }
    }
}
