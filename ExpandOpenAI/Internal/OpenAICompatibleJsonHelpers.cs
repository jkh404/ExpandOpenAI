using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.Internal;

internal static class OpenAICompatibleJsonHelpers
{
    public static string ExtractEventPayload(IReadOnlyList<string> eventLines)
    {
        var builder = new StringBuilder();
        var foundDataLine = false;

        foreach (var line in eventLines)
        {
            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                foundDataLine = true;
                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(line.Substring(5).TrimStart());
            }
        }

        if (foundDataLine)
        {
            return builder.ToString();
        }

        return string.Join(Environment.NewLine, eventLines);
    }

    public static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        var result= property.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.ToString(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => property.GetString(),
        };

        return result;
    }

    public static int? GetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
        {
            return value;
        }

        return null;
    }

    public static long? GetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value))
        {
            return value;
        }

        return null;
    }

    public static DateTimeOffset? GetCreatedAt(JsonElement element)
    {
        if (!element.TryGetProperty("created", out var created))
        {
            return null;
        }

        if (created.ValueKind == JsonValueKind.Number && created.TryGetInt64(out var unixSeconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        if (created.ValueKind == JsonValueKind.String)
        {
            if (created.TryGetDateTimeOffset(out var dto))
            {
                return dto;
            }

            if (long.TryParse(created.GetString(), out unixSeconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            }
        }

        return null;
    }

    public static ChatRole? ParseRole(string? role)
    {
        return role switch
        {
            null => null,
            "assistant" => ChatRole.Assistant,
            "user" => ChatRole.User,
            "system" => ChatRole.System,
            "tool" => ChatRole.Tool,
            _ => new ChatRole(role),
        };
    }

    public static ChatFinishReason? ParseFinishReason(JsonElement element)
    {
        var finishReason = GetString(element, "finish_reason");
        return string.IsNullOrWhiteSpace(finishReason)
            ? null
            : new ChatFinishReason(finishReason);
    }

    public static string? CreateMessageId(string? responseId, JsonElement choice)
    {
        if (choice.TryGetProperty("message", out var message))
        {
            var existingId = GetString(message, "id") ?? GetString(message, "message_id");
            if (!string.IsNullOrWhiteSpace(existingId))
            {
                return existingId;
            }
        }

        if (choice.TryGetProperty("delta", out var delta))
        {
            var existingId = GetString(delta, "id") ?? GetString(delta, "message_id");
            if (!string.IsNullOrWhiteSpace(existingId))
            {
                return existingId;
            }
        }

        var index = GetInt32(choice, "index");
        return responseId is null || index is null ? null : $"{responseId}:{index.Value}";
    }

    public static AdditionalPropertiesDictionary? CollectAdditionalProperties(
        JsonElement element,
        params string[] knownProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var known = new HashSet<string>(knownProperties, StringComparer.Ordinal);
        Dictionary<string, object?>? additionalProperties = null;

        foreach (var property in element.EnumerateObject())
        {
            if (known.Contains(property.Name))
            {
                continue;
            }

            additionalProperties ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            additionalProperties[property.Name] = property.Value.ValueKind == JsonValueKind.Null
                ? null
                : property.Value.Clone();
        }

        return additionalProperties is null
            ? null
            : new AdditionalPropertiesDictionary(additionalProperties);
    }
}
