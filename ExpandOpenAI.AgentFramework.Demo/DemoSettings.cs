using System.Text.Json;

namespace ExpandOpenAI.AgentFramework.Demo;

internal sealed class DemoSettings
{
    public const int DefaultCompressionTokenThreshold = 100_000;
    public const int MinimumCompressionTokenThreshold = 8_000;
    public const int MaximumCompressionTokenThreshold = 2_000_000;

    public string Endpoint { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string ChatModel { get; set; } = string.Empty;

    public string ChatRequestPath { get; set; } = "chat/completions";

    public string? NovelWorkspacePath { get; set; }

    public int CompressionTokenThreshold { get; set; } = DefaultCompressionTokenThreshold;

    public bool IsComplete =>
        Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint)
        && endpoint.Scheme is "http" or "https"
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(ChatModel);

    public DemoSettings WithEnvironmentOverrides()
    {
        return new DemoSettings
        {
            Endpoint = Environment.GetEnvironmentVariable("OPENAI_ENDPOINT") ?? Endpoint,
            ApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? ApiKey,
            ChatModel = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? ChatModel,
            ChatRequestPath = Environment.GetEnvironmentVariable("OPENAI_REQUEST_PATH") ?? ChatRequestPath,
            NovelWorkspacePath = Environment.GetEnvironmentVariable("EXPANDOPENAI_NOVEL_WORKSPACE")
                ?? NovelWorkspacePath,
            CompressionTokenThreshold = CompressionTokenThreshold,
        };
    }
}

internal sealed class DemoSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public DemoSettingsStore()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = AppContext.BaseDirectory;
        }

        DirectoryPath = Path.Combine(localData, "ExpandOpenAI", "AgentFramework.Demo");
        SettingsPath = Path.Combine(DirectoryPath, "settings.json");
        DefaultNovelWorkspacePath = Path.Combine(DirectoryPath, "NovelWorkspace");
    }

    public string DirectoryPath { get; }

    public string SettingsPath { get; }

    public string DefaultNovelWorkspacePath { get; }

    public DemoSettings Load(out string? errorMessage)
    {
        errorMessage = null;
        if (!File.Exists(SettingsPath))
        {
            return new DemoSettings();
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<DemoSettings>(json, SerializerOptions)
                ?? new DemoSettings();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            errorMessage = $"读取本地配置失败：{exception.Message}";
            return new DemoSettings();
        }
    }

    public async Task SaveAsync(DemoSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(DirectoryPath);

        var temporaryPath = SettingsPath + ".tmp";
        var json = JsonSerializer.Serialize(settings, SerializerOptions);
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }
}
