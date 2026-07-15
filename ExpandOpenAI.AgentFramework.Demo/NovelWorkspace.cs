using System.ComponentModel;
using System.Net;
using System.Text;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.AgentFramework.Demo;

/// <summary>
/// 小说撰写智能体可操作的受限工作区，以及其只读的公开 HTTP 查询能力。
/// </summary>
internal sealed class NovelWorkspace : IDisposable
{
    private const int MaximumFileCharacters = 120_000;
    private const int MaximumHttpResponseCharacters = 80_000;
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt",
        ".md",
    };
    private static readonly HashSet<string> ToolNames = new(StringComparer.Ordinal)
    {
        "list_workspace_files",
        "read_workspace_file",
        "write_workspace_file",
        "fetch_http",
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _rootPathWithSeparator;

    public NovelWorkspace(string rootPath, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        RootPath = Path.GetFullPath(rootPath.Trim().Trim('"'));
        Directory.CreateDirectory(RootPath);
        _rootPathWithSeparator = RootPath.EndsWith(Path.DirectorySeparatorChar)
            ? RootPath
            : RootPath + Path.DirectorySeparatorChar;

        _httpClient = httpClient ?? CreateHttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public string RootPath { get; }

    public static bool IsToolName(string name)
    {
        return ToolNames.Contains(name);
    }

    public IReadOnlyList<AITool> CreateTools()
    {
        return
        [
            AIFunctionFactory.Create(
                (Func<string, CancellationToken, Task<string>>)ListFilesAsync,
                "list_workspace_files",
                "列出小说工作区内指定目录的 .txt 和 .md 文件。目录必须相对于工作区；传入 . 表示根目录。"),
            AIFunctionFactory.Create(
                (Func<string, CancellationToken, Task<string>>)ReadTextFileAsync,
                "read_workspace_file",
                "读取小说工作区内一个 .txt 或 .md 文件。路径必须相对于工作区。"),
            AIFunctionFactory.Create(
                (Func<string, string, bool, CancellationToken, Task<string>>)WriteTextFileAsync,
                "write_workspace_file",
                "在小说工作区创建或写入 .txt / .md 文件。新文件会直接创建；已有文件仅在 overwrite 为 true 时覆盖，覆盖前必须先读取原文件。"),
            AIFunctionFactory.Create(
                (Func<string, CancellationToken, Task<string>>)FetchHttpAsync,
                "fetch_http",
                "通过 HTTP GET 读取公开网页或公开 API。只可使用 http 或 https URL；不支持认证、请求头或私有网络地址。"),
        ];
    }

    public Task<string> ListFilesAsync(
        [Description("相对于小说工作区的目录；使用 . 列出根目录。")] string relativeDirectory,
        CancellationToken cancellationToken = default)
    {
        var directory = ResolveDirectory(relativeDirectory);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"工作区目录不存在：{ToRelativePath(directory)}");
        }

        var entries = EnumerateFileEntries(directory, cancellationToken);
        if (entries.Count == 0)
        {
            return Task.FromResult("工作区中还没有 .txt 或 .md 文件。");
        }

        var output = new StringBuilder($"工作区文件（{entries.Count}）：\n");
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Append("- ")
                .Append(entry.Path)
                .Append(" · ")
                .Append(entry.Size)
                .Append(" bytes · ")
                .Append(entry.LastModifiedAt.ToString("O"))
                .AppendLine();
        }

        return Task.FromResult(output.ToString().TrimEnd());
    }

    public Task<IReadOnlyList<NovelWorkspaceFileEntry>> GetFileEntriesAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<NovelWorkspaceFileEntry>>(
            EnumerateFileEntries(RootPath, cancellationToken));
    }

    public async Task<string> ReadTextFileAsync(
        [Description("相对于小说工作区的 .txt 或 .md 文件路径。")] string relativePath,
        CancellationToken cancellationToken = default)
    {
        var path = ResolveTextFile(relativePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"工作区文件不存在：{ToRelativePath(path)}", path);
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16_384,
            useAsync: true);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var (content, truncated) = await ReadAtMostAsync(
            reader,
            MaximumFileCharacters,
            cancellationToken).ConfigureAwait(false);
        var notice = truncated
            ? $"\n\n[文件内容已截断为前 {MaximumFileCharacters} 个字符]"
            : string.Empty;
        return $"文件：{ToRelativePath(path)}\n\n{content}{notice}";
    }

    public async Task<string> WriteTextFileAsync(
        [Description("相对于小说工作区的 .txt 或 .md 文件路径。")] string relativePath,
        [Description("要写入的完整 UTF-8 文本。")]
        string content,
        [Description("仅当覆盖已存在的文件时传 true；覆盖前必须先调用 read_workspace_file。")]
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var path = ResolveTextFile(relativePath);
        var alreadyExists = File.Exists(path);
        if (alreadyExists && !overwrite)
        {
            throw new IOException(
                $"文件已存在：{ToRelativePath(path)}。请先读取它，确认后将 overwrite 设为 true 才能覆盖。");
        }

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("无法解析目标文件所在目录。");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            path,
            content,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);

        var action = alreadyExists ? "已覆盖" : "已创建";
        return $"{action}：{ToRelativePath(path)}（{content.Length} 个字符）";
    }

    public async Task<string> FetchHttpAsync(
        [Description("要读取的公开 http 或 https URL。")] string url,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var current))
        {
            throw new ArgumentException("URL 必须是绝对地址。", nameof(url));
        }

        for (var redirectCount = 0; redirectCount <= 5; redirectCount++)
        {
            await ValidatePublicHttpUriAsync(current, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                if (response.Headers.Location is null)
                {
                    throw new HttpRequestException($"服务器返回 {(int)response.StatusCode}，但没有重定向地址。");
                }

                current = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(current, response.Headers.Location);
                continue;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var (body, truncated) = await ReadAtMostAsync(
                reader,
                MaximumHttpResponseCharacters,
                cancellationToken).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "(未提供)";
            var notice = truncated
                ? $"\n\n[响应已截断为前 {MaximumHttpResponseCharacters} 个字符]"
                : string.Empty;
            return $"URL：{current}\n状态：{(int)response.StatusCode} {response.ReasonPhrase}\nContent-Type：{contentType}\n\n{body}{notice}";
        }

        throw new HttpRequestException("重定向次数超过 5 次。");
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ExpandOpenAI-NovelWriterDemo/1.0");
        return client;
    }

    private string ResolveDirectory(string relativeDirectory)
    {
        if (string.IsNullOrWhiteSpace(relativeDirectory) || relativeDirectory.Trim() == ".")
        {
            return RootPath;
        }

        var path = ResolveWorkspacePath(relativeDirectory);
        EnsureNoReparsePoints(path);
        return path;
    }

    private List<NovelWorkspaceFileEntry> EnumerateFileEntries(
        string directory,
        CancellationToken cancellationToken)
    {
        var entries = new List<NovelWorkspaceFileEntry>();
        foreach (var path in Directory.EnumerateFiles(directory, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     AttributesToSkip = FileAttributes.ReparsePoint,
                 }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SupportedExtensions.Contains(Path.GetExtension(path)))
            {
                continue;
            }

            var info = new FileInfo(path);
            entries.Add(new NovelWorkspaceFileEntry(
                ToRelativePath(path),
                info.Name,
                info.Extension.TrimStart('.').ToLowerInvariant(),
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc)));
        }

        entries.Sort(static (left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path));
        return entries;
    }

    private string ResolveTextFile(string relativePath)
    {
        var path = ResolveWorkspacePath(relativePath);
        if (!SupportedExtensions.Contains(Path.GetExtension(path)))
        {
            throw new NotSupportedException("小说工作区只允许 .txt 和 .md 纯文本文件。 ");
        }

        EnsureNoReparsePoints(path);
        return path;
    }

    private string ResolveWorkspacePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        relativePath = relativePath.Trim().Trim('"');
        if (Path.IsPathRooted(relativePath))
        {
            throw new UnauthorizedAccessException("必须使用相对于小说工作区的路径。 ");
        }

        var fullPath = Path.GetFullPath(Path.Combine(RootPath, relativePath));
        if (string.Equals(fullPath, RootPath, PathComparison)
            || !fullPath.StartsWith(_rootPathWithSeparator, PathComparison))
        {
            throw new UnauthorizedAccessException("路径不能离开小说工作区。 ");
        }

        return fullPath;
    }

    private string ToRelativePath(string path)
    {
        return Path.GetRelativePath(RootPath, path).Replace(Path.DirectorySeparatorChar, '/');
    }

    private void EnsureNoReparsePoints(string path)
    {
        var relativePath = Path.GetRelativePath(RootPath, path);
        if (relativePath is "." or "")
        {
            return;
        }

        var current = RootPath;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                return;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("工作区不允许通过符号链接或重解析点访问文件。 ");
            }
        }
    }

    private static async Task<(string Content, bool Truncated)> ReadAtMostAsync(
        TextReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder(Math.Min(maximumCharacters, 16_384));
        var buffer = new char[4_096];
        while (output.Length < maximumCharacters)
        {
            var requested = Math.Min(buffer.Length, maximumCharacters - output.Length);
            var read = await reader.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return (output.ToString(), false);
            }

            output.Append(buffer, 0, read);
        }

        return (
            output.ToString(),
            await reader.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0);
    }

    private static async Task ValidatePublicHttpUriAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme is not ("http" or "https"))
        {
            throw new NotSupportedException("HTTP 工具只支持 http 和 https URL。 ");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new UnauthorizedAccessException("HTTP URL 不能包含用户名、密码或其他凭据。 ");
        }

        if (uri.IsLoopback || IPAddress.TryParse(uri.Host, out var literalAddress) && IsPrivateAddress(literalAddress))
        {
            throw new UnauthorizedAccessException("HTTP 工具不能访问回环或私有网络地址。 ");
        }

        var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(IsPrivateAddress))
        {
            throw new UnauthorizedAccessException("HTTP 工具只能访问解析到公开 IP 地址的主机。 ");
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.Moved
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 0
                || bytes[0] == 10
                || bytes[0] == 127
                || bytes[0] >= 224
                || bytes[0] == 169 && bytes[1] == 254
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80
                || (bytes[0] & 0xfe) == 0xfc;
        }

        return true;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}

internal sealed record NovelWorkspaceFileEntry(
    string Path,
    string Name,
    string Extension,
    long Size,
    DateTimeOffset LastModifiedAt);
