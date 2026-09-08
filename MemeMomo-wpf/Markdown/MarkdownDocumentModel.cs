using Markdig;
using Markdig.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MemeMomo.Markdown;

internal enum MarkdownVisualKind { Normal, Heading1, Heading2, Heading3, Heading4, Bold, Italic, Strike, Underline, Mark, Code, CodeBlock, Link, Quote, Image, Rule, Table, Task, OrderedListMarker }

internal readonly record struct MarkdownVisualSpan(
    int Start,
    int Length,
    MarkdownVisualKind Kind,
    int SourceStart,
    int SourceLength,
    string? LinkTarget = null,
    string? ImageUri = null,
    string? AltText = null,
    double? ImageWidthPercent = null,
    string? CodeLabel = null,
    string? CodeContent = null,
    int? CodeContentSourceStart = null,
    int? CodeContentSourceEnd = null)
{
    public int End => Start + Length;
}

/// <summary>
/// Owns the Markdown source and its editable, marker-free projection. Markdig remains the
/// authority for block boundaries; only the changed source range is replaced after input.
/// </summary>
internal sealed partial class MarkdownDocumentModel
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
    private int[] _visibleToSourceBefore = [0];
    private int[] _visibleToSourceAfter = [0];
    private int[] _sourceToVisible = [0];
    private List<SourceRange> _visibleCharacters = [];

    public MarkdownDocumentModel(string? markdown = null) => SetMarkdown(markdown);

    public string Markdown { get; private set; } = string.Empty;
    public string VisibleText { get; private set; } = string.Empty;
    public long ProjectionVersion { get; private set; }
    public MarkdownDocument Ast { get; private set; } = new();
    public IReadOnlyList<MarkdownVisualSpan> Spans { get; private set; } = [];

    public void SetMarkdown(string? markdown)
    {
        Markdown = Normalize(markdown);
        RebuildProjection();
    }

    public void SetEditableMarkdown(string? markdown)
    {
        Markdown = Normalize(markdown);
        RebuildProjection(ensureEditableBlockBoundaries: true);
    }

    internal void ReplaceSourceRange(int start, int length, string? replacement)
    {
        start = Math.Clamp(start, 0, Markdown.Length);
        length = Math.Clamp(length, 0, Markdown.Length - start);
        string value = Normalize(replacement);
        Markdown = Markdown.Remove(start, length).Insert(start, value);
        RebuildProjection(ensureEditableBlockBoundaries: true);
    }

    internal void ReplaceTableCellSourceRange(int start, int length, string? replacement)
    {
        start = Math.Clamp(start, 0, Markdown.Length);
        length = Math.Clamp(length, 0, Markdown.Length - start);
        string value = Normalize(replacement);
        int oldEnd = start + length;
        MarkdownVisualSpan? table = Spans
            .Where(span => span.Kind == MarkdownVisualKind.Table &&
                start >= span.SourceStart && oldEnd <= span.SourceStart + span.SourceLength)
            .Select(span => (MarkdownVisualSpan?)span)
            .FirstOrDefault();
        if (table is not { } tableSpan || value.Contains('\n') || value.Contains('|'))
        {
            ReplaceSourceRange(start, length, value);
            return;
        }

        int delta = value.Length - length;
        int oldSourceLength = Markdown.Length;
        int[] oldSourceToVisible = _sourceToVisible;
        Markdown = Markdown.Remove(start, length).Insert(start, value);

        int[] sourceToVisible = new int[Markdown.Length + 1];
        for (int source = 0; source <= Markdown.Length; source++)
        {
            if (source <= start)
            {
                sourceToVisible[source] = oldSourceToVisible[source];
            }
            else if (source < start + value.Length)
            {
                sourceToVisible[source] = tableSpan.Start;
            }
            else
            {
                int oldSource = Math.Clamp(source - delta, 0, oldSourceLength);
                sourceToVisible[source] = oldSourceToVisible[oldSource];
            }
        }
        _sourceToVisible = sourceToVisible;

        _visibleCharacters = _visibleCharacters
            .Select(range => TransformSourceRange(range, start, oldEnd, delta, value.Length))
            .ToList();
        Spans = Spans.Select(span =>
        {
            int spanEnd = span.SourceStart + span.SourceLength;
            if (spanEnd <= start)
            {
                return span;
            }
            if (span.SourceStart >= oldEnd)
            {
                return span with { SourceStart = span.SourceStart + delta };
            }
            if (span.SourceStart <= start && spanEnd >= oldEnd)
            {
                return span with { SourceLength = span.SourceLength + delta };
            }

            return span;
        }).ToArray();
        BuildVisibleAffinityMaps();
        ProjectionVersion++;
    }

    private static SourceRange TransformSourceRange(
        SourceRange range,
        int start,
        int oldEnd,
        int delta,
        int replacementLength)
    {
        int mappedStart = range.Start <= start
            ? range.Start
            : range.Start >= oldEnd ? range.Start + delta : start;
        int mappedEnd = range.End <= start
            ? range.End
            : range.End >= oldEnd ? range.End + delta : start + replacementLength;
        return new SourceRange(mappedStart, Math.Max(mappedStart, mappedEnd));
    }

    public int SourceOffsetFromVisible(int visibleOffset, bool trailingAffinity = true)
    {
        var map = trailingAffinity ? _visibleToSourceAfter : _visibleToSourceBefore;
        return map[Math.Clamp(visibleOffset, 0, VisibleText.Length)];
    }

    public int VisibleOffsetFromSource(int sourceOffset) =>
        _sourceToVisible[Math.Clamp(sourceOffset, 0, Markdown.Length)];

    public (int Start, int End) ApplyVisibleText(string? visibleText) =>
        ApplyVisibleText(visibleText, null);

    public (int Start, int End) ApplyVisibleText(
        string? visibleText,
        int changeOffset,
        int removalLength,
        int insertionLength) =>
        ApplyVisibleText(visibleText, new VisibleTextChange(
            changeOffset, removalLength, insertionLength));

    private (int Start, int End) ApplyVisibleText(
        string? visibleText,
        VisibleTextChange? change)
    {
        var next = Normalize(visibleText);
        if (next == VisibleText) return (0, 0);

        int prefix;
        int oldSuffix;
        int newSuffix;
        if (IsExactChangeValid(next, change))
        {
            var exact = change!.Value;
            prefix = exact.Offset;
            oldSuffix = exact.Offset + exact.RemovalLength;
            newSuffix = exact.Offset + exact.InsertionLength;
        }
        else
        {
            prefix = 0;
            var shared = Math.Min(VisibleText.Length, next.Length);
            while (prefix < shared && VisibleText[prefix] == next[prefix]) prefix++;
            oldSuffix = VisibleText.Length;
            newSuffix = next.Length;
            while (oldSuffix > prefix && newSuffix > prefix &&
                   VisibleText[oldSuffix - 1] == next[newSuffix - 1])
            {
                oldSuffix--;
                newSuffix--;
            }
        }

        var inserted = next[prefix..newSuffix];
        bool requiresBoundaryNormalization = inserted.Contains('\n') ||
            (oldSuffix > prefix && next.Length >= newSuffix &&
             VisibleText.AsSpan(prefix, oldSuffix - prefix).Contains('\n'));

        if (oldSuffix == prefix &&
            prefix == VisibleText.Length &&
            inserted.Length > 0 &&
            !requiresBoundaryNormalization &&
            SourceOffsetForInsertion(prefix) == Markdown.Length)
        {
            AppendPlainTextAtEnd(inserted);
            return (prefix + inserted.Length, prefix + inserted.Length);
        }
        if (oldSuffix == prefix)
        {
            var sourceStart = SourceOffsetForInsertion(prefix);
            var emptyClosedFence = Spans
                .Where(span => span.Kind == MarkdownVisualKind.CodeBlock &&
                    span.CodeLabel is not null &&
                    span.Length == 0 &&
                    span.Start == prefix &&
                    span.CodeContentSourceStart is { } contentStart &&
                    span.CodeContentSourceEnd is { } contentEnd &&
                    contentStart == contentEnd &&
                    contentEnd < span.SourceStart + span.SourceLength)
                .Select(span => (MarkdownVisualSpan?)span)
                .FirstOrDefault();
            Markdown = emptyClosedFence is not null
                ? Markdown.Insert(sourceStart, inserted + "\n")
                : Markdown.Insert(sourceStart, inserted);
        }
        else
        {
            var visibleRanges = _visibleCharacters.Skip(prefix).Take(oldSuffix - prefix)
                .Distinct().ToArray();
            var insertionPoint = visibleRanges.Length == 0
                ? SourceOffsetFromVisible(prefix)
                : visibleRanges.Min(range => range.Start);
            var markerRanges = inserted.Length == 0
                ? EmptyMarkerRangesForDeletion(prefix, oldSuffix, visibleRanges)
                : [];
            var removed = WithoutTableBoundaryLineBreaks(
                MergeRanges(visibleRanges.Concat(markerRanges)));
            var output = new StringBuilder(Markdown.Length - removed.Sum(range => range.Length) + inserted.Length);
            var rangeIndex = 0;
            for (var source = 0; source < Markdown.Length;)
            {
                if (source == insertionPoint) output.Append(inserted);
                if (rangeIndex < removed.Length && source == removed[rangeIndex].Start)
                {
                    source = removed[rangeIndex].End;
                    rangeIndex++;
                }
                else output.Append(Markdown[source++]);
            }
            if (insertionPoint == Markdown.Length) output.Append(inserted);
            Markdown = output.ToString();
        }
        // Ordinary character input cannot change block boundaries. Avoid the
        // additional Markdig reparses in that hot path; Enter, deletion across
        // lines, and explicit structural edits still take the normalization path.
        RebuildProjection(ensureEditableBlockBoundaries: requiresBoundaryNormalization);
        var caret = Math.Clamp(prefix + inserted.Length, 0, VisibleText.Length);
        return (caret, caret);
    }

    private void AppendPlainTextAtEnd(string inserted)
    {
        int oldSourceLength = Markdown.Length;
        int oldVisibleLength = VisibleText.Length;
        Markdown += inserted;
        VisibleText += inserted;

        EnsureCapacity(ref _sourceToVisible, oldSourceLength + inserted.Length + 1);
        for (int index = 0; index < inserted.Length; index++)
        {
            _sourceToVisible[oldSourceLength + index] = oldVisibleLength + index;
        }
        _sourceToVisible[oldSourceLength + inserted.Length] = oldVisibleLength + inserted.Length;

        EnsureCapacity(ref _visibleToSourceBefore, oldVisibleLength + inserted.Length + 1);
        EnsureCapacity(ref _visibleToSourceAfter, oldVisibleLength + inserted.Length + 1);
        for (int index = 0; index < inserted.Length; index++)
        {
            int source = oldSourceLength + index + 1;
            _visibleToSourceBefore[oldVisibleLength + index + 1] = source;
            _visibleToSourceAfter[oldVisibleLength + index + 1] = source;
        }

        for (int index = 0; index < inserted.Length; index++)
        {
            _visibleCharacters.Add(new SourceRange(
                oldSourceLength + index,
                oldSourceLength + index + 1));
        }
        ProjectionVersion++;
    }

    private static void EnsureCapacity(ref int[] values, int requiredLength)
    {
        if (values.Length >= requiredLength)
        {
            return;
        }

        Array.Resize(ref values, Math.Max(requiredLength, Math.Max(4, values.Length * 2)));
    }

    internal int SourceOffsetForInsertion(int visibleOffset)
    {
        var fencedBoundary = Spans
            .Where(span => span.Kind == MarkdownVisualKind.CodeBlock &&
                span.CodeLabel is not null &&
                span.CodeContentSourceStart is not null &&
                span.CodeContentSourceEnd is not null &&
                (visibleOffset == span.Start || visibleOffset == span.End))
            .OrderBy(span => span.SourceStart)
            .Select(span => (MarkdownVisualSpan?)span)
            .FirstOrDefault();
        if (fencedBoundary is { } codeBlock)
        {
            return visibleOffset == codeBlock.Start
                ? codeBlock.CodeContentSourceStart!.Value
                : codeBlock.CodeContentSourceEnd!.Value;
        }

        if (visibleOffset == 0 || visibleOffset >= VisibleText.Length)
            return SourceOffsetFromVisible(visibleOffset, trailingAffinity: true);

        var before = SourceOffsetFromVisible(visibleOffset, trailingAffinity: false);
        var after = SourceOffsetFromVisible(visibleOffset, trailingAffinity: true);
        // A link's label ends before its hidden destination syntax. At that visible
        // boundary, insertion must land after the complete `[label](target)` source;
        // otherwise a newline is inserted before `](` and the link stops parsing.
        if (TryGetLinkSourceEndAtVisibleBoundary(visibleOffset) is { } linkSourceEnd)
            return linkSourceEnd;
        // The projection hides one of the two Markdown line breaks that terminate a quote.
        // At the first visible position below the quote, cross only that separator. Using the
        // full trailing affinity could also cross the next paragraph's hidden formatting or
        // object syntax, while inserting before the separator creates a lazy quote continuation.
        foreach (var quote in Spans.Where(span =>
                     span.Kind == MarkdownVisualKind.Quote && span.End + 1 == visibleOffset))
        {
            var quoteSourceEnd = Math.Clamp(
                quote.SourceStart + quote.SourceLength, quote.SourceStart, Markdown.Length);
            var separatorEnd = quoteSourceEnd + 2;
            if (separatorEnd <= Markdown.Length &&
                Markdown[quoteSourceEnd] == '\n' && Markdown[quoteSourceEnd + 1] == '\n' &&
                _sourceToVisible[separatorEnd] == visibleOffset)
                return separatorEnd;
        }
        if (after > before && QuotePrefixesOnly().IsMatch(Markdown[before..after]))
            return after;
        return before;
    }

    private int? TryGetLinkSourceEndAtVisibleBoundary(int visibleOffset)
    {
        foreach (MarkdownVisualSpan span in Spans.Where(span =>
                     span.Kind == MarkdownVisualKind.Link &&
                     span.Length > 0 &&
                     span.End == visibleOffset))
        {
            Match? match = LinkSyntax().Matches(Markdown).FirstOrDefault(candidate =>
                candidate.Groups[1].Index == span.SourceStart &&
                candidate.Groups[1].Length == span.SourceLength);
            if (match is not null)
            {
                return match.Index + match.Length;
            }

            int contentEnd = span.SourceStart + span.SourceLength;
            Match closingTag = HtmlTagSyntax().Match(Markdown, contentEnd);
            if (closingTag.Success &&
                closingTag.Index == contentEnd &&
                closingTag.Groups[1].Success &&
                string.Equals(closingTag.Groups[2].Value, "a", StringComparison.OrdinalIgnoreCase))
            {
                return closingTag.Index + closingTag.Length;
            }
        }

        return null;
    }

    private bool IsExactChangeValid(string next, VisibleTextChange? change)
    {
        if (change is not { } exact ||
            exact.Offset < 0 || exact.RemovalLength < 0 || exact.InsertionLength < 0 ||
            exact.Offset + exact.RemovalLength > VisibleText.Length ||
            exact.Offset + exact.InsertionLength > next.Length ||
            VisibleText.Length - exact.RemovalLength + exact.InsertionLength != next.Length)
            return false;

        return VisibleText.AsSpan(0, exact.Offset).SequenceEqual(next.AsSpan(0, exact.Offset)) &&
               VisibleText.AsSpan(exact.Offset + exact.RemovalLength)
                   .SequenceEqual(next.AsSpan(exact.Offset + exact.InsertionLength));
    }

    /// <summary>
    /// Computes the Markdown source range that a deletion of the visible range
    /// [<paramref name="visibleStart"/>, <paramref name="visibleEnd"/>) must remove: the
    /// source ranges of the visible characters, plus inline markers of spans fully contained
    /// in the range, plus hidden characters touching either boundary. Used when a deletion
    /// has to bypass the visible-text diff (atomic objects such as rules) but must still
    /// remove the surrounding selected text with the same semantics as a normal deletion.
    /// </summary>
    internal (int Start, int End) GetSourceDeletionRange(int visibleStart, int visibleEnd)
    {
        visibleStart = Math.Clamp(visibleStart, 0, VisibleText.Length);
        visibleEnd = Math.Clamp(visibleEnd, visibleStart, VisibleText.Length);
        var visibleRanges = _visibleCharacters.Skip(visibleStart).Take(visibleEnd - visibleStart)
            .Distinct().ToArray();
        if (visibleRanges.Length == 0)
        {
            return (
                SourceOffsetFromVisible(visibleStart, trailingAffinity: false),
                SourceOffsetFromVisible(visibleEnd, trailingAffinity: true));
        }

        var markerRanges = EmptyMarkerRangesForDeletion(visibleStart, visibleEnd, visibleRanges);
        var removed = MergeRanges(visibleRanges.Concat(markerRanges));
        var start = Math.Min(removed[0].Start, SourceOffsetFromVisible(visibleStart, trailingAffinity: false));
        var end = Math.Max(removed[^1].End, SourceOffsetFromVisible(visibleEnd, trailingAffinity: true));
        return (start, end);
    }

    private IEnumerable<SourceRange> EmptyMarkerRangesForDeletion(
        int visibleStart,
        int visibleEnd,
        IReadOnlyList<SourceRange> visibleRanges)
    {
        foreach (SourceRange range in QuotePrefixRangesAfterRemovedLineBreaks(visibleRanges))
        {
            yield return range;
        }

        foreach (var span in Spans.Where(span =>
            span.Start >= visibleStart && span.End <= visibleEnd &&
            span.Kind is MarkdownVisualKind.Bold or MarkdownVisualKind.Italic or
                MarkdownVisualKind.Strike or MarkdownVisualKind.Code))
        {
            var delimiterLength = span.Kind is MarkdownVisualKind.Bold or MarkdownVisualKind.Strike ? 2 : 1;
            var openStart = span.SourceStart - delimiterLength;
            var closeStart = span.SourceStart + span.SourceLength;
            if (openStart < 0 || closeStart + delimiterLength > Markdown.Length) continue;
            var open = Markdown.AsSpan(openStart, delimiterLength);
            var close = Markdown.AsSpan(closeStart, delimiterLength);
            if (open.SequenceEqual(close))
            {
                yield return new SourceRange(openStart, span.SourceStart);
                yield return new SourceRange(closeStart, closeStart + delimiterLength);
            }
        }
    }

    private IEnumerable<SourceRange> QuotePrefixRangesAfterRemovedLineBreaks(
        IReadOnlyList<SourceRange> visibleRanges)
    {
        HashSet<int> removedLineBreakEnds = visibleRanges
            .Where(range => range.Length == 1 && Markdown[range.Start] == '\n')
            .Select(range => range.End)
            .ToHashSet();
        foreach (int lineBreakEnd in removedLineBreakEnds)
        {
            Match prefix = QuoteLinePrefixAt().Match(Markdown, lineBreakEnd);
            if (prefix.Success && prefix.Index == lineBreakEnd)
            {
                int prefixEnd = prefix.Index + prefix.Length;
                bool belongsToQuote = Spans.Any(span =>
                    span.Kind == MarkdownVisualKind.Quote &&
                    prefix.Index >= span.SourceStart &&
                    prefixEnd <= span.SourceStart + span.SourceLength);
                if (belongsToQuote)
                {
                    yield return new SourceRange(prefix.Index, prefixEnd);
                }
            }
        }
    }

    private static SourceRange[] MergeRanges(IEnumerable<SourceRange> ranges)
    {
        var ordered = ranges.OrderBy(range => range.Start).ThenBy(range => range.End).ToArray();
        if (ordered.Length == 0) return [];
        var merged = new List<SourceRange> { ordered[0] };
        foreach (var range in ordered.Skip(1))
        {
            var previous = merged[^1];
            if (range.Start > previous.End) merged.Add(range);
            else if (range.End > previous.End) merged[^1] = new SourceRange(previous.Start, range.End);
        }
        return merged.ToArray();
    }

    /// <summary>
    /// 表格块后面的前两个源码换行是表格的块边界：只有它们存在时 Markdig 才把这段源码解析
    /// 成表格，删掉后表格会退回成原始竖线文本且无法由 EnsureTableTrailingLineBreaks 找回。
    /// 可见文本删除不得消耗这两个换行（与投影层"文本输入不能消耗表格边界"的规则一致）；
    /// 需要删除整个表格时走输入层的整表删除或 ReplaceSourceRange，不经过这里的可见删除。
    /// 被保留的换行会让本次编辑的可见文本与编辑器文档短暂不一致，由投影刷新收敛回来。
    /// </summary>
    private SourceRange[] WithoutTableBoundaryLineBreaks(SourceRange[] removed)
    {
        if (removed.Length == 0) return removed;
        var result = removed.ToList();
        foreach (var span in Spans.Where(span => span.Kind == MarkdownVisualKind.Table))
        {
            int tableStart = span.SourceStart;
            int tableEnd = span.SourceStart + span.SourceLength;
            // 整表都在删除范围内时，边界换行随表格一起删除。
            if (result.Any(range => range.Start <= tableStart && range.End >= tableEnd)) continue;
            for (var boundary = tableEnd;
                 boundary < tableEnd + 2 && boundary < Markdown.Length && Markdown[boundary] == '\n';
                 boundary++)
            {
                var containing = result.FindIndex(range => range.Start <= boundary && boundary < range.End);
                if (containing < 0) continue;
                var range = result[containing];
                result.RemoveAt(containing);
                if (range.Start < boundary) result.Add(new SourceRange(range.Start, boundary));
                if (boundary + 1 < range.End) result.Add(new SourceRange(boundary + 1, range.End));
            }
        }

        return result.Count == removed.Length ? result.ToArray() : MergeRanges(result);
    }

    public MarkdownVisualSpan? VisualAt(int visibleOffset, MarkdownVisualKind kind) =>
        Spans.FirstOrDefault(span => span.Kind == kind && visibleOffset >= span.Start && visibleOffset < span.End) is var found &&
        found.Length > 0 ? found : null;

    private void RebuildProjection(bool ensureEditableBlockBoundaries = false)
    {
        if (ensureEditableBlockBoundaries)
        {
            EnsureSequentialListNumbers();
        }
        Ast = Markdig.Markdown.Parse(Markdown, Pipeline);
        if (ensureEditableBlockBoundaries)
        {
            // Reparse after each normalization so later block spans use the updated source offsets.
            if (EnsureExplicitQuotePrefixes())
                Ast = Markdig.Markdown.Parse(Markdown, Pipeline);
            if (EnsureTableLeadingLineBreaks())
                Ast = Markdig.Markdown.Parse(Markdown, Pipeline);
            if (EnsureTableTrailingLineBreaks())
                Ast = Markdig.Markdown.Parse(Markdown, Pipeline);
            if (EnsureRuleTrailingLineBreaks())
                Ast = Markdig.Markdown.Parse(Markdown, Pipeline);
            if (EnsureQuoteTrailingLineBreaks())
                Ast = Markdig.Markdown.Parse(Markdown, Pipeline);
        }

        var hidden = new bool[Markdown.Length];
        var replacements = new Dictionary<int, Replacement>();
        var sourceStyles = new List<SourceStyle>();

        AnalyzeBlocks(hidden, replacements, sourceStyles);
        var inlineExclusions = InlineExcludedRanges();
        AnalyzeInline(hidden, replacements, sourceStyles, inlineExclusions);
        AnalyzeSafeHtml(hidden, replacements, sourceStyles, inlineExclusions);

        var visible = new StringBuilder(Markdown.Length);
        var visibleToSource = new List<int>(Markdown.Length + 1) { 0 };
        var visibleCharacters = new List<SourceRange>(Markdown.Length);
        var sourceToVisible = new int[Markdown.Length + 1];
        var generatedSpans = new List<MarkdownVisualSpan>();

        for (var source = 0; source < Markdown.Length;)
        {
            sourceToVisible[source] = visible.Length;
            if (replacements.TryGetValue(source, out var replacement))
            {
                var start = visible.Length;
                AppendReplacement(replacement.Text, source, replacement.SourceEnd, visible, visibleToSource, visibleCharacters);
                generatedSpans.Add(new MarkdownVisualSpan(start, replacement.Text.Length, replacement.Kind,
                    source, replacement.SourceEnd - source, replacement.LinkTarget, replacement.ImageUri,
                    replacement.AltText, replacement.ImageWidthPercent));
                for (var index = source; index < replacement.SourceEnd; index++) sourceToVisible[index] = start;
                source = replacement.SourceEnd;
                sourceToVisible[source] = visible.Length;
                continue;
            }
            if (!hidden[source])
            {
                visible.Append(Markdown[source]);
                visibleToSource.Add(source + 1);
                visibleCharacters.Add(new SourceRange(source, source + 1));
            }
            source++;
            sourceToVisible[source] = visible.Length;
        }

        foreach (var style in sourceStyles)
        {
            var start = sourceToVisible[Math.Clamp(style.Start, 0, Markdown.Length)];
            var end = sourceToVisible[Math.Clamp(style.End, 0, Markdown.Length)];
            if (end > start || style.Kind is MarkdownVisualKind.Quote or MarkdownVisualKind.CodeBlock)
                generatedSpans.Add(new MarkdownVisualSpan(start, end - start, style.Kind,
                    style.Start, style.End - style.Start, style.LinkTarget,
                    CodeLabel: style.CodeLabel,
                    CodeContent: style.CodeContent,
                    CodeContentSourceStart: style.CodeContentSourceStart,
                    CodeContentSourceEnd: style.CodeContentSourceEnd));
        }

        VisibleText = visible.ToString();
        _sourceToVisible = sourceToVisible;
        _visibleCharacters = visibleCharacters;
        BuildVisibleAffinityMaps();
        Spans = generatedSpans.OrderBy(span => span.Start).ThenByDescending(span => span.Length).ToArray();
        ProjectionVersion++;
    }

    private bool EnsureTableLeadingLineBreaks()
    {
        const int requiredLineBreaks = 2;
        var insertions = new List<(int Offset, int Count)>();
        foreach (var block in Ast)
        {
            if (!block.GetType().Name.Contains("Table", StringComparison.Ordinal)) continue;
            var start = Math.Clamp(block.Span.Start, 0, Markdown.Length);
            if (start == 0) continue;

            var existingLineBreaks = 0;
            while (start - existingLineBreaks - 1 >= 0 &&
                   existingLineBreaks < requiredLineBreaks &&
                   Markdown[start - existingLineBreaks - 1] == '\n')
                existingLineBreaks++;
            if (existingLineBreaks < requiredLineBreaks)
                insertions.Add((start, requiredLineBreaks - existingLineBreaks));
        }
        if (insertions.Count == 0) return false;

        var output = new StringBuilder(Markdown);
        foreach (var insertion in insertions.OrderByDescending(candidate => candidate.Offset))
            output.Insert(insertion.Offset, new string('\n', insertion.Count));
        Markdown = output.ToString();
        return true;
    }

    private bool EnsureExplicitQuotePrefixes()
    {
        var replacements = new List<(int Start, int End, string Prefix)>();
        foreach (var block in Ast.OfType<QuoteBlock>())
        {
            var blockStart = Math.Clamp(block.Span.Start, 0, Markdown.Length);
            var blockEnd = Math.Clamp(block.Span.End + 1, blockStart, Markdown.Length);
            var firstLineStart = blockStart == 0 ? 0 : Markdown.LastIndexOf('\n', blockStart - 1) + 1;
            var firstLineEnd = Markdown.IndexOf('\n', firstLineStart);
            if (firstLineEnd < 0 || firstLineEnd > blockEnd) firstLineEnd = blockEnd;
            var markerOffset = Markdown.IndexOf('>', firstLineStart, firstLineEnd - firstLineStart);
            if (markerOffset < 0) continue;
            var indentation = Markdown[firstLineStart..markerOffset];
            var prefix = indentation + "> ";

            var lineStart = firstLineEnd < blockEnd ? firstLineEnd + 1 : blockEnd;
            while (lineStart < blockEnd)
            {
                var lineEnd = Markdown.IndexOf('\n', lineStart);
                if (lineEnd < 0 || lineEnd > blockEnd) lineEnd = blockEnd;
                if (!QuoteItemLine().IsMatch(Markdown[lineStart..lineEnd]))
                {
                    var replacementEnd = lineStart;
                    while (replacementEnd < lineEnd &&
                           replacementEnd - lineStart < indentation.Length &&
                           Markdown[replacementEnd] is ' ' or '\t')
                        replacementEnd++;
                    replacements.Add((lineStart, replacementEnd, prefix));
                }

                lineStart = lineEnd + 1;
            }
        }
        if (replacements.Count == 0) return false;

        var output = new StringBuilder(Markdown);
        foreach (var replacement in replacements.Distinct().OrderByDescending(candidate => candidate.Start))
        {
            output.Remove(replacement.Start, replacement.End - replacement.Start);
            output.Insert(replacement.Start, replacement.Prefix);
        }
        Markdown = output.ToString();
        return true;
    }

    private bool EnsureTableTrailingLineBreaks()
    {
        const int requiredLineBreaks = 2;
        var insertions = new List<(int Offset, int Count)>();
        foreach (var block in Ast)
        {
            if (!block.GetType().Name.Contains("Table", StringComparison.Ordinal)) continue;
            var end = Math.Clamp(block.Span.End + 1, 0, Markdown.Length);
            var existingLineBreaks = 0;
            while (end + existingLineBreaks < Markdown.Length && existingLineBreaks < requiredLineBreaks &&
                   Markdown[end + existingLineBreaks] == '\n')
                existingLineBreaks++;
            if (existingLineBreaks < requiredLineBreaks)
                insertions.Add((end, requiredLineBreaks - existingLineBreaks));
        }
        if (insertions.Count == 0) return false;

        var output = new StringBuilder(Markdown);
        foreach (var insertion in insertions.OrderByDescending(candidate => candidate.Offset))
            output.Insert(insertion.Offset, new string('\n', insertion.Count));
        Markdown = output.ToString();
        return true;
    }

    private bool EnsureRuleTrailingLineBreaks()
    {
        var insertions = new List<(int Offset, int Count)>();
        foreach (var block in Ast.OfType<ThematicBreakBlock>())
        {
            var end = Math.Clamp(block.Span.End + 1, 0, Markdown.Length);
            if (end >= Markdown.Length || Markdown[end] != '\n')
                insertions.Add((end, 1));
        }
        if (insertions.Count == 0) return false;

        var output = new StringBuilder(Markdown);
        foreach (var insertion in insertions.OrderByDescending(candidate => candidate.Offset))
            output.Insert(insertion.Offset, new string('\n', insertion.Count));
        Markdown = output.ToString();
        return true;
    }

    private bool EnsureQuoteTrailingLineBreaks()
    {
        var insertions = new List<(int Offset, int Count)>();
        foreach (var block in Ast.OfType<QuoteBlock>())
        {
            var end = Math.Clamp(block.Span.End + 1, 0, Markdown.Length);
            var quoteLineStart = Markdown.LastIndexOf('\n', Math.Max(0, end - 2)) + 1;
            var quoteLine = Markdown[quoteLineStart..Math.Clamp(block.Span.End + 1, quoteLineStart, Markdown.Length)];
            var hasEmptyTrailingQuoteLine = Regex.IsMatch(quoteLine, @"^\s*>\s*$");
            var requiredLineBreaks = hasEmptyTrailingQuoteLine ? 1 : 2;
            var existingLineBreaks = 0;
            while (end + existingLineBreaks < Markdown.Length && existingLineBreaks < requiredLineBreaks &&
                   Markdown[end + existingLineBreaks] == '\n')
                existingLineBreaks++;
            if (existingLineBreaks < requiredLineBreaks)
                insertions.Add((end, requiredLineBreaks - existingLineBreaks));
        }
        if (insertions.Count == 0) return false;

        var output = new StringBuilder(Markdown);
        foreach (var insertion in insertions.OrderByDescending(candidate => candidate.Offset))
            output.Insert(insertion.Offset, new string('\n', insertion.Count));
        Markdown = output.ToString();
        return true;
    }

    private void AnalyzeBlocks(bool[] hidden, Dictionary<int, Replacement> replacements, List<SourceStyle> styles)
    {
        foreach (var block in Ast)
        {
            var start = Math.Clamp(block.Span.Start, 0, Markdown.Length);
            var end = Math.Clamp(block.Span.End + 1, start, Markdown.Length);
            switch (block)
            {
                case HeadingBlock heading:
                    var prefixEnd = FindContentStart(start, end);
                    Hide(hidden, start, prefixEnd);
                    styles.Add(new SourceStyle(prefixEnd, end, heading.Level switch
                    {
                        1 => MarkdownVisualKind.Heading1,
                        2 => MarkdownVisualKind.Heading2,
                        3 => MarkdownVisualKind.Heading3,
                        _ => MarkdownVisualKind.Heading4,
                    }));
                    break;
                case QuoteBlock:
                    styles.Add(new SourceStyle(start, end, MarkdownVisualKind.Quote));
                    HideLinePrefixes(hidden, start, end, QuotePrefix());
                    // Keep Markdown's blank separator after the quote without adding a second
                    // editable empty line to the WYSIWYG projection.
                    if (end + 1 < Markdown.Length &&
                        Markdown[end] == '\n' && Markdown[end + 1] == '\n')
                        hidden[end + 1] = true;
                    break;
                case ListBlock:
                    RewriteListPrefixes(hidden, replacements, start, end);
                    break;
                case FencedCodeBlock:
                    var fencedCode = ReadFencedCodeBlock(start, end);
                    Hide(hidden, start, fencedCode.ContentStart);
                    if (fencedCode.ClosingLineStart is not null)
                        Hide(hidden, fencedCode.ContentEnd, end);
                    styles.Add(new SourceStyle(start, end, MarkdownVisualKind.CodeBlock,
                        CodeLabel: fencedCode.Label,
                        CodeContent: fencedCode.Content,
                        CodeContentSourceStart: fencedCode.ContentStart,
                        CodeContentSourceEnd: fencedCode.ContentEnd));
                    break;
                case CodeBlock:
                    styles.Add(new SourceStyle(start, end, MarkdownVisualKind.CodeBlock,
                        CodeContent: Markdown[start..end]));
                    break;
                case ThematicBreakBlock:
                    // Space placeholders render nothing in any state (selection included); the
                    // visible rule is painted by MarkdownRuleRenderer on top of this line.
                    replacements[start] = new Replacement(end, "                ", MarkdownVisualKind.Rule);
                    // A table already hides its own structural separator. In that case the last
                    // newline is still needed to place the rule on the following visual line.
                    if (start >= 2 && Markdown[start - 1] == '\n' && Markdown[start - 2] == '\n' &&
                        !hidden[start - 2])
                        hidden[start - 1] = true;
                    break;
                default:
                    if (block.GetType().Name.Contains("Table", StringComparison.Ordinal))
                    {
                        replacements[start] = new Replacement(end, "￼", MarkdownVisualKind.Table);
                        // The second source line break is a permanent Markdown block separator.
                        // Keep it out of the editor so text input cannot consume the table boundary.
                        if (start >= 2 &&
                            Markdown[start - 1] == '\n' && Markdown[start - 2] == '\n')
                            hidden[start - 1] = true;
                        // Hide the table's structural line break, but keep the following blank
                        // line editable so text can be inserted after the table boundary.
                        if (end + 1 < Markdown.Length &&
                            Markdown[end] == '\n' && Markdown[end + 1] == '\n')
                            hidden[end] = true;
                    }
                    break;
            }
        }
    }

    private void BuildVisibleAffinityMaps()
    {
        _visibleToSourceBefore = Enumerable.Repeat(int.MaxValue, VisibleText.Length + 1).ToArray();
        _visibleToSourceAfter = new int[VisibleText.Length + 1];
        for (var source = 0; source <= Markdown.Length; source++)
        {
            var visible = Math.Clamp(_sourceToVisible[source], 0, VisibleText.Length);
            _visibleToSourceBefore[visible] = Math.Min(_visibleToSourceBefore[visible], source);
            _visibleToSourceAfter[visible] = Math.Max(_visibleToSourceAfter[visible], source);
        }
        for (var visible = 0; visible < _visibleToSourceBefore.Length; visible++)
        {
            if (_visibleToSourceBefore[visible] == int.MaxValue)
                _visibleToSourceBefore[visible] = visible == 0 ? 0 : _visibleToSourceAfter[visible - 1];
        }
    }

    private void AnalyzeInline(bool[] hidden, Dictionary<int, Replacement> replacements,
        List<SourceStyle> styles, IReadOnlyList<SourceRange> excludedRanges)
    {
        foreach (Match match in ImageSyntax().Matches(Markdown))
        {
            if (Overlaps(match.Index, match.Index + match.Length, excludedRanges) ||
                OverlapsReplacement(replacements, match.Index)) continue;
            replacements[match.Index] = new Replacement(match.Index + match.Length, "\uFFFC", MarkdownVisualKind.Image,
                ImageUri: match.Groups[2].Value,
                AltText: match.Groups[1].Value.Replace("\\]", "]").Replace("\\[", "["),
                ImageWidthPercent: MarkdownImageWidthSyntax.Parse(
                    match.Groups[3].Success ? match.Groups[3].Value : null));
        }
        foreach (Match match in LinkSyntax().Matches(Markdown))
        {
            if (Overlaps(match.Index, match.Index + match.Length, excludedRanges) ||
                OverlapsReplacement(replacements, match.Index)) continue;
            Hide(hidden, match.Index, match.Groups[1].Index);
            Hide(hidden, match.Groups[1].Index + match.Groups[1].Length, match.Index + match.Length);
            styles.Add(new SourceStyle(match.Groups[1].Index, match.Groups[1].Index + match.Groups[1].Length,
                MarkdownVisualKind.Link, match.Groups[2].Value));
        }
        foreach (Match match in BoldItalicSyntax().Matches(Markdown))
        {
            if (Overlaps(match.Index, match.Index + match.Length, excludedRanges)) continue;
            Hide(hidden, match.Index, match.Index + 3);
            Hide(hidden, match.Index + match.Length - 3, match.Index + match.Length);
            styles.Add(new SourceStyle(match.Index + 2, match.Index + match.Length - 2, MarkdownVisualKind.Bold));
            styles.Add(new SourceStyle(match.Index + 3, match.Index + match.Length - 3, MarkdownVisualKind.Italic));
        }
        AnalyzeDelimited(hidden, styles, BoldSyntax(), 2, MarkdownVisualKind.Bold, excludedRanges);
        AnalyzeDelimited(hidden, styles, BoldUnderscoreSyntax(), 2, MarkdownVisualKind.Bold, excludedRanges);
        AnalyzeDelimited(hidden, styles, StrikeSyntax(), 2, MarkdownVisualKind.Strike, excludedRanges);
        AnalyzeDelimited(hidden, styles, CodeSyntax(), 1, MarkdownVisualKind.Code, excludedRanges);
        AnalyzeDelimited(hidden, styles, ItalicSyntax(), 1, MarkdownVisualKind.Italic, excludedRanges);
        AnalyzeDelimited(hidden, styles, ItalicUnderscoreSyntax(), 1, MarkdownVisualKind.Italic, excludedRanges);
    }

    private void AnalyzeSafeHtml(bool[] hidden, Dictionary<int, Replacement> replacements,
        List<SourceStyle> styles, IReadOnlyList<SourceRange> excludedRanges)
    {
        var stacks = new Dictionary<string, Stack<HtmlOpen>>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in HtmlTagSyntax().Matches(Markdown))
        {
            if (Overlaps(match.Index, match.Index + match.Length, excludedRanges)) continue;
            var tag = match.Groups[2].Value.ToLowerInvariant();
            var closing = match.Groups[1].Success;
            var attributes = match.Groups[3].Value;
            if (!TryReadSafeHtmlTag(tag, closing, attributes, out var target, out var alt)) continue;

            if (tag == "br")
            {
                replacements[match.Index] = new Replacement(match.Index + match.Length, "\n", MarkdownVisualKind.Normal);
                continue;
            }
            if (tag == "img")
            {
                replacements[match.Index] = new Replacement(match.Index + match.Length, "\uFFFC", MarkdownVisualKind.Image,
                    ImageUri: target, AltText: alt);
                continue;
            }

            var family = tag switch { "strong" => "b", "em" => "i", "del" or "strike" => "s", _ => tag };
            if (!closing)
            {
                if (!stacks.TryGetValue(family, out var stack)) stacks[family] = stack = new Stack<HtmlOpen>();
                stack.Push(new HtmlOpen(match.Index, match.Index + match.Length, target));
                continue;
            }
            if (!stacks.TryGetValue(family, out var opens) || opens.Count == 0) continue;
            var open = opens.Pop();
            Hide(hidden, open.TagStart, open.ContentStart);
            Hide(hidden, match.Index, match.Index + match.Length);
            var kind = family switch
            {
                "b" => MarkdownVisualKind.Bold,
                "i" => MarkdownVisualKind.Italic,
                "s" => MarkdownVisualKind.Strike,
                "u" => MarkdownVisualKind.Underline,
                "mark" => MarkdownVisualKind.Mark,
                "code" => MarkdownVisualKind.Code,
                "a" => MarkdownVisualKind.Link,
                _ => MarkdownVisualKind.Normal,
            };
            styles.Add(new SourceStyle(open.ContentStart, match.Index, kind, open.Target));
        }
    }

    private SourceRange[] DangerousHtmlRanges() => DangerousHtmlContainerSyntax().Matches(Markdown).Cast<Match>()
        .Select(match => new SourceRange(match.Index, match.Index + match.Length)).ToArray();

    private SourceRange[] InlineExcludedRanges() => DangerousHtmlRanges()
        .Concat(EnumerateBlocks(Ast)
            .OfType<CodeBlock>()
            .Select(block => new SourceRange(
                Math.Clamp(block.Span.Start, 0, Markdown.Length),
                Math.Clamp(block.Span.End + 1, 0, Markdown.Length))))
        .ToArray();

    private static IEnumerable<Block> EnumerateBlocks(ContainerBlock container)
    {
        foreach (var block in container)
        {
            yield return block;
            if (block is not ContainerBlock nested) continue;
            foreach (var descendant in EnumerateBlocks(nested)) yield return descendant;
        }
    }

    private static bool Overlaps(int start, int end, IEnumerable<SourceRange> ranges) =>
        ranges.Any(range => start < range.End && end > range.Start);

    private static bool TryReadSafeHtmlTag(
        string tag, bool closing, string attributes, out string? target, out string? alt)
    {
        target = null;
        alt = null;
        if (tag is not ("b" or "strong" or "i" or "em" or "s" or "del" or "strike" or
            "u" or "mark" or "code" or "br" or "a" or "img")) return false;
        if (closing) return string.IsNullOrWhiteSpace(attributes) && tag is not ("br" or "img");
        if (tag is not ("a" or "img")) return string.IsNullOrWhiteSpace(attributes.Trim().TrimEnd('/'));

        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match attribute in HtmlAttributeSyntax().Matches(attributes))
        {
            var name = attribute.Groups[1].Value;
            if (parsed.ContainsKey(name)) return false;
            parsed[name] = attribute.Groups[3].Success
                ? attribute.Groups[3].Value
                : attribute.Groups[4].Value;
        }
        var residue = HtmlAttributeSyntax().Replace(attributes, string.Empty).Trim().TrimEnd('/').Trim();
        if (residue.Length > 0) return false;
        if (tag == "a")
        {
            if (parsed.Count != 1 || !parsed.TryGetValue("href", out target) || !IsSafeLink(target)) return false;
            return true;
        }
        if (parsed.Keys.Any(key => key is not ("src" or "alt")) ||
            !parsed.TryGetValue("src", out target) || !IsSafeImage(target)) return false;
        parsed.TryGetValue("alt", out alt);
        return true;
    }

    private static bool IsSafeLink(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" or "mailto";

    private static bool IsSafeImage(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
            return !string.IsNullOrWhiteSpace(target) && !target.StartsWith("//", StringComparison.Ordinal);
        return uri.Scheme == Uri.UriSchemeHttps;
    }

    private void AnalyzeDelimited(bool[] hidden, List<SourceStyle> styles, Regex regex, int delimiter,
        MarkdownVisualKind kind, IEnumerable<SourceRange> dangerousRanges)
    {
        foreach (Match match in regex.Matches(Markdown))
        {
            if (Overlaps(match.Index, match.Index + match.Length, dangerousRanges)) continue;
            Hide(hidden, match.Index, match.Index + delimiter);
            Hide(hidden, match.Index + match.Length - delimiter, match.Index + match.Length);
            styles.Add(new SourceStyle(match.Index + delimiter, match.Index + match.Length - delimiter, kind));
        }
    }

    /// <summary>
    /// Keeps ordered-list numbering sequential: a run of ordered items at the same indent
    /// (connected directly or through indented continuation lines) numbers from its own
    /// first marker. Blank lines and top-level non-list lines end a run, so a list
    /// deliberately restarted by Shift+Enter keeps numbering from 1, while deleting a list
    /// line renumbers the items below without touching deliberate start numbers.
    /// </summary>
    private void EnsureSequentialListNumbers()
    {
        string markdown = Markdown;
        var edits = new List<(int Start, int End, string Text)>();
        var runs = new Stack<ListNumberRun>();
        var lineStart = 0;
        while (lineStart < markdown.Length)
        {
            var lineEnd = markdown.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = markdown.Length;
            var match = ListLinePrefix().Match(markdown[lineStart..lineEnd]);
            if (match.Success)
            {
                var marker = match.Groups[2].Value;
                var ordered = char.IsDigit(marker[0]);
                var indent = match.Groups[1].Length;
                var delimiter = ordered ? marker[^1] : '\0';
                while (runs.Count > 0 && runs.Peek().Indent > indent) runs.Pop();
                if (runs.Count == 0 || runs.Peek().Indent != indent ||
                    runs.Peek().IsOrdered != ordered || runs.Peek().Delimiter != delimiter)
                {
                    runs.Push(new ListNumberRun(indent, ordered, delimiter));
                }
                var run = runs.Peek();
                if (ordered &&
                    int.TryParse(marker[..^1], out var number) &&
                    number is >= 0 and < 100_000_000)
                {
                    run.NextNumber ??= number;
                    if (number != run.NextNumber.Value)
                    {
                        var markerStart = lineStart + match.Groups[2].Index;
                        edits.Add((
                            markerStart,
                            markerStart + marker.Length - 1,
                            run.NextNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    }
                    run.NextNumber++;
                }
            }
            else if (markdown[lineStart] is not ' ' and not '\t')
            {
                // 只有空行或非缩进的普通行才断开编号游程；带缩进的纯空格行是列表续行
                // 的占位（规则 3.2 空项转续行、规则 3.4 续行恢复编号），游程跨过它继续，
                // 续行下方的序号才能重排成连续编号。
                runs.Clear();
            }
            lineStart = lineEnd + 1;
        }

        if (edits.Count == 0) return;
        foreach (var edit in edits.OrderByDescending(edit => edit.Start))
        {
            markdown = markdown[..edit.Start] + edit.Text + markdown[edit.End..];
        }
        Markdown = markdown;
    }

    private sealed class ListNumberRun(int indent, bool isOrdered, char delimiter)
    {
        public int Indent { get; } = indent;
        public bool IsOrdered { get; } = isOrdered;
        public char Delimiter { get; } = delimiter;
        public int? NextNumber { get; set; }
    }

    private void RewriteListPrefixes(bool[] hidden, Dictionary<int, Replacement> replacements, int start, int end)
    {
        foreach (Match match in ListLinePrefix().Matches(Markdown[start..end]))
        {
            var absolute = start + match.Index + match.Groups[1].Length;
            var marker = match.Groups[2].Value;
            var task = match.Groups[3].Success;
            var prefixEnd = start + match.Index + match.Length;
            Hide(hidden, absolute, prefixEnd);
            var replacement = task ? (match.Groups[3].Value.Contains('x', StringComparison.OrdinalIgnoreCase) ? "☑ " : "☐ ") :
                char.IsDigit(marker[0]) ? marker + " " : "• ";
            var kind = task
                ? MarkdownVisualKind.Task
                : char.IsDigit(marker[0]) ? MarkdownVisualKind.OrderedListMarker : MarkdownVisualKind.Normal;
            replacements[absolute] = new Replacement(prefixEnd, replacement, kind);
        }
    }

    private void RewriteTable(bool[] hidden, Dictionary<int, Replacement> replacements, int start, int end)
    {
        var lineStart = start;
        while (lineStart < end)
        {
            var lineEnd = Markdown.IndexOf('\n', lineStart);
            if (lineEnd < 0 || lineEnd > end) lineEnd = end;
            var line = Markdown[lineStart..lineEnd];
            if (TableDelimiter().IsMatch(line)) Hide(hidden, lineStart, lineEnd);
            else
            {
                for (var index = lineStart; index < lineEnd; index++)
                {
                    if (Markdown[index] != '|') continue;
                    if (index == lineStart || index == lineEnd - 1) hidden[index] = true;
                    else replacements[index] = new Replacement(index + 1, "  │  ", MarkdownVisualKind.Table);
                }
            }
            lineStart = Math.Min(lineEnd + 1, end);
        }
    }

    private FencedCodeProjection ReadFencedCodeBlock(int start, int end)
    {
        var firstEnd = Markdown.IndexOf('\n', start);
        if (firstEnd < 0 || firstEnd > end) firstEnd = end;
        var openingLine = Markdown[start..firstEnd];
        var markerStart = 0;
        while (markerStart < openingLine.Length && markerStart < 3 && openingLine[markerStart] == ' ')
            markerStart++;
        var fenceCharacter = markerStart < openingLine.Length ? openingLine[markerStart] : '`';
        var fenceLength = 0;
        while (markerStart + fenceLength < openingLine.Length &&
               openingLine[markerStart + fenceLength] == fenceCharacter)
            fenceLength++;
        var labelStart = Math.Min(markerStart + fenceLength, openingLine.Length);
        var label = openingLine[labelStart..].Trim();

        int? closingLineStart = null;
        if (firstEnd < end)
        {
            var candidateBreak = Markdown.LastIndexOf('\n', Math.Max(start, end - 1));
            var candidateStart = candidateBreak >= start ? candidateBreak + 1 : start;
            if (candidateStart > firstEnd &&
                IsClosingFence(Markdown[candidateStart..end], fenceCharacter, fenceLength))
                closingLineStart = candidateStart;
        }

        var contentStart = Math.Min(firstEnd + 1, end);
        var contentEnd = closingLineStart is { } closing
            ? Math.Max(contentStart, closing - 1)
            : end;
        return new FencedCodeProjection(
            contentStart,
            contentEnd,
            closingLineStart,
            label,
            Markdown[contentStart..contentEnd]);
    }

    private static bool IsClosingFence(string line, char fenceCharacter, int openingFenceLength)
    {
        var index = 0;
        while (index < line.Length && index < 3 && line[index] == ' ') index++;
        var runStart = index;
        while (index < line.Length && line[index] == fenceCharacter) index++;
        if (index - runStart < Math.Max(3, openingFenceLength)) return false;
        while (index < line.Length && line[index] is ' ' or '\t') index++;
        return index == line.Length;
    }

    private int FindContentStart(int start, int end)
    {
        while (start < end && (Markdown[start] == '#' || char.IsWhiteSpace(Markdown[start]))) start++;
        return start;
    }

    private void HideLinePrefixes(bool[] hidden, int start, int end, Regex prefix)
    {
        foreach (Match match in prefix.Matches(Markdown[start..end])) Hide(hidden, start + match.Index, start + match.Index + match.Length);
    }

    private static void Hide(bool[] hidden, int start, int end)
    {
        for (var index = Math.Clamp(start, 0, hidden.Length); index < Math.Clamp(end, 0, hidden.Length); index++) hidden[index] = true;
    }

    private static bool OverlapsReplacement(Dictionary<int, Replacement> replacements, int offset) =>
        replacements.Any(entry => offset >= entry.Key && offset < entry.Value.SourceEnd);

    private static void AppendReplacement(string text, int sourceStart, int sourceEnd, StringBuilder output,
        List<int> map, List<SourceRange> visibleCharacters)
    {
        for (var index = 0; index < text.Length; index++)
        {
            output.Append(text[index]);
            map.Add(index == text.Length - 1 ? sourceEnd : sourceStart);
            visibleCharacters.Add(new SourceRange(sourceStart, sourceEnd));
        }
    }

    private static string Normalize(string? text) => (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
    private readonly record struct Replacement(int SourceEnd, string Text, MarkdownVisualKind Kind,
        string? LinkTarget = null, string? ImageUri = null, string? AltText = null,
        double? ImageWidthPercent = null);
    private readonly record struct VisibleTextChange(int Offset, int RemovalLength, int InsertionLength);
    private readonly record struct SourceStyle(
        int Start,
        int End,
        MarkdownVisualKind Kind,
        string? LinkTarget = null,
        string? CodeLabel = null,
        string? CodeContent = null,
        int? CodeContentSourceStart = null,
        int? CodeContentSourceEnd = null);
    private readonly record struct SourceRange(int Start, int End) { public int Length => End - Start; }
    private readonly record struct HtmlOpen(int TagStart, int ContentStart, string? Target);
    private readonly record struct FencedCodeProjection(
        int ContentStart,
        int ContentEnd,
        int? ClosingLineStart,
        string Label,
        string Content);

    [GeneratedRegex(@"!\[((?:\\.|[^\]\n])*)\]\(([^)\n]+)\)(?:,\s*(\d+(?:\.\d+)?)\s*%)?")] private static partial Regex ImageSyntax();
    [GeneratedRegex(@"(?<!!)\[((?:\\.|[^\]\n])+)]\(([^)\n]+)\)")] private static partial Regex LinkSyntax();
    [GeneratedRegex(@"(?<!\*)\*\*\*(?=\S)(.+?)(?<=\S)\*\*\*(?!\*)", RegexOptions.Singleline)] private static partial Regex BoldItalicSyntax();
    [GeneratedRegex(@"(?<!\*)\*\*(?!\*)(?=\S)(.+?)(?<=\S)\*\*(?!\*)", RegexOptions.Singleline)] private static partial Regex BoldSyntax();
    [GeneratedRegex(@"(?<!_)__(?!_)(?=\S)(.+?)(?<=\S)__(?!_)", RegexOptions.Singleline)] private static partial Regex BoldUnderscoreSyntax();
    [GeneratedRegex(@"~~(?=\S)(.+?)(?<=\S)~~", RegexOptions.Singleline)] private static partial Regex StrikeSyntax();
    [GeneratedRegex(@"(?<!`)`([^`\n]+)`(?!`)")] private static partial Regex CodeSyntax();
    [GeneratedRegex(@"(?<!\*)\*(?!\*)(?=\S)(.+?)(?<=\S)\*(?!\*)", RegexOptions.Singleline)] private static partial Regex ItalicSyntax();
    [GeneratedRegex(@"(?<!_)_(?!_)(?=\S)(.+?)(?<=\S)_(?!_)", RegexOptions.Singleline)] private static partial Regex ItalicUnderscoreSyntax();
    // 列表占位符只吸收标记后的一个空格（任务框同理）：多出的空格属于正文，可以在
    // 占位符后面继续输入空格，投影不会把它们吞进占位符。
    [GeneratedRegex(@"(?m)^([ \t]*)([-+*]|\d+[.)])[ \t](\[[ xX]\][ \t])?")] private static partial Regex ListLinePrefix();
    [GeneratedRegex(@"(?m)^\s*>\s?")] private static partial Regex QuotePrefix();
    [GeneratedRegex(@"[ \t]*(?:>[ \t]?)+", RegexOptions.CultureInvariant)] private static partial Regex QuoteLinePrefixAt();
    [GeneratedRegex(@"^[ \t]*>")] private static partial Regex QuoteItemLine();
    [GeneratedRegex(@"^[ \t]*(?:>[ \t]?)+$")] private static partial Regex QuotePrefixesOnly();
    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$")] private static partial Regex TableDelimiter();
    [GeneratedRegex(@"<(/)?([A-Za-z][A-Za-z0-9]*)([^<>]*)>")] private static partial Regex HtmlTagSyntax();
    [GeneratedRegex(@"<(script|style|iframe|object|embed|svg|math)\b[^<>]*>(?:.*?</\1\s*>|.*\z)", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex DangerousHtmlContainerSyntax();
    [GeneratedRegex("\\s+([A-Za-z_:][\\w:.-]*)\\s*=\\s*(?:(['\\\"])(.*?)\\2|([^\\s'\\\"=<>`]+))", RegexOptions.Singleline)] private static partial Regex HtmlAttributeSyntax();
}
