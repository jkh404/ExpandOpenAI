using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.Internal;

internal sealed class OpenAICompatibleEmbeddingResponseParser
{
    public OpenAICompatibleEmbeddingResponseParser()
    {
    }

    public GeneratedEmbeddings<Embedding<float>> ParseResponse(JsonElement root, string? modelId = null)
    {
        if (TryGetEmbeddingsArray(root, "data", out JsonElement dataElement))
        {
            return ParseOpenAIEmbeddingsResponse(root, dataElement, modelId);
        }

        if (TryGetDashScopeEmbeddings(root, out JsonElement dashScopeEmbeddings))
        {
            return ParseDashScopeEmbeddingsResponse(root, dashScopeEmbeddings, modelId);
        }

        throw new JsonException($"Embedding response does not contain a data array or output.embeddings array.HttpBody:{root.GetRawText()}");
    }

    public GeneratedEmbeddings<Embedding<float>> ParseDashScopeMultimodalResponse(
        JsonElement root,
        string? modelId)
    {
        if (!root.TryGetProperty("output", out var outputElement)
            || outputElement.ValueKind != JsonValueKind.Object
            || !outputElement.TryGetProperty("embeddings", out var embeddingsElement)
            || embeddingsElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"DashScope multimodal embedding response does not contain output.embeddings array.HttpBody:{root.GetRawText()}");
        }

        return ParseDashScopeEmbeddingsResponse(root, embeddingsElement, modelId);
    }

    private GeneratedEmbeddings<Embedding<float>> ParseOpenAIEmbeddingsResponse(
        JsonElement root,
        JsonElement dataElement,
        string? modelId)
    {
        string? responseModelId = OpenAICompatibleJsonHelpers.GetString(root, "model") ?? modelId;
        DateTimeOffset? createdAt = OpenAICompatibleJsonHelpers.GetCreatedAt(root);
        List<IndexedEmbedding> indexedEmbeddings = ParseIndexedEmbeddings(dataElement, responseModelId, createdAt);

        return new GeneratedEmbeddings<Embedding<float>>(OrderEmbeddings(indexedEmbeddings))
        {
            Usage = ParseUsage(root),
            AdditionalProperties = OpenAICompatibleJsonHelpers.CollectAdditionalProperties(
                root,
                "object",
                "data",
                "model",
                "created",
                "usage"),
        };
    }

    private GeneratedEmbeddings<Embedding<float>> ParseDashScopeEmbeddingsResponse(
        JsonElement root,
        JsonElement embeddingsElement,
        string? modelId)
    {
        string? responseModelId = OpenAICompatibleJsonHelpers.GetString(root, "model") ?? modelId;
        List<IndexedEmbedding> indexedEmbeddings = ParseIndexedEmbeddings(embeddingsElement, responseModelId, createdAt: null);

        return new GeneratedEmbeddings<Embedding<float>>(OrderEmbeddings(indexedEmbeddings))
        {
            Usage = ParseUsage(root),
            AdditionalProperties = OpenAICompatibleJsonHelpers.CollectAdditionalProperties(
                root,
                "output",
                "usage"),
        };
    }

    private static bool TryGetEmbeddingsArray(JsonElement root, string propertyName, out JsonElement embeddingsElement)
    {
        if (root.TryGetProperty(propertyName, out embeddingsElement) &&
            embeddingsElement.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        embeddingsElement = default;
        return false;
    }

    private static bool TryGetDashScopeEmbeddings(JsonElement root, out JsonElement embeddingsElement)
    {
        if (root.TryGetProperty("output", out JsonElement outputElement) &&
            outputElement.ValueKind == JsonValueKind.Object &&
            outputElement.TryGetProperty("embeddings", out embeddingsElement) &&
            embeddingsElement.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        embeddingsElement = default;
        return false;
    }

    private List<IndexedEmbedding> ParseIndexedEmbeddings(
        JsonElement embeddingsElement,
        string? modelId,
        DateTimeOffset? createdAt)
    {
        var indexedEmbeddings = new List<IndexedEmbedding>();
        var position = 0;

        foreach (var item in embeddingsElement.EnumerateArray())
        {
            var index = OpenAICompatibleJsonHelpers.GetInt32(item, "index") ?? position;
            var embedding = ParseEmbedding(item, modelId, createdAt);
            indexedEmbeddings.Add(new IndexedEmbedding(index, position, embedding));
            position++;
        }

        return indexedEmbeddings;
    }

    private static IEnumerable<Embedding<float>> OrderEmbeddings(IEnumerable<IndexedEmbedding> indexedEmbeddings)
    {
        return indexedEmbeddings
            .OrderBy(static item => item.Index)
            .ThenBy(static item => item.Position)
            .Select(static item => item.Embedding);
    }

    private Embedding<float> ParseEmbedding(JsonElement item, string? modelId, DateTimeOffset? createdAt)
    {
        if (!item.TryGetProperty("embedding", out var embeddingElement))
        {
            throw new JsonException("Embedding item does not contain an embedding property.");
        }

        var vector = embeddingElement.ValueKind switch
        {
            JsonValueKind.Array => ParseFloatArray(embeddingElement),
            JsonValueKind.String => ParseBase64FloatArray(embeddingElement.GetString()),
            _ => throw new JsonException($"Unsupported embedding value kind: {embeddingElement.ValueKind}."),
        };

        return new Embedding<float>(vector)
        {
            ModelId = OpenAICompatibleJsonHelpers.GetString(item, "model") ?? modelId,
            CreatedAt = OpenAICompatibleJsonHelpers.GetCreatedAt(item) ?? createdAt,
            AdditionalProperties = OpenAICompatibleJsonHelpers.CollectAdditionalProperties(
                item,
                "object",
                "embedding",
                "index",
                "model",
                "created"),
        };
    }

    private static float[] ParseFloatArray(JsonElement embeddingElement)
    {
        var vector = new float[embeddingElement.GetArrayLength()];
        var index = 0;

        foreach (var number in embeddingElement.EnumerateArray())
        {
            if (number.ValueKind != JsonValueKind.Number)
            {
                throw new JsonException("Embedding vector contains a non-numeric value.");
            }

            vector[index++] = (float)number.GetDouble();
        }

        return vector;
    }

    private static float[] ParseBase64FloatArray(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<float>();
        }

        var bytes = Convert.FromBase64String(value);
        if (bytes.Length % sizeof(float) != 0)
        {
            throw new JsonException("Base64 embedding length is not a multiple of 4 bytes.");
        }

        var vector = new float[bytes.Length / sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = BitConverter.ToSingle(bytes, i * sizeof(float));
        }

        return vector;
    }

    private static UsageDetails? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usageElement) || usageElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        UsageDetails? dashScopeUsage = ParseDashScopeUsage(usageElement);
        if (dashScopeUsage is not null)
        {
            return dashScopeUsage;
        }

        return ParseOpenAIUsage(usageElement);
    }

    private static UsageDetails? ParseOpenAIUsage(JsonElement usageElement)
    {
        if (!usageElement.TryGetProperty("prompt_tokens", out _)
            && !usageElement.TryGetProperty("completion_tokens", out _)
            && !usageElement.TryGetProperty("total_tokens", out _))
        {
            return null;
        }

        return new UsageDetails
        {
            InputTokenCount = OpenAICompatibleJsonHelpers.GetInt64(usageElement, "prompt_tokens"),
            TotalTokenCount = OpenAICompatibleJsonHelpers.GetInt64(usageElement, "total_tokens"),
        };
    }

    private static UsageDetails? ParseDashScopeUsage(JsonElement usageElement)
    {
        if (!usageElement.TryGetProperty("input_tokens", out _)
            && !usageElement.TryGetProperty("output_tokens", out _)
            && !usageElement.TryGetProperty("total_tokens", out _))
        {
            return null;
        }

        return new UsageDetails
        {
            InputTokenCount = OpenAICompatibleJsonHelpers.GetInt64(usageElement, "input_tokens"),
            OutputTokenCount = OpenAICompatibleJsonHelpers.GetInt64(usageElement, "output_tokens"),
            TotalTokenCount = OpenAICompatibleJsonHelpers.GetInt64(usageElement, "total_tokens"),
        };
    }

    private sealed class IndexedEmbedding
    {
        public IndexedEmbedding(int index, int position, Embedding<float> embedding)
        {
            Index = index;
            Position = position;
            Embedding = embedding;
        }

        public int Index { get; }

        public int Position { get; }

        public Embedding<float> Embedding { get; }
    }
}
