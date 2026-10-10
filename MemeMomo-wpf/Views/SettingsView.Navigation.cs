using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MemeMomo.Models;
using MemeMomo.Services;
using MemeMomo.UI;
using MemeMomo.UI.Animation;
using MemeMomo.UI.Layout;
using MemeMomo.UI.Notifications;
using MemeMomo.ViewModels;
using Panel = System.Windows.Controls.Panel;
using Point = System.Windows.Point;

namespace MemeMomo.Views;

public partial class SettingsView
{
    private readonly SettingsPageLayoutController _layout;
    private readonly PageTransitionController _categoryTransition;
    private readonly Dictionary<SettingsCategory, Panel> _categoryPanels = [];
    private CancellationTokenSource? _pageCancellation;
    private Task<int>? _resetPrompt;
    private Task? _leaveTask;
    private DispatcherOperation? _positionRestore;
    private int _positionGeneration;
    private int _pageGeneration;
    private bool _active;
    private bool _leaving;
    private bool _disposed;
    private bool _updatingCategory;
    private bool _restoringPosition;
    private bool? _categoryTransitionForward;

    private sealed record CategoryOption(SettingsCategory Id, string Label);
    internal Func<Task>? ReturnRequested { get; set; }
    internal bool IsPageActive => _active && !_leaving && !_disposed;

    internal void Activate(AppSettings runtime)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_active && !_leaving) return;
        Session.SynchronizeRuntime(runtime);
        LocalizationService.SetLanguage(_settings.Language);
        _leaveTask = null;
        _active = true;
        _leaving = false;
        EnsurePageCancellation();
        ReturnButton.IsEnabled = true;
        UpdateTaskStatusPageVisibility();
        if (IsTaskStatusManagerVisible) _taskStatusManager?.Activate();
        ApplySettingsToUi();
        _layout.Refresh();
        RenderAboutContent();
        RestoreCategoryPosition();
        Session.RetryLatestSnapshot();
    }

    internal Task DeactivateAsync() => _leaveTask ??= LeavePageAsync();

    private async Task LeavePageAsync()
    {
        // Read the slider before disabling controls or suppressing change events.
        // This also covers returning while a key or a pointer gesture is unfinished.
        FlushSlider();
        SaveCategoryPosition();
        _leaving = true;
        CancelPageOperations();
        SettingsBody.IsEnabled = ReturnButton.IsEnabled = false;
        if (_taskStatusManager is not null) await _taskStatusManager.DeactivateAsync();
        await WaitForPendingSaveAsync();
        _active = false;
    }

    private void FlushSlider()
    {
        if (Mouse.Captured is DependencyObject captured && IsAncestorOf(captured)) Mouse.Capture(null);
        if (_settings.MainWindowDockSize == DockSizeSlider.Value) return;
        _settings.MainWindowDockSize = DockSizeSlider.Value;
        CommitChange();
    }

    private async void OnReturnClick(object sender, RoutedEventArgs e)
    {
        if (!IsPageActive || _subpageNavigating) return;
        if (IsTaskStatusManagerVisible) await ReturnFromTaskStatusManagerAsync();
        else if (ReturnRequested is { } navigate) await navigate();
    }

    private void EnsurePageCancellation()
    {
        if (_pageCancellation is not null || !_active || _leaving) return;
        _pageCancellation = new CancellationTokenSource();
        ++_pageGeneration;
    }

    private bool IsCurrentPage(int generation) => IsPageActive && generation == _pageGeneration
        && _pageCancellation is { IsCancellationRequested: false };

    private void OnPresentationChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_active || _leaving || _disposed) return;
        if (IsVisible && IsHitTestVisible)
        {
            EnsurePageCancellation();
            if (IsTaskStatusManagerVisible) _taskStatusManager?.Activate();
            RestoreCategoryPosition();
        }
        else
        {
            FlushSlider();
            SaveCategoryPosition();
            CancelPageOperations();
        }
    }

    private void CancelPageOperations()
    {
        _subpageTransition.Reset();
        ResetCategoryTransition();
        ++_pageGeneration;
        _pageCancellation?.Cancel();
        _pageCancellation?.Dispose();
        _pageCancellation = null;
        if (_resetPrompt is { } prompt) WindowNotifications.FindHost(_owner)?.CancelPrompt(prompt);
        _resetPrompt = null;
        CloseSettingsMenus();
        EndCapture();
        ClearValidation();
        ClearConflict();
        _updateFeedback?.Clear();
        if (_aboutUpdateState == AboutUpdateState.Checking)
        {
            _aboutUpdateState = AboutUpdateState.Initial;
            RenderAboutContent();
        }
    }

    private void CloseSettingsMenus()
    {
        CategorySelector.IsDropDownOpen = false;
        DefaultStatusSelector.CloseMenu();
    }

    private void InitializeCategories()
    {
        _categoryPanels.Add(SettingsCategory.General, GeneralCategory);
        _categoryPanels.Add(SettingsCategory.Appearance, AppearanceCategory);
        _categoryPanels.Add(SettingsCategory.Windows, WindowsCategory);
        _categoryPanels.Add(SettingsCategory.Tasks, TasksCategory);
        _categoryPanels.Add(SettingsCategory.Hotkeys, HotkeysCategory);
        _categoryPanels.Add(SettingsCategory.About, AboutCategory);
        RefreshCategoryLabels();
    }

    private void RefreshCategoryLabels()
    {
        _updatingCategory = true;
        try
        {
            CategoryOption[] options =
            [
                new(SettingsCategory.General, LocalizationService.Get("通用")),
                new(SettingsCategory.Appearance, LocalizationService.Get("外观")),
                new(SettingsCategory.Windows, LocalizationService.Get("窗口与便签")),
                new(SettingsCategory.Tasks, LocalizationService.Get("任务与提醒")),
                new(SettingsCategory.Hotkeys, LocalizationService.Get("快捷键")),
                new(SettingsCategory.About, LocalizationService.Get("关于与维护"))
            ];
            CategorySelector.ItemsSource = CategoryNavigation.ItemsSource = options;
            CategorySelector.SelectedValue = CategoryNavigation.SelectedValue = Session.Category;
        }
        finally { _updatingCategory = false; }
    }

    private void OnCategorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingCategory || e.AddedItems.Count == 0 || e.AddedItems[0] is not CategoryOption category) return;
        SelectCategory(category.Id);
    }

    internal void SelectCategory(SettingsCategory category)
    {
        if (_disposed || _leaving || IsTaskStatusManagerVisible || !_categoryPanels.ContainsKey(category) || Session.Category == category) return;
        SaveCategoryPosition();
        EndCapture();
        CloseSettingsMenus();
        if (IsPageActive && IsVisible && IsHitTestVisible && MotionPreferences.AnimationsEnabled)
        {
            _categoryTransition.CaptureCurrentPage();
            _categoryTransitionForward = category > Session.Category;
        }
        else ResetCategoryTransition();
        Session.Category = category;
        _updatingCategory = true;
        try
        {
            foreach (var pair in _categoryPanels)
                pair.Value.Visibility = pair.Key == category ? Visibility.Visible : Visibility.Collapsed;
            CategorySelector.SelectedValue = CategoryNavigation.SelectedValue = category;
        }
        finally { _updatingCategory = false; }
        RestoreCategoryPosition();
    }

    private void ResetCategoryTransition()
    {
        _categoryTransitionForward = null;
        _categoryTransition.Reset();
    }

    private void OnSettingsScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_active || _leaving || _restoringPosition || !ReferenceEquals(e.OriginalSource, SettingsScrollViewer)) return;
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0) RestoreCategoryPosition();
        else if (e.VerticalChange != 0) SaveCategoryPosition();
    }

    private void SaveCategoryPosition()
    {
        if (!_active || IsTaskStatusManagerVisible || _restoringPosition || !_categoryPanels.TryGetValue(Session.Category, out var panel)) return;
        double offset = SettingsScrollViewer.VerticalOffset;
        FrameworkElement? anchor = panel.Children.OfType<FrameworkElement>().FirstOrDefault(child =>
            child.Visibility == Visibility.Visible && child.TranslatePoint(new Point(0, child.ActualHeight), CategoryForms).Y > offset);
        double top = anchor?.TranslatePoint(new Point(), CategoryForms).Y ?? 0;
        Session.Positions[Session.Category] = new SettingsScrollPosition(anchor?.Name, offset - top, offset);
    }

    private void RestoreCategoryPosition()
    {
        if (!_active || _leaving || _disposed || IsTaskStatusManagerVisible) return;
        _restoringPosition = true;
        _positionRestore?.Abort();
        int generation = ++_positionGeneration;
        SettingsCategory category = Session.Category;
        _positionRestore = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _positionRestore = null;
            if (_disposed || !_active || _leaving || IsTaskStatusManagerVisible || category != Session.Category) return;
            Session.Positions.TryGetValue(category, out var position);
            var anchor = _categoryPanels[category].Children.OfType<FrameworkElement>()
                .FirstOrDefault(child => child.Name == position.Anchor && child.Visibility == Visibility.Visible);
            double offset = anchor is null ? position.Offset : anchor.TranslatePoint(new Point(), CategoryForms).Y + position.Inset;
            SettingsScrollViewer.ScrollToVerticalOffset(Math.Clamp(offset, 0, SettingsScrollViewer.ScrollableHeight));
            if (_categoryTransitionForward is { } forward)
            {
                // Apply the target category's scroll position before its first animated frame.
                SettingsScrollViewer.UpdateLayout();
                _categoryTransitionForward = null;
                _categoryTransition.Start(forward);
            }
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_disposed || generation != _positionGeneration) return;
                _restoringPosition = false;
                SaveCategoryPosition();
            }));
        }));
    }
}
