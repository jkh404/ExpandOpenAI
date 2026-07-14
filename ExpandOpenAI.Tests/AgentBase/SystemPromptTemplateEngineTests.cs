using System.Text.Json.Nodes;
using ExpandOpenAI.AgentBase;

namespace ExpandOpenAI.Tests.AgentBase;

public sealed class SystemPromptTemplateEngineTests
{
    [Fact]
    public void Render_PreservesMissingPlaceholderWhenConfigured()
    {
        var result = SystemPromptTemplateEngine.Render(
            "Hello {{name}}",
            values: null,
            MissingTemplateValueBehavior.PreservePlaceholder);

        Assert.Equal("Hello {{name}}", result);
    }

    [Fact]
    public void Render_ThrowsForMissingPlaceholderWhenConfigured()
    {
        var values = new Dictionary<string, JsonNode?>();

        var exception = Assert.Throws<KeyNotFoundException>(() =>
            SystemPromptTemplateEngine.Render(
                "Hello {{name}}",
                values,
                MissingTemplateValueBehavior.Throw));

        Assert.Contains("name", exception.Message);
    }
}
