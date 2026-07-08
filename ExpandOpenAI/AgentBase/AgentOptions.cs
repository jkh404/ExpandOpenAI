using Microsoft.Extensions.AI;

namespace ExpandOpenAI.AgentBase;

public class AgentOptions
{
    /// <summary>
    /// 系统提示模板，支持占位符（例如：{{userName}}），在运行时会被模板引擎结合SystemPromptTemplateDic替换为实际值
    /// </summary>
    public string SystemPromptTemplate { get; set; } = string.Empty;


    public DynamicConcurrentDictionary? SystemPromptTemplateDic { get; set; }

    /// <summary>
    /// 消息在进入 AI 或进入历史记录前的处理器。
    /// </summary>
    public IAIMessageHandler? AIMessageHandler { get; set; }

    /// <summary>
    /// 是否对本轮准备发给 AI 的消息集合触发压缩。
    /// </summary>
    public Func<IReadOnlyList<ChatMessage>, bool>? ShouldCompressMessages { get; set; }

    /// <summary>
    /// 模型最大上下文长度（Token 数）。
    /// 供外部触发压缩策略或压缩器实现参考。
    /// </summary>
    public int? MaxContextLength { get; set; }

    /// <summary>
    /// IChatClient 调用失败时的最大重试次数。
    /// 上下文超限后的强制压缩重试不计入此次数。
    /// </summary>
    public int ChatClientRetryCount { get; set; }

    /// <summary>
    /// 当前 Agent 可提供给模型调用的工具集合。
    /// 自动工具调用要求工具本身可被本地执行，通常应传入 <see cref="AIFunction"/>。
    /// </summary>
    public IList<AITool> Tools { get; set; } = new List<AITool>();

    /// <summary>
    /// 工具调用模式。
    /// </summary>
    public ChatToolMode? ToolMode { get; set; }

    /// <summary>
    /// 是否允许模型在单次响应中请求多个工具调用。
    /// </summary>
    public bool? AllowMultipleToolCalls { get; set; }

    /// <summary>
    /// 工具执行审批委托。返回 <see langword="true"/> 时允许执行，返回 <see langword="false"/> 时拒绝执行。
    /// 默认拒绝所有工具执行。
    /// </summary>
    public Func<AITool, bool> ToolApprovalFunc { get; set; } = static _ => false;

    public IDictionary<string, object> RequestBody { get; set; } = new Dictionary<string, object>();




}

//public class McpHttpServerInfo
//{
//    public string Name { get; set; }
//    public string? ServerUrl { get; set; }
//    public IDictionary<string,object>? Header{ get; set; }


//}
//public class McpStdioServerInfo
//{
//    public string Name { get; set; }
//    public string? Command { get; set; }
//    public List<string> Args { get; set; }
//}
