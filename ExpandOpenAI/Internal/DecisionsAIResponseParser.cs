using System.Text.Json;

namespace ExpandOpenAI.Internal;

internal sealed class DecisionsAIResponseParser
{
    public DecisionsResponse ParseResponse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("answers", out var answersElement)
            || answersElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Decisions response does not contain an answers object.");
        }

        var answers = new Dictionary<string, DecisionsAnswer>(StringComparer.Ordinal);
        foreach (var property in answersElement.EnumerateObject())
        {
            answers.Add(property.Name, ParseAnswer(property.Value));
        }

        return new DecisionsResponse
        {
            Id = GetString(root, "id"),
            ModelId = GetString(root, "model"),
            Provider = GetString(root, "provider"),
            Answers = answers,
            Usage = ParseUsage(root),
            AdditionalProperties = OpenAICompatibleJsonHelpers.CollectAdditionalProperties(
                root, "id", "model", "provider", "answers", "usage"),
        };
    }

    private static DecisionsAnswer ParseAnswer(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Each Decisions answer must be a JSON object.");
        }

        var type = GetString(element, "type") ?? throw new JsonException("Answer is missing type.");
        return type switch
        {
            "noul" => new DecisionsNoulAnswer
            {
                Noul = GetNumber(element, "noul", probability: true)
                    ?? throw new JsonException("Noul answer is missing noul."),
                AdditionalProperties = OpenAICompatibleJsonHelpers.CollectAdditionalProperties(
                    element, "type", "noul"),
            },
            "choice" => new DecisionsChoiceAnswer
            {
                Choice = GetString(element, "choice") ?? throw new JsonException("Choice answer is missing choice."),
                Confidence = GetNumber(element, "confidence", probability: true),
                Probabilities = ParseProbabilities(element),
                AdditionalProperties = OpenAICompatibleJsonHelpers.CollectAdditionalProperties(
                    element, "type", "choice", "confidence", "probabilities"),
            },
            "score" => new DecisionsScoreAnswer
            {
                Score = GetNumber(element, "score") ?? throw new JsonException("Score answer is missing score."),
                Confidence = GetNumber(element, "confidence", probability: true),
                Probabilities = ParseProbabilities(element),
                Legend = ParseLegend(element),
                AdditionalProperties = OpenAICompatibleJsonHelpers.CollectAdditionalProperties(
                    element, "type", "score", "confidence", "probabilities", "legend"),
            },
            _ => new DecisionsUnknownAnswer(type, element)
            {
                AdditionalProperties = OpenAICompatibleJsonHelpers.CollectAdditionalProperties(element, "type"),
            },
        };
    }

    private static DecisionsUsage? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (usage.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Decisions usage must be an object.");
        }

        return new DecisionsUsage
        {
            InputTokens = GetTokenCount(usage, "input_tokens"),
            OutputTokens = GetTokenCount(usage, "output_tokens"),
            Cost = GetNumber(usage, "cost"),
            AdditionalProperties = OpenAICompatibleJsonHelpers.CollectAdditionalProperties(
                usage, "input_tokens", "output_tokens", "cost"),
        };
    }

    private static IReadOnlyDictionary<string, double>? ParseProbabilities(JsonElement element)
    {
        if (!element.TryGetProperty("probabilities", out var probabilities)
            || probabilities.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (probabilities.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Answer probabilities must be an object.");
        }

        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in probabilities.EnumerateObject())
        {
            result.Add(property.Name, GetNumber(probabilities, property.Name, probability: true)
                ?? throw new JsonException($"Probability '{property.Name}' cannot be null."));
        }

        // Do not enforce an exact sum: the service may round each probability independently.
        return result;
    }

    private static IReadOnlyDictionary<string, JsonElement>? ParseLegend(JsonElement element)
    {
        if (!element.TryGetProperty("legend", out var legend) || legend.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (legend.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Score legend must be an object.");
        }

        return legend.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => property.Value.Clone(),
            StringComparer.Ordinal);
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"Decisions response field '{name}' must be a string.");
        }

        return value.GetString();
    }

    private static long GetTokenCount(JsonElement usage, string name)
    {
        if (usage.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result) && result >= 0)
        {
            return result;
        }

        throw new JsonException($"Decisions usage must contain a nonnegative integer '{name}'.");
    }

    private static double? GetNumber(JsonElement element, string name, bool probability = false)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var result)
            || double.IsNaN(result) || double.IsInfinity(result) || result < 0
            || (probability && result > 1))
        {
            throw new JsonException($"Invalid numeric Decisions response field '{name}'.");
        }

        return result;
    }
}
