using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using MemeMomo.Markdown;
using MemeMomo.UI;

namespace MemeMomo.Editor;

/// <summary>Displays list prefixes outside the editable text while retaining source/undo anchors.</summary>
internal sealed class MarkdownListIndentation(
    MarkdownDocumentModel model,
    Func<MarkdownVisualSpan, UIElement> taskFactory) : VisualLineElementGenerator
{
    private const double OrderedTrailingGap = 6;
    private const double BulletMinimumGap = 4;
    private readonly Dictionary<(int QuoteDepth, int Indent), double> _markerWidths = [];
    private long _widthVersion = -1;
    private (Typeface Typeface, double Size, TextFormattingMode Mode, double Dpi)? _widthMetrics;

    public override int GetFirstInterestedOffset(int startOffset) =>
        GetPrefix() is { } prefix && prefix.Start >= startOffset ? prefix.Start : -1;

    public override VisualLineElement? ConstructElement(int offset)
    {
        if (GetPrefix() is not { } prefix || prefix.Start != offset) return null;

        TextView view = CurrentContext.TextView;
        var ownerLine = MarkdownListLine.Read(model.Markdown, prefix.Owner.SourceStart);
        int indentColumn = IndentColumn(ownerLine.Text, view.Options.IndentationSize);
        double indentWidth = MeasureText(new string(' ', indentColumn), view);
        double markerWidth = MarkerWidth(ownerLine.QuoteDepth, indentColumn, view);
        double prefixWidth = indentWidth + markerWidth;
        UIElement control;
        if (prefix.Marker is { Kind: MarkdownVisualKind.Task } task)
        {
            UIElement checkBox = taskFactory(task);
            Grid grid = new() { Width = prefixWidth, Focusable = false };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(indentWidth) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(markerWidth) });
            if (indentColumn > 0)
            {
                grid.Children.Add(CreateText(new string(' ', indentColumn), view, prefix.Start));
            }
            if (checkBox is FrameworkElement element) element.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            Grid.SetColumn(checkBox, 1);
            grid.Children.Add(checkBox);
            TextBlock.SetBaselineOffset(grid, TextBlock.GetBaselineOffset(checkBox));
            control = grid;
        }
        else
        {
            string text = prefix.Marker is { } marker
                ? model.VisibleText[marker.Start..marker.End].TrimEnd(' ', '\t')
                : model.VisibleText[prefix.Start..prefix.End];
            TextBlock block = CreateText(text, view, prefix.Start);
            block.Width = prefixWidth;
            if (prefix.Marker is { } textMarker)
            {
                double left = textMarker.Kind == MarkdownVisualKind.OrderedListMarker
                    ? 0 : Math.Max(BulletMinimumGap, (markerWidth - MeasureText(text, view)) / 2);
                block.Padding = new Thickness(indentWidth + left, 0,
                    textMarker.Kind == MarkdownVisualKind.OrderedListMarker ? OrderedTrailingGap : left, 0);
            }
            control = block;
        }
        return new MarkdownListPrefixElement(prefix.End - prefix.Start, control);
    }

    private Prefix? GetPrefix()
    {
        DocumentLine line = CurrentContext.VisualLine.FirstDocumentLine;
        return GetPrefix(line.Offset, line.EndOffset);
    }

    internal (int Start, int End) NormalizeSelection(int start, int end)
    {
        Prefix? first = PrefixAt(start);
        Prefix? last = PrefixAt(end);
        if (first is { } leading && start < leading.End) start = leading.End;
        if (last is { } trailing && end > trailing.Start && end <= trailing.End) end = trailing.Start;
        return (start, Math.Max(start, end));
    }

    internal int NormalizeCaret(int offset) => PrefixAt(offset) is { } prefix && offset < prefix.End
        ? prefix.End : offset;

    private Prefix? PrefixAt(int offset)
    {
        string text = model.VisibleText;
        offset = Math.Clamp(offset, 0, text.Length);
        int start = offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
        int end = text.IndexOf('\n', start);
        return GetPrefix(start, end < 0 ? text.Length : end);
    }

    private Prefix? GetPrefix(int lineStart, int lineEnd)
    {
        var sourceLine = MarkdownListLine.Read(model.Markdown,
            model.SourceOffsetFromVisible(lineStart, trailingAffinity: false));
        int start = model.VisibleOffsetFromSource(sourceLine.ContentStart);
        MarkdownVisualSpan? marker = model.Spans
            .Where(span => span.Start >= start && span.End <= lineEnd && IsMarker(span))
            .Select(span => (MarkdownVisualSpan?)span).FirstOrDefault();
        if (marker is { } span) return new(start, span.End, span, span);

        // Only the owning list's fixed content column is structural. Extra spaces
        // and ordinary/code indentation remain editable text.
        if (model.Spans.Any(span => span.Kind is MarkdownVisualKind.CodeBlock or MarkdownVisualKind.Table &&
                span.Start <= lineStart && span.End > lineStart) ||
            sourceLine.ContinuationOwner(model.Markdown) is not { } owner)
            return null;
        int column = owner.ContentColumn;
        int leading = sourceLine.Text.Length - sourceLine.Text.TrimStart(' ', '\t').Length;
        if (leading < column) return null;
        MarkdownVisualSpan? ownerMarker = model.Spans
            .Where(span => span.SourceStart >= owner.Line.ContentStart && span.SourceStart < owner.Line.End && IsMarker(span))
            .Select(span => (MarkdownVisualSpan?)span).FirstOrDefault();
        if (ownerMarker is not { } listMarker) return null;
        int end = model.VisibleOffsetFromSource(sourceLine.ContentStart + column);
        return end > start ? new(start, end, null, listMarker) : null;
    }

    private bool IsMarker(MarkdownVisualSpan span) =>
        span.Kind is MarkdownVisualKind.OrderedListMarker or MarkdownVisualKind.Task ||
        span.Kind == MarkdownVisualKind.Normal && span.Length == 2 &&
        model.VisibleText.AsSpan(span.Start, span.Length).SequenceEqual("• ") &&
        model.Markdown[span.SourceStart] is '-' or '+' or '*';

    private TextBlock CreateText(string text, TextView view, int offset)
    {
        var properties = CurrentContext.GlobalTextRunProperties;
        int lineStart = CurrentContext.VisualLine.FirstDocumentLine.Offset;
        double topSpacing = model.Spans.Any(span => span.Kind == MarkdownVisualKind.Quote &&
            span.Start >= lineStart && span.Start <= offset && span.End > offset)
            ? MarkdownQuoteSpacing.VerticalMargin + MarkdownQuoteSpacing.VerticalPadding : 0;
        TextBlock block = new()
        {
            Text = text,
            FontFamily = properties.Typeface.FontFamily,
            FontSize = properties.FontRenderingEmSize,
            FontStyle = properties.Typeface.Style,
            FontWeight = properties.Typeface.Weight,
            Foreground = properties.ForegroundBrush,
            Margin = new Thickness(0, topSpacing, 0, 0),
            IsHitTestVisible = false,
            Focusable = false
        };
        TextBlock.SetBaselineOffset(block, view.DefaultBaseline + topSpacing);
        return block;
    }

    private double MarkerWidth(int quoteDepth, int indent, TextView view)
    {
        var properties = CurrentContext.GlobalTextRunProperties;
        var metrics = (properties.Typeface, properties.FontRenderingEmSize,
            TextOptions.GetTextFormattingMode(view), VisualTreeHelper.GetDpi(view).PixelsPerDip);
        if (_widthVersion != model.ProjectionVersion || _widthMetrics != metrics)
        {
            _markerWidths.Clear();
            foreach (MarkdownVisualSpan marker in model.Spans.Where(IsMarker))
            {
                var line = MarkdownListLine.Read(model.Markdown, marker.SourceStart);
                var level = (line.QuoteDepth, IndentColumn(line.Text, view.Options.IndentationSize));
                double width = TaskCheckBoxFactory.InlineWidth;
                if (marker.Kind == MarkdownVisualKind.OrderedListMarker)
                    width = Math.Max(width, MeasureText(model.VisibleText[marker.Start..marker.End].TrimEnd(' ', '\t'), view) + OrderedTrailingGap);
                _markerWidths[level] = Math.Max(_markerWidths.GetValueOrDefault(level), Math.Ceiling(width));
            }
            _widthVersion = model.ProjectionVersion;
            _widthMetrics = metrics;
        }
        return _markerWidths.GetValueOrDefault((quoteDepth, indent), TaskCheckBoxFactory.InlineWidth);
    }

    private double MeasureText(string text, TextView view)
    {
        if (text.Length == 0) return 0;
        var properties = CurrentContext.GlobalTextRunProperties;
        return new FormattedText(text, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
            properties.Typeface, properties.FontRenderingEmSize, properties.ForegroundBrush,
            null, TextOptions.GetTextFormattingMode(view), VisualTreeHelper.GetDpi(view).PixelsPerDip)
            .WidthIncludingTrailingWhitespace;
    }

    private static int IndentColumn(string text, int tabSize)
    {
        int column = 0;
        foreach (char character in text)
        {
            if (character == ' ') column++;
            else if (character == '\t') column += tabSize - column % tabSize;
            else break;
        }
        return column;
    }

    private readonly record struct Prefix(int Start, int End, MarkdownVisualSpan? Marker, MarkdownVisualSpan Owner);
}

internal sealed class MarkdownListPrefixElement(int length, UIElement control) : InlineObjectElement(length, control)
{
    internal static Rect ClipSelectionRect(TextView view, Rect rect)
    {
        foreach (VisualLine line in view.VisualLines)
        {
            MarkdownListPrefixElement? prefix = line.Elements.OfType<MarkdownListPrefixElement>().FirstOrDefault();
            if (prefix is null || line.TextLines.Count == 0) continue;
            var firstRow = line.TextLines[0];
            double top = line.GetTextLineVisualYPosition(firstRow, VisualYPosition.LineTop) - view.VerticalOffset;
            if (Math.Abs(top - rect.Top) >= 0.01) continue;
            double left = line.GetVisualPosition(prefix.VisualColumn + prefix.VisualLength, VisualYPosition.TextTop).X -
                view.HorizontalOffset;
            left = Math.Max(rect.Left, left);
            return new Rect(left, rect.Top, Math.Max(0, rect.Right - left), rect.Height);
        }
        return rect;
    }

    public override bool HandlesLineBorders => true;
    public override bool IsWhitespace(int visualColumn) => true;
    public override int GetVisualColumn(int relativeTextOffset) => VisualColumn + VisualLength;
    public override int GetRelativeOffset(int visualColumn) => RelativeTextOffset + DocumentLength;

    public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode)
    {
        int end = VisualColumn + VisualLength;
        return direction == LogicalDirection.Forward && visualColumn < end ||
            direction == LogicalDirection.Backward && visualColumn > end ? end : -1;
    }

    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
    {
        if (Element is TextBlock text)
        {
            text.Foreground = TextRunProperties.ForegroundBrush;
            text.FontSize = TextRunProperties.FontRenderingEmSize;
            text.FontStyle = TextRunProperties.Typeface.Style;
            text.FontWeight = TextRunProperties.Typeface.Weight;
        }
        return new PrefixRun(VisualLength, TextRunProperties, Element);
    }

    private sealed class PrefixRun(int length, TextRunProperties properties, UIElement element)
        : InlineObjectRun(length, properties, element)
    {
        public override LineBreakCondition BreakBefore => LineBreakCondition.BreakRestrained;
        public override LineBreakCondition BreakAfter => LineBreakCondition.BreakRestrained;

        public override void Draw(DrawingContext drawingContext, System.Windows.Point origin, bool rightToLeft, bool sideways)
        {
            if (Element is not TextBlock text || VisualLine is not { TextLines.Count: > 0 } line) return;
            var metrics = new FormattedText(text.Text, CultureInfo.CurrentCulture, text.FlowDirection,
                new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize, text.Foreground,
                null, TextOptions.GetTextFormattingMode(text), VisualTreeHelper.GetDpi(text).PixelsPerDip);
            // AvalonEdit arranges inline controls at the row top, regardless of their
            // baseline. Align the glyphs after the final row metrics include paragraph
            // spacing, quote padding and any taller text on the same row.
            text.RenderTransform = new TranslateTransform(0, line.TextLines[0].Baseline - text.Margin.Top - metrics.Baseline);
        }
    }
}
