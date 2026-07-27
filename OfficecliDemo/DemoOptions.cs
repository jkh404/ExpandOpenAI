using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OfficecliDemo;

internal enum DemoRunMode
{
    Combined,
    OutlineOnly,
    ExtractionOnly,
}

internal sealed class DemoOptions
{
    public required string SourceDocumentPath { get; init; }

    public required string OutputDirectory { get; init; }

    public required string ModelId { get; init; }

    public required string ApiKey { get; init; }

    public required Uri Endpoint { get; init; }

    public required string ConfigurationDirectory { get; init; }

    public DemoRunMode RunMode { get; init; } = DemoRunMode.Combined;

    public string RequestPath { get; init; } = "chat/completions";

    public string OfficeCliCommand { get; init; } = "officecli";

    public bool EnableThinking { get; init; } = true;

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public int MaximumToolCalls { get; init; } = 300;

    public int MaximumHistoryTokenEstimate { get; init; } = 12_000;

    public int MaximumMessageTokenEstimate { get; init; } = 12_000;

    public int RecentSummaryTurnCount { get; init; } = 2;

    public int SummaryMaxOutputTokens { get; init; } = 1_000;

    public int MemoryRecallMaxResults { get; init; } = 50;

    public int BodyIndexScanBatchSize { get; init; } = 200;

    public bool EnableContextCompactionTool { get; init; } = true;

    public LogLevel MinimumLogLevel { get; init; } = LogLevel.Information;

    public bool ShowPrompts { get; init; } = true;

    public bool ShowAiOutput { get; init; } = true;

    public bool ShowAiReasoning { get; init; } = true;

    public bool ShowToolArguments { get; init; } = true;

    public bool ShowToolResults { get; init; }

    public int MaximumLogTextLength { get; init; } = 12_000;

    public DemoOptions ForDocument(string documentPath)
    {
        return new DemoOptions
        {
            SourceDocumentPath = Path.GetFullPath(documentPath),
            OutputDirectory = OutputDirectory,
            ModelId = ModelId,
            ApiKey = ApiKey,
            Endpoint = Endpoint,
            ConfigurationDirectory = ConfigurationDirectory,
            RunMode = RunMode,
            RequestPath = RequestPath,
            OfficeCliCommand = OfficeCliCommand,
            EnableThinking = EnableThinking,
            RequestTimeout = RequestTimeout,
            MaximumToolCalls = MaximumToolCalls,
            MaximumHistoryTokenEstimate = MaximumHistoryTokenEstimate,
            MaximumMessageTokenEstimate = MaximumMessageTokenEstimate,
            RecentSummaryTurnCount = RecentSummaryTurnCount,
            SummaryMaxOutputTokens = SummaryMaxOutputTokens,
            MemoryRecallMaxResults = MemoryRecallMaxResults,
            BodyIndexScanBatchSize = BodyIndexScanBatchSize,
            EnableContextCompactionTool = EnableContextCompactionTool,
            MinimumLogLevel = MinimumLogLevel,
            ShowPrompts = ShowPrompts,
            ShowAiOutput = ShowAiOutput,
            ShowAiReasoning = ShowAiReasoning,
            ShowToolArguments = ShowToolArguments,
            ShowToolResults = ShowToolResults,
            MaximumLogTextLength = MaximumLogTextLength,
        };
    }

    public static DemoOptions Load(string[] args)
    {
        if (args.Length > 3)
        {
            throw new ArgumentException(
                "最多只能传入三个可选参数：<招标书.docx> [输出目录] [combined|outline|extraction]。 ");
        }

        var configurationDirectory = ResolveConfigurationDirectory();
        var configuration = new ConfigurationBuilder()
            .SetBasePath(configurationDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "OFFICECLIDEMO_")
            .Build();

        var configuredDocumentPath = args.Length > 0
            ? args[0]
            : configuration["Demo:DocumentPath"];
        var sourcePath = ReadDocumentPath(configuredDocumentPath, configurationDirectory);

        var endpointText = ReadRequiredValue(
            ReadSetting(configuration, "OpenAI:Endpoint", "OPENAI_ENDPOINT"),
            "OpenAI 接口地址（例如 http://localhost:8000/v1）：",
            secret: false,
            configurationDirectory);
        var endpoint = ReadEndpoint(endpointText, configurationDirectory);

        var modelId = ReadRequiredValue(
            ReadSetting(configuration, "OpenAI:Model", "OPENAI_MODEL"),
            "模型名称：",
            secret: false,
            configurationDirectory);
        var apiKey = ReadRequiredValue(
            ReadSetting(configuration, "OpenAI:ApiKey", "OPENAI_API_KEY"),
            "API Key（输入时不会显示明文）：",
            secret: true,
            configurationDirectory);

        var defaultOutputDirectory = Path.Combine(
            Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory,
            $"{Path.GetFileNameWithoutExtension(sourcePath)}-提取结果");
        var configuredOutputDirectory = args.Length > 1
            ? args[1]
            : configuration["Demo:OutputDirectory"];
        var outputDirectory = ReadOutputDirectory(
            configuredOutputDirectory,
            defaultOutputDirectory,
            configurationDirectory);

        return new DemoOptions
        {
            SourceDocumentPath = sourcePath,
            OutputDirectory = outputDirectory,
            ModelId = modelId,
            ApiKey = apiKey,
            Endpoint = endpoint,
            ConfigurationDirectory = configurationDirectory,
            RunMode = ParseRunMode(
                args.Length > 2
                    ? args[2]
                    : ReadSetting(configuration, "Demo:Mode", "DEMO_MODE")),
            RequestPath = ReadSetting(configuration, "OpenAI:RequestPath", "OPENAI_REQUEST_PATH")
                ?? "chat/completions",
            OfficeCliCommand = ReadSetting(configuration, "OfficeCli:Command", "OFFICECLI_COMMAND")
                ?? "officecli",
            EnableThinking = ReadBoolean(
                configuration,
                "OpenAI:EnableThinking",
                "OPENAI_ENABLE_THINKING",
                fallback: true),
            RequestTimeout = TimeSpan.FromSeconds(
                ReadPositiveInteger(configuration, "OpenAI:TimeoutSeconds", "OPENAI_TIMEOUT_SECONDS", 300)),
            MaximumToolCalls = ReadPositiveInteger(
                configuration,
                "OfficeCli:MaximumToolCalls",
                "OFFICECLI_MAX_TOOL_CALLS",
                300),
            MaximumHistoryTokenEstimate = ReadPositiveInteger(
                configuration,
                "Agent:MaximumHistoryTokenEstimate",
                "AGENT_MAXIMUM_HISTORY_TOKEN_ESTIMATE",
                12_000),
            MaximumMessageTokenEstimate = ReadPositiveInteger(
                configuration,
                "Agent:MaximumMessageTokenEstimate",
                "AGENT_MAXIMUM_MESSAGE_TOKEN_ESTIMATE",
                12_000),
            RecentSummaryTurnCount = ReadPositiveInteger(
                configuration,
                "Agent:RecentSummaryTurnCount",
                "AGENT_RECENT_SUMMARY_TURN_COUNT",
                2),
            SummaryMaxOutputTokens = ReadPositiveInteger(
                configuration,
                "Agent:SummaryMaxOutputTokens",
                "AGENT_SUMMARY_MAX_OUTPUT_TOKENS",
                1_000),
            MemoryRecallMaxResults = ReadPositiveInteger(
                configuration,
                "Agent:MemoryRecallMaxResults",
                "AGENT_MEMORY_RECALL_MAX_RESULTS",
                50),
            BodyIndexScanBatchSize = Math.Clamp(
                ReadPositiveInteger(
                    configuration,
                    "Agent:BodyIndexScanBatchSize",
                    "AGENT_BODY_INDEX_SCAN_BATCH_SIZE",
                    200),
                1,
                200),
            EnableContextCompactionTool = ReadBoolean(
                configuration,
                "Agent:EnableContextCompactionTool",
                "AGENT_ENABLE_CONTEXT_COMPACTION_TOOL",
                fallback: true),
            MinimumLogLevel = ReadLogLevel(configuration, "Logging:MinimumLevel", LogLevel.Information),
            ShowPrompts = ReadBoolean(
                configuration,
                "Logging:ShowPrompts",
                "LOGGING_SHOW_PROMPTS",
                fallback: true),
            ShowAiOutput = ReadBoolean(
                configuration,
                "Logging:ShowAiOutput",
                "LOGGING_SHOW_AI_OUTPUT",
                fallback: true),
            ShowAiReasoning = ReadBoolean(
                configuration,
                "Logging:ShowAiReasoning",
                "LOGGING_SHOW_AI_REASONING",
                fallback: true),
            ShowToolArguments = ReadBoolean(
                configuration,
                "Logging:ShowToolArguments",
                "LOGGING_SHOW_TOOL_ARGUMENTS",
                fallback: true),
            ShowToolResults = ReadBoolean(
                configuration,
                "Logging:ShowToolResults",
                "LOGGING_SHOW_TOOL_RESULTS",
                fallback: false),
            MaximumLogTextLength = Math.Clamp(
                ReadPositiveInteger(
                    configuration,
                    "Logging:MaximumTextLength",
                    "LOGGING_MAXIMUM_TEXT_LENGTH",
                    12_000),
                500,
                100_000),
        };
    }

    private static string ResolveConfigurationDirectory()
    {
        var currentDirectory = Environment.CurrentDirectory;
        var currentProjectDirectory = FindProjectConfigurationDirectory(currentDirectory);
        if (currentProjectDirectory is not null)
        {
            return currentProjectDirectory;
        }

        var executableProjectDirectory = FindProjectConfigurationDirectory(AppContext.BaseDirectory);
        if (executableProjectDirectory is not null)
        {
            return executableProjectDirectory;
        }

        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "appsettings.json")))
        {
            return AppContext.BaseDirectory;
        }

        return currentDirectory;
    }

    internal static string? FindProjectConfigurationDirectory(string startDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (directory is not null)
        {
            if (IsProjectConfigurationDirectory(directory.FullName))
            {
                return directory.FullName;
            }

            var childProjectDirectory = Path.Combine(directory.FullName, "OfficecliDemo");
            if (IsProjectConfigurationDirectory(childProjectDirectory))
            {
                return childProjectDirectory;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static bool IsProjectConfigurationDirectory(string directory)
    {
        return File.Exists(Path.Combine(directory, "OfficecliDemo.csproj"))
            && File.Exists(Path.Combine(directory, "appsettings.json"));
    }

    private static string ReadDocumentPath(string? configuredValue, string configurationDirectory)
    {
        var value = configuredValue;
        while (true)
        {
            value = ReadRequiredValue(
                value,
                "请输入 Word 招标书路径：",
                secret: false,
                configurationDirectory);
            var fullPath = ResolvePath(value, configurationDirectory);
            if (File.Exists(fullPath)
                && string.Equals(Path.GetExtension(fullPath), ".docx", StringComparison.OrdinalIgnoreCase))
            {
                return fullPath;
            }

            if (!CanPrompt())
            {
                throw new FileNotFoundException("配置的 .docx 招标书不存在。", fullPath);
            }

            Console.WriteLine($"文件不存在或不是 .docx：{fullPath}");
            value = null;
        }
    }

    private static Uri ReadEndpoint(string configuredValue, string configurationDirectory)
    {
        var value = configuredValue;
        while (true)
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var endpoint))
            {
                return endpoint;
            }

            if (!CanPrompt())
            {
                throw new InvalidOperationException("OpenAI.Endpoint 必须是绝对 URI。 ");
            }

            Console.WriteLine($"接口地址不是有效的绝对 URI：{value}");
            value = ReadRequiredValue(
                null,
                "请重新输入 OpenAI 接口地址：",
                secret: false,
                configurationDirectory);
        }
    }

    private static string ReadOutputDirectory(
        string? configuredValue,
        string defaultValue,
        string configurationDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredValue))
        {
            return ResolvePath(configuredValue, configurationDirectory);
        }

        if (!CanPrompt())
        {
            return defaultValue;
        }

        Console.Write($"输出目录（直接回车使用 {defaultValue}）：");
        var entered = Console.ReadLine()?.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(entered)
            ? defaultValue
            : ResolvePath(entered, configurationDirectory);
    }

    private static string ReadRequiredValue(
        string? configuredValue,
        string prompt,
        bool secret,
        string configurationDirectory)
    {
        var value = configuredValue?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (!CanPrompt())
        {
            throw new InvalidOperationException(
                $"缺少配置。请编辑 {Path.Combine(configurationDirectory, "appsettings.Local.json")}。 ");
        }

        while (string.IsNullOrWhiteSpace(value))
        {
            Console.Write(prompt);
            value = secret ? ReadSecret() : Console.ReadLine()?.Trim().Trim('"');
        }

        return value;
    }

    private static string ReadSecret()
    {
        var value = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return value.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                {
                    value.Length--;
                    Console.Write("\b \b");
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                value.Append(key.KeyChar);
                Console.Write('*');
            }
        }
    }

    private static string? ReadSetting(
        IConfiguration configuration,
        string configurationKey,
        string legacyEnvironmentVariable)
    {
        var environmentValue = Environment.GetEnvironmentVariable(legacyEnvironmentVariable)?.Trim();
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue;
        }

        var configuredValue = configuration[configurationKey]?.Trim();
        return string.IsNullOrWhiteSpace(configuredValue) ? null : configuredValue;
    }

    private static int ReadPositiveInteger(
        IConfiguration configuration,
        string configurationKey,
        string legacyEnvironmentVariable,
        int fallback)
    {
        var value = ReadSetting(configuration, configurationKey, legacyEnvironmentVariable);
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
    }

    private static bool ReadBoolean(
        IConfiguration configuration,
        string configurationKey,
        string legacyEnvironmentVariable,
        bool fallback)
    {
        var value = ReadSetting(configuration, configurationKey, legacyEnvironmentVariable);
        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static LogLevel ReadLogLevel(
        IConfiguration configuration,
        string configurationKey,
        LogLevel fallback)
    {
        var value = configuration[configurationKey];
        return Enum.TryParse<LogLevel>(value, ignoreCase: true, out var parsed)
            ? parsed
            : fallback;
    }

    internal static DemoRunMode ParseRunMode(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "combined" or "all" or "pipeline" => DemoRunMode.Combined,
            "outline" or "outlineonly" or "repair" => DemoRunMode.OutlineOnly,
            "extraction" or "extractiononly" or "extract" => DemoRunMode.ExtractionOnly,
            _ => throw new InvalidOperationException(
                "Demo.Mode 只支持 Combined、OutlineOnly 或 ExtractionOnly。 "),
        };
    }

    private static string ResolvePath(string value, string configurationDirectory)
    {
        var trimmed = value.Trim().Trim('"');
        return Path.IsPathFullyQualified(trimmed)
            ? Path.GetFullPath(trimmed)
            : Path.GetFullPath(trimmed, configurationDirectory);
    }

    private static bool CanPrompt()
    {
        return Environment.UserInteractive && !Console.IsInputRedirected;
    }
}
