# ExpandOpenAI.AgentFramework

`ExpandOpenAI.AgentFramework` 提供建立在 `ExpandOpenAI` 和 `Microsoft.Extensions.AI` 之上的轻量 Agent 抽象与默认实现。

外部代码优先依赖 `AIAgent` 抽象基类和 `IAgentSession` 接口；`DefaultAIAgent` 与 `AgentSession` 是可直接使用的默认实现。

该项目是基础设施类库，只负责会话、压缩、记忆存取和工具调用等通用机制，不判断哪些领域信息具有业务价值，也不会自动把会话记忆提升为全局记忆。

## 特性

- `AIAgent` 抽象基类和 `IAgentSession` 会话接口
- 每个会话独立维护对话历史
- 系统提示模板和动态模板变量
- 按用户轮次工作的默认历史压缩器
- 独立的会话层记忆和可共享的全局层记忆
- 内置只读 `recall_memory` 召回工具
- 默认启用、可关闭的 `request_context_compaction` 模型主动压缩工具
- 可替换的消息处理器、压缩器和记忆体 Adapter
- 上下文超限后的单次压缩恢复
- 工具调用审批
- 流式响应完整结束后再提交历史
- 同一会话的并发运行保护

## 快速开始

```csharp
using System.Text.Json.Nodes;
using ExpandOpenAI.AgentFramework;
using Microsoft.Extensions.AI;

var promptValues = new DynamicConcurrentDictionary
{
    ["assistantName"] = JsonValue.Create("Expand Assistant"),
};

AIAgent agent = new DefaultAIAgent(client, new AgentOptions
{
    SystemPromptTemplate = "你的名字是 {{assistantName}}。",
    SystemPromptTemplateValues = promptValues,
    MissingTemplateValueBehavior = MissingTemplateValueBehavior.Throw,
    DefaultChatOptions = new ChatOptions
    {
        Temperature = 0.2f,
    },
});

IAgentSession session = agent.CreateSession();
var response = await session.RunAsync("介绍一下你自己。");

Console.WriteLine(response.Text);
```

一个 Agent 可以创建多个互不影响的会话。默认 `AgentSession` 不允许同一个会话并发运行。

`ClearHistory()` 只清空活动上下文，不清空会话记忆；`ClearMemoryAsync()` 只清空会话层记忆；`DestroyAsync()` 同时清空历史和会话层记忆，并使会话不可再次运行。以上操作都不会清除全局记忆。

## 流式调用

只有完整枚举结束后才会提交历史；调用方提前停止枚举时，不会保存部分响应。

```csharp
await foreach (var update in session.RunStreamAsync("流式回答。"))
{
    Console.Write(update.Text);
}
```

## 工具调用

工具执行默认拒绝。启用本地工具时，应根据函数及实际参数进行审批：

```csharp
AIAgent agent = new DefaultAIAgent(client, new AgentOptions
{
    DefaultChatOptions = new ChatOptions
    {
        Tools = [myFunction],
    },
    ToolApprovalAsync = (context, cancellationToken) =>
    {
        var approved = context.Function.Name == "safe_function";
        return new ValueTask<bool>(approved);
    },
});
```

## 扩展 Agent

自定义 Agent 可以继承 `AIAgent`，并返回自己的 `IAgentSession` Adapter：

```csharp
public sealed class CustomAgent : AIAgent
{
    public CustomAgent(IChatClient chatClient, AgentOptions? options = null)
        : base(chatClient, options)
    {
    }

    public override IAgentSession CreateSession(
        IEnumerable<ChatMessage>? initialHistory = null)
    {
        return new CustomAgentSession(initialHistory);
    }
}
```

派生 `AgentOptions` 时应重写 `Clone()`，确保创建稳定配置快照时保留扩展字段。

## 历史压缩

一轮从一条 `User` 消息开始，到下一条 `User` 消息之前结束。Assistant 消息、工具调用和工具结果都会留在发起它们的同一轮中。

`AgentOptions` 默认使用 `DefaultTokenCompressor`。压缩器主动触发或模型返回上下文超限后，默认执行：

1. 如果 `MaximumMessageTokenEstimate` 大于 0，先压缩超过该阈值的单条 Assistant 文本或 Tool Result；设置为 0 时关闭消息级压缩。User、System 和 FunctionCall 不参与消息级压缩，Tool Result 压缩后保留原 CallId。
2. 消息级压缩完成后重新估算历史，再执行轮次级压缩。
3. 最近 1 轮整轮原样保留；只有该轮 Token 估算超过活动历史总阈值的三分之二时，才压缩其中的 Assistant 和 Tool 内容。
4. 再之前的 10 轮分别保留 User 消息原文，并将 Assistant 和 Tool 内容压缩为逐轮摘要后继续放在活动上下文。
5. 更早的轮次移出活动上下文并写入会话层记忆；记忆中仍保留 User 消息原文，并附带 Assistant 和 Tool 内容摘要。

`DefaultTokenCompressorOptions` 可以调整消息级阈值、原样轮数、摘要轮数、活动历史 Token 阈值、摘要输出限制、两级摘要提示词和 Token 估算器。最近原样轮次的压缩界限固定为活动历史总阈值的三分之二。内置估算器是跨模型的保守近似值；需要精确限制时，应注入与实际模型匹配的 `TokenEstimator`。

消息级与轮次级摘要默认都会要求模型“提炼任务相关关键信息”。可以分别通过 `MessageSummaryPrompt` 和 `SummaryPrompt` 覆盖；两者共享 `SummaryMaxOutputTokens`，并固定使用 `Temperature = 0`。摘要使用 Agent 的同一个 `IChatClient` 及其默认模型。

如果一个轮次包含大量 Assistant、FunctionCall 和 Tool Result，导致轮次摘要输入本身超过总历史阈值的三分之二，压缩器会执行分层摘要：

1. FunctionCall 与具有对应 CallId 的 Tool Result 优先组成同一个有序交互块。
2. 按 Token 估算将交互块组织为多个输入片段；只有单个交互块自身仍然超限时，才退化为有序文本分片。
3. 每个片段分别生成局部摘要，再按原始顺序递归合并，最多归并 8 轮。
4. 中间摘要不会写入活动历史或 MemoryUnit；最终仍然只保留 User 原文和一条轮次摘要。

工具摘要输入会显式包含工具名称、CallId、参数、结果和异常。若局部摘要无法在配置阈值内继续收敛，压缩器会明确抛出异常，而不是静默截断信息。

### 模型主动压缩

配置了 `TokenCompressor` 时，框架默认向模型提供内置 `request_context_compaction(summary, reason?)` 工具。模型可以在上下文冗长、单轮工具调用过多或需要建立任务检查点时主动调用它；`summary` 应提炼当前目标、关键事实、决定、约束、已完成工作、工具结果、未完成事项和下一步。

该工具不会在普通业务工具内部重入 `AgentSession`。工具调用只记录压缩请求并结束当前内部工具循环，`AgentSession` 随后在安全检查点压缩本次运行中的完整上下文，再把压缩结果和原工具调用/结果形式的模型任务检查点放回上下文，最后让模型在同一轮继续推理。因此，压缩前已经产生的大量 Assistant、FunctionCall 和 Tool Result 也会进入压缩器，而不必等待下一条 User 消息。

主动压缩遵守以下约束：

- `System` 消息始终绕过压缩器并原样恢复。
- `User` 消息必须原样、按原顺序保留；模型主动压缩路径会校验自定义压缩器的结果。
- 主动压缩工具必须独占一次工具调用，不能与业务工具并行请求。
- 同一次 `RunAsync` 或 `RunStreamAsync` 最多成功执行一次主动压缩，之后不再向模型提供该工具。
- `summary` 最多 8000 个字符，`reason` 最多 1000 个字符。
- 流式调用会隐藏该内置控制工具自身的 FunctionCall 和 FunctionResult；如果已经向调用方输出 Assistant 正文，本轮后续主动压缩请求会被拒绝，避免重写已经公开的响应。
- 主动压缩产生的待写入记忆会在本轮最终成功后提交；本轮后续模型调用失败时，不提交压缩历史和这些新增记忆。

可以显式关闭该能力；自动阈值压缩和上下文超限恢复不受影响：

```csharp
var agent = new DefaultAIAgent(client, new AgentOptions
{
    EnableContextCompactionTool = false,
});
```

内置主动压缩工具属于框架控制工具，不经过 `ToolApprovalAsync`。模型是否主动调用仍由模型决定，现有自动压缩策略继续作为安全兜底。

自定义历史压缩器实现 `ITokenCompressor`，返回 `TokenCompressionResult`：

- `Messages` 是压缩后继续留在活动上下文中的历史。
- `SessionMemoriesToStore` 写入当前会话记忆。
- `GlobalMemoriesToStore` 只在调用方明确配置 `GlobalMemoryUnit` 时写入。

所有 `System` 消息都会完全绕过压缩器：它们不会出现在压缩输入中，也不允许由压缩器返回；压缩完成后，框架会把全部原系统提示恢复到活动历史。所有 `User` 消息在活动摘要或会话记忆中均保留原文，不交由摘要替代；默认摘要模型只负责压缩 Assistant 和 Tool 内容。配置模板生成的系统提示只会在历史中不存在时插入，避免多轮运行重复累积。待移出的记忆会先按 ID 幂等写入记忆体；模型调用成功后，压缩历史才会替换原会话历史。记忆写入失败或模型调用失败都不会裁掉原历史。

## 两层记忆

`IMemoryUnit` 定义幂等写入、召回和清空三个基础设施操作。默认的 `InMemoryMemoryUnit` 适合作为进程内会话记忆，也可以由数据库或向量存储 Adapter 替换。

```csharp
var globalMemory = new InMemoryMemoryUnit();

AIAgent agent = new DefaultAIAgent(client, new AgentOptions
{
    // 每次 CreateSession 都必须返回一个独立实例。
    SessionMemoryUnitFactory = static () => new InMemoryMemoryUnit(),

    // 该实例由所有会话共享。生产环境应由外部 Adapter 按租户、用户或 Agent 隔离。
    GlobalMemoryUnit = globalMemory,
});
```

内置 `recall_memory` 工具默认同时查询会话层和全局层，合并时会话层优先，并按记忆 ID 和内容去重。它是框架内部的只读工具，不经过 `ToolApprovalAsync`；其他业务工具仍然需要审批。

框架不会判断哪些信息值得进入全局记忆。业务层可以直接写入自己持有的 `GlobalMemoryUnit`，或者通过自定义 `ITokenCompressor` 明确返回 `GlobalMemoriesToStore`。

Agent 层只处理上下文超限后的语义恢复。HTTP 超时、429 和 5xx 等传输重试应配置在底层 `IChatClient`，避免包含工具副作用的整轮对话被重复执行。

## License

本项目使用 MIT License。
