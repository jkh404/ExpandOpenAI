using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.Providers.DashScope;

/// <summary>
/// DashScope 兼容接口使用的音频输入内容。
/// </summary>
public sealed class DashScopeAudioContent : OpenAIRequestContent
{
    public DashScopeAudioContent(DataContent audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        Audio = audio;
    }

    public DataContent Audio { get; }

    public override JsonObject SerializeToOpenAIRequestContentPart(JsonSerializerOptions serializerOptions)
    {
        if (!Audio.HasTopLevelMediaType("audio"))
        {
            throw new InvalidOperationException(
                $"DashScopeAudioContent 只接受音频 DataContent，当前类型为 {Audio.MediaType}。");
        }

        return new JsonObject
        {
            ["type"] = "input_audio",
            ["input_audio"] = new JsonObject
            {
                ["data"] = Audio.Uri,
            },
        };
    }
}
