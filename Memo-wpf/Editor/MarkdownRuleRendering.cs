using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using Memo.Markdown;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;

namespace Memo.Editor;

internal static class MarkdownRuleStyle
{
    internal const double HorizontalMargin = 0;
    internal const double Thickness = 2;
    internal const double CornerRadius = 1;
    internal const double BorderPaddingX = 0;
    internal const double BorderPaddingY = 4;
    internal const double BorderCornerRadius = 4;
}

/// <summary>
/// Paints projected thematic breaks as a full-width separator line. Space placeholders stay
/// in the document for caret and undo mapping but render nothing; this renderer draws the
/// visible rule on top of them and the 1px border while the rule is bordered-selected.
/// </summary>
internal sealed class MarkdownRuleRenderer(
    MarkdownDocumentModel model,
    MarkdownRuleInteraction interaction) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (textView.Document is null || !textView.VisualLinesValid)
        {
            return;
        }

        Brush brush = FindBrush("BorderEmphasisBrush", Brushes.Gray);
        MarkdownVisualSpan? selected = interaction.SelectedRule;

        foreach (MarkdownVisualSpan span in model.Spans.Where(value => value.Kind == MarkdownVisualKind.Rule))
        {
            if (!TryGetLineBounds(textView, span, out Rect lineBounds))
            {
                continue;
            }

            double middle = lineBounds.Top + lineBounds.Height / 2;
            Rect ruleRect = new(
                MarkdownRuleStyle.HorizontalMargin,
                middle - MarkdownRuleStyle.Thickness / 2,
                Math.Max(0, textView.ActualWidth - MarkdownRuleStyle.HorizontalMargin * 2),
                MarkdownRuleStyle.Thickness);
            if (ruleRect.Width > 0)
            {
                drawingContext.DrawRoundedRectangle(
                    brush,
                    null,
                    ruleRect,
                    MarkdownRuleStyle.CornerRadius,
                    MarkdownRuleStyle.CornerRadius);
            }

            if (selected is { } selection && selection.Start == span.Start && selection.End == span.End)
            {
                drawingContext.DrawRoundedRectangle(
                    null,
                    CreateBorderPen(),
                    new Rect(
                        ruleRect.Left - MarkdownRuleStyle.BorderPaddingX,
                        ruleRect.Top - MarkdownRuleStyle.BorderPaddingY,
                        ruleRect.Width + MarkdownRuleStyle.BorderPaddingX * 2,
                        ruleRect.Height + MarkdownRuleStyle.BorderPaddingY * 2),
                    MarkdownRuleStyle.BorderCornerRadius,
                    MarkdownRuleStyle.BorderCornerRadius);
            }
        }
    }

    private static Pen CreateBorderPen() => new(FindBrush("AccentPrimaryBrush", Brushes.DarkCyan), 1);

    /// <summary>The full-width visual band of the rule's line, in TextView coordinates.</summary>
    internal static bool TryGetLineBounds(TextView textView, MarkdownVisualSpan span, out Rect bounds)
    {
        foreach (VisualLine visualLine in textView.VisualLines)
        {
            if (visualLine.FirstDocumentLine.Offset > span.Start ||
                visualLine.LastDocumentLine.EndOffset < span.End)
            {
                continue;
            }

            if (visualLine.TextLines.Count == 0)
            {
                break;
            }

            double top = visualLine.GetTextLineVisualYPosition(
                visualLine.TextLines[0],
                VisualYPosition.LineTop) - textView.VerticalOffset;
            double bottom = visualLine.GetTextLineVisualYPosition(
                visualLine.TextLines[0],
                VisualYPosition.LineBottom) - textView.VerticalOffset;
            bounds = new Rect(0, top, Math.Max(0, textView.ActualWidth), Math.Max(0, bottom - top));
            return true;
        }

        bounds = default;
        return false;
    }

    private static Brush FindBrush(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;
}
