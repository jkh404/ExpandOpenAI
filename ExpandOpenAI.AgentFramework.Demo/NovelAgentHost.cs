using System.Runtime.CompilerServices;
using ExpandOpenAI;
using ExpandOpenAI.AgentFramework;

namespace ExpandOpenAI.AgentFramework.Demo;

/// <summary>
/// 为本机 Web 写作台维护一个串行 Agent 运行时；配置变更时会安全地重建运行时。
/// </summary>
internal sealed class NovelAgentHost(IHttpClientFactory httpClientFactory) : IDisposable
{
    private readonly DemoSettingsStore _settingsStore = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OpenAICompatibleChatClient? _chatClient;
    private NovelWorkspace? _workspace;
    private AgentDemoApplication? _application;
    private bool _disposed;

    public async Task<NovelBootstrapResponse> GetBootstrapAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CreateBootstrapAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NovelBootstrapResponse> ConfigureAsync(
        NovelSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var configured = ValidateAndCreateSettings(request);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = _settingsStore.Load(out _);
            if (string.IsNullOrWhiteSpace(configured.ApiKey))
            {
                configured.ApiKey = existing.ApiKey;
            }

            if (string.IsNullOrWhiteSpace(configured.ApiKey)
                && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
            {
                throw new ArgumentException("API Key 不能为空；若使用环境变量，请设置 OPENAI_API_KEY。 ");
            }

            await _settingsStore.SaveAsync(configured, cancellationToken).ConfigureAwait(false);
            DisposeRuntime();
            return await CreateBootstrapAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<NovelSessionSummary> CreateSessionAsync(
        string? name,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var application = await EnsureApplicationAsync(cancellationToken).ConfigureAwait(false);
            return await application.CreateSessionAsync(name, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SelectSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await (await EnsureApplicationAsync(cancellationToken).ConfigureAwait(false))
                .SelectSessionAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkspaceExplorerResponse> GetWorkspaceFilesAsync(
        CancellationToken cancellationToken = default)
    {
        var workspace = await GetWorkspaceAsync(cancellationToken).ConfigureAwait(false);
        return new WorkspaceExplorerResponse(
            workspace.RootPath,
            await workspace.GetFileEntriesAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<string> ReadWorkspaceFileAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var workspace = await GetWorkspaceAsync(cancellationToken).ConfigureAwait(false);
        return await workspace.ReadTextFileAsync(relativePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NovelMemorySnippet>> GetSessionMemoriesAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await (await EnsureApplicationAsync(cancellationToken).ConfigureAwait(false))
                .GetSessionMemoriesAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<NovelMemorySnippet>> GetGlobalMemoriesAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await (await EnsureApplicationAsync(cancellationToken).ConfigureAwait(false))
                .GetGlobalMemoriesAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async IAsyncEnumerable<NovelStreamEvent> RunStreamAsync(
        string? sessionId,
        string message,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AgentDemoApplication? application = null;
        Exception? preparationError = null;
        try
        {
            application = await EnsureApplicationAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(sessionId)
                && !string.Equals(sessionId, application.ActiveSessionId, StringComparison.Ordinal))
            {
                await application.SelectSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _gate.Release();
            throw;
        }
        catch (Exception exception)
        {
            preparationError = exception;
        }

        if (preparationError is not null)
        {
            _gate.Release();
            yield return new NovelStreamEvent("error", $"{preparationError.GetType().Name}: {preparationError.Message}");
            yield break;
        }

        try
        {
            yield return new NovelStreamEvent("status", "已自动批准文件、HTTP 与全局记忆工具；正在执行任务…");
            await foreach (var update in application!.SendStreamAsync(message, cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeRuntime();
        _gate.Dispose();
    }

    private async Task<NovelBootstrapResponse> CreateBootstrapAsync(CancellationToken cancellationToken)
    {
        var settings = _settingsStore.Load(out var settingsError);
        if (settingsError is not null)
        {
            return NovelBootstrapResponse.Unconfigured(settingsError, ToPublicSettings(settings));
        }

        DemoSettings effectiveSettings;
        try
        {
            effectiveSettings = settings.WithEnvironmentOverrides();
        }
        catch (Exception exception)
        {
            return NovelBootstrapResponse.Unconfigured(exception.Message, ToPublicSettings(settings));
        }

        if (!effectiveSettings.IsComplete)
        {
            return NovelBootstrapResponse.Unconfigured("请先在设置中配置模型服务、模型名称和小说工作区。 ", ToPublicSettings(settings));
        }

        try
        {
            var application = await EnsureApplicationAsync(cancellationToken).ConfigureAwait(false);
            return new NovelBootstrapResponse(
                true,
                null,
                ToPublicSettings(settings),
                application.Sessions,
                application.ActiveSessionId,
                application.GetActiveConversation(),
                application.Workspace.RootPath,
                application.SessionStatePath,
                application.GlobalMemoryStatePath);
        }
        catch (Exception exception)
        {
            return NovelBootstrapResponse.Unconfigured(exception.Message, ToPublicSettings(settings));
        }
    }

    private async Task<AgentDemoApplication> EnsureApplicationAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_application is not null)
        {
            return _application;
        }

        var settings = _settingsStore.Load(out var error);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }

        var effective = settings.WithEnvironmentOverrides();
        if (!effective.IsComplete)
        {
            throw new InvalidOperationException("请先配置模型服务、API Key 和 Chat 模型。 ");
        }

        var workspacePath = ResolveNovelWorkspacePath(effective, _settingsStore.DefaultNovelWorkspacePath);
        var options = new OpenAICompatibleChatClientOptions
        {
            Endpoint = new Uri(effective.Endpoint, UriKind.Absolute),
            ApiKey = effective.ApiKey,
            ModelId = effective.ChatModel,
            RequestPath = effective.ChatRequestPath,
        };
        if (bool.TryParse(Environment.GetEnvironmentVariable("OPENAI_ENABLE_THINKING"), out var enableThinking))
        {
            options.RequestBody = new Dictionary<string, object?> { ["enable_thinking"] = enableThinking };
        }

        var chatClient = new OpenAICompatibleChatClient(options);
        var workspace = new NovelWorkspace(workspacePath, httpClientFactory.CreateClient("novel-http"));
        var application = new AgentDemoApplication(
            chatClient,
            options,
            workspace,
            effective.CompressionTokenThreshold,
            new InMemoryMemoryUnit());
        try
        {
            await application.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _chatClient = chatClient;
            _workspace = workspace;
            _application = application;
            return application;
        }
        catch
        {
            workspace.Dispose();
            chatClient.Dispose();
            throw;
        }
    }

    private async Task<NovelWorkspace> GetWorkspaceAsync(CancellationToken cancellationToken)
    {
        if (_workspace is not null)
        {
            return _workspace;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await EnsureApplicationAsync(cancellationToken).ConfigureAwait(false)).Workspace;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void DisposeRuntime()
    {
        _application = null;
        _workspace?.Dispose();
        _workspace = null;
        _chatClient?.Dispose();
        _chatClient = null;
    }

    private DemoSettings ValidateAndCreateSettings(NovelSettingsRequest request)
    {
        if (!Uri.TryCreate(request.Endpoint?.Trim(), UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("服务 URL 必须是有效的 http 或 https 绝对地址。 ");
        }

        if (string.IsNullOrWhiteSpace(request.ChatModel))
        {
            throw new ArgumentException("Chat 模型不能为空。 ");
        }

        if (string.IsNullOrWhiteSpace(request.ChatRequestPath))
        {
            throw new ArgumentException("Chat 请求路径不能为空。 ");
        }

        if (string.IsNullOrWhiteSpace(request.NovelWorkspacePath))
        {
            throw new ArgumentException("小说工作区文件夹不能为空。 ");
        }

        var compressionTokenThreshold = request.CompressionTokenThreshold
            ?? DemoSettings.DefaultCompressionTokenThreshold;
        if (compressionTokenThreshold is < DemoSettings.MinimumCompressionTokenThreshold
            or > DemoSettings.MaximumCompressionTokenThreshold)
        {
            throw new ArgumentException(
                $"上下文压缩阈值必须在 {DemoSettings.MinimumCompressionTokenThreshold:N0} 到 "
                + $"{DemoSettings.MaximumCompressionTokenThreshold:N0} tokens 之间。 ");
        }

        string workspacePath;
        try
        {
            workspacePath = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(request.NovelWorkspacePath.Trim().Trim('"')));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"小说工作区文件夹无效：{exception.Message}");
        }

        return new DemoSettings
        {
            Endpoint = endpoint.ToString().TrimEnd('/'),
            ApiKey = request.ApiKey?.Trim() ?? string.Empty,
            ChatModel = request.ChatModel.Trim(),
            ChatRequestPath = request.ChatRequestPath.Trim(),
            NovelWorkspacePath = workspacePath,
            CompressionTokenThreshold = compressionTokenThreshold,
        };
    }

    private static NovelPublicSettings ToPublicSettings(DemoSettings settings)
    {
        return new NovelPublicSettings(
            settings.Endpoint,
            settings.ChatModel,
            settings.ChatRequestPath,
            settings.NovelWorkspacePath,
            settings.CompressionTokenThreshold);
    }

    private static string ResolveNovelWorkspacePath(DemoSettings settings, string defaultWorkspacePath)
    {
        var configured = string.IsNullOrWhiteSpace(settings.NovelWorkspacePath)
            ? defaultWorkspacePath
            : settings.NovelWorkspacePath;
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"')));
    }
}

internal sealed record NovelPublicSettings(
    string Endpoint,
    string ChatModel,
    string ChatRequestPath,
    string? NovelWorkspacePath,
    int CompressionTokenThreshold);

internal sealed record NovelBootstrapResponse(
    bool IsConfigured,
    string? ConfigurationError,
    NovelPublicSettings Settings,
    IReadOnlyList<NovelSessionSummary> Sessions,
    string? ActiveSessionId,
    IReadOnlyList<NovelConversationItem> Conversation,
    string? WorkspacePath,
    string? SessionStatePath,
    string? GlobalMemoryStatePath)
{
    public static NovelBootstrapResponse Unconfigured(string error, NovelPublicSettings settings)
    {
        return new NovelBootstrapResponse(
            false,
            error,
            settings,
            [],
            null,
            [],
            null,
            null,
            null);
    }
}
