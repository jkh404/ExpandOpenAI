using ExpandOpenAI.AgentFramework;

namespace ExpandOpenAI.Tests.AgentBase;

public sealed class MemoryUnitTests
{
    [Fact]
    public async Task InMemoryMemoryUnit_UpsertsByIdAndRecallsRelevantMemory()
    {
        var memory = new InMemoryMemoryUnit();

        await memory.RememberAsync(
        [
            new MemoryEntry("preference", "User prefers English responses."),
            new MemoryEntry("project", "The project targets .NET 10."),
        ]);
        await memory.RememberAsync(
        [
            new MemoryEntry("preference", "User prefers Chinese responses."),
        ]);

        var result = await memory.RecallAsync(new MemoryRecallRequest("Chinese"));

        Assert.Collection(
            result,
            entry =>
            {
                Assert.Equal("preference", entry.Id);
                Assert.Equal("User prefers Chinese responses.", entry.Content);
            });

        Assert.True(await memory.RemoveAsync("preference"));
        Assert.False(await memory.RemoveAsync("preference"));
        Assert.Empty(await memory.RecallAsync(new MemoryRecallRequest("Chinese")));

        await memory.ClearAsync();
        Assert.Empty(await memory.RecallAsync(new MemoryRecallRequest(string.Empty)));
    }
}
