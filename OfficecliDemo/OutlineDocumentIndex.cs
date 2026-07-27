using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace OfficecliDemo;

/// <summary>
/// 宿主侧只读计数器，用于把完整文档划分为 officecli 扫描范围。
/// 不向 Agent 暴露工具，也不判断哪些段落是标题。
/// </summary>
internal sealed class OutlineDocumentIndex
{
    private OutlineDocumentIndex(int paragraphCount, int nonEmptyParagraphCount)
    {
        ParagraphCount = paragraphCount;
        NonEmptyParagraphCount = nonEmptyParagraphCount;
    }

    public int ParagraphCount { get; }

    public int NonEmptyParagraphCount { get; }

    public static OutlineDocumentIndex Create(string documentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        using var document = WordprocessingDocument.Open(documentPath, false);
        var body = document.MainDocumentPart?.Document?.Body
            ?? throw new InvalidOperationException("Word 文档缺少 body。 ");
        var paragraphs = body.Elements<Paragraph>().ToList();
        return new OutlineDocumentIndex(
            paragraphs.Count,
            paragraphs.Count(static paragraph => !string.IsNullOrWhiteSpace(paragraph.InnerText)));
    }
}
