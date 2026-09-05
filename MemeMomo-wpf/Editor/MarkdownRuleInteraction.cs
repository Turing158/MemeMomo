using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using MemeMomo.Markdown;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;

namespace MemeMomo.Editor;

internal enum RuleExitDirection
{
    Up,
    Down
}

/// <summary>
/// Tracks the bordered "selection" state of an atomic horizontal rule. The caret never rests
/// on a rule line: keyboard navigation and mouse clicks that would reach a rule highlight it
/// with a 1px border instead of moving the caret, and a second navigation step jumps past it.
/// </summary>
internal sealed class MarkdownRuleInteraction(TextEditor editor, Func<MarkdownDocumentModel> model)
{
    private readonly Brush _normalCaretBrush = editor.TextArea.Caret.CaretBrush;
    private MarkdownVisualSpan? _selected;

    internal bool IsBordered => _selected is not null;

    /// <summary>The bordered rule resolved against the current projection, or null when stale.</summary>
    internal MarkdownVisualSpan? SelectedRule => _selected is not { } selected
        ? null
        : model().Spans
            .Where(value => value.Kind == MarkdownVisualKind.Rule && value.Start == selected.Start)
            .Select(value => (MarkdownVisualSpan?)value)
            .FirstOrDefault();

    internal bool TryEnterBorder(MarkdownVisualSpan span)
    {
        if (SelectedRule is { } current && current.Start == span.Start && current.End == span.End)
        {
            return true;
        }

        _selected = span;
        editor.TextArea.ClearSelection();
        HideCaret();
        editor.TextArea.TextView.Redraw();
        return true;
    }

    internal void Clear()
    {
        if (_selected is null)
        {
            return;
        }

        _selected = null;
        ShowCaret();
        editor.TextArea.TextView.Redraw();
    }

    /// <summary>
    /// Leaves the bordered state by moving the caret to the content on the given side of the
    /// rule. When that side has no content line (document edge or another rule) the rule stays
    /// bordered and the caret remains hidden.
    /// </summary>
    internal bool TryExit(RuleExitDirection direction)
    {
        if (SelectedRule is not { } rule)
        {
            Clear();
            return false;
        }

        DocumentLine ruleLine = editor.Document.GetLineByOffset(rule.Start);
        DocumentLine? target = direction == RuleExitDirection.Up ? ruleLine.PreviousLine : ruleLine.NextLine;
        if (target is null || IsRuleLine(target))
        {
            return false;
        }

        Clear();
        editor.CaretOffset = direction == RuleExitDirection.Up ? target.EndOffset : target.Offset;
        editor.TextArea.Caret.BringCaretToView();
        return true;
    }

    internal MarkdownVisualSpan? HitRule(Point pointRelativeToTextView)
    {
        TextView textView = editor.TextArea.TextView;
        if (textView.Document is null || !textView.VisualLinesValid)
        {
            return null;
        }

        foreach (MarkdownVisualSpan span in model().Spans.Where(value => value.Kind == MarkdownVisualKind.Rule))
        {
            if (MarkdownRuleRenderer.TryGetLineBounds(textView, span, out Rect bounds) &&
                bounds.Contains(pointRelativeToTextView))
            {
                return span;
            }
        }

        return null;
    }

    internal bool IsRuleLine(DocumentLine line) => model().Spans.Any(span =>
        span.Kind == MarkdownVisualKind.Rule &&
        span.Start <= line.Offset && span.End >= line.EndOffset);

    private void HideCaret()
    {
        editor.TextArea.Caret.CaretBrush = Brushes.Transparent;
        editor.TextArea.Caret.Hide();
    }

    private void ShowCaret()
    {
        editor.TextArea.Caret.CaretBrush = _normalCaretBrush;
        editor.TextArea.Caret.Show();
    }
}
