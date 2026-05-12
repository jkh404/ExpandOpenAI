using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI;

/// <summary>
/// 允许内容对象自己定义 OpenAI-compatible 请求片段的序列化方式。
/// </summary>
public abstract class OpenAIRequestContent : AIContent
{
    public abstract JsonObject SerializeToOpenAIRequestContentPart(JsonSerializerOptions serializerOptions);
}
