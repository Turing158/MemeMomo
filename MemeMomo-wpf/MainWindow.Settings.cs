using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MemeMomo.Components;
using MemeMomo.Infrastructure;
using MemeMomo.Models;
using MemeMomo.Services;
using MemeMomo.UI;
using MemeMomo.UI.Notifications;
using MemeMomo.Views;
using Point = System.Windows.Point;

namespace MemeMomo;

public partial class MainWindow
{
    private SettingsView? _settingsView;
    private bool _settingsNavigating;
    private bool _settingsCloseApproved;
    private Task? _settingsCloseTask;
    private Task<bool>? _settingsReturnTask;
    private IInputElement? _settingsReturnFocus;
    private MarkdownEditor.ViewPosition _settingsEditorPosition;
    private long _settingsEditorSession;
    private double _settingsListOffset;
    private Guid? _settingsListAnchor;
    private double _settingsListAnchorTop;
    private int _settingsNavigationGeneration;

    public bool IsSettingsPageVisible { get; private set; }
    internal SettingsView? SettingsPage => _settingsView;
    internal bool IsCapturingSettingsHotkey => IsSettingsPageVisible && _settingsView?.IsCapturingHotkey == true;

    internal SettingsView EnsureSettingsPage(ISettingsRuntimeCoordinator? coordinator = null,
        Action<Uri>? launchProjectUri = null, Func<CancellationToken, Task<GitHubRelease>>? getLatestReleaseAsync = null)
    {
        if (_settingsView is not null) return _settingsView;
        coordinator ??= new DelegateSettingsRuntimeCoordinator(snapshot =>
        {
            var effective = MergeSettingsWithRuntime(snapshot);
            ApplySettings(effective);
            ThemePreferences.ApplyMode(effective.ThemeMode);
            MotionPreferences.ApplyMode(effective.MotionMode);
        }, _ => Task.CompletedTask, reportSaveFailure: (owner, error) => WindowNotifications.Show(owner,
            LocalizationService.Format("无法保存设置：{0}", error.Message), NotificationKind.Error));
        _settingsView = new SettingsView(this, _settings, coordinator, launchProjectUri, getLatestReleaseAsync);
        _settingsView.ConfigureTaskService(_viewModel.Tasks);
        _settingsView.ReturnRequested = async () => await ReturnFromSettingsAsync();
        SettingsHost.Content = _settingsView;
        return _settingsView;
    }

    internal AppSettings MergeSettingsWithRuntime(AppSettings requested)
    {
        var effective = requested.Clone();
        _settings.CopyMainWindowRuntimeStateTo(effective);
        CopyRuntimeWindowStateTo(effective);
        // These two fields are preferences currently being edited, even though
        // the window also records their live values in runtime snapshots.
        effective.MainWindowDockSize = Math.Clamp(requested.MainWindowDockSize,
            AppSettings.MinimumMainWindowDockSize, AppSettings.MaximumMainWindowDockSize);
        effective.MainWindowDockEnabled = requested.MainWindowDockEnabled;
        if (!effective.MainWindowDockEnabled) effective.MainWindowDocked = false;
        return effective;
    }

    internal async Task<bool> ShowSettingsAsync(ISettingsRuntimeCoordinator? coordinator = null)
    {
        if (_disposed || _settingsNavigating || _modeChanging || _recycleActionPending) return false;
        if (IsSettingsPageVisible) { ShowExpandedWithTransition(); return true; }
        _settingsNavigating = true;
        int generation = ++_settingsNavigationGeneration;
        try
        {
            ShowExpandedWithTransition();
            if (!await WaitForSettingsHostAsync()) return false;
            _settingsReturnFocus = Keyboard.FocusedElement;
            CloseModeMenus();
            MarkdownEditor.ClosePageMenus();
            _dragManager?.Detach();
            StopDeleteAnimation();
            _responsiveLayout.SetWorkspaceActive(false);
            // Transfer focus while the original editor is still visible and writable,
            // allowing IME to commit before the page stops accepting input.
            SettingsButton.Focus();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            if (_disposed) return false;
            _settingsEditorSession = MarkdownEditor.SessionVersion;
            _settingsEditorPosition = MarkdownEditor.CaptureViewPosition();
            _settingsListOffset = MemoScrollViewer.VerticalOffset;
            var anchor = FindVisualChildren<Border>(MemoList).FirstOrDefault(card => card.Name == "MemoCard"
                && card.TranslatePoint(new Point(0, card.ActualHeight), MemoScrollViewer).Y > 0);
            _settingsListAnchor = (anchor?.DataContext as MemoItem)?.Id;
            _settingsListAnchorTop = anchor?.TranslatePoint(new Point(), MemoScrollViewer).Y ?? 0;
            SettingsView view = EnsureSettingsPage(coordinator);
            _pageTransition.CaptureCurrentPage();
            IsSettingsPageVisible = true;
            RefreshFilterBadge();
            InteractionState.SetIsPinActive(SettingsButton, true);
            PageContent.IsEnabled = PageContent.IsHitTestVisible = false;
            PageContent.Visibility = Visibility.Hidden;
            SettingsHost.Visibility = Visibility.Visible;
            view.Activate(_settings);
            PageSurface.UpdateLayout();
            _pageTransition.Start(forward: true);
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!_disposed && generation == _settingsNavigationGeneration && IsSettingsPageVisible && view.IsVisible && ExpandedSurface.IsHitTestVisible)
                    ((UIElement)view.FindName("ReturnButton")).Focus();
            }));
            return true;
        }
        finally
        {
            _settingsNavigating = false;
            if (!IsSettingsPageVisible && !_disposed)
            {
                _responsiveLayout.SetWorkspaceActive(true);
                if (!IsRecycleBinMode) _dragManager?.Attach();
            }
        }
    }

    private async Task<bool> WaitForSettingsHostAsync()
    {
        bool Ready() => IsVisible && DockState == DockState.Expanded && ExpandedSurface.IsHitTestVisible;
        if (Ready()) return true;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible) completion.TrySetResult(false);
            else if (Ready()) completion.TrySetResult(true);
        }
        void ClosedBeforeReady(object? sender, EventArgs e) => completion.TrySetResult(false);
        IsVisibleChanged += Changed;
        ExpandedSurface.IsHitTestVisibleChanged += Changed;
        Closed += ClosedBeforeReady;
        try { return await completion.Task; }
        finally
        {
            IsVisibleChanged -= Changed;
            ExpandedSurface.IsHitTestVisibleChanged -= Changed;
            Closed -= ClosedBeforeReady;
        }
    }

    internal Task<bool> ReturnFromSettingsAsync()
    {
        if (_settingsReturnTask is { IsCompleted: false }) return _settingsReturnTask;
        return _settingsReturnTask = ReturnFromSettingsCoreAsync();
    }

    private async Task<bool> ReturnFromSettingsCoreAsync()
    {
        if (_disposed || _settingsNavigating) return false;
        if (!IsSettingsPageVisible || _settingsView is null) return true;
        _settingsNavigating = true;
        int generation = ++_settingsNavigationGeneration;
        try
        {
            await _settingsView.DeactivateAsync();
            if (_disposed) return false;
            _pageTransition.CaptureCurrentPage();
            IsSettingsPageVisible = false;
            RefreshFilterBadge();
            InteractionState.SetIsPinActive(SettingsButton, false);
            SettingsHost.Visibility = Visibility.Collapsed;
            PageContent.Visibility = Visibility.Visible;
            PageContent.IsEnabled = PageContent.IsHitTestVisible = true;
            _responsiveLayout.SetWorkspaceActive(true);
            PageSurface.UpdateLayout();
            if (MarkdownEditor.SessionVersion == _settingsEditorSession)
                MarkdownEditor.RestoreViewPosition(_settingsEditorPosition);
            var anchor = _viewModel.Memos.Concat(_viewModel.DeletedMemos)
                .FirstOrDefault(item => item.Id == _settingsListAnchor);
            var card = anchor is null ? null : FindCard(anchor);
            double offset = card is null ? _settingsListOffset : MemoScrollViewer.VerticalOffset
                + card.TranslatePoint(new Point(), MemoScrollViewer).Y - _settingsListAnchorTop;
            MemoScrollViewer.ScrollToVerticalOffset(Math.Clamp(offset, 0, MemoScrollViewer.ScrollableHeight));
            _pageTransition.Start(forward: false);
            if (!IsRecycleBinMode) _dragManager?.Attach();
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (_disposed || generation != _settingsNavigationGeneration || IsSettingsPageVisible || !PageContent.IsVisible) return;
                if (_settingsReturnFocus is UIElement { IsVisible: true, IsEnabled: true } focus) Keyboard.Focus(focus);
                else MarkdownEditor.Adapter.FocusEditor();
            }));
            return true;
        }
        finally { _settingsNavigating = false; }
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (IsCapturingSettingsHotkey)
        {
            _settingsView!.HandlePreviewKeyDown(e);
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private void OnWindowPreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (IsSettingsPageVisible) _settingsView?.HandlePreviewKeyUp(e);
    }

    internal async Task FlushSettingsForExitAsync()
    {
        if (_settingsView is not null) await _settingsView.DeactivateAsync();
        _settingsCloseApproved = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_settingsView is not null && !_settingsCloseApproved)
        {
            e.Cancel = true;
            _settingsCloseTask ??= FlushSettingsAndCloseAsync();
            return;
        }
        base.OnClosing(e);
    }

    private async Task FlushSettingsAndCloseAsync()
    {
        await FlushSettingsForExitAsync();
        // Avoid reentering native Close if the last save completed synchronously.
        await Dispatcher.InvokeAsync(CloseWithTransition);
    }
}
