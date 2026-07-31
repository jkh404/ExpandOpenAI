namespace ExpandVectorStore.Qdrant;

/// <summary>
/// Options for cursor-based Qdrant scroll operations.
/// </summary>
public sealed class QdrantScrollOptions
{
    /// <summary>
    /// Number of records requested per Qdrant scroll page.
    /// </summary>
    public int BatchSize { get; init; } = 256;

    /// <summary>
    /// Number of matching records to skip before yielding results.
    /// </summary>
    public int Skip { get; init; }

    /// <summary>
    /// Maximum number of records to yield. A null value reads all matching records.
    /// </summary>
    public int? Top { get; init; }

    /// <summary>
    /// Whether vectors should be returned with each record.
    /// </summary>
    public bool IncludeVectors { get; init; }
}
