using Microsoft.Extensions.AI;

namespace ExpandOpenAI;

/// <summary>
/// Generates embeddings for multimodal content such as text, images, and videos.
/// </summary>
public interface IMultimodalEmbeddingGenerator : IDisposable
{
    /// <summary>
    /// Generates embeddings for multimodal inputs represented as <see cref="AIContent"/>.
    /// </summary>
    Task<GeneratedEmbeddings<Embedding<float>>> GenerateMultimodalAsync(
        IEnumerable<AIContent> contents,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default);
}
