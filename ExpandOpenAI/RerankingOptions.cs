using Microsoft.Extensions.AI;

namespace ExpandOpenAI;

/// <summary>
/// Per-request options for reranking.
/// </summary>
public class RerankingOptions
{
    public string? ModelId { get; init; }

    public int? TopN { get; init; }

    public string? Instruct { get; init; }

    public AdditionalPropertiesDictionary? AdditionalProperties { get; init; }
}
