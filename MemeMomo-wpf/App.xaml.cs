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
    private readonly Dictionary<Guid, ReminderWindow> _reminderWindows = [];
    private SettingsWindow? _settingsWindow;
    private TrayMenuWindow? _trayMenu;
    private WindowsTrayIcon? _trayIcon;
    private GlobalHotkeyService? _hotkeyService;
    private readonly ReminderToastQueue _toastQueue = new();
    private DispatcherTimer? _reminderTimer;
    private string? _lastClipboardText;
    private bool _settingsFailureDialogVisible;
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
            MainViewModel viewModel = new();
            MainWindow mainWindow = new(viewModel, _settings, new AppHostActions(this));
            ConfigureMainWindow(mainWindow, _settings);
            mainWindow.ConfigureWindowStatePersistence(SaveWindowStateAsync);
            mainWindow.ShowInTaskbar = false;
            StartReminderTimer();

            AppSettings loaded = await _settingsStorage.LoadAsync();
            if (Volatile.Read(ref _exitRequested) != 0)
            {
                return;
            }

            loaded.CopyTo(_settings);
            ThemePreferences.ApplyMode(_settings.ThemeMode);
            MotionPreferences.ApplyMode(_settings.MotionMode);
            mainWindow.ApplySettings(_settings);
            mainWindow.InitializeFromSettings(_settings);
            InitializeSystemIntegrations(mainWindow);

            mainWindow.Show();
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
        popout.ApplySettings(_settings);
        popout.ConfigureDockPopLengths(LoadPopoutDockPopLength, SavePopoutDockPopLength);
        popout.ReminderRequested += OnReminderRequested;
        popout.Activated += OnPopoutActivated;
        popout.Closed += OnPopoutClosed;
        memo.PopoutRefCount++;
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

    private void OnReminderRequested(MemoPopoutWindow popout, MemoItem memo)
    {
        SetLatestMemoPopout(popout);
        OpenReminderWindow(popout, memo);
    }

    private void OpenReminderWindow(MemoPopoutWindow owner, MemoItem memo)
    {
        if (_reminderWindows.TryGetValue(memo.Id, out ReminderWindow? existing) && existing.IsVisible)
        {
            existing.Activate();
            return;
        }

        ReminderWindow window = new(memo, SaveReminderAsync)
        {
            Owner = owner,
            Topmost = owner.Topmost
        };
        _reminderWindows[memo.Id] = window;
        window.Closed += (_, _) => _reminderWindows.Remove(memo.Id);
        window.ShowDialog();
    }

    private async Task SaveReminderAsync(MemoItem memo, DateTime? reminderAt)
    {
        if (_typedMainWindow is null)
        {
            throw new InvalidOperationException("提醒服务尚未就绪。");
        }

        memo.ReminderAt = reminderAt;
        await _typedMainWindow.ViewModel.SaveAsync();
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

    private void OnMemosLoaded() => CheckDueReminders();

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
        if (memo is not null)
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

        popout.ReminderRequested -= OnReminderRequested;
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

        if (_reminderWindows.Remove(memoId, out ReminderWindow? reminder))
        {
            reminder.CloseImmediatelyForTest();
        }
    }

    internal static MemoItem[] TakeDueReminders(IEnumerable<MemoItem> memos, DateTime now)
    {
        MemoItem[] dueMemos = memos
            .Where(item => item.ReminderAt is { } reminderAt && reminderAt <= now)
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
            AddQuickMemoFromClipboard);
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
        _memoPopouts.RemoveAll(window => !window.IsVisible);
        _tutorialWindows.RemoveAll(window => !window.IsVisible);
        if (_latestMemoWindow is MemoPopoutWindow latestPopout && !_memoPopouts.Contains(latestPopout))
        {
            _latestMemoWindow = (Window?)_memoPopouts.LastOrDefault()
                ?? (Window?)_tutorialWindows.LastOrDefault()
                ?? _typedMainWindow;
        }
        else if (_latestMemoWindow is TutorialWindow latestTutorial && !_tutorialWindows.Contains(latestTutorial))
        {
            _latestMemoWindow = (Window?)_tutorialWindows.LastOrDefault()
                ?? (Window?)_memoPopouts.LastOrDefault()
                ?? _typedMainWindow;
        }

        switch (_latestMemoWindow)
        {
            case MainWindow mainWindow:
                mainWindow.TogglePinned();
                return;
            case MemoPopoutWindow popout when _memoPopouts.Contains(popout):
                popout.TogglePinned();
                return;
            case TutorialWindow tutorial when _tutorialWindows.Contains(tutorial):
                tutorial.TogglePinned();
                return;
        }

        if (_memoPopouts.LastOrDefault() is { } fallbackPopout)
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
        _memoPopouts.RemoveAll(window => !window.IsVisible);
        if (_latestMemoPopout is null || !_memoPopouts.Contains(_latestMemoPopout))
        {
            _latestMemoPopout = _memoPopouts.LastOrDefault();
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
        if (_typedMainWindow is null)
        {
            return;
        }

        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        ISettingsRuntimeCoordinator coordinator = new DelegateSettingsRuntimeCoordinator(
            PreviewSettings,
            SaveSettingsAsync,
            OpenTutorial,
            ReportSettingsSaveFailure);
        SettingsWindow settingsWindow = new(_settings, coordinator)
        {
            Owner = context.Window as Window ?? _typedMainWindow,
            Topmost = _typedMainWindow.Topmost
        };
        _settingsWindow = settingsWindow;
        settingsWindow.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settingsWindow, settingsWindow))
            {
                _settingsWindow = null;
            }
        };
        // Settings is an owned modal surface in the source app. This also
        // prevents a second settings instance from racing the live preview.
        settingsWindow.ShowDialog();
        if (ReferenceEquals(_settingsWindow, settingsWindow))
        {
            _settingsWindow = null;
        }
    }

    private void PreviewSettings(AppSettings snapshot)
    {
        if (_typedMainWindow is null)
        {
            return;
        }

        AppSettings effective = MergeSettingsWithRuntime(snapshot);
        effective.CopyTo(_settings);
        ThemePreferences.ApplyMode(_settings.ThemeMode);
        MotionPreferences.ApplyMode(_settings.MotionMode);
        _typedMainWindow.ApplySettings(_settings);
        foreach (MemoPopoutWindow popout in _memoPopouts)
        {
            popout.ApplySettings(_settings);
        }
    }

    private async Task SaveSettingsAsync(AppSettings snapshot)
    {
        if (_typedMainWindow is null)
        {
            return;
        }

        PreviewSettings(snapshot);
        ApplySystemSettings(_typedMainWindow);
        await _settingsStorage.SaveAsync(_settings.Clone());
    }

    private async Task SaveWindowStateAsync(AppSettings snapshot)
    {
        snapshot.CopyTo(_settings);
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
        CloseActionDialog dialog = new();
        CloseButtonAction? action = dialog.ShowDialog(owner);
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

    private AppSettings MergeSettingsWithRuntime(AppSettings requested)
    {
        AppSettings effective = requested.Clone();
        int requestedDockSize = Math.Clamp(
            effective.MainWindowDockSize,
            AppSettings.MinimumMainWindowDockSize,
            AppSettings.MaximumMainWindowDockSize);
        bool requestedDockEnabled = effective.MainWindowDockEnabled;
        _typedMainWindow!.CopyRuntimeWindowStateTo(effective);
        // CopyRuntimeWindowStateTo also records the live dock handle size and
        // enable state for shutdown persistence. During a settings preview,
        // those two fields are user edits and must win over the old runtime
        // snapshot captured when the window was opened.
        effective.MainWindowDockSize = requestedDockSize;
        effective.MainWindowDockEnabled = requestedDockEnabled;
        if (!requestedDockEnabled)
        {
            effective.MainWindowDocked = false;
        }

        return effective;
    }

    private void ReportSettingsSaveFailure(Window owner, Exception exception)
    {
        if (!owner.IsVisible || _settingsFailureDialogVisible)
        {
            UiExceptionReporter.Report(exception);
            return;
        }

        _settingsFailureDialogVisible = true;
        try
        {
            ConfirmDialog dialog = new(
                "保存设置失败",
                $"无法保存设置：{exception.Message}");
            dialog.ShowDialog(owner);
        }
        finally
        {
            _settingsFailureDialogVisible = false;
        }
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

    private void ExitApplication(int exitCode = 0)
    {
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
