using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using MemeMomo.Components.Dialogs;
using MemeMomo.Markdown;
using MemeMomo.UI.Popup;
using Clipboard = System.Windows.Clipboard;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Point = System.Windows.Point;

namespace MemeMomo.Editor;

internal sealed class MarkdownLinkInteraction(AvalonEditMarkdownAdapter adapter) : IDisposable
{
    private bool _disposed;

    internal bool PrependItems(ContextMenu menu, TextEditor editor, MarkdownDocumentModel model, Point? point = null)
    {
        if (_disposed || adapter.IsSourceMode) return false;
        int offset = editor.SelectionLength > 0 ? editor.SelectionStart : editor.CaretOffset;
        if (point is { } location)
        {
            var position = editor.GetPositionFromPoint(location);
            if (position is not { } value) return false;
            offset = editor.Document.GetOffset(value.Location);
        }
        if (model.VisualAt(offset, MarkdownVisualKind.Link) is not { LinkTarget: { } url } link ||
            !model.TryGetLinkSourceRange(link, out int linkStart, out int linkEnd)) return false;
        string source = model.Markdown;
        string documentSource = adapter.Markdown;
        long session = adapter.SessionVersion;
        int selectionStart = editor.SelectionStart;
        int selectionLength = editor.SelectionLength;
        int copyStart = selectionLength == 0 ? link.Start : selectionStart;
        int copyEnd = selectionLength == 0 ? link.End : selectionStart + selectionLength;
        bool IsCurrent() => !_disposed && editor.IsLoaded && !adapter.IsSourceMode &&
            adapter.SessionVersion == session && adapter.Markdown == documentSource && model.Markdown == source;
        void Focus() => editor.Focus();
        void Apply(MarkdownEditResult edit)
        {
            if (editor is MarkdownTableCell cell) cell.ApplyEdit(edit);
            else adapter.ApplyEdit(edit);
            Focus();
        }
        void Copy(bool cut)
        {
            if (!IsCurrent() || (cut && (adapter.IsReadOnly || editor.IsReadOnly))) return;
            string text = MarkdownClipboardText.RestoreSources(model, copyStart, copyEnd) ?? model.VisibleText[copyStart..copyEnd];
            if (!TryCopy(text)) return;
            ContextMenuAnimations.Close(menu);
            if (cut)
            {
                MarkdownDocumentModel changed = new(source);
                changed.ApplyVisibleText(model.VisibleText.Remove(copyStart, copyEnd - copyStart), copyStart, copyEnd - copyStart, 0);
                int caret = changed.SourceOffsetForInsertion(copyStart);
                Apply(new MarkdownEditResult(changed.Markdown, caret, caret));
            }
            else Focus();
        }
        EditorClipboardMenu.PrependLinkItems(menu, editor.TextArea,
            adapter.IsReadOnly || editor.IsReadOnly ? null : () =>
            {
                if (!IsCurrent() || adapter.IsReadOnly || editor.IsReadOnly || Window.GetWindow(editor) is not { } owner) return;
                ContextMenuAnimations.Close(menu);
                LinkEditValue? value = new LinkEditDialog(model.VisibleText[link.Start..link.End], url).ShowDialog(owner);
                if (!IsCurrent() || adapter.IsReadOnly || editor.IsReadOnly) return;
                if (value is null)
                {
                    editor.Select(selectionStart, selectionLength);
                    Focus();
                    return;
                }
                string label = MarkdownClipboardText.EscapeLabel(value.Label);
                string replacement = $"[{label}]({value.Url})";
                Apply(new MarkdownEditResult(source[..linkStart] + replacement + source[linkEnd..],
                    linkStart + 1, linkStart + 1 + label.Length));
            },
            adapter.IsReadOnly || editor.IsReadOnly ? null : () =>
            {
                if (!IsCurrent() || adapter.IsReadOnly || editor.IsReadOnly) return;
                ContextMenuAnimations.Close(menu);
                string content = source.Substring(link.SourceStart, link.SourceLength);
                Apply(new MarkdownEditResult(source[..linkStart] + content + source[linkEnd..], linkStart, linkStart + content.Length));
            },
            () => { if (IsCurrent()) TryCopy(url); }, () => Copy(false), () => Copy(true));
        return true;
    }

    private static bool TryCopy(string text)
    {
        try
        {
            Clipboard.SetText(TextUtilities.NormalizeNewLines(text, Environment.NewLine));
            return true;
        }
        catch (ExternalException) { return false; }
    }

    public void Dispose() => _disposed = true;
}
