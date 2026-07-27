using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace OfficecliDemo;

internal static class Program
{
    private static readonly Version MinimumOfficeCliVersion = new(1, 0, 136);

    private sealed record ExtractionArtifacts(
        string ResultPath,
        string BusinessPath,
        string TechnicalPath,
        bool BusinessExported,
        bool TechnicalExported);

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        using var cancellationSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };

        try
        {
            var options = DemoOptions.Load(args);
            Directory.CreateDirectory(options.OutputDirectory);
            using var loggerFactory = CreateLoggerFactory(options);
            var logger = loggerFactory.CreateLogger("OfficecliDemo");
            var officeCliVersion = await ReadOfficeCliVersionAsync(
                options,
                cancellationSource.Token).ConfigureAwait(false);

            logger.LogInformation("[Startup] 配置目录：{Directory}", options.ConfigurationDirectory);
            logger.LogInformation(
                "[Startup] OfficeCLI 版本：{Version}（要求 >= {MinimumVersion}；已启用真实分页、XPath/Index 和 Body Index 范围）",
                officeCliVersion,
                MinimumOfficeCliVersion);
            logger.LogInformation("[Startup] 接口地址：{Endpoint}", options.Endpoint);
            logger.LogInformation("[Startup] 模型名称：{Model}", options.ModelId);
            logger.LogInformation("[Startup] 模型思考输出：{EnableThinking}", options.EnableThinking);
            logger.LogInformation("[Startup] 源招标书：{Document}", options.SourceDocumentPath);
            logger.LogInformation("[Startup] 输出目录：{OutputDirectory}", options.OutputDirectory);
            logger.LogInformation("[Startup] 运行模式：{RunMode}", options.RunMode);
            logger.LogInformation(
                "[Startup] 大纲修复智能体上下文 JSON：{ContextPath}",
                Path.Combine(options.OutputDirectory, OutlineRepairAgent.ContextFileName));
            logger.LogInformation(
                "[Startup] 商务/技术提取智能体上下文 JSON：{ContextPath}",
                Path.Combine(options.OutputDirectory, TenderExtractionAgent.ContextFileName));
            logger.LogInformation(
                "[Startup] Token 速度统计：已启用；每次底层模型请求后打印 [Model][TokenSpeed]，口径=OutputTokens/请求总耗时");
            logger.LogInformation(
                "[Startup] 上下文策略：DefaultTokenCompressor（历史阈值 {Threshold}，单消息阈值 {MessageThreshold} tokens）+ 主动压缩工具={CompactionEnabled} + 会话长期记忆（最多召回 {RecallCount} 条）",
                options.MaximumHistoryTokenEstimate,
                options.MaximumMessageTokenEstimate,
                options.EnableContextCompactionTool,
                options.MemoryRecallMaxResults);
            logger.LogInformation(
                "[Startup] 输出：Prompt/工具/压缩/TokenSpeed 使用结构化日志，Prompt={Prompts}；AI普通输出(Console流式)={AiOutput}，AI思考(Console流式)={Reasoning}；工具参数={ToolArguments}，工具结果={ToolResults}，日志级别={Level}",
                options.ShowPrompts,
                options.ShowAiOutput,
                options.ShowAiReasoning,
                options.ShowToolArguments,
                options.ShowToolResults,
                options.MinimumLogLevel);

            if (options.RunMode == DemoRunMode.ExtractionOnly)
            {
                logger.LogInformation(
                    "[Pipeline] ExtractionOnly：直接创建独立 TenderExtractionAgent；不创建或调用 OutlineRepairAgent");
                var standaloneExtraction = await RunExtractionAsync(
                    options,
                    loggerFactory,
                    logger,
                    cancellationSource.Token).ConfigureAwait(false);
                Console.WriteLine();
                Console.WriteLine($"提取计划：{standaloneExtraction.ResultPath}");
                Console.WriteLine($"商务标：{(standaloneExtraction.BusinessExported ? standaloneExtraction.BusinessPath : "未找到，未生成")}");
                Console.WriteLine($"技术标：{(standaloneExtraction.TechnicalExported ? standaloneExtraction.TechnicalPath : "未找到，未生成")}");
                return 0;
            }

            var workingDocument = await WorkingDocumentPreparer.PrepareAsync(
                options.SourceDocumentPath,
                options.OutputDirectory,
                logger,
                (path, cancellationToken) => TryCloseOfficeCliResidentAsync(
                    options,
                    path,
                    logger,
                    cancellationToken),
                cancellationSource.Token).ConfigureAwait(false);
            var pendingRepairDocumentPath = workingDocument.DocumentPath;
            logger.LogInformation(
                "[Document] 已复制大纲待修复工作副本：{Document}；正式修复文件将在完整大纲成功应用后发布",
                pendingRepairDocumentPath);
            var workingOptions = options.ForDocument(pendingRepairDocumentPath);

            logger.LogInformation(
                "[Pipeline] 阶段 1/2：创建独立 OutlineRepairAgent；只负责大纲修复，且只注册 officecli 文档工具");
            OutlineRepairPlan outlinePlan;
            await using (var outlineAgent = await OutlineRepairAgent.CreateAsync(
                             workingOptions,
                             loggerFactory,
                             cancellationSource.Token).ConfigureAwait(false))
            {
                outlinePlan = await outlineAgent.RepairAsync(cancellationSource.Token).ConfigureAwait(false);
            }

            logger.LogInformation(
                "[Pipeline] OutlineRepairAgent 已完成并释放；现在发布正式大纲修复文档");
            var outlineResultPath = Path.Combine(options.OutputDirectory, "大纲修复结果.json");
            await File.WriteAllTextAsync(
                outlineResultPath,
                JsonSerializer.Serialize(outlinePlan.Items, TenderExtractionResult.JsonOptions),
                Encoding.UTF8,
                cancellationSource.Token).ConfigureAwait(false);
            var repairedDocumentPath = await WorkingDocumentPreparer.PublishRepairedAsync(
                workingDocument,
                logger,
                (path, cancellationToken) => TryCloseOfficeCliResidentAsync(
                    options,
                    path,
                    logger,
                    cancellationToken),
                cancellationSource.Token).ConfigureAwait(false);
            logger.LogInformation(
                "[Outline] 大纲修复完成，共应用 {Count} 个标题；计划已写入 {Path}",
                outlinePlan.Items.Count,
                outlineResultPath);

            if (options.RunMode == DemoRunMode.OutlineOnly)
            {
                logger.LogInformation(
                    "[Pipeline] OutlineOnly：大纲修复完成；不会创建或调用 TenderExtractionAgent");
                Console.WriteLine();
                Console.WriteLine($"大纲修复结果：{outlineResultPath}");
                Console.WriteLine($"大纲修复文档：{repairedDocumentPath}");
                return 0;
            }

            var extractionOptions = options.ForDocument(repairedDocumentPath);
            logger.LogInformation(
                "[Pipeline] 阶段 2/2：将正式修复文档作为输入创建全新的 TenderExtractionAgent；组合仅传递文件，不共享对象、会话、压缩历史或长期记忆");
            var extraction = await RunExtractionAsync(
                extractionOptions,
                loggerFactory,
                logger,
                cancellationSource.Token).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine($"大纲修复结果：{outlineResultPath}");
            Console.WriteLine($"大纲修复文档：{repairedDocumentPath}");
            Console.WriteLine($"提取计划：{extraction.ResultPath}");
            Console.WriteLine($"商务标：{(extraction.BusinessExported ? extraction.BusinessPath : "未找到，未生成")}");
            Console.WriteLine($"技术标：{(extraction.TechnicalExported ? extraction.TechnicalPath : "未找到，未生成")}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("操作已取消。 ");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task<Version> ReadOfficeCliVersionAsync(
        DemoOptions options,
        CancellationToken cancellationToken)
    {
        var result = await OfficeCliProcess.RunAsync(
            options.OfficeCliCommand,
            ["--version"],
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess("读取 OfficeCLI 版本");
        if (!TryReadOfficeCliVersion(result.StandardOutput, out var version)
            || version < MinimumOfficeCliVersion)
        {
            throw new InvalidOperationException(
                $"OfficecliDemo 要求 OfficeCLI {MinimumOfficeCliVersion} 或更高版本，当前输出：{result.StandardOutput.Trim()} ");
        }

        return version;
    }

    internal static bool TryReadOfficeCliVersion(string? output, out Version version)
    {
        version = new Version();
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var match = Regex.Match(
            output,
            @"(?<!\d)(?<version>\d+\.\d+\.\d+(?:\.\d+)?)(?!\d)",
            RegexOptions.CultureInvariant);
        return match.Success
            && Version.TryParse(match.Groups["version"].Value, out version!);
    }

    private static async Task<ExtractionArtifacts> RunExtractionAsync(
        DemoOptions inputOptions,
        ILoggerFactory loggerFactory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        TenderExtractionResult result;
        await using (var extractionAgent = await TenderExtractionAgent.CreateAsync(
                         inputOptions,
                         loggerFactory,
                         cancellationToken).ConfigureAwait(false))
        {
            result = await extractionAgent.ExtractAsync(cancellationToken).ConfigureAwait(false);
        }

        var resultPath = Path.Combine(inputOptions.OutputDirectory, "提取结果.json");
        await File.WriteAllTextAsync(
            resultPath,
            JsonSerializer.Serialize(result, TenderExtractionResult.JsonOptions),
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);

        var exporter = new WordSectionExporter(inputOptions.SourceDocumentPath);
        var businessPath = Path.Combine(inputOptions.OutputDirectory, "商务标.docx");
        var technicalPath = Path.Combine(inputOptions.OutputDirectory, "技术标.docx");
        var businessExported = exporter.Export(result.Business, businessPath);
        var technicalExported = exporter.Export(result.Technical, technicalPath);
        logger.LogInformation(
            "[Export] 商务标导出={BusinessExported}，segments={BusinessSegments}；技术标导出={TechnicalExported}，segments={TechnicalSegments}",
            businessExported,
            result.Business.Segments.Count,
            technicalExported,
            result.Technical.Segments.Count);

        if (businessExported)
        {
            await ValidateOutputAsync(
                inputOptions,
                businessPath,
                logger,
                cancellationToken).ConfigureAwait(false);
        }

        if (technicalExported)
        {
            await ValidateOutputAsync(
                inputOptions,
                technicalPath,
                logger,
                cancellationToken).ConfigureAwait(false);
        }

        return new ExtractionArtifacts(
            resultPath,
            businessPath,
            technicalPath,
            businessExported,
            technicalExported);
    }

    private static async Task ValidateOutputAsync(
        DemoOptions options,
        string documentPath,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        OfficeCliProcessResult? sourceValidation = null;
        OfficeCliProcessResult? outputValidation = null;
        logger.LogInformation("[OfficeCli] 开始验证导出文档：{Document}", documentPath);
        try
        {
            sourceValidation = await OfficeCliProcess.RunAsync(
                options.OfficeCliCommand,
                ["validate", options.SourceDocumentPath],
                TimeSpan.FromMinutes(2),
                cancellationToken).ConfigureAwait(false);
            outputValidation = await OfficeCliProcess.RunAsync(
                options.OfficeCliCommand,
                ["validate", documentPath],
                TimeSpan.FromMinutes(2),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await CloseOfficeCliResidentAsync(options, documentPath, logger).ConfigureAwait(false);
            await CloseOfficeCliResidentAsync(options, options.SourceDocumentPath, logger).ConfigureAwait(false);
        }

        if (outputValidation.ExitCode == 0)
        {
            logger.LogInformation("[OfficeCli] 验证通过：{Document}", documentPath);
            return;
        }

        var sourceErrorCount = ReadValidationErrorCount(sourceValidation);
        var outputErrorCount = ReadValidationErrorCount(outputValidation);
        if (sourceErrorCount is not null
            && outputErrorCount is not null
            && outputErrorCount <= sourceErrorCount)
        {
            logger.LogWarning(
                "[OfficeCli] {Document} 继承了源文档已有的 {ErrorCount} 个 schema 问题，未发现新增问题",
                Path.GetFileName(documentPath),
                outputErrorCount);
            return;
        }

        outputValidation.EnsureSuccess($"验证 {Path.GetFileName(documentPath)}");
    }

    internal static int? ReadValidationErrorCount(OfficeCliProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var output = string.Concat(
            result.StandardOutput,
            Environment.NewLine,
            result.StandardError);
        var match = Regex.Match(
            output,
            "Found\\s+(?<count>\\d+)\\s+validation error",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups["count"].Value, out var count)
            ? count
            : null;
    }

    private static async Task CloseOfficeCliResidentAsync(
        DemoOptions options,
        string documentPath,
        ILogger logger)
    {
        try
        {
            await OfficeCliProcess.RunAsync(
                options.OfficeCliCommand,
                ["close", documentPath],
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            logger.LogDebug("[OfficeCli] 已释放 resident：{Document}", documentPath);
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "[OfficeCli] 释放 resident 失败，但不覆盖主要验证结果：{Document}",
                documentPath);
        }
    }

    private static async Task TryCloseOfficeCliResidentAsync(
        DemoOptions options,
        string documentPath,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("[OfficeCli] 复制前尝试释放旧 resident：{Document}", documentPath);
            await OfficeCliProcess.RunAsync(
                options.OfficeCliCommand,
                ["close", documentPath],
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogDebug(
                exception,
                "[OfficeCli] 复制前释放 resident 未成功，将继续尝试复制：{Document}",
                documentPath);
        }
    }

    private static ILoggerFactory CreateLoggerFactory(DemoOptions options)
    {
        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(options.MinimumLogLevel);
            builder.AddSimpleConsole(console =>
            {
                console.ColorBehavior = LoggerColorBehavior.Enabled;
                console.IncludeScopes = true;
                console.SingleLine = false;
                console.TimestampFormat = "HH:mm:ss.fff ";
                console.UseUtcTimestamp = false;
            });
        });
    }
}
