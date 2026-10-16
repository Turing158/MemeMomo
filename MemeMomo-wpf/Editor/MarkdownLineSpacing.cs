using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using MemeMomo.Markdown;
using FontFamily = System.Windows.Media.FontFamily;

namespace MemeMomo.Editor;

/// <summary>Adds breathing room to rendered rows, including soft wraps, without changing glyph sizes.</summary>
internal sealed class MarkdownLineSpacing(Func<IReadOnlyList<MarkdownVisualSpan>> spans)
    : VisualLineElementGenerator, IVisualLineTransformer
{
    internal const double VerticalPadding = 2;
    private readonly Dictionary<(Typeface Typeface, double Size, double Padding, TextFormattingMode Mode, double Dpi), FontFamily> _families = new();

    public override int GetFirstInterestedOffset(int startOffset)
    {
        var line = CurrentContext.VisualLine.FirstDocumentLine;
        return line.Length == 0 && startOffset == line.Offset && !IsTable(CurrentContext)
            ? line.Offset : -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        TextView view = CurrentContext.TextView;
        Border spacer = new()
        {
            Width = 0,
            Height = view.DefaultLineHeight,
            IsHitTestVisible = false,
            Focusable = false
        };
        TextBlock.SetBaselineOffset(spacer, view.DefaultBaseline);
        return new EmptyLinePaddingElement(spacer);
    }

    public void Transform(ITextRunConstructionContext context, IList<VisualLineElement> elements)
    {
        if (IsTable(context)) return;
        double padding = GetVerticalPadding(context);

        foreach (VisualLineElement element in elements)
        {
            var properties = element.TextRunProperties;
            Typeface typeface = properties.Typeface;
            var key = (Typeface: typeface, Size: properties.FontRenderingEmSize, Padding: padding,
                Mode: TextOptions.GetTextFormattingMode(context.TextView),
                Dpi: VisualTreeHelper.GetDpi(context.TextView).PixelsPerDip);
            if (!_families.TryGetValue(key, out FontFamily? family))
            {
                // Composite font metrics reserve space above/below each formatted row.
                // The family map retains the original fonts, fallback, widths and glyphs.
                var metrics = new FormattedText("x", CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
                    typeface, key.Size, System.Windows.Media.Brushes.Black, null, key.Mode, key.Dpi);
                family = new FontFamily
                {
                    Baseline = (metrics.Baseline + padding) / key.Size,
                    LineSpacing = (metrics.Height + padding * 2) / key.Size
                };
                family.FamilyMaps.Add(new FontFamilyMap { Target = typeface.FontFamily.Source });
                _families.Add(key, family);
            }
            properties.SetTypeface(new Typeface(family, typeface.Style, typeface.Weight, typeface.Stretch));

            // Inline controls and block spacers participate in the same line metrics.
            // Their margin also moves the actual control with the text baseline.
            if (element is InlineObjectElement { Element: FrameworkElement inline })
            {
                Thickness margin = inline.Margin;
                inline.Margin = new Thickness(margin.Left, margin.Top + padding,
                    margin.Right, margin.Bottom + padding);
                double baseline = TextBlock.GetBaselineOffset(inline);
                if (!double.IsNaN(baseline))
                    TextBlock.SetBaselineOffset(inline, baseline + padding);
            }
        }
    }

    private double GetVerticalPadding(ITextRunConstructionContext context)
    {
        VisualLine line = context.VisualLine;
        double padding = VerticalPadding;
        foreach (MarkdownVisualSpan span in spans())
        {
            if (span.Start > line.LastDocumentLine.EndOffset || span.End <= line.FirstDocumentLine.Offset) continue;
            double headingPadding = span.Kind switch
            {
                MarkdownVisualKind.Heading1 => 8,
                MarkdownVisualKind.Heading2 => 6,
                MarkdownVisualKind.Heading3 => 4,
                MarkdownVisualKind.Heading4 => 3,
                _ => VerticalPadding
            };
            padding = Math.Max(padding, headingPadding);
        }
        return padding;
    }

    private bool IsTable(ITextRunConstructionContext context)
    {
        var line = context.VisualLine;
        return spans().Any(span => span.Kind == MarkdownVisualKind.Table &&
            span.Start <= line.LastDocumentLine.EndOffset && span.End > line.FirstDocumentLine.Offset);
    }

    private sealed class EmptyLinePaddingElement(UIElement spacer) : InlineObjectElement(0, spacer)
    {
        public override bool HandlesLineBorders => true;
        public override int GetVisualColumn(int relativeTextOffset) => VisualColumn + VisualLength;
        public override int GetNextCaretPosition(int visualColumn,
            System.Windows.Documents.LogicalDirection direction, CaretPositioningMode mode)
        {
            int caret = VisualColumn + VisualLength;
            return direction == System.Windows.Documents.LogicalDirection.Forward && visualColumn < caret ||
                direction == System.Windows.Documents.LogicalDirection.Backward && visualColumn > caret ? caret : -1;
        }
        public override bool IsWhitespace(int visualColumn) => true;
    }
}
