using OfficecliDemo;

namespace ExpandOpenAI.Tests.OfficecliDemo;

public sealed class ProgramTests
{
    [Theory]
    [InlineData(null, "Combined")]
    [InlineData("combined", "Combined")]
    [InlineData("outline", "OutlineOnly")]
    [InlineData("extraction", "ExtractionOnly")]
    public void ParseRunMode_SupportsIndependentAndCombinedModes(
        string? value,
        string expected)
    {
        Assert.Equal(expected, DemoOptions.ParseRunMode(value).ToString());
    }

    [Fact]
    public void ReadValidationErrorCount_ReadsPlainTextFromStandardError()
    {
        var result = new OfficeCliProcessResult(
            1,
            string.Empty,
            "Found 6 validation error(s):");

        Assert.Equal(6, Program.ReadValidationErrorCount(result));
    }

    [Theory]
    [InlineData("1.0.136", 1, 0, 136)]
    [InlineData("officecli version 1.0.140", 1, 0, 140)]
    public void TryReadOfficeCliVersion_ParsesSemanticVersion(
        string output,
        int major,
        int minor,
        int build)
    {
        Assert.True(Program.TryReadOfficeCliVersion(output, out var version));
        Assert.Equal(new Version(major, minor, build), version);
    }
}
