using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OfficecliDemo;

namespace ExpandOpenAI.Tests.OfficecliDemo;

public sealed class LiveAgentOutputWriterTests
{
    [Fact]
    public void Observe_WritesStreamingTextDirectlyWithoutLoggerPrefixesBetweenChunks()
    {
        using var console = new StringWriter();
        var writer = new OfficeCliAgentRuntime.LiveAgentOutputWriter(
            NullLogger.Instance,
            console,
            showReasoning: true,
            showOutput: true,
            "OutlineRepairAgent",
            taskNumber: 1,
            "大纲全局概况");

        writer.Observe(new ChatResponseUpdate(
            ChatRole.Assistant,
            [new TextReasoningContent("先读取统计。")]));
        writer.Observe(new ChatResponseUpdate(ChatRole.Assistant, "全局概况"));
        writer.Observe(new ChatResponseUpdate(ChatRole.Assistant, "备忘录"));
        writer.Complete(TimeSpan.FromSeconds(1));

        var output = console.ToString();
        Assert.Contains("AI 思考流", output);
        Assert.Contains("AI 普通输出流", output);
        Assert.Contains("全局概况备忘录", output);
        Assert.DoesNotContain("info:", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[AI][普通输出流]", output, StringComparison.Ordinal);
    }
}
