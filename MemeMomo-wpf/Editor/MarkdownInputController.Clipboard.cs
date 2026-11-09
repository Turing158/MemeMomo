using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Editing;
using MemeMomo.Markdown;

namespace MemeMomo.Editor;

internal sealed partial class MarkdownInputController
{
    private bool CanEditListClipboard => !IsSourceMode && !_editor.IsReadOnly &&
        Volatile.Read(ref _disposed) == 0 && !IsTableCellInputFocused() &&
        _editor.TextArea.Selection is not RectangleSelection;

    private void OnListClipboardPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (!CanEditListClipboard || e.IsDragDrop || !ReferenceEquals(e.OriginalSource, _editor.TextArea)) return;
        string? text;
        bool wholeLine;
        try
        {
            text = e.DataObject.GetData(e.FormatToApply) as string;
            wholeLine = e.DataObject.GetDataPresent("MSDEVLineSelect");
        }
        catch (ExternalException)
        {
            e.CancelCommand();
            return;
        }
        if (string.IsNullOrEmpty(text)) return;
        MarkdownDocumentModel pasted = new(text);
        if (!pasted.Spans.Any(span => MarkdownClipboardText.IsListMarker(pasted, span))) return;

        MarkdownDocumentModel model = _model();
        int start = _editor.SelectionStart;
        int end = start + _editor.SelectionLength;
        (int sourceStart, int sourceEnd) = start == end
            ? (model.SourceOffsetForInsertion(start), model.SourceOffsetForInsertion(start))
            : model.GetSourceDeletionRange(start, end);
        // MSDEVLineSelect is AvalonEdit's whole-line clipboard flag. Keep its
        // insertion position while applying the Markdown source as one undo unit.
        if (start == end && wholeLine)
            sourceStart = sourceEnd = MarkdownListLine.Read(model.Markdown, sourceStart).Start;
        text = pasted.Markdown;
        int caret = sourceStart + text.Length;
        _applyEdit(new MarkdownEditResult(model.Markdown[..sourceStart] + text + model.Markdown[sourceEnd..], caret, caret), false);
        e.CancelCommand();
    }

    private async void OnListClipboardCut(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Cut || !CanEditListClipboard ||
            !ReferenceEquals(e.OriginalSource, _editor.TextArea)) return;
        MarkdownDocumentModel model = _model();
        int start = _editor.SelectionStart;
        int end = start + _editor.SelectionLength;
        if (start == end)
        {
            if (!_editor.Options.CutCopyWholeLine) return;
            var line = _editor.Document.GetLineByOffset(start);
            start = line.Offset;
            end = line.Offset + line.TotalLength;
        }
        if (!model.Spans.Any(span => MarkdownClipboardText.IsListMarker(model, span) &&
                span.Start < end && span.End >= start)) return;
        MarkdownClipboardText.RestoreSources(model, start, end, out bool containsListPrefix);
        if (!containsListPrefix) return;

        e.Handled = true;
        long projectionVersion = model.ProjectionVersion;
        long sessionVersion = _sessionVersion();
        int selectionStart = _editor.SelectionStart;
        int selectionLength = _editor.SelectionLength;
        if (!await _copyListClipboard() || !CanEditListClipboard ||
            model.ProjectionVersion != projectionVersion || _sessionVersion() != sessionVersion ||
            _editor.SelectionStart != selectionStart || _editor.SelectionLength != selectionLength) return;
        MarkdownDocumentModel changed = new(model.Markdown);
        changed.ApplyVisibleText(model.VisibleText.Remove(start, end - start), start, end - start, 0);
        int caret = changed.SourceOffsetForInsertion(start);
        _applyEdit(new MarkdownEditResult(changed.Markdown, caret, caret), false);
    }
}
