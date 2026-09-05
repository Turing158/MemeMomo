using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MemeMomo.Components;
using MemeMomo.Components.Dialogs;
using MemeMomo.Infrastructure;
using MemeMomo.Models;
using MemeMomo.Services;
using MemeMomo.UI;
using MemeMomo.UI.Windows;

namespace MemeMomo.Views;

using WpfButton = System.Windows.Controls.Button;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

public partial class SettingsWindow : BorderlessWindow
{
    private AppSettings _settings = AppSettings.CreateDefault();
    private ISettingsRuntimeCoordinator _coordinator = new DelegateSettingsRuntimeCoordinator(
        static _ => { },
        static _ => Task.CompletedTask);
    private readonly object _saveLock = new();
    private Task _saveChain = Task.CompletedTask;
    private bool _applyingUi;
    private bool _closingAfterFlush;
    private HotkeyAction? _capturingAction;
    private WpfButton? _capturingButton;
    private WpfButton? _conflictingButton;
    private readonly HashSet<string> _heldModifiers = [];
    private string? _captureMainKey;
    private readonly HotkeySetting _captureCandidate = new();

    public SettingsWindow()
    {
        InitializeComponent();
        InitializeSelectors();
        DockSizeSlider.ValueCommitted += OnDockSizeValueCommitted;
        TrayClickToggle.ValueChanged += OnTrayClickValueChanged;
        MotionPreferences.Changed += OnMotionPreferencesChanged;
        Closed += OnClosed;
        ApplySettingsToUi();
    }

    public SettingsWindow(
        AppSettings settings,
        Action<AppSettings> previewSettings,
        Func<AppSettings, Task> saveSettingsAsync)
        : this(settings, new DelegateSettingsRuntimeCoordinator(previewSettings, saveSettingsAsync))
    {
    }

    public SettingsWindow(AppSettings settings, ISettingsRuntimeCoordinator coordinator)
        : this()
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(coordinator);
        _settings = settings.Clone();
        _coordinator = coordinator;
        ApplySettingsToUi();
    }

    public AppSettings SettingsSnapshot => _settings.Clone();
    public bool IsCapturingHotkey => _capturingAction is not null;
    public string? LastSaveError { get; private set; }
    internal CollapsibleSection MemoTaskbarHotkeySectionPart => MemoTaskbarHotkeySection;
    internal CollapsibleSection QuickMemoPopoutSectionPart => QuickMemoPopoutSection;
    internal CollapsibleSection DockSizeSectionPart => DockSizeSection;
    internal CollapsibleSection HotkeyValidationSectionPart => HotkeyValidationSection;
    internal WpfButton ToggleTopmostHotkeyButtonPart => ToggleTopmostHotkeyButton;
    internal WpfButton ToggleMemoTaskbarHotkeyButtonPart => ToggleMemoTaskbarHotkeyButton;
    internal AnimatedSlider DockSizeSliderPart => DockSizeSlider;
    internal AnimatedCheckBox QuickMemoEnabledCheckBoxPart => QuickMemoEnabledCheckBox;
    internal AnimatedCheckBox PopoutDockEnabledCheckBoxPart => PopoutDockEnabledCheckBox;
    internal AnimatedCheckBox MemoWindowTopmostCheckBoxPart => MemoWindowTopmostCheckBox;
    internal void CloseImmediatelyForTest() => CloseImmediately();

    public static bool FindFirstDuplicatePair(AppSettings settings, out string fieldA, out string fieldB) =>
        HotkeyValidation.FindFirstDuplicatePair(settings, out fieldA, out fieldB);

    public async Task WaitForPendingSaveAsync()
    {
        while (true)
        {
            Task pending;
            lock (_saveLock)
            {
                pending = _saveChain;
            }

            await pending;
            lock (_saveLock)
            {
                if (ReferenceEquals(pending, _saveChain))
                {
                    return;
                }
            }
        }
    }

    internal async Task ResetToDefaultsAsync()
    {
        // The source reset restores preferences immediately, while the live
        // window keeps its current geometry, docking position and pin state.
        AppSettings.CreateDefault().CopyUserPreferencesTo(_settings);
        ThemePreferences.ApplyMode(_settings.ThemeMode);
        MotionPreferences.ApplyMode(_settings.MotionMode);
        ApplySettingsToUi();
        CommitChange();
        await WaitForPendingSaveAsync();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!IsCloseApproved)
        {
            e.Cancel = true;
            if (!_closingAfterFlush)
            {
                FlushAndCloseAsync().Observe();
            }

            return;
        }

        base.OnClosing(e);
    }

    private void InitializeSelectors()
    {
        CloseActionSelector.Options =
        [
            new(nameof(CloseButtonAction.MinimizeToTray), "最小化托盘"),
            new(nameof(CloseButtonAction.Close), "关闭")
        ];
        ThemeSelector.Options =
        [
            new(nameof(ThemeMode.FollowSystem), "跟随系统"),
            new(nameof(ThemeMode.Light), "亮色"),
            new(nameof(ThemeMode.Dark), "暗色")
        ];
        MotionSelector.Options =
        [
            new(nameof(MotionMode.FollowSystem), "跟随系统"),
            new(nameof(MotionMode.AlwaysOn), "始终开启"),
            new(nameof(MotionMode.Off), "关闭")
        ];
        ReminderNotificationSelector.Options =
        [
            new(nameof(ReminderNotificationMode.SystemToast), "系统通知"),
            new(nameof(ReminderNotificationMode.InAppWindow), "应用内窗口"),
            new(nameof(ReminderNotificationMode.Both), "两者")
        ];
        CloseActionSelector.SelectionChanged += OnCloseActionSelectionChanged;
        ThemeSelector.SelectionChanged += OnThemeSelectionChanged;
        MotionSelector.SelectionChanged += OnMotionSelectionChanged;
        ReminderNotificationSelector.SelectionChanged += OnReminderNotificationSelectionChanged;
    }

    private void ApplySettingsToUi()
    {
        _applyingUi = true;
        try
        {
            _settings.MainWindowDockSize = Math.Clamp(
                _settings.MainWindowDockSize,
                AppSettings.MinimumMainWindowDockSize,
                AppSettings.MaximumMainWindowDockSize);
            CloseActionSelector.SelectedKey = _settings.CloseButtonAction.ToString();
            ThemeSelector.SelectedKey = _settings.ThemeMode.ToString();
            MotionSelector.SelectedKey = _settings.MotionMode.ToString();
            ReminderNotificationSelector.SelectedKey = _settings.ReminderNotification.ToString();
            QuickMemoEnabledCheckBox.IsChecked = _settings.QuickMemoEnabled;
            QuickMemoShowPopoutCheckBox.IsChecked = _settings.QuickMemoShowPopoutAfterAdd;
            DuplicateMemoCheckBox.IsChecked = _settings.DuplicateMemoEnabled;
            DockEnabledCheckBox.IsChecked = _settings.MainWindowDockEnabled;
            PopoutDockEnabledCheckBox.IsChecked = _settings.PopoutDockEnabled;
            ShowMainWindowTaskbarCheckBox.IsChecked = _settings.ShowMainWindowTaskbarIcon;
            ShowMemoWindowTaskbarCheckBox.IsChecked = _settings.ShowMemoWindowTaskbarIcon;
            MemoWindowTopmostCheckBox.IsChecked = _settings.MemoWindowTopmostByDefault;
            TrayClickToggle.Value = !_settings.TraySingleClickToShow;
            DockSizeSlider.Value = _settings.MainWindowDockSize;
        }
        finally
        {
            _applyingUi = false;
        }

        UpdateHotkeyButtons();
        UpdateDependentSections();
        UpdateMotionStatus();
    }

    private void UpdateDependentSections()
    {
        QuickMemoPopoutSection.IsExpanded = _settings.QuickMemoEnabled;
        QuickMemoHotkeyButton.IsEnabled = _settings.QuickMemoEnabled;
        QuickMemoHotkeyButton.Opacity = _settings.QuickMemoEnabled ? 1 : 0.45;
        DockSizeSection.IsExpanded = _settings.MainWindowDockEnabled;
        MemoTaskbarHotkeySection.IsExpanded = _settings.ShowMemoWindowTaskbarIcon;

        if (!_settings.QuickMemoEnabled && _capturingAction == HotkeyAction.QuickMemo)
        {
            EndCapture();
        }
        if (!_settings.ShowMemoWindowTaskbarIcon && _capturingAction == HotkeyAction.ToggleMemoTaskbar)
        {
            EndCapture();
        }
    }

    private void UpdateMotionStatus()
    {
        MotionSystemStatusText.Text = MotionPreferences.SystemAnimationsEnabled
            ? "系统当前已开启动画"
            : "系统当前已减少动画";
        MotionSystemStatusSection.IsExpanded = _settings.MotionMode == MotionMode.FollowSystem;
    }

    private void UpdateHotkeyButtons()
    {
        ToggleTopmostHotkeyButton.Content = _settings.ToggleTopmostHotkey.ToString();
        ToggleMemoTaskbarHotkeyButton.Content = _settings.ToggleMemoTaskbarHotkey.ToString();
        MinimizeHotkeyButton.Content = _settings.MinimizeHotkey.ToString();
        ShowWindowHotkeyButton.Content = _settings.ShowWindowHotkey.ToString();
        QuickMemoHotkeyButton.Content = _settings.QuickMemoHotkey.ToString();
    }

    private void CommitChange(bool preview = true)
    {
        if (_applyingUi || _closingAfterFlush)
        {
            return;
        }

        AppSettings snapshot = _settings.Clone();
        if (preview)
        {
            try
            {
                _coordinator.Preview(snapshot.Clone());
            }
            catch (Exception ex)
            {
                LastSaveError = ex.Message;
                _coordinator.ReportSaveFailure(this, ex);
            }
        }

        lock (_saveLock)
        {
            _saveChain = _saveChain
                .ContinueWith(
                    _ => SaveSnapshotAsync(snapshot),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default)
                .Unwrap();
        }
    }

    private async Task SaveSnapshotAsync(AppSettings snapshot)
    {
        try
        {
            Task saveTask = await Dispatcher.InvokeAsync(() => _coordinator.SaveAsync(snapshot));
            await saveTask;
            LastSaveError = null;
        }
        catch (Exception ex)
        {
            LastSaveError = ex.Message;
            await Dispatcher.InvokeAsync(() => _coordinator.ReportSaveFailure(this, ex));
        }
    }

    private async Task FlushAndCloseAsync()
    {
        if (_closingAfterFlush)
        {
            return;
        }

        _closingAfterFlush = true;
        EndCapture();
        await WaitForPendingSaveAsync();
        CloseWithTransition();
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
            e.Handled = true;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => FlushAndCloseAsync().Observe();

    private void OnCloseActionSelectionChanged(object? sender, SegmentedSelectionChangedEventArgs e)
    {
        if (_applyingUi || !Enum.TryParse(e.NewKey, out CloseButtonAction action) || _settings.CloseButtonAction == action) return;
        _settings.CloseButtonAction = action;
        _settings.HasAskedCloseButtonAction = true;
        CommitChange();
    }

    private void OnThemeSelectionChanged(object? sender, SegmentedSelectionChangedEventArgs e)
    {
        if (_applyingUi || !Enum.TryParse(e.NewKey, out ThemeMode mode) || _settings.ThemeMode == mode) return;
        _settings.ThemeMode = mode;
        ThemePreferences.ApplyMode(mode);
        CommitChange();
    }

    private void OnMotionSelectionChanged(object? sender, SegmentedSelectionChangedEventArgs e)
    {
        if (_applyingUi || !Enum.TryParse(e.NewKey, out MotionMode mode) || _settings.MotionMode == mode) return;
        _settings.MotionMode = mode;
        MotionPreferences.ApplyMode(mode);
        UpdateMotionStatus();
        CommitChange();
    }

    private void OnReminderNotificationSelectionChanged(object? sender, SegmentedSelectionChangedEventArgs e)
    {
        if (_applyingUi || !Enum.TryParse(e.NewKey, out ReminderNotificationMode mode) || _settings.ReminderNotification == mode) return;
        _settings.ReminderNotification = mode;
        // 该设置无即时运行时副作用：App 在保存回调后从 _settings 读取，下一条到期提醒即生效。
        CommitChange();
    }

    private void OnMotionPreferencesChanged(object? sender, EventArgs e) => UpdateMotionStatus();

    private void OnQuickMemoEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_applyingUi) return;
        _settings.QuickMemoEnabled = QuickMemoEnabledCheckBox.IsChecked == true;
        UpdateDependentSections();
        CommitChange();
    }

    private void OnQuickMemoShowPopoutChanged(object sender, RoutedEventArgs e)
    {
        if (_applyingUi) return;
        _settings.QuickMemoShowPopoutAfterAdd = QuickMemoShowPopoutCheckBox.IsChecked == true;
        CommitChange();
    }

    private void OnDuplicateMemoChanged(object sender, RoutedEventArgs e)
    {
        if (_applyingUi) return;
        _settings.DuplicateMemoEnabled = DuplicateMemoCheckBox.IsChecked == true;
        CommitChange();
    }

    private void OnDockEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_applyingUi) return;
        _settings.MainWindowDockEnabled = DockEnabledCheckBox.IsChecked == true;
        if (!_settings.MainWindowDockEnabled)
        {
            _settings.MainWindowDocked = false;
        }
        UpdateDependentSections();
        CommitChange();
    }

    private void OnPopoutDockEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_applyingUi) return;
        _settings.PopoutDockEnabled = PopoutDockEnabledCheckBox.IsChecked == true;
        // 已打开便签的即时还原由 App 的 PreviewSettings → ApplySettings 下发，此处只落设置。
        CommitChange();
    }

    private void OnShowMainWindowTaskbarChanged(object sender, RoutedEventArgs e)
    {
        if (_applyingUi) return;
        _settings.ShowMainWindowTaskbarIcon = ShowMainWindowTaskbarCheckBox.IsChecked == true;
        CommitChange();
    }

    private void OnShowMemoWindowTaskbarChanged(object sender, RoutedEventArgs e)
    {
        if (_applyingUi) return;
        _settings.ShowMemoWindowTaskbarIcon = ShowMemoWindowTaskbarCheckBox.IsChecked == true;
        UpdateDependentSections();
        CommitChange();
    }

    private void OnMemoWindowTopmostChanged(object sender, RoutedEventArgs e)
    {
        if (_applyingUi) return;
        _settings.MemoWindowTopmostByDefault = MemoWindowTopmostCheckBox.IsChecked == true;
        CommitChange();
    }

    private void OnTrayClickValueChanged(bool useDoubleClick)
    {
        if (_applyingUi) return;
        _settings.TraySingleClickToShow = !useDoubleClick;
        CommitChange();
    }

    private void OnDockSizeValueCommitted(object? sender, SliderValueChangedEventArgs e)
    {
        if (_applyingUi) return;
        _settings.MainWindowDockSize = e.NewValue;
        CommitChange();
    }

    private void OnTutorialClick(object sender, RoutedEventArgs e) => _coordinator.OpenTutorial(_settings.Clone());

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        ConfirmDialog dialog = new("重置设置", "确定要恢复默认设置吗？");
        if (dialog.ShowDialog(this))
        {
            ResetToDefaultsAsync().Observe();
        }
    }

    private void OnHotkeyButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton { Tag: string actionName } button && Enum.TryParse(actionName, out HotkeyAction action))
        {
            StartCapture(action, button);
        }
    }

    private void StartCapture(HotkeyAction action, WpfButton button)
    {
        EndCapture();
        _capturingAction = action;
        _capturingButton = button;
        _heldModifiers.Clear();
        _captureMainKey = null;
        ClearHotkey(_captureCandidate);
        ClearValidation();
        ClearConflict();
        button.Content = "按下快捷键...";
        Focus();
    }

    private void OnWindowPreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (_capturingAction is null || _capturingButton is null) return;
        e.Handled = true;
        Key actualKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (actualKey == Key.Escape)
        {
            ClearHotkey(GetHotkey(_capturingAction.Value));
            ClearValidation();
            ClearConflict();
            EndCapture();
            CommitChange();
            return;
        }

        string? key = HotkeyValidation.NormalizeKey(actualKey);
        if (key is null) return;
        if (HotkeyValidation.IsModifierKey(key))
        {
            _heldModifiers.Add(key);
            UpdateCapturePreview();
            return;
        }

        _captureMainKey = key;
        ModifierKeys modifiers = Keyboard.Modifiers;
        _captureCandidate.Key = key;
        _captureCandidate.Ctrl = modifiers.HasFlag(ModifierKeys.Control);
        _captureCandidate.Alt = modifiers.HasFlag(ModifierKeys.Alt);
        _captureCandidate.Shift = modifiers.HasFlag(ModifierKeys.Shift);
        _captureCandidate.Win = modifiers.HasFlag(ModifierKeys.Windows);
        UpdateCapturePreview();
    }

    private void OnWindowPreviewKeyUp(object sender, WpfKeyEventArgs e)
    {
        if (_capturingAction is null || _capturingButton is null) return;
        e.Handled = true;
        Key actualKey = e.Key == Key.System ? e.SystemKey : e.Key;
        string? key = HotkeyValidation.NormalizeKey(actualKey);
        if (key is not null && HotkeyValidation.IsModifierKey(key))
        {
            _heldModifiers.Remove(key);
            UpdateCapturePreview();
        }

        if (key is not null && key == _captureMainKey)
        {
            ApplyCapture();
        }
        else if (_captureMainKey is null && _heldModifiers.Count == 0)
        {
            EndCapture();
        }
    }

    private void ApplyCapture()
    {
        if (_capturingAction is not HotkeyAction action) return;
        HotkeyValidationResult result = HotkeyValidation.Validate(_captureCandidate, _settings, action);
        if (!result.IsValid)
        {
            ShowValidation(result.Error);
            if (result.Conflict is HotkeyAction conflict)
            {
                MarkConflict(GetHotkeyButton(conflict));
            }
            EndCapture();
            return;
        }

        CopyHotkey(_captureCandidate, GetHotkey(action));
        ClearValidation();
        ClearConflict();
        EndCapture();
        CommitChange();
    }

    private void OnHotkeyButtonRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_capturingAction is null) return;
        e.Handled = true;
        ClearValidation();
        ClearConflict();
        EndCapture();
    }

    private void UpdateCapturePreview()
    {
        if (_capturingButton is null) return;
        List<string> parts = [];
        if (_heldModifiers.Contains("Ctrl")) parts.Add("Ctrl");
        if (_heldModifiers.Contains("Alt")) parts.Add("Alt");
        if (_heldModifiers.Contains("Shift")) parts.Add("Shift");
        if (_heldModifiers.Contains("Win")) parts.Add("Win");
        parts.Add(_captureMainKey ?? "?");
        _capturingButton.Content = string.Join(" + ", parts);
    }

    private void EndCapture()
    {
        _capturingAction = null;
        _capturingButton = null;
        _heldModifiers.Clear();
        _captureMainKey = null;
        UpdateHotkeyButtons();
    }

    private HotkeySetting GetHotkey(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleTopmost => _settings.ToggleTopmostHotkey,
        HotkeyAction.ToggleMemoTaskbar => _settings.ToggleMemoTaskbarHotkey,
        HotkeyAction.Minimize => _settings.MinimizeHotkey,
        HotkeyAction.ShowWindow => _settings.ShowWindowHotkey,
        HotkeyAction.QuickMemo => _settings.QuickMemoHotkey,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private WpfButton GetHotkeyButton(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleTopmost => ToggleTopmostHotkeyButton,
        HotkeyAction.ToggleMemoTaskbar => ToggleMemoTaskbarHotkeyButton,
        HotkeyAction.Minimize => MinimizeHotkeyButton,
        HotkeyAction.ShowWindow => ShowWindowHotkeyButton,
        HotkeyAction.QuickMemo => QuickMemoHotkeyButton,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private void ShowValidation(string message)
    {
        HotkeyValidationText.Text = message;
        HotkeyValidationSection.IsExpanded = true;
    }

    private void ClearValidation() => HotkeyValidationSection.IsExpanded = false;

    private void MarkConflict(WpfButton button)
    {
        ClearConflict();
        _conflictingButton = button;
        InteractionState.SetIsInvalid(button, true);
    }

    private void ClearConflict()
    {
        if (_conflictingButton is not null)
        {
            InteractionState.SetIsInvalid(_conflictingButton, false);
            _conflictingButton = null;
        }
    }

    private static void ClearHotkey(HotkeySetting hotkey)
    {
        hotkey.Key = string.Empty;
        hotkey.Ctrl = hotkey.Alt = hotkey.Shift = hotkey.Win = false;
    }

    private static void CopyHotkey(HotkeySetting source, HotkeySetting target)
    {
        target.Key = source.Key;
        target.Ctrl = source.Ctrl;
        target.Alt = source.Alt;
        target.Shift = source.Shift;
        target.Win = source.Win;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        MotionPreferences.Changed -= OnMotionPreferencesChanged;
        DockSizeSlider.ValueCommitted -= OnDockSizeValueCommitted;
        TrayClickToggle.ValueChanged -= OnTrayClickValueChanged;
        CloseActionSelector.SelectionChanged -= OnCloseActionSelectionChanged;
        ThemeSelector.SelectionChanged -= OnThemeSelectionChanged;
        MotionSelector.SelectionChanged -= OnMotionSelectionChanged;
        ReminderNotificationSelector.SelectionChanged -= OnReminderNotificationSelectionChanged;
    }
}
