using Microsoft.Extensions.AI;

namespace ExpandOpenAI;

public class OpenAICompatibleChatClientOptions : OpenAICompatibleChatOptions
{
    public const string ApiKeyEnvironmentVariable = "OPENAI_API_KEY";
    public const string ModelEnvironmentVariable = "OPENAI_MODEL";
    public const string EndpointEnvironmentVariable = "OPENAI_ENDPOINT";
    public const string RequestPathEnvironmentVariable = "OPENAI_REQUEST_PATH";

    public OpenAICompatibleChatClientOptions()
        : base("chat/completions")
    {
    }

    protected OpenAICompatibleChatClientOptions(OpenAICompatibleChatClientOptions other)
        : base(other)
    {
    }

    public override ChatOptions Clone() => new OpenAICompatibleChatClientOptions(this);

    public static OpenAICompatibleChatClientOptions FromEnvironment()
    {
        var endpointValue = GetRequiredEnvironmentVariable(EndpointEnvironmentVariable);
        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException($"环境变量 {EndpointEnvironmentVariable} 不是有效的绝对 URI: {endpointValue}");
        }

        return new OpenAICompatibleChatClientOptions
        {
            ModelId = GetRequiredEnvironmentVariable(ModelEnvironmentVariable),
            ApiKey = GetRequiredEnvironmentVariable(ApiKeyEnvironmentVariable),
            Endpoint = endpoint,
            RequestPath = Environment.GetEnvironmentVariable(RequestPathEnvironmentVariable) ?? "chat/completions",
        };
    }

    private static string GetRequiredEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        throw new InvalidOperationException($"环境变量 {name} 未设置。");
    }
}
