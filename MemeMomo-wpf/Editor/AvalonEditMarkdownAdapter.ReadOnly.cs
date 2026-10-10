using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MemeMomo.UI.Popup;

namespace MemeMomo.Editor;

internal sealed partial class AvalonEditMarkdownAdapter
{
    internal long SessionVersion { get; private set; }
    internal bool IsReadOnly
    {
        get => Editor.IsReadOnly;
        set
        {
            Editor.IsReadOnly = value;
            Editor.AllowDrop = !value;
            InputMethod.SetIsInputMethodEnabled(Editor, !value);
            foreach (var element in _tableElements.Values)
                if (element.Element is MarkdownTableControl table) table.UpdateReadOnly();
        }
    }
    internal void ResetInteractionSession()
    {
        SessionVersion++;
        _projectionRefreshPending = false;
        _activeTableCell = null;
        foreach (var element in _tableElements.Values)
            if (element.Element is MarkdownTableControl table) table.EndSession();
        _tableElements.Clear();
        if (Editor.TextArea.ContextMenu is { } menu) ContextMenuAnimations.Close(menu, immediate: true);
        Editor.Select(0, 0);
        Editor.Document.UndoStack.ClearAll();
    }
    private void OnPreviewReadOnlyCommand(object sender, ExecutedRoutedEventArgs e)
    {
        if (IsReadOnly && e.Command != ApplicationCommands.Copy && e.Command != ApplicationCommands.SelectAll)
            e.Handled = true;
    }
    private void OnPreviewReadOnlyCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (!IsReadOnly || e.Command == ApplicationCommands.Copy || e.Command == ApplicationCommands.SelectAll) return;
        e.CanExecute = false;
        e.Handled = true;
    }
}
