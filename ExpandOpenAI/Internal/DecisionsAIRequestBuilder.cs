using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExpandOpenAI.Internal;

internal sealed class DecisionsAIRequestBuilder
{
    private readonly DecisionsAIClientOptions _options;
    private readonly JsonSerializerOptions _serializerOptions;

    public DecisionsAIRequestBuilder(
        DecisionsAIClientOptions options,
        JsonSerializerOptions serializerOptions)
    {
        _options = options;
        _serializerOptions = serializerOptions;
    }

    public HttpRequestMessage CreateRequestMessage(DecisionsRequest request)
    {
        var body = CreateRequestBody(request);
        var message = new HttpRequestMessage(HttpMethod.Post, BuildRequestUri())
        {
            Content = new StringContent(
                body.ToJsonString(_serializerOptions),
                Encoding.UTF8,
                "application/json"),
        };

        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        AddAuthenticationHeader(message);
        foreach (var pair in _options.Headers)
        {
            message.Headers.Remove(pair.Key);
            message.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        }

        try
        {
            _options.ConfigureRequest?.Invoke(message, request);
        }
        catch
        {
            message.Dispose();
            throw;
        }
        return message;
    }

    private Uri BuildRequestUri()
    {
        if (string.IsNullOrWhiteSpace(_options.RequestPath))
        {
            return _options.Endpoint;
        }

        if (Uri.TryCreate(_options.RequestPath, UriKind.Absolute, out var absoluteUri))
        {
            if (absoluteUri.Scheme is not ("http" or "https"))
            {
                throw new ArgumentException("RequestPath must use HTTP or HTTPS.", nameof(_options.RequestPath));
            }

            return absoluteUri;
        }

        var baseUri = _options.Endpoint;
        if (!baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal))
        {
            baseUri = new Uri($"{baseUri.AbsoluteUri}/", UriKind.Absolute);
        }

        return new Uri(baseUri, _options.RequestPath);
    }

    private JsonObject CreateRequestBody(DecisionsRequest request)
    {
        ArgumentGuard.ThrowIfNull(request.State, nameof(request.State));
        ArgumentGuard.ThrowIfNull(request.Questions, nameof(request.Questions));
        if (request.Questions.Count == 0)
        {
            throw new ArgumentException("At least one question is required.", nameof(request));
        }

        var model = request.ModelId ?? _options.ModelId;
        ArgumentGuard.ThrowIfNullOrWhiteSpace(model, nameof(request.ModelId));
        var body = new JsonObject();
        MergeRequestProperties(body, _options.RequestBody);
        MergeRequestProperties(body, request.AdditionalProperties);

        body["model"] = model;
        body["state"] = SerializeEntry(request.State, "state", allowNull: false);

        var questions = new JsonObject();
        foreach (var pair in request.Questions)
        {
            ArgumentGuard.ThrowIfNullOrWhiteSpace(pair.Key, nameof(request.Questions));
            ArgumentGuard.ThrowIfNull(pair.Value, nameof(request.Questions));
            questions[pair.Key] = SerializeQuestion(pair.Key, pair.Value);
        }

        body["questions"] = questions;
        AddIfPresent(body, "provider", request.Provider);
        AddIfPresent(body, "session_id", request.SessionId);
        AddIfPresent(body, "trace", request.Trace);
        AddIfPresent(body, "user", request.User);

        _options.ConfigureRequestBody?.Invoke(body, request);
        return body;
    }

    private JsonObject SerializeQuestion(string id, DecisionsQuestion question)
    {
        var body = new JsonObject();
        MergeRequestProperties(body, question.AdditionalProperties);
        body["type"] = question.Type;
        body["instructions"] = SerializeEntry(question.Instructions, $"questions.{id}.instructions");
        // Omitted noul criteria must not inherit a criteria value from extension fields.
        body.Remove("criteria");
        switch (question)
        {
            case DecisionsNoulQuestion noul when noul.Criteria is not null:
                body["criteria"] = new JsonObject
                {
                    ["true"] = SerializeEntry(noul.Criteria.True, $"questions.{id}.criteria.true"),
                    ["false"] = SerializeEntry(noul.Criteria.False, $"questions.{id}.criteria.false"),
                };
                break;
            case DecisionsNoulQuestion:
                break;
            case DecisionsChoiceQuestion choice:
                ArgumentGuard.ThrowIfNull(choice.Criteria, nameof(choice.Criteria));
                if (choice.Criteria.Count is 0 or > 255)
                {
                    throw new ArgumentException($"Choice question '{id}' requires between 1 and 255 options.");
                }

                var choices = new JsonObject();
                foreach (var pair in choice.Criteria)
                {
                    ArgumentGuard.ThrowIfNullOrWhiteSpace(pair.Key, nameof(choice.Criteria));
                    choices[pair.Key] = SerializeEntry(pair.Value, $"questions.{id}.criteria.{pair.Key}");
                }

                body["criteria"] = choices;
                break;
            case DecisionsScoreQuestion score:
                ArgumentGuard.ThrowIfNull(score.Criteria, nameof(score.Criteria));
                if (score.Criteria.Count is < 2 or > 10)
                {
                    throw new ArgumentException($"Score question '{id}' requires between 2 and 10 levels.");
                }

                var levels = new JsonArray();
                for (var index = 0; index < score.Criteria.Count; index++)
                {
                    levels.Add(SerializeEntry(score.Criteria[index], $"questions.{id}.criteria[{index}]"));
                }

                body["criteria"] = levels;
                break;
            default:
                throw new ArgumentException($"Unsupported question type '{question.GetType().Name}'.");
        }

        return body;
    }

    private JsonNode? SerializeEntry(object? value, string field, bool allowNull = true)
    {
        var node = SerializeValue(value);
        var kind = node?.GetValueKind() ?? JsonValueKind.Null;
        if (kind is JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array
            || (allowNull && kind == JsonValueKind.Null))
        {
            return node;
        }

        throw new ArgumentException(
            $"{field} must be a string, object, array{(allowNull ? ", or null" : string.Empty)}.");
    }

    private JsonNode? SerializeValue(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is JsonNode node)
        {
            return node.DeepClone();
        }

        if (value is JsonElement element)
        {
            return JsonNode.Parse(element.GetRawText());
        }

        return JsonSerializer.SerializeToNode(value, _serializerOptions);
    }

    private void AddIfPresent(JsonObject body, string name, object? value)
    {
        if (value is not null)
        {
            var node = SerializeValue(value);
            if (name is "provider" or "trace" && node is not JsonObject)
            {
                throw new ArgumentException($"{name} must be a JSON object.");
            }

            body[name] = node;
        }
    }

    private void MergeRequestProperties(
        JsonObject body,
        IReadOnlyDictionary<string, object?>? properties)
    {
        if (properties is null)
        {
            return;
        }

        foreach (var pair in properties)
        {
            body[pair.Key] = SerializeValue(pair.Value);
        }
    }

    private void AddAuthenticationHeader(HttpRequestMessage request)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return;
        }

        var value = string.IsNullOrWhiteSpace(_options.ApiKeyScheme)
            ? _options.ApiKey
            : $"{_options.ApiKeyScheme} {_options.ApiKey}";

        request.Headers.Remove(_options.ApiKeyHeaderName);
        request.Headers.TryAddWithoutValidation(_options.ApiKeyHeaderName, value);
    }
}
