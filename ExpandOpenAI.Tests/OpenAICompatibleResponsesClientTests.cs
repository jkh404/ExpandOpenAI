using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.Tests;

public sealed class OpenAICompatibleResponsesClientTests
{
    [Fact]
    public async Task Request_UsesResponsesApiWireFormat()
    {
        string? requestBody = null;
        Uri? requestUri = null;
        string? authorization = null;
        using var handler = new DelegateHttpMessageHandler(async (_, request, _) =>
        {
            requestUri = request.RequestUri;
            authorization = request.Headers.Authorization?.ToString();
            requestBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse(MinimalResponseJson);
        });

        var weather = AIFunctionFactory.Create(
            (string city) => city,
            "get_weather",
            "查询天气");
        var options = CreateOptions();
        options.ApiKey = "test-key";
        options.Instructions = "回答要简洁";
        options.Store = false;
        options.PreviousResponseId = "resp_previous";
        options.Include = ["reasoning.encrypted_content"];
        options.Metadata = new Dictionary<string, object?> { ["tenant"] = "alpha" };
        options.MaxToolCalls = 2;
        options.Tools = [weather];
        options.ToolMode = ChatToolMode.Auto;
        options.AllowMultipleToolCalls = true;
        options.ResponseFormat = ChatResponseFormat.ForJsonSchema(
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    summary = new { type = "string" },
                },
            }),
            schemaName: "weather-summary");

        using var client = new OpenAICompatibleResponsesClient(handler, options);
        var messages = new ChatMessage[]
        {
            new(
                ChatRole.User,
                [
                    new TextContent("看图并查询天气"),
                    new UriContent("https://example.test/weather.png", "image/png"),
                ]),
            new(
                ChatRole.Tool,
                [new FunctionResultContent("call_123", new { temperature = 26 })]),
        };

        await client.GetResponseAsync(messages);

        Assert.Equal("https://example.test/v1/responses", requestUri?.ToString());
        Assert.Equal("Bearer test-key", authorization);
        using var document = JsonDocument.Parse(Assert.IsType<string>(requestBody));
        var root = document.RootElement;
        Assert.Equal("test-model", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal("回答要简洁", root.GetProperty("instructions").GetString());
        Assert.False(root.GetProperty("store").GetBoolean());
        Assert.Equal("resp_previous", root.GetProperty("previous_response_id").GetString());
        Assert.Equal(2, root.GetProperty("max_tool_calls").GetInt32());
        Assert.True(root.GetProperty("parallel_tool_calls").GetBoolean());
        var format = root.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("weather-summary", format.GetProperty("name").GetString());
        Assert.Equal("object", format.GetProperty("schema").GetProperty("type").GetString());
        Assert.False(format.TryGetProperty("json_schema", out _));

        var input = root.GetProperty("input");
        Assert.Equal(2, input.GetArrayLength());
        var message = input[0];
        Assert.Equal("message", message.GetProperty("type").GetString());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("input_text", message.GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("input_image", message.GetProperty("content")[1].GetProperty("type").GetString());
        Assert.Equal(
            "https://example.test/weather.png",
            message.GetProperty("content")[1].GetProperty("image_url").GetString());

        var functionOutput = input[1];
        Assert.Equal("function_call_output", functionOutput.GetProperty("type").GetString());
        Assert.Equal("call_123", functionOutput.GetProperty("call_id").GetString());
        using (var output = JsonDocument.Parse(functionOutput.GetProperty("output").GetString()!))
        {
            Assert.Equal(26, output.RootElement.GetProperty("temperature").GetInt32());
        }

        var tool = root.GetProperty("tools")[0];
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("get_weather", tool.GetProperty("name").GetString());
        Assert.True(tool.TryGetProperty("parameters", out _));
        Assert.False(tool.TryGetProperty("function", out _));
        Assert.Equal("auto", root.GetProperty("tool_choice").GetString());
    }

    [Fact]
    public async Task Response_MapsTextReasoningFunctionUsageAndUnknownItems()
    {
        using var handler = new DelegateHttpMessageHandler((_, _, _) => Task.FromResult(JsonResponse(ComplexResponseJson)));
        using var client = new OpenAICompatibleResponsesClient(handler, CreateOptions());

        var response = await client.GetResponseAsync("hello");

        Assert.Equal("resp_123", response.ResponseId);
        Assert.Equal("msg_123", Assert.Single(response.Messages).MessageId);
        Assert.Equal("conv_123", response.ConversationId);
        Assert.Equal("actual-model", response.ModelId);
        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
        Assert.Equal(10, response.Usage?.InputTokenCount);
        Assert.Equal(12, response.Usage?.OutputTokenCount);
        Assert.Equal(22, response.Usage?.TotalTokenCount);
        Assert.Equal(3, response.Usage?.CachedInputTokenCount);
        Assert.Equal(4, response.Usage?.ReasoningTokenCount);

        var contents = response.Messages[0].Contents;
        Assert.Equal("分析天气", Assert.Single(contents.OfType<TextReasoningContent>()).Text);
        Assert.Equal("天气晴朗", Assert.Single(contents.OfType<TextContent>()).Text);

        var functionCall = Assert.Single(contents.OfType<FunctionCallContent>());
        Assert.Equal("call_123", functionCall.CallId);
        Assert.Equal("get_weather", functionCall.Name);
        Assert.Equal("北京", functionCall.Arguments!["city"]?.ToString());

        var rawContents = contents.OfType<OpenAIResponsesRawContent>().ToList();
        Assert.Equal(2, rawContents.Count);
        Assert.Contains(rawContents, content =>
            content.IsTopLevelItem && content.Value["type"]?.GetValue<string>() == "web_search_call");
        Assert.Contains(rawContents, content =>
            !content.IsTopLevelItem && content.Value["type"]?.GetValue<string>() == "vendor_extension");
    }

    [Fact]
    public async Task UnknownTopLevelItem_CanBeSentBackWithoutLosingFields()
    {
        string? requestBody = null;
        using var handler = new DelegateHttpMessageHandler(async (_, request, _) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse(MinimalResponseJson);
        });
        using var client = new OpenAICompatibleResponsesClient(handler, CreateOptions());
        var raw = new OpenAIResponsesRawContent(new JsonObject
        {
            ["type"] = "vendor_state",
            ["id"] = "state_1",
            ["payload"] = new JsonObject { ["cursor"] = 7 },
        });

        await client.GetResponseAsync([new ChatMessage(ChatRole.Assistant, [raw])]);

        using var document = JsonDocument.Parse(Assert.IsType<string>(requestBody));
        var item = document.RootElement.GetProperty("input")[0];
        Assert.Equal("vendor_state", item.GetProperty("type").GetString());
        Assert.Equal("state_1", item.GetProperty("id").GetString());
        Assert.Equal(7, item.GetProperty("payload").GetProperty("cursor").GetInt32());
    }

    [Fact]
    public async Task StreamingResponse_EmitsDeltasFunctionOnceAndFinalUsage()
    {
        using var handler = new DelegateHttpMessageHandler((_, _, _) => Task.FromResult(
            EventStreamResponse(StreamingResponse)));
        using var client = new OpenAICompatibleResponsesClient(handler, CreateOptions());
        var updates = new List<ChatResponseUpdate>();

        await foreach (var update in client.GetStreamingResponseAsync("hello"))
        {
            updates.Add(update);
        }

        Assert.Equal("你好", string.Concat(updates.Select(static update => update.Text)));
        var functionCall = Assert.Single(updates.SelectMany(static update => update.Contents).OfType<FunctionCallContent>());
        Assert.Equal("call_123", functionCall.CallId);
        Assert.Equal("get_weather", functionCall.Name);
        Assert.Equal("北京", functionCall.Arguments!["city"]?.ToString());

        var usage = Assert.Single(updates.SelectMany(static update => update.Contents).OfType<UsageContent>()).Details;
        Assert.Equal(5, usage.InputTokenCount);
        Assert.Equal(7, usage.OutputTokenCount);
        Assert.Equal(12, usage.TotalTokenCount);
        Assert.Equal(ChatFinishReason.ToolCalls, updates[^1].FinishReason);
        Assert.All(updates, update => Assert.Equal("resp_stream", update.ResponseId));
    }

    [Fact]
    public async Task Client_RetriesTransientFailureBeforeParsingResponse()
    {
        using var handler = new DelegateHttpMessageHandler((attempt, _, _) => Task.FromResult(
            attempt == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : JsonResponse(MinimalResponseJson)));
        using var client = new OpenAICompatibleResponsesClient(handler, CreateOptions());

        var response = await client.GetResponseAsync("hello");

        Assert.Equal("ok", response.Text);
        Assert.Equal(2, handler.AttemptCount);
    }

    [Fact]
    public async Task IncompleteResponse_MapsMaxOutputTokensToLength()
    {
        const string json = """
            {
              "id":"resp_incomplete",
              "status":"incomplete",
              "incomplete_details":{"reason":"max_output_tokens"},
              "output":[]
            }
            """;
        using var handler = new DelegateHttpMessageHandler((_, _, _) => Task.FromResult(JsonResponse(json)));
        using var client = new OpenAICompatibleResponsesClient(handler, CreateOptions());

        var response = await client.GetResponseAsync("hello");

        Assert.Equal(ChatFinishReason.Length, response.FinishReason);
    }

    [Fact]
    public async Task Request_RejectsPreviousResponseIdAndConversationTogether()
    {
        using var handler = new DelegateHttpMessageHandler((_, _, _) => Task.FromResult(JsonResponse(MinimalResponseJson)));
        var options = CreateOptions();
        options.PreviousResponseId = "resp_previous";
        options.Conversation = "conv_123";
        using var client = new OpenAICompatibleResponsesClient(handler, options);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync("hello"));

        Assert.Contains("previous_response_id", exception.Message);
        Assert.Equal(0, handler.AttemptCount);
    }

    private static OpenAICompatibleResponsesClientOptions CreateOptions()
    {
        return new OpenAICompatibleResponsesClientOptions
        {
            Endpoint = new Uri("https://example.test/v1"),
            ModelId = "test-model",
            RetryOptions = new OpenAICompatibleHttpRetryOptions
            {
                MaxRetryAttempts = 2,
                InitialDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
            },
        };
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private static HttpResponseMessage EventStreamResponse(string content)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "text/event-stream"),
        };
    }

    private const string MinimalResponseJson = """
        {
          "id":"resp_minimal",
          "status":"completed",
          "model":"test-model",
          "output":[
            {
              "type":"message",
              "id":"msg_minimal",
              "role":"assistant",
              "content":[{"type":"output_text","text":"ok","annotations":[]}]
            }
          ]
        }
        """;

    private const string ComplexResponseJson = """
        {
          "id":"resp_123",
          "object":"response",
          "created_at":1710000000,
          "status":"completed",
          "model":"actual-model",
          "conversation":{"id":"conv_123"},
          "output":[
            {
              "type":"reasoning",
              "id":"rs_123",
              "summary":[{"type":"summary_text","text":"分析天气"}],
              "encrypted_content":"protected"
            },
            {
              "type":"message",
              "id":"msg_123",
              "role":"assistant",
              "content":[
                {"type":"output_text","text":"天气晴朗","annotations":[]},
                {"type":"vendor_extension","score":0.9}
              ]
            },
            {
              "type":"function_call",
              "id":"fc_123",
              "call_id":"call_123",
              "name":"get_weather",
              "arguments":"{\"city\":\"北京\"}"
            },
            {
              "type":"web_search_call",
              "id":"ws_123",
              "status":"completed",
              "vendor_field":"preserved"
            }
          ],
          "usage":{
            "input_tokens":10,
            "input_tokens_details":{"cached_tokens":3},
            "output_tokens":12,
            "output_tokens_details":{"reasoning_tokens":4},
            "total_tokens":22
          }
        }
        """;

    private const string StreamingResponse = """
        event: response.created
        data: {"type":"response.created","response":{"id":"resp_stream","status":"in_progress","created_at":1710000000,"model":"test-model"}}

        event: response.output_item.added
        data: {"type":"response.output_item.added","output_index":1,"item":{"type":"function_call","id":"fc_123","call_id":"call_123","name":"get_weather","arguments":""}}

        event: response.output_text.delta
        data: {"type":"response.output_text.delta","item_id":"msg_123","output_index":0,"content_index":0,"delta":"你"}

        event: response.output_text.delta
        data: {"type":"response.output_text.delta","item_id":"msg_123","output_index":0,"content_index":0,"delta":"好"}

        event: response.function_call_arguments.delta
        data: {"type":"response.function_call_arguments.delta","item_id":"fc_123","output_index":1,"delta":"{\"city\":"}

        event: response.function_call_arguments.delta
        data: {"type":"response.function_call_arguments.delta","item_id":"fc_123","output_index":1,"delta":"\"北京\"}"}

        event: response.function_call_arguments.done
        data: {"type":"response.function_call_arguments.done","item_id":"fc_123","output_index":1,"call_id":"call_123","name":"get_weather","arguments":"{\"city\":\"北京\"}"}

        event: response.output_item.done
        data: {"type":"response.output_item.done","output_index":1,"item":{"type":"function_call","id":"fc_123","call_id":"call_123","name":"get_weather","arguments":"{\"city\":\"北京\"}"}}

        event: response.completed
        data: {"type":"response.completed","response":{"id":"resp_stream","status":"completed","created_at":1710000000,"model":"test-model","output":[{"type":"message","id":"msg_123","role":"assistant","content":[{"type":"output_text","text":"你好","annotations":[]}]},{"type":"function_call","id":"fc_123","call_id":"call_123","name":"get_weather","arguments":"{\"city\":\"北京\"}"}],"usage":{"input_tokens":5,"output_tokens":7,"total_tokens":12}}}

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
