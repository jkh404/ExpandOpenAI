using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExpandOpenAI.Tests;

public sealed class DecisionsAIClientTests
{
    [Fact]
    public void Options_RequireEndpointAndModelAndDefaultToSystemOnePath()
    {
        var options = new DecisionsAIClientOptions();

        Assert.Null(options.Endpoint);
        Assert.Equal("v1/systemone", options.RequestPath);
        Assert.Null(options.ModelId);
        using var httpClient = new HttpClient();
        Assert.Throws<ArgumentNullException>(() => new DecisionsAIClient(httpClient, options));
        options.Endpoint = new Uri("https://gateway.example.test/api");
        Assert.Throws<ArgumentException>(() => new DecisionsAIClient(httpClient, options));

        using var client = new DecisionsAIClient("decision-model", "gateway-key", options.Endpoint);
        var clientOptions = Assert.IsType<DecisionsAIClientOptions>(
            client.GetService(typeof(DecisionsAIClientOptions)));
        Assert.Equal("v1/systemone", clientOptions.RequestPath);
    }

    [Fact]
    public async Task Request_UsesDecisionsWireFormatAndSupportsStructuredPrimitives()
    {
        string? body = null;
        Uri? uri = null;
        string? authorization = null;
        using var handler = new DelegateHttpMessageHandler(async (_, request, _) =>
        {
            uri = request.RequestUri;
            authorization = request.Headers.Authorization?.ToString();
            body = await request.Content!.ReadAsStringAsync();
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("application/json", request.Content.Headers.ContentType?.MediaType);
            return JsonResponse(MinimalResponse);
        });

        using var client = new DecisionsAIClient(
            handler,
            new DecisionsAIClientOptions
            {
                Endpoint = new Uri("https://gateway.example.test/api"),
                ModelId = "decision-model",
                ApiKey = "test-key",
                RequestPath = "custom/decisions",
                RetryOptions = NoRetryOptions(),
            });

        var questions = new Dictionary<string, DecisionsQuestion>
        {
            ["is_bug"] = new DecisionsNoulQuestion
            {
                Instructions = new { question = "Is this a bug?", focus = "Unexpected behavior" },
                Criteria = new DecisionsNoulCriteria
                {
                    True = "Broken or unexpected product behavior",
                    False = new[] { "A question", "A feature request" },
                },
            },
            ["team"] = new DecisionsChoiceQuestion
            {
                Instructions = "Which team owns this?",
                Criteria = new Dictionary<string, object?>
                {
                    ["frontend"] = new { what = "Rendering and browser issues" },
                    ["billing"] = null,
                },
            },
            ["urgency"] = new DecisionsScoreQuestion
            {
                Instructions = "How urgent is this?",
                Criteria = new object?[] { "Can wait", new { what = "Fix this week" }, "Blocking" },
            },
        };

        var response = await client.GetResponseAsync(
            new DecisionsRequest
            {
                State = new { ticket = "The checkout page is blank." },
                Questions = questions,
                User = "user-1",
                SessionId = "session-1",
                Trace = new { trace_name = "triage" },
            });

        Assert.Equal("https://gateway.example.test/api/custom/decisions", uri?.ToString());
        Assert.Equal("Bearer test-key", authorization);
        Assert.NotNull(body);
        using var document = JsonDocument.Parse(body!);
        var root = document.RootElement;
        Assert.Equal("decision-model", root.GetProperty("model").GetString());
        Assert.Equal("The checkout page is blank.", root.GetProperty("state").GetProperty("ticket").GetString());
        Assert.Equal("noul", root.GetProperty("questions").GetProperty("is_bug").GetProperty("type").GetString());
        Assert.Equal("Is this a bug?", root.GetProperty("questions").GetProperty("is_bug")
            .GetProperty("instructions").GetProperty("question").GetString());
        Assert.Equal(2, root.GetProperty("questions").GetProperty("is_bug").GetProperty("criteria")
            .GetProperty("false").GetArrayLength());
        Assert.Equal("frontend", root.GetProperty("questions").GetProperty("team").GetProperty("criteria").EnumerateObject().First().Name);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("questions").GetProperty("team")
            .GetProperty("criteria").GetProperty("billing").ValueKind);
        Assert.Equal(3, root.GetProperty("questions").GetProperty("urgency").GetProperty("criteria").GetArrayLength());
        Assert.Equal("session-1", root.GetProperty("session_id").GetString());
        Assert.Equal("triage", root.GetProperty("trace").GetProperty("trace_name").GetString());
    }

    [Fact]
    public async Task Response_MapsTypedAnswersUsageAndUnknownFields()
    {
        using var handler = new DelegateHttpMessageHandler((_, _, _) => Task.FromResult(JsonResponse("""
            {
              "id":"gen-dec-1",
              "model":"decision-model-v2",
              "provider":"test-provider",
              "answers":{
                "refund":{"type":"noul","noul":0.98},
                "team":{"type":"choice","choice":"billing","confidence":0.8,"probabilities":{"billing":0.8,"technical":0.2}},
                "urgency":{"type":"score","score":1.4,"confidence":0.35,"legend":{"0":"low","1":{"what":"high"},"2":["urgent"],"3":null},"probabilities":{"0":0.1,"1":0.4,"2":0.5}},
                "future":{"type":"new_type","value":7}
              },
              "usage":{"input_tokens":275,"output_tokens":20,"cost":0.00003,"cached_tokens":4},
              "request_trace":"preserved"
            }
            """)));
        using var client = new DecisionsAIClient(handler, CreateOptions());

        var response = await client.GetResponseAsync(
            new DecisionsRequest
            {
                State = "I was charged twice.",
                Questions = new Dictionary<string, DecisionsQuestion>
                {
                    ["refund"] = new DecisionsNoulQuestion { Instructions = "Is a refund requested?" },
                },
            });

        Assert.Equal("gen-dec-1", response.Id);
        Assert.Equal("test-provider", response.Provider);
        Assert.Equal("decision-model-v2", response.ModelId);
        Assert.Equal(0.98, Assert.IsType<DecisionsNoulAnswer>(response.Answers["refund"]).Noul);
        var choice = Assert.IsType<DecisionsChoiceAnswer>(response.Answers["team"]);
        Assert.Equal("billing", choice.Choice);
        Assert.Equal(0.8, choice.Confidence);
        Assert.Equal(0.2, choice.Probabilities!["technical"]);
        var score = Assert.IsType<DecisionsScoreAnswer>(response.Answers["urgency"]);
        Assert.Equal(1.4, score.Score);
        Assert.Equal(0.35, score.Confidence);
        Assert.Equal("high", score.Legend!["1"].GetProperty("what").GetString());
        Assert.Equal("urgent", score.Legend["2"][0].GetString());
        Assert.Equal(JsonValueKind.Null, score.Legend["3"].ValueKind);
        var unknown = Assert.IsType<DecisionsUnknownAnswer>(response.Answers["future"]);
        Assert.Equal(7, unknown.Raw.GetProperty("value").GetInt32());
        Assert.Equal(275, response.Usage!.InputTokens);
        Assert.Equal(20, response.Usage.OutputTokens);
        Assert.Equal(0.00003, response.Usage.Cost);
        Assert.Equal(295, response.Usage.TotalTokens);
        Assert.Equal(4, ((JsonElement)response.Usage.AdditionalProperties!["cached_tokens"]!).GetInt32());
        Assert.Equal("preserved", ((JsonElement)response.AdditionalProperties!["request_trace"]!).GetString());
    }

    [Fact]
    public async Task InvalidScoreQuestion_IsRejectedBeforeSending()
    {
        using var handler = new DelegateHttpMessageHandler((_, _, _) =>
            Task.FromResult(JsonResponse("{}")));
        using var client = new DecisionsAIClient(handler, CreateOptions());

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => client.GetResponseAsync(
            new DecisionsRequest
            {
                State = "ticket",
                Questions = new Dictionary<string, DecisionsQuestion>
                {
                    ["severity"] = new DecisionsScoreQuestion { Instructions = "How severe?", Criteria = new[] { "low" } },
                },
            }));

        Assert.Contains("between 2 and 10", exception.Message);
        Assert.Equal(0, handler.AttemptCount);
    }

    [Theory]
    [InlineData("https://gateway.example.test/api", "custom/decisions", "https://gateway.example.test/api/custom/decisions")]
    [InlineData("https://gateway.example.test/api/", "v1/systemone", "https://gateway.example.test/api/v1/systemone")]
    [InlineData("https://service.example.test", "v1/systemone", "https://service.example.test/v1/systemone")]
    [InlineData("https://example.test/systemone?version=1", "", "https://example.test/systemone?version=1")]
    [InlineData("https://example.test", "https://proxy.test/decision", "https://proxy.test/decision")]
    public async Task Request_RoutesConfiguredEndpoints(string endpoint, string path, string expected)
    {
        using var handler = new DelegateHttpMessageHandler((_, request, _) =>
        {
            Assert.Equal(expected, request.RequestUri?.AbsoluteUri);
            return Task.FromResult(JsonResponse(MinimalResponse));
        });
        var options = CreateOptions();
        options.Endpoint = new Uri(endpoint);
        options.RequestPath = path;
        using var client = new DecisionsAIClient(handler, options);
        await client.GetResponseAsync(CreateRequest());
    }

    [Fact]
    public async Task Request_AllowsAdvancedNullAndArrayEntriesWithoutChangingInput()
    {
        using var handler = new DelegateHttpMessageHandler(async (_, request, _) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = document.RootElement;
            Assert.Equal(JsonValueKind.Array, root.GetProperty("state").ValueKind);
            var questions = root.GetProperty("questions");
            Assert.Equal(JsonValueKind.Null, questions.GetProperty("noul").GetProperty("instructions").ValueKind);
            Assert.False(questions.GetProperty("noul").TryGetProperty("criteria", out _));
            Assert.Equal(JsonValueKind.Null, questions.GetProperty("noul_criteria").GetProperty("criteria").GetProperty("true").ValueKind);
            Assert.Equal(JsonValueKind.Array, questions.GetProperty("choice").GetProperty("instructions").ValueKind);
            Assert.Equal(JsonValueKind.Null, questions.GetProperty("score").GetProperty("criteria")[0].ValueKind);
            return JsonResponse(MinimalResponse);
        });
        var options = CreateOptions();
        options.Endpoint = new Uri("https://service.example.test");
        options.RequestPath = "v1/systemone";
        using var client = new DecisionsAIClient(handler, options);
        var request = new DecisionsRequest
        {
            State = new object[] { "conversation", new { text = "ticket" } },
            Questions = new Dictionary<string, DecisionsQuestion>
            {
                ["noul"] = new DecisionsNoulQuestion(),
                ["noul_criteria"] = new DecisionsNoulQuestion { Criteria = new DecisionsNoulCriteria() },
                ["choice"] = new DecisionsChoiceQuestion
                {
                    Instructions = new[] { "Read the ticket", "Choose the team" },
                    Criteria = new Dictionary<string, object?> { ["other"] = null },
                },
                ["score"] = new DecisionsScoreQuestion { Criteria = new object?[] { null, new[] { "high" } } },
            },
        };
        await client.GetResponseAsync(request);
        Assert.Null(request.ModelId);
    }

    [Fact]
    public async Task Request_RetriesWithReusableJsonNodesAndHonorsExtensions()
    {
        var shared = JsonNode.Parse("""{"ticket":"duplicate charge"}""")!;
        var payloads = new List<string>();
        using var handler = new DelegateHttpMessageHandler(async (attempt, request, _) =>
        {
            payloads.Add(await request.Content!.ReadAsStringAsync());
            Assert.Equal("custom-key", request.Headers.GetValues("X-API-Key").Single());
            Assert.Equal("my-app", request.Headers.GetValues("X-Title").Single());
            Assert.Equal("hook", request.Headers.GetValues("X-Hook").Single());
            return attempt == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : JsonResponse(MinimalResponse);
        });
        var options = CreateOptions();
        options.ApiKey = "custom-key";
        options.ApiKeyHeaderName = "X-API-Key";
        options.ApiKeyScheme = null;
        options.Headers = new Dictionary<string, string> { ["X-Title"] = "my-app" };
        options.RequestBody = new Dictionary<string, object?> { ["model"] = "ignored", ["extension"] = "global" };
        options.ConfigureRequest = (message, _) => message.Headers.Add("X-Hook", "hook");
        options.ConfigureRequestBody = (body, _) => body["hook"] = true;
        options.RetryOptions = new OpenAICompatibleHttpRetryOptions
        {
            MaxRetryAttempts = 1, InitialDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero,
        };
        using var client = new DecisionsAIClient(handler, options);
        var request = CreateRequest();
        request.ModelId = "override-model";
        request.State = shared;
        request.Provider = new { only = new[] { "test-provider" } };
        request.AdditionalProperties = new Dictionary<string, object?>
        {
            ["state"] = "ignored",
            ["questions"] = null,
            ["extension"] = "request",
            ["context"] = shared,
        };
        await client.GetResponseAsync(request);
        await client.GetResponseAsync(request);

        Assert.Equal(3, handler.AttemptCount);
        Assert.All(payloads, payload => Assert.Equal(payloads[0], payload));
        Assert.Null(shared.Parent);
        using var document = JsonDocument.Parse(payloads[0]);
        var root = document.RootElement;
        Assert.Equal("override-model", root.GetProperty("model").GetString());
        Assert.Equal("duplicate charge", root.GetProperty("state").GetProperty("ticket").GetString());
        Assert.True(root.GetProperty("questions").TryGetProperty("is_bug", out _));
        Assert.Equal("request", root.GetProperty("extension").GetString());
        Assert.True(root.GetProperty("hook").GetBoolean());
        Assert.Equal("test-provider", root.GetProperty("provider").GetProperty("only")[0].GetString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("true")]
    public async Task Request_RejectsUnsupportedStateBeforeSending(string state)
    {
        using var handler = new DelegateHttpMessageHandler((_, _, _) => Task.FromResult(JsonResponse(MinimalResponse)));
        using var client = new DecisionsAIClient(handler, CreateOptions());
        var request = CreateRequest();
        request.State = JsonSerializer.Deserialize<JsonElement>(state);
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetResponseAsync(request));
        Assert.Equal(0, handler.AttemptCount);
    }

    [Theory]
    [InlineData("""{"type":"noul"}""")]
    [InlineData("""{"type":"noul","noul":1.5}""")]
    [InlineData("""{"type":"choice","choice":5}""")]
    [InlineData("""{"type":"choice","choice":"team","probabilities":{"team":"invalid"}}""")]
    [InlineData("""{"type":"score","score":null}""")]
    [InlineData("""{"type":"score","score":1,"legend":[]}""")]
    [InlineData("""{"type":"score","score":1,"confidence":-0.1}""")]
    [InlineData("null")]
    public async Task Response_RejectsMalformedKnownAnswers(string answer)
    {
        using var handler = new DelegateHttpMessageHandler((_, _, _) =>
            Task.FromResult(JsonResponse("{\"answers\":{\"is_bug\":" + answer + "}}")));
        using var client = new DecisionsAIClient(handler, CreateOptions());
        await Assert.ThrowsAsync<JsonException>(() => client.GetResponseAsync(CreateRequest()));
    }

    [Theory]
    [InlineData("empty_questions")]
    [InlineData("empty_choice")]
    [InlineData("too_many_choices")]
    [InlineData("too_many_levels")]
    [InlineData("numeric_instruction")]
    [InlineData("boolean_criterion")]
    public async Task Request_RejectsInvalidQuestionShapesBeforeSending(string scenario)
    {
        using var handler = new DelegateHttpMessageHandler((_, _, _) => Task.FromResult(JsonResponse(MinimalResponse)));
        using var client = new DecisionsAIClient(handler, CreateOptions());
        var questions = new Dictionary<string, DecisionsQuestion>();
        switch (scenario)
        {
            case "empty_choice":
            case "too_many_choices":
                questions["choice"] = new DecisionsChoiceQuestion
                {
                    Criteria = Enumerable.Range(0, scenario == "empty_choice" ? 0 : 256)
                        .ToDictionary(index => index.ToString(), _ => (object?)"option"),
                };
                break;
            case "too_many_levels":
                questions["score"] = new DecisionsScoreQuestion { Criteria = Enumerable.Repeat<object?>("level", 11).ToArray() };
                break;
            case "numeric_instruction":
                questions["noul"] = new DecisionsNoulQuestion { Instructions = 123 };
                break;
            case "boolean_criterion":
                questions["noul"] = new DecisionsNoulQuestion
                {
                    Criteria = new DecisionsNoulCriteria { True = true, False = "false description" },
                };
                break;
        }

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetResponseAsync("state", questions));
        Assert.Equal(0, handler.AttemptCount);
    }

    [Fact]
    public async Task Response_AcceptsMinimalAnswersWithoutOptionalMetadata()
    {
        using var handler = new DelegateHttpMessageHandler((_, _, _) => Task.FromResult(JsonResponse("""
            {"model":"decision-model","answers":{"choice":{"type":"choice","choice":"team"},"score":{"type":"score","score":0}}}
            """)));
        using var client = new DecisionsAIClient(handler, CreateOptions());
        var response = await client.GetResponseAsync(CreateRequest());
        Assert.Null(response.Id);
        Assert.Null(response.Provider);
        Assert.Null(response.Usage);
        Assert.Null(Assert.IsType<DecisionsChoiceAnswer>(response.Answers["choice"]).Probabilities);
        var score = Assert.IsType<DecisionsScoreAnswer>(response.Answers["score"]);
        Assert.Equal(0, score.Score);
        Assert.Null(score.Confidence);
        Assert.Null(score.Legend);
    }

    [Fact]
    public async Task Client_PreservesHttpErrorBodyWithoutRetryingBadRequests()
    {
        using var handler = new DelegateHttpMessageHandler((_, _, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"message":"invalid criteria"}}"""),
            }));
        var options = CreateOptions();
        options.RetryOptions = new OpenAICompatibleHttpRetryOptions { InitialDelay = TimeSpan.Zero };
        using var client = new DecisionsAIClient(handler, options);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync(CreateRequest()));
        Assert.Contains("400", error.Message);
        Assert.Contains("invalid criteria", error.Message);
        Assert.Equal(1, handler.AttemptCount);
    }

    [Fact]
    public async Task Client_CancelsInFlightWithoutRetryingAndKeepsInjectedClientAlive()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new DelegateHttpMessageHandler(async (attempt, _, token) =>
        {
            if (attempt == 1)
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.Infinite, token);
            }
            return JsonResponse(MinimalResponse);
        });
        using var httpClient = new HttpClient(handler);
        var options = CreateOptions();
        options.RetryOptions = new OpenAICompatibleHttpRetryOptions { InitialDelay = TimeSpan.Zero };
        var client = new DecisionsAIClient(httpClient, options);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetResponseAsync(CreateRequest(), cancellation.Token));
        Assert.Equal(1, handler.AttemptCount);
        client.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetResponseAsync(CreateRequest()));
        using var response = await httpClient.GetAsync("https://example.test");
        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Client_RejectsNullRequestAndAlreadyCancelledCallsBeforeSending()
    {
        using var handler = new DelegateHttpMessageHandler((_, _, _) => Task.FromResult(JsonResponse(MinimalResponse)));
        using var client = new DecisionsAIClient(handler, CreateOptions());
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.GetResponseAsync(null!));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetResponseAsync(CreateRequest(), new CancellationToken(canceled: true)));
        Assert.Equal(0, handler.AttemptCount);
    }

    [Fact]
    public void Environment_RequiresExplicitEndpointAndModelAndUsesGenericConfiguration()
    {
        string[] names =
        [
            DecisionsAIClient.ApiKeyEnvironmentVariable, DecisionsAIClient.ModelEnvironmentVariable,
            DecisionsAIClient.EndpointEnvironmentVariable, DecisionsAIClient.RequestPathEnvironmentVariable,
        ];
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, null);
            var missingEndpoint = Assert.Throws<InvalidOperationException>(DecisionsAIClientOptions.FromEnvironment);
            Assert.Contains("DECISIONS_ENDPOINT", missingEndpoint.Message);

            Environment.SetEnvironmentVariable(DecisionsAIClient.EndpointEnvironmentVariable, "https://gateway.example.test/api");
            var missingModel = Assert.Throws<InvalidOperationException>(DecisionsAIClientOptions.FromEnvironment);
            Assert.Contains("DECISIONS_MODEL", missingModel.Message);

            Environment.SetEnvironmentVariable(DecisionsAIClient.ModelEnvironmentVariable, "custom-model");
            var options = DecisionsAIClientOptions.FromEnvironment();
            Assert.Equal("https://gateway.example.test/api", options.Endpoint.AbsoluteUri);
            Assert.Equal("v1/systemone", options.RequestPath);
            Assert.Equal("custom-model", options.ModelId);
            Assert.Null(options.ApiKey);

            Environment.SetEnvironmentVariable(DecisionsAIClient.ApiKeyEnvironmentVariable, "gateway-key");
            Environment.SetEnvironmentVariable(DecisionsAIClient.ModelEnvironmentVariable, "explicit-model");
            Environment.SetEnvironmentVariable(DecisionsAIClient.RequestPathEnvironmentVariable, "custom/decisions");
            var explicitOptions = DecisionsAIClientOptions.FromEnvironment();
            Assert.Equal("explicit-model", explicitOptions.ModelId);
            Assert.Equal("custom/decisions", explicitOptions.RequestPath);
            Assert.Equal("gateway-key", explicitOptions.ApiKey);
            Environment.SetEnvironmentVariable(DecisionsAIClient.EndpointEnvironmentVariable, "/relative");
            Assert.Throws<InvalidOperationException>(DecisionsAIClientOptions.FromEnvironment);
        }
        finally
        {
            foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    private static DecisionsRequest CreateRequest() => new()
    {
        State = "ticket",
        Questions = new Dictionary<string, DecisionsQuestion>
        {
            ["is_bug"] = new DecisionsNoulQuestion { Instructions = "Is this a bug?" },
        },
    };

    private static DecisionsAIClientOptions CreateOptions()
    {
        return new DecisionsAIClientOptions
        {
            Endpoint = new Uri("https://example.test/api"),
            ModelId = "decision-model",
            RetryOptions = NoRetryOptions(),
        };
    }

    private static OpenAICompatibleHttpRetryOptions NoRetryOptions()
    {
        return new OpenAICompatibleHttpRetryOptions
        {
            MaxRetryAttempts = 0,
            InitialDelay = TimeSpan.Zero,
            MaxDelay = TimeSpan.Zero,
        };
    }

    private const string MinimalResponse = """
        {"model":"decision-model","answers":{"is_bug":{"type":"noul","noul":0.1}},"usage":{"input_tokens":1,"output_tokens":1}}
        """;

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

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
