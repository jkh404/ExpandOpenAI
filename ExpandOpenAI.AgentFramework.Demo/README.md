# ExpandOpenAI.AgentFramework.Demo

`ExpandOpenAI.AgentFramework.Demo` 是一个本机 Web 小说撰写智能体示例。前端使用 Vue 3、TypeScript 与 Vite，后端使用 ASP.NET Core 10；它连接 OpenAI-compatible Chat 服务，在指定根工作区中持续创作。

## 能力

- 一个根工作区可保存多个小说会话；每个会话自动获得一个独立子目录，小说文件、会话历史和会话记忆不会串到其他作品。关闭程序后再次使用相同根工作区，可继续选择原会话创作。
- 智能体可以在同一任务中自主多轮调用工具：盘点资料、阅读章节、查询公开网页或 API、写入提纲和章节，再依据结果继续行动。
- 对话文本会逐块显示在独立滚动的中间写作区；默认跟随最新内容，用户向上滚动后暂停跟随并显示“回到最新”按钮。运行期间执行按钮切换为“停止执行”，可取消后台任务并保留页面上已经生成的内容。
- 创作任务在后台运行并获得独立 `runId`。刷新或关闭页面只会断开事件订阅，不会取消模型任务；重新打开页面会自动恢复当前任务并重放遗漏事件。
- 每次任务的状态、文本增量、工具调用、压缩和错误事件都会持久化为 NDJSON，便于恢复界面和排查问题。
- 运行观察器显示当前阶段、已运行时长与每 5 秒一次的服务端心跳。即使模型长时间没有返回首个文字，也能区分“仍在运行”和“请求失败”。
- 文件、HTTP 和记忆工具以独立活动轨迹展示工具名、参数摘要、执行状态和结果摘要，不与小说正文混排。
- 文件工具只允许读取、创建和写入工作区内的 `.txt`、`.md` 纯文本文件；无删除工具。覆盖已有文件前，智能体必须先读取原文，并显式确认覆盖。
- 长文件可使用 `append_workspace_file` 分段追加，并通过预期字符数防止重复写入；局部修订可使用 `replace_workspace_text` 和 SHA-256 校验防止覆盖新版本。所有写入采用临时文件原子替换。
- `fetch_http` 只读取公开、无需认证的 HTTP GET 资源；它拒绝回环、私有网络、认证 URL 和非 HTTP 地址，并限制响应大小。
- 所有已注册文件、HTTP 与记忆工具均自动批准，调用过程仍实时显示在对话中。
- `DefaultTokenCompressor` 保留近期原文、压缩早期历史，并将更早摘要归档到当前会话的长期记忆。智能体可用 `recall_memory` 召回人物、剧情决定和章节摘要。
- `recall_memory` 对会话记忆按相关性检索，并在 `All` / `Global` 范围附带数量受控的全局稳定偏好；用户称呼等信息不再依赖查询词恰好命中。
- 右侧工作区采用可折叠的文件资源管理器视图，每秒自动检测 `.txt`、`.md` 文件变化；点击文件可直接预览内容。
- 右侧“上下文”页显示活动历史 token 估算、压缩阈值、输出上限、记忆数量、压缩记录和最近后台任务。
- 全局偏好支持新增、更新和删除；会话压缩记忆支持单条删除。每个小说会话还可以保存独立的补充提示词。

## 两层记忆

小说内容绝不进入全局记忆，避免多个作品串设定。

- **会话记忆**：人物、世界观、章节、伏笔、创作过程和压缩摘要。每个小说会话独立保存。
- **全局记忆（可选）**：仅限跨小说也有效的用户写作偏好、协作约定或输出格式。宿主传入全局记忆体时，智能体可自行判断是否调用 `remember_global_memory` 保存；没有传入时，该工具不会注册。

网页右侧“项目检查器”会分别显示工作区文件、当前会话记忆片段和全局记忆片段，便于演示长期创作与上下文压缩。

## 工作区与持久化

设置中选择的是根工作区。每次新建会话，Demo 都会创建带会话 ID 后缀的独立子目录；切换会话时，智能体文件工具和右侧资源管理器会一起切换目录：

```text
<根工作区>/规则怪谈-游乐园-a1b2c3d4/
<根工作区>/另一部小说-e5f6a7b8/
<根工作区>/.expandopenai-agent/novel-sessions.json
<根工作区>/.expandopenai-agent/global-memories.json
<根工作区>/.expandopenai-agent/runs/<run-id>.meta.json
<根工作区>/.expandopenai-agent/runs/<run-id>.events.ndjson
```

原稿、提纲和研究资料只写入当前会话子目录。`novel-sessions.json` 保存所有会话的子目录映射、活动历史、压缩摘要标记和会话长期记忆。`global-memories.json` 仅在宿主启用全局记忆时保存跨小说偏好。智能体文件工具被限制在当前会话子目录，因此不能访问其他作品或根目录的 `.expandopenai-agent`。

浏览器刷新后，仍在运行的任务可以重新订阅。若整个 Demo 服务进程退出，运行中的模型请求无法跨进程续跑；再次启动时会保留已有事件并将任务明确标记为 `interrupted`，不会伪装成成功完成。

旧版状态文件没有会话子目录字段时，首次启动会为各会话生成并回写独立子目录；旧版直接写在根目录中的文件不会被自动移动或删除。

## 配置

打开网页后，在“连接与工作区设置”中填写：

- OpenAI-compatible 服务 URL
- API Key
- Chat 模型
- Chat 请求路径
- 小说根工作区文件夹
- 上下文压缩阈值（默认 `100000` tokens，可配置 `8000`–`2000000`）
- 单次模型输出上限（默认 `16384` tokens，可配置 `1000`–`131072`；长章节的工具参数也会占用输出 tokens）
- 可编辑的完整系统提示词；支持 `{{assistant.name}}`、`{{utcNow}}`、`{{workspace.root}}` 动态占位符，可一键恢复默认值

压缩阈值按活动历史的 Token 估算值触发。120k 上下文模型建议设置为 90k–100k，为系统提示、工具结果和模型输出留出余量；1M 上下文模型可按实际输出需求提高到约 800k。

模型偶尔会把工具参数包在 `$raw` 中，或使用 `relative_path` 一类变体。AgentFramework 会在执行前恢复嵌套 JSON 并按工具参数架构归一名称；如果 JSON 已因输出截断而不完整，工具不会写入部分文件，而会把可重试说明返回给模型。

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
$env:EXPANDOPENAI_NOVEL_WORKSPACE = "D:\Writing\Novels"
$env:EXPANDOPENAI_SYSTEM_PROMPT = "你的自定义系统提示词"
```

如果服务不校验密钥，`OPENAI_API_KEY` 仍需设置一个非空占位值。

## 运行

前端构建需要 Node.js `20.19+` 或 `22.12+`。首次构建时，MSBuild 会根据 `ClientApp/package-lock.json` 自动执行 `npm ci`，之后每次构建都会运行 TypeScript 检查和 Vite 生产构建。

直接双击：

```text
Run-Demo.cmd
```

或在终端运行：

```powershell
dotnet run --project ExpandOpenAI.AgentFramework.Demo
```

然后用浏览器打开 [http://127.0.0.1:5179](http://127.0.0.1:5179)。

前端开发时可以分别启动 ASP.NET Core 与 Vite 热更新服务器：

```powershell
# 终端 1
dotnet run --project ExpandOpenAI.AgentFramework.Demo

# 终端 2
cd ExpandOpenAI.AgentFramework.Demo/ClientApp
npm install
npm run dev
```

开发页面地址是 `http://127.0.0.1:5173`，`/api` 会代理到 ASP.NET Core 的 `5179` 端口。

不连接模型服务即可验证工作区边界、会话持久化与两层记忆：

```powershell
dotnet run --project ExpandOpenAI.AgentFramework.Demo -- --workspace-self-test
dotnet run --project ExpandOpenAI.AgentFramework.Demo -- --stream-format-self-test
dotnet run --project ExpandOpenAI.AgentFramework.Demo -- --run-manager-self-test
```

Vue 组件和流式解析测试：

```powershell
cd ExpandOpenAI.AgentFramework.Demo/ClientApp
npm test
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
- 文件路径必须相对于当前会话工作区；绝对路径、`..` 路径逃逸、重解析点以及非 `.txt` / `.md` 文件都会被拒绝。
- 发送给模型的系统提示只包含当前会话目录名，不包含本机绝对路径，避免从路径推断 Windows 用户名等环境信息。
