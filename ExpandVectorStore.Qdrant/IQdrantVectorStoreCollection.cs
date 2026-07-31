using System.Linq.Expressions;
using Microsoft.Extensions.VectorData;

namespace ExpandVectorStore.Qdrant;

/// <summary>
/// Qdrant-specific operations that are not currently represented by
/// <see cref="VectorStoreCollection{TKey, TRecord}"/>.
/// </summary>
public interface IQdrantVectorStoreCollection<TKey, TRecord>
    where TKey : notnull
    where TRecord : class
{
    /// <summary>
    /// Validates that the existing Qdrant collection matches the configured vector shape and payload indexes.
    /// </summary>
    Task ValidateCollectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates any missing payload indexes for data properties marked as indexed.
    /// </summary>
    Task EnsurePayloadIndexesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes payload indexes managed by this collection definition.
    /// </summary>
    Task DeletePayloadIndexesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Scrolls every matching record using Qdrant's cursor-based scroll API.
    /// </summary>
    IAsyncEnumerable<TRecord> ScrollAsync(
        Expression<Func<TRecord, bool>>? filter = null,
        QdrantScrollOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all records matching a translated Qdrant filter.
    /// </summary>
    Task DeleteAsync(Expression<Func<TRecord, bool>> filter, CancellationToken cancellationToken = default);
}
