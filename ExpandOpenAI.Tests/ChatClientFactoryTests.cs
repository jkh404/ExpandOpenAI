namespace ExpandOpenAI.Tests;

public class ChatClientFactoryTests
{
    [Fact]
    public void Create_ResponsesProtocol_ReturnsResponsesClientWithDefaultPath()
    {
        using var client = ChatClientFactory.Create(
            "test-model",
            "test-key",
            new Uri("https://example.test/v1/"),
            OpenAICompatibleChatProtocol.Responses);

        var responsesClient = Assert.IsType<OpenAICompatibleResponsesClient>(client);
        var options = Assert.IsType<OpenAICompatibleResponsesClientOptions>(
            responsesClient.GetService(typeof(OpenAICompatibleResponsesClientOptions)));
        Assert.IsAssignableFrom<OpenAICompatibleChatOptions>(options);
        Assert.Equal("responses", options.RequestPath);
    }

    [Fact]
    public void Create_ChatCompletionsProtocol_ReturnsChatClientWithDefaultPath()
    {
        using var client = ChatClientFactory.Create(
            "test-model",
            "test-key",
            new Uri("https://example.test/v1/"),
            OpenAICompatibleChatProtocol.ChatCompletions);

        var chatClient = Assert.IsType<OpenAICompatibleChatClient>(client);
        var options = Assert.IsType<OpenAICompatibleChatClientOptions>(
            chatClient.GetService(typeof(OpenAICompatibleChatClientOptions)));
        Assert.IsAssignableFrom<OpenAICompatibleChatOptions>(options);
        Assert.Equal("chat/completions", options.RequestPath);
    }

    [Fact]
    public void Create_DefaultPath_ReturnsChatCompletionsClient()
    {
        using var client = ChatClientFactory.Create(
            "test-model",
            "test-key",
            new Uri("https://example.test/v1/"));

        Assert.IsType<OpenAICompatibleChatClient>(client);
    }

    [Fact]
    public void Create_AutoWithResponsesEndpoint_ReturnsResponsesClientWithoutAppendingPath()
    {
        var endpoint = new Uri("https://example.test/v1/responses?api-version=2026-01-01");

        using var client = ChatClientFactory.Create(
            "test-model",
            "test-key",
            endpoint,
            OpenAICompatibleChatProtocol.Auto);

        var responsesClient = Assert.IsType<OpenAICompatibleResponsesClient>(client);
        var options = Assert.IsType<OpenAICompatibleResponsesClientOptions>(
            responsesClient.GetService(typeof(OpenAICompatibleResponsesClientOptions)));
        Assert.Equal(endpoint, options.Endpoint);
        Assert.Equal(string.Empty, options.RequestPath);
    }

    [Fact]
    public void Create_AutoWithChatCompletionsEndpoint_ReturnsChatClientWithoutAppendingPath()
    {
        var endpoint = new Uri("https://example.test/v1/CHAT/COMPLETIONS/");

        using var client = ChatClientFactory.Create(
            "test-model",
            "test-key",
            endpoint,
            OpenAICompatibleChatProtocol.Auto);

        var chatClient = Assert.IsType<OpenAICompatibleChatClient>(client);
        var options = Assert.IsType<OpenAICompatibleChatClientOptions>(
            chatClient.GetService(typeof(OpenAICompatibleChatClientOptions)));
        Assert.Equal(endpoint, options.Endpoint);
        Assert.Equal(string.Empty, options.RequestPath);
    }

    [Fact]
    public void Create_AutoWithBaseEndpoint_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => ChatClientFactory.Create(
            "test-model",
            "test-key",
            new Uri("https://example.test/v1/"),
            OpenAICompatibleChatProtocol.Auto));

        Assert.Equal("endpoint", exception.ParamName);
    }

    [Fact]
    public void Create_AutoWithRelativeEndpoint_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => ChatClientFactory.Create(
            "test-model",
            "test-key",
            new Uri("v1/responses", UriKind.Relative),
            OpenAICompatibleChatProtocol.Auto));

        Assert.Equal("endpoint", exception.ParamName);
    }

    [Fact]
    public void Create_UnknownProtocol_Throws()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => ChatClientFactory.Create(
            "test-model",
            "test-key",
            new Uri("https://example.test/v1/"),
            (OpenAICompatibleChatProtocol)999));

        Assert.Equal("protocol", exception.ParamName);
    }

    [Fact]
    public void Options_KeepProtocolSpecificDefaultPaths()
    {
        Assert.Equal("chat/completions", new OpenAICompatibleChatClientOptions().RequestPath);
        Assert.Equal("responses", new OpenAICompatibleResponsesClientOptions().RequestPath);
    }

    [Fact]
    public void Clone_PreservesConcreteTypeAndCopiesCommonDictionaries()
    {
        var options = new OpenAICompatibleResponsesClientOptions
        {
            Endpoint = new Uri("https://example.test/v1/"),
            ModelId = "test-model",
            Headers = new Dictionary<string, string> { ["X-Test"] = "value" },
            RequestBody = new Dictionary<string, object?> { ["vendor"] = true },
            Store = true,
        };

        var clone = Assert.IsType<OpenAICompatibleResponsesClientOptions>(options.Clone());

        Assert.IsAssignableFrom<OpenAICompatibleChatOptions>(clone);
        Assert.NotSame(options.Headers, clone.Headers);
        Assert.NotSame(options.RequestBody, clone.RequestBody);
        Assert.Equal("value", clone.Headers["X-Test"]);
        Assert.Equal(true, clone.RequestBody!["vendor"]);
        Assert.True(clone.Store);
    }
}
