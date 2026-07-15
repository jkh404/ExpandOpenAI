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

1. 最近 1 轮尽量原样保留；单轮估算超限时改为摘要。
2. 再之前的 10 轮分别生成摘要并保留在活动上下文。
3. 更早的轮次分别生成摘要，移出活动上下文并写入会话层记忆。

`DefaultTokenCompressorOptions` 可以调整原样轮数、摘要轮数、Token 估算限制、摘要输出限制和 Token 估算器。内置估算器是跨模型的保守近似值；需要精确限制时，应注入与实际模型匹配的 `TokenEstimator`。

自定义历史压缩器实现 `ITokenCompressor`，返回 `TokenCompressionResult`：

- `Messages` 是压缩后继续留在活动上下文中的历史。
- `SessionMemoriesToStore` 写入当前会话记忆。
- `GlobalMemoriesToStore` 只在调用方明确配置 `GlobalMemoryUnit` 时写入。

所有 `System` 消息都会完全绕过压缩器：它们不会出现在压缩输入中，也不允许由压缩器返回；压缩完成后，框架会把全部原系统提示恢复到活动历史。配置模板生成的系统提示只会在历史中不存在时插入，避免多轮运行重复累积。待移出的记忆会先按 ID 幂等写入记忆体；模型调用成功后，压缩历史才会替换原会话历史。记忆写入失败或模型调用失败都不会裁掉原历史。

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
