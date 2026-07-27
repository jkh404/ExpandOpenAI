using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.Tests;

public sealed class OpenAICompatibleEmbeddingGeneratorTests
{
    [Fact]
    public async Task GenerateMultimodalAsync_UsesDashScopeWireFormatAndParsesResponse()
    {
        string? requestBody = null;
        Uri? requestUri = null;
        string? authorization = null;
        using var handler = new DelegateHttpMessageHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            requestUri = request.RequestUri;
            authorization = request.Headers.Authorization?.ToString();
            return JsonResponse(DashScopeMultimodalResponseJson);
        });
        using var generator = new OpenAICompatibleEmbeddingGenerator(
            handler,
            new OpenAICompatibleEmbeddingGeneratorOptions
            {
                Endpoint = new Uri("https://workspace.cn-beijing.maas.aliyuncs.com/api/v1/services/embeddings/multimodal-embedding/multimodal-embedding"),
                RequestPath = string.Empty,
                ApiKey = "test-key",
                ModelId = "tongyi-embedding-vision-plus",
            });

        var embeddings = await generator.GenerateMultimodalAsync(
        [
            new TextContent("一只在草地上奔跑的狗"),
            new UriContent("https://example.test/dog.png", "image/png"),
            new UriContent("https://example.test/dog.mp4", "video/mp4"),
        ]);

        Assert.Equal(
            "https://workspace.cn-beijing.maas.aliyuncs.com/api/v1/services/embeddings/multimodal-embedding/multimodal-embedding",
            requestUri?.ToString());
        Assert.Equal("Bearer test-key", authorization);

        using var document = JsonDocument.Parse(Assert.IsType<string>(requestBody));
        var root = document.RootElement;
        Assert.Equal("tongyi-embedding-vision-plus", root.GetProperty("model").GetString());
        Assert.Empty(root.GetProperty("parameters").EnumerateObject());
        var contents = root.GetProperty("input").GetProperty("contents");
        Assert.Equal("一只在草地上奔跑的狗", contents[0].GetProperty("text").GetString());
        Assert.Equal("https://example.test/dog.png", contents[1].GetProperty("image").GetString());
        Assert.Equal("https://example.test/dog.mp4", contents[2].GetProperty("video").GetString());

        Assert.Equal(3, embeddings.Count);
        Assert.Equal(0.1f, embeddings[0].Vector.Span[0]);
        Assert.Equal(0.3f, embeddings[1].Vector.Span[0]);
        Assert.Equal(0.5f, embeddings[2].Vector.Span[0]);
        Assert.Equal(10, embeddings.Usage?.InputTokenCount);
        Assert.Equal(3, embeddings.Usage?.OutputTokenCount);
        Assert.Equal(13, embeddings.Usage?.TotalTokenCount);
    }

    [Fact]
    public async Task GenerateMultimodalAsync_MapsDimensionsAndCustomParameters()
    {
        string? requestBody = null;
        using var handler = new DelegateHttpMessageHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse(DashScopeMultimodalResponseJson);
        });
        using var generator = new OpenAICompatibleEmbeddingGenerator(
            handler,
            new OpenAICompatibleEmbeddingGeneratorOptions
            {
                Endpoint = new Uri("https://example.test/v1"),
                ModelId = "qwen3-vl-embedding",
                DefaultModelDimensions = 1024,
                ConfigureMultimodalRequestBody = static (body, _, _) =>
                    body["parameters"]!["enable_fusion"] = true,
            });

        await generator.GenerateMultimodalAsync([new TextContent("product description")]);

        using var document = JsonDocument.Parse(Assert.IsType<string>(requestBody));
        var parameters = document.RootElement.GetProperty("parameters");
        Assert.Equal(1024, parameters.GetProperty("dimension").GetInt32());
        Assert.True(parameters.GetProperty("enable_fusion").GetBoolean());
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private const string DashScopeMultimodalResponseJson = """
        {
          "output": {
            "embeddings": [
              { "index": 2, "embedding": [0.5, 0.6], "type": "video" },
              { "index": 0, "embedding": [0.1, 0.2], "type": "text" },
              { "index": 1, "embedding": [0.3, 0.4], "type": "image" }
            ]
          },
          "usage": {
            "input_tokens": 10,
            "output_tokens": 3,
            "total_tokens": 13
          },
          "request_id": "request_123"
        }
        """;

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return handler(request);
        }
    }
}
