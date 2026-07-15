using ExpandOpenAI.AgentFramework;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.Tests.AgentBase;

public sealed class AgentMemoryTests
{
    [Fact]
    public async Task RunAsync_StoresCompressedMemoryBeforeRecallAndBypassesBusinessToolApproval()
    {
        var compressor = new TestTokenCompressor(
            [new ChatMessage(ChatRole.User, "compressed history")],
            shouldCompress: true,
            sessionMemories:
            [
                new MemoryEntry("framework", "The project uses .NET 10."),
            ]);
        string? toolResult = null;
        using var client = CreateRecallingClient(result => toolResult = result);
        IAgentSession session = new DefaultAIAgent(client, new AgentOptions
        {
            TokenCompressor = compressor,
            ToolApprovalAsync = static (_, _) =>
                throw new InvalidOperationException("内置只读工具不应进入业务审批。"),
        }).CreateSession(
        [
            new ChatMessage(ChatRole.User, "old question"),
            new ChatMessage(ChatRole.Assistant, "old answer"),
        ]);

        var response = await session.RunAsync("what framework?");

        Assert.Equal("done", response.Text);
        Assert.Contains("The project uses .NET 10.", toolResult);
        Assert.Equal(1, compressor.CallCount);
    }

    [Fact]
    public async Task RecallMemory_PrefersSessionMemoryAndSharesOnlyGlobalMemoryAcrossSessions()
    {
        var sessionUnits = new List<InMemoryMemoryUnit>();
        var globalMemory = new InMemoryMemoryUnit();
        await globalMemory.RememberAsync(
        [
            new MemoryEntry("location", "global location"),
        ]);
        var recalled = new List<string>();
        using var client = CreateRecallingClient(recalled.Add);
        AIAgent agent = new DefaultAIAgent(client, new AgentOptions
        {
            GlobalMemoryUnit = globalMemory,
            SessionMemoryUnitFactory = () =>
            {
                var unit = new InMemoryMemoryUnit();
                sessionUnits.Add(unit);
                return unit;
            },
        });
        var firstSession = agent.CreateSession();
        var secondSession = agent.CreateSession();
        await sessionUnits[0].RememberAsync(
        [
            new MemoryEntry("location", "session location"),
        ]);

        await firstSession.RunAsync("recall location");
        await secondSession.RunAsync("recall location");

        Assert.Equal(2, recalled.Count);
        Assert.Contains("session location", recalled[0]);
        Assert.DoesNotContain("global location", recalled[0]);
        Assert.Contains("global location", recalled[1]);
        Assert.DoesNotContain("session location", recalled[1]);
    }

    [Fact]
    public async Task RecallMemory_IncludesStableGlobalPreferencesWithoutLexicalQueryMatch()
    {
        var globalMemory = new InMemoryMemoryUnit();
        await globalMemory.RememberAsync(
        [
            new MemoryEntry(
                "preference-user-name",
                "用户希望被称呼为 sky。",
                metadata: new Dictionary<string, string> { ["kind"] = "cross-novel-preference" }),
        ]);
        string? recalled = null;
        using var client = CreateRecallingClient(result => recalled = result, "我是谁");
        var session = new DefaultAIAgent(client, new AgentOptions
        {
            GlobalMemoryUnit = globalMemory,
        }).CreateSession();

        await session.RunAsync("我是谁？");

        Assert.Contains("sky", recalled);
        Assert.Contains("global", recalled);
    }

    [Fact]
    public async Task RunAsync_StoresGlobalMemoryOnlyWhenCustomCompressorRequestsIt()
    {
        var globalMemory = new InMemoryMemoryUnit();
        var compressor = new TestTokenCompressor(
            [new ChatMessage(ChatRole.User, "compressed history")],
            shouldCompress: true,
            globalMemories:
            [
                new MemoryEntry("shared", "explicitly promoted global memory"),
            ]);
        using var client = new TestChatClient
        {
            ResponseHandler = (_, _, _) => Task.FromResult(Response("done")),
        };
        IAgentSession session = new DefaultAIAgent(client, new AgentOptions
        {
            TokenCompressor = compressor,
            GlobalMemoryUnit = globalMemory,
            EnableMemoryRecallTool = false,
        }).CreateSession(
        [
            new ChatMessage(ChatRole.User, "old question"),
            new ChatMessage(ChatRole.Assistant, "old answer"),
        ]);

        await session.RunAsync("new question");

        var recalled = await globalMemory.RecallAsync(new MemoryRecallRequest("promoted"));
        Assert.Collection(
            recalled,
            memory => Assert.Equal("explicitly promoted global memory", memory.Content));
    }

    [Fact]
    public async Task ClearHistoryKeepsSessionMemoryAndDestroyClearsOnlySessionLayer()
    {
        var sessionUnits = new List<InMemoryMemoryUnit>();
        var globalMemory = new InMemoryMemoryUnit();
        await globalMemory.RememberAsync([new MemoryEntry("global", "global memory")]);
        using var client = new TestChatClient();
        IAgentSession session = new DefaultAIAgent(client, new AgentOptions
        {
            GlobalMemoryUnit = globalMemory,
            SessionMemoryUnitFactory = () =>
            {
                var unit = new InMemoryMemoryUnit();
                sessionUnits.Add(unit);
                return unit;
            },
        }).CreateSession([new ChatMessage(ChatRole.User, "history")]);
        await sessionUnits[0].RememberAsync([new MemoryEntry("session", "session memory")]);

        session.ClearHistory();
        Assert.NotEmpty(await sessionUnits[0].RecallAsync(new MemoryRecallRequest(string.Empty)));

        await session.ClearMemoryAsync();
        Assert.Empty(await sessionUnits[0].RecallAsync(new MemoryRecallRequest(string.Empty)));
        await sessionUnits[0].RememberAsync([new MemoryEntry("session", "session memory")]);

        await session.DestroyAsync();

        Assert.Empty(await sessionUnits[0].RecallAsync(new MemoryRecallRequest(string.Empty)));
        Assert.NotEmpty(await globalMemory.RecallAsync(new MemoryRecallRequest(string.Empty)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.RunAsync("after destroy"));
    }

    private static TestChatClient CreateRecallingClient(
        Action<string> captureResult,
        string query = "location framework .NET")
    {
        return new TestChatClient
        {
            ResponseHandler = (messages, options, _) =>
            {
                Assert.Contains(options!.Tools!, tool => tool.Name == "recall_memory");
                var functionResult = messages
                    .SelectMany(static message => message.Contents)
                    .OfType<FunctionResultContent>()
                    .LastOrDefault();
                if (functionResult is not null)
                {
                    captureResult(functionResult.Result?.ToString() ?? string.Empty);
                    return Task.FromResult(Response("done"));
                }

                return Task.FromResult(new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [new FunctionCallContent(
                        "recall-1",
                        "recall_memory",
                        new Dictionary<string, object?> { ["query"] = query })])));
            },
        };
    }

    private static ChatResponse Response(string text)
    {
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
    }
}
