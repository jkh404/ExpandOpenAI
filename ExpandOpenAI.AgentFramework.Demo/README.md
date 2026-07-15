# ExpandOpenAI.AgentFramework.Demo

`ExpandOpenAI.AgentFramework.Demo` 是一个本机 Web 小说撰写智能体示例。它连接 OpenAI-compatible Chat 服务，在指定小说工作区中持续创作；网页对话会流式显示文本、思考与工具执行过程。

## 能力

- 一个工作区可保存多个小说会话。关闭程序后再次使用相同工作区，可继续选择原会话创作。
- 智能体可以在同一任务中自主多轮调用工具：盘点资料、阅读章节、查询公开网页或 API、写入提纲和章节，再依据结果继续行动。
- 文件工具只允许读取、创建和写入工作区内的 `.txt`、`.md` 纯文本文件；无删除工具。覆盖已有文件前，智能体必须先读取原文，并显式确认覆盖。
- `fetch_http` 只读取公开、无需认证的 HTTP GET 资源；它拒绝回环、私有网络、认证 URL 和非 HTTP 地址，并限制响应大小。
- 所有已注册文件、HTTP 与记忆工具均自动批准，调用过程仍实时显示在对话中。
- `DefaultTokenCompressor` 保留近期原文、压缩早期历史，并将更早摘要归档到当前会话的长期记忆。智能体可用 `recall_memory` 召回人物、剧情决定和章节摘要。
- 右侧工作区采用可折叠的文件资源管理器视图，每秒自动检测 `.txt`、`.md` 文件变化；点击文件可直接预览内容。

## 两层记忆

小说内容绝不进入全局记忆，避免多个作品串设定。

- **会话记忆**：人物、世界观、章节、伏笔、创作过程和压缩摘要。每个小说会话独立保存。
- **全局记忆（可选）**：仅限跨小说也有效的用户写作偏好、协作约定或输出格式。宿主传入全局记忆体时，智能体可自行判断是否调用 `remember_global_memory` 保存；没有传入时，该工具不会注册。

网页右侧“项目检查器”会分别显示工作区文件、当前会话记忆片段和全局记忆片段，便于演示长期创作与上下文压缩。

## 工作区与持久化

原稿、提纲和研究资料由智能体写在所选工作区。Demo 的私有状态在：

```text
<小说工作区>/.expandopenai-agent/novel-sessions.json
<小说工作区>/.expandopenai-agent/global-memories.json
```

`novel-sessions.json` 保存所有会话的活动历史、压缩摘要标记和会话长期记忆。`global-memories.json` 仅在宿主启用全局记忆时保存跨小说偏好。智能体不能通过文件工具访问 `.expandopenai-agent`。

## 配置

打开网页后，在“连接与工作区设置”中填写：

- OpenAI-compatible 服务 URL
- API Key
- Chat 模型
- Chat 请求路径
- 小说工作区文件夹
- 上下文压缩阈值（默认 `100000` tokens，可配置 `8000`–`2000000`）

压缩阈值按活动历史的 Token 估算值触发。120k 上下文模型建议设置为 90k–100k，为系统提示、工具结果和模型输出留出余量；1M 上下文模型可按实际输出需求提高到约 800k。

API Key 仅保存在当前用户的本地配置中，不会返回给浏览器：

```text
%LOCALAPPDATA%\ExpandOpenAI\AgentFramework.Demo\settings.json
```

环境变量可覆盖本地配置：

```powershell
$env:OPENAI_ENDPOINT = "https://example.com/v1"
$env:OPENAI_API_KEY = "your-api-key"
$env:OPENAI_MODEL = "your-chat-model"

# 可选
$env:OPENAI_REQUEST_PATH = "chat/completions"
$env:OPENAI_ENABLE_THINKING = "false"
$env:EXPANDOPENAI_NOVEL_WORKSPACE = "D:\Writing\Project-Starlight"
```

如果服务不校验密钥，`OPENAI_API_KEY` 仍需设置一个非空占位值。

## 运行

直接双击：

```text
Run-Demo.cmd
```

或在终端运行：

```powershell
dotnet run --project ExpandOpenAI.AgentFramework.Demo
```

然后用浏览器打开 [http://127.0.0.1:5179](http://127.0.0.1:5179)。

不连接模型服务即可验证工作区边界、会话持久化与两层记忆：

```powershell
dotnet run --project ExpandOpenAI.AgentFramework.Demo -- --workspace-self-test
dotnet run --project ExpandOpenAI.AgentFramework.Demo -- --stream-format-self-test
```

## 建议试用指令

```text
盘点工作区，读取已有设定，为第一卷建立章节规划，并把规划写入 planning/volume-1.md。
```

```text
读取人物设定和最近两章，检查时间线冲突。先提出修订方案，确认没有矛盾后更新 planning/continuity.md。
```

```text
继续写第二章，保持与前文一致，写入 chapters/chapter-02.md，并说明新增的伏笔。
```

```text
以后续所有小说的默认叙事风格为冷峻克制、第三人称有限视角。仅当它确实适合跨作品复用时，再保存为全局偏好。
```

## 边界说明

- 网页只绑定 `127.0.0.1`，不对局域网开放。
- HTTP 工具不能发送 API Key、Cookie、自定义请求头或本地文件。
- 工具返回的网页文本只可作为资料，不能当作系统指令。
- 文件路径必须相对于小说工作区；绝对路径、`..` 路径逃逸、重解析点以及非 `.txt` / `.md` 文件都会被拒绝。
