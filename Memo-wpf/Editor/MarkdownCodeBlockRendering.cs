using System.Globalization;
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
using FlowDirection = System.Windows.FlowDirection;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace Memo.Editor;

internal static class MarkdownCodeBlockStyle
{
    internal const double HorizontalPadding = 10;
    internal const double VerticalPadding = 3;
    internal const double VerticalMargin = 2;
    internal const double CornerRadius = 7;
    internal const double ButtonInset = 6;
    internal const double LabelFontSize = 9.5;
    internal const double LabelLineHeight = 12;
    internal const double LabelCodeGap = 2;
}

internal readonly record struct MarkdownCodeBlockLayout(
    Rect Bounds,
    bool IncludesFirstLine,
    bool IncludesLastLine);

internal sealed class MarkdownCodeBlockRenderer(MarkdownDocumentModel model) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (textView.Document is not { } document || !textView.VisualLinesValid)
        {
            return;
        }

        Brush background = FindBrush("MarkdownCodeBlockBackgroundBrush", Brushes.LightSteelBlue);
        Brush border = FindBrush("MarkdownCodeBlockBorderBrush", Brushes.SlateGray);
        Brush labelForeground = FindBrush("TextSecondaryBrush", Brushes.DimGray);
        Pen borderPen = new(border, 1);

        foreach (MarkdownVisualSpan span in model.Spans.Where(value => value.Kind == MarkdownVisualKind.CodeBlock))
        {
            if (!MarkdownCodeBlockLayoutCalculator.TryGet(textView, document, span, out MarkdownCodeBlockLayout layout))
            {
                continue;
            }

            drawingContext.DrawRoundedRectangle(
                background,
                borderPen,
                layout.Bounds,
                MarkdownCodeBlockStyle.CornerRadius,
                MarkdownCodeBlockStyle.CornerRadius);

            if (layout.IncludesFirstLine && !string.IsNullOrWhiteSpace(span.CodeLabel))
            {
                DrawLabel(textView, drawingContext, labelForeground, layout.Bounds, span.CodeLabel);
            }
        }
    }

    private static void DrawLabel(
        TextView textView,
        DrawingContext drawingContext,
        Brush labelForeground,
        Rect bounds,
        string label)
    {
        double labelWidth = Math.Max(1, bounds.Width - MarkdownCodeBlockStyle.HorizontalPadding * 2 - 8);
        double pixelsPerDip = VisualTreeHelper.GetDpi(textView).PixelsPerDip;
        FormattedText formatted = new(
            label,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Cascadia Mono, Consolas, Microsoft YaHei UI"),
            MarkdownCodeBlockStyle.LabelFontSize,
            labelForeground,
            pixelsPerDip)
        {
            MaxTextWidth = labelWidth,
            MaxLineCount = 1,
            LineHeight = MarkdownCodeBlockStyle.LabelLineHeight,
            Trimming = TextTrimming.CharacterEllipsis
        };
        drawingContext.DrawText(
            formatted,
            new Point(bounds.Left + MarkdownCodeBlockStyle.HorizontalPadding, bounds.Top + MarkdownCodeBlockStyle.VerticalPadding));
    }

    internal MarkdownVisualSpan? HitTest(TextView textView, Point point)
    {
        if (textView.Document is not { } document || !textView.VisualLinesValid)
        {
            return null;
        }

        foreach (MarkdownVisualSpan span in model.Spans
            .Where(value => value.Kind == MarkdownVisualKind.CodeBlock)
            .OrderByDescending(value => value.Length))
        {
            if (TryGetCardBounds(textView, span, out Rect bounds) && bounds.Contains(point))
            {
                return span;
            }
        }

        return null;
    }

    internal bool TryGetCardBounds(TextView textView, MarkdownVisualSpan span, out Rect bounds)
    {
        if (textView.Document is not { } document ||
            !textView.VisualLinesValid ||
            !MarkdownCodeBlockLayoutCalculator.TryGet(textView, document, span, out MarkdownCodeBlockLayout layout))
        {
            bounds = default;
            return false;
        }

        bounds = layout.Bounds;
        return bounds.Width > 0;
    }

    private static Brush FindBrush(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;
}

internal static class MarkdownCodeBlockLayoutCalculator
{
    internal static bool TryGet(
        TextView textView,
        TextDocument document,
        MarkdownVisualSpan span,
        out MarkdownCodeBlockLayout layout)
    {
        int startOffset = Math.Clamp(span.Start, 0, document.TextLength);
        int endOffset = Math.Clamp(span.End, startOffset, document.TextLength);
        int firstLine = document.GetLineByOffset(startOffset).LineNumber;
        int lastLine = document.GetLineByOffset(endOffset).LineNumber;
        VisualLine[] visibleLines = textView.VisualLines
            .Where(line => line.LastDocumentLine.LineNumber >= firstLine &&
                line.FirstDocumentLine.LineNumber <= lastLine)
            .ToArray();
        if (visibleLines.Length == 0)
        {
            layout = default;
            return false;
        }

        bool includesFirstLine = visibleLines.Any(line =>
            line.FirstDocumentLine.LineNumber <= firstLine &&
            line.LastDocumentLine.LineNumber >= firstLine);
        bool includesLastLine = visibleLines.Any(line =>
            line.FirstDocumentLine.LineNumber <= lastLine &&
            line.LastDocumentLine.LineNumber >= lastLine);
        double top = visibleLines.Min(line => line.VisualTop) - textView.VerticalOffset +
            (includesFirstLine ? MarkdownCodeBlockStyle.VerticalMargin : 0);
        double bottom = visibleLines.Max(line => line.VisualTop + line.Height) -
            textView.VerticalOffset -
            (includesLastLine ? MarkdownCodeBlockStyle.VerticalMargin : 0);
        double width = textView.ActualWidth;
        if (bottom <= top || width <= 0)
        {
            layout = default;
            return false;
        }

        layout = new MarkdownCodeBlockLayout(
            new Rect(0, top, width, bottom - top),
            includesFirstLine,
            includesLastLine);
        return true;
    }
}

internal sealed class MarkdownCodeBlockPaddingGenerator(
    MarkdownDocumentModel model,
    TextView textView) : VisualLineElementGenerator
{
    public override int GetFirstInterestedOffset(int startOffset)
    {
        DocumentLine line = CurrentContext.VisualLine.FirstDocumentLine;
        if (startOffset > line.Offset || CodeBlockForLine(line.Offset, line.EndOffset) is null)
        {
            return -1;
        }

        return line.Offset;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        DocumentLine line = CurrentContext.VisualLine.FirstDocumentLine;
        MarkdownVisualSpan? codeBlock = CodeBlockForLine(line.Offset, line.EndOffset);
        if (offset != line.Offset || codeBlock is null)
        {
            return null;
        }

        bool hasTopSpacing = line.Offset <= codeBlock.Value.Start;
        bool hasBottomSpacing = line.EndOffset >= codeBlock.Value.End;
        bool hasLabel = hasTopSpacing && !string.IsNullOrWhiteSpace(codeBlock.Value.CodeLabel);
        double topSpacing = hasTopSpacing
            ? MarkdownCodeBlockStyle.VerticalMargin + MarkdownCodeBlockStyle.VerticalPadding +
              (hasLabel
                  ? MarkdownCodeBlockStyle.LabelLineHeight + MarkdownCodeBlockStyle.LabelCodeGap
                  : 0)
            : 0;
        double bottomSpacing = hasBottomSpacing
            ? MarkdownCodeBlockStyle.VerticalMargin + MarkdownCodeBlockStyle.VerticalPadding
            : 0;
        Border spacer = new()
        {
            Width = MarkdownCodeBlockStyle.HorizontalPadding,
            Height = textView.DefaultLineHeight + topSpacing + bottomSpacing,
            IsHitTestVisible = false,
            Focusable = false
        };
        spacer.SetValue(TextBlock.BaselineOffsetProperty, textView.DefaultBaseline + topSpacing);
        return new CodeBlockPaddingElement(
            spacer,
            topSpacing,
            bottomSpacing,
            textView.DefaultLineHeight);
    }

    private MarkdownVisualSpan? CodeBlockForLine(int lineStart, int lineEnd) => model.Spans
        .Where(span => span.Kind == MarkdownVisualKind.CodeBlock &&
            span.Start <= lineEnd && span.End >= lineStart)
        .OrderByDescending(span => span.Length)
        .Select(span => (MarkdownVisualSpan?)span)
        .FirstOrDefault();
}

internal sealed class CodeBlockPaddingElement(
    UIElement spacer,
    double topSpacing,
    double bottomSpacing,
    double contentHeight) : InlineObjectElement(0, spacer)
{
    internal double TopSpacing { get; } = topSpacing;
    internal double BottomSpacing { get; } = bottomSpacing;
    internal double ContentHeight { get; } = contentHeight;

    public override bool HandlesLineBorders => true;

    public override int GetVisualColumn(int relativeTextOffset) => VisualColumn + VisualLength;

    public override int GetNextCaretPosition(
        int visualColumn,
        System.Windows.Documents.LogicalDirection direction,
        CaretPositioningMode mode)
    {
        int end = VisualColumn + VisualLength;
        if (direction == System.Windows.Documents.LogicalDirection.Forward && visualColumn < end)
        {
            return end;
        }

        if (direction == System.Windows.Documents.LogicalDirection.Backward && visualColumn > end)
        {
            return end;
        }

        return -1;
    }

    public override bool IsWhitespace(int visualColumn) => true;
}
