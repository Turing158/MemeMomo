using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MemeMomo.Components;
using MemeMomo.Models;
using MemeMomo.Services;
using MemeMomo.UI.Notifications;
using MemeMomo.UI.Popup;
using Button = System.Windows.Controls.Button;

namespace MemeMomo;

public partial class MainWindow
{
    private Guid? _filteredNewMemoId;
    private bool _reorderNoticeShown;
    private Guid? _hiddenNoticeMemoId;
    private NotificationMessage? _reorderNotification;
    private NotificationMessage? _hiddenNotification;
    internal MemoTaskStatusSelector ComposerTaskSelector => (MemoTaskStatusSelector)MarkdownEditor.ToolbarLeadingContent!;
    private void OnMemoCardMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        OpenMemoCardMenu(sender, e, PlacementMode.MousePoint);
    }

    private void OnMemoCardPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (HandleRecycleCardKey(sender, e)) return;
        if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
            OpenMemoCardMenu(sender, e, PlacementMode.Bottom);
    }

    private void OpenMemoCardMenu(object sender, RoutedEventArgs e, PlacementMode placement)
    {
        if (IsRecycleBinMode || _modeChanging || sender is not FrameworkElement card) return;
        var selector = FindVisualChildren<MemoTaskStatusSelector>(card).FirstOrDefault();
        if (selector is null) return;
        e.Handled = true;
        selector.OpenMenu(card, placement);
    }

    private void OnMemoSaveFailed(string error)
    {
        Notifications.Show(LocalizationService.Format("保存失败，请重试：{0}", error), NotificationKind.Error);
    }
    private void RefreshTaskView()
    {
        if (FilterBadge is null) return;
        RefreshFilterBadge();
        if (IsRecycleBinMode) { RefreshRecycleView(); return; }
        if (NormalEmptyState is not null)
        {
            NormalEmptyState.Visibility = _viewModel.Memos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            MemoScrollViewer.Visibility = _viewModel.Memos.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        var view = _viewModel.TaskView;
        if (!view.CanReorder && !_reorderNoticeShown)
            _reorderNotification = Notifications.Queue.Show(LocalizationService.Get("筛选时只能拖出便签；清除条件后可重排。"));
        if (view.CanReorder && _reorderNotification is { } reorder) Notifications.Queue.Dismiss(reorder);
        _reorderNoticeShown = !view.CanReorder;
        NoFilterResults.Visibility = _viewModel.Memos.Count > 0 && view.VisibleCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool hiddenDraft = _displayedMemo is not null && !view.Items.Contains(_displayedMemo);
        bool hiddenNew = _filteredNewMemoId is { } id && _viewModel.Memos.Any(item => item.Id == id) && view.Items.All(item => item.Id != id);
        Guid? hiddenId = hiddenDraft ? _displayedMemo?.Id : hiddenNew ? _filteredNewMemoId : null;
        if (hiddenId is not null && hiddenId != _hiddenNoticeMemoId) ShowFilteredMemoNotice();
        if (hiddenId is null && _hiddenNotification is { } hidden) Notifications.Queue.Dismiss(hidden);
        _hiddenNoticeMemoId = hiddenId;
        if (!hiddenNew) _filteredNewMemoId = null;
    }

    private void ShowFilteredMemoNotice() => _hiddenNotification = Notifications.Queue.Show(
        LocalizationService.Get("当前记录不在筛选结果中"), NotificationKind.Information,
        LocalizationService.Get("显示全部"), () => { _viewModel.TaskView.Clear(); return Task.FromResult(true); });

    private void ClearTaskNotices()
    {
        if (_reorderNotification is { } reorder) Notifications.Queue.Dismiss(reorder);
        if (_hiddenNotification is { } hidden) Notifications.Queue.Dismiss(hidden);
        _reorderNoticeShown = false;
        _hiddenNoticeMemoId = null;
    }
    private void OnClearFilters(object sender, RoutedEventArgs e) => _viewModel.TaskView.Clear();
}
