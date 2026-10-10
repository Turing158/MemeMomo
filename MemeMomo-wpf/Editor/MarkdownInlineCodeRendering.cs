using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using MemeMomo.Markdown;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace MemeMomo.Editor;

internal static class MarkdownInlineCodeStyle
{
    internal const double HorizontalPadding = 1;
    internal const double HorizontalMargin = 1;
    internal const double VerticalPadding = 0;
    internal const double CornerRadius = 3;

    internal static Rect GetContentRect(TextView textView, Rect rect)
    {
        rect = HtmlAlignmentLayout.ClipSelectionRect(textView, rect);
        // Block padding expands the formatted line, but inline backgrounds belong
        // only to its text content, just like the normal selection highlight.
        foreach (VisualLine line in textView.VisualLines)
        {
            if (!line.Elements.OfType<QuotePaddingElement>().Any())
            {
                continue;
            }

            foreach (System.Windows.Media.TextFormatting.TextLine textLine in line.TextLines)
            {
                double lineTop = line.GetTextLineVisualYPosition(textLine, VisualYPosition.LineTop) -
                    textView.VerticalOffset;
                if (Math.Abs(lineTop - rect.Top) >= 0.01)
                {
                    continue;
                }

                double top = line.GetTextLineVisualYPosition(textLine, VisualYPosition.TextTop) -
                    textView.VerticalOffset;
                double bottom = line.GetTextLineVisualYPosition(textLine, VisualYPosition.TextBottom) -
                    textView.VerticalOffset;
                return new Rect(rect.Left, top, rect.Width, bottom - top);
            }
        }

        return rect;
    }

    internal static bool IsInlineCode(MarkdownVisualSpan span) => span.Kind == MarkdownVisualKind.Code;
}
internal sealed class MarkdownInlineCodeRenderer(
    MarkdownDocumentModel model) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (textView.Document is not { } document || !textView.VisualLinesValid)
        {
            return;
        }

        Brush background = FindBrush(textView, "MarkdownInlineCodeBackgroundBrush", "AccentMutedBrush", Brushes.BurlyWood);
        foreach (MarkdownVisualSpan span in model.Spans.Where(value =>
                     value.Kind == MarkdownVisualKind.Code &&
                     MarkdownInlineCodeStyle.IsInlineCode(value)))
        {
            int start = Math.Clamp(span.Start, 0, document.TextLength);
            int length = Math.Clamp(span.Length, 0, document.TextLength - start);
            if (length <= 0)
            {
                continue;
            }

            SelectionSegment segment = new(start, start + length);
            foreach (Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(
                         textView,
                         segment,
                         extendToFullWidthAtLineEnd: false))
            {
                Rect content = MarkdownInlineCodeStyle.GetContentRect(textView, rect);
                Rect padded = new(
                    content.Left - MarkdownInlineCodeStyle.HorizontalPadding,
                    content.Top - MarkdownInlineCodeStyle.VerticalPadding,
                    content.Width + MarkdownInlineCodeStyle.HorizontalPadding * 2,
                    content.Height + MarkdownInlineCodeStyle.VerticalPadding * 2);
                drawingContext.DrawRoundedRectangle(
                    background,
                    null,
                    padded,
                    MarkdownInlineCodeStyle.CornerRadius,
                    MarkdownInlineCodeStyle.CornerRadius);
            }
        }
    }

    internal static Brush FindBrush(TextView textView, string key, string fallbackKey, Brush fallback) =>
        textView.TryFindResource(key) as Brush ??
        textView.TryFindResource(fallbackKey) as Brush ?? fallback;
}

internal sealed class MarkdownInlineCodePaddingGenerator(
    MarkdownDocumentModel model) : VisualLineElementGenerator
{
    public override int GetFirstInterestedOffset(int startOffset)
    {
        DocumentLine line = CurrentContext.VisualLine.FirstDocumentLine;
        int interestedOffset = int.MaxValue;
        foreach (MarkdownVisualSpan span in model.Spans.Where(value =>
                     value.Kind == MarkdownVisualKind.Code &&
                     value.Length > 0 &&
                     MarkdownInlineCodeStyle.IsInlineCode(value)))
        {
            if (span.Start >= startOffset && span.Start <= line.EndOffset)
            {
                interestedOffset = Math.Min(interestedOffset, span.Start);
            }

            if (span.End >= startOffset && span.End <= line.EndOffset)
            {
                interestedOffset = Math.Min(interestedOffset, span.End);
            }
        }

        return interestedOffset == int.MaxValue ? -1 : interestedOffset;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        bool isLeading = false;
        bool isBoundary = false;
        foreach (MarkdownVisualSpan span in model.Spans.Where(value =>
                     value.Kind == MarkdownVisualKind.Code &&
                     value.Length > 0 &&
                     MarkdownInlineCodeStyle.IsInlineCode(value)))
        {
            if (span.Start == offset)
            {
                isLeading = true;
                isBoundary = true;
            }

            if (span.End == offset)
            {
                isBoundary = true;
            }
        }

        if (!isBoundary)
        {
            return null;
        }

        Border spacer = new()
        {
            Width = MarkdownInlineCodeStyle.HorizontalPadding +
                MarkdownInlineCodeStyle.HorizontalMargin,
            Height = 1,
            IsHitTestVisible = false,
            Focusable = false
        };
        spacer.SetValue(TextBlock.BaselineOffsetProperty, 1d);
        return new InlineCodePaddingElement(spacer, isLeading);
    }
}

internal sealed class InlineCodePaddingElement(
    UIElement spacer,
    bool isLeading) : InlineObjectElement(0, spacer)
{
    public override bool HandlesLineBorders => true;

    public override int GetVisualColumn(int relativeTextOffset) =>
        isLeading ? VisualColumn + VisualLength : VisualColumn;

    public override int GetNextCaretPosition(
        int visualColumn,
        System.Windows.Documents.LogicalDirection direction,
        CaretPositioningMode mode)
    {
        int caret = isLeading ? VisualColumn + VisualLength : VisualColumn;
        if (direction == System.Windows.Documents.LogicalDirection.Forward && visualColumn < caret)
        {
            return caret;
        }

        if (direction == System.Windows.Documents.LogicalDirection.Backward && visualColumn > caret)
        {
            return caret;
        }

        return -1;
    }

    public override bool IsWhitespace(int visualColumn) => true;
}
