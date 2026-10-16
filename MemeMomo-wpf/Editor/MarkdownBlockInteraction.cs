using System.Windows;
using System.Windows.Controls;
using ICSharpCode.AvalonEdit.Rendering;
using MemeMomo.Components.Dialogs;
using MemeMomo.Markdown;
using MemeMomo.UI.Popup;
using Point = System.Windows.Point;

namespace MemeMomo.Editor;

internal sealed class MarkdownBlockInteraction(AvalonEditMarkdownAdapter adapter, Action pasteImages) : IDisposable
{
    private bool _disposed;

    internal ContextMenu CreateMenu(Point? pointToEditor)
    {
        if (adapter.IsReadOnly) return EditorClipboardMenu.CreateReadOnly(adapter.Editor.TextArea);
        if (adapter.IsSourceMode) return EditorClipboardMenu.CreateForTextArea(adapter.Editor.TextArea, pasteImages);
        long session = adapter.SessionVersion;
        TextView view = adapter.Editor.TextArea.TextView;
        MarkdownVisualSpan? target = null;
        if (pointToEditor is { } point)
        {
            Point viewPoint = adapter.Editor.TranslatePoint(point, view);
            if (new Rect(view.RenderSize).Contains(viewPoint))
                target = adapter.CodeBlockRenderer.HitTest(view, viewPoint) ??
                    view.BackgroundRenderers.OfType<MarkdownQuoteRenderer>().Single().HitTest(view, viewPoint);
        }
        else
        {
            int caret = adapter.Editor.CaretOffset;
            target = adapter.Model.Spans.Where(span => span.Kind is MarkdownVisualKind.Quote or MarkdownVisualKind.CodeBlock &&
                    span.Start <= caret && caret <= span.End)
                .OrderByDescending(span => span.Kind == MarkdownVisualKind.CodeBlock)
                .ThenBy(span => span.Length).Select(span => (MarkdownVisualSpan?)span).FirstOrDefault();
        }
        ContextMenu menu = EditorClipboardMenu.CreateForTextArea(adapter.Editor.TextArea, pasteImages);
        if (target is not { } block) return menu;
        string source = adapter.Markdown;
        EditorClipboardMenu.PrependBlockItems(menu, adapter.Editor.TextArea,
            block.Kind == MarkdownVisualKind.Quote ? "删除引用块" : "删除代码块",
            () => { if (session == adapter.SessionVersion) Delete(menu, source, block); },
            block.Kind == MarkdownVisualKind.CodeBlock ? () => { if (session == adapter.SessionVersion) EditLanguage(menu, source, block); }
        : null);
        return menu;
    }

    private bool IsCurrent(string source) => !_disposed && !adapter.IsReadOnly && adapter.Editor.IsLoaded && adapter.Markdown == source;

    private void Delete(ContextMenu menu, string source, MarkdownVisualSpan block)
    {
        if (!IsCurrent(source)) return;
        ContextMenuAnimations.Close(menu);
        adapter.ApplyEdit(MarkdownBlockCommands.Delete(adapter.Model, block));
        adapter.FocusEditor();
    }

    private void EditLanguage(ContextMenu menu, string source, MarkdownVisualSpan block)
    {
        if (!IsCurrent(source) || Window.GetWindow(adapter.Editor) is not { } owner) return;
        int selectionStart = adapter.Editor.SelectionStart;
        int selectionLength = adapter.Editor.SelectionLength;
        ContextMenuAnimations.Close(menu);
        long session = adapter.SessionVersion;
        string? language = new CodeLanguageDialog(block.CodeLabel ?? string.Empty).ShowDialog(owner);
        if (session != adapter.SessionVersion || !IsCurrent(source)) return;
        if (language is not null && language != (block.CodeLabel ?? string.Empty))
        {
            string updated = MarkdownBlockCommands.WithLanguage(adapter.Model, block, language);
            if (updated != source)
            {
                MarkdownDocumentModel projection = new(updated);
                int visibleStart = MapVisibleOffset(adapter.VisibleText, projection.VisibleText, selectionStart);
                int visibleEnd = MapVisibleOffset(adapter.VisibleText, projection.VisibleText, selectionStart + selectionLength);
                int start = projection.SourceOffsetForInsertion(visibleStart);
                int end = selectionLength == 0 ? start : projection.SourceOffsetFromVisible(visibleEnd);
                adapter.ApplyEdit(new MarkdownEditResult(updated, start, end));
            }
        }
        else adapter.Editor.Select(selectionStart, selectionLength);
        adapter.FocusEditor();
    }

    private static int MapVisibleOffset(string before, string after, int offset)
    {
        if (before == after) return offset;
        // Converting indented code to a fence removes structural leading spaces.
        // Keep the caret/selection on the same code characters on each logical line.
        int lineStart = offset == 0 ? 0 : before.LastIndexOf('\n', offset - 1) + 1;
        int lineEnd = before.IndexOf('\n', offset);
        if (lineEnd < 0) lineEnd = before.Length;
        int newStart = 0;
        for (int index = 0; index < lineStart; index++)
        {
            if (before[index] != '\n') continue;
            int next = after.IndexOf('\n', newStart);
            if (next < 0) return after.Length;
            newStart = next + 1;
        }
        int newEnd = after.IndexOf('\n', newStart);
        if (newEnd < 0) newEnd = after.Length;
        string oldLine = before[lineStart..lineEnd];
        string newLine = after[newStart..newEnd];
        int removedIndent = oldLine.EndsWith(newLine, StringComparison.Ordinal) ? oldLine.Length - newLine.Length : 0;
        return newStart + Math.Clamp(offset - lineStart - removedIndent, 0, newLine.Length);
    }

    public void Dispose()
    {
        _disposed = true;
        if (adapter.Editor.TextArea.ContextMenu is { } menu) ContextMenuAnimations.Close(menu, immediate: true);
    }
}
