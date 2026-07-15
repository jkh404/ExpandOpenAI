using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using ExpandOpenAI.AgentFramework.Demo;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.AI;

if (args.Any(static argument => argument is "--workspace-self-test"))
{
    return await RunWorkspaceSelfTestAsync();
}

if (args.Any(static argument => argument is "--stream-format-self-test"))
{
    return RunStreamFormatSelfTest();
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5179");
builder.Services.AddProblemDetails();
builder.Services.AddRateLimiter(options =>
{
    options.AddConcurrencyLimiter("novel-run", limiter =>
    {
        limiter.PermitLimit = 1;
        limiter.QueueLimit = 0;
        limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});
builder.Services.AddHttpClient("novel-http", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ExpandOpenAI-NovelWriterDemo/1.0");
})
.AddStandardResilienceHandler();
builder.Services.AddSingleton<NovelAgentHost>();

var app = builder.Build();
app.UseExceptionHandler();
app.UseStaticFiles();
app.UseRateLimiter();

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

api.MapPost("/chat/stream", async (
    ChatRequest request,
    NovelAgentHost host,
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

    context.Response.ContentType = "application/x-ndjson; charset=utf-8";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers["X-Accel-Buffering"] = "no";
    await foreach (var update in host.RunStreamAsync(request.SessionId, request.Message, cancellationToken))
    {
        await JsonSerializer.SerializeAsync(context.Response.Body, update, cancellationToken: cancellationToken);
        await context.Response.WriteAsync("\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }
}).RequireRateLimiting("novel-run");

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

    Console.WriteLine("Stream format self-test passed: reasoning chunks are grouped once.");
    return 0;
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

        var sessionStore = new NovelSessionStore(workspace.RootPath);
        var original = new PersistedNovelSession
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = "第一卷",
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
            || restoredFirstNovel.Memories.Single().Id != "turn-001"
            || restoredSecondNovel.Memories.Single().Id != "turn-002")
        {
            throw new InvalidOperationException("会话历史、压缩摘要标记或小说间的记忆隔离没有正确恢复。 ");
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

        Console.WriteLine("Novel workspace self-test passed: tools, scope, isolated sessions, compressed memories, and cross-novel preferences.");
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
    int? CompressionTokenThreshold);

internal sealed record CreateSessionRequest(string? Name);

internal sealed record ChatRequest(string? SessionId, string Message);

internal sealed record NovelInspectorResponse(
    string Kind,
    string Description,
    IReadOnlyList<NovelMemorySnippet> Memories);

internal sealed record WorkspaceExplorerResponse(
    string RootPath,
    IReadOnlyList<NovelWorkspaceFileEntry> Files);

internal sealed record WorkspaceFilePreviewResponse(string Path, string Content);
