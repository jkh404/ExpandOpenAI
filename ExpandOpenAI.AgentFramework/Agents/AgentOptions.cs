using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.AgentFramework;

/// <summary>
/// Agent 的稳定配置。创建 <see cref="AIAgent"/> 时会生成配置快照。
/// </summary>
public class AgentOptions
{
    public AgentOptions()
    {
    }

    /// <summary>
    /// 初始化配置副本。派生类型可在自己的复制构造函数中调用此构造函数。
    /// </summary>
    protected AgentOptions(AgentOptions other)
    {
        if (other is null)
        {
            throw new ArgumentNullException(nameof(other));
        }

        if (other.ToolApprovalAsync is null)
        {
            throw new ArgumentException("ToolApprovalAsync 不能为 null。", nameof(other));
        }

        SystemPromptTemplate = other.SystemPromptTemplate ?? string.Empty;
        SystemPromptTemplateValues = other.SystemPromptTemplateValues;
        MissingTemplateValueBehavior = other.MissingTemplateValueBehavior;
        MessageHandler = other.MessageHandler;
        TokenCompressor = other.TokenCompressor;
        ShouldCompressMessages = other.ShouldCompressMessages;
        SessionMemoryUnitFactory = other.SessionMemoryUnitFactory;
        GlobalMemoryUnit = other.GlobalMemoryUnit;
        EnableMemoryRecallTool = other.EnableMemoryRecallTool;
        MemoryRecallMaxResults = other.MemoryRecallMaxResults;
        DefaultChatOptions = other.DefaultChatOptions?.Clone();
        ToolApprovalAsync = other.ToolApprovalAsync;
        ContextLengthExceededDetector = other.ContextLengthExceededDetector;
    }

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
    public ITokenCompressor? TokenCompressor { get; init; } = new DefaultTokenCompressor();

    /// <summary>
    /// 覆盖压缩器自己的主动触发判断。上下文超限后的强制压缩不受此委托限制。
    /// </summary>
    public Func<IReadOnlyList<ChatMessage>, bool>? ShouldCompressMessages { get; init; }

    /// <summary>
    /// 为每个会话创建独立记忆体。默认使用 <see cref="InMemoryMemoryUnit"/>。
    /// </summary>
    public Func<IMemoryUnit> SessionMemoryUnitFactory { get; init; }
        = static () => new InMemoryMemoryUnit();

    /// <summary>
    /// 跨会话共享的全局记忆体。调用方应按租户、用户或 Agent 范围提供已经隔离的实例。
    /// </summary>
    public IMemoryUnit? GlobalMemoryUnit { get; init; }

    /// <summary>
    /// 是否向模型提供只读的内置 <c>recall_memory</c> 工具。
    /// </summary>
    public bool EnableMemoryRecallTool { get; init; } = true;

    /// <summary>
    /// 内置记忆召回工具每次最多返回的结果数。
    /// </summary>
    public int MemoryRecallMaxResults { get; init; } = 5;

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

    /// <summary>
    /// 创建当前配置的稳定副本。派生类型应重写此方法以保留自己的扩展配置。
    /// </summary>
    public virtual AgentOptions Clone()
    {
        return new AgentOptions(this);
    }

    protected internal virtual ChatOptions? CreateChatOptions(ChatOptions? runOptions)
    {
        return (runOptions ?? DefaultChatOptions)?.Clone();
    }

    protected internal virtual void Validate()
    {
        if (SessionMemoryUnitFactory is null)
        {
            throw new InvalidOperationException("SessionMemoryUnitFactory 不能为 null。");
        }

        if (MemoryRecallMaxResults <= 0)
        {
            throw new InvalidOperationException("MemoryRecallMaxResults 必须大于 0。");
        }
    }
}
