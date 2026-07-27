using System.Text.RegularExpressions;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace OfficecliDemo;

internal sealed class WordOutlineRepairer
{
    private readonly string _documentPath;

    public WordOutlineRepairer(string documentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        _documentPath = Path.GetFullPath(documentPath);
    }

    public IReadOnlyList<string> Validate(OutlineRepairPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var errors = new List<string>();
        if (plan.Items.Count == 0)
        {
            errors.Add("大纲数组不能为空。 ");
            return errors;
        }

        using var document = WordprocessingDocument.Open(_documentPath, false);
        var body = GetBody(document);
        var resolved = new List<(int Index, OutlineRepairItem Item)>();
        var seenIndexes = new HashSet<int>();
        for (var itemIndex = 0; itemIndex < plan.Items.Count; itemIndex++)
        {
            var item = plan.Items[itemIndex];
            var prefix = $"items[{itemIndex}]";
            if (string.IsNullOrWhiteSpace(item.Title)
                || !int.TryParse(item.Index, out var bodyIndex)
                || bodyIndex < 0)
            {
                errors.Add($"{prefix} 必须提供 title 和零基 Body.ChildElements index。 ");
                continue;
            }

            if (item.Level is < 1 or > 5)
            {
                errors.Add($"{prefix}.level 必须在 1 到 5 之间。 ");
            }

            if (!seenIndexes.Add(bodyIndex))
            {
                errors.Add($"{prefix}.index 重复：{item.Index}");
                continue;
            }

            var paragraph = ResolveParagraph(body, bodyIndex, item.Title);
            if (paragraph is null)
            {
                errors.Add(
                    $"{prefix}.index={item.Index} 未定位到标题原文对应的段落；该值必须来自 OfficeCLI 输出的 Index。 ");
                continue;
            }

            if (!AreTitlesEquivalent(paragraph.InnerText, item.Title))
            {
                errors.Add(
                    $"{prefix}.title 与定位段落内容明显不一致。JSON=\"{item.Title}\"，原文=\"{NormalizeText(paragraph.InnerText)}\"。 ");
                continue;
            }

            resolved.Add((bodyIndex, item));
        }

        for (var index = 1; index < resolved.Count; index++)
        {
            if (resolved[index].Index <= resolved[index - 1].Index)
            {
                errors.Add("大纲项必须按文档原始顺序排列。 ");
                break;
            }

            if (resolved[index].Item.Level > resolved[index - 1].Item.Level + 1)
            {
                errors.Add(
                    $"大纲级别不能跳级：\"{resolved[index - 1].Item.Title}\" level={resolved[index - 1].Item.Level}，" +
                    $"下一项 \"{resolved[index].Item.Title}\" level={resolved[index].Item.Level}。 ");
            }
        }

        if (resolved.Count > 0 && resolved[0].Item.Level != 1)
        {
            errors.Add("第一条大纲必须是 level=1。 ");
        }

        return errors.AsReadOnly();
    }

    public int Apply(OutlineRepairPlan plan)
    {
        var errors = Validate(plan);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "大纲计划校验失败：" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }

        using var document = WordprocessingDocument.Open(_documentPath, true);
        var mainPart = document.MainDocumentPart
            ?? throw new InvalidOperationException("Word 文档缺少 MainDocumentPart。 ");
        var body = GetBody(document);

        foreach (var outlineLevel in body.Descendants<OutlineLevel>().ToList())
        {
            outlineLevel.Remove();
        }

        var styles = mainPart.StyleDefinitionsPart?.Styles;
        if (styles is not null)
        {
            foreach (var outlineLevel in styles.Descendants<OutlineLevel>().ToList())
            {
                outlineLevel.Remove();
            }

            styles.Save();
        }

        var applied = 0;
        foreach (var item in plan.Items)
        {
            var bodyIndex = int.Parse(item.Index!);
            var paragraph = ResolveParagraph(body, bodyIndex, item.Title)
                ?? throw new InvalidOperationException($"应用大纲时 Body.ChildElements Index 失效：{item.Index}");
            var properties = paragraph.GetFirstChild<ParagraphProperties>();
            if (properties is null)
            {
                properties = new ParagraphProperties();
                paragraph.PrependChild(properties);
            }

            properties.RemoveAllChildren<OutlineLevel>();
            properties.AppendChild(new OutlineLevel { Val = item.Level - 1 });
            applied++;
        }

        mainPart.Document!.Save();
        return applied;
    }

    private static Body GetBody(WordprocessingDocument document)
    {
        return document.MainDocumentPart?.Document?.Body
            ?? throw new InvalidOperationException("Word 文档缺少 body。 ");
    }

    private static Paragraph? ResolveParagraph(Body body, int bodyIndex, string? expectedTitle)
    {
        if (bodyIndex < 0 || bodyIndex >= body.ChildElements.Count)
        {
            return null;
        }

        var topLevelElement = body.ChildElements[bodyIndex];
        if (topLevelElement is Paragraph paragraph)
        {
            return paragraph;
        }

        if (topLevelElement is not SdtBlock)
        {
            return null;
        }

        var normalizedExpectedTitle = NormalizeText(expectedTitle);
        var matches = topLevelElement
            .Descendants<Paragraph>()
            .Where(candidate => AreTitlesEquivalent(candidate.InnerText, normalizedExpectedTitle))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static string NormalizeText(string? text)
    {
        return Regex.Replace(text ?? string.Empty, "\\s+", " ").Trim();
    }

    private static bool AreTitlesEquivalent(string? left, string? right)
    {
        return string.Equals(
            NormalizeTitleForComparison(left),
            NormalizeTitleForComparison(right),
            StringComparison.Ordinal);
    }

    private static string NormalizeTitleForComparison(string? text)
    {
        return string.Concat((text ?? string.Empty)
            .Normalize(NormalizationForm.FormKC)
            .Where(static character => !char.IsWhiteSpace(character)));
    }
}
