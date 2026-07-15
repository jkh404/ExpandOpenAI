using ExpandOpenAI;
using Microsoft.Extensions.AI;

namespace ExpandOpenAI.Tests.AgentBase;

public sealed class JsonRepairerTests
{
    [Fact]
    public async Task RepairAsync_NormalizesValidJsonWithoutCallingModel()
    {
        using var client = new TestChatClient();
        var repairer = new JsonRepairer(client);

        var result = await repairer.RepairAsync("{ \"name\": \"value\", }");

        Assert.Equal("{\"name\":\"value\"}", result);
        Assert.Equal(0, client.ResponseCallCount);
    }

    [Fact]
    public async Task RepairAsync_ValidatesAndNormalizesModelResult()
    {
        using var client = new TestChatClient
        {
            ResponseHandler = (_, _, _) => Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "```json\n{\"fixed\":true}\n```"))),
        };
        var repairer = new JsonRepairer(client);

        var result = await repairer.RepairAsync("{broken");

        Assert.Equal("{\"fixed\":true}", result);
        Assert.Equal(1, client.ResponseCallCount);
    }

    [Fact]
    public void ExtractJsonText_FindsBalancedJsonInsideText()
    {
        var result = JsonRepairer.ExtractJsonText("result: {\"text\":\"a } b\",\"items\":[1,2]}; done");

        Assert.Equal("{\"text\":\"a } b\",\"items\":[1,2]}", result);
    }
}
