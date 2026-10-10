using System.Windows;

namespace MemeMomo.UI.Windows;

/// <summary>Keeps one unsaved quick-add window without reusing already saved memos.</summary>
internal sealed class QuickAddWindowController(
    Func<System.Windows.Point, MemoPopoutWindow> create,
    Action<MemoPopoutWindow, System.Windows.Point> relocate)
{
    private MemoPopoutWindow? _draft;

    internal MemoPopoutWindow Open(System.Windows.Point pointerPixels)
    {
        if (_draft is { IsQuickAddDraft: true } existing)
        {
            if (!existing.IsVisible) existing.Show();
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            relocate(existing, pointerPixels);
            existing.Activate();
            return existing;
        }

        if (_draft is not null) _draft.Closed -= OnClosed;
        _draft = create(pointerPixels);
        _draft.Closed += OnClosed;
        return _draft;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (sender is MemoPopoutWindow window) window.Closed -= OnClosed;
        if (ReferenceEquals(sender, _draft)) _draft = null;
    }
}
