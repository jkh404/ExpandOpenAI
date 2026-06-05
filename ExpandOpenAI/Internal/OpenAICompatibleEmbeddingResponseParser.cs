using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.Internal;

internal sealed class OpenAICompatibleEmbeddingResponseParser
{
    private readonly JsonSerializerOptions _serializerOptions;

    public OpenAICompatibleEmbeddingResponseParser(JsonSerializerOptions serializerOptions)
    {
        _serializerOptions = serializerOptions;
    }

    public GeneratedEmbeddings<Embedding<float>> ParseResponse(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Embedding response does not contain a data array.");
        }

        var modelId = OpenAICompatibleJsonHelpers.GetString(root, "model");
        var createdAt = OpenAICompatibleJsonHelpers.GetCreatedAt(root);
        var indexedEmbeddings = new List<IndexedEmbedding>();
        var position = 0;

        foreach (var item in dataElement.EnumerateArray())
        {
            var index = OpenAICompatibleJsonHelpers.GetInt32(item, "index") ?? position;
            var embedding = ParseEmbedding(item, modelId, createdAt);
            indexedEmbeddings.Add(new IndexedEmbedding(index, position, embedding));
            position++;
        }

        var result = new GeneratedEmbeddings<Embedding<float>>(
            indexedEmbeddings
                .OrderBy(static item => item.Index)
                .ThenBy(static item => item.Position)
                .Select(static item => item.Embedding))
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

        return result;
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

        return new UsageDetails
        {
            InputTokenCount = OpenAICompatibleJsonHelpers.GetInt64(usageElement, "prompt_tokens"),
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
