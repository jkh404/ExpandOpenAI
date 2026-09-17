using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.Internal;

internal sealed class OpenAICompatibleRequestBuilder
{
    /// <summary>内容片段整体替换用的额外属性键，与工具序列化保持一致。</summary>
    private const string OpenAIPayloadKey = "openai_payload";

    /// <summary>OpenAI 图片细节参数，需写入 <c>image_url</c> 内部。</summary>
    private const string ImageDetailKey = "detail";

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
            body["response_format"] = SerializeResponseFormat(options.ResponseFormat);
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

        if (CanCollapseToPlainText(visibleContents))
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

    /// <summary>
    /// 只有当所有内容都是纯文本且未携带额外属性时才折叠为字符串，否则会丢失 <see cref="AIContent.AdditionalProperties"/>。
    /// </summary>
    private static bool CanCollapseToPlainText(IList<AIContent> contents)
    {
        foreach (var content in contents)
        {
            if (content is not TextContent || content.AdditionalProperties is { Count: > 0 })
            {
                return false;
            }
        }

        return true;
    }

    private JsonObject? SerializeContentPart(AIContent content)
    {
        var part = CreateContentPart(content);
        return part is null ? null : ApplyAdditionalProperties(part, content);
    }

    private JsonObject CreateContentPart(AIContent content)
    {
        switch (content)
        {
            case OpenAIRequestContent custom:
                return custom.SerializeToOpenAIRequestContentPart(_serializerOptions);

            case TextContent text:
                return new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text.Text,
                };

            case DataContent data when data.HasTopLevelMediaType("image"):
                return CreateImagePart(data.Uri);

            case UriContent uri when uri.HasTopLevelMediaType("image"):
                return CreateImagePart(uri.Uri.ToString());

            case DataContent data when data.HasTopLevelMediaType("audio"):
                // OpenAI 要求 input_audio.data 为纯 base64 数据，音频格式通过 format 字段表达。
                return CreateAudioPart(data.Base64Data.ToString(), data.MediaType);

            case UriContent uri when uri.HasTopLevelMediaType("audio"):
                return CreateAudioPart(uri);

            default:
                throw new NotSupportedException(
                    $"当前内容类型 {content.GetType().FullName} 未实现默认 OpenAI 兼容序列化，请自定义请求构造逻辑。");
        }
    }

    private static JsonObject CreateImagePart(string url)
    {
        return new JsonObject
        {
            ["type"] = "image_url",
            ["image_url"] = new JsonObject
            {
                ["url"] = url,
            },
        };
    }

    private static JsonObject CreateAudioPart(string base64Data, string? mediaType)
    {
        var inputAudio = new JsonObject
        {
            ["data"] = base64Data,
        };

        if (GetAudioFormat(mediaType) is { Length: > 0 } format)
        {
            inputAudio["format"] = format;
        }

        return new JsonObject
        {
            ["type"] = "input_audio",
            ["input_audio"] = inputAudio,
        };
    }

    private static JsonObject CreateAudioPart(UriContent content)
    {
        if (string.Equals(content.Uri.Scheme, "data", StringComparison.OrdinalIgnoreCase))
        {
            return CreateAudioPart(ExtractBase64Data(content), content.MediaType);
        }

        // OpenAI 未定义远程音频输入，这里保留 url 形状，供支持该扩展的兼容服务使用。
        var inputAudio = new JsonObject
        {
            ["url"] = content.Uri.ToString(),
        };

        if (GetAudioFormat(content.MediaType) is { Length: > 0 } format)
        {
            inputAudio["format"] = format;
        }

        return new JsonObject
        {
            ["type"] = "input_audio",
            ["input_audio"] = inputAudio,
        };
    }

    private static string ExtractBase64Data(UriContent content)
    {
        try
        {
            return new DataContent(content.Uri, content.MediaType).Base64Data.ToString();
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException)
        {
            throw new InvalidOperationException($"无法解析音频 data URI：{content.Uri}", exception);
        }
    }

    /// <summary>
    /// 把内容对象上的 <see cref="AIContent.AdditionalProperties"/> 合并到内容片段。
    /// </summary>
    /// <remarks>
    /// 规则：
    /// <list type="bullet">
    /// <item><c>openai_payload</c> 与工具序列化保持一致，用于整体替换片段结构。</item>
    /// <item>图片的 <c>detail</c> 按 OpenAI 规范写入 <c>image_url</c> 内部。</item>
    /// <item>其余键值默认写入片段顶层；若片段中同名键本身是 JSON 对象且新值也是 JSON 对象，则合并到该嵌套对象内部。</item>
    /// </list>
    /// </remarks>
    private JsonObject ApplyAdditionalProperties(JsonObject part, AIContent content)
    {
        var properties = content.AdditionalProperties;
        if (properties is null || properties.Count == 0)
        {
            return part;
        }

        if (properties.TryGetValue(OpenAIPayloadKey, out var rawPayload))
        {
            part = ToJsonObject(OpenAIPayloadKey, rawPayload);
        }

        var imageUrl = part["image_url"] as JsonObject;

        foreach (var pair in properties)
        {
            if (string.Equals(pair.Key, OpenAIPayloadKey, StringComparison.Ordinal))
            {
                continue;
            }

            var value = SerializePropertyValue(pair.Value);

            if (imageUrl is not null && string.Equals(pair.Key, ImageDetailKey, StringComparison.Ordinal))
            {
                imageUrl[ImageDetailKey] = value;
                continue;
            }

            if (part[pair.Key] is JsonObject nested && value is JsonObject nestedValue)
            {
                foreach (var nestedPair in nestedValue)
                {
                    nested[nestedPair.Key] = CloneNode(nestedPair.Value);
                }

                continue;
            }

            part[pair.Key] = value;
        }

        return part;
    }

    private JsonNode? SerializePropertyValue(object? value)
    {
        return value switch
        {
            null => null,
            JsonNode node => CloneNode(node),
            JsonElement element when element.ValueKind == JsonValueKind.Undefined => null,
            JsonElement element => JsonNode.Parse(element.GetRawText()),
            _ => JsonSerializer.SerializeToNode(value, _serializerOptions),
        };
    }

    private JsonObject ToJsonObject(string propertyName, object? value)
    {
        var node = value switch
        {
            null => null,
            JsonObject jsonObject => CloneObject(jsonObject),
            JsonElement element when element.ValueKind == JsonValueKind.Object
                => JsonNode.Parse(element.GetRawText()) as JsonObject,
            _ => JsonSerializer.SerializeToNode(value, _serializerOptions) as JsonObject,
        };

        return node ?? throw new InvalidOperationException(
            $"AdditionalProperties[\"{propertyName}\"] 必须是 JSON 对象。");
    }

    private static JsonNode? CloneNode(JsonNode? node)
    {
        return node is null ? null : JsonNode.Parse(node.ToJsonString());
    }

    private static JsonObject CloneObject(JsonObject value)
    {
        return JsonNode.Parse(value.ToJsonString()) as JsonObject
            ?? throw new InvalidOperationException("无法复制 OpenAI 请求 JSON 对象。");
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

            if (tool.AdditionalProperties?.TryGetValue(OpenAIPayloadKey, out var rawToolPayload) == true)
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

    private JsonNode SerializeResponseFormat(ChatResponseFormat responseFormat)
    {
        return responseFormat switch
        {
            ChatResponseFormatText => new JsonObject
            {
                ["type"] = "text",
            },
            ChatResponseFormatJson json when json.Schema is null => new JsonObject
            {
                ["type"] = "json_object",
            },
            ChatResponseFormatJson json => SerializeJsonSchemaResponseFormat(json),
            _ => JsonSerializer.SerializeToNode(responseFormat, _serializerOptions)
                ?? throw new NotSupportedException($"未实现响应格式 {responseFormat.GetType().FullName} 的默认序列化。"),
        };
    }

    private JsonObject SerializeJsonSchemaResponseFormat(ChatResponseFormatJson responseFormat)
    {
        var schemaNode = responseFormat.Schema is JsonElement schema
            ? JsonSerializer.SerializeToNode(schema, _serializerOptions)
            : null;

        if (schemaNode is null)
        {
            throw new InvalidOperationException("Json schema response format 缺少 Schema。");
        }

        var jsonSchema = new JsonObject
        {
            ["name"] = SanitizeResponseFormatName(responseFormat.SchemaName),
            ["schema"] = schemaNode,
        };

        if (!string.IsNullOrWhiteSpace(responseFormat.SchemaDescription))
        {
            jsonSchema["description"] = responseFormat.SchemaDescription;
        }

        return new JsonObject
        {
            ["type"] = "json_schema",
            ["json_schema"] = jsonSchema,
        };
    }

    private static string SanitizeResponseFormatName(string? name)
    {
        const string fallbackName = "schema";
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallbackName;
        }

        var effectiveName = name!;
        var builder = new StringBuilder(Math.Min(effectiveName.Length, 64));

        foreach (var character in effectiveName)
        {
            if (builder.Length >= 64)
            {
                break;
            }

            builder.Append(char.IsLetterOrDigit(character) || character is '_' or '-'
                ? character
                : '_');
        }

        var sanitized = builder.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? fallbackName : sanitized;
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

    private static string? GetAudioFormat(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return null;
        }

        var effectiveMediaType = mediaType!;
        var slashIndex = effectiveMediaType.IndexOf('/');
        var subtype = slashIndex >= 0
            ? effectiveMediaType.Substring(slashIndex + 1)
            : effectiveMediaType;

        // 去掉 audio/wav;codecs=1 这类参数。
        var parameterIndex = subtype.IndexOf(';');
        if (parameterIndex >= 0)
        {
            subtype = subtype.Substring(0, parameterIndex);
        }

        subtype = subtype.Trim().ToLowerInvariant();
        if (subtype.Length == 0)
        {
            return null;
        }

        return subtype switch
        {
            "mpeg" => "mp3",
            "mpga" => "mp3",
            "x-wav" => "wav",
            "x-m4a" => "m4a",
            _ => subtype,
        };
    }
}
