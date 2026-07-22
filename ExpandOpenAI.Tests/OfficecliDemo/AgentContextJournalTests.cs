using System.Text.Json;
using ExpandOpenAI.Tests.AgentBase;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OfficecliDemo;

namespace ExpandOpenAI.Tests.OfficecliDemo;

public sealed class AgentContextJournalTests
{
    [Fact]
    public async Task ContextChatClient_StreamingWritesFinalResponseSnapshot()
    {
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(AgentContextJournalTests),
            Guid.NewGuid().ToString("N"));

        try
        {
            var journal = new AgentContextJournal(
                outputDirectory,
                "stream-context.json",
                NullLogger<AgentContextJournal>.Instance);
            journal.SetStage("流式测试");
            var inner = new TestChatClient
            {
                StreamingHandler = StreamResponse,
            };
            using var client = new ContextSnapshotChatClient(inner, journal);

            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in client.GetStreamingResponseAsync(
                               [new ChatMessage(ChatRole.User, "开始")]))
            {
                updates.Add(update);
            }

            Assert.Equal("思考完成", updates.ToChatResponse().Text);
            var contextPath = Path.Combine(outputDirectory, "stream-context.json");
            using var context = JsonDocument.Parse(await File.ReadAllTextAsync(contextPath));
            Assert.Equal("model-stream-response", context.RootElement.GetProperty("event").GetString());
            Assert.Contains("思考完成", await File.ReadAllTextAsync(contextPath));
            Assert.Equal(1, inner.StreamingResponseCallCount);
            Assert.Equal(0, inner.ResponseCallCount);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }

        static async IAsyncEnumerable<ChatResponseUpdate> StreamResponse(
            IReadOnlyList<ChatMessage> messages,
            ChatOptions? options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "思考");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "完成");
        }
    }

    [Fact]
    public async Task ContextChatClient_WritesActualRequestAndResponseAtomically()
    {
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(AgentContextJournalTests),
            Guid.NewGuid().ToString("N"));

        try
        {
            var journal = new AgentContextJournal(
                outputDirectory,
                "outline-repair-agent-context.json",
                NullLogger<AgentContextJournal>.Instance);
            journal.SetStage("大纲范围 1/2");
            var inner = new TestChatClient
            {
                ResponseHandler = static (_, _, _) => Task.FromResult(
                    new ChatResponse(new ChatMessage(ChatRole.Assistant, "完成"))),
            };
            using var client = new ContextSnapshotChatClient(inner, journal);

            await client.GetResponseAsync(
            [
                new ChatMessage(ChatRole.User, "读取范围"),
                new ChatMessage(ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        "call-1",
                        "officecli",
                        new Dictionary<string, object?>
                        {
                            ["command"] = "view tender.docx text --page 1-20",
                        }),
                ]),
                new ChatMessage(ChatRole.Tool,
                [
                    new FunctionResultContent("call-1", "工具返回正文"),
                ]),
            ]);

            var contextPath = Path.Combine(outputDirectory, "outline-repair-agent-context.json");
            Assert.True(File.Exists(contextPath));
            var contextJson = await File.ReadAllTextAsync(contextPath);
            Assert.Contains("大纲范围 1/2", contextJson);
            Assert.DoesNotContain("\\u5927\\u7eb2", contextJson, StringComparison.OrdinalIgnoreCase);
            using var context = JsonDocument.Parse(contextJson);
            Assert.Equal("model-response", context.RootElement.GetProperty("event").GetString());
            Assert.Equal("大纲范围 1/2", context.RootElement.GetProperty("stage").GetString());
            Assert.Equal(4, context.RootElement.GetProperty("messageCount").GetInt32());
            var messages = context.RootElement.GetProperty("messages");
            Assert.Equal(4, messages.GetArrayLength());
            Assert.Equal(
                "function_result",
                messages[2].GetProperty("contents")[0].GetProperty("type").GetString());
            Assert.False(File.Exists(contextPath + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }
}
