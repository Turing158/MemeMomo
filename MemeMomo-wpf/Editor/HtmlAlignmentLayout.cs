using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using MemeMomo.Markdown;
using Size = System.Windows.Size;
using Rect = System.Windows.Rect;
using FlowDirection = System.Windows.FlowDirection;

namespace MemeMomo.Editor;

/// <summary>Alignment is layout space, so text, selection, inline objects and caret share coordinates.</summary>
internal sealed class HtmlAlignmentLayout(Func<IReadOnlyList<MarkdownVisualSpan>> spans)
    : VisualLineElementGenerator, IVisualLineTransformer
{
    private int[] _boundaries = [];

    public override void StartGeneration(ITextRunConstructionContext context)
    {
        base.StartGeneration(context);
        var line = context.VisualLine.FirstDocumentLine;
        _boundaries = Alignment(line.Offset, line.EndOffset) is "center" or "right" or "justify"
            ? StringInfo.ParseCombiningCharacters(context.Document.GetText(line)).Select(offset => line.Offset + offset).ToArray()
            : [];
    }

    internal void OnViewportChanged(object sender, SizeChangedEventArgs args)
    {
        if (args.WidthChanged && spans().Any(span => span.Presentation?.Alignment is "center" or "right" or "justify"))
            ((TextView)sender).Redraw();
    }

    private string? Alignment(int start, int end) => spans()
        .Where(span => span.Presentation?.Alignment is not null && span.Start <= end && span.End > start)
        .OrderBy(span => span.Length).ThenByDescending(span => span.SourceStart)
        .Select(span => span.Presentation!.Alignment).FirstOrDefault();

    public override int GetFirstInterestedOffset(int startOffset)
    {
        int index = Array.BinarySearch(_boundaries, startOffset);
        if (index < 0) index = ~index;
        return index < _boundaries.Length ? _boundaries[index] : -1;
    }

    public override VisualLineElement? ConstructElement(int offset) => new AlignmentSpace();

    internal static Rect ClipSelectionRect(TextView view, Rect rect)
    {
        foreach (var visualLine in view.VisualLines)
        {
            int column = 0;
            foreach (var line in visualLine.TextLines)
            {
                if (Math.Abs(visualLine.GetTextLineVisualYPosition(line, VisualYPosition.LineTop) - view.VerticalOffset - rect.Top) < 0.01)
                {
                    var leading = visualLine.Elements.OfType<AlignmentSpace>()
                        .FirstOrDefault(space => space.VisualColumn <= column && space.VisualColumn + space.VisualLength > column);
                    if (leading is { Width: > 0 })
                    {
                        double left = Math.Min(rect.Right, Math.Max(rect.Left, leading.Width - view.HorizontalOffset));
                        return new Rect(left, rect.Top, rect.Right - left, rect.Height);
                    }
                    return rect;
                }
                column += line.Length;
            }
        }
        return rect;
    }

    public void Transform(ITextRunConstructionContext context, IList<VisualLineElement> elements)
    {
        var spaces = elements.OfType<AlignmentSpace>().ToArray();
        double width = context.TextView.ActualWidth;
        if (spaces.Length == 0 || width <= 1 || !double.IsFinite(width)) return;
        var visualLine = context.VisualLine;
        string? alignment = Alignment(visualLine.FirstDocumentLine.Offset, visualLine.LastDocumentLine.EndOffset);
        using TextFormatter formatter = TextFormatter.Create(TextOptions.GetTextFormattingMode(context.TextView));
        var source = new AlignmentTextSource(context, elements, width);
        var paragraph = new AlignmentParagraph(context.GlobalTextRunProperties);
        int offset = 0;
        TextLineBreak? previousBreak = null;
        try
        {
            while (offset < visualLine.VisualLength)
            {
                using TextLine line = formatter.FormatLine(source, offset, width, paragraph, previousBreak);
                int end = offset + line.Length;
                AlignmentSpace? leading = spaces.LastOrDefault(space => space.VisualColumn <= offset);
                if (leading is not null)
                {
                    leading.BreakLine = offset > 0;
                    double unused = Math.Max(0, width - line.WidthIncludingTrailingWhitespace - 0.5);
                    leading.Width = alignment == "center" ? unused / 2 : alignment == "right" ? unused : 0;
                    if (alignment == "justify" && end < visualLine.VisualLength)
                    {
                        var gaps = spaces.Where(space => space.VisualColumn > offset && space.VisualColumn < end &&
                            space.RelativeTextOffset > 0 && char.IsWhiteSpace(context.Document.GetCharAt(
                                visualLine.FirstDocumentLine.Offset + space.RelativeTextOffset - 1))).ToArray();
                        foreach (var gap in gaps) gap.Width = unused / Math.Max(1, gaps.Length);
                    }
                }
                if (end <= offset) break;
                offset = end;
                previousBreak?.Dispose();
                previousBreak = line.GetTextLineBreak();
            }
        }
        finally { previousBreak?.Dispose(); }
    }

    private sealed class AlignmentSpace() : VisualLineElement(2, 0)
    {
        internal double Width { get; set; }
        internal bool BreakLine { get; set; }
        public override bool HandlesLineBorders => true;
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
        {
            int relative = startVisualColumn - VisualColumn;
            if (BreakLine && relative == 0) return new TextEndOfLine(1);
            return Width <= 0 ? new TextHidden(VisualLength - relative) :
                new LayoutObject(VisualLength - relative, Width, 1, 1, TextRunProperties);
        }
        public override int GetVisualColumn(int relativeTextOffset) => VisualColumn + VisualLength;
        public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode)
        {
            int caret = VisualColumn + VisualLength;
            return direction == LogicalDirection.Forward && visualColumn < caret ||
                direction == LogicalDirection.Backward && visualColumn > caret ? caret : -1;
        }
        // Alignment space is not source indentation; wrapped rows compute their own padding.
        public override bool IsWhitespace(int visualColumn) => false;
    }

    private sealed class LayoutObject(int length, double width, double height, double baseline, TextRunProperties properties) : TextEmbeddedObject
    {
        public override CharacterBufferReference CharacterBufferReference => new();
        public override int Length => length;
        public override TextRunProperties Properties => properties;
        public override LineBreakCondition BreakBefore => LineBreakCondition.BreakRestrained;
        public override LineBreakCondition BreakAfter => LineBreakCondition.BreakRestrained;
        public override bool HasFixedSize => true;
        public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth) => new(width, height, baseline);
        public override Rect ComputeBoundingBox(bool rightToLeft, bool sideways) => new(0, 0, width, height);
        public override void Draw(DrawingContext drawingContext, System.Windows.Point origin, bool rightToLeft, bool sideways) { }
    }

    private sealed class AlignmentTextSource(ITextRunConstructionContext context, IList<VisualLineElement> elements, double width) : TextSource
    {
        public override TextRun GetTextRun(int textSourceCharacterIndex)
        {
            var element = elements.FirstOrDefault(value => textSourceCharacterIndex >= value.VisualColumn &&
                textSourceCharacterIndex < value.VisualColumn + value.VisualLength);
            if (element is null) return new TextEndOfParagraph(1);
            // Measure against the original unaligned text, even while earlier rows acquire padding.
            if (element is AlignmentSpace) return new TextHidden(element.VisualColumn + element.VisualLength - textSourceCharacterIndex);
            if (element is InlineObjectElement inline)
            {
                inline.Element.Measure(new Size(width, double.PositiveInfinity));
                Size size = inline.Element.DesiredSize;
                return new LayoutObject(element.VisualLength, size.Width, size.Height, size.Height, element.TextRunProperties);
            }
            return element.CreateTextRun(textSourceCharacterIndex, context);
        }
        public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(int textSourceCharacterIndexLimit) =>
            new(0, new CultureSpecificCharacterBufferRange(CultureInfo.CurrentCulture, new CharacterBufferRange(string.Empty, 0, 0)));
        public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int textSourceCharacterIndex) => textSourceCharacterIndex;
    }

    private sealed class AlignmentParagraph(TextRunProperties properties) : TextParagraphProperties
    {
        public override FlowDirection FlowDirection => FlowDirection.LeftToRight;
        public override TextAlignment TextAlignment => TextAlignment.Left;
        public override double LineHeight => 0;
        public override bool FirstLineInParagraph => true;
        public override TextRunProperties DefaultTextRunProperties => properties;
        public override TextWrapping TextWrapping => TextWrapping.Wrap;
        public override TextMarkerProperties? TextMarkerProperties => null;
        public override double Indent => 0;
    }
}
