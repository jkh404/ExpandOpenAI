# Scripts

`scripts` 目录放解决方案级脚本。当前解决方案有两个 NuGet 包，打包时通过 `-Package` 明确选择目标包。

## 打包全部包

```powershell
.\scripts\pack-nuget.cmd -Package All -Version 1.0.0
```

`-Package All` 是默认值，因此也可以写成：

```powershell
.\scripts\pack-nuget.cmd -Version 1.0.0
```

## 打包单个包

```powershell
.\scripts\pack-nuget.cmd -Package ExpandOpenAI -Version 1.0.0
.\scripts\pack-nuget.cmd -Package ExpandVectorStore.Qdrant -Version 1.0.0
```

## 参数

| 参数 | 说明 |
| --- | --- |
| `-Package` | `All`、`ExpandOpenAI` 或 `ExpandVectorStore.Qdrant`。默认 `All`。 |
| `-Version` | 可选的 SemVer 版本覆盖，例如 `1.0.0` 或 `1.0.0-preview.1`。 |
| `-Configuration` | 构建配置，默认 `Release`。 |
| `-OutputDir` | 输出目录，默认 `artifacts\nuget`。 |
| `-SkipBuild` | 传给 `dotnet pack --no-build`。 |
| `-NoSymbols` | 不生成 `.snupkg` 符号包。 |
