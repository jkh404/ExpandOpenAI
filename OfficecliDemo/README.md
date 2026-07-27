# OfficecliDemo

基于 `ExpandOpenAI.AgentFramework` 和 officecli MCP 的招标书大纲修复、商务标/技术标提取控制台 Demo。项目包含两个彼此独立的智能体：`OutlineRepairAgent` 和 `TenderExtractionAgent`。

## 最简单的运行方式

直接运行，不需要预先设置环境变量，也不需要传参数：

```powershell
dotnet run --project .\OfficecliDemo\OfficecliDemo.csproj
```

缺少配置时，控制台会依次询问：

1. Word 招标书路径；
2. OpenAI 兼容接口地址；
3. 模型名称；
4. API Key（输入时只显示 `*`）；
5. 输出目录，直接回车则使用默认目录。

## 推荐：使用本地配置文件

将示例复制为本地配置：

```powershell
Copy-Item `
  .\OfficecliDemo\appsettings.Local.json.example `
  .\OfficecliDemo\appsettings.Local.json
```

然后编辑 [appsettings.Local.json.example](./appsettings.Local.json.example) 对应格式：

```json
{
  "OpenAI": {
    "Endpoint": "http://your-host/v1",
    "ApiKey": "your-api-key",
    "Model": "your-model",
    "EnableThinking": true
  },
  "Demo": {
    "DocumentPath": "E:\\path\\招标文件.docx",
    "OutputDirectory": "E:\\path\\提取结果",
    "Mode": "Combined"
  }
}
```

`appsettings.Local.json` 已被项目内的 `.gitignore` 忽略，不会误提交 API Key。公共默认值位于 [appsettings.json](./appsettings.json)。

压缩器和记忆参数也可以在本地配置中覆盖：

```json
{
  "Agent": {
    "MaximumHistoryTokenEstimate": 12000,
    "MaximumMessageTokenEstimate": 12000,
    "RecentSummaryTurnCount": 2,
    "SummaryMaxOutputTokens": 1000,
    "MemoryRecallMaxResults": 50,
    "BodyIndexScanBatchSize": 200,
    "EnableContextCompactionTool": true
  }
}
```

控制台默认按“智能体任务 → 底层模型请求 → 工具调用”三层打印处理链路。智能体通过 `RunStreamAsync` 真正流式运行：推理内容和普通内容直接使用 `Console.Write` 连续输出，模型每返回一个增量就立即 Flush，不再经过 ILogger 分块，因此正文中不会反复插入时间戳和日志 scope。思考/普通输出开始或切换时只打印简短 Console 分隔线。请求结束后打印 `[Model][TokenSpeed]`；Prompt、工具审批、压缩、长期记忆、历史清空和导出继续使用结构化日志。原有每 5 秒 `[Model][Streaming]` 心跳已移除，避免打断正文。工具结果正文默认关闭。两个智能体分别原子更新 `outline-repair-agent-context.json` 和 `tender-extraction-agent-context.json`，流式生成过程中也会持续更新，不会混写上下文。日志开关位于：

```json
{
  "Logging": {
    "MinimumLevel": "Information",
    "ShowPrompts": true,
    "ShowAiOutput": true,
    "ShowAiReasoning": true,
    "ShowToolArguments": true,
    "ShowToolResults": false,
    "MaximumTextLength": 12000
  }
}
```

`ShowToolResults=false` 时仍会记录工具名称、参数、审批和执行完成状态，但不打印工具返回正文。`MaximumTextLength` 是其他长文本日志的最大字符数；发生截断时日志会显示原始字符数。模型提供显式推理内容时，会分别打印 `[AI][思考输出]` 和 `[AI][普通输出]`。这要求 `OpenAI.EnableThinking=true`、`Logging.ShowAiReasoning=true`，并且接口实际返回 `reasoning_content`。

配置优先级为：

1. 命令行中的文档路径和输出目录；
2. 环境变量；
3. `appsettings.Local.json`；
4. `appsettings.json`；
5. 控制台现场询问。

## 可选命令行参数

只想临时更换文档时，可以传入参数：

```powershell
dotnet run --project .\OfficecliDemo\OfficecliDemo.csproj -- `
  "E:\path\招标文件.docx" `
  "E:\path\输出目录" `
  combined
```

第二、第三个参数可省略。第三个参数支持 `combined`、`outline`、`extraction`。

## 可选环境变量

环境变量不是必需的，仅适合部署或自动化。支持原有名称：

```powershell
$env:OPENAI_ENDPOINT = "http://your-host/v1"
$env:OPENAI_API_KEY = "your-api-key"
$env:OPENAI_MODEL = "your-model"
$env:OPENAI_REQUEST_PATH = "chat/completions"
$env:OPENAI_ENABLE_THINKING = "true"
$env:OPENAI_TIMEOUT_SECONDS = "300"
$env:OFFICECLI_COMMAND = "officecli"
$env:OFFICECLI_MAX_TOOL_CALLS = "300"
$env:DEMO_MODE = "Combined"
$env:AGENT_MAXIMUM_MESSAGE_TOKEN_ESTIMATE = "12000"
$env:AGENT_ENABLE_CONTEXT_COMPACTION_TOOL = "true"
$env:LOGGING_SHOW_PROMPTS = "true"
$env:LOGGING_SHOW_AI_OUTPUT = "true"
$env:LOGGING_SHOW_AI_REASONING = "true"
$env:LOGGING_SHOW_TOOL_ARGUMENTS = "true"
$env:LOGGING_SHOW_TOOL_RESULTS = "true"
$env:LOGGING_MAXIMUM_TEXT_LENGTH = "12000"
```

也支持带 `OFFICECLIDEMO_` 前缀的分层配置，例如 `OFFICECLIDEMO_OpenAI__Model`。

## 分析流程

两个智能体没有对象、会话、长期记忆或压缩历史依赖，可以分别运行：

- `Combined`：复制为 `-大纲待修复.docx` → 独立运行 `OutlineRepairAgent` → 释放其全部资源 → 原子发布 `-大纲修复后.docx` → 把该文件路径交给全新创建的 `TenderExtractionAgent`。两者只通过 Word 文件组合。
- `OutlineOnly`：只运行 `OutlineRepairAgent` 并输出大纲 JSON 和修复后文档，不创建提取智能体。
- `ExtractionOnly`：直接针对配置的源招标书运行 `TenderExtractionAgent`，不创建大纲修复智能体。现有 outline 只是可选线索；即使 outline 缺失或错误，也会依靠全文分页扫描完成提取。

修复失败或中途取消时不会提前生成新的正式修复文件。

本项目针对长招标书做了以下改进：

1. **连续 Body Index 原文扫描**：参考 `OutlineRepairerDemo2` 的分治思路，先通过 officecli 获取 stats/outline，再按零基 `Body.ChildElements Index` 从上到下扫描；每批 200 个元素，闭区间依次为 `0-199、200-399……`。宿主不预先筛选“疑似标题”，避免真实标题在进入 AI 前被漏掉。
2. **Agent 只用 officecli 读取文档**：Agent 只注册 officecli MCP 文档工具，不再注册自定义分页、段落窗口、全文搜索或 Word/PDF 索引工具。大纲扫描、分页读取、关键词搜索和段落复核全部通过 officecli 完成。
3. **单智能体连续大纲修复**：参考 `OutlineRepairerDemo2`，宿主只启动一次完整大纲修复任务。`OutlineRepairAgent` 在同一会话中依次调用 officecli 扫描 `0-199、200-399……`，扫描过程中不清空历史、不把每批拆成独立 AI 任务，也不依赖最后的长期记忆召回合并。上下文变长时由 `DefaultTokenCompressor` 和 `request_context_compaction` 在当前连续任务内建立检查点并从下一 Index 继续，全文完成后一次性输出完整大纲。
4. **每个智能体独立记忆**：大纲范围结论只写入大纲智能体自己的 `IMemoryUnit`；商务/技术候选索引和边界复核只写入提取智能体自己的 `IMemoryUnit`。两个智能体不会互相召回记忆。
5. **提取智能体可独立全文分析**：提取智能体自行读取 stats/outline，并使用 `view <docx> text --startIndex I --endIndex J` 按每组 200 个元素覆盖全文；疑难位置使用 annotated Index 范围，每次精读最多 20 个元素。组合模式下修复后 outline 能提高定位质量，但不是提取智能体运行的前置条件。
6. **XPath 与 Body Index 分离**：OfficeCLI 1.0.136 的 text/annotated 输出为 `[XPath=/body/p[N], Index=I] 原文`。XPath 是一基同类型段落路径，商务标/技术标边界使用它；Index 是零基 `Body.ChildElements[I]` 位置，大纲最终 JSON 和 `--startIndex/--endIndex` 精确重读使用它。Agent 必须成对保存、禁止互相换算，并忽略 paraId。只有截断、同名冲突或边界不清时，才对少量存疑段落使用 `get <docx> /body/p[N] --depth 0`。
7. **确定性校验和导出**：宿主验证大纲标题、路径、层级以及商务技术范围；校验失败时让智能体修正，再由本地代码写入 Word 副本。

大纲项固定输出为 `[{"title":"原文标题","index":"1","level":1}]`，其中 index 是 OfficeCLI 打印的零基 `Body.ChildElements Index` 字符串；宿主直接按该 Index 定位并校验标题段落。商务标/技术标边界和证据仍使用位置 XPath `/body/p[N]`。Demo 不接受 `--para-id`；大纲修复只设置 outline level，不增删 body 元素，因此后续提取阶段的 XPath 与 Index 都保持稳定。`view stats --page-count` 返回的 `Body.ChildElements` 数量也会被宿主解析并写入大纲扫描日志。

输出文件：

- `outline-repair-agent-context.json`（大纲智能体运行时持续更新）；
- `tender-extraction-agent-context.json`（提取智能体运行时持续更新）；
- `提取结果.json`；
- `大纲修复结果.json`；
- `原文件名-大纲修复后.docx`；
- `商务标.docx`；
- `技术标.docx`。

## officecli 权限边界

Agent 的文档访问只允许调用 officecli MCP；`help` 和 `load_skill word` 作为元命令保留。实际文档读取严格限制为以下组合：

- `view <docx> stats --page-count`；
- `view <docx> outline`；
- `view <docx> text --start S --end E`，最多 200 个一基输出项；
- `view <docx> annotated --start S --end E`，最多 20 个一基输出项；
- `view <docx> text --startIndex I --endIndex J`，最多 200 个零基 `Body.ChildElements`；
- `view <docx> annotated --startIndex I --endIndex J`，最多 20 个零基 `Body.ChildElements`；
- `get <docx> <path> --depth 0`；
- `query <docx> <selector> --find <find>`。

自定义文档读取工具不会注册给 Agent。两个智能体的实际任务权限只允许零基 `--startIndex/--endIndex` 读取正文；`--page` 和旧 `--start/--end` 扫描会被阶段权限拒绝。`issues`、`html`、Agent 侧 `validate`、`--para-id`、无范围 text/annotated、无 `--find` query、非 depth 0 get 以及所有修改命令也会被拒绝。宿主在导出后仍会直接调用 officecli validate，这不属于 Agent 工具调用。

所有 officecli 命令都使用普通文本输出，审批层会拒绝 JSON 输出开关。

## 已知限制

- Agent 不再使用自定义全文页索引，因此全文覆盖率取决于 officecli 分页扫描是否完整执行；日志会记录每轮 AI 输出、officecli 调用和上下文压缩，便于核查漏扫页面。
- 生产环境仍建议人工复核提取结果。
- 当前只支持 `.docx`，不处理旧版 `.doc`、PDF 或扫描件。
