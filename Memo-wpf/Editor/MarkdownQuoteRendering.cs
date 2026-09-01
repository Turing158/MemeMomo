using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using Memo.Markdown;
using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;

namespace Memo.Editor;

internal static class MarkdownQuoteSpacing
{
    internal const double HorizontalPadding = 10;
    internal const double VerticalPadding = 5;
    internal const double VerticalMargin = 4;
}

internal sealed class MarkdownQuoteRenderer(Func<IReadOnlyList<MarkdownVisualSpan>> spans)
    : IBackgroundRenderer
{
    private const double CornerRadius = 6;
    private const double AccentWidth = 3;
    private const double AccentInset = 1;

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (textView.Document is not { } document || !textView.VisualLinesValid)
        {
            return;
        }

        Brush background = FindBrush("MarkdownQuoteBackgroundBrush", Brushes.Linen);
        Brush border = FindBrush("MarkdownQuoteBorderBrush", Brushes.Tan);
        Brush accent = FindBrush("AccentPrimaryBrush", Brushes.Peru);
        Pen borderPen = new(border, 1);

        foreach (MarkdownVisualSpan span in spans().Where(value => value.Kind == MarkdownVisualKind.Quote))
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
                continue;
            }

            bool includesFirstLine = visibleLines.Any(line =>
                line.FirstDocumentLine.LineNumber <= firstLine &&
                line.LastDocumentLine.LineNumber >= firstLine);
            bool includesLastLine = visibleLines.Any(line =>
                line.FirstDocumentLine.LineNumber <= lastLine &&
                line.LastDocumentLine.LineNumber >= lastLine);
            double top = visibleLines.Min(line => line.VisualTop) - textView.VerticalOffset +
                (includesFirstLine ? MarkdownQuoteSpacing.VerticalMargin : 0);
            double bottom = visibleLines.Max(line => line.VisualTop + line.Height) -
                textView.VerticalOffset -
                (includesLastLine ? MarkdownQuoteSpacing.VerticalMargin : 0);
            double width = textView.ActualWidth;
            if (bottom <= top || width <= 0)
            {
                continue;
            }

            Rect block = new(0, top, width, bottom - top);
            drawingContext.DrawRoundedRectangle(background, borderPen, block, CornerRadius, CornerRadius);
            double accentHeight = Math.Max(0, block.Height - (AccentInset * 2));
            if (accentHeight > 0)
            {
                Rect accentBar = new(
                    block.Left + AccentInset,
                    block.Top + AccentInset,
                    AccentWidth,
                    accentHeight);
                drawingContext.DrawRoundedRectangle(
                    accent,
                    null,
                    accentBar,
                    AccentWidth / 2,
                    AccentWidth / 2);
            }
        }
    }

    private static Brush FindBrush(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;
}

internal sealed class MarkdownQuotePaddingGenerator(
    Func<IReadOnlyList<MarkdownVisualSpan>> spans,
    TextView textView) : VisualLineElementGenerator
{
    public override int GetFirstInterestedOffset(int startOffset)
    {
        DocumentLine line = CurrentContext.VisualLine.FirstDocumentLine;
        if (startOffset > line.Offset || QuoteForLine(line.Offset, line.EndOffset) is null)
        {
            return -1;
        }

        return line.Offset;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        DocumentLine line = CurrentContext.VisualLine.FirstDocumentLine;
        MarkdownVisualSpan? quote = QuoteForLine(line.Offset, line.EndOffset);
        if (offset != line.Offset || quote is null)
        {
            return null;
        }

        bool hasTopSpacing = line.Offset <= quote.Value.Start;
        bool hasBottomSpacing = line.EndOffset >= quote.Value.End;
        double topSpacing = hasTopSpacing
            ? MarkdownQuoteSpacing.VerticalMargin + MarkdownQuoteSpacing.VerticalPadding
            : 0;
        double bottomSpacing = hasBottomSpacing
            ? MarkdownQuoteSpacing.VerticalMargin + MarkdownQuoteSpacing.VerticalPadding
            : 0;
        Border spacer = new()
        {
            Width = MarkdownQuoteSpacing.HorizontalPadding,
            Height = textView.DefaultLineHeight + topSpacing + bottomSpacing,
            IsHitTestVisible = false,
            Focusable = false
        };
        spacer.SetValue(TextBlock.BaselineOffsetProperty, textView.DefaultBaseline + topSpacing);
        return new QuotePaddingElement(
            spacer,
            topSpacing,
            bottomSpacing,
            textView.DefaultLineHeight);
    }

    private MarkdownVisualSpan? QuoteForLine(int lineStart, int lineEnd) => spans()
        .Where(span => span.Kind == MarkdownVisualKind.Quote &&
            span.Start <= lineEnd && span.End >= lineStart)
        .OrderByDescending(span => span.Length)
        .Select(span => (MarkdownVisualSpan?)span)
        .FirstOrDefault();
}

internal sealed class QuotePaddingElement(
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

internal sealed class MarkdownSelectionRenderer(
    TextArea textArea,
    MarkdownDocumentModel model,
    Func<Brush> selectionBrush,
    Func<Brush> inlineCodeSelectionBrush) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (textView.Document is null || !textView.VisualLinesValid || textArea.Selection.IsEmpty)
        {
            return;
        }

        Pen? selectionBorder = textArea.SelectionBorder;
        BackgroundGeometryBuilder normalGeometry = CreateGeometryBuilder(selectionBorder);
        BackgroundGeometryBuilder quoteGeometry = CreateGeometryBuilder(selectionBorder);
        BackgroundGeometryBuilder quoteBridgeGeometry = CreateGeometryBuilder(null, cornerRadius: 0);
        List<Rect> quoteRects = [];

        foreach (SelectionSegment segment in textArea.Selection.Segments)
        {
            foreach (Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(
                textView,
                segment,
                textArea.Selection.EnableVirtualSpace))
            {
                if (!TryGetQuoteContentBounds(
                    textView,
                    rect.Top,
                    out double contentLeft,
                    out double contentTop,
                    out double contentHeight))
                {
                    normalGeometry.AddRectangle(textView, rect);
                    continue;
                }

                double selectionLeft = Math.Max(rect.Left, contentLeft);
                if (rect.Right > selectionLeft)
                {
                    quoteRects.Add(new Rect(
                        selectionLeft,
                        contentTop,
                        rect.Right - selectionLeft,
                        contentHeight));
                }
            }
        }

        foreach (Rect rect in quoteRects)
        {
            quoteGeometry.AddRectangle(textView, rect);
        }
        AddQuoteSelectionBridges(
            textView,
            quoteBridgeGeometry,
            quoteRects,
            textArea.SelectionCornerRadius);

        Brush currentSelectionBrush = selectionBrush();
        DrawGeometry(drawingContext, normalGeometry, currentSelectionBrush, selectionBorder);
        DrawGeometry(drawingContext, quoteGeometry, currentSelectionBrush, selectionBorder);
        DrawGeometry(drawingContext, quoteBridgeGeometry, currentSelectionBrush, null);
        DrawInlineCodeSelection(
            textView,
            drawingContext,
            textArea.Selection.Segments,
            model,
            inlineCodeSelectionBrush());
    }

    private BackgroundGeometryBuilder CreateGeometryBuilder(
        Pen? selectionBorder,
        double? cornerRadius = null) => new()
        {
            AlignToWholePixels = true,
            BorderThickness = selectionBorder?.Thickness ?? 0,
            ExtendToFullWidthAtLineEnd = textArea.Selection.EnableVirtualSpace,
            CornerRadius = cornerRadius ?? textArea.SelectionCornerRadius
        };

    private static void DrawGeometry(
        DrawingContext drawingContext,
        BackgroundGeometryBuilder builder,
        Brush brush,
        Pen? border)
    {
        Geometry? geometry = builder.CreateGeometry();
        if (geometry is not null)
        {
            drawingContext.DrawGeometry(brush, border, geometry);
        }
    }

    private static void DrawInlineCodeSelection(
        TextView textView,
        DrawingContext drawingContext,
        IEnumerable<SelectionSegment> selections,
        MarkdownDocumentModel model,
        Brush brush)
    {
        if (textView.Document is null)
        {
            return;
        }

        foreach (SelectionSegment selection in selections)
        {
            if (selection.Length == 0)
            {
                continue;
            }

            foreach (MarkdownVisualSpan span in model.Spans.Where(value =>
                         value.Kind == MarkdownVisualKind.Code &&
                         MarkdownInlineCodeStyle.IsInlineCode(model, value)))
            {
                int start = Math.Max(selection.StartOffset, span.Start);
                int end = Math.Min(selection.EndOffset, span.End);
                if (end <= start)
                {
                    continue;
                }

                foreach (Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(
                             textView,
                             new SelectionSegment(start, end),
                             extendToFullWidthAtLineEnd: false))
                {
                    double leftPadding = start == span.Start
                        ? MarkdownInlineCodeStyle.HorizontalPadding
                        : 0;
                    double rightPadding = end == span.End
                        ? MarkdownInlineCodeStyle.HorizontalPadding
                        : 0;
                    Rect padded = new(
                        rect.Left - leftPadding,
                        rect.Top - MarkdownInlineCodeStyle.VerticalPadding,
                        rect.Width + leftPadding + rightPadding,
                        rect.Height + MarkdownInlineCodeStyle.VerticalPadding * 2);
                    drawingContext.DrawRoundedRectangle(
                        brush,
                        null,
                        padded,
                        MarkdownInlineCodeStyle.CornerRadius,
                        MarkdownInlineCodeStyle.CornerRadius);
                }
            }
        }
    }

    private static void AddQuoteSelectionBridges(
        TextView textView,
        BackgroundGeometryBuilder geometry,
        IReadOnlyList<Rect> rects,
        double cornerRadius)
    {
        if (cornerRadius <= 0)
        {
            return;
        }

        for (int firstIndex = 0; firstIndex < rects.Count; firstIndex++)
        {
            Rect first = rects[firstIndex];
            for (int secondIndex = firstIndex + 1; secondIndex < rects.Count; secondIndex++)
            {
                Rect second = rects[secondIndex];
                double boundary = Math.Abs(first.Bottom - second.Top) < 0.01
                    ? first.Bottom
                    : Math.Abs(second.Bottom - first.Top) < 0.01
                        ? second.Bottom
                        : double.NaN;
                if (!double.IsFinite(boundary))
                {
                    continue;
                }

                double left = Math.Max(first.Left, second.Left);
                double right = Math.Min(first.Right, second.Right);
                if (right <= left)
                {
                    continue;
                }

                geometry.AddRectangle(textView, new Rect(
                    left,
                    boundary - cornerRadius,
                    right - left,
                    cornerRadius * 2));
            }
        }
    }

    private static bool TryGetQuoteContentBounds(
        TextView textView,
        double selectionTop,
        out double contentLeft,
        out double contentTop,
        out double contentHeight)
    {
        foreach (VisualLine visualLine in textView.VisualLines)
        {
            QuotePaddingElement? quotePadding = visualLine.Elements
                .OfType<QuotePaddingElement>()
                .FirstOrDefault();
            CodeBlockPaddingElement? codeBlockPadding = visualLine.Elements
                .OfType<CodeBlockPaddingElement>()
                .FirstOrDefault();
            if (quotePadding is null && codeBlockPadding is null)
            {
                continue;
            }

            double horizontalPadding = codeBlockPadding is not null
                ? MarkdownCodeBlockStyle.HorizontalPadding
                : MarkdownQuoteSpacing.HorizontalPadding;
            PaddingElement padding = codeBlockPadding is not null
                ? new PaddingElement(
                    codeBlockPadding.TopSpacing,
                    codeBlockPadding.BottomSpacing,
                    codeBlockPadding.ContentHeight)
                : new PaddingElement(
                    quotePadding!.TopSpacing,
                    quotePadding.BottomSpacing,
                    quotePadding.ContentHeight);
            foreach (System.Windows.Media.TextFormatting.TextLine textLine in visualLine.TextLines)
            {
                double lineTop = visualLine.GetTextLineVisualYPosition(
                    textLine,
                    VisualYPosition.LineTop) - textView.VerticalOffset;
                if (Math.Abs(lineTop - selectionTop) >= 0.01)
                {
                    continue;
                }

                bool hasExpandedQuoteSpacing =
                    textLine.Height > padding.ContentHeight + 0.01;
                contentLeft = horizontalPadding - textView.HorizontalOffset;
                contentTop = lineTop + (hasExpandedQuoteSpacing ? padding.TopSpacing : 0);
                contentHeight = hasExpandedQuoteSpacing
                    ? Math.Max(0, textLine.Height - padding.TopSpacing - padding.BottomSpacing)
                    : textLine.Height;
                return true;
            }
        }

        contentLeft = 0;
        contentTop = 0;
        contentHeight = 0;
        return false;
    }

    private readonly record struct PaddingElement(double TopSpacing, double BottomSpacing, double ContentHeight);
}
