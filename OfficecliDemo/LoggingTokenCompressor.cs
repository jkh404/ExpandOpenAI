using System.Diagnostics;
using System.Text.Json;
using ExpandOpenAI;
using ExpandOpenAI.AgentFramework;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace OfficecliDemo;

internal sealed class LoggingTokenCompressor(
    ITokenCompressor inner,
    int configuredThreshold,
    int configuredMessageThreshold,
    AgentContextJournal contextJournal,
    ILogger<LoggingTokenCompressor> logger) : ITokenCompressor
{
    public bool ShouldCompress(IReadOnlyList<ChatMessage> messages)
    {
        var estimate = Estimate(messages);
        var shouldCompress = inner.ShouldCompress(messages);
        logger.LogInformation(
            "[Compression] 压缩检查：历史消息 {MessageCount} 条，估算 {TokenEstimate} tokens；历史阈值 {Threshold}，单消息阈值 {MessageThreshold}；结果：{Decision}",
            messages.Count,
            estimate,
            configuredThreshold,
            configuredMessageThreshold,
            shouldCompress ? "触发压缩" : "暂不压缩");
        return shouldCompress;
    }

    public async ValueTask<TokenCompressionResult> CompressAsync(
        TokenCompressionContext context,
        IChatClient chatClient,
        CancellationToken cancellationToken = default)
    {
        var beforeEstimate = Estimate(context.Messages);
        var stopwatch = Stopwatch.StartNew();
        if (context.Reason == TokenCompressionReason.ModelRequested)
        {
            logger.LogWarning(
                "[Tool] 内置工具 request_context_compaction 已触发；框架将压缩本轮上下文并从检查点继续执行");
        }

        logger.LogWarning(
            "[Compression] 开始压缩。原因：{Reason}；压缩前消息 {MessageCount} 条，估算 {TokenEstimate} tokens",
            DescribeReason(context.Reason),
            context.Messages.Count,
            beforeEstimate);
        await contextJournal.WriteAsync(
            "compression-start",
            context.Messages,
            new Dictionary<string, object?>
            {
                ["reason"] = DescribeReason(context.Reason),
            },
            cancellationToken).ConfigureAwait(false);

        try
        {
            var compressionChatClient = chatClient.GetService(typeof(TokenSpeedLoggingChatClient))
                as IChatClient ?? chatClient;
            var result = await inner.CompressAsync(
                context,
                compressionChatClient,
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            logger.LogWarning(
                "[Compression] 压缩完成，耗时 {ElapsedMs} ms；消息 {BeforeCount} -> {AfterCount}，估算 tokens {BeforeTokens} -> {AfterTokens}；新增会话记忆 {SessionMemories} 条，全局记忆 {GlobalMemories} 条",
                stopwatch.ElapsedMilliseconds,
                context.Messages.Count,
                result.Messages.Count,
                beforeEstimate,
                Estimate(result.Messages),
                result.SessionMemoriesToStore.Count,
                result.GlobalMemoriesToStore.Count);
            await contextJournal.WriteAsync(
                "compression-complete",
                result.Messages,
                new Dictionary<string, object?>
                {
                    ["reason"] = DescribeReason(context.Reason),
                    ["beforeTokens"] = beforeEstimate,
                    ["afterTokens"] = Estimate(result.Messages),
                },
                cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            logger.LogError(
                exception,
                "[Compression] 压缩失败，原因：{Reason}，耗时 {ElapsedMs} ms",
                DescribeReason(context.Reason),
                stopwatch.ElapsedMilliseconds);
            await contextJournal.WriteAsync(
                "compression-error",
                context.Messages,
                new Dictionary<string, object?>
                {
                    ["reason"] = DescribeReason(context.Reason),
                    ["exception"] = exception.ToString(),
                },
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public static int Estimate(IReadOnlyList<ChatMessage> messages)
    {
        long estimate = 0;
        foreach (var message in messages)
        {
            estimate += 4;
            foreach (var content in message.Contents)
            {
                estimate += content switch
                {
                    TextContent text => EstimateText(text.Text),
                    TextReasoningContent reasoning => EstimateText(reasoning.Text),
                    FunctionCallContent call => EstimateText(call.Name)
                        + EstimateText(JsonSerializer.Serialize(call.Arguments)),
                    FunctionResultContent result => EstimateText(
                        DemoLogFormatter.Serialize(result.Result, 100_000)),
                    _ => EstimateText(content.ToString()),
                };
            }
        }

        return (int)Math.Min(int.MaxValue, Math.Max(0, estimate));
    }

    private static int EstimateText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var ascii = 0;
        var nonAscii = 0;
        foreach (var character in text)
        {
            if (character <= 0x7f)
            {
                ascii++;
            }
            else
            {
                nonAscii++;
            }
        }

        return Math.Max(1, (int)Math.Ceiling(ascii / 4d + nonAscii * 0.8d));
    }

    private static string DescribeReason(TokenCompressionReason reason)
    {
        return reason switch
        {
            TokenCompressionReason.Configured => "达到配置的主动压缩条件",
            TokenCompressionReason.ContextLengthExceeded => "模型报告上下文长度超限，框架执行强制压缩后重试",
            TokenCompressionReason.ModelRequested => "模型调用 request_context_compaction 主动建立上下文检查点",
            _ => reason.ToString(),
        };
    }
}
