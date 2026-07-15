# ExpandOpenAI

这个解决方案当前维护三个 NuGet 包：

| 包 | 项目 | README | 说明 |
| --- | --- | --- | --- |
| `ExpandOpenAI` | `ExpandOpenAI/ExpandOpenAI.csproj` | `ExpandOpenAI/README.md` | OpenAI Compatible Chat Completions、Responses API、embeddings 和 reranking 实现，基于 `Microsoft.Extensions.AI`。 |
| `ExpandOpenAI.AgentFramework` | `ExpandOpenAI.AgentFramework/ExpandOpenAI.AgentFramework.csproj` | `ExpandOpenAI.AgentFramework/README.md` | Agent 基础设施类库，提供独立会话、分层记忆、按轮次历史压缩和工具审批。 |
| `ExpandVectorStore.Qdrant` | `ExpandVectorStore.Qdrant/ExpandVectorStore.Qdrant.csproj` | `ExpandVectorStore.Qdrant/README.md` | Qdrant vector store provider，基于 `Microsoft.Extensions.VectorData`。 |

根目录 README 只作为解决方案入口。每个包的 NuGet 说明、示例和限制请维护在对应项目目录下的 `README.md`，并由各自的 `.csproj` 打包进 nupkg。

## 项目结构

- `ExpandOpenAI/`：核心 OpenAI Compatible Chat Completions、Responses API、embeddings 和 reranking 包。
- `ExpandOpenAI.AgentFramework/`：依赖核心包的 Agent 抽象与默认实现。
- `ExpandOpenAI.AgentFramework.Demo/`：本机 Web 小说撰写智能体示例；它在指定工作区内用文件与 HTTP 工具自主创作，跨启动保存多会话与压缩摘要，并分别展示会话记忆和跨小说偏好记忆。
- `ExpandVectorStore.Qdrant/`：Qdrant 向量存储包。
- `ExpandOpenAI.TestConsole/`：本地示例和验证项目。
- `ExpandOpenAI.Tests/`：核心类库的自动化测试项目。
- `scripts/`：解决方案级脚本。
- `artifacts/`：本地构建和打包输出。

## 构建

```powershell
dotnet build ExpandOpenAI.slnx
```

运行自动化测试：

```powershell
dotnet test ExpandOpenAI.slnx
```

## NuGet 打包

默认打包全部可发布包：

```powershell
.\scripts\pack-nuget.cmd -Version 1.0.0
```

只打 `ExpandOpenAI`：

```powershell
.\scripts\pack-nuget.cmd -Package ExpandOpenAI -Version 1.0.0
```

只打 `ExpandOpenAI.AgentFramework`：

```powershell
.\scripts\pack-nuget.cmd -Package ExpandOpenAI.AgentFramework -Version 1.0.0
```

只打 `ExpandVectorStore.Qdrant`：

```powershell
.\scripts\pack-nuget.cmd -Package ExpandVectorStore.Qdrant -Version 1.0.0
```

常用参数：

```powershell
.\scripts\pack-nuget.cmd -Package All -Version 1.0.0-preview.1
.\scripts\pack-nuget.cmd -Package ExpandOpenAI -Version 1.0.0 -OutputDir .\artifacts\nuget\ExpandOpenAI
.\scripts\pack-nuget.cmd -Package ExpandOpenAI.AgentFramework -Version 1.0.0 -OutputDir .\artifacts\nuget\ExpandOpenAI.AgentFramework
.\scripts\pack-nuget.cmd -Package ExpandVectorStore.Qdrant -Version 1.0.0 -NoSymbols
```

默认输出目录为 `artifacts\nuget`，会生成 `.nupkg`，未指定 `-NoSymbols` 时还会生成 `.snupkg`。

## 文档维护约定

- 修改 `ExpandOpenAI` 包功能时，同步更新 `ExpandOpenAI/README.md`。
- 修改 Agent 功能时，同步更新 `ExpandOpenAI.AgentFramework/README.md`。
- 修改 `ExpandVectorStore.Qdrant` 包功能时，同步更新 `ExpandVectorStore.Qdrant/README.md`。
- 根 README 只维护解决方案结构、包清单和通用脚本说明，避免混入单个包的详细 API 文档。

## License

本项目使用 [MIT License](./LICENSE.txt)。
