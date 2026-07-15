using System.Runtime.CompilerServices;
using ExpandOpenAI.AgentFramework;
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

internal sealed class TestTokenCompressor(
    IReadOnlyList<ChatMessage> result,
    bool shouldCompress = false,
    IReadOnlyList<MemoryEntry>? sessionMemories = null,
    IReadOnlyList<MemoryEntry>? globalMemories = null) : ITokenCompressor
{
    public int CallCount { get; private set; }

    public TokenCompressionContext? LastContext { get; private set; }

    public bool ShouldCompress(IReadOnlyList<ChatMessage> messages)
    {
        return shouldCompress;
    }

    public ValueTask<TokenCompressionResult> CompressAsync(
        TokenCompressionContext context,
        IChatClient chatClient,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        LastContext = context;
        return new ValueTask<TokenCompressionResult>(new TokenCompressionResult(
            result,
            sessionMemories,
            globalMemories));
    }
}
