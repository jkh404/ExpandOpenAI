using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ExpandOpenAI.AgentFramework;

/// <summary>
/// 模板变量缺失时的处理方式。
/// </summary>
public enum MissingTemplateValueBehavior
{
    /// <summary>替换为空字符串。</summary>
    Empty,

    /// <summary>保留原占位符。</summary>
    PreservePlaceholder,

    /// <summary>抛出 <see cref="KeyNotFoundException"/>。</summary>
    Throw,
}

/// <summary>
/// 渲染系统提示模板中的占位符。
/// </summary>
public static class SystemPromptTemplateEngine
{
    private static readonly Regex PlaceholderRegex = new Regex(
        @"\{\{\s*(?<path>[^{}]+?)\s*\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 使用变量字典渲染模板。
    /// </summary>
    public static string Render(
        string? template,
        IReadOnlyDictionary<string, JsonNode?>? values,
        MissingTemplateValueBehavior missingValueBehavior = MissingTemplateValueBehavior.Empty)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        return PlaceholderRegex.Replace(template, match =>
        {
            var path = match.Groups["path"].Value.Trim();
            if (TryResolveValue(path, values, out var value))
            {
                return FormatValue(value);
            }

            return missingValueBehavior switch
            {
                MissingTemplateValueBehavior.Empty => string.Empty,
                MissingTemplateValueBehavior.PreservePlaceholder => match.Value,
                MissingTemplateValueBehavior.Throw => throw new KeyNotFoundException($"系统提示模板变量不存在：{path}"),
                _ => throw new ArgumentOutOfRangeException(nameof(missingValueBehavior)),
            };
        });
    }

    internal static string Render(AgentOptions options)
    {
        return Render(
            options.SystemPromptTemplate,
            options.SystemPromptTemplateValues,
            options.MissingTemplateValueBehavior);
    }

    private static bool TryResolveValue(
        string path,
        IReadOnlyDictionary<string, JsonNode?>? values,
        out JsonNode? value)
    {
        if (values is null || values.Count == 0)
        {
            value = null;
            return false;
        }

        if (values.TryGetValue(path, out value))
        {
            return true;
        }

        var parts = path.Split('.').Select(static part => part.Trim()).ToArray();
        if (parts.Length == 0 || parts.Any(static part => part.Length == 0) || !values.TryGetValue(parts[0], out value))
        {
            value = null;
            return false;
        }

        for (var index = 1; index < parts.Length; index++)
        {
            if (value is not JsonObject jsonObject || !jsonObject.TryGetPropertyValue(parts[index], out value))
            {
                value = null;
                return false;
            }
        }

        return true;
    }

    private static string FormatValue(JsonNode? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<string>(out var stringValue))
            {
                return stringValue ?? string.Empty;
            }

            if (jsonValue.TryGetValue<bool>(out var boolValue))
            {
                return boolValue ? "true" : "false";
            }

            if (jsonValue.TryGetValue<int>(out var intValue))
            {
                return intValue.ToString(CultureInfo.InvariantCulture);
            }

            if (jsonValue.TryGetValue<long>(out var longValue))
            {
                return longValue.ToString(CultureInfo.InvariantCulture);
            }

            if (jsonValue.TryGetValue<decimal>(out var decimalValue))
            {
                return decimalValue.ToString(CultureInfo.InvariantCulture);
            }

            if (jsonValue.TryGetValue<double>(out var doubleValue))
            {
                return doubleValue.ToString(CultureInfo.InvariantCulture);
            }
        }

        return value.ToJsonString();
    }
}
