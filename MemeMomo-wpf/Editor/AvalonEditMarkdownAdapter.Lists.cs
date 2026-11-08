using ICSharpCode.AvalonEdit.Editing;

namespace MemeMomo.Editor;

internal sealed partial class AvalonEditMarkdownAdapter
{
    private void OnListSelectionChanged(object? sender, EventArgs e)
    {
        if (IsSourceMode || _updatingProjection || _normalizingListSelection ||
            Editor.TextArea.Selection is RectangleSelection ||
            Volatile.Read(ref _disposed) != 0 || Editor.Document.TextLength != _model.VisibleText.Length)
            return;
        if (Editor.SelectionLength == 0)
        {
            RedirectCaretOutOfListPrefix();
            return;
        }
        int start = Editor.SelectionStart;
        int end = start + Editor.SelectionLength;
        (int normalizedStart, int normalizedEnd) = _listIndentation.NormalizeSelection(start, end);
        if (start == normalizedStart && end == normalizedEnd) return;
        bool reversed = Editor.Document.GetOffset(Editor.TextArea.Selection.StartPosition.Location) >
            Editor.Document.GetOffset(Editor.TextArea.Selection.EndPosition.Location);
        _normalizingListSelection = true;
        try
        {
            Editor.TextArea.Selection = Selection.Create(Editor.TextArea,
                reversed ? normalizedEnd : normalizedStart, reversed ? normalizedStart : normalizedEnd);
            Editor.CaretOffset = reversed ? normalizedStart : normalizedEnd;
        }
        finally { _normalizingListSelection = false; }
    }

    private void RedirectCaretOutOfListPrefix()
    {
        if (IsSourceMode || Editor.SelectionLength != 0 || IsAnyTableCellFocused() ||
            Editor.Document.TextLength != _model.VisibleText.Length)
            return;
        int target = _listIndentation.NormalizeCaret(Editor.CaretOffset);
        if (target == Editor.CaretOffset) return;
        _redirectingAtomicCaret = true;
        try { Editor.CaretOffset = target; }
        finally { _redirectingAtomicCaret = false; }
    }
}
