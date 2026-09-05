using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using MemeMomo.Markdown;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;
using FontStyle = System.Windows.FontStyle;
using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;

namespace MemeMomo.Editor;

internal class MarkdownVisualTransformer(Func<IReadOnlyList<MarkdownVisualSpan>> spans)
    : DocumentColorizingTransformer
{
    protected override void ColorizeLine(DocumentLine line)
    {
        foreach (MarkdownVisualSpan span in spans())
        {
            int start = Math.Max(span.Start, line.Offset);
            int end = Math.Min(span.End, line.EndOffset);
            if (end <= start)
            {
                continue;
            }

            ChangeLinePart(start, end, element => ApplyStyle(element, span.Kind));
        }
    }

    private static void ApplyStyle(VisualLineElement element, MarkdownVisualKind kind)
    {
        switch (kind)
        {
            case MarkdownVisualKind.Heading1:
                element.TextRunProperties.SetFontRenderingEmSize(22);
                ApplyTypeface(element, new FontFamily("Microsoft YaHei UI"), weight: FontWeights.SemiBold);
                break;
            case MarkdownVisualKind.Heading2:
                element.TextRunProperties.SetFontRenderingEmSize(18);
                ApplyTypeface(element, new FontFamily("Microsoft YaHei UI"), weight: FontWeights.SemiBold);
                break;
            case MarkdownVisualKind.Heading3:
                element.TextRunProperties.SetFontRenderingEmSize(16);
                ApplyTypeface(element, new FontFamily("Microsoft YaHei UI"), weight: FontWeights.SemiBold);
                break;
            case MarkdownVisualKind.Heading4:
                ApplyTypeface(element, new FontFamily("Microsoft YaHei UI"), weight: FontWeights.SemiBold);
                break;
            case MarkdownVisualKind.Bold:
                ApplyTypeface(element, new FontFamily("Microsoft YaHei UI"), weight: FontWeights.Bold);
                break;
            case MarkdownVisualKind.Italic:
                ApplyTypeface(element, new FontFamily("Microsoft YaHei UI"), style: FontStyles.Italic);
                break;
            case MarkdownVisualKind.Link:
                element.TextRunProperties.SetForegroundBrush(FindBrush("AccentPrimaryBrush", Brushes.DarkCyan));
                element.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
                break;
            case MarkdownVisualKind.Quote:
                element.TextRunProperties.SetForegroundBrush(FindBrush("TextSecondaryBrush", Brushes.DimGray));
                break;
            case MarkdownVisualKind.Strike:
                element.TextRunProperties.SetTextDecorations(TextDecorations.Strikethrough);
                break;
            case MarkdownVisualKind.Underline:
                element.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
                break;
            case MarkdownVisualKind.Mark:
                element.TextRunProperties.SetBackgroundBrush(FindBrush("AccentSubtleBrush", Brushes.PaleGoldenrod));
                break;
            case MarkdownVisualKind.Code:
                ApplyTypeface(element, new FontFamily("Cascadia Mono, Consolas"));
                break;
            case MarkdownVisualKind.CodeBlock:
                ApplyTypeface(element, new FontFamily("Cascadia Mono, Consolas"));
                break;
            case MarkdownVisualKind.Rule:
                // The placeholder glyphs are only caret/undo anchors; MarkdownRuleRenderer paints the line.
                element.TextRunProperties.SetForegroundBrush(Brushes.Transparent);
                break;
        }
    }

    private static Brush FindBrush(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;

    private static void ApplyTypeface(
        VisualLineElement element,
        FontFamily? family = null,
        FontStyle? style = null,
        FontWeight? weight = null)
    {
        Typeface current = element.TextRunProperties.Typeface;
        element.TextRunProperties.SetTypeface(new Typeface(
            family ?? current.FontFamily,
            style ?? current.Style,
            weight ?? current.Weight,
            current.Stretch));
    }
}

internal sealed class MarkdownColorizer(Func<IReadOnlyList<MarkdownVisualSpan>> spans)
    : MarkdownVisualTransformer(spans);
