using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI;

/// <summary>
/// 使用本地 JSON 解析和可选模型调用修复 JSON 文本。
/// </summary>
public sealed class JsonRepairer
{
    private readonly IChatClient _chatClient;

    public JsonRepairer(IChatClient chatClient)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
    }

    /// <summary>
    /// 规范化 JSON；本地无法解析时调用模型修复，并再次验证模型输出。
    /// </summary>
    public async Task<string> RepairAsync(
        string json,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (json is null)
        {
            throw new ArgumentNullException(nameof(json));
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return json;
        }

        if (TryNormalizeJson(json, out var normalized))
        {
            return normalized;
        }

        var response = await _chatClient.GetResponseAsync(
        [
            new ChatMessage(
                ChatRole.System,
                "你负责修复无效 JSON。只输出修复后的 JSON，不要输出 Markdown 代码块或解释。"),
            new ChatMessage(ChatRole.User, json),
        ],
        chatOptions?.Clone(),
        cancellationToken).ConfigureAwait(false);

        var candidate = ExtractJsonText(response.Text);
        if (candidate is not null && TryNormalizeJson(candidate, out normalized))
        {
            return normalized;
        }

        throw new JsonException("模型返回的内容仍不是有效 JSON。");
    }

    /// <summary>
    /// 从纯 JSON、Markdown 代码块或包含说明文字的文本中提取第一个有效 JSON 对象或数组。
    /// </summary>
    public static string? ExtractJsonText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text!.Trim();
        var fenced = ExtractFencedContent(trimmed);
        if (fenced is not null && TryNormalizeJson(fenced, out _))
        {
            return fenced;
        }

        if (TryNormalizeJson(trimmed, out _))
        {
            return trimmed;
        }

        for (var start = 0; start < trimmed.Length; start++)
        {
            if (trimmed[start] is not ('{' or '['))
            {
                continue;
            }

            var end = FindBalancedJsonEnd(trimmed, start);
            if (end < 0)
            {
                continue;
            }

            var candidate = trimmed.Substring(start, end - start + 1);
            if (TryNormalizeJson(candidate, out _))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? ExtractFencedContent(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return null;
        }

        var contentStart = text.IndexOf('\n');
        var contentEnd = text.LastIndexOf("```", StringComparison.Ordinal);
        if (contentStart < 0 || contentEnd <= contentStart)
        {
            return null;
        }

        return text.Substring(contentStart + 1, contentEnd - contentStart - 1).Trim();
    }

    private static int FindBalancedJsonEnd(string text, int start)
    {
        var stack = new Stack<char>();
        var insideString = false;
        var escaped = false;

        for (var index = start; index < text.Length; index++)
        {
            var current = text[index];
            if (insideString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == '"')
                {
                    insideString = false;
                }

                continue;
            }

            if (current == '"')
            {
                insideString = true;
                continue;
            }

            if (current is '{' or '[')
            {
                stack.Push(current);
                continue;
            }

            if (current is not ('}' or ']'))
            {
                continue;
            }

            if (stack.Count == 0)
            {
                return -1;
            }

            var opening = stack.Pop();
            if ((opening == '{' && current != '}') || (opening == '[' && current != ']'))
            {
                return -1;
            }

            if (stack.Count == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TryNormalizeJson(string json, out string normalized)
    {
        try
        {
            var node = JsonNode.Parse(
                json,
                new JsonNodeOptions
                {
                    PropertyNameCaseInsensitive = true,
                },
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                    MaxDepth = 64,
                });
            normalized = node?.ToJsonString() ?? "null";
            return true;
        }
        catch (JsonException)
        {
            normalized = string.Empty;
            return false;
        }
    }
}
