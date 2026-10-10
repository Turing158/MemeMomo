using System.Windows;
using System.Windows.Threading;
using MemeMomo.Models;
using MemeMomo.Services;
using MemeMomo.UI.Animation;

namespace MemeMomo.Views;

public partial class SettingsView
{
    private MemoTaskService? _taskService;
    private bool _refreshingTasks;
    private readonly PageTransitionController _subpageTransition;
    private TaskStatusManagerView? _taskStatusManager;
    private bool _subpageNavigating;
    internal bool IsTaskStatusManagerVisible { get; private set; }
    internal TaskStatusManagerView? TaskStatusManagerPage => _taskStatusManager;
    public void ConfigureTaskService(MemoTaskService service)
    {
        if (_taskService == service) return;
        if (_taskStatusManager is not null)
        {
            _taskStatusManager.Dispose();
            _taskStatusManager = null;
            TaskStatusManagerHost.Content = null;
            IsTaskStatusManagerVisible = false;
            UpdateTaskStatusPageVisibility();
        }
        DetachTaskSettings(); _taskService = service;
        service.Catalog.Changed += RefreshTaskSettings;
        service.DefaultsChanged += OnTaskDefaultsChanged;
        DefaultKindSelector.SelectionChanged += OnDefaultKindChanged;
        DefaultStatusSelector.Service = service;
        TaskSettingsCard.Visibility = Visibility.Visible;
        RefreshTaskSettings();
    }
    private void DetachTaskSettings()
    {
        if (_taskService is null) return;
        _taskService.Catalog.Changed -= RefreshTaskSettings;
        _taskService.DefaultsChanged -= OnTaskDefaultsChanged;
        DefaultKindSelector.SelectionChanged -= OnDefaultKindChanged;
        DefaultStatusSelector.Service = null;
    }
    private void OnTaskDefaultsChanged()
    {
        if (_taskService is null) return;
        _settings.DefaultTaskStatusId = _taskService.Settings.DefaultTaskStatusId;
        RefreshTaskSettings();
    }
    private void RefreshTaskSettings()
    {
        if (_taskService is null) return;
        _refreshingTasks = true;
        try
        {
            _taskService.Catalog.RepairDefault(_settings);
            DefaultKindSelector.Options = [new(nameof(MemoKind.Note), LocalizationService.Get("普通备忘")), new(nameof(MemoKind.Task), LocalizationService.Get("任务备忘"))];
            DefaultKindSelector.SelectedKey = _settings.DefaultMemoKind.ToString();
            DefaultStatusSelector.NewStatusId = _settings.DefaultTaskStatusId;
            DefaultStatusSelector.Refresh();
        }
        finally { _refreshingTasks = false; }
    }
    private void OnDefaultKindChanged(object? sender, EventArgs e)
    {
        if (_refreshingTasks || !Enum.TryParse<MemoKind>(DefaultKindSelector.SelectedKey, out var kind)) return;
        _settings.DefaultMemoKind = kind; CommitChange();
    }
    private void OnDefaultStatusChanged(object? sender, EventArgs e)
    {
        if (_refreshingTasks || !IsPageActive || !DefaultStatusSelector.IsVisible || _taskService is null) return;
        if (_taskService.Catalog.Find(DefaultStatusSelector.NewStatusId) is not { IsHidden: false, IsClosed: false } status) return;
        if (_settings.DefaultTaskStatusId == status.Id) return;
        _settings.DefaultTaskStatusId = status.Id;
        CommitChange();
    }
    private async void OnManageStatusesClick(object sender, RoutedEventArgs e) => await ShowTaskStatusManagerAsync();

    internal async Task ShowTaskStatusManagerAsync()
    {
        if (_taskService is null || !IsPageActive || _subpageNavigating || IsTaskStatusManagerVisible) return;
        _subpageNavigating = true;
        try
        {
            int generation = _pageGeneration;
            await WaitForPendingSaveAsync();
            if (!IsCurrentPage(generation)) return;
            SaveCategoryPosition();
            CancelPageOperations();
            EnsurePageCancellation();
            _taskStatusManager ??= new TaskStatusManagerView(_taskService) { ReturnRequested = ReturnFromTaskStatusManagerAsync };
            TaskStatusManagerHost.Content = _taskStatusManager;
            _subpageTransition.CaptureCurrentPage();
            IsTaskStatusManagerVisible = true;
            UpdateTaskStatusPageVisibility();
            _taskStatusManager.Activate();
            SettingsSurface.UpdateLayout();
            _subpageTransition.Start(forward: true);
            ReturnButton.Focus();
        }
        finally { _subpageNavigating = false; }
    }

    internal async Task ReturnFromTaskStatusManagerAsync()
    {
        if (!IsPageActive || _subpageNavigating || !IsTaskStatusManagerVisible || _taskStatusManager is null) return;
        _subpageNavigating = true;
        try
        {
            int generation = _pageGeneration;
            await _taskStatusManager.DeactivateAsync();
            if (!IsCurrentPage(generation)) return;
            _subpageTransition.CaptureCurrentPage();
            IsTaskStatusManagerVisible = false;
            UpdateTaskStatusPageVisibility();
            RestoreCategoryPosition();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (!IsCurrentPage(generation)) return;
            SettingsSurface.UpdateLayout();
            _subpageTransition.Start(forward: false);
            ManageStatusesButton.Focus();
        }
        finally { _subpageNavigating = false; }
    }

    private void UpdateTaskStatusPageVisibility()
    {
        SettingsBody.Visibility = IsTaskStatusManagerVisible ? Visibility.Hidden : Visibility.Visible;
        SettingsBody.IsEnabled = !IsTaskStatusManagerVisible && !_leaving;
        SettingsTitle.Visibility = IsTaskStatusManagerVisible ? Visibility.Collapsed : Visibility.Visible;
        TaskStatusManagerTitle.Visibility = TaskStatusManagerHost.Visibility = IsTaskStatusManagerVisible ? Visibility.Visible : Visibility.Collapsed;
    }
}
