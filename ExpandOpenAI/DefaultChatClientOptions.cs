using System.ComponentModel;

namespace ExpandOpenAI;

[Obsolete("Use OpenAICompatibleChatClientOptions instead.")]
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class DefaultChatClientOptions : OpenAICompatibleChatClientOptions
{
    public new static DefaultChatClientOptions FromEnvironment()
    {
        var options = OpenAICompatibleChatClientOptions.FromEnvironment();

        return new DefaultChatClientOptions
        {
            Endpoint = options.Endpoint,
            RequestPath = options.RequestPath,
            ModelId = options.ModelId,
            ApiKey = options.ApiKey,
            ApiKeyHeaderName = options.ApiKeyHeaderName,
            ApiKeyScheme = options.ApiKeyScheme,
            SerializerOptions = options.SerializerOptions,
            Headers = options.Headers,
            RequestBody = options.RequestBody,
            ConfigureRequest = options.ConfigureRequest,
            ConfigureRequestBody = options.ConfigureRequestBody,
        };
    }
}
