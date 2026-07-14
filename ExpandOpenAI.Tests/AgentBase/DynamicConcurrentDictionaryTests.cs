using System.Text.Json.Nodes;
using ExpandOpenAI.AgentBase;

namespace ExpandOpenAI.Tests.AgentBase;

public sealed class DynamicConcurrentDictionaryTests
{
    [Fact]
    public void Indexer_ReplacesDynamicValueWithStaticValue()
    {
        var values = new DynamicConcurrentDictionary();
        Assert.True(values.RegisterDynamicValue("name", () => JsonValue.Create("dynamic")));

        values["name"] = JsonValue.Create("static");

        Assert.Equal("static", values["name"]!.GetValue<string>());
        Assert.Single(values);
    }

    [Fact]
    public void RegisterDynamicValue_DoesNotReplaceExistingKey()
    {
        var values = new DynamicConcurrentDictionary
        {
            ["name"] = JsonValue.Create("static"),
        };

        Assert.False(values.RegisterDynamicValue("name", () => JsonValue.Create("dynamic")));
        Assert.Equal("static", values["name"]!.GetValue<string>());
    }

    [Fact]
    public void Add_ThrowsForDuplicateKey()
    {
        var values = new DynamicConcurrentDictionary
        {
            ["name"] = JsonValue.Create("first"),
        };

        Assert.Throws<ArgumentException>(() => values.Add("name", JsonValue.Create("second")));
    }

    [Fact]
    public void CopyTo_AllowsEndIndexForEmptyCollection()
    {
        var values = new DynamicConcurrentDictionary();
        var destination = Array.Empty<KeyValuePair<string, JsonNode?>>();

        values.CopyTo(destination, destination.Length);
    }
}
