using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using MemeMomo.Markdown;
using FlowDirection = System.Windows.FlowDirection;
using Size = System.Windows.Size;
using Point = System.Windows.Point;

namespace MemeMomo.Editor;

/// <summary>Adds space only to the first and last formatted rows of a nonempty paragraph.</summary>
internal sealed class MarkdownParagraphSpacing(Func<IReadOnlyList<MarkdownVisualSpan>> spans)
    : VisualLineElementGenerator, IVisualLineTransformer
{
    internal const double VerticalPadding = 2;

    public override int GetFirstInterestedOffset(int startOffset)
    {
        DocumentLine line = CurrentContext.VisualLine.FirstDocumentLine;
        if (line.Length == 0 || string.IsNullOrWhiteSpace(CurrentContext.Document.GetText(line)) ||
            spans().Any(span => span.Kind is MarkdownVisualKind.Table or MarkdownVisualKind.CodeBlock or MarkdownVisualKind.Rule &&
                span.Start <= line.EndOffset && span.End > line.Offset)) return -1;
        if (startOffset <= line.Offset) return line.Offset;
        return startOffset <= line.EndOffset ? line.EndOffset : -1;
    }

    public override VisualLineElement ConstructElement(int offset) =>
        new ParagraphPaddingElement(offset == CurrentContext.VisualLine.FirstDocumentLine.Offset);

    public void Transform(ITextRunConstructionContext context, IList<VisualLineElement> elements)
    {
        ParagraphPaddingElement[] padding = elements.OfType<ParagraphPaddingElement>().ToArray();
        double width = context.TextView.ActualWidth;
        if (padding.Length == 0 || !double.IsFinite(width) || width <= 1) return;

        // Measure after all styles and row spacing, hiding our own zero-width elements.
        // Using the actual edge-row metrics also handles large fonts and inline images.
        using TextFormatter formatter = TextFormatter.Create(TextOptions.GetTextFormattingMode(context.TextView));
        var source = new ParagraphTextSource(context, elements, width)
        {
            PixelsPerDip = VisualTreeHelper.GetDpi(context.TextView).PixelsPerDip
        };
        var properties = new ParagraphProperties(context);
        TextLineBreak? previousBreak = null;
        int offset = 0;
        try
        {
            while (offset < context.VisualLine.VisualLength)
            {
                using TextLine row = formatter.FormatLine(source, offset, width, properties, previousBreak);
                int end = offset + row.Length;
                if (offset == 0)
                {
                    foreach (var element in padding.Where(element => element.IsLeading))
                        element.SetMetrics(row.Height + VerticalPadding, row.Baseline + VerticalPadding);
                    properties.StartWrappedRows(row, elements, width);
                }
                foreach (var element in padding.Where(element => !element.IsLeading))
                    element.SetMetrics(row.Height + VerticalPadding, row.Baseline);
                if (end <= offset) break;
                offset = end;
                previousBreak?.Dispose();
                previousBreak = row.GetTextLineBreak();
            }
        }
        finally { previousBreak?.Dispose(); }
    }

    private sealed class ParagraphPaddingElement(bool isLeading) : VisualLineElement(1, 0)
    {
        internal bool IsLeading => isLeading;
        private double _height;
        private double _baseline;
        internal void SetMetrics(double height, double baseline) => (_height, _baseline) = (height, baseline);
        public override bool HandlesLineBorders => true;
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) =>
            _height == 0 ? new TextHidden(VisualLength) : new SpacingRun(VisualLength, 0, _height, _baseline, TextRunProperties);
        public override int GetVisualColumn(int relativeTextOffset) => isLeading ? VisualColumn + VisualLength : VisualColumn;
        public override int GetNextCaretPosition(int visualColumn, System.Windows.Documents.LogicalDirection direction, CaretPositioningMode mode)
        {
            int caret = isLeading ? VisualColumn + VisualLength : VisualColumn;
            return direction == System.Windows.Documents.LogicalDirection.Forward && visualColumn < caret ||
                direction == System.Windows.Documents.LogicalDirection.Backward && visualColumn > caret ? caret : -1;
        }
        // Zero-width padding must not stop wrapped rows from inheriting quote/list indentation.
        public override bool IsWhitespace(int visualColumn) => true;
    }

    private sealed class SpacingRun(int length, double width, double height, double baseline, TextRunProperties properties) : TextEmbeddedObject
    {
        public override CharacterBufferReference CharacterBufferReference => new();
        public override int Length => length;
        public override TextRunProperties Properties => properties;
        public override LineBreakCondition BreakBefore => LineBreakCondition.BreakRestrained;
        public override LineBreakCondition BreakAfter => LineBreakCondition.BreakRestrained;
        public override bool HasFixedSize => true;
        public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth) => new(width, height, baseline);
        public override Rect ComputeBoundingBox(bool rightToLeft, bool sideways) => new(0, 0, width, height);
        public override void Draw(DrawingContext drawingContext, Point origin, bool rightToLeft, bool sideways) { }
    }

    private sealed class ParagraphTextSource(ITextRunConstructionContext context, IList<VisualLineElement> elements, double width) : TextSource
    {
        public override TextRun GetTextRun(int index)
        {
            var element = elements.FirstOrDefault(value => index >= value.VisualColumn && index < value.VisualColumn + value.VisualLength);
            if (element is null) return new TextEndOfParagraph(1);
            if (element is ParagraphPaddingElement) return new TextHidden(element.VisualLength);
            if (element is InlineObjectElement inline)
            {
                inline.Element.Measure(new Size(width, double.PositiveInfinity));
                Size size = inline.Element.DesiredSize;
                double baseline = TextBlock.GetBaselineOffset(inline.Element);
                return new SpacingRun(element.VisualLength, size.Width, size.Height,
                    double.IsNaN(baseline) ? size.Height : baseline, element.TextRunProperties);
            }
            return element.CreateTextRun(index, context);
        }
        public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(int limit) =>
            new(0, new CultureSpecificCharacterBufferRange(CultureInfo.CurrentCulture, new CharacterBufferRange(string.Empty, 0, 0)));
        public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int index) => index;
    }

    private sealed class ParagraphProperties(ITextRunConstructionContext context) : TextParagraphProperties
    {
        private bool _firstLine = true;
        private double _indent;

        internal void StartWrappedRows(TextLine firstRow, IList<VisualLineElement> elements, double width)
        {
            _firstLine = false;
            double indentation = context.TextView.Options.WordWrapIndentation;
            if (context.TextView.Options.InheritWordWrapIndentation)
            {
                int column = 0;
                foreach (var element in elements)
                {
                    while (column < element.VisualColumn + element.VisualLength && element.IsWhitespace(column))
                        column++;
                    if (column < element.VisualColumn + element.VisualLength) break;
                }
                if (column > 0 && column < firstRow.Length)
                    indentation += firstRow.GetDistanceFromCharacterHit(new CharacterHit(column, 0));
            }
            // Match AvalonEdit's wrapping width so the final row's padding uses
            // the metrics of the row that is actually rendered.
            if (indentation > 0 && indentation * 2 < width) _indent = indentation;
        }

        public override FlowDirection FlowDirection => context.TextView.FlowDirection;
        public override TextAlignment TextAlignment => TextAlignment.Left;
        public override double LineHeight => 0;
        public override bool FirstLineInParagraph => _firstLine;
        public override TextRunProperties DefaultTextRunProperties => context.GlobalTextRunProperties;
        public override TextWrapping TextWrapping => TextWrapping.Wrap;
        public override TextMarkerProperties? TextMarkerProperties => null;
        public override double Indent => _indent;
        public override double DefaultIncrementalTab => context.TextView.WideSpaceWidth * context.TextView.Options.IndentationSize;
    }
}
