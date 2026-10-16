using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MemeMomo.UI.Popup;

namespace MemeMomo.Components;

public partial class MarkdownEditor
{
    private long _sessionVersion;
    internal long SessionVersion => _sessionVersion;
    private bool _lifecycleSuspended;
    private readonly SemaphoreSlim _editorSaveGate = new(1, 1);
    public bool IsReadOnlyPreview { get; private set; }
    public readonly record struct ViewPosition(int Start, int End, double VerticalOffset);
    public ViewPosition CaptureViewPosition() => new(_adapter.SelectionStart, _adapter.SelectionEnd, _adapter.Editor.VerticalOffset);
    public void RestoreViewPosition(ViewPosition position)
    {
        long version = _sessionVersion;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!IsCurrentSession(version)) return;
            _adapter.SelectSourceRange(position.Start, position.End);
            _adapter.Editor.ScrollToVerticalOffset(position.VerticalOffset);
        }));
    }
    private bool IsCurrentSession(long version) => Volatile.Read(ref _disposed) == 0 && version == _sessionVersion;
    private bool CanWriteSession(long version) => IsCurrentSession(version) && !IsReadOnlyPreview && !_lifecycleSuspended;

    internal void ClosePageMenus()
    {
        ContextMenuAnimations.Close(MoreMenu, immediate: true);
        ContextMenuAnimations.Close(HeadingMenu, immediate: true);
        PopupAnimations.Close(TablePickerPopup, immediate: true, restoreFocus: false);
    }

    private void ResetSession(bool preview)
    {
        ++_sessionVersion;
        CancelAutoSave();
        _savedMarkdown = null;
        _lifecycleSuspended = false;
        Toolbar.IsEnabled = true;
        _adapter.Editor.IsHitTestVisible = true;
        IsReadOnlyPreview = preview;
        AllowDrop = !preview;
        _adapter.IsReadOnly = preview;
        HideCodeCopyButton();
        HideStatus();
        _dropStatus.Clear();
        ContextMenuAnimations.Close(MoreMenu, immediate: true);
        ContextMenuAnimations.Close(HeadingMenu, immediate: true);
        PopupAnimations.Close(TablePickerPopup, immediate: true, restoreFocus: false);
        _adapter.ResetInteractionSession();
    }

    public void ShowReadOnlyPreview(string? markdown)
    {
        ThrowIfDisposed();
        ResetSession(preview: true);
        IsNewMemo = false;
        _adapter.SetMarkdown(markdown, clearUndo: true, preserveViewState: false);
        _adapter.Editor.ScrollToVerticalOffset(0);
        UpdateActions();
        UpdateWatermark();
    }

    internal async Task<string> SuspendAndCollectAsync()
    {
        ThrowIfDisposed();
        // Moving focus commits the current IME composition before freezing input.
        Keyboard.ClearFocus();
        CancelAutoSave();
        _lifecycleSuspended = true;
        _adapter.IsReadOnly = true;
        Toolbar.IsEnabled = false;
        _adapter.Editor.IsHitTestVisible = false;
        await _editorSaveGate.WaitAsync();
        try { ThrowIfDisposed(); return Markdown; }
        finally { _editorSaveGate.Release(); }
    }
    internal void ResumeAfterLifecycleFailure()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _lifecycleSuspended = false;
        Toolbar.IsEnabled = true;
        _adapter.Editor.IsHitTestVisible = true;
        _adapter.IsReadOnly = IsReadOnlyPreview;
    }
}
