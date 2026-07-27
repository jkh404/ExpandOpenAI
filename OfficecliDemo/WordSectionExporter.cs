using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace OfficecliDemo;

internal sealed class WordSectionExporter
{
    private readonly string _sourceDocumentPath;

    public WordSectionExporter(string sourceDocumentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDocumentPath);
        _sourceDocumentPath = Path.GetFullPath(sourceDocumentPath);
    }

    public IReadOnlyList<string> Validate(TenderExtractionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        using var document = WordprocessingDocument.Open(_sourceDocumentPath, false);
        var index = new BodyBlockIndex(document);
        var errors = new List<string>();
        var businessRanges = ValidateSection("商务标", result.Business, index, errors);
        var technicalRanges = ValidateSection("技术标", result.Technical, index, errors);
        ValidateEvidence("商务标", result.Business, index, errors);
        ValidateEvidence("技术标", result.Technical, index, errors);

        foreach (var businessRange in businessRanges)
        {
            foreach (var technicalRange in technicalRanges)
            {
                if (businessRange.StartIndex <= technicalRange.EndIndex
                    && technicalRange.StartIndex <= businessRange.EndIndex)
                {
                    errors.Add(
                        $"商务标范围 {businessRange.StartPath}..{businessRange.EndPath} 与技术标范围 " +
                        $"{technicalRange.StartPath}..{technicalRange.EndPath} 重叠，请拆分或修正边界。 ");
                }
            }
        }

        return errors.AsReadOnly();
    }

    private static void ValidateEvidence(
        string sectionName,
        TenderSection section,
        BodyBlockIndex index,
        List<string> errors)
    {
        for (var evidenceIndex = 0; evidenceIndex < section.Evidence.Count; evidenceIndex++)
        {
            var evidence = section.Evidence[evidenceIndex];
            var prefix = $"{sectionName}.evidence[{evidenceIndex}]";
            if (string.IsNullOrWhiteSpace(evidence.DataPath)
                || !IsTopLevelParagraphIndexPath(evidence.DataPath))
            {
                errors.Add($"{prefix}.dataPath 只能使用顶层段落序号 /body/p[N]，禁止使用 paraId。 ");
                continue;
            }

            if (index.Resolve(evidence.DataPath) is null)
            {
                errors.Add($"{prefix}.dataPath 在源文档中不存在：{evidence.DataPath}");
            }
        }
    }

    public bool Export(TenderSection section, string outputDocumentPath)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDocumentPath);
        if (!section.Found || section.Segments.Count == 0)
        {
            return false;
        }

        var elements = new List<OpenXmlElement>();
        SectionProperties? sectionProperties;
        using (var sourceDocument = WordprocessingDocument.Open(_sourceDocumentPath, false))
        {
            var index = new BodyBlockIndex(sourceDocument);
            sectionProperties = index.FinalSectionProperties;
            foreach (var segment in section.Segments)
            {
                var start = index.Resolve(segment.StartDataPath)
                    ?? throw new InvalidOperationException($"无法解析起始路径：{segment.StartDataPath}");
                var end = index.Resolve(segment.EndDataPath)
                    ?? throw new InvalidOperationException($"无法解析结束路径：{segment.EndDataPath}");
                if (start.Index > end.Index)
                {
                    throw new InvalidOperationException(
                        $"起始路径位于结束路径之后：{segment.StartDataPath}..{segment.EndDataPath}");
                }

                if (elements.Count > 0)
                {
                    elements.Add(new Paragraph(new Run(new Text(string.Empty))));
                }

                for (var indexValue = start.Index; indexValue <= end.Index; indexValue++)
                {
                    elements.Add(index.Blocks[indexValue].CloneNode(true));
                }
            }
        }

        WriteDocument(outputDocumentPath, elements, sectionProperties);
        return true;
    }

    private static IReadOnlyList<ValidatedRange> ValidateSection(
        string sectionName,
        TenderSection section,
        BodyBlockIndex index,
        List<string> errors)
    {
        if (section.Confidence is < 0 or > 1)
        {
            errors.Add($"{sectionName}.confidence 必须在 0 到 1 之间。 ");
        }

        if (!section.Found)
        {
            if (section.Segments.Count > 0)
            {
                errors.Add($"{sectionName}.found=false 时 segments 必须为空。 ");
            }

            return [];
        }

        if (section.Segments.Count == 0)
        {
            errors.Add($"{sectionName}.found=true 时至少需要一个 segment。 ");
            return [];
        }

        var ranges = new List<ValidatedRange>();
        for (var segmentIndex = 0; segmentIndex < section.Segments.Count; segmentIndex++)
        {
            var segment = section.Segments[segmentIndex];
            var prefix = $"{sectionName}.segments[{segmentIndex}]";
            if (string.IsNullOrWhiteSpace(segment.StartDataPath)
                || string.IsNullOrWhiteSpace(segment.EndDataPath))
            {
                errors.Add($"{prefix} 必须同时提供 startDataPath 和 endDataPath。 ");
                continue;
            }

            if (!IsTopLevelParagraphIndexPath(segment.StartDataPath)
                || !IsTopLevelParagraphIndexPath(segment.EndDataPath))
            {
                errors.Add(
                    $"{prefix} 只能使用顶层段落序号 /body/p[N]，禁止使用 paraId、表格或内容控件路径。 ");
                continue;
            }

            if (segment.StartPage is <= 0 || segment.EndPage is <= 0)
            {
                errors.Add($"{prefix} 的页码必须大于 0。 ");
            }
            else if (segment.StartPage is not null
                && segment.EndPage is not null
                && segment.StartPage > segment.EndPage)
            {
                errors.Add($"{prefix}.startPage 不能大于 endPage。 ");
            }

            var start = index.Resolve(segment.StartDataPath);
            var end = index.Resolve(segment.EndDataPath);
            if (start is null)
            {
                errors.Add($"{prefix}.startDataPath 在源文档中不存在：{segment.StartDataPath}");
            }

            if (end is null)
            {
                errors.Add($"{prefix}.endDataPath 在源文档中不存在：{segment.EndDataPath}");
            }

            if (start is null || end is null)
            {
                continue;
            }

            if (start.Index > end.Index)
            {
                errors.Add($"{prefix} 的起始块位于结束块之后。 ");
                continue;
            }

            ranges.Add(new ValidatedRange(
                start.Index,
                end.Index,
                segment.StartDataPath,
                segment.EndDataPath));
        }

        var orderedRanges = ranges.OrderBy(static range => range.StartIndex).ToList();
        for (var rangeIndex = 1; rangeIndex < orderedRanges.Count; rangeIndex++)
        {
            if (orderedRanges[rangeIndex].StartIndex <= orderedRanges[rangeIndex - 1].EndIndex)
            {
                errors.Add(
                    $"{sectionName} 内部 segments 重叠：{orderedRanges[rangeIndex - 1].StartPath}.." +
                    $"{orderedRanges[rangeIndex - 1].EndPath} 与 {orderedRanges[rangeIndex].StartPath}.." +
                    $"{orderedRanges[rangeIndex].EndPath}。 ");
            }
        }

        return orderedRanges.AsReadOnly();
    }

    private static bool IsTopLevelParagraphIndexPath(string dataPath)
    {
        return Regex.IsMatch(
            dataPath,
            "^/body/p\\[\\d+\\]$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private void WriteDocument(
        string outputDocumentPath,
        IReadOnlyList<OpenXmlElement> elements,
        SectionProperties? sectionProperties)
    {
        var outputFullPath = Path.GetFullPath(outputDocumentPath);
        if (string.Equals(_sourceDocumentPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("输出文档不能覆盖源招标书。 ");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputFullPath) ?? Environment.CurrentDirectory);
        File.Copy(_sourceDocumentPath, outputFullPath, overwrite: true);

        using var outputDocument = WordprocessingDocument.Open(outputFullPath, true);
        var mainPart = outputDocument.MainDocumentPart
            ?? throw new InvalidOperationException("Word 文档缺少 MainDocumentPart。 ");
        var documentRoot = mainPart.Document
            ?? throw new InvalidOperationException("Word 文档缺少 document 根节点。 ");
        var body = documentRoot.Body
            ?? throw new InvalidOperationException("Word 文档缺少 body。 ");
        body.RemoveAllChildren();
        foreach (var element in elements)
        {
            body.AppendChild(element.CloneNode(true));
        }

        if (sectionProperties is not null)
        {
            body.AppendChild(sectionProperties.CloneNode(true));
        }

        documentRoot.Save();
    }

    private sealed class BodyBlockIndex
    {
        private readonly Body _body;

        public BodyBlockIndex(WordprocessingDocument document)
        {
            var mainPart = document.MainDocumentPart
                ?? throw new InvalidOperationException("Word 文档缺少 MainDocumentPart。 ");
            var documentRoot = mainPart.Document
                ?? throw new InvalidOperationException("Word 文档缺少 document 根节点。 ");
            _body = documentRoot.Body
                ?? throw new InvalidOperationException("Word 文档缺少 body。 ");
            Blocks = _body.ChildElements
                .Where(static element =>
                    element is not DocumentFormat.OpenXml.Wordprocessing.SectionProperties)
                .ToList()
                .AsReadOnly();
            FinalSectionProperties = _body.Elements<SectionProperties>()
                .LastOrDefault()
                ?.CloneNode(true) as SectionProperties;
        }

        public IReadOnlyList<OpenXmlElement> Blocks { get; }

        public SectionProperties? FinalSectionProperties { get; }

        public ResolvedBlock? Resolve(string? dataPath)
        {
            if (string.IsNullOrWhiteSpace(dataPath))
            {
                return null;
            }

            var paragraphMatch = Regex.Match(
                dataPath,
                "(?:^|/)p\\[(?<index>\\d+)\\]$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (paragraphMatch.Success)
            {
                return ResolveByPosition<Paragraph>(paragraphMatch.Groups["index"].Value);
            }

            var tableMatch = Regex.Match(
                dataPath,
                "(?:^|/)(?:tbl|table)\\[(?<index>\\d+)\\](?:/|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (tableMatch.Success)
            {
                return ResolveByPosition<Table>(tableMatch.Groups["index"].Value);
            }

            var sdtMatch = Regex.Match(
                dataPath,
                "(?:^|/)sdt\\[(?<index>\\d+)\\](?:/|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return sdtMatch.Success
                ? ResolveByPosition<SdtBlock>(sdtMatch.Groups["index"].Value)
                : null;
        }

        private ResolvedBlock? ResolveByPosition<T>(string indexText)
            where T : OpenXmlElement
        {
            if (!int.TryParse(indexText, out var position))
            {
                return null;
            }

            var elements = _body.Elements<T>().ToList();
            return position >= 1 && position <= elements.Count
                ? ResolveBodyChild(elements[position - 1])
                : null;
        }

        private ResolvedBlock? ResolveBodyChild(OpenXmlElement element)
        {
            var bodyChild = element;
            while (bodyChild.Parent is not null && bodyChild.Parent != _body)
            {
                bodyChild = bodyChild.Parent;
            }

            if (bodyChild.Parent != _body)
            {
                return null;
            }

            for (var index = 0; index < Blocks.Count; index++)
            {
                if (ReferenceEquals(Blocks[index], bodyChild))
                {
                    return new ResolvedBlock(index, bodyChild);
                }
            }

            return null;
        }
    }

    private sealed record ResolvedBlock(int Index, OpenXmlElement Element);

    private sealed record ValidatedRange(
        int StartIndex,
        int EndIndex,
        string StartPath,
        string EndPath);
}
