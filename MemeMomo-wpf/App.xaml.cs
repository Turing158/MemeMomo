using System.Windows;
using System.Windows.Threading;
using MemeMomo.Platform.Windows;
using MemeMomo.Infrastructure;
using MemeMomo.UI;
using MemeMomo.UI.Animation;
using MemeMomo.UI.Resources;
using MemeMomo.UI.Windows;
using MemeMomo.Models;
using MemeMomo.Services;
using MemeMomo.ViewModels;
using MemeMomo.Views;
using MemeMomo.Components.Dialogs;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;
using WpfSize = System.Windows.Size;

namespace MemeMomo;

public partial class App : System.Windows.Application
{
    private Window? _mainWindow;
    private MainWindow? _typedMainWindow;
    private readonly List<MemoPopoutWindow> _memoPopouts = [];
    private readonly Dictionary<Window, FrameAnimation> _positionAnimations = [];
    private readonly MonitorService _monitorService = new();
    private readonly JsonSettingsStorage _settingsStorage = new();
    private AppSettings _settings = AppSettings.CreateDefault();
    private MemoPopoutWindow? _latestMemoPopout;
    private Window? _latestMemoWindow;
    private readonly List<TutorialWindow> _tutorialWindows = [];
    private TrayMenuWindow? _trayMenu;
    private WindowsTrayIcon? _trayIcon;
    private GlobalHotkeyService? _hotkeyService;
    private readonly ReminderToastQueue _toastQueue = new();
    private DispatcherTimer? _reminderTimer;
    private string? _lastClipboardText;
    private MemeMomo.UI.Notifications.NotificationFeedback? _settingsSaveFeedback;
    private int _exitRequested;

    internal IReadOnlyList<MemoPopoutWindow> MemoPopouts => _memoPopouts;
    internal MemoPopoutWindow? LatestMemoPopout => _latestMemoPopout;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        VisualFoundationResources.EnsureLoaded(this);
        ThemePreferences.Initialize(this);
        MotionPreferences.Initialize(this);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        StartMainWindowAsync().Observe();
    }

    private async Task StartMainWindowAsync()
    {
        try
        {
            AppSettings loaded = await _settingsStorage.LoadAsync();
            loaded.CopyTo(_settings);
            LocalizationService.SetLanguage(_settings.Language);
            TaskStatusCatalog catalog = new();
            await catalog.LoadAsync();
            catalog.RepairDefault(_settings);
            MainViewModel viewModel = new(new JsonMemoStorage(), catalog, _settings, () => _settingsStorage.TrySaveAsync(_settings));
            await viewModel.LoadAsync();
            MainWindow mainWindow = new(viewModel, _settings, new AppHostActions(this));
            ConfigureMainWindow(mainWindow, _settings);
            mainWindow.ConfigureWindowStatePersistence(SaveWindowStateAsync);
            if (Volatile.Read(ref _exitRequested) != 0)
            {
                return;
            }

            LocalizationService.SetLanguage(_settings.Language);
            ThemePreferences.ApplyMode(_settings.ThemeMode);
            MotionPreferences.ApplyMode(_settings.MotionMode);
            mainWindow.ApplySettings(_settings);
            mainWindow.InitializeFromSettings(_settings);
            InitializeSystemIntegrations(mainWindow);

            mainWindow.Show();
            StartReminderTimer();
            string[] diagnostics = catalog.Diagnostics.Concat(new[] { _settingsStorage.LoadDiagnostic, viewModel.LoadDiagnostic }
                .Where(message => message is not null).Select(message => message!)).ToArray();
            if (diagnostics.Length > 0)
                new ConfirmDialog(LocalizationService.Get("数据恢复提示"), LocalizationService.Get("已保留原始文件。请检查备份与状态引用；无法恢复的数据文件将暂停保存。") + "\n" + string.Join("\n", diagnostics)).ShowDialog(mainWindow);
        }
        catch (Exception exception)
        {
            UiExceptionReporter.Report(exception);
            ExitApplication(2);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        CancelPositionAnimations();
        DisposeSystemIntegrations();
        StopReminderTimer();
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        MotionPreferences.Shutdown();
        ThemePreferences.Shutdown();
        base.OnExit(e);
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (_typedMainWindow is not null)
        {
            _typedMainWindow.MemoPopoutRequested -= OnMemoPopoutRequested;
            _typedMainWindow.ViewModel.MemoDeleted -= OnMemoDeleted;
            _typedMainWindow.ViewModel.MemosLoaded -= OnMemosLoaded;
            _typedMainWindow.ViewModel.Tasks.ReminderCanceled -= OnTaskReminderCanceled;
            _typedMainWindow.Activated -= OnMainWindowActivated;
            _typedMainWindow.DockContextMenuRequested -= ShowSharedTrayMenu;
            _typedMainWindow = null;
        }

        if (_mainWindow is not null)
        {
            _mainWindow.Closed -= OnMainWindowClosed;
            _mainWindow = null;
        }

        ExitApplication();
    }

    private void ConfigureMainWindow(MainWindow mainWindow, AppSettings settings)
    {
        _typedMainWindow = mainWindow;
        _mainWindow = mainWindow;
        _settings = settings;
        _latestMemoWindow = mainWindow;
        mainWindow.MemoPopoutRequested += OnMemoPopoutRequested;
        mainWindow.ViewModel.MemoDeleted += OnMemoDeleted;
        mainWindow.ViewModel.MemosLoaded += OnMemosLoaded;
        mainWindow.ViewModel.Tasks.ReminderCanceled += OnTaskReminderCanceled;
        mainWindow.ViewModel.Tasks.HasPendingNotification = id => _toastQueue.ActiveMemoId == id || _toastQueue.QueuedMemoIds.Contains(id);
        _toastQueue.DeliveryGuard = id => mainWindow.ViewModel.Memos.FirstOrDefault(item => item.Id == id) is { } memo
            ? memo.IsTaskCommitPending || memo.IsLifecyclePending ? null : memo.CanRemind : false;
        mainWindow.Activated += OnMainWindowActivated;
        mainWindow.Closed += OnMainWindowClosed;
        mainWindow.DockContextMenuRequested += ShowSharedTrayMenu;
    }

    private void OnMainWindowActivated(object? sender, EventArgs e)
    {
        if (_typedMainWindow is not null)
        {
            _latestMemoWindow = _typedMainWindow;
        }
    }

    private void OnMemoPopoutRequested(MemoItem memo, WpfPoint pointerPixels)
    {
        OpenMemoPopout(memo, ClampPopoutPosition(pointerPixels), alwaysReuse: false);
    }

    internal MemoPopoutWindow? OpenMemoPopoutForTest(MemoItem memo, WpfPoint pointerPixels, bool alwaysReuse = false)
    {
        WpfPoint position = ClampPopoutPosition(pointerPixels);
        return OpenMemoPopout(memo, position, alwaysReuse);
    }

    private MemoPopoutWindow? OpenMemoPopout(MemoItem memo, WpfPoint position, bool alwaysReuse, bool popOutFromDock = false)
    {
        ArgumentNullException.ThrowIfNull(memo);
        if (memo.IsDeleted || memo.IsLifecyclePending) return null;
        if (_typedMainWindow is null)
        {
            return null;
        }

        if (alwaysReuse || !_settings.DuplicateMemoEnabled)
        {
            MemoPopoutWindow? existing = _memoPopouts.FirstOrDefault(popout => popout.Memo.Id == memo.Id);
            if (existing is not null)
            {
                if (!existing.IsVisible)
                {
                    existing.Show();
                }
                if (existing.WindowState == WindowState.Minimized)
                {
                    existing.WindowState = WindowState.Normal;
                }
                if (existing.IsEdgeDocked)
                {
                    if (popOutFromDock && existing.PopOutFromDock())
                    {
                        // 显式查看入口（点击系统通知）：贴边便签自动脱离贴边，弹出完整窗口。
                        SetLatestMemoPopout(existing);
                        existing.Activate();
                        return existing;
                    }

                    // 贴边中的便签保持原位：不移动、不抢焦点，只刷新引用。
                    SetLatestMemoPopout(existing);
                    return existing;
                }

                AnimateWindowPosition(existing, position);
                SetLatestMemoPopout(existing);
                existing.Activate();
                return existing;
            }
        }

        MemoPopoutWindow popout = new(
            memo,
            position,
            (item, content) => _typedMainWindow.ViewModel.UpdateItemAndSaveAsync(item.Id, content));
        return RegisterMemoPopout(popout);
    }

    private MemoPopoutWindow RegisterMemoPopout(MemoPopoutWindow popout)
    {
        popout.ApplySettings(_settings);
        popout.ConfigureTaskService(_typedMainWindow!.ViewModel.Tasks);
        popout.ConfigureDockPopLengths(LoadPopoutDockPopLength, SavePopoutDockPopLength);
        popout.ConfigureReminderSaving(SaveReminderAsync);
        popout.SetApplicationExitPending(_exitPromptPending);
        popout.Activated += OnPopoutActivated;
        popout.Closed += OnPopoutClosed;
        popout.Memo.PopoutRefCount++;
        _memoPopouts.Add(popout);
        SetLatestMemoPopout(popout);
        popout.Show();
        popout.Activate();
        return popout;
    }

    internal MemoPopoutWindow? OpenMemoPopoutCentered(MemoItem memo, bool popOutFromDock = false)
    {
        if (_typedMainWindow is null)
        {
            return null;
        }

        PixelMonitorInfo monitor = _monitorService.FromWindow(_typedMainWindow);
        WpfRect placement = MemoWindowPlacement.CenterPixels(
            new WpfSize(MemoWindowPlacement.DefaultWidthDip, MemoWindowPlacement.DefaultHeightDip),
            monitor);
        return OpenMemoPopout(
            memo,
            new WpfPoint(placement.Left, placement.Top),
            alwaysReuse: true,
            popOutFromDock);
    }

    private async Task SaveReminderAsync(MemoItem memo, DateTime? reminderAt)
    {
        if (_typedMainWindow is null)
        {
            throw new InvalidOperationException(LocalizationService.Get("提醒服务尚未就绪。"));
        }

        (await _typedMainWindow.ViewModel.Tasks.SetReminderAsync(memo.Id, reminderAt)).ThrowIfFailed();
    }

    private void StartReminderTimer()
    {
        if (_reminderTimer is not null)
        {
            return;
        }

        _reminderTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _reminderTimer.Tick += OnReminderTimerTick;
        _reminderTimer.Start();
    }

    private void StopReminderTimer()
    {
        DispatcherTimer? timer = _reminderTimer;
        _reminderTimer = null;
        if (timer is null)
        {
            return;
        }

        timer.Stop();
        timer.Tick -= OnReminderTimerTick;
    }

    private void OnReminderTimerTick(object? sender, EventArgs e)
    {
        CheckDueReminders();
        _toastQueue.Pump(Utils.DateTimeUtils.Now);
    }

    private void OnMemosLoaded() { if (_reminderTimer is not null) CheckDueReminders(); }

    internal void CheckDueReminders()
    {
        if (Volatile.Read(ref _exitRequested) != 0 || _typedMainWindow?.ViewModel.IsLoaded != true)
        {
            return;
        }

        MemoItem[] dueMemos = TakeDueReminders(_typedMainWindow.ViewModel.Memos, Utils.DateTimeUtils.Now);
        if (dueMemos.Length == 0)
        {
            return;
        }

        foreach (MemoItem memo in dueMemos)
        {
            switch (_settings.ReminderNotification)
            {
                case ReminderNotificationMode.InAppWindow:
                    _ = OpenMemoPopoutCentered(memo);
                    break;
                case ReminderNotificationMode.Both:
                    _ = OpenMemoPopoutCentered(memo);
                    _toastQueue.Enqueue(memo.Id, memo.Title, memo.Subtitle);
                    break;
                default:
                    // 队列溢出（32 条上限）时该条改走应用内弹窗，不静默丢弃提醒。
                    if (!_toastQueue.Enqueue(memo.Id, memo.Title, memo.Subtitle))
                    {
                        _ = OpenMemoPopoutCentered(memo);
                    }

                    break;
            }
        }

        _ = _typedMainWindow.ViewModel.SaveAsync();
    }

    private void OnBalloonClicked()
    {
        if (Dispatcher.CheckAccess())
        {
            OpenMemoFromBalloon();
        }
        else
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(OpenMemoFromBalloon));
        }
    }

    private void OnBalloonDismissed()
    {
        if (Dispatcher.CheckAccess())
        {
            _toastQueue.NotifyDismissed();
        }
        else
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(_toastQueue.NotifyDismissed));
        }
    }

    private void OpenMemoFromBalloon()
    {
        // 通知中心历史条目的点击可能不回传或迟于看门狗结束（memoId 为 null），
        // 且对应便签可能已被删除；两种情况都退化为恢复主窗口。
        Guid? memoId = _toastQueue.NotifyClicked();
        MemoItem? memo = memoId is { } id
            ? _typedMainWindow?.ViewModel.Memos.FirstOrDefault(item => item.Id == id)
            : null;
        if (memo?.CanRemind == true)
        {
            _ = OpenMemoPopoutCentered(memo, popOutFromDock: true);
            return;
        }

        if (_typedMainWindow is not null)
        {
            RestoreWindow(_typedMainWindow);
        }
    }

    private void OnPopoutActivated(object? sender, EventArgs e)
    {
        if (sender is MemoPopoutWindow popout)
        {
            SetLatestMemoPopout(popout);
        }
    }

    private void OnPopoutClosed(object? sender, EventArgs e)
    {
        if (sender is not MemoPopoutWindow popout)
        {
            return;
        }

        popout.Activated -= OnPopoutActivated;
        popout.Closed -= OnPopoutClosed;
        if (_positionAnimations.Remove(popout, out FrameAnimation? animation))
        {
            animation.Dispose();
        }

        if (_memoPopouts.Remove(popout))
        {
            popout.Memo.PopoutRefCount = Math.Max(0, popout.Memo.PopoutRefCount - 1);
        }

        if (ReferenceEquals(_latestMemoPopout, popout))
        {
            _latestMemoPopout = _memoPopouts.LastOrDefault(window => window.IsVisible);
        }

        if (ReferenceEquals(_latestMemoWindow, popout))
        {
            _latestMemoWindow = (Window?)_memoPopouts.LastOrDefault(window => window.IsVisible) ?? _typedMainWindow;
        }

        // A duplicate popout may have been waiting for this window's edit
        // lease. Re-attempt ownership after the closing lease is gone so the
        // remaining visible window becomes fully editable.
        foreach (MemoPopoutWindow other in _memoPopouts
            .Where(window => window.Memo.Id == popout.Memo.Id && window.IsVisible)
            .ToArray())
        {
            other.EnsureEditorOwnershipAsync().Observe();
        }
    }

    private void SetLatestMemoPopout(MemoPopoutWindow popout)
    {
        _latestMemoPopout = popout;
        _latestMemoWindow = popout;
    }

    private void OnMemoDeleted(Guid memoId)
    {
        _toastQueue.Cancel(memoId);
        foreach (MemoPopoutWindow popout in _memoPopouts.Where(window => window.Memo.Id == memoId).ToArray())
        {
            popout.CloseBecauseSourceDeleted();
        }

    }

    private void OnTaskReminderCanceled(Guid memoId)
    {
        _toastQueue.Cancel(memoId);
        foreach (MemoPopoutWindow popout in _memoPopouts.Where(window => window.Memo.Id == memoId).ToArray())
            popout.InvalidateReminderForTask();
    }

    internal static MemoItem[] TakeDueReminders(IEnumerable<MemoItem> memos, DateTime now)
    {
        MemoItem[] dueMemos = memos
            .Where(item => item.CanRemind && item.ReminderAt is { } reminderAt && reminderAt <= now)
            .OrderBy(item => item.ReminderAt)
            .ToArray();
        foreach (MemoItem memo in dueMemos)
        {
            memo.ReminderAt = null;
        }

        return dueMemos;
    }

    private void InitializeSystemIntegrations(MainWindow mainWindow)
    {
        _trayIcon = new WindowsTrayIcon(
            () => Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ShowSharedTrayMenu)),
            () => Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => RestoreWindow(mainWindow))))
        {
            TraySingleClickToShow = _settings.TraySingleClickToShow
        };
        _trayIcon.BalloonClicked += OnBalloonClicked;
        _trayIcon.BalloonDismissed += OnBalloonDismissed;
        _toastQueue.Sink = _trayIcon;

        _hotkeyService = new GlobalHotkeyService();
        _hotkeyService.RestoreRequested += OnRestoreRequested;
        ApplySystemSettings(mainWindow);
    }

    private void ApplySystemSettings(MainWindow mainWindow)
    {
        _hotkeyService?.Apply(
            _settings,
            mainWindow,
            ToggleLatestTopmostTarget,
            ToggleLatestMemoTaskbarTarget,
            AddQuickMemoFromClipboard,
            OpenQuickAdd);
        if (_trayIcon is not null)
        {
            _trayIcon.TraySingleClickToShow = _settings.TraySingleClickToShow;
        }
    }

    private void DisposeSystemIntegrations()
    {
        GlobalHotkeyService? hotkeys = _hotkeyService;
        _hotkeyService = null;
        if (hotkeys is not null)
        {
            hotkeys.RestoreRequested -= OnRestoreRequested;
            hotkeys.Dispose();
        }

        WindowsTrayIcon? trayIcon = _trayIcon;
        _trayIcon = null;
        if (trayIcon is not null)
        {
            trayIcon.BalloonClicked -= OnBalloonClicked;
            trayIcon.BalloonDismissed -= OnBalloonDismissed;
            trayIcon.Dispose();
        }

        _toastQueue.Sink = null;
        _toastQueue.Clear();
    }

    private void OnRestoreRequested()
    {
        if (_typedMainWindow is null || Volatile.Read(ref _exitRequested) != 0)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            RestoreWindow(_typedMainWindow);
        }
        else
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                new Action(() =>
                {
                    if (_typedMainWindow is not null)
                    {
                        RestoreWindow(_typedMainWindow);
                    }
                }));
        }
    }

    internal static void RestoreWindow(Window mainWindow)
    {
        ArgumentNullException.ThrowIfNull(mainWindow);
        if (mainWindow is MainWindow window)
        {
            window.ShowExpandedWithTransition();
            return;
        }

        if (!mainWindow.IsVisible) mainWindow.Show();
        if (mainWindow.WindowState == WindowState.Minimized) mainWindow.WindowState = WindowState.Normal;
        TaskbarIconVisibility.SetVisible(mainWindow, false);
        mainWindow.Activate();
    }

    internal static void HideWindow(Window mainWindow)
    {
        ArgumentNullException.ThrowIfNull(mainWindow);
        if (mainWindow is MainWindow window)
        {
            window.HideToTrayWithTransition();
            return;
        }

        mainWindow.WindowState = WindowState.Minimized;
        TaskbarIconVisibility.SetVisible(mainWindow, false);
    }

    private void ToggleLatestTopmostTarget()
    {
        // Hidden popouts still own reminder sessions and must remain registered for exit/deletion.
        MemoPopoutWindow[] visiblePopouts = _memoPopouts.Where(window => window.IsVisible).ToArray();
        _tutorialWindows.RemoveAll(window => !window.IsVisible);
        if (_latestMemoWindow is MemoPopoutWindow latestPopout && !visiblePopouts.Contains(latestPopout))
        {
            _latestMemoWindow = (Window?)visiblePopouts.LastOrDefault()
                ?? (Window?)_tutorialWindows.LastOrDefault()
                ?? _typedMainWindow;
        }
        else if (_latestMemoWindow is TutorialWindow latestTutorial && !_tutorialWindows.Contains(latestTutorial))
        {
            _latestMemoWindow = (Window?)_tutorialWindows.LastOrDefault()
                ?? (Window?)visiblePopouts.LastOrDefault()
                ?? _typedMainWindow;
        }

        switch (_latestMemoWindow)
        {
            case MainWindow mainWindow:
                mainWindow.TogglePinned();
                return;
            case MemoPopoutWindow popout when visiblePopouts.Contains(popout):
                popout.TogglePinned();
                return;
            case TutorialWindow tutorial when _tutorialWindows.Contains(tutorial):
                tutorial.TogglePinned();
                return;
        }

        if (visiblePopouts.LastOrDefault() is { } fallbackPopout)
        {
            fallbackPopout.TogglePinned();
            _latestMemoWindow = fallbackPopout;
        }
        else if (_tutorialWindows.LastOrDefault() is { } fallbackTutorial)
        {
            fallbackTutorial.TogglePinned();
            _latestMemoWindow = fallbackTutorial;
        }
        else
        {
            _typedMainWindow?.TogglePinned();
        }
    }

    private void ToggleLatestMemoTaskbarTarget()
    {
        if (_latestMemoPopout is null || !_latestMemoPopout.IsVisible || !_memoPopouts.Contains(_latestMemoPopout))
        {
            _latestMemoPopout = _memoPopouts.LastOrDefault(window => window.IsVisible);
        }

        _latestMemoPopout?.ToggleTaskbarIcon();
    }

    private void AddQuickMemoFromClipboard()
    {
        if (!_settings.QuickMemoEnabled || _typedMainWindow is null)
        {
            return;
        }

        WpfPoint? cursorPosition = null;
        if (_settings.QuickMemoShowPopoutAfterAdd
            && _monitorService.TryGetCursorState(out WpfPoint pointerPixels, out _))
        {
            cursorPosition = pointerPixels;
        }

        string? text;
        try
        {
            if (!System.Windows.Clipboard.ContainsText(System.Windows.TextDataFormat.UnicodeText))
            {
                return;
            }

            text = System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.UnicodeText);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return;
        }

        AddQuickMemoText(text, cursorPosition);
    }

    internal MemoItem? AddQuickMemoTextForTest(string? text, WpfPoint? cursorPosition = null) =>
        AddQuickMemoText(text, cursorPosition);

    private MemoItem? AddQuickMemoText(string? text, WpfPoint? cursorPosition)
    {
        if (!_settings.QuickMemoEnabled || _typedMainWindow is null || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string content = text.Trim();
        if (text == _lastClipboardText)
        {
            MemoItem? duplicate = _typedMainWindow.ViewModel.FindByContent(content);
            if (duplicate is not null && cursorPosition.HasValue && _settings.QuickMemoShowPopoutAfterAdd)
            {
                ShowQuickMemoPopout(duplicate, cursorPosition.Value);
            }

            return duplicate;
        }

        _lastClipboardText = text;
        MemoItem? memo = _typedMainWindow.ViewModel.AddOrPromoteItem(content);
        if (memo is not null && cursorPosition.HasValue && _settings.QuickMemoShowPopoutAfterAdd)
        {
            ShowQuickMemoPopout(memo, cursorPosition.Value);
        }

        return memo;
    }

    private void ShowQuickMemoPopout(MemoItem memo, WpfPoint cursorPosition)
    {
        WpfPoint target = ClampPopoutPosition(cursorPosition);
        MemoPopoutWindow? existing = _memoPopouts.FirstOrDefault(window => window.Memo.Id == memo.Id);
        if (existing is not null)
        {
            AnimateWindowPosition(existing, target);
            SetLatestMemoPopout(existing);
            existing.Activate();
            return;
        }

        _ = OpenMemoPopout(memo, target, alwaysReuse: false);
    }

    private WpfPoint ClampPopoutPosition(WpfPoint pointerPixels)
    {
        PixelMonitorInfo monitor = _monitorService.FromPoint(pointerPixels);
        WpfRect placement = MemoWindowPlacement.FromPointerPixels(
            pointerPixels,
            new WpfSize(MemoWindowPlacement.DefaultWidthDip, MemoWindowPlacement.DefaultHeightDip),
            monitor);
        return new WpfPoint(placement.Left, placement.Top);
    }

    private void ShowSharedTrayMenu()
    {
        if (_typedMainWindow is null)
        {
            return;
        }

        EnsureTrayMenu();
        _trayMenu!.ShowNearPointer(_typedMainWindow);
    }

    private void EnsureTrayMenu()
    {
        if (_trayMenu is null)
        {
            _trayMenu = new TrayMenuWindow(new AppTrayMenuActions(this));
        }
    }

    private void OpenSettings(WindowContext context)
    {
        if (_typedMainWindow is null) return;
        ISettingsRuntimeCoordinator coordinator = new DelegateSettingsRuntimeCoordinator(
            PreviewSettings, SaveSettingsAsync, OpenTutorial, ReportSettingsSaveFailure,
            active =>
            {
                _hotkeyService?.SetCaptureActive(active);
                if (!active && _typedMainWindow is not null) ApplySystemSettings(_typedMainWindow);
            });
        _typedMainWindow.ShowSettingsAsync(coordinator).Observe();
    }

    private void PreviewSettings(AppSettings snapshot)
    {
        if (_typedMainWindow is null)
        {
            return;
        }

        AppSettings effective = MergeSettingsWithRuntime(snapshot);
        _typedMainWindow.ViewModel.Tasks.Catalog.RepairDefault(effective);
        effective.CopyTo(_settings);
        ThemePreferences.ApplyMode(_settings.ThemeMode);
        MotionPreferences.ApplyMode(_settings.MotionMode);
        _typedMainWindow.ApplySettings(_settings);
        foreach (MemoPopoutWindow popout in _memoPopouts)
        {
            popout.ApplySettings(_settings);
        }
        ApplySystemSettings(_typedMainWindow);
    }

    private async Task SaveSettingsAsync(AppSettings snapshot)
    {
        if (_typedMainWindow is null)
        {
            return;
        }

        // Serial disk writes must not preview an older queued preference over a
        // newer live edit. Only runtime geometry is merged at the time of writing.
        AppSettings effective = MergeSettingsWithRuntime(snapshot);
        _typedMainWindow.ViewModel.Tasks.Catalog.RepairDefault(effective);
        await _settingsStorage.SaveAsync(effective);
    }

    private async Task SaveWindowStateAsync(AppSettings snapshot)
    {
        snapshot.CopyMainWindowRuntimeStateTo(_settings);
        await _settingsStorage.SaveAsync(_settings.Clone());
    }

    private double LoadPopoutDockPopLength(Guid memoId)
    {
        return _settings.PopoutDock.GetPopLength(memoId);
    }

    private void SavePopoutDockPopLength(Guid memoId, double popLength)
    {
        _settings.PopoutDock.PopLengths[memoId.ToString()] = popLength;
        _ = SaveSettingsFileAsync();
    }

    private async Task SaveSettingsFileAsync()
    {
        await _settingsStorage.SaveAsync(_settings.Clone());
    }

    private async Task<CloseButtonAction?> AskCloseButtonActionAsync(WindowContext context)
    {
        if (_typedMainWindow is null)
        {
            return null;
        }

        Window owner = context.Window as Window ?? _typedMainWindow;
        int choice = await MemeMomo.UI.Notifications.WindowNotifications.ChooseAsync(owner,
            LocalizationService.Get("点击右上角关闭按钮时，要最小化到托盘还是直接关闭应用？"),
            LocalizationService.Get("取消"), LocalizationService.Get("最小化托盘"), LocalizationService.Get("关闭"));
        CloseButtonAction? action = choice == 1 ? CloseButtonAction.MinimizeToTray : choice == 2 ? CloseButtonAction.Close : null;
        if (action is null)
        {
            return null;
        }

        _settings.CloseButtonAction = action.Value;
        _settings.HasAskedCloseButtonAction = true;
        _typedMainWindow.ApplySettings(_settings);
        await _settingsStorage.SaveAsync(_settings.Clone());
        return action;
    }

    private AppSettings MergeSettingsWithRuntime(AppSettings requested) => _typedMainWindow!.MergeSettingsWithRuntime(requested);

    private void ReportSettingsSaveFailure(Window owner, Exception exception)
    {
        (_settingsSaveFeedback ??= new(owner)).Show(
            LocalizationService.Format("无法保存设置：{0}", exception.Message),
            MemeMomo.UI.Notifications.NotificationKind.Error);
    }

    internal void OpenTutorial(AppSettings snapshot)
    {
        if (_typedMainWindow is null)
        {
            return;
        }

        PixelMonitorInfo monitor = _monitorService.FromWindow(_typedMainWindow);
        WpfRect placement = MemoWindowPlacement.CenterPixels(new WpfSize(380, 360), monitor);
        TutorialWindow tutorial = new(snapshot)
        {
            Left = placement.Left,
            Top = placement.Top
        };
        _tutorialWindows.Add(tutorial);
        _latestMemoWindow = tutorial;
        tutorial.Activated += (_, _) => _latestMemoWindow = tutorial;
        tutorial.Closed += (_, _) =>
        {
            _tutorialWindows.Remove(tutorial);
            if (ReferenceEquals(_latestMemoWindow, tutorial))
            {
                _latestMemoWindow = _tutorialWindows.LastOrDefault(w => w.IsVisible)
                    ?? (Window?)_memoPopouts.LastOrDefault(w => w.IsVisible)
                    ?? _typedMainWindow;
            }
        };
        tutorial.Show();
    }

    private void AnimateWindowPosition(Window window, WpfPoint target)
    {
        if (_positionAnimations.Remove(window, out FrameAnimation? previous))
        {
            previous.Dispose();
        }

        WpfPoint from = new(window.Left, window.Top);
        double dx = target.X - from.X;
        double dy = target.Y - from.Y;
        double distance = Math.Sqrt((dx * dx) + (dy * dy));
        if (distance < 10 || !MotionPreferences.AnimationsEnabled)
        {
            window.Left = target.X;
            window.Top = target.Y;
            return;
        }

        FrameAnimation? animation = null;
        animation = new FrameAnimation();
        _positionAnimations[window] = animation;
        animation.Start(
            MotionPreferences.AdaptiveDuration(distance),
            MotionEasing.CubicEaseOut,
            progress =>
            {
                if (!window.IsVisible)
                {
                    return;
                }

                window.Left = from.X + ((target.X - from.X) * progress);
                window.Top = from.Y + ((target.Y - from.Y) * progress);
            },
            () =>
            {
                window.Left = target.X;
                window.Top = target.Y;
                if (_positionAnimations.Remove(window, out FrameAnimation? current) && ReferenceEquals(current, animation))
                {
                    current.Dispose();
                }
            });
    }

    private void CancelPositionAnimations()
    {
        foreach (FrameAnimation animation in _positionAnimations.Values.ToArray())
        {
            animation.Dispose();
        }

        _positionAnimations.Clear();
    }

    private void CloseAllMemoPopouts()
    {
        CancelPositionAnimations();
        foreach (MemoPopoutWindow popout in _memoPopouts.ToArray())
        {
            popout.CloseImmediatelyForTest();
        }

        _memoPopouts.Clear();
        _latestMemoPopout = null;
    }

    private async void ExitApplication(int exitCode = 0)
    {
        if (_exitPromptPending || Volatile.Read(ref _exitRequested) != 0) return;
        _exitPromptPending = true;
        MemoPopoutWindow[] popouts = _memoPopouts.ToArray();
        bool prepared = false;
        foreach (MemoPopoutWindow popout in popouts) popout.SetApplicationExitPending(true);
        try
        {
            if (_typedMainWindow is not null) await _typedMainWindow.ViewModel.RecycleBin.WaitForIdleAsync();
            if (!await WaitForPopoutRemindersAsync(popouts)) return;
            MemoPopoutWindow? draft = _memoPopouts.FirstOrDefault(window => window.IsQuickAddDraft);
            if (draft is not null && !await draft.RequestQuickAddCloseAsync()) return;
            foreach (var popout in _memoPopouts.Where(window => !window.IsQuickAddDraft && window.IsEditorOwner).ToArray())
                if (!await popout.Editor.CompleteEditingAsync()) return;
            if (_typedMainWindow is not null && !await _typedMainWindow.SaveNormalSessionBeforeExitAsync()) return;
            if (_typedMainWindow is not null) await _typedMainWindow.FlushSettingsForExitAsync();
            prepared = true;
        }
        finally
        {
            _exitPromptPending = false;
            if (!prepared)
                foreach (MemoPopoutWindow popout in _memoPopouts.ToArray()) popout.SetApplicationExitPending(false);
        }

        if (Interlocked.Exchange(ref _exitRequested, 1) != 0)
        {
            return;
        }

        CancelPositionAnimations();
        DisposeSystemIntegrations();
        ThemePreferences.Shutdown();
        MotionPreferences.Shutdown();
        Shutdown(exitCode);
    }

    internal static async Task<bool> WaitForPopoutRemindersAsync(IEnumerable<MemoPopoutWindow> popouts)
    {
        bool success = true;
        foreach (MemoPopoutWindow popout in popouts.ToArray())
            success &= await popout.WaitForReminderSubmissionAsync();
        return success;
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        UiExceptionReporter.Report(e.Exception);
    }

    private sealed class AppHostActions : IMainWindowHostActions
    {
        private readonly App _app;

        internal AppHostActions(App app) => _app = app;

        public void OpenSettings(WindowContext context)
        {
            _app.OpenSettings(context);
        }

        public void MinimizeToTray(WindowContext context) => HideWindow((Window)context.Window);

        public void SetTaskbarIconVisible(WindowContext context, bool visible) =>
            TaskbarIconVisibility.SetVisible((Window)context.Window, visible);

        public Task<CloseButtonAction?> AskCloseButtonAction(WindowContext context) =>
            _app.AskCloseButtonActionAsync(context);

        public void ExitApplication(WindowContext context) => _app.ExitApplication();
    }

    private sealed class AppTrayMenuActions(App app) : ITrayMenuHostActions
    {
        public bool IsMainWindowPinned => app._typedMainWindow?.Topmost == true;
        public void OpenMainWindow() => app._typedMainWindow?.ShowExpandedWithTransition();
        public void CreateNewMemo() => app._typedMainWindow?.ShowExpandedWithTransition(focusInput: true);
        public void ToggleMainWindowPinned() => app._typedMainWindow?.TogglePinned();
        public void ExitApplication() => app.ExitApplication();
    }
}
