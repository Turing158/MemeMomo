using System.Windows;
using ICSharpCode.AvalonEdit.Rendering;
using MemeMomo.Markdown;

namespace MemeMomo.Editor;

/// <summary>Includes rendered list markers in AvalonEdit's inherited wrap indentation.</summary>
internal sealed class MarkdownListIndentation(MarkdownDocumentModel model) : VisualLineElementGenerator
{
    public override int GetFirstInterestedOffset(int startOffset) => model.Spans
        .Where(span => span.Start >= startOffset && IsTextMarker(span))
        .Select(span => span.Start).DefaultIfEmpty(-1).Min();

    public override VisualLineElement? ConstructElement(int offset)
    {
        MarkdownVisualSpan? marker = model.Spans
            .Where(span => span.Start == offset && IsTextMarker(span))
            .Select(span => (MarkdownVisualSpan?)span).FirstOrDefault();
        return marker is { } span ? new ListMarkerText(CurrentContext.VisualLine, span.Length) : null;
    }

    private bool IsTextMarker(MarkdownVisualSpan span) =>
        span.Kind == MarkdownVisualKind.OrderedListMarker ||
        span.Kind == MarkdownVisualKind.Normal && span.Length == 2 &&
        model.VisibleText.AsSpan(span.Start, span.Length).SequenceEqual("• ") &&
        model.Markdown[span.SourceStart] is '-' or '+' or '*';

    private sealed class ListMarkerText(VisualLine line, int length) : VisualLineText(line, length)
    {
        // Keep the glyphs and normal caret/selection mapping; only the layout's
        // whitespace scan treats the marker as part of the first-line indent.
        public override bool IsWhitespace(int visualColumn) => true;
        protected override VisualLineText CreateInstance(int length) => new ListMarkerText(ParentVisualLine, length);
    }
}

internal sealed class MarkdownTaskMarkerElement(int length, UIElement control) : InlineObjectElement(length, control)
{
    public override bool IsWhitespace(int visualColumn) => true;
}
