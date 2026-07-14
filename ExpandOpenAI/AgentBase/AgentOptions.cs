using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.AgentBase;

/// <summary>
/// Agent 的稳定配置。创建 <see cref="AIAgent"/> 时会生成配置快照。
/// </summary>
public sealed class AgentOptions
{
    /// <summary>
    /// 系统提示模板，支持 <c>{{name}}</c> 和 <c>{{user.name}}</c> 占位符。
    /// </summary>
    public string SystemPromptTemplate { get; init; } = string.Empty;

    /// <summary>
    /// 系统提示模板变量。可使用 <see cref="DynamicConcurrentDictionary"/> 提供运行时动态值。
    /// </summary>
    public IReadOnlyDictionary<string, JsonNode?>? SystemPromptTemplateValues { get; init; }

    /// <summary>
    /// 模板变量缺失时的处理方式。
    /// </summary>
    public MissingTemplateValueBehavior MissingTemplateValueBehavior { get; init; }

    /// <summary>
    /// 用户消息和助手消息进入模型或历史前的处理器。
    /// </summary>
    public IAgentMessageHandler? MessageHandler { get; init; }

    /// <summary>
    /// 历史消息压缩器。
    /// </summary>
    public ITokenCompressor? TokenCompressor { get; init; }

    /// <summary>
    /// 判断本轮发送前是否应压缩历史。上下文超限后的强制压缩不受此委托限制。
    /// </summary>
    public Func<IReadOnlyList<ChatMessage>, bool>? ShouldCompressMessages { get; init; }

    /// <summary>
    /// 默认模型调用选项。每次运行都会克隆该实例。
    /// </summary>
    public ChatOptions? DefaultChatOptions { get; init; }

    /// <summary>
    /// 工具执行审批。默认拒绝本地工具执行。
    /// </summary>
    public Func<FunctionInvocationContext, CancellationToken, ValueTask<bool>> ToolApprovalAsync { get; init; }
        = static (_, _) => new ValueTask<bool>(false);

    /// <summary>
    /// 自定义上下文超限异常识别器。未配置时使用内置的跨提供商文本识别。
    /// </summary>
    public Func<Exception, bool>? ContextLengthExceededDetector { get; init; }

    internal AgentOptions CreateSnapshot()
    {
        if (ToolApprovalAsync is null)
        {
            throw new ArgumentException("ToolApprovalAsync 不能为 null。", nameof(ToolApprovalAsync));
        }

        return new AgentOptions
        {
            SystemPromptTemplate = SystemPromptTemplate ?? string.Empty,
            SystemPromptTemplateValues = SystemPromptTemplateValues,
            MissingTemplateValueBehavior = MissingTemplateValueBehavior,
            MessageHandler = MessageHandler,
            TokenCompressor = TokenCompressor,
            ShouldCompressMessages = ShouldCompressMessages,
            DefaultChatOptions = DefaultChatOptions?.Clone(),
            ToolApprovalAsync = ToolApprovalAsync,
            ContextLengthExceededDetector = ContextLengthExceededDetector,
        };
    }

    internal ChatOptions? CreateChatOptions(ChatOptions? runOptions)
    {
        return (runOptions ?? DefaultChatOptions)?.Clone();
    }
}
