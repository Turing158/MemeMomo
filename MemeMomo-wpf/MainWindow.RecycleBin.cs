using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MemeMomo.Components;
using MemeMomo.Components.Dialogs;
using MemeMomo.Models;
using MemeMomo.Services;
using MemeMomo.UI;
using MemeMomo.UI.Notifications;
using MemeMomo.UI.Popup;
using Button = System.Windows.Controls.Button;

namespace MemeMomo;

public partial class MainWindow
{
    public bool IsRecycleBinMode { get; private set; }
    private bool _modeChanging;
    private bool _recycleActionPending;
    private NotificationMessage? _undoNotification;
    private Guid? _undoNotificationId;
    private long _displayedLifecycleVersion;
    private NormalSession? _normalSession;
    private MemoItem? _previewMemo;
    private sealed record NormalSession(Guid? EditingId, string Draft, MemoKind Kind, string? Status,
        MarkdownEditor.ViewPosition Position, double ScrollOffset);

    internal async Task<bool> SetRecycleBinModeAsync(bool recycle)
    {
        if (IsSettingsPageVisible || _settingsNavigating) return false;
        if (_modeChanging || _recycleActionPending || _disposed) return false;
        if (recycle == IsRecycleBinMode) return true;
        _modeChanging = true;
        FilterBadge.CloseMenu();
        FilterBadge.IsEnabled = ReturnMemosButton.IsEnabled = false;
        try
        {
            CloseModeMenus();
            _dragManager?.Detach();
            Keyboard.ClearFocus();
            if (recycle)
            {
                var session = new NormalSession(_displayedMemo?.Id,
                    MarkdownEditor.IsNewMemo ? MarkdownEditor.Markdown : _newMemoDraft,
                    ComposerTaskSelector.NewKind, ComposerTaskSelector.NewStatusId,
                    MarkdownEditor.CaptureViewPosition(), MemoScrollViewer.VerticalOffset);
                if (_displayedMemo is { } edited)
                {
                    using var reservation = await MemoEditCoordinator.Shared.CollectForLifecycleAsync(edited.Id);
                    if (!ReferenceEquals(_displayedMemo, edited) || !MemoEditCoordinator.Shared.IsOwner(edited.Id, this))
                        throw new InvalidOperationException(LocalizationService.Get("无法获取编辑权，请稍后重试。"));
                    await _viewModel.UpdateItemAndSaveAsync(edited.Id, reservation.Content ?? edited.Content, _displayedLifecycleVersion);
                    reservation.Committed = true;
                }
                else session = session with { Draft = await MarkdownEditor.SuspendAndCollectAsync() };
                if (_disposed) return false;
                _responsiveLayout.CompleteTransition();
                _pageTransition.CaptureCurrentPage();
                StopDeleteAnimation();
                _normalSession = session;
                _viewModel.EndEdit();
                SetDisplayedMemo(null);
                IsRecycleBinMode = true;
                _previewMemo = null;
                _viewModel.RecycleView.EnsureSelection();
                RefreshRecycleView();
                RestoreListOffset(_viewModel.RecycleView.ScrollOffset);
            }
            else
            {
                var session = _normalSession;
                var edited = _viewModel.Memos.FirstOrDefault(item => item.Id == session?.EditingId);
                if (edited is not null && (edited.IsLifecyclePending || !await AcquireMainEditorAsync(edited)))
                {
                    OnMemoSaveFailed(LocalizationService.Get("无法获取编辑权，请稍后重试。"));
                    return false;
                }
                if (_disposed) return false;
                _responsiveLayout.CompleteTransition();
                _pageTransition.CaptureCurrentPage();
                StopDeleteAnimation();
                _viewModel.RecycleView.ScrollOffset = MemoScrollViewer.VerticalOffset;
                IsRecycleBinMode = false;
                _previewMemo = null;
                SetDisplayedMemo(edited);
                _newMemoDraft = session?.Draft ?? string.Empty;
                if (edited is not null)
                {
                    _viewModel.BeginEdit(edited.Id);
                    MarkdownEditor.BeginExistingEdit(edited.Content);
                }
                else
                {
                    MarkdownEditor.BeginNew(_newMemoDraft);
                    _newMemoDraft = string.Empty;
                    if (session is not null)
                    {
                        ComposerTaskSelector.NewKind = session.Kind;
                        ComposerTaskSelector.NewStatusId = session.Status;
                        ComposerTaskSelector.Refresh();
                        if (session.EditingId is not null) ShowRecycleNotice("原备忘录已删除，已返回新建草稿。");
                    }
                }
                if (session is not null) MarkdownEditor.RestoreViewPosition(session.Position);
                RefreshRecycleView();
                RestoreListOffset(session?.ScrollOffset ?? 0);
                _normalSession = null;
            }
            _pageTransition.Start(forward: recycle);
            return true;
        }
        catch (Exception error)
        {
            _pageTransition.Reset();
            MarkdownEditor.ResumeAfterLifecycleFailure();
            OnMemoSaveFailed(error.Message);
            return false;
        }
        finally
        {
            _modeChanging = false;
            ReturnMemosButton.IsEnabled = true;
            RefreshFilterBadge();
            if (!IsRecycleBinMode) _dragManager?.Attach();
        }
    }

    private Task<bool> AcquireMainEditorAsync(MemoItem item) => MemoEditCoordinator.Shared.AcquireAsync(
        item.Id, this, RelinquishActiveEditorAsync, MarkdownEditor.SuspendAndCollectAsync, MarkdownEditor.ResumeAfterLifecycleFailure);

    private void CloseModeMenus()
    {
        foreach (var element in FindVisualChildren<FrameworkElement>(WindowRoot))
            if (element.ContextMenu is { } menu) ContextMenuAnimations.Close(menu, immediate: true);
        foreach (var selector in FindVisualChildren<MemoTaskStatusSelector>(WindowRoot))
            if (selector.StatusMenu is { } menu) ContextMenuAnimations.Close(menu, immediate: true);
    }

    private void RestoreListOffset(double offset) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
        new Action(() => { if (!_disposed) MemoScrollViewer.ScrollToVerticalOffset(offset); }));

    private void RefreshRecycleView()
    {
        if (FilterBadge is null) return;
        if (_undoNotification is { } undo && _viewModel.DeletedMemos.All(item => item.Id != _undoNotificationId))
        {
            Notifications.Queue.Dismiss(undo);
            _undoNotification = null;
            _undoNotificationId = null;
        }
        RefreshFilterBadge();
        RecycleTitle.Text = LocalizationService.Format("回收站（{0}）", _viewModel.DeletedMemos.Count);
        RecycleListTools.Visibility = IsRecycleBinMode ? Visibility.Visible : Visibility.Collapsed;
        ClearRecycleBinButton.IsEnabled = !_recycleActionPending && _viewModel.DeletedMemos.Count > 0;
        MemoList.ItemsSource = IsRecycleBinMode ? _viewModel.RecycleView.Items : _viewModel.TaskView.Items;
        MemoScrollViewer.Visibility = (IsRecycleBinMode ? _viewModel.DeletedMemos.Count : _viewModel.Memos.Count) > 0
            ? Visibility.Visible : Visibility.Collapsed;
        NormalEmptyState.Visibility = !IsRecycleBinMode && _viewModel.Memos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecycleEmptyState.Visibility = IsRecycleBinMode && _viewModel.DeletedMemos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (IsRecycleBinMode)
        {
            NoFilterResults.Visibility = Visibility.Collapsed;
            ClearTaskNotices();
            if (_viewModel.RecycleView.SelectedItem is null && _viewModel.RecycleView.Items.Count > 0)
            { _viewModel.RecycleView.EnsureSelection(); return; }
            var selected = _viewModel.RecycleView.SelectedItem;
            if (!ReferenceEquals(_previewMemo, selected) || !MarkdownEditor.IsReadOnlyPreview)
            {
                _previewMemo = selected;
                MarkdownEditor.ShowReadOnlyPreview(selected?.Content);
            }
        }
        else RefreshTaskView();
        UpdateEditVisual();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { if (!_disposed) UpdateEditVisual(); }));
    }

    private async void OnReturnMemosClick(object sender, RoutedEventArgs e) => await SetRecycleBinModeAsync(false);
    private void ShowRecycleNotice(string message, Guid? undoId = null)
    {
        var notification = Notifications.Show(LocalizationService.Get(message), NotificationKind.Success,
            undoId is null ? null : LocalizationService.Get("撤销"),
            undoId is not { } id ? null : async () =>
            {
                if (_recycleActionPending) return false;
                await RestoreMemoAsync(id);
                return _viewModel.DeletedMemos.All(item => item.Id != id);
            });
        if (undoId is not null)
        {
            _undoNotification = notification;
            _undoNotificationId = undoId;
        }
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: MemoItem item }) await RestoreMemoAsync(item.Id);
    }
    internal async Task RestoreMemoAsync(Guid id)
    {
        if (_recycleActionPending) return;
        await RunRecycleActionAsync(async () => await _viewModel.RecycleBin.RestoreAsync(id), result =>
        {
            bool hidden = _viewModel.TaskView.Items.All(item => item.Id != id);
            ShowRecycleNotice((hidden ? LocalizationService.Get("已恢复；当前筛选隐藏了此记录。") : LocalizationService.Get("已恢复"))
                + (result.Error is null ? string.Empty : " · " + result.Error));
        });
    }
    private async Task PurgeMemoAsync(MemoItem item)
    {
        if (_recycleActionPending) return;
        await RunRecycleActionAsync(async () =>
        {
            if (!await WindowNotifications.ConfirmAsync(this, LocalizationService.Get("彻底删除"),
                LocalizationService.Format("彻底删除“{0}”？删除后无法从回收站恢复。", item.Title))) return null;
            return await _viewModel.RecycleBin.PurgeAsync(item.Id);
        }, _ => ShowRecycleNotice("已彻底删除"));
    }
    private async void OnClearRecycleBinClick(object sender, RoutedEventArgs e)
    {
        if (_recycleActionPending) return;
        var ids = _viewModel.DeletedMemos.Select(item => item.Id).ToArray();
        if (ids.Length == 0) return;
        await RunRecycleActionAsync(async () =>
        {
            if (!await WindowNotifications.ConfirmAsync(this, LocalizationService.Get("清空回收站"),
                LocalizationService.Format("彻底删除回收站中的 {0} 条记录？删除后无法从回收站恢复。", ids.Length))) return null;
            return await _viewModel.RecycleBin.ClearAsync(ids);
        }, _ => ShowRecycleNotice("回收站已清空"));
    }
    private async Task RunRecycleActionAsync(Func<Task<StorageResult?>> action, Action<StorageResult> success)
    {
        _recycleActionPending = true;
        var focused = Keyboard.FocusedElement;
        MemoList.IsEnabled = ReturnMemosButton.IsEnabled = false;
        ClearRecycleBinButton.IsEnabled = false;
        try
        {
            var result = await action();
            if (result is { Success: true }) success(result);
            else if (result is not null) OnMemoSaveFailed(result.Error ?? string.Empty);
        }
        finally
        {
            _recycleActionPending = false;
            MemoList.IsEnabled = ReturnMemosButton.IsEnabled = true;
            RefreshRecycleView();
            if (focused is UIElement { IsVisible: true, IsEnabled: true } element) element.Focus();
            else if (_viewModel.RecycleView.SelectedItem is { } selected) FindCard(selected)?.Focus();
            else ReturnMemosButton.Focus();
        }
    }
    private bool HandleRecycleCardKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!IsRecycleBinMode) return false;
        if (e.OriginalSource is DependencyObject source && FindParent<Button>(source) is not null) return true;
        if (sender is not FrameworkElement { DataContext: MemoItem item }) return true;
        var items = _viewModel.RecycleView.Items;
        int index = items.IndexOf(item);
        if (e.Key is Key.Up or Key.Down && items.Count > 0)
        {
            item = items[Math.Clamp(index + (e.Key == Key.Up ? -1 : 1), 0, items.Count - 1)];
            FindCard(item)?.Focus();
            FindCard(item)?.BringIntoView();
        }
        else if (e.Key is not (Key.Enter or Key.Space)) return true;
        _viewModel.RecycleView.Select(item);
        e.Handled = true;
        return true;
    }

    internal async Task<bool> SaveNormalSessionBeforeExitAsync()
    {
        // The preview is never treated as a new draft. Existing main-window drafts
        // retain their original close behavior; persisted editors are flushed.
        if (_modeChanging || _recycleActionPending) return false;
        if (!IsRecycleBinMode && _displayedMemo is not null && !await MarkdownEditor.CompleteEditingAsync()) return false;
        var result = await _viewModel.TrySaveAsync();
        if (!result.Success) OnMemoSaveFailed(result.Error ?? string.Empty);
        return result.Success;
    }
}
