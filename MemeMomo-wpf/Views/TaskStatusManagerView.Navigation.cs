using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MemeMomo.Services;
using MemeMomo.UI;
using MemeMomo.UI.Notifications;

namespace MemeMomo.Views;

public partial class TaskStatusManagerView
{
    private bool _active;
    private bool _disposed;
    private int _generation;
    private Task _pendingOperation = Task.CompletedTask;
    private Task<int>? _confirmation;
    private double _scrollOffset;
    private double _listOffset;
    private DispatcherOperation? _positionRestore;

    internal Func<Task>? ReturnRequested { get; set; }
    private bool CanInteract => _active && !_disposed && !_busy && _confirmation is null && IsVisible;
    private ScrollViewer? ListScrollViewer => StatusList.Template.FindName("StatusListScrollViewer", StatusList) as ScrollViewer;

    internal void Activate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _active = true;
        RestorePosition();
    }

    internal async Task DeactivateAsync()
    {
        if (_active) SavePosition();
        _active = false;
        CancelTransientOperations();
        await _pendingOperation;
    }

    private async Task<bool> ConfirmMigrationAsync(string impact)
    {
        int generation = _generation;
        Task<int> prompt = WindowNotifications.ChooseAsync(this,
            LocalizationService.Get("确认状态迁移") + "\n" + impact,
            LocalizationService.Get("取消"), LocalizationService.Get("确定"));
        _confirmation = prompt;
        try
        {
            return await prompt == 1 && _active && !_disposed && IsVisible && generation == _generation;
        }
        finally
        {
            if (ReferenceEquals(_confirmation, prompt)) _confirmation = null;
        }
    }

    private void OnPresentationChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_active || _disposed) return;
        if (IsVisible && IsHitTestVisible) RestorePosition();
        else
        {
            SavePosition();
            CancelTransientOperations();
        }
    }

    private void SavePosition()
    {
        // A queued restore still owns the saved offsets until the page is laid out.
        if (_positionRestore is { Status: DispatcherOperationStatus.Pending }) return;
        _scrollOffset = ManagerScrollViewer.VerticalOffset;
        _listOffset = ListScrollViewer?.VerticalOffset ?? 0;
    }

    private void RestorePosition()
    {
        _positionRestore?.Abort();
        _positionRestore = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _positionRestore = null;
            if (!_active || _disposed || !IsVisible) return;
            ManagerScrollViewer.ScrollToVerticalOffset(_scrollOffset);
            ListScrollViewer?.ScrollToVerticalOffset(_listOffset);
        }));
    }

    private void CancelTransientOperations()
    {
        ++_generation;
        _positionRestore?.Abort();
        _positionRestore = null;
        CloseChoiceMenus();
        if (_confirmation is { } prompt) WindowNotifications.FindHost(this)?.CancelPrompt(prompt);
        _confirmation = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        CancelTransientOperations();
        _disposed = true;
        _active = false;
        _feedback?.Clear();
        IsVisibleChanged -= OnPresentationChanged;
        IsHitTestVisibleChanged -= OnPresentationChanged;
        _service.Catalog.Changed -= RefreshList;
        _service.MemosChanged -= RefreshList;
        LocalizationService.LanguageChanged -= OnAppearanceChanged;
        ThemePreferences.PaletteChanged -= OnAppearanceChanged;
    }
}
