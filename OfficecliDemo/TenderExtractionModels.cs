using System.Text.Json;
using System.Text.Json.Serialization;

namespace OfficecliDemo;

internal sealed class TenderExtractionResult
{
    [JsonPropertyName("商务标")]
    public TenderSection Business { get; init; } = new();

    [JsonPropertyName("技术标")]
    public TenderSection Technical { get; init; } = new();

    public static bool TryParse(string? text, out TenderExtractionResult? result, out string? error)
    {
        result = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "模型没有返回内容。";
            return false;
        }

        var candidate = ExtractJsonObject(text);
        if (candidate is null)
        {
            error = "模型输出中没有完整的 JSON Object。";
            return false;
        }

        try
        {
            result = JsonSerializer.Deserialize<TenderExtractionResult>(candidate, JsonOptions);
            if (result is null)
            {
                error = "模型输出反序列化后为空。";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string? ExtractJsonObject(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLineBreak = trimmed.IndexOf('\n');
            if (firstLineBreak >= 0)
            {
                trimmed = trimmed[(firstLineBreak + 1)..];
            }

            if (trimmed.EndsWith("```", StringComparison.Ordinal))
            {
                trimmed = trimmed[..^3];
            }
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end >= start ? trimmed[start..(end + 1)] : null;
    }
}

internal sealed class TenderSection
{
    [JsonPropertyName("found")]
    public bool Found { get; init; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }

    [JsonPropertyName("segments")]
    public List<TenderSegment> Segments { get; init; } = [];

    [JsonPropertyName("evidence")]
    public List<TenderEvidence> Evidence { get; init; } = [];
}

internal sealed class TenderSegment
{
    [JsonPropertyName("startDataPath")]
    public string? StartDataPath { get; init; }

    [JsonPropertyName("endDataPath")]
    public string? EndDataPath { get; init; }

    [JsonPropertyName("startPage")]
    public int? StartPage { get; init; }

    [JsonPropertyName("endPage")]
    public int? EndPage { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

internal sealed class TenderEvidence
{
    [JsonPropertyName("page")]
    public int? Page { get; init; }

    [JsonPropertyName("dataPath")]
    public string? DataPath { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}
