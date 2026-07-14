# ExpandOpenAI

`ExpandOpenAI` 是一个面向 OpenAI Compatible 接口的轻量级 `IChatClient`、`IEmbeddingGenerator<string, Embedding<float>>` 与 reranking 实现，基于 `Microsoft.Extensions.AI` 构建，适合接入 OpenAI、阿里云 DashScope 兼容模式，以及其他遵循 `/chat/completions`、`/responses`、`/embeddings`、`/reranks` 协议的模型服务。

它的目标不是重新发明一套 SDK，而是把“兼容 OpenAI 的 HTTP 接口”包装成标准的 `IChatClient`、`IEmbeddingGenerator` 和轻量 reranker，方便你继续使用 `ChatMessage`、`ChatOptions`、流式输出、工具调用、多模态内容、向量生成和重排序。

## 特性

- 实现 `Microsoft.Extensions.AI.IChatClient`
- 同时提供 Chat Completions 和 Responses API 两种 `IChatClient` 实现
- 实现 `Microsoft.Extensions.AI.IEmbeddingGenerator<string, Embedding<float>>`
- 提供 OpenAI Compatible `/reranks` 重排序客户端
- 支持普通响应和流式响应
- 支持 OpenAI Compatible embeddings 请求
- 支持 `ChatOptions` 常见参数映射
- 支持工具声明、工具调用和工具结果消息
- 支持 `reasoning` / `reasoning_content` 解析为 `TextReasoningContent`
- 支持文本、图片、音频内容的 OpenAI Compatible 序列化
- Responses API 支持 `input_text`、`input_image`、`input_file`、函数调用 Item 和 `text.format`
- Responses API 未识别的 output Item 会保留为 `OpenAIResponsesRawContent`
- 支持通过 `OpenAIRequestContent` 扩展自定义内容片段
- 支持环境变量初始化和代码配置初始化
- 支持自定义请求头、认证头、请求体扩展字段和请求钩子
- 提供带独立会话历史、上下文压缩和工具审批的轻量 Agent

## 项目结构

- `ExpandOpenAI/`：核心类库
- `ExpandOpenAI.TestConsole/`：控制台示例项目
- `ExpandOpenAI.Tests/`：自动化测试项目

## 运行要求

- .NET 10
- NuGet 依赖：
  - `Microsoft.Extensions.AI`

## 快速开始

先克隆仓库并构建：

```powershell
dotnet build
```

如果你要在自己的项目中直接引用源码项目：

```powershell
dotnet add <YourProject>.csproj reference .\ExpandOpenAI\ExpandOpenAI.csproj
```

## NuGet 打包

仓库内提供了打包脚本。Windows 下推荐直接用 `cmd` 包装器：

```powershell
.\scripts\pack-nuget.cmd -Version 1.0.0
```

如果你希望直接执行 PowerShell 脚本：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\pack-nuget.ps1 -Version 1.0.0
```

默认输出目录为 `.\artifacts\nuget`，同时会生成：

- `.nupkg`
- `.snupkg`

常用参数示例：

```powershell
.\scripts\pack-nuget.cmd -Version 1.0.0-preview.1
.\scripts\pack-nuget.cmd -Version 1.0.0 -OutputDir .\artifacts\release
.\scripts\pack-nuget.cmd -Version 1.0.0 -NoSymbols
```

### 基础调用

```csharp
using ExpandOpenAI;
using Microsoft.Extensions.AI;

var client = new OpenAICompatibleChatClient(new OpenAICompatibleChatClientOptions
{
    Endpoint = new Uri("https://api.openai.com/v1"),
    ApiKey = "<your-api-key>",
    ModelId = "gpt-4o-mini",
});

var response = await client.GetResponseAsync(
[
    new ChatMessage(ChatRole.User, "用一句话介绍 ExpandOpenAI。")
]);

Console.WriteLine(response.Text);
```

### Responses API

需要调用 OpenAI Responses API 或兼容服务时，使用 `OpenAICompatibleResponsesClient`。它同样实现 `IChatClient`，因此调用方式与 Chat Completions 客户端一致：

```csharp
using ExpandOpenAI;
using Microsoft.Extensions.AI;

IChatClient client = new OpenAICompatibleResponsesClient(
    new OpenAICompatibleResponsesClientOptions
    {
        Endpoint = new Uri("https://api.openai.com/v1"),
        ApiKey = "<your-api-key>",
        ModelId = "<responses-model>",
        Instructions = "回答要简洁。",
        Store = true,
    });

var response = await client.GetResponseAsync("介绍一下 Responses API。");
Console.WriteLine(response.Text);
```

`ChatOptions` 会按 Responses 语义映射：`Instructions` 使用顶层 `instructions`，`MaxOutputTokens` 使用 `max_output_tokens`，`ResponseFormat` 使用 `text.format`，函数工具使用扁平定义，`AllowMultipleToolCalls` 使用 `parallel_tool_calls`。

Responses 专有字段由 `OpenAICompatibleResponsesClientOptions` 提供，包括 `Store`、`PreviousResponseId`、`Conversation`、`Include`、`Truncation`、`Metadata` 和 `MaxToolCalls`。使用上一轮 Response ID 继续对话：

```csharp
var next = await client.GetResponseAsync(
    "继续说明。",
    new OpenAICompatibleResponsesClientOptions
    {
        PreviousResponseId = response.ResponseId,
    });
```

标准 OpenAI Responses API 不允许同时设置 `PreviousResponseId` 和 `Conversation`，客户端会在构造请求时检查这一冲突。

响应中的 `message`、`reasoning`、`function_call` 和 `function_call_output` 会映射为对应的 `Microsoft.Extensions.AI` 内容类型。尚未内置映射的工具 Item 或第三方 Item 会保留为 `OpenAIResponsesRawContent`；该对象可随消息再次发送，以完整保留未知 JSON 字段。

### 流式输出

```csharp
using ExpandOpenAI;
using Microsoft.Extensions.AI;

var client = new OpenAICompatibleChatClient(new OpenAICompatibleChatClientOptions
{
    Endpoint = new Uri("https://api.openai.com/v1"),
    ApiKey = "<your-api-key>",
    ModelId = "gpt-4o-mini",
});

await foreach (var update in client.GetStreamingResponseAsync(
[
    new ChatMessage(ChatRole.User, "请流式输出一段简短说明。")
]))
{
    foreach (var content in update.Contents)
    {
        if (content is TextReasoningContent reasoning)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.Write(reasoning.Text);
            Console.ResetColor();
        }
        else if (content is TextContent text)
        {
            Console.Write(text.Text);
        }
    }
}
```

## 环境变量方式

`OpenAICompatibleChatClient` 支持直接从环境变量读取配置：

- `OPENAI_ENDPOINT`
- `OPENAI_MODEL`
- `OPENAI_API_KEY`
- `OPENAI_REQUEST_PATH`：可选，默认值为 `chat/completions`

示例：

```powershell
$env:OPENAI_ENDPOINT="https://api.openai.com/v1"
$env:OPENAI_MODEL="gpt-4o-mini"
$env:OPENAI_API_KEY="<your-api-key>"
```

```csharp
using ExpandOpenAI;

var client = new OpenAICompatibleChatClient();
```

`OpenAICompatibleResponsesClient` 使用以下环境变量：

- `OPENAI_ENDPOINT`
- `OPENAI_RESPONSES_MODEL`，未设置时回退到 `OPENAI_MODEL`
- `OPENAI_API_KEY`
- `OPENAI_RESPONSES_REQUEST_PATH`：可选，默认值为 `responses`

```csharp
var client = new OpenAICompatibleResponsesClient();
```

## 向量模型

`OpenAICompatibleEmbeddingGenerator` 实现了 `IEmbeddingGenerator<string, Embedding<float>>`，可以直接用于 `Microsoft.Extensions.VectorData`、Qdrant、Semantic Kernel Vector Store 等依赖 `Microsoft.Extensions.AI` embedding 抽象的场景。

```csharp
using ExpandOpenAI;
using Microsoft.Extensions.AI;

IEmbeddingGenerator<string, Embedding<float>> generator =
    new OpenAICompatibleEmbeddingGenerator(new OpenAICompatibleEmbeddingGeneratorOptions
    {
        Endpoint = new Uri("https://dashscope.aliyuncs.com/compatible-mode/v1"),
        ApiKey = "<your-api-key>",
        ModelId = "text-embedding-v4",
    });

var embedding = await generator.GenerateAsync("需要向量化的文本");
Console.WriteLine(embedding.Vector.Length);
```

批量生成：

```csharp
var embeddings = await generator.GenerateAsync(
[
    "第一段文本",
    "第二段文本",
]);

foreach (var item in embeddings)
{
    Console.WriteLine(item.Vector.Length);
}
```

如果你的向量模型支持自定义维度，可以通过 `EmbeddingGenerationOptions.Dimensions` 传入：

```csharp
var embedding = await generator.GenerateAsync(
    "需要向量化的文本",
    new EmbeddingGenerationOptions
    {
        Dimensions = 1024,
    });
```

也可以在生成器配置里设置默认维度；单次调用传入的 `EmbeddingGenerationOptions.Dimensions` 会覆盖默认值：

```csharp
var generator = new OpenAICompatibleEmbeddingGenerator(new OpenAICompatibleEmbeddingGeneratorOptions
{
    Endpoint = new Uri("https://dashscope.aliyuncs.com/compatible-mode/v1"),
    ApiKey = "<your-api-key>",
    ModelId = "text-embedding-v4",
    DefaultModelDimensions = 1024,
});
```

使用便捷构造函数时也可以传入默认维度：

```csharp
var generator = new OpenAICompatibleEmbeddingGenerator(
    "text-embedding-v4",
    "<your-api-key>",
    new Uri("https://dashscope.aliyuncs.com/compatible-mode/v1"),
    defaultModelDimensions: 1024);
```

向量模型也支持环境变量初始化：

- `OPENAI_ENDPOINT`
- `OPENAI_EMBEDDING_MODEL`
- `OPENAI_API_KEY`
- `OPENAI_EMBEDDING_REQUEST_PATH`：可选，默认值为 `embeddings`

```csharp
var generator = new OpenAICompatibleEmbeddingGenerator();
```

## 重排序模型

`OpenAICompatibleReranker` 面向 OpenAI Compatible `/reranks` 接口，默认请求体为 `model`、`query`、`documents`，可选 `top_n` 和 `instruct`。返回结果会解析 `results[].index`、`results[].relevance_score`、可选 `results[].document.text` 和 `usage.total_tokens`。如果服务未返回 `results[].document`，库会按 `results[].index` 回填请求中的原始 document 文本。

```csharp
using ExpandOpenAI;

var reranker = new OpenAICompatibleReranker(new OpenAICompatibleRerankerOptions
{
    Endpoint = new Uri("https://dashscope.aliyuncs.com/compatible-api/v1"),
    ApiKey = "<your-api-key>",
    ModelId = "qwen3-rerank",
});

var response = await reranker.RerankAsync(
    "什么是重排序模型",
    [
        "重排序模型广泛应用于搜索引擎和推荐系统，用于按相关性对候选文本排序",
        "量子计算是计算科学的前沿领域",
        "预训练语言模型的发展为重排序模型带来了新的突破",
    ],
    new RerankingOptions
    {
        TopN = 2,
    });

foreach (var result in response.Results)
{
    Console.WriteLine($"{result.Index}: {result.RelevanceScore}");
    Console.WriteLine(result.Document?.Text);
}
```

如果服务支持厂商扩展字段，可以通过全局 `RequestBody` 或单次请求的 `AdditionalProperties` 透传：

```csharp
var reranker = new OpenAICompatibleReranker(new OpenAICompatibleRerankerOptions
{
    Endpoint = new Uri("https://dashscope.aliyuncs.com/compatible-api/v1"),
    ApiKey = "<your-api-key>",
    ModelId = "qwen3-rerank",
    RequestBody = new Dictionary<string, object?>
    {
        ["return_documents"] = true,
    },
});

var response = await reranker.RerankAsync(
    "How to change my password?",
    [
        "Click Settings > Security > Change Password to update your credentials",
        "What if I forgot my password?",
        "Our platform supports two-factor authentication",
    ],
    new RerankingOptions
    {
        Instruct = "Retrieve semantically similar text.",
        AdditionalProperties = new()
        {
            ["custom_field"] = "custom value",
        },
    });
```

重排序模型也支持环境变量初始化：

- `OPENAI_ENDPOINT`
- `OPENAI_RERANKING_MODEL`
- `OPENAI_API_KEY`
- `OPENAI_RERANKING_REQUEST_PATH`：可选，默认值为 `reranks`

```csharp
var reranker = new OpenAICompatibleReranker();
```

## 配置项说明

`OpenAICompatibleChatClientOptions`、`OpenAICompatibleResponsesClientOptions`、`OpenAICompatibleEmbeddingGeneratorOptions` 和 `OpenAICompatibleRerankerOptions` 主要提供以下能力：

| 配置项 | 说明 |
| --- | --- |
| `Endpoint` | 服务根地址，例如 `https://api.openai.com/v1` |
| `RequestPath` | 请求路径，默认分别为 `chat/completions`、`responses`、`embeddings`、`reranks`，也可传绝对地址 |
| `ModelId` | 默认模型 ID |
| `ApiKey` | API Key |
| `ApiKeyHeaderName` | 认证头名称，默认 `Authorization` |
| `ApiKeyScheme` | 认证方案，默认 `Bearer`，可设为 `null` 或空字符串 |
| `DefaultModelDimensions` | 向量生成默认维度；单次调用的 `EmbeddingGenerationOptions.Dimensions` 优先 |
| `DefaultTopN` | 重排序默认返回条数；单次调用的 `RerankingOptions.TopN` 优先 |
| `DefaultInstruct` | 重排序默认任务指令；单次调用的 `RerankingOptions.Instruct` 优先 |
| `Headers` | 额外请求头 |
| `RequestBody` | 额外请求体字段 |
| `SerializerOptions` | 自定义 JSON 序列化配置 |
| `ConfigureRequest` | 请求发送前自定义 `HttpRequestMessage` |
| `ConfigureRequestBody` | 请求发送前自定义 JSON Body |
| `RetryOptions` | HTTP 瞬时故障重试配置，适用于 Chat Completions、Responses、embeddings 和 reranking 客户端 |

例如某些兼容服务要求使用自定义认证头：

```csharp
var client = new OpenAICompatibleChatClient(new OpenAICompatibleChatClientOptions
{
    Endpoint = new Uri("https://example.com/v1"),
    ModelId = "my-model",
    ApiKey = "<your-api-key>",
    ApiKeyHeaderName = "api-key",
    ApiKeyScheme = null,
});
```

## HTTP 重试

`OpenAICompatibleChatClient`、`OpenAICompatibleResponsesClient`、`OpenAICompatibleEmbeddingGenerator` 和 `OpenAICompatibleReranker` 默认启用相同的瞬时故障重试策略：

- 首次请求失败后最多重试 2 次，总计最多发送 3 次。
- 第一次等待 200 毫秒，后续使用指数退避，单次最多等待 5 秒。
- 重试 `HttpRequestException`、非调用方取消导致的超时，以及 HTTP 408、429 和 5xx。
- 支持服务端 `Retry-After`，等待时间受 `MaxDelay` 限制。
- 调用方主动取消时立即停止，不进行重试。

可以在任意客户端 Options 中调整：

```csharp
var retryOptions = new OpenAICompatibleHttpRetryOptions
{
    MaxRetryAttempts = 3,
    InitialDelay = TimeSpan.FromMilliseconds(300),
    MaxDelay = TimeSpan.FromSeconds(10),
};

var client = new OpenAICompatibleChatClient(new OpenAICompatibleChatClientOptions
{
    Endpoint = new Uri("https://api.openai.com/v1"),
    ApiKey = "<your-api-key>",
    ModelId = "gpt-4o-mini",
    RetryOptions = retryOptions,
});
```

如果传入的 `HttpClient` 或 `HttpMessageHandler` 已经配置外部 resilience 管线，应将 `MaxRetryAttempts` 设为 `0`，避免两层重试叠加。

流式 Chat/Responses 只会在尚未取得成功响应头时重试；一旦开始读取或输出流内容，后续网络中断会直接向调用方抛出，不会重新发送整段请求。由于网络异常可能发生在服务端已收到请求之后，重试仍可能产生重复模型计算或计费。

## `response_format` 支持

`ExpandOpenAI` 会把 `Microsoft.Extensions.AI.ChatResponseFormat` 映射为 OpenAI Compatible `chat/completions` 所需的 `response_format`：

- `ChatResponseFormat.Text` -> `{ "type": "text" }`
- `ChatResponseFormat.Json` -> `{ "type": "json_object" }`
- `ChatResponseFormat.ForJsonSchema(...)` -> `{ "type": "json_schema", "json_schema": { ... } }`

使用 `OpenAICompatibleResponsesClient` 时，同一配置会改为 Responses API 所需的 `text.format`，JSON Schema 的 `name` 和 `schema` 直接位于 format 对象中。

例如启用 JSON mode：

```csharp
using ExpandOpenAI;
using Microsoft.Extensions.AI;

var client = new OpenAICompatibleChatClient(new OpenAICompatibleChatClientOptions
{
    Endpoint = new Uri("https://api.openai.com/v1"),
    ApiKey = "<your-api-key>",
    ModelId = "gpt-4o-mini",
    ResponseFormat = ChatResponseFormat.Json,
});
```

如果你希望模型按 JSON Schema 输出：

```csharp
using ExpandOpenAI;
using Microsoft.Extensions.AI;

var client = new OpenAICompatibleChatClient(new OpenAICompatibleChatClientOptions
{
    Endpoint = new Uri("https://api.openai.com/v1"),
    ApiKey = "<your-api-key>",
    ModelId = "gpt-4o-mini",
    ResponseFormat = ChatResponseFormat.ForJsonSchema<MyResponse>(),
});
```

## 多模态支持

默认请求构造器支持以下内容类型：

- `TextContent`
- `DataContent` 图片输入
- `UriContent` 图片输入
- `DataContent` 音频输入
- `UriContent` 音频输入
- 继承自 `OpenAIRequestContent` 的自定义内容

Responses 客户端还支持 `HostedFileContent`、普通文件 `DataContent` / `UriContent`，并分别映射为 `input_file.file_id`、`input_file.file_data` 或 `input_file.file_url`。

其中：

- 图片会被序列化为 `image_url`
- 音频会被序列化为 `input_audio`
- 不支持的内容类型会抛出 `NotSupportedException`

## DashScope 音频示例

仓库中已经提供了 `DashScopeAudioContent`，用于构造 DashScope 兼容接口所需的音频输入片段。

```csharp
using ExpandOpenAI;
using ExpandOpenAI.Providers.DashScope;
using Microsoft.Extensions.AI;

var client = new OpenAICompatibleChatClient(new OpenAICompatibleChatClientOptions
{
    Endpoint = new Uri("https://dashscope.aliyuncs.com/compatible-mode/v1"),
    ApiKey = "<your-api-key>",
    ModelId = "qwen3-asr-flash",
});

var message = new ChatMessage(ChatRole.User)
{
    Contents =
    [
        new DashScopeAudioContent(
            new DataContent(File.ReadAllBytes("sample.mp3"), "audio/mpeg"))
    ]
};

await foreach (var update in client.GetStreamingResponseAsync([message]))
{
    foreach (var content in update.Contents.OfType<TextContent>())
    {
        Console.Write(content.Text);
    }
}
```

## 工具调用支持

请求构造器会自动处理：

- `ChatOptions.Tools`
- `ChatOptions.ToolMode`
- `ChatOptions.AllowMultipleToolCalls`
- `FunctionCallContent`
- `FunctionResultContent`

响应解析器会把兼容接口返回的 `tool_calls` 解析回 `FunctionCallContent`，包括流式场景下分段返回的参数拼接。

Responses 客户端使用扁平的 function tool 结构，并把 `function_call` / `function_call_output` 作为顶层 Item 处理；流式函数参数会在 `response.function_call_arguments.*` 事件中累计，完成后只输出一次 `FunctionCallContent`。

## Agent 会话

`AIAgent` 保存可复用的客户端和稳定配置，`AgentSession` 保存一段独立对话的历史。一个 Agent 可以创建多个互不影响的会话：

```csharp
using System.Text.Json.Nodes;
using ExpandOpenAI.AgentBase;
using Microsoft.Extensions.AI;

var promptValues = new DynamicConcurrentDictionary
{
    ["assistantName"] = JsonValue.Create("Expand Assistant"),
};

var agent = new AIAgent(client, new AgentOptions
{
    SystemPromptTemplate = "你的名字是 {{assistantName}}。",
    SystemPromptTemplateValues = promptValues,
    MissingTemplateValueBehavior = MissingTemplateValueBehavior.Throw,
    DefaultChatOptions = new ChatOptions
    {
        Temperature = 0.2f,
    },
});

var session = agent.CreateSession();
var response = await session.RunAsync(
    "介绍一下你自己。",
    cancellationToken: cancellationToken);

Console.WriteLine(response.Text);
```

流式调用只有在完整枚举结束后才提交历史；调用方提前停止枚举时，不会把部分响应写入会话：

```csharp
await foreach (var update in session.RunStreamAsync(
    "流式回答。",
    cancellationToken: cancellationToken))
{
    Console.Write(update.Text);
}
```

需要并行处理多段对话时，应创建多个 `AgentSession`。同一个会话不允许并发运行，避免历史消息交叉污染。

单次 `RunAsync` 或 `RunStreamAsync` 传入的 `ChatOptions` 会替换 `DefaultChatOptions`；无论使用哪一种，发送前都会创建独立副本，避免底层客户端修改共享配置。

Agent 层只处理“上下文超限后压缩一次再重试”这种语义恢复。HTTP 超时、429 和 5xx 等传输重试应配置在底层 `IChatClient` 或 HTTP resilience 管线中，避免包含工具副作用的整轮对话被重复执行。

工具执行默认拒绝。启用本地工具时，应使用异步审批委托检查函数名和实际参数：

```csharp
var agent = new AIAgent(client, new AgentOptions
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

历史压缩器实现 `ITokenCompressor`。压缩输入不包含系统提示，返回结果同样不能包含 `System` 消息；压缩结果只会在本轮模型调用成功后提交到会话历史。

### JSON 修复

JSON 修复是独立功能，不参与 Agent 历史和工具流程：

```csharp
using ExpandOpenAI;

var repairer = new JsonRepairer(client);
var validJson = await repairer.RepairAsync(
    invalidJson,
    cancellationToken: cancellationToken);
```

`JsonRepairer` 会先尝试本地解析；只有本地解析失败时才调用模型，并对模型结果再次进行 JSON 验证。

## 扩展自定义内容

如果某个服务的内容结构不是标准的 OpenAI Compatible 格式，可以继承 `OpenAIRequestContent` 自己定义序列化逻辑：

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using ExpandOpenAI;

public sealed class CustomContent : OpenAIRequestContent
{
    public override JsonObject SerializeToOpenAIRequestContentPart(JsonSerializerOptions serializerOptions)
    {
        return new JsonObject
        {
            ["type"] = "custom_part",
            ["value"] = "hello"
        };
    }
}
```

## 示例项目

控制台示例位于 `ExpandOpenAI.TestConsole/Program.cs`，当前包含：

- DashScope 音频输入示例
- 图片理解示例
- 流式响应输出示例

你可以直接修改其中的 `Endpoint`、`ApiKey`、`ModelId` 和本地文件路径进行测试。

## License

本项目使用 [MIT License](./LICENSE.txt)。
