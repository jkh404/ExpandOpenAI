using System.Text.Json;
using System.Globalization;
using System.Text.Json.Serialization;

namespace OfficecliDemo;

internal sealed class OutlineRepairPlan
{
    public List<OutlineRepairItem> Items { get; init; } = [];

    public static bool TryParse(string? text, out OutlineRepairPlan? plan, out string? error)
    {
        plan = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "模型没有返回大纲内容。";
            return false;
        }

        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end < start)
        {
            error = "模型输出中没有完整 JSON Array。";
            return false;
        }

        try
        {
            var wireItems = JsonSerializer.Deserialize<List<OutlineRepairWireItem>>(
                text[start..(end + 1)],
                TenderExtractionResult.JsonOptions);
            if (wireItems is null)
            {
                error = "大纲 JSON 反序列化后为空。";
                return false;
            }

            var items = new List<OutlineRepairItem>(wireItems.Count);
            for (var index = 0; index < wireItems.Count; index++)
            {
                var wireItem = wireItems[index];
                if (!int.TryParse(
                        wireItem.Index,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var bodyIndex)
                    || bodyIndex < 0)
                {
                    error = $"items[{index}].index 必须是 OfficeCLI 输出的零基 Body.ChildElements Index 字符串。";
                    return false;
                }

                items.Add(new OutlineRepairItem
                {
                    Title = wireItem.Title,
                    Index = bodyIndex.ToString(CultureInfo.InvariantCulture),
                    Level = wireItem.Level,
                });
            }

            plan = new OutlineRepairPlan { Items = items };
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

internal sealed class OutlineRepairWireItem
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("index")]
    public string? Index { get; init; }

    [JsonPropertyName("level")]
    public int Level { get; init; }
}

internal sealed class OutlineRepairItem
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("index")]
    public string? Index { get; init; }

    [JsonPropertyName("level")]
    public int Level { get; init; }
}
