using System.Windows;
using MemeMomo.Services;
using MemeMomo.UI.Windows;

namespace MemeMomo;

public partial class App
{
    private bool _exitPromptPending;
    private QuickAddWindowController? _quickAddWindows;

    private void OpenQuickAdd()
    {
        if (_monitorService.TryGetCursorState(out System.Windows.Point pointer, out _))
            OpenQuickAddAtPointer(pointer);
    }

    internal MemoPopoutWindow? OpenQuickAddAtPointer(System.Windows.Point pointerPixels)
    {
        if (_typedMainWindow is null || Volatile.Read(ref _exitRequested) != 0 || _exitPromptPending) return null;
        var viewModel = _typedMainWindow.ViewModel;
        _quickAddWindows ??= new QuickAddWindowController(
            pointer => RegisterMemoPopout(new MemoPopoutWindow(ClampPopoutPosition(pointer),
                (content, kind, statusId) => viewModel.AddItemAndSaveAsync(content, kind, statusId),
                (memo, content) => viewModel.UpdateItemAndSaveAsync(memo.Id, content))),
            (window, pointer) =>
            {
                var monitor = _monitorService.FromPoint(pointer);
                var placement = MemoWindowPlacement.FromPointerPixels(pointer,
                    new System.Windows.Size(window.Width, window.Height), monitor);
                AnimateWindowPosition(window, new System.Windows.Point(placement.Left, placement.Top));
            });
        MemoPopoutWindow result = _quickAddWindows.Open(pointerPixels);
        SetLatestMemoPopout(result);
        return result;
    }
}
