using System.Text.Json;
using System.Text.Encodings.Web;

namespace OfficecliDemo;

internal static class DemoLogFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        MaxDepth = 16,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(object? value, int maximumLength)
    {
        string text;
        try
        {
            text = value switch
            {
                null => "null",
                string stringValue => stringValue,
                JsonElement element => element.GetRawText(),
                _ => JsonSerializer.Serialize(value, value.GetType(), JsonOptions),
            };
        }
        catch
        {
            text = value?.ToString() ?? "null";
        }

        return Limit(text, maximumLength);
    }

    public static string Limit(string? text, int maximumLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "<empty>";
        }

        if (text.Length <= maximumLength)
        {
            return text;
        }

        return text[..maximumLength] + $"\n... <日志截断，原始字符数 {text.Length}>";
    }
}
