using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.Internal;

internal sealed class OpenAICompatibleRequestBuilder
{
    private readonly OpenAICompatibleChatClientOptions _options;
    private readonly JsonSerializerOptions _serializerOptions;

    public OpenAICompatibleRequestBuilder(OpenAICompatibleChatClientOptions options, JsonSerializerOptions serializerOptions)
    {
        _options = options;
        _serializerOptions = serializerOptions;
    }

    public HttpRequestMessage CreateRequestMessage(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        bool stream,
        Action<JsonObject, IReadOnlyList<ChatMessage>, ChatOptions?, bool>? configureRequestBody,
        Action<HttpRequestMessage, IReadOnlyList<ChatMessage>, ChatOptions?, bool>? configureRequest)
    {
        var compatibleOptions = options as OpenAICompatibleChatClientOptions ?? _options;
        var requestUri = BuildRequestUri(compatibleOptions);
        var body = CreateRequestBody(messages, options, stream, configureRequestBody);
        var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(body.ToJsonString(_serializerOptions), Encoding.UTF8, "application/json"),
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (stream)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        }

        AddAuthenticationHeader(request, compatibleOptions);
        AddDefaultHeaders(request, compatibleOptions);

        compatibleOptions.ConfigureRequest?.Invoke(request, messages, options, stream);
        configureRequest?.Invoke(request, messages, options, stream);
        return request;
    }

    private static Uri BuildRequestUri(OpenAICompatibleChatClientOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RequestPath))
        {
            return options.Endpoint;
        }

        if (Uri.TryCreate(options.RequestPath, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri;
        }

        var baseUri = options.Endpoint;
        if (!baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal))
        {
            baseUri = new Uri($"{baseUri.AbsoluteUri}/", UriKind.Absolute);
        }

        return new Uri(baseUri, options.RequestPath);
    }

    private JsonObject CreateRequestBody(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        bool stream,
        Action<JsonObject, IReadOnlyList<ChatMessage>, ChatOptions?, bool>? configureRequestBody)
    {
        var compatibleOptions = options as OpenAICompatibleChatClientOptions ?? _options;
        var body = new JsonObject
        {
            ["model"] = options?.ModelId ?? compatibleOptions.ModelId,
            ["messages"] = new JsonArray(messages.Select(SerializeMessage).ToArray()),
            ["stream"] = stream,
        };

        if (options?.Temperature is not null)
        {
            body["temperature"] = JsonSerializer.SerializeToNode(options.Temperature, _serializerOptions);
        }

        if (options?.MaxOutputTokens is not null)
        {
            body["max_tokens"] = JsonSerializer.SerializeToNode(options.MaxOutputTokens, _serializerOptions);
        }

        if (options?.TopP is not null)
        {
            body["top_p"] = JsonSerializer.SerializeToNode(options.TopP, _serializerOptions);
        }

        if (options?.TopK is not null)
        {
            body["top_k"] = JsonSerializer.SerializeToNode(options.TopK, _serializerOptions);
        }

        if (options?.FrequencyPenalty is not null)
        {
            body["frequency_penalty"] = JsonSerializer.SerializeToNode(options.FrequencyPenalty, _serializerOptions);
        }

        if (options?.PresencePenalty is not null)
        {
            body["presence_penalty"] = JsonSerializer.SerializeToNode(options.PresencePenalty, _serializerOptions);
        }

        if (options?.Seed is not null)
        {
            body["seed"] = JsonSerializer.SerializeToNode(options.Seed, _serializerOptions);
        }

        if (options?.StopSequences is { Count: > 0 })
        {
            body["stop"] = new JsonArray(options.StopSequences.Select(static item => JsonValue.Create(item)).ToArray());
        }

        if (options?.ResponseFormat is not null)
        {
            body["response_format"] = JsonSerializer.SerializeToNode(options.ResponseFormat, _serializerOptions);
        }

        if (options?.Reasoning is not null)
        {
            body["reasoning"] = JsonSerializer.SerializeToNode(options.Reasoning, _serializerOptions);
        }

        if (options?.Tools is { Count: > 0 })
        {
            body["tools"] = SerializeTools(options.Tools);
        }

        if (options?.ToolMode is not null)
        {
            body["tool_choice"] = SerializeToolChoice(options.ToolMode);
        }

        if (options?.AllowMultipleToolCalls is not null)
        {
            body["parallel_tool_calls"] = options.AllowMultipleToolCalls.Value;
        }

        MergeRequestProperties(body, compatibleOptions.RequestBody);
        MergeRequestProperties(body, options?.AdditionalProperties);

        compatibleOptions.ConfigureRequestBody?.Invoke(body, messages, options, stream);
        configureRequestBody?.Invoke(body, messages, options, stream);

        return body;
    }

    private JsonObject SerializeMessage(ChatMessage message)
    {
        var node = new JsonObject
        {
            ["role"] = message.Role.Value,
        };

        if (!string.IsNullOrWhiteSpace(message.AuthorName))
        {
            node["name"] = message.AuthorName;
        }

        if (message.Role == ChatRole.Tool)
        {
            SerializeToolResultMessage(message, node);
            return node;
        }

        var toolCalls = message.Contents.OfType<FunctionCallContent>().ToList();
        if (toolCalls.Count > 0)
        {
            node["tool_calls"] = new JsonArray(toolCalls.Select(SerializeFunctionCall).ToArray());
        }

        var contentNode = SerializeContentPayload(message.Contents);
        if (contentNode is not null || toolCalls.Count == 0)
        {
            node["content"] = contentNode ?? string.Empty;
        }

        return node;
    }

    private JsonNode? SerializeContentPayload(IList<AIContent> contents)
    {
        var visibleContents = contents
            .Where(content => content is not FunctionCallContent && content is not FunctionResultContent && content is not TextReasoningContent)
            .ToList();

        if (visibleContents.Count == 0)
        {
            return null;
        }

        if (visibleContents.All(static content => content is TextContent))
        {
            var text = string.Concat(visibleContents.Cast<TextContent>().Select(content => content.Text));
            return JsonValue.Create(text);
        }

        var parts = new JsonArray();
        foreach (var content in visibleContents)
        {
            var serialized = SerializeContentPart(content);
            if (serialized is not null)
            {
                parts.Add(serialized);
            }
        }

        return parts;
    }

    private JsonObject? SerializeContentPart(AIContent content)
    {
        return content switch
        {
            OpenAIRequestContent custom => custom.SerializeToOpenAIRequestContentPart(_serializerOptions),
            TextContent text => new JsonObject
            {
                ["type"] = "text",
                ["text"] = text.Text,
            },
            DataContent data when data.HasTopLevelMediaType("image") => new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject
                {
                    ["url"] = data.Uri,
                },
            },
            UriContent uri when uri.HasTopLevelMediaType("image") => new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject
                {
                    ["url"] = uri.Uri.ToString(),
                },
            },
            DataContent data when data.HasTopLevelMediaType("audio") => new JsonObject
            {
                ["type"] = "input_audio",
                ["input_audio"] = new JsonObject
                {
                    ["data"] = data.Uri,
                },
            },
            UriContent uri when uri.HasTopLevelMediaType("audio") => new JsonObject
            {
                ["type"] = "input_audio",
                ["input_audio"] = SerializeAudioInput(uri),
            },
            _ => throw new NotSupportedException(
                $"当前内容类型 {content.GetType().FullName} 未实现默认 OpenAI 兼容序列化，请自定义请求构造逻辑。"),
        };
    }

    private void SerializeToolResultMessage(ChatMessage message, JsonObject node)
    {
        var results = message.Contents.OfType<FunctionResultContent>().ToList();
        if (results.Count > 1)
        {
            throw new NotSupportedException("单个 tool 消息默认只支持一个 FunctionResultContent。");
        }

        if (results.Count == 1)
        {
            var result = results[0];
            node["tool_call_id"] = result.CallId;
            node["content"] = result.Result switch
            {
                null => string.Empty,
                string text => text,
                _ => JsonSerializer.Serialize(result.Result, _serializerOptions),
            };

            return;
        }

        node["content"] = SerializeContentPayload(message.Contents) ?? string.Empty;
    }

    private JsonObject SerializeFunctionCall(FunctionCallContent functionCall)
    {
        return new JsonObject
        {
            ["id"] = functionCall.CallId,
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = functionCall.Name,
                ["arguments"] = JsonSerializer.Serialize(functionCall.Arguments ?? new Dictionary<string, object?>(), _serializerOptions),
            },
        };
    }

    private JsonArray SerializeTools(IEnumerable<AITool> tools)
    {
        var array = new JsonArray();

        foreach (var tool in tools)
        {
            if (tool is AIFunctionDeclaration function)
            {
                array.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = function.Name,
                        ["description"] = function.Description,
                        ["parameters"] = JsonSerializer.SerializeToNode(function.JsonSchema, _serializerOptions),
                    },
                });

                continue;
            }

            if (tool.AdditionalProperties?.TryGetValue("openai_payload", out var rawToolPayload) == true)
            {
                array.Add(JsonSerializer.SerializeToNode(rawToolPayload, _serializerOptions));
                continue;
            }

            throw new NotSupportedException(
                $"工具类型 {tool.GetType().FullName} 未实现默认 OpenAI 兼容序列化，请提供 AIFunctionDeclaration 或 openai_payload。");
        }

        return array;
    }

    private static JsonNode SerializeToolChoice(ChatToolMode toolMode)
    {
        return toolMode switch
        {
            NoneChatToolMode => JsonValue.Create("none")!,
            AutoChatToolMode => JsonValue.Create("auto")!,
            RequiredChatToolMode required when string.IsNullOrWhiteSpace(required.RequiredFunctionName) => JsonValue.Create("required")!,
            RequiredChatToolMode required => new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = required.RequiredFunctionName,
                },
            },
            _ => throw new NotSupportedException($"未实现工具模式 {toolMode.GetType().FullName} 的默认序列化。"),
        };
    }

    private static void AddAuthenticationHeader(HttpRequestMessage request, OpenAICompatibleChatClientOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return;
        }

        var headerName = options.ApiKeyHeaderName;
        var value = string.IsNullOrWhiteSpace(options.ApiKeyScheme)
            ? options.ApiKey
            : $"{options.ApiKeyScheme} {options.ApiKey}";

        request.Headers.Remove(headerName);
        request.Headers.TryAddWithoutValidation(headerName, value);
    }

    private static void AddDefaultHeaders(HttpRequestMessage request, OpenAICompatibleChatClientOptions options)
    {
        foreach (var pair in options.Headers)
        {
            request.Headers.Remove(pair.Key);
            request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        }
    }

    private void MergeRequestProperties(JsonObject body, IReadOnlyDictionary<string, object?>? properties)
    {
        if (properties is null)
        {
            return;
        }

        foreach (var pair in properties)
        {
            body[pair.Key] = JsonSerializer.SerializeToNode(pair.Value, _serializerOptions);
        }
    }

    private JsonObject SerializeAudioInput(UriContent uri)
    {
        if (string.Equals(uri.Uri.Scheme, "data", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject
            {
                ["data"] = uri.Uri.ToString(),
            };
        }

        return new JsonObject
        {
            ["url"] = uri.Uri.ToString(),
            ["format"] = GetAudioFormat(uri.MediaType),
        };
    }

    private static string? GetAudioFormat(string mediaType)
    {
        var subtype = mediaType.Substring(mediaType.IndexOf('/') + 1).Trim().ToLowerInvariant();
        return subtype switch
        {
            "mpeg" => "mp3",
            "mpga" => "mp3",
            "x-wav" => "wav",
            _ => subtype,
        };
    }
}
