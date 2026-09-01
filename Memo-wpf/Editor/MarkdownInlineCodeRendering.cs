using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using Memo.Markdown;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace Memo.Editor;

internal static class MarkdownInlineCodeStyle
{
    internal const double HorizontalPadding = 1;
    internal const double HorizontalMargin = 1;
    internal const double VerticalPadding = 0;
    internal const double CornerRadius = 3;

    internal static bool IsInlineCode(MarkdownDocumentModel model, MarkdownVisualSpan span)
    {
        int sourceStart = Math.Clamp(span.SourceStart, 0, model.Markdown.Length);
        int sourceEnd = Math.Clamp(
            span.SourceStart + span.SourceLength,
            sourceStart,
            model.Markdown.Length);
        if (sourceStart > 0 && sourceEnd < model.Markdown.Length &&
            model.Markdown[sourceStart - 1] == '`' && model.Markdown[sourceEnd] == '`')
        {
            return true;
        }

        const string openTag = "<code>";
        const string closeTag = "</code>";
        return sourceStart >= openTag.Length &&
            sourceEnd + closeTag.Length <= model.Markdown.Length &&
            model.Markdown.AsSpan(sourceStart - openTag.Length, openTag.Length)
                .Equals(openTag, StringComparison.OrdinalIgnoreCase) &&
            model.Markdown.AsSpan(sourceEnd, closeTag.Length)
                .Equals(closeTag, StringComparison.OrdinalIgnoreCase);
    }
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

        Brush background = FindBrush("MarkdownInlineCodeBackgroundBrush", "AccentMutedBrush", Brushes.BurlyWood);
        foreach (MarkdownVisualSpan span in model.Spans.Where(value =>
                     value.Kind == MarkdownVisualKind.Code &&
                     MarkdownInlineCodeStyle.IsInlineCode(model, value)))
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
                Rect padded = new(
                    rect.Left - MarkdownInlineCodeStyle.HorizontalPadding,
                    rect.Top - MarkdownInlineCodeStyle.VerticalPadding,
                    rect.Width + MarkdownInlineCodeStyle.HorizontalPadding * 2,
                    rect.Height + MarkdownInlineCodeStyle.VerticalPadding * 2);
                drawingContext.DrawRoundedRectangle(
                    background,
                    null,
                    padded,
                    MarkdownInlineCodeStyle.CornerRadius,
                    MarkdownInlineCodeStyle.CornerRadius);
            }
        }
    }

    internal static Brush FindBrush(string key, string fallbackKey, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ??
        Application.Current?.TryFindResource(fallbackKey) as Brush ?? fallback;
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
                     MarkdownInlineCodeStyle.IsInlineCode(model, value)))
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
                     MarkdownInlineCodeStyle.IsInlineCode(model, value)))
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
