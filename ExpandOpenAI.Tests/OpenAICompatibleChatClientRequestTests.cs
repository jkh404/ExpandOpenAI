using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.Tests;

/// <summary>
/// 覆盖 Chat Completions 请求构造器的多模态与额外属性序列化行为。
/// </summary>
public sealed class OpenAICompatibleChatClientRequestTests
{
    [Fact]
    public async Task AudioDataContent_SerializesBase64WithFormat()
    {
        var audio = new byte[] { 1, 2, 3, 4 };

        var part = await CaptureFirstContentPartAsync(
            new ChatMessage(ChatRole.User, [new DataContent(audio, "audio/mpeg")]));

        Assert.Equal("input_audio", part.GetProperty("type").GetString());
        var inputAudio = part.GetProperty("input_audio");
        Assert.Equal(Convert.ToBase64String(audio), inputAudio.GetProperty("data").GetString());
        Assert.Equal("mp3", inputAudio.GetProperty("format").GetString());
        Assert.False(inputAudio.TryGetProperty("url", out _));
    }

    [Theory]
    [InlineData("audio/mpeg", "mp3")]
    [InlineData("audio/mp3", "mp3")]
    [InlineData("audio/wav", "wav")]
    [InlineData("audio/x-wav", "wav")]
    [InlineData("audio/ogg", "ogg")]
    [InlineData("audio/wav;codecs=1", "wav")]
    public async Task AudioDataContent_MapsMediaTypeToFormat(string mediaType, string expectedFormat)
    {
        var part = await CaptureFirstContentPartAsync(
            new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 9 }, mediaType)]));

        Assert.Equal(
            expectedFormat,
            part.GetProperty("input_audio").GetProperty("format").GetString());
    }

    [Fact]
    public async Task AudioUriContent_DataUri_IsDecodedToBase64WithFormat()
    {
        var audio = new byte[] { 10, 20, 30 };
        var dataUri = new Uri($"data:audio/mpeg;base64,{Convert.ToBase64String(audio)}");

        var part = await CaptureFirstContentPartAsync(
            new ChatMessage(ChatRole.User, [new UriContent(dataUri, "audio/mpeg")]));

        var inputAudio = part.GetProperty("input_audio");
        Assert.Equal(Convert.ToBase64String(audio), inputAudio.GetProperty("data").GetString());
        Assert.Equal("mp3", inputAudio.GetProperty("format").GetString());
        Assert.DoesNotContain("data:", inputAudio.GetProperty("data").GetString());
    }

    [Fact]
    public async Task AudioUriContent_RemoteUrl_KeepsUrlShapeAndAddsFormat()
    {
        var part = await CaptureFirstContentPartAsync(
            new ChatMessage(ChatRole.User, [new UriContent("https://example.test/sample.mp3", "audio/mpeg")]));

        var inputAudio = part.GetProperty("input_audio");
        Assert.Equal("https://example.test/sample.mp3", inputAudio.GetProperty("url").GetString());
        Assert.Equal("mp3", inputAudio.GetProperty("format").GetString());
    }

    [Fact]
    public async Task ImageContent_DetailAdditionalProperty_IsPlacedInsideImageUrl()
    {
        var image = new UriContent("https://example.test/cat.png", "image/png")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["detail"] = "high",
            },
        };

        var part = await CaptureFirstContentPartAsync(
            new ChatMessage(ChatRole.User, [image]));

        Assert.Equal("image_url", part.GetProperty("type").GetString());
        var imageUrl = part.GetProperty("image_url");
        Assert.Equal("https://example.test/cat.png", imageUrl.GetProperty("url").GetString());
        Assert.Equal("high", imageUrl.GetProperty("detail").GetString());
        Assert.False(part.TryGetProperty("detail", out _));
    }

    [Fact]
    public async Task TextContent_AdditionalProperties_AreMergedAndKeepPartsShape()
    {
        var text = new TextContent("hello")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["cache_control"] = new Dictionary<string, object?> { ["type"] = "ephemeral" },
            },
        };

        var part = await CaptureFirstContentPartAsync(
            new ChatMessage(ChatRole.User, [text]));

        Assert.Equal("text", part.GetProperty("type").GetString());
        Assert.Equal("hello", part.GetProperty("text").GetString());
        Assert.Equal("ephemeral", part.GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ImageContent_NestedAdditionalProperty_MergesWithoutReplacingPayload()
    {
        var image = new DataContent(new byte[] { 5, 6 }, "image/png")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["image_url"] = new Dictionary<string, object?> { ["detail"] = "low" },
            },
        };

        var part = await CaptureFirstContentPartAsync(
            new ChatMessage(ChatRole.User, [image]));

        var imageUrl = part.GetProperty("image_url");
        Assert.StartsWith("data:image/png;base64,", imageUrl.GetProperty("url").GetString());
        Assert.Equal("low", imageUrl.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task OpenAIPayload_ReplacesContentPart()
    {
        var audio = new DataContent(new byte[] { 7, 8 }, "audio/mpeg")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                // 与工具序列化一致的逃生通道：需要 data URI 形状的服务（如 DashScope 兼容模式）可整体覆盖片段。
                ["openai_payload"] = new JsonObject
                {
                    ["type"] = "input_audio",
                    ["input_audio"] = new JsonObject
                    {
                        ["data"] = "data:audio/mpeg;base64,AAA",
                    },
                },
            },
        };

        var part = await CaptureFirstContentPartAsync(
            new ChatMessage(ChatRole.User, [audio]));

        Assert.Equal("input_audio", part.GetProperty("type").GetString());
        Assert.Equal(
            "data:audio/mpeg;base64,AAA",
            part.GetProperty("input_audio").GetProperty("data").GetString());
        Assert.False(part.GetProperty("input_audio").TryGetProperty("format", out _));
    }

    [Fact]
    public async Task PlainTextMessage_StillCollapsesToPlainString()
    {
        var root = await CaptureRequestBodyAsync(
            new ChatMessage(ChatRole.User, [new TextContent("你"), new TextContent("好")]));

        Assert.Equal(
            "你好",
            root.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    private static async Task<JsonElement> CaptureFirstContentPartAsync(ChatMessage message)
    {
        var root = await CaptureRequestBodyAsync(message);
        return root.GetProperty("messages")[0].GetProperty("content")[0];
    }

    private static async Task<JsonElement> CaptureRequestBodyAsync(params ChatMessage[] messages)
    {
        string? requestBody = null;
        using var handler = new DelegateHttpMessageHandler(async (_, request, _) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse(MinimalResponseJson);
        });

        using var client = new OpenAICompatibleChatClient(handler, CreateOptions());
        await client.GetResponseAsync(messages);

        using var document = JsonDocument.Parse(Assert.IsType<string>(requestBody));
        return document.RootElement.Clone();
    }

    private static OpenAICompatibleChatClientOptions CreateOptions()
    {
        return new OpenAICompatibleChatClientOptions
        {
            Endpoint = new Uri("https://example.test/v1"),
            RequestPath = "chat/completions",
            ApiKey = "test-key",
            ModelId = "test-model",
        };
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private const string MinimalResponseJson = """
        {
          "id":"chatcmpl-1",
          "object":"chat.completion",
          "created":1710000000,
          "model":"test-model",
          "choices":[
            {
              "index":0,
              "message":{"role":"assistant","content":"ok"},
              "finish_reason":"stop"
            }
          ],
          "usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}
        }
        """;

    private sealed class DelegateHttpMessageHandler(
        Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        private int _attemptCount;

        public int AttemptCount => Volatile.Read(ref _attemptCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attemptCount);
            return handler(attempt, request, cancellationToken);
        }
    }
}
