using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace OfficecliDemo;

[Flags]
internal enum OfficeCliCommandPermissions
{
    None = 0,
    Help = 1 << 0,
    LoadWordSkill = 1 << 1,
    Stats = 1 << 2,
    Outline = 1 << 3,
    TextParagraphRange = 1 << 4,
    TextPageRange = 1 << 5,
    AnnotatedParagraphRange = 1 << 6,
    AnnotatedPageRange = 1 << 7,
    Get = 1 << 8,
    Query = 1 << 9,
    TextBodyIndexRange = 1 << 10,
    AnnotatedBodyIndexRange = 1 << 11,
    MetadataRead = Help | LoadWordSkill | Stats | Outline,
    ParagraphRead = Help | TextBodyIndexRange | AnnotatedBodyIndexRange | Get,
    OutlineRepairRead = MetadataRead | TextBodyIndexRange | AnnotatedBodyIndexRange | Get | Query,
    ExtractionFollowUp = Help | TextBodyIndexRange | AnnotatedBodyIndexRange | Get | Query,
    ExtractionRead = Help | LoadWordSkill | Stats | Outline
        | TextBodyIndexRange | AnnotatedBodyIndexRange | Get | Query,
    All = MetadataRead | TextParagraphRange | TextPageRange
        | AnnotatedParagraphRange | AnnotatedPageRange
        | TextBodyIndexRange | AnnotatedBodyIndexRange | Get | Query,
}

internal sealed class OfficeCliToolApproval
{
    private readonly HashSet<string> _officeCliMcpToolNames;
    private readonly string _sourceDocumentPath;
    private readonly int _maximumToolCalls;
    private readonly bool _showArguments;
    private readonly int _maximumLogTextLength;
    private readonly ILogger<OfficeCliToolApproval> _logger;
    private int _allowedCommandPermissions = (int)OfficeCliCommandPermissions.All;
    private int _wordSkillLoaded;
    private int _approvedToolCallCount;
    private int _requestedToolCallCount;

    public OfficeCliToolApproval(
        IEnumerable<string> officeCliMcpToolNames,
        string sourceDocumentPath,
        int maximumToolCalls,
        bool showArguments,
        int maximumLogTextLength,
        ILogger<OfficeCliToolApproval> logger)
    {
        _officeCliMcpToolNames = officeCliMcpToolNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _sourceDocumentPath = Path.GetFullPath(sourceDocumentPath);
        _maximumToolCalls = Math.Max(1, maximumToolCalls);
        _showArguments = showArguments;
        _maximumLogTextLength = maximumLogTextLength;
        _logger = logger;
    }

    public ValueTask<bool> ApproveAsync(
        FunctionInvocationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var requestNumber = Interlocked.Increment(ref _requestedToolCallCount);
        if (_showArguments)
        {
            _logger.LogInformation(
                "[Tool] 调用 #{RequestNumber}：{ToolName}；参数：{Arguments}",
                requestNumber,
                context.Function.Name,
                DemoLogFormatter.Serialize(context.Arguments, _maximumLogTextLength));
        }
        else
        {
            _logger.LogInformation(
                "[Tool] 调用 #{RequestNumber}：{ToolName}；参数打印已关闭",
                requestNumber,
                context.Function.Name);
        }

        var approved = false;
        string reason;
        if (_officeCliMcpToolNames.Contains(context.Function.Name))
        {
            approved = TryApproveOfficeCliCommand(context.Arguments, out reason);
        }
        else
        {
            reason = "工具不在允许列表中";
        }

        if (!approved)
        {
            _logger.LogWarning(
                "[Tool] 拒绝请求 #{RequestNumber}：{ToolName}。原因：{Reason}",
                requestNumber,
                context.Function.Name,
                reason);
            return new ValueTask<bool>(false);
        }

        var approvedNumber = Interlocked.Increment(ref _approvedToolCallCount);
        if (approvedNumber > _maximumToolCalls)
        {
            _logger.LogWarning(
                "[Tool] 拒绝请求 #{RequestNumber}：已超过最大工具调用数 {MaximumToolCalls}",
                requestNumber,
                _maximumToolCalls);
            return new ValueTask<bool>(false);
        }

        _logger.LogInformation(
            "[Tool] 批准请求 #{RequestNumber}（已批准 {ApprovedCount}/{MaximumToolCalls}）：{Reason}",
            requestNumber,
            approvedNumber,
            _maximumToolCalls,
            reason);
        return new ValueTask<bool>(true);
    }

    public void SetStagePermissions(OfficeCliCommandPermissions permissions)
    {
        Volatile.Write(ref _allowedCommandPermissions, (int)permissions);
        _logger.LogInformation(
            "[ToolPolicy] 当前智能体任务允许的 officecli 命令：{Permissions}",
            permissions);
    }

    private bool TryApproveOfficeCliCommand(AIFunctionArguments arguments, out string reason)
    {
        if (!arguments.TryGetValue("command", out var value))
        {
            reason = "officecli MCP 参数中缺少 command";
            return false;
        }

        var command = value switch
        {
            string text => text,
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            _ => value?.ToString(),
        };
        if (string.IsNullOrWhiteSpace(command))
        {
            reason = "officecli command 为空";
            return false;
        }

        return TryApproveOfficeCliCommand(command, out reason);
    }

    internal bool TryApproveOfficeCliCommand(string command, out string reason)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            reason = "officecli command 为空";
            return false;
        }

        var normalized = command.Trim();
        if (Regex.IsMatch(
                normalized,
                "(?:^|\\s)--json(?:\\s|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            reason = "Demo 已禁用 officecli --json，请使用普通文本输出";
            return false;
        }

        if (Regex.IsMatch(
                normalized,
                "(?:^|\\s)--para-id(?:\\s|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            reason = "Demo 禁止使用 officecli --para-id；必须保留位置 XPath /body/p[N] 和 Body.ChildElements Index";
            return false;
        }

        if (Regex.IsMatch(normalized, "^help(?:\\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            if (!IsAllowed(OfficeCliCommandPermissions.Help, out reason))
            {
                return false;
            }

            reason = "允许读取 officecli help";
            return true;
        }

        if (Regex.IsMatch(
                normalized,
                "^load_skill\\s+word\\s*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            if (!IsAllowed(OfficeCliCommandPermissions.LoadWordSkill, out reason))
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _wordSkillLoaded, 1, 0) != 0)
            {
                reason = "当前智能体已经加载过 word skill，请直接继续任务，不要重复加载";
                return false;
            }

            reason = "允许加载 officecli word skill";
            return true;
        }

        if (!normalized.Contains(_sourceDocumentPath, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"命令没有限定到当前工作文档。请原样使用绝对路径：\"{_sourceDocumentPath}\"";
            return false;
        }

        if (Regex.IsMatch(normalized, "^view(?:\\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return TryApproveViewCommand(normalized, out reason);
        }

        if (Regex.IsMatch(normalized, "^get(?:\\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            if (!IsAllowed(OfficeCliCommandPermissions.Get, out reason))
            {
                return false;
            }

            return TryApproveGetCommand(normalized, out reason);
        }

        if (Regex.IsMatch(normalized, "^query(?:\\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            if (!IsAllowed(OfficeCliCommandPermissions.Query, out reason))
            {
                return false;
            }

            return TryApproveQueryCommand(normalized, out reason);
        }

        reason = "命令不在允许组合中";
        return false;
    }

    private bool TryApproveViewCommand(string command, out string reason)
    {
        var viewMatch = Regex.Match(
            command,
            "^view\\s+(?:\"[^\"]+\"|'[^']+'|\\S+)\\s+(?<mode>\\S+)(?<arguments>.*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!viewMatch.Success)
        {
            reason = "view 命令格式无效";
            return false;
        }

        var mode = viewMatch.Groups["mode"].Value;
        var arguments = viewMatch.Groups["arguments"].Value.Trim();
        if (string.Equals(mode, "outline", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsAllowed(OfficeCliCommandPermissions.Outline, out reason))
            {
                return false;
            }

            var approved = arguments.Length == 0;
            reason = approved
                ? "允许 view outline"
                : "outline 不允许附加参数";
            return approved;
        }

        if (string.Equals(mode, "stats", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsAllowed(OfficeCliCommandPermissions.Stats, out reason))
            {
                return false;
            }

            var approved = string.Equals(arguments, "--page-count", StringComparison.OrdinalIgnoreCase);
            reason = approved
                ? "允许 view stats --page-count"
                : "stats 只允许 --page-count 组合";
            return approved;
        }

        var isText = string.Equals(mode, "text", StringComparison.OrdinalIgnoreCase);
        var isAnnotated = string.Equals(mode, "annotated", StringComparison.OrdinalIgnoreCase);
        if (!isText && !isAnnotated)
        {
            reason = "view 只允许 stats、outline、text、annotated";
            return false;
        }

        var pageMatch = Regex.Match(
            arguments,
            "^--page(?:=|\\s+)(?<value>[0-9,-]+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (pageMatch.Success)
        {
            var permission = isText
                ? OfficeCliCommandPermissions.TextPageRange
                : OfficeCliCommandPermissions.AnnotatedPageRange;
            if (!IsAllowed(permission, out reason))
            {
                return false;
            }

            if (!TryReadPageSelectionCount(arguments, out var selectedPageCount)
                || selectedPageCount is null)
            {
                reason = "--page 格式无效";
                return false;
            }

            var maximumPages = isText ? 20 : 3;
            var approved = selectedPageCount <= maximumPages;
            reason = approved
                ? $"允许 view {mode} --page，共 {selectedPageCount} 页"
                : $"view {mode} --page 超过单次 {maximumPages} 页限制";
            return approved;
        }

        var bodyIndexRangeMatch = Regex.Match(
            arguments,
            "^--startIndex(?:=|\\s+)(?<start>\\d+)\\s+--endIndex(?:=|\\s+)(?<end>\\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (bodyIndexRangeMatch.Success
            && int.TryParse(bodyIndexRangeMatch.Groups["start"].Value, out var startBodyIndex)
            && int.TryParse(bodyIndexRangeMatch.Groups["end"].Value, out var endBodyIndex)
            && endBodyIndex >= startBodyIndex)
        {
            var bodyIndexPermission = isText
                ? OfficeCliCommandPermissions.TextBodyIndexRange
                : OfficeCliCommandPermissions.AnnotatedBodyIndexRange;
            if (!IsAllowed(bodyIndexPermission, out reason))
            {
                return false;
            }

            var maximumBodyElements = isText ? 200 : 20;
            var bodyElementCount = endBodyIndex - startBodyIndex + 1;
            var approved = bodyElementCount <= maximumBodyElements;
            var suggestedEndIndex = Math.Min(
                int.MaxValue,
                (long)startBodyIndex + maximumBodyElements - 1);
            reason = approved
                ? $"允许 view {mode} --startIndex/--endIndex，共 {bodyElementCount} 个零基 Body.ChildElements"
                : $"view {mode} --startIndex/--endIndex 超过单次 {maximumBodyElements} 个 Body.ChildElements 限制；" +
                  $"闭区间请改为 --startIndex {startBodyIndex} --endIndex {suggestedEndIndex} 或更短范围";
            return approved;
        }

        var rangeMatch = Regex.Match(
            arguments,
            "^--start(?:=|\\s+)(?<start>\\d+)\\s+--end(?:=|\\s+)(?<end>\\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!rangeMatch.Success
            || !int.TryParse(rangeMatch.Groups["start"].Value, out var start)
            || !int.TryParse(rangeMatch.Groups["end"].Value, out var end)
            || start < 1
            || end < start)
        {
            reason = $"view {mode} 只允许 --page A-B、--startIndex I --endIndex J 或 --start S --end E 组合";
            return false;
        }

        var rangePermission = isText
            ? OfficeCliCommandPermissions.TextParagraphRange
            : OfficeCliCommandPermissions.AnnotatedParagraphRange;
        if (!IsAllowed(rangePermission, out reason))
        {
            return false;
        }

        var maximumSpan = isText ? 200 : 20;
        var spanApproved = end >= start && end - start + 1 <= maximumSpan;
        reason = spanApproved
            ? $"允许 view {mode} --start/--end，共 {end - start + 1} 个一基输出项"
            : $"view {mode} --start/--end 超过单次 {maximumSpan} 个输出项限制";
        return spanApproved;
    }

    private static bool TryApproveGetCommand(string command, out string reason)
    {
        var match = Regex.Match(
            command,
            "^get\\s+(?:\"[^\"]+\"|'[^']+'|\\S+)\\s+(?<path>\"[^\"]+\"|'[^']+'|\\S+)\\s+--depth(?:=|\\s+)0$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            reason = "get 只允许 <path> --depth 0 组合";
            return false;
        }

        var path = Unquote(match.Groups["path"].Value);
        if (path.Contains("@paraId", StringComparison.OrdinalIgnoreCase))
        {
            reason = "get 禁止使用 paraId，必须使用段落序号路径";
            return false;
        }

        reason = "允许 get <path> --depth 0";
        return true;
    }

    private static bool TryApproveQueryCommand(string command, out string reason)
    {
        var match = Regex.Match(
            command,
            "^query\\s+(?:\"[^\"]+\"|'[^']+'|\\S+)\\s+(?<selector>\"[^\"]+\"|'[^']+'|\\S+)\\s+--find(?:=|\\s+)(?<find>\"[^\"]+\"|'[^']+'|\\S+)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success
            || string.IsNullOrWhiteSpace(Unquote(match.Groups["selector"].Value))
            || string.IsNullOrWhiteSpace(Unquote(match.Groups["find"].Value)))
        {
            reason = "query 只允许 <selector> --find <find> 组合";
            return false;
        }

        reason = "允许 query <selector> --find <find>";
        return true;
    }

    private static string Unquote(string value)
    {
        return value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"')
                || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;
    }

    private bool IsAllowed(OfficeCliCommandPermissions permission, out string reason)
    {
        var allowed = (OfficeCliCommandPermissions)Volatile.Read(ref _allowedCommandPermissions);
        if ((allowed & permission) == permission)
        {
            reason = string.Empty;
            return true;
        }

        reason = $"当前智能体任务不允许 {permission}；本任务允许：{allowed}";
        return false;
    }

    private static bool TryReadPageSelectionCount(string command, out int? pageCount)
    {
        pageCount = null;
        var match = Regex.Match(
            command,
            "--page(?:=|\\s+)(?<value>[0-9,-]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return !Regex.IsMatch(
                command,
                "--page(?:=|\\s|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        var count = 0;
        foreach (var part in match.Groups["value"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var rangeParts = part.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (rangeParts.Length == 1
                && int.TryParse(rangeParts[0], out var page)
                && page > 0)
            {
                count++;
            }
            else if (rangeParts.Length == 2
                && int.TryParse(rangeParts[0], out var start)
                && int.TryParse(rangeParts[1], out var end)
                && start > 0
                && end >= start)
            {
                count += end - start + 1;
            }
            else
            {
                return false;
            }

            if (count > 100)
            {
                return false;
            }
        }

        pageCount = count;
        return count > 0;
    }

}
