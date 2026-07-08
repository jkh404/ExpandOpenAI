using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ExpandOpenAI.AgentBase;

/// <summary>
/// 渲染系统提示模板中的占位符。
/// </summary>
public static class SystemPromptTemplateEngine
{
    private static readonly Regex PlaceholderRegex = new Regex(
        @"\{\{\s*(?<path>[^{}]+?)\s*\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 使用变量字典渲染模板。未找到的占位符按 null 处理，并替换为空字符串。
    /// </summary>
    public static string Render(string? template, IReadOnlyDictionary<string, JsonNode?>? values)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        return PlaceholderRegex.Replace(template!, match =>
        {
            var path = match.Groups["path"].Value.Trim();
            return TryResolveValue(path, values, out var value)
                ? FormatValue(value)
                : string.Empty;
        });
    }

    /// <summary>
    /// 使用 <see cref="AgentOptions.SystemPromptTemplate"/> 和 <see cref="AgentOptions.SystemPromptTemplateDic"/> 渲染系统提示。
    /// </summary>
    public static string Render(AgentOptions? options)
    {
        return options is null
            ? string.Empty
            : Render(options.SystemPromptTemplate, options.SystemPromptTemplateDic);
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

        var parts = path.Split('.');
        if (parts.Length == 0 || !values.TryGetValue(parts[0], out value))
        {
            value = null;
            return false;
        }

        for (var i = 1; i < parts.Length; i++)
        {
            if (value is not JsonObject jsonObject || !jsonObject.TryGetPropertyValue(parts[i], out value))
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
