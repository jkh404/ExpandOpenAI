using System.Runtime.CompilerServices;
using ExpandOpenAI.AgentBase;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.Tests.AgentBase;

internal sealed class TestChatClient : IChatClient
{
    public int ResponseCallCount { get; private set; }

    public Func<IReadOnlyList<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>>? ResponseHandler { get; init; }

    public Func<IReadOnlyList<ChatMessage>, ChatOptions?, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>? StreamingHandler { get; init; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ResponseCallCount++;
        return ResponseHandler?.Invoke(messages.ToList().AsReadOnly(), options, cancellationToken)
            ?? Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return StreamingHandler?.Invoke(messages.ToList().AsReadOnly(), options, cancellationToken)
            ?? EmptyStream(cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> EmptyStream(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }
}

internal sealed class TestTokenCompressor(IReadOnlyList<ChatMessage> result) : ITokenCompressor
{
    public int CallCount { get; private set; }

    public ValueTask<IReadOnlyList<ChatMessage>> CompressAsync(
        IReadOnlyList<ChatMessage> messages,
        IChatClient chatClient,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        return new ValueTask<IReadOnlyList<ChatMessage>>(result);
    }
}
