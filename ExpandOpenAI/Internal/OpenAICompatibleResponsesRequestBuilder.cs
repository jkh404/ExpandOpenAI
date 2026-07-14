using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.Internal;

internal sealed class OpenAICompatibleResponsesRequestBuilder
{
    private readonly OpenAICompatibleResponsesClientOptions _options;
    private readonly JsonSerializerOptions _serializerOptions;

    public OpenAICompatibleResponsesRequestBuilder(
        OpenAICompatibleResponsesClientOptions options,
        JsonSerializerOptions serializerOptions)
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
        var compatibleOptions = options as OpenAICompatibleResponsesClientOptions ?? _options;
        var body = CreateRequestBody(messages, options, stream, configureRequestBody);
        var request = new HttpRequestMessage(HttpMethod.Post, BuildRequestUri(compatibleOptions))
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

    private JsonObject CreateRequestBody(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        bool stream,
        Action<JsonObject, IReadOnlyList<ChatMessage>, ChatOptions?, bool>? configureRequestBody)
    {
        var compatibleOptions = options as OpenAICompatibleResponsesClientOptions ?? _options;
        var body = new JsonObject
        {
            ["model"] = options?.ModelId ?? compatibleOptions.ModelId,
            ["input"] = SerializeInput(messages),
            ["stream"] = stream,
        };

        AddValue(body, "instructions", options?.Instructions);
        AddValue(body, "temperature", options?.Temperature);
        AddValue(body, "max_output_tokens", options?.MaxOutputTokens);
        AddValue(body, "top_p", options?.TopP);
        AddValue(body, "reasoning", options?.Reasoning);
        AddValue(body, "parallel_tool_calls", options?.AllowMultipleToolCalls);
        AddValue(body, "background", options?.AllowBackgroundResponses);

        AddValue(body, "store", compatibleOptions.Store);
        AddValue(body, "truncation", compatibleOptions.Truncation);
        AddValue(body, "max_tool_calls", compatibleOptions.MaxToolCalls);

        var conversation = compatibleOptions.Conversation ?? options?.ConversationId;
        if (!string.IsNullOrWhiteSpace(compatibleOptions.PreviousResponseId) && conversation is not null)
        {
            throw new InvalidOperationException(
                "Responses API 的 previous_response_id 与 conversation 不能同时设置。");
        }

        AddValue(body, "previous_response_id", compatibleOptions.PreviousResponseId);
        AddValue(body, "conversation", conversation);

        if (compatibleOptions.Include is { Count: > 0 })
        {
            body["include"] = new JsonArray(
                compatibleOptions.Include.Select(static item => JsonValue.Create(item)).ToArray());
        }

        if (compatibleOptions.Metadata is not null)
        {
            body["metadata"] = JsonSerializer.SerializeToNode(compatibleOptions.Metadata, _serializerOptions);
        }

        if (options?.ResponseFormat is not null)
        {
            body["text"] = new JsonObject
            {
                ["format"] = SerializeResponseFormat(options.ResponseFormat),
            };
        }

        if (options?.Tools is { Count: > 0 })
        {
            body["tools"] = SerializeTools(options.Tools);
        }

        if (options?.ToolMode is not null)
        {
            body["tool_choice"] = SerializeToolChoice(options.ToolMode);
        }

        MergeRequestProperties(body, compatibleOptions.RequestBody);
        MergeRequestProperties(body, options?.AdditionalProperties);

        compatibleOptions.ConfigureRequestBody?.Invoke(body, messages, options, stream);
        configureRequestBody?.Invoke(body, messages, options, stream);
        return body;
    }

    private JsonArray SerializeInput(IReadOnlyList<ChatMessage> messages)
    {
        var input = new JsonArray();
        foreach (var message in messages)
        {
            SerializeMessage(input, message);
        }

        return input;
    }

    private void SerializeMessage(JsonArray input, ChatMessage message)
    {
        var pendingParts = new JsonArray();

        void FlushMessage()
        {
            if (pendingParts.Count == 0)
            {
                return;
            }

            input.Add(new JsonObject
            {
                ["type"] = "message",
                ["role"] = message.Role.Value,
                ["content"] = pendingParts,
            });

            pendingParts = new JsonArray();
        }

        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case FunctionCallContent functionCall:
                    FlushMessage();
                    input.Add(SerializeFunctionCall(functionCall));
                    break;

                case FunctionResultContent functionResult:
                    FlushMessage();
                    input.Add(SerializeFunctionResult(functionResult));
                    break;

                case TextReasoningContent reasoning:
                    FlushMessage();
                    input.Add(SerializeReasoning(reasoning));
                    break;

                case OpenAIResponsesRawContent raw when raw.IsTopLevelItem:
                    FlushMessage();
                    input.Add(raw.CloneValue());
                    break;

                default:
                    pendingParts.Add(SerializeContentPart(content, message.Role));
                    break;
            }
        }

        FlushMessage();
    }

    private JsonObject SerializeContentPart(AIContent content, ChatRole role)
    {
        if (content is OpenAIResponsesRawContent raw)
        {
            return raw.CloneValue();
        }

        if (content is OpenAIRequestContent custom)
        {
            return custom.SerializeToOpenAIRequestContentPart(_serializerOptions);
        }

        if (content is TextContent text)
        {
            var expectedType = role == ChatRole.Assistant ? "output_text" : "input_text";
            return TryCloneRawObject(text, expectedType) ?? new JsonObject
            {
                ["type"] = expectedType,
                ["text"] = text.Text,
            };
        }

        if (content is DataContent imageData && imageData.HasTopLevelMediaType("image"))
        {
            return new JsonObject
            {
                ["type"] = "input_image",
                ["image_url"] = imageData.Uri,
            };
        }

        if (content is UriContent imageUri && imageUri.HasTopLevelMediaType("image"))
        {
            return new JsonObject
            {
                ["type"] = "input_image",
                ["image_url"] = imageUri.Uri.ToString(),
            };
        }

        if (content is HostedFileContent hostedFile)
        {
            return new JsonObject
            {
                ["type"] = "input_file",
                ["file_id"] = hostedFile.FileId,
            };
        }

        if (content is DataContent fileData)
        {
            var result = new JsonObject
            {
                ["type"] = "input_file",
                ["file_data"] = fileData.Uri,
            };

            if (!string.IsNullOrWhiteSpace(fileData.Name))
            {
                result["filename"] = fileData.Name;
            }

            return result;
        }

        if (content is UriContent fileUri)
        {
            return new JsonObject
            {
                ["type"] = "input_file",
                ["file_url"] = fileUri.Uri.ToString(),
            };
        }

        throw new NotSupportedException(
            $"当前内容类型 {content.GetType().FullName} 未实现默认 Responses API 序列化，请使用 OpenAIResponsesRawContent 或 OpenAIRequestContent。");
    }

    private JsonObject SerializeReasoning(TextReasoningContent reasoning)
    {
        var raw = TryCloneRawObject(reasoning, "reasoning");
        if (raw is not null)
        {
            return raw;
        }

        var result = new JsonObject
        {
            ["type"] = "reasoning",
        };

        if (!string.IsNullOrEmpty(reasoning.Text))
        {
            result["summary"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "summary_text",
                    ["text"] = reasoning.Text,
                },
            };
        }

        if (!string.IsNullOrWhiteSpace(reasoning.ProtectedData))
        {
            result["encrypted_content"] = reasoning.ProtectedData;
        }

        return result;
    }

    private JsonObject SerializeFunctionCall(FunctionCallContent functionCall)
    {
        var raw = TryCloneRawObject(functionCall, "function_call");
        if (raw is not null)
        {
            return raw;
        }

        return new JsonObject
        {
            ["type"] = "function_call",
            ["call_id"] = functionCall.CallId,
            ["name"] = functionCall.Name,
            ["arguments"] = SerializeArguments(functionCall.Arguments),
        };
    }

    private JsonObject SerializeFunctionResult(FunctionResultContent functionResult)
    {
        var raw = TryCloneRawObject(functionResult, "function_call_output");
        if (raw is not null)
        {
            return raw;
        }

        return new JsonObject
        {
            ["type"] = "function_call_output",
            ["call_id"] = functionResult.CallId,
            ["output"] = functionResult.Result switch
            {
                null => string.Empty,
                string text => text,
                _ => JsonSerializer.Serialize(functionResult.Result, _serializerOptions),
            },
        };
    }

    private string SerializeArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments?.TryGetValue("$raw", out var raw) == true && raw is string rawJson)
        {
            return rawJson;
        }

        return JsonSerializer.Serialize(arguments ?? new Dictionary<string, object?>(), _serializerOptions);
    }

    private JsonArray SerializeTools(IEnumerable<AITool> tools)
    {
        var result = new JsonArray();
        foreach (var tool in tools)
        {
            if (tool is AIFunctionDeclaration function)
            {
                var functionNode = new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = function.Name,
                    ["description"] = function.Description,
                    ["parameters"] = JsonSerializer.SerializeToNode(function.JsonSchema, _serializerOptions),
                };

                if (function.AdditionalProperties?.TryGetValue("strict", out var strict) == true)
                {
                    functionNode["strict"] = JsonSerializer.SerializeToNode(strict, _serializerOptions);
                }

                result.Add(functionNode);
                continue;
            }

            if (tool.AdditionalProperties?.TryGetValue("openai_payload", out var rawToolPayload) == true)
            {
                result.Add(JsonSerializer.SerializeToNode(rawToolPayload, _serializerOptions));
                continue;
            }

            throw new NotSupportedException(
                $"工具类型 {tool.GetType().FullName} 未实现默认 Responses API 序列化，请提供 AIFunctionDeclaration 或 openai_payload。");
        }

        return result;
    }

    private static JsonNode SerializeToolChoice(ChatToolMode toolMode)
    {
        return toolMode switch
        {
            NoneChatToolMode => JsonValue.Create("none")!,
            AutoChatToolMode => JsonValue.Create("auto")!,
            RequiredChatToolMode required when string.IsNullOrWhiteSpace(required.RequiredFunctionName)
                => JsonValue.Create("required")!,
            RequiredChatToolMode required => new JsonObject
            {
                ["type"] = "function",
                ["name"] = required.RequiredFunctionName,
            },
            _ => throw new NotSupportedException($"未实现工具模式 {toolMode.GetType().FullName} 的 Responses API 序列化。"),
        };
    }

    private JsonObject SerializeResponseFormat(ChatResponseFormat responseFormat)
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
            _ => JsonSerializer.SerializeToNode(responseFormat, _serializerOptions) as JsonObject
                ?? throw new NotSupportedException($"未实现响应格式 {responseFormat.GetType().FullName} 的 Responses API 序列化。"),
        };
    }

    private JsonObject SerializeJsonSchemaResponseFormat(ChatResponseFormatJson responseFormat)
    {
        var schema = responseFormat.Schema is JsonElement schemaElement
            ? JsonSerializer.SerializeToNode(schemaElement, _serializerOptions)
            : null;

        if (schema is null)
        {
            throw new InvalidOperationException("Json schema response format 缺少 Schema。");
        }

        var result = new JsonObject
        {
            ["type"] = "json_schema",
            ["name"] = SanitizeResponseFormatName(responseFormat.SchemaName),
            ["schema"] = schema,
        };

        if (!string.IsNullOrWhiteSpace(responseFormat.SchemaDescription))
        {
            result["description"] = responseFormat.SchemaDescription;
        }

        return result;
    }

    private static string SanitizeResponseFormatName(string? name)
    {
        const string fallbackName = "schema";
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallbackName;
        }

        var builder = new StringBuilder(Math.Min(name!.Length, 64));
        foreach (var character in name)
        {
            if (builder.Length >= 64)
            {
                break;
            }

            builder.Append(char.IsLetterOrDigit(character) || character is '_' or '-' ? character : '_');
        }

        var sanitized = builder.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? fallbackName : sanitized;
    }

    private void AddValue(JsonObject body, string propertyName, object? value)
    {
        if (value is not null)
        {
            body[propertyName] = JsonSerializer.SerializeToNode(value, _serializerOptions);
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

    private static JsonObject? TryCloneRawObject(AIContent content, string expectedType)
    {
        JsonObject? result = content.RawRepresentation switch
        {
            JsonObject jsonObject => CloneObject(jsonObject),
            JsonElement element when element.ValueKind == JsonValueKind.Object
                => JsonNode.Parse(element.GetRawText()) as JsonObject,
            _ => null,
        };

        return result is not null
            && string.Equals(result["type"]?.GetValue<string>(), expectedType, StringComparison.Ordinal)
                ? result
                : null;
    }

    private static JsonObject CloneObject(JsonObject value)
    {
        return JsonNode.Parse(value.ToJsonString()) as JsonObject
            ?? throw new InvalidOperationException("无法复制 Responses JSON 对象。");
    }

    private static Uri BuildRequestUri(OpenAICompatibleResponsesClientOptions options)
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

    private static void AddAuthenticationHeader(
        HttpRequestMessage request,
        OpenAICompatibleResponsesClientOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return;
        }

        var value = string.IsNullOrWhiteSpace(options.ApiKeyScheme)
            ? options.ApiKey
            : $"{options.ApiKeyScheme} {options.ApiKey}";

        request.Headers.Remove(options.ApiKeyHeaderName);
        request.Headers.TryAddWithoutValidation(options.ApiKeyHeaderName, value);
    }

    private static void AddDefaultHeaders(
        HttpRequestMessage request,
        OpenAICompatibleResponsesClientOptions options)
    {
        foreach (var pair in options.Headers)
        {
            request.Headers.Remove(pair.Key);
            request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        }
    }
}
