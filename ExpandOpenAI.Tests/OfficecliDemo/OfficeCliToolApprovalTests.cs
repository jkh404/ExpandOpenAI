using Microsoft.Extensions.Logging.Abstractions;
using OfficecliDemo;

namespace ExpandOpenAI.Tests.OfficecliDemo;

public sealed class OfficeCliToolApprovalTests
{
    [Fact]
    public void StagePermissions_BlockCommandsThatBelongToAnotherAgentTask()
    {
        var documentPath = Path.GetFullPath("tender.docx");
        var approval = CreateApproval(documentPath);
        approval.SetStagePermissions(OfficeCliCommandPermissions.MetadataRead);

        Assert.True(approval.TryApproveOfficeCliCommand(
            $"view \"{documentPath}\" stats --page-count",
            out var statsReason), statsReason);
        Assert.False(approval.TryApproveOfficeCliCommand(
            $"view \"{documentPath}\" text --start 1 --end 200",
            out var textReason));
        Assert.Contains("当前智能体任务不允许", textReason);
    }

    [Fact]
    public void IndexScanStage_RejectsPageAndLegacyOutputPositionScanning()
    {
        var documentPath = Path.GetFullPath("tender.docx");
        var approval = CreateApproval(documentPath);
        approval.SetStagePermissions(OfficeCliCommandPermissions.OutlineRepairRead);

        Assert.True(approval.TryApproveOfficeCliCommand(
            $"view \"{documentPath}\" text --startIndex 0 --endIndex 199",
            out var indexReason), indexReason);
        Assert.False(approval.TryApproveOfficeCliCommand(
            $"view \"{documentPath}\" text --page 1-20",
            out var pageReason));
        Assert.Contains("当前智能体任务不允许", pageReason);
        Assert.False(approval.TryApproveOfficeCliCommand(
            $"view \"{documentPath}\" text --start 1 --end 200",
            out var legacyReason));
        Assert.Contains("当前智能体任务不允许", legacyReason);
    }

    [Fact]
    public void WordSkill_CanOnlyBeLoadedOncePerAgentRuntime()
    {
        var approval = CreateApproval(Path.GetFullPath("tender.docx"));

        Assert.True(approval.TryApproveOfficeCliCommand("load_skill word", out var firstReason), firstReason);
        Assert.False(approval.TryApproveOfficeCliCommand("load_skill word", out var secondReason));
        Assert.Contains("已经加载过", secondReason);
    }

    [Fact]
    public void ParagraphGet_RequiresDepthZero()
    {
        var documentPath = Path.GetFullPath("tender.docx");
        var approval = new OfficeCliToolApproval(
            ["officecli"],
            documentPath,
            10,
            true,
            1_000,
            NullLogger<OfficeCliToolApproval>.Instance);

        Assert.True(approval.TryApproveOfficeCliCommand(
            $"get \"{documentPath}\" /body/p[40] --depth 0",
            out var approvedReason), approvedReason);
        Assert.False(approval.TryApproveOfficeCliCommand(
            $"get \"{documentPath}\" /body/p[40] --depth 1",
            out var rejectedReason));
        Assert.Contains("--depth 0", rejectedReason);
    }

    [Theory]
    [InlineData("text", "1-20", true)]
    [InlineData("text", "1-21", false)]
    [InlineData("annotated", "1-3", true)]
    [InlineData("annotated", "1-4", false)]
    public void PageView_EnforcesModeSpecificPageLimits(
        string mode,
        string pages,
        bool expected)
    {
        var documentPath = Path.GetFullPath("tender.docx");
        var approval = new OfficeCliToolApproval(
            ["officecli"],
            documentPath,
            10,
            true,
            1_000,
            NullLogger<OfficeCliToolApproval>.Instance);

        var approved = approval.TryApproveOfficeCliCommand(
            $"view \"{documentPath}\" {mode} --page {pages}",
            out var reason);

        Assert.Equal(expected, approved);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void CommandWhitelist_RequiresExactSupportedCombinations()
    {
        var documentPath = Path.GetFullPath("tender.docx");
        var approval = new OfficeCliToolApproval(
            ["officecli"],
            documentPath,
            30,
            true,
            1_000,
            NullLogger<OfficeCliToolApproval>.Instance);

        AssertApproved($"view \"{documentPath}\" stats --page-count");
        AssertApproved($"view \"{documentPath}\" outline");
        AssertApproved($"view \"{documentPath}\" text --start 1 --end 200");
        AssertApproved($"view \"{documentPath}\" annotated --start 1 --end 20");
        AssertApproved($"view \"{documentPath}\" text --startIndex 0 --endIndex 199");
        AssertApproved($"view \"{documentPath}\" annotated --startIndex 0 --endIndex 19");
        AssertApproved($"query \"{documentPath}\" paragraph --find \"商务标\"");

        AssertRejected($"view \"{documentPath}\" stats");
        AssertRejected($"view \"{documentPath}\" outline --page 1");
        AssertRejected($"view \"{documentPath}\" text");
        AssertRejected($"view \"{documentPath}\" text --start 1 --end 201");
        AssertRejected($"view \"{documentPath}\" annotated --start 1 --end 21");
        AssertRejected($"view \"{documentPath}\" text --startIndex 0 --endIndex 200");
        AssertRejected($"view \"{documentPath}\" annotated --startIndex 0 --endIndex 20");
        AssertRejected($"view \"{documentPath}\" text --startIndex 0 --end 20");
        AssertRejected($"view \"{documentPath}\" text --para-id --page 1");
        AssertRejected($"view \"{documentPath}\" html --page 1");
        AssertRejected($"query \"{documentPath}\" paragraph");
        AssertRejected($"query \"{documentPath}\" paragraph --find \"商务标\" --compact");
        AssertRejected($"validate \"{documentPath}\"");

        void AssertApproved(string command)
        {
            Assert.True(approval.TryApproveOfficeCliCommand(command, out var reason), reason);
        }

        void AssertRejected(string command)
        {
            Assert.False(approval.TryApproveOfficeCliCommand(command, out _));
        }
    }

    [Fact]
    public void BodyIndexRange_UsesZeroBasedInclusiveLimits()
    {
        var documentPath = Path.GetFullPath("tender.docx");
        var approval = CreateApproval(documentPath);

        Assert.True(approval.TryApproveOfficeCliCommand(
            $"view \"{documentPath}\" text --startIndex 0 --endIndex 199",
            out var textReason), textReason);
        Assert.Contains("零基 Body.ChildElements", textReason);

        Assert.False(approval.TryApproveOfficeCliCommand(
            $"view \"{documentPath}\" text --startIndex 0 --endIndex 200",
            out var oversizedRangeReason));
        Assert.Contains("--startIndex 0 --endIndex 199", oversizedRangeReason);
        Assert.False(approval.TryApproveOfficeCliCommand(
            $"view \"{documentPath}\" annotated --startIndex -1 --endIndex 5",
            out _));
    }

    [Fact]
    public void WrongDocumentPath_RejectionReturnsTheExactWorkingDocument()
    {
        var documentPath = Path.GetFullPath("tender.docx");
        var approval = CreateApproval(documentPath);

        Assert.False(approval.TryApproveOfficeCliCommand(
            "view 招标书.docx text --startIndex 0 --endIndex 199",
            out var reason));

        Assert.Contains($"\"{documentPath}\"", reason);
    }

    private static OfficeCliToolApproval CreateApproval(string documentPath)
    {
        return new OfficeCliToolApproval(
            ["officecli"],
            documentPath,
            30,
            true,
            1_000,
            NullLogger<OfficeCliToolApproval>.Instance);
    }
}
