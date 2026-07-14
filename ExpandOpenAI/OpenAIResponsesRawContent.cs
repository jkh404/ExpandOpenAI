using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI;

/// <summary>
/// 原样保留 Responses API 尚未映射为 Microsoft.Extensions.AI 类型的 Item 或内容块。
/// </summary>
public sealed class OpenAIResponsesRawContent : AIContent
{
    public OpenAIResponsesRawContent(JsonObject value, bool isTopLevelItem = true)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        Value = CloneObject(value);
        IsTopLevelItem = isTopLevelItem;
        RawRepresentation = Value;
    }

    public OpenAIResponsesRawContent(JsonElement value, bool isTopLevelItem = true)
        : this(
            JsonNode.Parse(value.GetRawText()) as JsonObject
                ?? throw new ArgumentException("Responses 原始内容必须是 JSON 对象。", nameof(value)),
            isTopLevelItem)
    {
    }

    public JsonObject Value { get; }

    public bool IsTopLevelItem { get; }

    internal JsonObject CloneValue() => CloneObject(Value);

    private static JsonObject CloneObject(JsonObject value)
    {
        return JsonNode.Parse(value.ToJsonString()) as JsonObject
            ?? throw new InvalidOperationException("无法复制 Responses JSON 对象。");
    }
}
