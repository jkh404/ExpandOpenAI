using System.Text.Json.Nodes;
using ExpandOpenAI.AgentFramework;
using OfficecliDemo;

namespace ExpandOpenAI.Tests.OfficecliDemo;

public sealed class AgentSystemPromptTests
{
    [Fact]
    public void OutlinePrompt_RendersTheExactDocumentAndInclusiveScanLimits()
    {
        var documentPath = Path.GetFullPath("待修复招标书.docx");
        var values = OfficeCliAgentRuntime.CreateSystemPromptTemplateValues(
            documentPath,
            textBatchSize: 200,
            pageCount: 211,
            bodyChildElementCount: 3683);

        var rendered = SystemPromptTemplateEngine.Render(
            OutlineRepairAgent.SystemPromptTemplate,
            values,
            MissingTemplateValueBehavior.Throw);

        Assert.Contains($"\"{documentPath}\"", rendered);
        Assert.Contains("Body.ChildElements=3683", rendered);
        Assert.Contains("有效 Index=0-3682", rendered);
        Assert.Contains("0-199", rendered);
        Assert.Contains("200-399", rendered);
        Assert.Contains("标题工作账本", rendered);
        Assert.Contains("[XPath=/body/p[203], Index=211]", rendered);
        Assert.Contains("\"index\":\"211\"", rendered);
        Assert.DoesNotContain("{{", rendered);
    }

    [Fact]
    public void ValidationErrorSummary_OnlyIncludesRepresentativeSamples()
    {
        var errors = Enumerable.Range(1, 20)
            .Select(static number => $"错误 {number}")
            .ToArray();

        var summary = OutlineRepairAgent.FormatValidationErrorSummary(errors, maximumSamples: 3);

        Assert.Contains("错误 1", summary);
        Assert.Contains("错误 3", summary);
        Assert.DoesNotContain("错误 4", summary);
        Assert.Contains("其余 17 条", summary);
    }

    [Fact]
    public void RuntimePromptValues_KeepTheExactPathAsAString()
    {
        var documentPath = Path.GetFullPath("含 空格 招标书.docx");

        var values = OfficeCliAgentRuntime.CreateSystemPromptTemplateValues(
            documentPath,
            textBatchSize: 200);

        Assert.Equal(documentPath, values["sourceDocumentPath"]!.GetValue<string>());
        Assert.Equal(199, values["textBatchEndOffset"]!.GetValue<int>());
        Assert.Equal(20, values["annotatedBatchSize"]!.GetValue<int>());
    }
}
