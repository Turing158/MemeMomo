using System.Text;

namespace MemeMomo.Markdown;

internal static class MarkdownClipboardText
{
    /// <summary>Restore images, links and list prefixes; partial content selections keep only their selected text.</summary>
    internal static string? RestoreSources(MarkdownDocumentModel model, int visibleStart, int visibleEnd)
        => RestoreSources(model, visibleStart, visibleEnd, out _);

    internal static string? RestoreSources(MarkdownDocumentModel model, int visibleStart, int visibleEnd,
        out bool containsListPrefix)
    {
        containsListPrefix = false;
        int start = Math.Clamp(Math.Min(visibleStart, visibleEnd), 0, model.VisibleText.Length);
        int end = Math.Clamp(Math.Max(visibleStart, visibleEnd), start, model.VisibleText.Length);
        if (start == end) return null;
        int selectedSourceEnd = model.SourceOffsetFromVisible(end, trailingAffinity: false);
        int lineEnd = model.Markdown.IndexOf('\n', selectedSourceEnd);
        if (lineEnd < 0) lineEnd = model.Markdown.Length;
        int prefixSearchEnd = Math.Max(end, model.VisibleOffsetFromSource(lineEnd));
        // Include the marker on the final selected line even if only its leading
        // indentation is selected; restore no prefixes from unrelated lines.
        var prefixes = model.Spans.Where(span => span.End >= start && span.Start <= prefixSearchEnd && IsListMarker(model, span))
            .Select(span => RestoreListPrefix(model, span)).ToArray();
        // List selection begins after the unselectable prefix. Include that prefix
        // only when the complete content of the first selected line is copied.
        foreach (var prefix in prefixes)
        {
            if (prefix.End == start && end > start &&
                end >= model.VisibleOffsetFromSource(MarkdownListLine.Read(model.Markdown, prefix.SourceStart).End))
            {
                start = prefix.Start;
                break;
            }
        }
        var spans = model.Spans.Where(span => span.Kind is MarkdownVisualKind.Image or MarkdownVisualKind.Link)
            .Concat(prefixes).Where(span => span.Start < end && span.End > start)
            .OrderBy(span => span.Start).ThenByDescending(span => span.Length).ToArray();
        if (spans.Length == 0) return null;
        StringBuilder result = new();
        int offset = start;
        foreach (var span in spans)
        {
            int from = Math.Max(offset, span.Start);
            int to = Math.Min(end, span.End);
            if (from >= to) continue;
            result.Append(model.VisibleText, offset, from - offset);
            if (span.Kind != MarkdownVisualKind.Link)
            {
                containsListPrefix |= span.Kind != MarkdownVisualKind.Image;
                // Quote block separators can contain a hidden line break. Keep
                // it between copied items so the next list stays outside the quote.
                if (span.Kind != MarkdownVisualKind.Image && from > start)
                {
                    int source = span.SourceStart;
                    while (source > 0 && model.Markdown[source - 1] == '\n' &&
                           model.VisibleOffsetFromSource(source - 1) == span.Start)
                    {
                        result.Append('\n');
                        source--;
                    }
                }
                result.Append(model.Markdown, span.SourceStart, span.SourceLength);
            }
            else if (from == span.Start && to == span.End && model.TryGetLinkSourceRange(span, out int sourceStart, out int sourceEnd))
                result.Append(model.Markdown, sourceStart, sourceEnd - sourceStart);
            else
                result.Append('[').Append(EscapeLabel(model.VisibleText[from..to])).Append("](").Append(span.LinkTarget).Append(')');
            offset = to;
        }
        result.Append(model.VisibleText, offset, end - offset);
        return result.ToString();
    }

    internal static bool IsListMarker(MarkdownDocumentModel model, MarkdownVisualSpan span) =>
        span.Kind == MarkdownVisualKind.Task ||
        span.Kind == MarkdownVisualKind.Normal && span.Length == 2 &&
        model.VisibleText.AsSpan(span.Start, span.Length).SequenceEqual("• ") &&
        model.Markdown[span.SourceStart] is '-' or '+' or '*';

    private static MarkdownVisualSpan RestoreListPrefix(MarkdownDocumentModel model, MarkdownVisualSpan marker)
    {
        int lineStart = marker.SourceStart == 0 ? 0 : model.Markdown.LastIndexOf('\n', marker.SourceStart - 1) + 1;
        int start = model.VisibleOffsetFromSource(lineStart);
        return marker with
        {
            Start = start,
            Length = marker.End - start,
            SourceStart = lineStart,
            SourceLength = marker.SourceStart + marker.SourceLength - lineStart
        };
    }

    internal static string EscapeLabel(string label) => label.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal);
}
