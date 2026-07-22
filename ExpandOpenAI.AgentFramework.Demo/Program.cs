using System.Text;
using System.Text.Json;
using ExpandOpenAI.AgentFramework.Demo;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Any(static argument => argument is "--workspace-self-test"))
{
    return await RunWorkspaceSelfTestAsync();
}

if (args.Any(static argument => argument is "--stream-format-self-test"))
{
    return RunStreamFormatSelfTest();
}

if (args.Any(static argument => argument is "--run-manager-self-test"))
{
    return await RunManagerSelfTestAsync();
}

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
{
    builder.WebHost.UseUrls("http://127.0.0.1:5179");
}
builder.Services.AddProblemDetails();
builder.Services.AddHttpClient("novel-http", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ExpandOpenAI-NovelWriterDemo/1.0");
})
.AddStandardResilienceHandler();
builder.Services.AddSingleton<NovelAgentHost>();
builder.Services.AddSingleton<INovelRunExecutor>(services => services.GetRequiredService<NovelAgentHost>());
builder.Services.AddSingleton<NovelRunManager>();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/bootstrap", async Task<Ok<NovelBootstrapResponse>> (NovelAgentHost host, CancellationToken cancellationToken) =>
    TypedResults.Ok(await host.GetBootstrapAsync(cancellationToken)));

api.MapPost("/settings", async Task<Results<Ok<NovelBootstrapResponse>, BadRequest<ProblemDetails>>>
    (NovelSettingsRequest request, NovelAgentHost host, CancellationToken cancellationToken) =>
{
    try
    {
        return TypedResults.Ok(await host.ConfigureAsync(request, cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return TypedResults.BadRequest(new ProblemDetails
        {
            Title = "配置无效",
            Detail = exception.Message,
            Status = StatusCodes.Status400BadRequest,
        });
    }
});

api.MapPost("/sessions", async Task<Results<Ok<NovelSessionSummary>, BadRequest<ProblemDetails>>>
    (CreateSessionRequest request, NovelAgentHost host, CancellationToken cancellationToken) =>
{
    try
    {
        return TypedResults.Ok(await host.CreateSessionAsync(request.Name, cancellationToken));
    }
    catch (InvalidOperationException exception)
    {
        return TypedResults.BadRequest(new ProblemDetails
        {
            Title = "无法创建会话",
            Detail = exception.Message,
            Status = StatusCodes.Status400BadRequest,
        });
    }
});

api.MapPost("/sessions/{sessionId}/activate", async Task<Results<Ok<NovelBootstrapResponse>, NotFound<ProblemDetails>>>
    (string sessionId, NovelAgentHost host, CancellationToken cancellationToken) =>
{
    try
    {
        await host.SelectSessionAsync(sessionId, cancellationToken);
        return TypedResults.Ok(await host.GetBootstrapAsync(cancellationToken));
    }
    catch (KeyNotFoundException exception)
    {
        return TypedResults.NotFound(new ProblemDetails
        {
            Title = "会话不存在",
            Detail = exception.Message,
            Status = StatusCodes.Status404NotFound,
        });
    }
});

api.MapPut("/sessions/{sessionId}/instructions", async Task<Results<NoContent, NotFound<ProblemDetails>>> (
    string sessionId,
    UpdateSessionInstructionsRequest request,
    NovelAgentHost host,
    CancellationToken cancellationToken) =>
{
    try
    {
        await host.UpdateSessionInstructionsAsync(sessionId, request.Instructions, cancellationToken);
        return TypedResults.NoContent();
    }
    catch (KeyNotFoundException exception)
    {
        return TypedResults.NotFound(new ProblemDetails
        {
            Title = "会话不存在",
            Detail = exception.Message,
            Status = StatusCodes.Status404NotFound,
        });
    }
});

api.MapGet("/workspace", async Task<Ok<WorkspaceExplorerResponse>> (NovelAgentHost host, CancellationToken cancellationToken) =>
    TypedResults.Ok(await host.GetWorkspaceFilesAsync(cancellationToken)));

api.MapGet("/workspace/file", async Task<Results<Ok<WorkspaceFilePreviewResponse>, BadRequest<ProblemDetails>, NotFound<ProblemDetails>>>
    (string path, NovelAgentHost host, CancellationToken cancellationToken) =>
{
    try
    {
        return TypedResults.Ok(new WorkspaceFilePreviewResponse(
            path,
            await host.ReadWorkspaceFileAsync(path, cancellationToken)));
    }
    catch (FileNotFoundException exception)
    {
        return TypedResults.NotFound(new ProblemDetails
        {
            Title = "文件不存在",
            Detail = exception.Message,
            Status = StatusCodes.Status404NotFound,
        });
    }
    catch (Exception exception) when (exception is ArgumentException
                                      or UnauthorizedAccessException
                                      or NotSupportedException)
    {
        return TypedResults.BadRequest(new ProblemDetails
        {
            Title = "无法读取工作区文件",
            Detail = exception.Message,
            Status = StatusCodes.Status400BadRequest,
        });
    }
});

api.MapGet("/memory/session", async Task<Ok<NovelInspectorResponse>> (NovelAgentHost host, CancellationToken cancellationToken) =>
    TypedResults.Ok(new NovelInspectorResponse(
        "session",
        "当前会话因上下文压缩归档的摘要。",
        await host.GetSessionMemoriesAsync(cancellationToken))));

api.MapGet("/memory/global", async Task<Ok<NovelInspectorResponse>> (NovelAgentHost host, CancellationToken cancellationToken) =>
    TypedResults.Ok(new NovelInspectorResponse(
        "global",
        "仅保存跨小说可复用的写作偏好与协作约定；不保存任何小说设定。",
        await host.GetGlobalMemoriesAsync(cancellationToken))));

api.MapPut("/memory/global/{id}", async Task<Results<NoContent, BadRequest<ProblemDetails>>> (
    string id,
    UpsertMemoryRequest request,
    NovelAgentHost host,
    CancellationToken cancellationToken) =>
{
    try
    {
        await host.UpsertGlobalMemoryAsync(id, request.Content, cancellationToken);
        return TypedResults.NoContent();
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
    {
        return TypedResults.BadRequest(new ProblemDetails
        {
            Title = "无法保存全局记忆",
            Detail = exception.Message,
            Status = StatusCodes.Status400BadRequest,
        });
    }
});

api.MapDelete("/memory/{scope}/{id}", async Task<Results<NoContent, BadRequest<ProblemDetails>, NotFound<ProblemDetails>>> (
    string scope,
    string id,
    NovelAgentHost host,
    CancellationToken cancellationToken) =>
{
    try
    {
        return await host.DeleteMemoryAsync(scope, id, cancellationToken)
            ? TypedResults.NoContent()
            : TypedResults.NotFound(new ProblemDetails
            {
                Title = "记忆不存在",
                Status = StatusCodes.Status404NotFound,
            });
    }
    catch (ArgumentException exception)
    {
        return TypedResults.BadRequest(new ProblemDetails
        {
            Title = "记忆范围无效",
            Detail = exception.Message,
            Status = StatusCodes.Status400BadRequest,
        });
    }
});

api.MapGet("/context", async Task<Ok<NovelContextInspectorResponse>> (
    NovelAgentHost host,
    NovelRunManager runs,
    CancellationToken cancellationToken) =>
{
    var context = await host.GetContextDiagnosticsAsync(cancellationToken);
    var recentRuns = await runs.GetRecentAsync(context.SessionId, cancellationToken: cancellationToken);
    return TypedResults.Ok(new NovelContextInspectorResponse(context, recentRuns));
});

api.MapPost("/runs", async Task<Results<Accepted<NovelRunSummary>, BadRequest<ProblemDetails>, Conflict<ProblemDetails>>>
    (CreateRunRequest request, NovelRunManager runs, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.SessionId) || string.IsNullOrWhiteSpace(request.Message))
    {
        return TypedResults.BadRequest(new ProblemDetails
        {
            Title = "创作任务无效",
            Detail = "会话 ID 和创作指令不能为空。",
            Status = StatusCodes.Status400BadRequest,
        });
    }

    try
    {
        var run = await runs.StartAsync(request.SessionId, request.Message, cancellationToken);
        return TypedResults.Accepted($"/api/runs/{run.Id}", run);
    }
    catch (NovelRunConflictException exception)
    {
        return TypedResults.Conflict(new ProblemDetails
        {
            Title = "已有任务正在运行",
            Detail = $"任务 {exception.ActiveRun.Id} 仍在运行，请先等待或停止它。",
            Status = StatusCodes.Status409Conflict,
            Extensions = { ["activeRunId"] = exception.ActiveRun.Id },
        });
    }
});

api.MapGet("/runs/active", async Task<Ok<NovelRunSummary?>> (
    string? sessionId,
    NovelRunManager runs,
    CancellationToken cancellationToken) =>
    TypedResults.Ok<NovelRunSummary?>(await runs.GetActiveAsync(sessionId, cancellationToken)));

api.MapGet("/runs/recent/{sessionId}", async Task<Ok<IReadOnlyList<NovelRunSummary>>> (
    string sessionId,
    NovelRunManager runs,
    CancellationToken cancellationToken) =>
    TypedResults.Ok(await runs.GetRecentAsync(sessionId, cancellationToken: cancellationToken)));

api.MapGet("/runs/{runId}", async Task<Results<Ok<NovelRunSummary>, NotFound<ProblemDetails>>> (
    string runId,
    NovelRunManager runs,
    CancellationToken cancellationToken) =>
{
    var run = await runs.GetAsync(runId, cancellationToken);
    return run is null
        ? TypedResults.NotFound(new ProblemDetails
        {
            Title = "任务不存在",
            Status = StatusCodes.Status404NotFound,
        })
        : TypedResults.Ok(run);
});

api.MapGet("/runs/{runId}/events", async (
    string runId,
    long? after,
    NovelRunManager runs,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    if (await runs.GetAsync(runId, cancellationToken) is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = "任务不存在",
            Status = StatusCodes.Status404NotFound,
        }, cancellationToken);
        return;
    }

    context.Response.ContentType = "application/x-ndjson; charset=utf-8";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers["X-Accel-Buffering"] = "no";
    await foreach (var runEvent in runs.SubscribeAsync(runId, after ?? 0, cancellationToken))
    {
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            runEvent,
            NovelJson.StreamOptions,
            cancellationToken);
        await context.Response.WriteAsync("\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }
});

api.MapDelete("/runs/{runId}", async Task<Results<Accepted<NovelRunSummary>, NotFound<ProblemDetails>>> (
    string runId,
    NovelRunManager runs,
    CancellationToken cancellationToken) =>
{
    if (!await runs.CancelAsync(runId, cancellationToken))
    {
        return TypedResults.NotFound(new ProblemDetails
        {
            Title = "任务不存在",
            Status = StatusCodes.Status404NotFound,
        });
    }

    var run = await runs.GetAsync(runId, cancellationToken)
        ?? throw new InvalidOperationException("任务取消后无法读取状态。 ");
    return TypedResults.Accepted($"/api/runs/{runId}", run);
});

api.MapPost("/chat/stream", async (
    ChatRequest request,
    NovelRunManager runs,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = "创作指令不能为空",
            Status = StatusCodes.Status400BadRequest,
        }, cancellationToken);
        return;
    }

    if (string.IsNullOrWhiteSpace(request.SessionId))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = "会话 ID 不能为空",
            Status = StatusCodes.Status400BadRequest,
        }, cancellationToken);
        return;
    }

    NovelRunSummary run;
    try
    {
        run = await runs.StartAsync(request.SessionId, request.Message, cancellationToken);
    }
    catch (NovelRunConflictException exception)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = "已有任务正在运行",
            Detail = $"任务 {exception.ActiveRun.Id} 仍在运行。",
            Status = StatusCodes.Status409Conflict,
        }, cancellationToken);
        return;
    }

    context.Response.ContentType = "application/x-ndjson; charset=utf-8";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers["X-Accel-Buffering"] = "no";
    await foreach (var update in runs.SubscribeAsync(run.Id, 0, cancellationToken))
    {
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            update,
            NovelJson.StreamOptions,
            cancellationToken);
        await context.Response.WriteAsync("\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }
});

app.MapFallbackToFile("index.html");
await app.RunAsync();
return 0;

static int RunStreamFormatSelfTest()
{
    var formatter = new StreamingTranscriptFormatter();
    var transcript = string.Concat(
        formatter.Format(new ChatResponseUpdate(
            ChatRole.Assistant,
            [new TextReasoningContent("先检查已有章节。")])),
        formatter.Format(new ChatResponseUpdate(
            ChatRole.Assistant,
            [new TextReasoningContent("然后整理人物关系。")])),
        formatter.Format(new ChatResponseUpdate(
            ChatRole.Assistant,
            [new TextContent("我会先读取设定文档。")] )));

    var thinkingMarkerCount = transcript.Split("[思考]", StringSplitOptions.None).Length - 1;
    if (thinkingMarkerCount != 1
        || transcript != "[思考]\n先检查已有章节。然后整理人物关系。\n\n我会先读取设定文档。")
    {
        Console.Error.WriteLine($"Stream format self-test failed: {transcript}");
        return 1;
    }

    var streamJson = JsonSerializer.Serialize(
        new NovelStreamEvent("heartbeat", "任务仍在运行。"),
        NovelJson.StreamOptions);
    if (!streamJson.Contains("\"type\":\"heartbeat\"", StringComparison.Ordinal)
        || !streamJson.Contains("\"content\":", StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"Stream JSON self-test failed: {streamJson}");
        return 1;
    }

    Console.WriteLine("Stream format self-test passed: reasoning chunks are grouped once.");
    return 0;
}

static async Task<int> RunManagerSelfTestAsync()
{
    var root = Path.Combine(Path.GetTempPath(), $"expandopenai-run-manager-{Guid.NewGuid():N}");
    try
    {
        var executor = new SelfTestRunExecutor(root);
        await using var manager = new NovelRunManager(executor, NullLogger<NovelRunManager>.Instance);
        var sessionId = Guid.NewGuid().ToString("D");
        var run = await manager.StartAsync(sessionId, "继续创作第一章");

        long disconnectedAfter;
        await using (var firstSubscription = manager.SubscribeAsync(run.Id, 0).GetAsyncEnumerator())
        {
            if (!await firstSubscription.MoveNextAsync())
            {
                throw new InvalidOperationException("首次订阅没有收到任务事件。 ");
            }

            disconnectedAfter = firstSubscription.Current.Sequence;
        }

        var replayed = new List<NovelRunEvent>();
        await foreach (var runEvent in manager.SubscribeAsync(run.Id, disconnectedAfter))
        {
            replayed.Add(runEvent);
        }

        var completed = await manager.GetAsync(run.Id)
            ?? throw new InvalidOperationException("任务完成后无法查询状态。 ");
        if (completed.Status != NovelRunStatuses.Completed
            || replayed.All(static runEvent => runEvent.Type != "delta")
            || replayed.All(static runEvent => runEvent.Type != "complete")
            || Directory.EnumerateFiles(root, "*.events.ndjson").Count() != 1)
        {
            throw new InvalidOperationException("后台任务重连、完成状态或事件持久化不符合预期。 ");
        }

        Console.WriteLine("Novel run manager self-test passed: detached execution, replay, and persistence work.");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Novel run manager self-test failed: {exception}");
        return 1;
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static async Task<int> RunWorkspaceSelfTestAsync()
{
    var workspacePath = Path.Combine(Path.GetTempPath(), $"expandopenai-novel-demo-{Guid.NewGuid():N}");

    try
    {
        using var workspace = new NovelWorkspace(workspacePath);
        await workspace.WriteTextFileAsync(
            "planning/outline.md",
            "# 星港计划\n\n主角在第一章收到一封匿名信。 ");
        try
        {
            await workspace.WriteTextFileAsync("planning/outline.md", "不应静默覆盖。 ");
            throw new InvalidOperationException("已有文件没有要求显式覆盖确认。 ");
        }
        catch (IOException)
        {
        }

        await workspace.WriteTextFileAsync(
            "planning/outline.md",
            "# 星港计划\n\n主角在第一章收到一封匿名信。 ",
            overwrite: true);
        var content = await workspace.ReadTextFileAsync("planning/outline.md");
        var listing = await workspace.ListFilesAsync(".");
        var fileEntries = await workspace.GetFileEntriesAsync();
        if (!content.Contains("星港计划", StringComparison.Ordinal)
            || !listing.Contains("planning/outline.md", StringComparison.Ordinal)
            || fileEntries.Single().Path != "planning/outline.md"
            || fileEntries.Single().Extension != "md")
        {
            throw new InvalidOperationException("小说工作区读写或资源管理器文件条目没有返回预期结果。 ");
        }

        try
        {
            await workspace.WriteTextFileAsync("../outside.md", "must not be written");
            throw new InvalidOperationException("工作区路径逃逸没有被拒绝。 ");
        }
        catch (UnauthorizedAccessException)
        {
        }

        try
        {
            await workspace.FetchHttpAsync("file:///not-an-http-resource");
            throw new InvalidOperationException("非 HTTP URL 没有被拒绝。 ");
        }
        catch (NotSupportedException)
        {
        }

        var writeTool = workspace.CreateTools()
            .OfType<AIFunction>()
            .Single(static tool => tool.Name == "write_workspace_file");
        await writeTool.InvokeAsync(new AIFunctionArguments(
            new Dictionary<string, object?>
            {
                ["relativePath"] = "planning/from-tool.md",
                ["content"] = "通过 AIFunction 调用创建的文件。",
            }));
        if (!File.Exists(Path.Combine(workspace.RootPath, "planning", "from-tool.md")))
        {
            throw new InvalidOperationException("write_workspace_file 无法通过模型工具调用参数执行。 ");
        }

        await workspace.WriteTextFileAsync("chapters/incremental.md", "第一段");
        await workspace.AppendTextFileAsync(
            "chapters/incremental.md",
            "\n第二段",
            expectedLengthCharacters: 3);
        await workspace.ReplaceTextAsync(
            "chapters/incremental.md",
            "第二段",
            "修订后的第二段");
        var incremental = await workspace.ReadTextFileAsync("chapters/incremental.md");
        if (!incremental.Contains("第一段\n修订后的第二段", StringComparison.Ordinal)
            || !incremental.Contains("SHA-256：", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("增量追加、精确替换或文件版本信息不符合预期。 ");
        }

        try
        {
            await workspace.AppendTextFileAsync(
                "chapters/incremental.md",
                "不应写入",
                expectedLengthCharacters: 1);
            throw new InvalidOperationException("追加工具没有拒绝过期的文件长度。 ");
        }
        catch (IOException)
        {
        }

        var sessionStore = new NovelSessionStore(workspace.RootPath);
        var original = new PersistedNovelSession
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = "第一卷",
            WorkspaceDirectoryName = "第一卷-a1b2c3d4",
            LastOpenedAt = DateTimeOffset.UtcNow,
            History =
            [
                new PersistedChatMessage
                {
                    Role = "user",
                    Content = "[Earlier conversation turn summary]\n先建立世界观。",
                    IsCompressedTurnSummary = true,
                    SummaryMemoryId = "turn-001",
                },
            ],
            Memories =
            [
                new PersistedMemoryEntry
                {
                    Id = "turn-001",
                    Content = "主角不相信匿名信的来源。",
                    CreatedAt = DateTimeOffset.UtcNow,
                },
            ],
        };
        var secondNovel = new PersistedNovelSession
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = "第二卷",
            WorkspaceDirectoryName = "第二卷-e5f6a7b8",
            LastOpenedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            Memories =
            [
                new PersistedMemoryEntry
                {
                    Id = "turn-002",
                    Content = "第二卷的主角从未收到匿名信。",
                    CreatedAt = DateTimeOffset.UtcNow,
                },
            ],
        };
        await sessionStore.SaveAsync([original, secondNovel]);
        var restored = await sessionStore.LoadAsync();
        var restoredFirstNovel = restored.Single(static session => session.Name == "第一卷");
        var restoredSecondNovel = restored.Single(static session => session.Name == "第二卷");
        var restoredSummary = restoredFirstNovel.History.Single().ToChatMessage();
        if (restored.Count != 2
            || restoredSummary.AdditionalProperties?.TryGetValue(
                "expandopenai.agent.turn_summary",
                out var marker) != true
            || marker is not true
            || restoredFirstNovel.WorkspaceDirectoryName != "第一卷-a1b2c3d4"
            || restoredSecondNovel.WorkspaceDirectoryName != "第二卷-e5f6a7b8"
            || restoredFirstNovel.Memories.Single().Id != "turn-001"
            || restoredSecondNovel.Memories.Single().Id != "turn-002")
        {
            throw new InvalidOperationException("会话历史、压缩摘要标记或小说间的记忆隔离没有正确恢复。 ");
        }

        using var firstNovelWorkspace = new NovelWorkspace(Path.Combine(
            workspace.RootPath,
            restoredFirstNovel.WorkspaceDirectoryName));
        using var secondNovelWorkspace = new NovelWorkspace(Path.Combine(
            workspace.RootPath,
            restoredSecondNovel.WorkspaceDirectoryName));
        await firstNovelWorkspace.WriteTextFileAsync("chapters/chapter-01.md", "第一部小说的第一章。");
        await secondNovelWorkspace.WriteTextFileAsync("chapters/chapter-01.md", "第二部小说的第一章。");
        var firstChapter = await firstNovelWorkspace.ReadTextFileAsync("chapters/chapter-01.md");
        var secondChapter = await secondNovelWorkspace.ReadTextFileAsync("chapters/chapter-01.md");
        if (firstNovelWorkspace.RootPath == secondNovelWorkspace.RootPath
            || !firstChapter.Contains("第一部小说", StringComparison.Ordinal)
            || !secondChapter.Contains("第二部小说", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("不同会话的小说文件没有隔离到根工作区下的独立子目录。 ");
        }

        var globalStore = new NovelGlobalMemoryStore(workspace.RootPath);
        await globalStore.SaveAsync([new PersistedMemoryEntry
        {
            Id = "preference-narrative-style",
            Content = "默认使用冷峻克制的第三人称有限视角。",
            CreatedAt = DateTimeOffset.UtcNow,
        }]);
        if ((await globalStore.LoadAsync()).Single().Id != "preference-narrative-style")
        {
            throw new InvalidOperationException("跨小说偏好记忆没有正确持久化。 ");
        }

        Console.WriteLine("Novel workspace self-test passed: tools, per-session folders, isolated memories, compressed history, and cross-novel preferences.");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Novel workspace self-test failed: {exception}");
        return 1;
    }
    finally
    {
        if (Directory.Exists(workspacePath))
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }
}

internal sealed record NovelSettingsRequest(
    string Endpoint,
    string? ApiKey,
    string ChatModel,
    string ChatRequestPath,
    string NovelWorkspacePath,
    int? CompressionTokenThreshold,
    int? MaximumOutputTokens,
    string? SystemPrompt);

internal sealed record CreateSessionRequest(string? Name);

internal sealed record UpdateSessionInstructionsRequest(string? Instructions);

internal sealed record UpsertMemoryRequest(string Content);

internal sealed record ChatRequest(string? SessionId, string Message);

internal sealed record CreateRunRequest(string SessionId, string Message);

internal sealed record NovelInspectorResponse(
    string Kind,
    string Description,
    IReadOnlyList<NovelMemorySnippet> Memories);

internal sealed record WorkspaceExplorerResponse(
    string RootPath,
    IReadOnlyList<NovelWorkspaceFileEntry> Files);

internal sealed record WorkspaceFilePreviewResponse(string Path, string Content);

internal static class NovelJson
{
    public static JsonSerializerOptions StreamOptions { get; } = new(JsonSerializerDefaults.Web);
}

internal sealed class SelfTestRunExecutor(string stateDirectory) : INovelRunExecutor
{
    public Task<string> GetRunStateDirectoryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(stateDirectory);
        return Task.FromResult(stateDirectory);
    }

    public async IAsyncEnumerable<NovelStreamEvent> ExecuteRunAsync(
        string sessionId,
        string message,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new NovelStreamEvent("status", $"会话 {sessionId} 已开始处理：{message}");
        await Task.Delay(25, cancellationToken);
        yield return new NovelStreamEvent("delta", "第一段正文");
        yield return new NovelStreamEvent("complete", "第一段正文");
    }
}
