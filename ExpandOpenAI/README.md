# ExpandOpenAI

`ExpandOpenAI` 是一个面向 OpenAI Compatible 接口的轻量级 `IChatClient`、`IEmbeddingGenerator<string, Embedding<float>>`、Decisions AI、多模态 embedding 与 reranking 实现，基于 `Microsoft.Extensions.AI` 构建，适合接入 OpenAI、OpenRouter/TypeSafe、阿里云 DashScope 兼容模式，以及其他遵循 `/chat/completions`、`/responses`、`/embeddings`、`/reranks` 协议的模型服务。

它的目标不是重新发明一套 SDK，而是把“兼容 OpenAI 的 HTTP 接口”包装成标准的 `IChatClient`、`IEmbeddingGenerator` 和轻量 reranker，方便你继续使用 `ChatMessage`、`ChatOptions`、流式输出、工具调用、多模态内容、向量生成和重排序。

## 特性

- 实现 `Microsoft.Extensions.AI.IChatClient`
- 同时提供 Chat Completions 和 Responses API 两种 `IChatClient` 实现
- 实现 `Microsoft.Extensions.AI.IEmbeddingGenerator<string, Embedding<float>>`
- 多模态向量同时提供 `IMultimodalEmbeddingGenerator` 和 `IEmbeddingGenerator<AIContent, Embedding<float>>`
- 提供 OpenAI Compatible `/reranks` 重排序客户端
- 提供 OpenRouter Alpha.Decisions / TypeSafe System One 决策模型客户端
- 支持普通响应和流式响应
- 支持 OpenAI Compatible embeddings 请求
- 支持 DashScope 向量的 `output.embeddings` 响应；文本 `GenerateAsync` 与多模态 `GenerateMultimodalAsync` 均可解析
- 支持 `ChatOptions` 常见参数映射
- 支持工具声明、工具调用和工具结果消息
- 支持 `reasoning` / `reasoning_content` 解析为 `TextReasoningContent`
- 支持文本、图片、音频内容的 OpenAI Compatible 序列化
- Responses API 支持 `input_text`、`input_image`、`input_file`、函数调用 Item 和 `text.format`
- Responses API 未识别的 output Item 会保留为 `OpenAIResponsesRawContent`
- 支持通过 `OpenAIRequestContent` 扩展自定义内容片段
- 支持环境变量初始化和代码配置初始化
- 支持自定义请求头、认证头、请求体扩展字段和请求钩子

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

### 通过工厂选择协议

当调用方只持有统一的模型、密钥和 Endpoint 时，可以通过 `ChatClientFactory` 创建 `IChatClient`：

```csharp
IChatClient client = ChatClientFactory.Create(
    modelId: "gpt-4o-mini",
    apiKey: "<your-api-key>",
    endpoint: new Uri("https://api.openai.com/v1"),
    protocol: OpenAICompatibleChatProtocol.Responses);
```

选择 `Responses` 时工厂创建 `OpenAICompatibleResponsesClient`，选择 `ChatCompletions` 时创建 `OpenAICompatibleChatClient`。协议参数默认为 `ChatCompletions`；两个客户端分别使用 `responses` 和 `chat/completions` 作为默认请求路径。

当 `endpoint` 已经是完整请求地址时，可以选择 `Auto`：

```csharp
IChatClient client = ChatClientFactory.Create(
    modelId: "gpt-4o-mini",
    apiKey: "<your-api-key>",
    endpoint: new Uri("https://api.openai.com/v1/responses"),
    protocol: OpenAICompatibleChatProtocol.Auto);
```

`Auto` 根据 Endpoint 是否以 `/responses` 或 `/chat/completions` 结尾选择客户端，并直接请求该完整地址，不再追加默认路径。无法识别时会抛出 `ArgumentException`。需要其他供应商自定义路径时，请直接使用对应客户端的构造器或 Options。

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

Responses 客户端同样支持流式调用；`response.output_text.delta` 等服务端事件会被统一转换为 `ChatResponseUpdate`：

```csharp
await foreach (var update in client.GetStreamingResponseAsync("请流式介绍 Responses API。"))
{
    Console.Write(update.Text);
}
```

流式函数调用的参数会在服务端增量事件中累计，完成后只产生一个 `FunctionCallContent`，可以继续按 `IChatClient` 的常规工具调用流程处理。

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

`GenerateAsync` 默认发送 OpenAI Compatible `/embeddings` 风格请求，响应解析同时兼容 OpenAI 的 `data[].embedding` 和 DashScope 的 `output.embeddings[].embedding`。因此配置 DashScope 格式 embedding 模型时，纯文本向量仍可继续走 `IEmbeddingGenerator<string, Embedding<float>>`。

### DashScope 多模态向量

DashScope 的多模态向量端点不是 OpenAI Compatible `/embeddings` 协议。使用 `IMultimodalEmbeddingGenerator.GenerateMultimodalAsync` 或 `IEmbeddingGenerator<AIContent, Embedding<float>>.GenerateAsync` 传入 `Microsoft.Extensions.AI` 内容对象时，客户端会发送 `input.contents`，并解析返回的 `output.embeddings`。

```csharp
using ExpandOpenAI;
using Microsoft.Extensions.AI;

using var generator = new OpenAICompatibleEmbeddingGenerator(
    new OpenAICompatibleEmbeddingGeneratorOptions
    {
        Endpoint = new Uri(
            "https://<workspace>.cn-beijing.maas.aliyuncs.com/api/v1/services/embeddings/multimodal-embedding/multimodal-embedding"),
        RequestPath = string.Empty,
        ApiKey = "<your-api-key>",
        ModelId = "tongyi-embedding-vision-plus",
    });

IMultimodalEmbeddingGenerator multimodalGenerator = generator;
var embeddings = await multimodalGenerator.GenerateMultimodalAsync(
[
    new TextContent("一只在草地上奔跑的狗"),
    new UriContent("https://example.com/dog.png", "image/png"),
]);

foreach (var embedding in embeddings)
{
    Console.WriteLine(embedding.Vector.Length);
}
```

如果你的上层 adapter 想继续使用 `Microsoft.Extensions.AI` 的泛型接口，也可以这样调用：

```csharp
IEmbeddingGenerator<AIContent, Embedding<float>> multimodalGenerator = generator;
var embeddings = await multimodalGenerator.GenerateAsync(
[
    new TextContent("产品说明"),
    new UriContent("https://example.com/product.png", "image/png"),
]);
```

`TextContent` 会映射为 `{"text":"..."}`；图片 `UriContent` / `DataContent` 映射为 `{"image":"..."}`，视频 `UriContent` 映射为 `{"video":"..."}`。图片可以使用公开 URL 或 Data URI；视频必须使用公开 URL。每个内容对象对应 `contents` 中的一个元素，因此会返回独立向量。

`EmbeddingGenerationOptions.Dimensions`（或 `DefaultModelDimensions`）会映射为 DashScope 的 `parameters.dimension`。其他模型参数可在 `ConfigureMultimodalRequestBody` 中设置：

```csharp
var generator = new OpenAICompatibleEmbeddingGenerator(
    new OpenAICompatibleEmbeddingGeneratorOptions
    {
        // 省略通用配置
        ConfigureMultimodalRequestBody = (body, _, _) =>
        {
            body["parameters"]!["enable_fusion"] = true;
        },
    });
```

详情和模型支持范围请参考 [DashScope 多模态向量 API 文档](https://help.aliyun.com/zh/model-studio/multimodal-embedding-api-reference)。

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

多模态向量可以使用独立环境变量，避免和标准文本 `/embeddings` 配置混用：

- `OPENAI_MULTIMODAL_EMBEDDING_ENDPOINT`，未设置时回退到 `OPENAI_ENDPOINT`
- `OPENAI_MULTIMODAL_EMBEDDING_MODEL`，未设置时回退到 `OPENAI_EMBEDDING_MODEL` 或 `OPENAI_MODEL`
- `OPENAI_MULTIMODAL_EMBEDDING_API_KEY`，未设置时回退到 `OPENAI_API_KEY`
- `OPENAI_MULTIMODAL_EMBEDDING_REQUEST_PATH`：可选，默认值为空字符串，适合直接传完整 DashScope 多模态端点
- `OPENAI_MULTIMODAL_EMBEDDING_DIMENSIONS`：可选，映射为 DashScope `parameters.dimension`

```csharp
using var generator = new OpenAICompatibleEmbeddingGenerator(
    OpenAICompatibleEmbeddingGeneratorOptions.FromMultimodalEnvironment());
```

## Decisions AI / TypeSafe System One

`DecisionsAIClient` 提供独立的 `DecisionsRequest` / `DecisionsResponse` 契约，默认 POST 到 `https://openrouter.ai/api/alpha/decisions`。每次请求把一个 state 和多个独立问题一起发送，返回按问题 ID 索引的强类型答案。它不实现 `IChatClient`，不生成聊天文本，也不提供流式接口。

| 问题 / 答案类型 | criteria | 结果 |
| --- | --- | --- |
| `DecisionsNoulQuestion` / `DecisionsNoulAnswer` | 可省略；提供时用 `DecisionsNoulCriteria.True/False` 描述两个结果 | `Noul` 是为真的概率（0～1），阈值由业务代码决定 |
| `DecisionsChoiceQuestion` / `DecisionsChoiceAnswer` | 选项名到描述的字典，最多 255 个选项 | `Choice`、可选 `Confidence` 和 `Probabilities` |
| `DecisionsScoreQuestion` / `DecisionsScoreAnswer` | 2～10 个描述组成的有序数组，下标从 0 开始 | `Score` 可以是小数；另有可选 `Confidence`、`Probabilities`、`Legend` |

state 接受字符串、JSON 对象或数组。所有问题的 `Instructions`、Choice 的每个选项描述、Score 的每个等级描述、Noul 的 True/False 描述，均按 TypeSafe 的 EntryType 支持字符串、对象、数组或 null。可以直接传匿名对象、字典、数组、`JsonElement` 或 `JsonNode`，无需先转成 JSON 字符串。Score 的概率和 legend 保留协议中的字符串下标；legend 值为 `JsonElement`，可读取结构化描述。

```csharp
using ExpandOpenAI;

using var decisions = new DecisionsAIClient(new DecisionsAIClientOptions
{
    Endpoint = new Uri("https://openrouter.ai/api"),
    ApiKey = "<openrouter-api-key>",
    ModelId = "typesafe/jev-1.13",
});

var response = await decisions.GetResponseAsync(
    new DecisionsRequest
    {
        State = new
        {
            ticket = "The checkout page is blank after clicking Pay.",
            customer_tier = "enterprise",
        },
        Questions = new Dictionary<string, DecisionsQuestion>
        {
            ["is_bug"] = new DecisionsNoulQuestion
            {
                Instructions = "Is the customer reporting a software defect?",
                Criteria = new DecisionsNoulCriteria
                {
                    True = "Broken or unexpected product behavior",
                    False = "A question or feature request",
                },
            },
            ["team"] = new DecisionsChoiceQuestion
            {
                Instructions = new
                {
                    question = "Which team should own this ticket?",
                    focus = "The reported product behavior",
                },
                Criteria = new Dictionary<string, object?>
                {
                    ["frontend"] = new
                    {
                        description = "Rendering, layout, or browser compatibility issues",
                        examples = new[] { "blank page", "broken layout" },
                    },
                    ["payments"] = "Checkout, billing, or payment processing issues",
                    ["account"] = "Login, permissions, or profile issues",
                },
            },
            ["urgency"] = new DecisionsScoreQuestion
            {
                Instructions = "How urgent is this ticket?",
                Criteria = new object?[]
                {
                    "Can wait for the next release",
                    "Should be fixed this week",
                    "Blocking revenue right now",
                },
            },
        },
    });

var isBug = ((DecisionsNoulAnswer)response.Answers["is_bug"]).Noul;
var team = ((DecisionsChoiceAnswer)response.Answers["team"]).Choice;
var urgency = ((DecisionsScoreAnswer)response.Answers["urgency"]).Score;
Console.WriteLine($"bug={isBug}, team={team}, urgency={urgency}");
```

入口配置如下，模型 ID 原样发送：

| 服务 | Endpoint | RequestPath | 模型示例 |
| --- | --- | --- | --- |
| OpenRouter Alpha.Decisions（默认） | `https://openrouter.ai/api` | `alpha/decisions` | `typesafe/jev-1.13` 或 `~typesafe/jev-latest` |
| OpenRouter System One | `https://openrouter.ai/api` | `v1/systemone` | `jev-latest` |
| TypeSafe 直连 | `https://api.typesafe.ai` | `v1/systemone` | `jev-latest` |

当 Endpoint 已包含完整请求路径时，设置 `RequestPath = string.Empty`；RequestPath 也支持绝对 URL。使用 TypeSafe 直连需要显式设置对应的 ModelId 和 API key。

`DecisionsRequest.ModelId` 可覆盖单次请求的模型；省略时使用客户端默认值，客户端不会改写传入的请求对象。也支持 `GetResponseAsync(state, questions, cancellationToken)` 简写。OpenRouter 的 `Provider`、`User`、`SessionId`、`Trace` 可在请求中设置，Provider/Trace 使用对象或字典，字段名遵循 OpenRouter 协议。

通过 `new DecisionsAIClient()` 或 `DecisionsAIClientOptions.FromEnvironment()` 读取环境变量：

- `OPENROUTER_API_KEY`：OpenRouter API key；代码配置也可使用自定义认证头
- `OPENROUTER_DECISIONS_MODEL`：其次回退到 `OPENROUTER_MODEL`，默认 `~typesafe/jev-latest`
- `OPENROUTER_ENDPOINT`：默认 `https://openrouter.ai/api`
- `OPENROUTER_DECISIONS_REQUEST_PATH`：默认 `alpha/decisions`，TypeSafe 入口使用 `v1/systemone`

未设置 OPENROUTER_ENDPOINT 且设置了 `TYPESAFE_BASE_URL` 时，使用该基地址、`TYPESAFE_API_KEY`、默认路径 `v1/systemone` 和默认模型 `jev-latest`；显式的模型和路径环境变量仍可覆盖默认值。若同时配置两套环境变量，OPENROUTER_ENDPOINT 优先并使用 OPENROUTER_API_KEY。

客户端复用现有 `OpenAICompatibleHttpRetryOptions`，支持 CancellationToken、注入 HttpClient/HttpMessageHandler、自定义认证头、Headers 和请求钩子。注入 HttpClient 时默认由调用方管理生命周期。扩展字段按全局 `RequestBody`、单次 `AdditionalProperties`、强类型字段的顺序合并；model/state/questions 始终取本次请求，随后 `ConfigureRequestBody` 可对最终 JSON 做高级修改。

响应包含实际 `ModelId`、可选 Id/Provider，以及输入/输出 token 数和可选 Cost。响应、答案和 usage 的未知字段保留在 AdditionalProperties；新出现的答案类型返回 `DecisionsUnknownAnswer.Raw`。已知答案缺少必需值、数值类型错误或概率越界时抛出 JsonException；HTTP 失败则抛出包含状态码及响应正文的 HttpRequestException。

这些接口仍会随供应商演进。TypeSafe 文档允许 null EntryType；OpenRouter Alpha.Decisions 当前的 SDK schema 对 instructions、Score 等级和 Noul criteria 描述更严格，使用该入口时应提供非 null 描述。Choice 的选项描述可为 null。客户端保留 TypeSafe 的完整输入形状，由所选服务校验其具体限制。

参考：[OpenRouter TypeSafe 接入](https://openrouter.ai/docs/guides/community/typesafe-sdk)、[Alpha.Decisions](https://openrouter.ai/docs/client-sdks/go/sdks/decisions/README#alpha-decisions)、[System One](https://docs.typesafe.ai/concepts/system-one)、[Choice](https://docs.typesafe.ai/primitives/choice)、[Score](https://docs.typesafe.ai/primitives/score)、[Noul](https://docs.typesafe.ai/primitives/noul)、[Advanced structure](https://docs.typesafe.ai/primitives/advanced)。

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

`OpenAICompatibleChatClientOptions` 和 `OpenAICompatibleResponsesClientOptions` 的公共配置定义在 `OpenAICompatibleChatOptions`；它们与 `OpenAICompatibleEmbeddingGeneratorOptions`、`OpenAICompatibleRerankerOptions` 主要提供以下能力：

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

### 图片

`DataContent` 图片会被编码为 data URI 写入 `image_url.url`，`UriContent` 图片直接使用原始 URL。

`AIContent.AdditionalProperties` 中的 `detail` 会按 OpenAI 规范写入 `image_url` 内部：

```csharp
var image = new UriContent("https://example.test/cat.png", "image/png")
{
    AdditionalProperties = new AdditionalPropertiesDictionary { ["detail"] = "high" },
};
```

生成结果：

```json
{"type":"image_url","image_url":{"url":"https://example.test/cat.png","detail":"high"}}
```

### 音频

音频片段遵循 OpenAI 规范：`input_audio.data` 为**纯 base64 数据**，音频格式由 `input_audio.format` 表达。

- `DataContent` 音频：`data` 取字节的 base64，`format` 由媒体类型推导
- `UriContent` 音频且 URI 为 `data:` 形式：自动剥离 data URI 前缀后写入 `data`
- `UriContent` 音频且 URI 为 `http(s)`：OpenAI 未定义远程音频输入，此处保留 `input_audio.url` 形状并附带 `format`，供支持该扩展的兼容服务使用

`format` 的映射规则：`audio/mpeg`、`audio/mpga` → `mp3`，`audio/wav`、`audio/x-wav` → `wav`，`audio/x-m4a` → `m4a`，其余取媒体类型子类型（忽略 `;` 之后的参数）。

```csharp
var message = new ChatMessage(ChatRole.User)
{
    Contents = [new DataContent(File.ReadAllBytes("sample.mp3"), "audio/mpeg")],
};
```

生成结果：

```json
{"type":"input_audio","input_audio":{"data":"SUQzBAAAAAAAI1RTU0UAAAAP...","format":"mp3"}}
```

### 额外属性

`AIContent.AdditionalProperties` 会合并到对应的内容片段：

- 默认写入片段顶层，例如 `cache_control`、`asr_options` 等厂商扩展字段
- 若片段中同名键本身是 JSON 对象且新值也是 JSON 对象，则合并进该嵌套对象（例如 `image_url`），不会覆盖 `url`
- `detail` 是特例，图片场景下写入 `image_url` 内部
- `openai_payload` 用于**整体替换**片段结构，与工具序列化保持一致，适合结构差异较大的服务

`openai_payload` 示例（DashScope 兼容模式要求 `input_audio.data` 为 data URI）：

```csharp
var audio = new DataContent(File.ReadAllBytes("sample.mp3"), "audio/mpeg")
{
    AdditionalProperties = new AdditionalPropertiesDictionary
    {
        ["openai_payload"] = new JsonObject
        {
            ["type"] = "input_audio",
            ["input_audio"] = new JsonObject
            {
                ["data"] = $"data:audio/mpeg;base64,{Convert.ToBase64String(File.ReadAllBytes("sample.mp3"))}",
            },
        },
    },
};
```

只有消息内容全部为纯文本且未携带额外属性时，`content` 才会折叠为字符串；否则统一输出内容片段数组，避免丢失额外属性。

## DashScope 音频示例

仓库中已经提供了 `DashScopeAudioContent`，用于构造 DashScope 兼容接口所需的音频输入片段。

DashScope 兼容模式的 `input_audio` 只使用 `data`（data URI 或公网 URL），不识别 `format` 字段，因此 `DashScopeAudioContent` 不会输出 `format`；如需在默认构造器上直接发送该形状，可参考上一节的 `openai_payload` 用法。

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

## JSON 修复

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
