using System.Collections;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI;

/// <summary>
/// Response returned by an OpenAI-compatible reranking endpoint.
/// </summary>
public sealed class RerankingResponse : IReadOnlyList<RerankingResult>
{
    private readonly IReadOnlyList<RerankingResult> _results;

    public RerankingResponse(IEnumerable<RerankingResult> results)
    {
        _results = results.ToList();
    }

    public string? Id { get; init; }

    public string? Object { get; init; }

    public string? ModelId { get; init; }

    public UsageDetails? Usage { get; init; }

    public AdditionalPropertiesDictionary? AdditionalProperties { get; init; }

    public IReadOnlyList<RerankingResult> Results => _results;

    public int Count => _results.Count;

    public RerankingResult this[int index] => _results[index];

    public IEnumerator<RerankingResult> GetEnumerator()
    {
        return _results.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}

/// <summary>
/// A single reranked document result.
/// </summary>
public sealed class RerankingResult
{
    public RerankingResult(int index, double relevanceScore)
    {
        Index = index;
        RelevanceScore = relevanceScore;
    }

    public int Index { get; }

    public double RelevanceScore { get; }

    public RerankingDocument? Document { get; init; }

    public AdditionalPropertiesDictionary? AdditionalProperties { get; init; }
}

/// <summary>
/// Document payload returned with a reranking result when the service includes it.
/// </summary>
public sealed class RerankingDocument
{
    public string? Text { get; init; }

    public AdditionalPropertiesDictionary? AdditionalProperties { get; init; }
}
