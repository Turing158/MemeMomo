using System.ComponentModel;
using System.Windows;

namespace MemeMomo;

public partial class MemoPopoutWindow
{
    private Task<bool>? _normalCloseTask;
    private bool _normalClosePending;
    private bool _normalCloseScheduled;
    private bool _normalCloseApproved;
    private bool _applicationExitPending;
    private bool IsPreparingToClose => _normalClosePending || _normalCloseApproved || _applicationExitPending;

    internal Task<bool> WaitForReminderSubmissionAsync() => _reminderView?.WaitForPendingSubmissionAsync() ?? Task.FromResult(true);

    internal void SetApplicationExitPending(bool pending)
    {
        if (_cleanupDone) return;
        _applicationExitPending = pending;
        UpdateCloseInput();
    }

    internal Task<bool> RequestCloseAsync()
    {
        if (_cleanupDone || _normalCloseApproved) return Task.FromResult(true);
        if (_applicationExitPending) return Task.FromResult(false);
        if (IsQuickAddDraft) return RequestQuickAddCloseAsync();
        if (_normalCloseTask is { IsCompleted: false }) return _normalCloseTask;
        return _normalCloseTask = CloseNormalMemoAsync();
    }

    private async Task<bool> CloseNormalMemoAsync()
    {
        _normalClosePending = true;
        UpdateCloseInput();
        try
        {
            if (!await WaitForReminderSubmissionAsync()) return false;
            if (_cleanupDone || _sourceDeleted) return true;
            if (IsEditorOwner && !await MarkdownEditor.CompleteEditingAsync()) return false;
            if (_cleanupDone || _sourceDeleted) return true;
            ReleaseEditorOwnership();
            _normalCloseApproved = true;
            _reminderView?.EndSession();
            _reminderTransition?.Reset();
            CloseWithTransition();
            return true;
        }
        finally
        {
            _normalClosePending = false;
            if (!_cleanupDone) UpdateCloseInput();
        }
    }

    private void UpdateCloseInput()
    {
        _reminderView?.SetHostClosing(IsPreparingToClose);
        UpdateReminderButtonVisual();
        UpdateToolbarButtonVisual();
        SynchronizeReminderVisibility();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_sourceDeleted || _normalCloseApproved)
        {
            base.OnClosing(e);
            return;
        }
        if (!_quickCloseApproved && (_quickAddSaving || IsQuickAddDraft))
        {
            e.Cancel = true;
            ResetWindowTransition();
            if (!_quickAddSaving && !_quickClosePending && !_quickCloseScheduled)
            {
                _quickCloseScheduled = true;
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    _quickCloseScheduled = false;
                    await RequestQuickAddCloseAsync();
                }));
            }
            return;
        }
        if (_quickCloseApproved)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        ResetWindowTransition();
        if (_normalClosePending || _normalCloseScheduled || _applicationExitPending) return;
        _normalCloseScheduled = true;
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            _normalCloseScheduled = false;
            await RequestCloseAsync();
        }));
    }
}
