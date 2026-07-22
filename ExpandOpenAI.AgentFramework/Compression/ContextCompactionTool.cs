using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.AgentFramework;

internal sealed class ContextCompactionTool
{
    public const string Name = "request_context_compaction";

    public ContextCompactionTool()
    {
        Function = AIFunctionFactory.Create(
            (Func<string, string?, ContextCompactionToolResponse>)CreatePlaceholderResponse,
            Name,
            "主动压缩当前对话上下文。仅在上下文冗长、工具调用过多或需要建立任务检查点时调用；"
            + "本工具必须独占一次工具调用。summary 必须提炼任务相关关键信息，包括目标、事实、决定、约束、"
            + "已完成工作、工具结果、未完成事项和下一步。System 与 User 消息不会被压缩或丢弃。");
    }

    public AIFunction Function { get; }

    private static ContextCompactionToolResponse CreatePlaceholderResponse(
        [Description("当前任务检查点摘要。提炼任务相关关键信息，不要复制整段对话。")]
        string summary,
        [Description("主动压缩的简短原因。")]
        string? reason = null)
    {
        return ContextCompactionToolResponse.Rejected(
            "该工具只能由 AgentSession 的运行循环执行。",
            summary,
            reason);
    }
}

internal sealed class ContextCompactionToolResponse
{
    private ContextCompactionToolResponse(
        bool accepted,
        bool applied,
        string notice,
        string? summary,
        string? reason)
    {
        Accepted = accepted;
        Applied = applied;
        Notice = notice;
        Summary = summary;
        Reason = reason;
    }

    public bool Accepted { get; }

    public bool Applied { get; }

    public string Notice { get; }

    public string? Summary { get; }

    public string? Reason { get; }

    public static ContextCompactionToolResponse AcceptedRequest(string summary, string? reason)
    {
        return new ContextCompactionToolResponse(
            accepted: true,
            applied: false,
            "上下文压缩请求已接受。",
            summary,
            reason);
    }

    public static ContextCompactionToolResponse AppliedRequest(string summary, string? reason)
    {
        return new ContextCompactionToolResponse(
            accepted: true,
            applied: true,
            "上下文已压缩。请根据保留的消息、压缩摘要和任务检查点继续当前任务。",
            summary,
            reason);
    }

    public static ContextCompactionToolResponse Rejected(
        string notice,
        string? summary = null,
        string? reason = null)
    {
        return new ContextCompactionToolResponse(
            accepted: false,
            applied: false,
            notice,
            summary,
            reason);
    }
}
