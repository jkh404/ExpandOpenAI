using System.ComponentModel;

namespace ExpandOpenAI;

[Obsolete("Use OpenAICompatibleChatClient instead.")]
[EditorBrowsable(EditorBrowsableState.Never)]
public class DefaultChatClient : OpenAICompatibleChatClient
{
    public DefaultChatClient()
        : base()
    {
    }

    public DefaultChatClient(DefaultChatClientOptions options)
        : base(options)
    {
    }

    public DefaultChatClient(HttpMessageHandler httpMessageHandler, DefaultChatClientOptions options, bool disposeHandler = true)
        : base(httpMessageHandler, options, disposeHandler)
    {
    }

    public DefaultChatClient(string modelId, string apiKey, Uri endpoint, string requestPath = "chat/completions")
        : base(modelId, apiKey, endpoint, requestPath)
    {
    }
}
