using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using OfficecliDemo;
using System.Text.Json;

namespace ExpandOpenAI.Tests.OfficecliDemo;

public sealed class OutlineDocumentIndexTests
{
    [Fact]
    public void OutlineRepairValidation_ValidatesInitialOutputAndEveryCorrection()
    {
        var correctionsUsed = OutlineRepairAgent
            .MaximumCorrectionAttempts;
        var validationPasses = OfficeCliAgentRuntime
            .EnumerateCorrectionValidationPasses(correctionsUsed)
            .ToArray();

        Assert.Equal([0, 1, 2, 3], validationPasses);
    }

    [Fact]
    public void Create_CountsEveryTopLevelParagraphWithoutTitlePreselection()
    {
        var documentPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.docx");
        try
        {
            using (var document = WordprocessingDocument.Create(
                documentPath,
                DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var mainPart = document.AddMainDocumentPart();
                mainPart.Document = new Document(new Body(
                    new Paragraph(new Run(new Text("普通正文。"))),
                    new Paragraph(new Run(new Text(new string('长', 150)))),
                    new Paragraph(new Run(new Text("没有编号、关键词和标题样式的真实标题"))),
                    new Paragraph()));
                mainPart.Document.Save();
            }

            var index = OutlineDocumentIndex.Create(documentPath);

            Assert.Equal(4, index.ParagraphCount);
            Assert.Equal(3, index.NonEmptyParagraphCount);
        }
        finally
        {
            File.Delete(documentPath);
        }
    }

    [Fact]
    public void OutlineRepairPlan_UsesZeroBasedBodyIndex()
    {
        var indexJson =
            """[{"title":"第一章 招标公告","index":"7","level":1}]""";
        var xpathJson =
            """[{"title":"第一章 招标公告","dataPath":"/body/p[7]","level":1}]""";

        Assert.True(OutlineRepairPlan.TryParse(indexJson, out var plan, out var indexError), indexError);
        Assert.Equal("7", Assert.Single(plan!.Items).Index);
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(
            plan.Items,
            TenderExtractionResult.JsonOptions));
        var serializedItem = serialized.RootElement[0];
        Assert.Equal(
            ["title", "index", "level"],
            serializedItem.EnumerateObject().Select(static property => property.Name));
        Assert.Equal("7", serializedItem.GetProperty("index").GetString());
        Assert.False(OutlineRepairPlan.TryParse(xpathJson, out _, out var xpathError));
        Assert.Contains("Body.ChildElements Index", xpathError);
    }

    [Fact]
    public void WordOutlineRepairer_ResolvesBodyChildIndexWithoutParagraphOrdinalConversion()
    {
        var documentPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.docx");
        try
        {
            using (var document = WordprocessingDocument.Create(
                documentPath,
                DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var mainPart = document.AddMainDocumentPart();
                mainPart.Document = new Document(new Body(
                    new Paragraph(new Run(new Text("普通正文"))),
                    new Table(new TableRow(new TableCell(new Paragraph(new Run(new Text("表格")))))),
                    new Paragraph(new Run(new Text("第一章 招标公告")))));
                mainPart.Document.Save();
            }

            Assert.True(OutlineRepairPlan.TryParse(
                """[{"title":"第一章 招标公告","index":"2","level":1}]""",
                out var plan,
                out var parseError), parseError);
            var repairer = new WordOutlineRepairer(documentPath);

            Assert.Empty(repairer.Validate(plan!));
            Assert.Equal(1, repairer.Apply(plan!));

            using var repairedDocument = WordprocessingDocument.Open(documentPath, false);
            var repairedParagraph = Assert.IsType<Paragraph>(
                repairedDocument.MainDocumentPart!.Document!.Body!.ChildElements[2]);
            Assert.Equal(0, repairedParagraph.ParagraphProperties!.OutlineLevel!.Val!.Value);

            Assert.True(OutlineRepairPlan.TryParse(
                """[{"title":"表格","index":"1","level":1}]""",
                out var tablePlan,
                out parseError), parseError);
            Assert.Contains(
                repairer.Validate(tablePlan!),
                error => error.Contains("未定位到标题原文对应的段落", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(documentPath);
        }
    }

    [Fact]
    public void WordOutlineRepairer_AllowsWordLayoutWhitespaceDifferencesInTitle()
    {
        var documentPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.docx");
        try
        {
            using (var document = WordprocessingDocument.Create(
                documentPath,
                DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var mainPart = document.AddMainDocumentPart();
                mainPart.Document = new Document(new Body(
                    new Paragraph(new Run(new Text("四、 施工安全措施")))));
                mainPart.Document.Save();
            }

            Assert.True(OutlineRepairPlan.TryParse(
                """[{"title":"四、施工安全措施","index":"0","level":1}]""",
                out var plan,
                out var parseError), parseError);

            var repairer = new WordOutlineRepairer(documentPath);
            Assert.Empty(repairer.Validate(plan!));

            Assert.True(OutlineRepairPlan.TryParse(
                """[{"title":"附件B：无效标条件","index":"0","level":1}]""",
                out var unrelatedPlan,
                out parseError), parseError);
            Assert.Contains(
                repairer.Validate(unrelatedPlan!),
                error => error.Contains("明显不一致", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(documentPath);
        }
    }
}
