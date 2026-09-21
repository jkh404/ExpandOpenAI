using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI;

/// <summary>Evaluates one state against a set of independent questions.</summary>
public sealed class DecisionsRequest
{
    /// <summary>Overrides the client's model for this request. Null uses the client default.</summary>
    public string? ModelId { get; set; }

    /// <summary>A string, JSON object, or JSON array. Objects are serialized without stringifying them.</summary>
    public required object State { get; set; }

    public required IReadOnlyDictionary<string, DecisionsQuestion> Questions { get; set; }

    /// <summary>Optional OpenRouter provider routing preferences, as a JSON object.</summary>
    public object? Provider { get; set; }

    public string? SessionId { get; set; }

    /// <summary>Optional OpenRouter tracing metadata, as a JSON object.</summary>
    public object? Trace { get; set; }

    public string? User { get; set; }

    /// <summary>Additional top-level request fields. Model, state and questions cannot be overridden here.</summary>
    public IReadOnlyDictionary<string, object?>? AdditionalProperties { get; set; }
}

/// <summary>Base for the three System One primitives.</summary>
public abstract class DecisionsQuestion
{
    public abstract string Type { get; }

    /// <summary>String, JSON object, JSON array, or null, as specified by TypeSafe's EntryType.</summary>
    public object? Instructions { get; set; }

    public IReadOnlyDictionary<string, object?>? AdditionalProperties { get; set; }
}

/// <summary>A yes/no question. Its answer is the probability of yes, not a degree or a boolean.</summary>
public sealed class DecisionsNoulQuestion : DecisionsQuestion
{
    public override string Type => "noul";

    public DecisionsNoulCriteria? Criteria { get; set; }
}

/// <summary>Optional descriptions of both outcomes. Each description accepts an EntryType.</summary>
public sealed class DecisionsNoulCriteria
{
    [JsonPropertyName("true")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public object? True { get; set; }

    [JsonPropertyName("false")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public object? False { get; set; }
}

/// <summary>Selects one named option. Descriptions accept string, object, array, or null.</summary>
public sealed class DecisionsChoiceQuestion : DecisionsQuestion
{
    public override string Type => "choice";

    public required IReadOnlyDictionary<string, object?> Criteria { get; set; }
}

/// <summary>Rates a state against ordered levels. Array positions define the numeric scale from zero.</summary>
public sealed class DecisionsScoreQuestion : DecisionsQuestion
{
    public override string Type => "score";

    /// <summary>Two to ten levels, each accepting string, object, array, or null.</summary>
    public required IReadOnlyList<object?> Criteria { get; set; }
}

/// <summary>Response returned by a Decisions or System One endpoint.</summary>
public sealed class DecisionsResponse
{
    public string? Id { get; init; }

    public string? ModelId { get; init; }

    public string? Provider { get; init; }

    public required IReadOnlyDictionary<string, DecisionsAnswer> Answers { get; init; }

    public DecisionsUsage? Usage { get; init; }

    public AdditionalPropertiesDictionary? AdditionalProperties { get; init; }
}

public abstract class DecisionsAnswer
{
    public abstract string Type { get; }

    public AdditionalPropertiesDictionary? AdditionalProperties { get; init; }
}

public sealed class DecisionsNoulAnswer : DecisionsAnswer
{
    public override string Type => "noul";

    /// <summary>Probability of yes, from zero to one. Thresholds are chosen by the caller.</summary>
    public required double Noul { get; init; }
}

public sealed class DecisionsChoiceAnswer : DecisionsAnswer
{
    public override string Type => "choice";

    public required string Choice { get; init; }

    public double? Confidence { get; init; }

    public IReadOnlyDictionary<string, double>? Probabilities { get; init; }
}

public sealed class DecisionsScoreAnswer : DecisionsAnswer
{
    public override string Type => "score";

    /// <summary>Probability-weighted mean of the zero-based level indices. Can be fractional.</summary>
    public required double Score { get; init; }

    public double? Confidence { get; init; }

    public IReadOnlyDictionary<string, double>? Probabilities { get; init; }

    /// <summary>Original level descriptions keyed by level index, preserved as JSON values.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Legend { get; init; }
}

/// <summary>Preserves an unknown answer type for forward compatibility with the alpha API.</summary>
public sealed class DecisionsUnknownAnswer : DecisionsAnswer
{
    public DecisionsUnknownAnswer(string type, JsonElement raw)
    {
        Type = type;
        Raw = raw.Clone();
    }

    public override string Type { get; }

    public JsonElement Raw { get; }
}

public sealed class DecisionsUsage
{
    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public long TotalTokens => InputTokens + OutputTokens;

    /// <summary>Optional request cost reported by OpenRouter.</summary>
    public double? Cost { get; init; }

    public AdditionalPropertiesDictionary? AdditionalProperties { get; init; }
}
