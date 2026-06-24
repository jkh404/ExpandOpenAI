using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.Internal;

internal sealed class OpenAICompatibleRerankResponseParser
{
    public RerankingResponse ParseResponse(JsonElement root)
    {
        var resultsElement = GetResultsElement(root);
        var results = new List<RerankingResult>();
        var position = 0;

        foreach (var item in resultsElement.EnumerateArray())
        {
            var index = OpenAICompatibleJsonHelpers.GetInt32(item, "index") ?? position;
            var relevanceScore = GetRequiredDouble(item, "relevance_score", "score");
            results.Add(new RerankingResult(index, relevanceScore)
            {
                Document = ParseDocument(item),
                AdditionalProperties = OpenAICompatibleJsonHelpers.CollectAdditionalProperties(
                    item,
                    "document",
                    "index",
                    "relevance_score",
                    "score"),
            });

            position++;
        }

        return new RerankingResponse(results)
        {
            Id = OpenAICompatibleJsonHelpers.GetString(root, "id"),
            Object = OpenAICompatibleJsonHelpers.GetString(root, "object"),
            ModelId = OpenAICompatibleJsonHelpers.GetString(root, "model"),
            Usage = ParseUsage(root),
            AdditionalProperties = OpenAICompatibleJsonHelpers.CollectAdditionalProperties(
                root,
                "id",
                "object",
                "model",
                "results",
                "usage"),
        };
    }

    private static JsonElement GetResultsElement(JsonElement root)
    {
        if (root.TryGetProperty("results", out var resultsElement) && resultsElement.ValueKind == JsonValueKind.Array)
        {
            return resultsElement;
        }

        throw new JsonException("Rerank response does not contain a results array.");
    }

    private static RerankingDocument? ParseDocument(JsonElement item)
    {
        if (!item.TryGetProperty("document", out var documentElement) || documentElement.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (documentElement.ValueKind == JsonValueKind.String)
        {
            return new RerankingDocument
            {
                Text = documentElement.GetString(),
            };
        }

        if (documentElement.ValueKind == JsonValueKind.Object)
        {
            return new RerankingDocument
            {
                Text = OpenAICompatibleJsonHelpers.GetString(documentElement, "text"),
                AdditionalProperties = OpenAICompatibleJsonHelpers.CollectAdditionalProperties(documentElement, "text"),
            };
        }

        return new RerankingDocument
        {
            AdditionalProperties = new AdditionalPropertiesDictionary(
                new Dictionary<string, object?>
                {
                    ["value"] = documentElement.Clone(),
                }),
        };
    }

    private static UsageDetails? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usageElement) || usageElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new UsageDetails
        {
            InputTokenCount = OpenAICompatibleJsonHelpers.GetInt64(usageElement, "prompt_tokens"),
            TotalTokenCount = OpenAICompatibleJsonHelpers.GetInt64(usageElement, "total_tokens"),
        };
    }

    private static double GetRequiredDouble(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Number)
            {
                return property.GetDouble();
            }

            if (property.ValueKind == JsonValueKind.String &&
                double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        throw new JsonException($"Rerank result does not contain a numeric {propertyNames[0]} property.");
    }
}
