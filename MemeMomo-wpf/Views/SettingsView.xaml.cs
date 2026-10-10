using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using MemeMomo.Components;
using MemeMomo.Infrastructure;
using MemeMomo.Models;
using MemeMomo.Services;
using MemeMomo.UI;
using MemeMomo.UI.Animation;
using MemeMomo.UI.Notifications;
using MemeMomo.UI.Layout;
using MemeMomo.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace MemeMomo.Views;

using WpfButton = System.Windows.Controls.Button;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

public partial class SettingsView : UserControl, IDisposable
{
    private readonly Window _owner;
    private readonly ISettingsRuntimeCoordinator _coordinator;
    private AppSettings _settings => Session.Settings;
    private bool _applyingUi = true;
    private HotkeyAction? _capturingAction;
    private WpfButton? _capturingButton;
    private WpfButton? _conflictingButton;
    private readonly HashSet<string> _heldModifiers = [];
    private string? _captureMainKey;
    private readonly HotkeySetting _captureCandidate = new();
    private static readonly string DisplayVersion = ReadDisplayVersion();
    private readonly SafeHyperlinkCommand _projectLinkCommand;
    private readonly Func<CancellationToken, Task<GitHubRelease>> _getLatestReleaseAsync;
    private AboutUpdateState _aboutUpdateState;
    private Uri? _aboutUpdateReleaseUri;
    private NotificationFeedback? _hotkeyFeedback;
    private NotificationFeedback? _captureFeedback;
    private NotificationFeedback? _updateFeedback;

    private enum AboutUpdateState { Initial, Checking, UpdateAvailable, UpToDate, Failed }

    public SettingsView(Window owner, AppSettings settings, ISettingsRuntimeCoordinator coordinator)
        : this(owner, settings, coordinator, null, null) { }

    internal SettingsView(Window owner, AppSettings settings, ISettingsRuntimeCoordinator coordinator,
        Action<Uri>? launchProjectUri, Func<CancellationToken, Task<GitHubRelease>>? getLatestReleaseAsync)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        Session = new SettingsViewModel(settings, coordinator, error =>
        {
            if (!_disposed) _coordinator.ReportSaveFailure(_owner, error);
        });
        _projectLinkCommand = new SafeHyperlinkCommand(launchProjectUri);
        _getLatestReleaseAsync = getLatestReleaseAsync ?? new GitHubReleaseService().GetLatestReleaseAsync;
        InitializeComponent();
        _categoryTransition = new PageTransitionController(CategoryViewport, SettingsScrollViewer, CategoryTransitionSnapshot);
        _subpageTransition = new PageTransitionController(SettingsViewport, SettingsSurface, SettingsTransitionSnapshot);
        InitializeSelectors();
        InitializeCategories();
        _layout = new SettingsPageLayoutController(SettingsBody, CategoryNavigation, CategorySelector,
            CategoryContentHost, RestoreCategoryPosition);
        DockSizeSlider.ValueCommitted += OnDockSizeValueCommitted;
        TrayClickToggle.ValueChanged += OnTrayClickValueChanged;
        MotionPreferences.Changed += OnMotionPreferencesChanged;
        LocalizationService.LanguageChanged += OnLanguageChanged;
        IsVisibleChanged += OnPresentationChanged;
        IsHitTestVisibleChanged += OnPresentationChanged;
        TaskSettingsCard.Visibility = Visibility.Collapsed;
        ApplySettingsToUi();
        RenderAboutContent();
    }

    public SettingsViewModel Session { get; }
    public AppSettings SettingsSnapshot => Session.SettingsSnapshot;
    public bool IsCapturingHotkey => _capturingAction is not null;
    public string? LastSaveError => Session.LastSaveError;
    internal CollapsibleSection MemoTaskbarHotkeySectionPart => MemoTaskbarHotkeySection;
    internal CollapsibleSection QuickMemoPopoutSectionPart => QuickMemoPopoutSection;
    internal CollapsibleSection DockSizeSectionPart => DockSizeSection;
    internal string HotkeyValidationMessage => _hotkeyFeedback?.Text ?? string.Empty;
    internal WpfButton ToggleTopmostHotkeyButtonPart => ToggleTopmostHotkeyButton;
    internal WpfButton ToggleMemoTaskbarHotkeyButtonPart => ToggleMemoTaskbarHotkeyButton;
    internal AnimatedSlider DockSizeSliderPart => DockSizeSlider;
    internal AnimatedCheckBox QuickMemoEnabledCheckBoxPart => QuickMemoEnabledCheckBox;
    internal AnimatedCheckBox PopoutDockEnabledCheckBoxPart => PopoutDockEnabledCheckBox;
    internal AnimatedCheckBox MemoWindowTopmostCheckBoxPart => MemoWindowTopmostCheckBox;

    public static bool FindFirstDuplicatePair(AppSettings settings, out string fieldA, out string fieldB) =>
        HotkeyValidation.FindFirstDuplicatePair(settings, out fieldA, out fieldB);

    public Task WaitForPendingSaveAsync() => Session.WaitForPendingSaveAsync();

    internal async Task ResetToDefaultsAsync()
    {
        // The source reset restores preferences immediately, while the live
        // window keeps its current geometry, docking position and pin state.
        AppSettings.CreateDefault().CopyUserPreferencesTo(_settings);
        LocalizationService.SetLanguage(_settings.Language);
        ThemePreferences.ApplyMode(_settings.ThemeMode);
        MotionPreferences.ApplyMode(_settings.MotionMode);
        ApplySettingsToUi();
        CommitChange();
        await WaitForPendingSaveAsync();
    }

    private void InitializeSelectors()
    {
        LanguageSelector.Options =
        [
            new(nameof(AppLanguage.ChineseSimplified), "简体中文"),
            new(nameof(AppLanguage.English), "English"),
            new(nameof(AppLanguage.ChineseTraditional), "繁體中文")
        ];
        RefreshLocalizedSelectors();
        CloseActionSelector.SelectionChanged += OnCloseActionSelectionChanged;
        LanguageSelector.SelectionChanged += OnLanguageSelectionChanged;
        ThemeSelector.SelectionChanged += OnThemeSelectionChanged;
        MemoBackgroundSelector.SelectionChanged += OnMemoColorSelectionChanged;
        MotionSelector.SelectionChanged += OnMotionSelectionChanged;
        ReminderNotificationSelector.SelectionChanged += OnReminderNotificationSelectionChanged;
    }

    private void ApplySettingsToUi()
    {
        RefreshTaskSettings();
        _applyingUi = true;
        try
        {
            _settings.MainWindowDockSize = Math.Clamp(
                _settings.MainWindowDockSize,
                AppSettings.MinimumMainWindowDockSize,
                AppSettings.MaximumMainWindowDockSize);
            LanguageSelector.SelectedKey = _settings.Language.ToString();
            CloseActionSelector.SelectedKey = _settings.CloseButtonAction.ToString();
            ThemeSelector.SelectedKey = _settings.ThemeMode.ToString();
            MemoBackgroundSelector.SelectedPreset = _settings.MemoBackgroundColor;
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

    private void RefreshLocalizedSelectors()
    {
        _applyingUi = true;
        try
        {
            CloseActionSelector.Options =
            [
                new(nameof(CloseButtonAction.MinimizeToTray), LocalizationService.Get("最小化托盘")),
                new(nameof(CloseButtonAction.Close), LocalizationService.Get("关闭"))
            ];
            ThemeSelector.Options =
            [
                new(nameof(ThemeMode.FollowSystem), LocalizationService.Get("跟随系统")),
                new(nameof(ThemeMode.Light), LocalizationService.Get("亮色")),
                new(nameof(ThemeMode.Dark), LocalizationService.Get("暗色"))
            ];
            MotionSelector.Options =
            [
                new(nameof(MotionMode.FollowSystem), LocalizationService.Get("跟随系统")),
                new(nameof(MotionMode.AlwaysOn), LocalizationService.Get("始终开启")),
                new(nameof(MotionMode.Off), LocalizationService.Get("关闭动画"))
            ];
            ReminderNotificationSelector.Options =
            [
                new(nameof(ReminderNotificationMode.SystemToast), LocalizationService.Get("系统通知")),
                new(nameof(ReminderNotificationMode.InAppWindow), LocalizationService.Get("应用内窗口")),
                new(nameof(ReminderNotificationMode.Both), LocalizationService.Get("两者"))
            ];
            LanguageSelector.SelectedKey = _settings.Language.ToString();
            CloseActionSelector.SelectedKey = _settings.CloseButtonAction.ToString();
            ThemeSelector.SelectedKey = _settings.ThemeMode.ToString();
            MotionSelector.SelectedKey = _settings.MotionMode.ToString();
            ReminderNotificationSelector.SelectedKey = _settings.ReminderNotification.ToString();
        }
        finally
        {
            _applyingUi = false;
        }
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
            ? LocalizationService.Get("系统当前已开启动画")
            : LocalizationService.Get("系统当前已减少动画");
        MotionSystemStatusSection.IsExpanded = _settings.MotionMode == MotionMode.FollowSystem;
    }

    private void UpdateHotkeyButtons()
    {
        ToggleTopmostHotkeyButton.Content = _settings.ToggleTopmostHotkey.ToString();
        ToggleMemoTaskbarHotkeyButton.Content = _settings.ToggleMemoTaskbarHotkey.ToString();
        MinimizeHotkeyButton.Content = _settings.MinimizeHotkey.ToString();
        ShowWindowHotkeyButton.Content = _settings.ShowWindowHotkey.ToString();
        QuickMemoHotkeyButton.Content = _settings.QuickMemoHotkey.ToString();
        QuickAddHotkeyButton.Content = _settings.QuickAddHotkey.ToString();
    }

    private void CommitChange()
    {
        if (!_applyingUi && !_leaving && !_disposed) Session.CommitChange();
    }

    private void OnCloseActionSelectionChanged(object? sender, SegmentedSelectionChangedEventArgs e)
    {
        if (_applyingUi || !Enum.TryParse(e.NewKey, out CloseButtonAction action) || _settings.CloseButtonAction == action) return;
        _settings.CloseButtonAction = action;
        _settings.HasAskedCloseButtonAction = true;
        CommitChange();
    }

    private void OnLanguageSelectionChanged(object? sender, SegmentedSelectionChangedEventArgs e)
    {
        if (_applyingUi || !Enum.TryParse(e.NewKey, out AppLanguage language) || _settings.Language == language)
        {
            return;
        }

        _settings.Language = language;
        LocalizationService.SetLanguage(language);
        CommitChange();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        _subpageTransition.Reset();
        ResetCategoryTransition();
        SaveCategoryPosition();
        RefreshTaskSettings();
        RefreshLocalizedSelectors();
        RefreshCategoryLabels();
        EndCapture();
        ClearValidation();
        ClearConflict();
        UpdateMotionStatus();
        RenderAboutContent();
        RestoreCategoryPosition();
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

    private void OnMemoColorSelectionChanged(object? sender, EventArgs e)
    {
        if (_applyingUi) return;
        _settings.MemoBackgroundColor = MemoBackgroundSelector.SelectedPreset;
        CommitChange();
    }

    private void OnReminderNotificationSelectionChanged(object? sender, SegmentedSelectionChangedEventArgs e)
    {
        if (_applyingUi || !Enum.TryParse(e.NewKey, out ReminderNotificationMode mode) || _settings.ReminderNotification == mode) return;
        _settings.ReminderNotification = mode;
        // 该设置无即时运行时副作用：App 在保存回调后从 _settings 读取，下一条到期提醒即生效。
        CommitChange();
    }

    private void OnMotionPreferencesChanged(object? sender, EventArgs e)
    {
        if (!MotionPreferences.AnimationsEnabled)
        {
            ResetCategoryTransition();
            _subpageTransition.Reset();
        }
        UpdateMotionStatus();
    }

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

    private async void OnUpdateStatusClick(object sender, RoutedEventArgs e)
    {
        if (!_active || _leaving || _pageCancellation is null || _aboutUpdateState is AboutUpdateState.Checking or AboutUpdateState.UpToDate)
        {
            return;
        }

        if (_aboutUpdateState == AboutUpdateState.UpdateAvailable)
        {
            _projectLinkCommand.Execute(_aboutUpdateReleaseUri);
            return;
        }

        int generation = _pageGeneration;
        CancellationToken token = _pageCancellation.Token;
        _aboutUpdateState = AboutUpdateState.Checking;
        RenderAboutContent();
        try
        {
            GitHubRelease release = await _getLatestReleaseAsync(token);
            if (!IsCurrentPage(generation)) return;

            _aboutUpdateReleaseUri = release.HtmlUrl;
            _aboutUpdateState = release.MatchesVersion(DisplayVersion)
                ? AboutUpdateState.UpToDate
                : AboutUpdateState.UpdateAvailable;
        }
        catch (Exception exception)
        {
            if (!IsCurrentPage(generation)) return;

            Debug.WriteLine($"[Settings] checking for updates failed: {exception.Message}");
            _aboutUpdateState = AboutUpdateState.Failed;
        }

        RenderAboutContent();
    }

    private void RenderAboutContent()
    {
        CurrentVersionText.Text = DisplayVersion;
        AutomationProperties.SetName(
            CurrentVersionBadge,
            LocalizationService.Format("当前版本：{0}", DisplayVersion));
        string statusSource = _aboutUpdateState switch
        {
            AboutUpdateState.Initial => "检测更新",
            AboutUpdateState.Checking => "检测中",
            AboutUpdateState.UpdateAvailable => "有新版本",
            AboutUpdateState.UpToDate => "已是最新",
            AboutUpdateState.Failed => "检测失败",
            _ => throw new InvalidOperationException($"Unknown update state: {_aboutUpdateState}")
        };
        string status = LocalizationService.Get(statusSource);
        UpdateStatusLabel.Text = LocalizationService.Get(_aboutUpdateState == AboutUpdateState.UpdateAvailable ? "查看新版本" : "检测更新");
        if (_aboutUpdateState != AboutUpdateState.Initial && _active && !_leaving && IsVisible)
        {
            int generation = _pageGeneration;
            Func<Task<bool>>? openRelease = _aboutUpdateState == AboutUpdateState.UpdateAvailable
                ? () => { if (!IsCurrentPage(generation)) return Task.FromResult(false); _projectLinkCommand.Execute(_aboutUpdateReleaseUri); return Task.FromResult(true); }
            : null;
            (_updateFeedback ??= new(this)).Show(status,
                _aboutUpdateState == AboutUpdateState.Failed ? NotificationKind.Error
                    : _aboutUpdateState == AboutUpdateState.Checking ? NotificationKind.Information : NotificationKind.Success,
                persistent: _aboutUpdateState == AboutUpdateState.Checking,
                actionText: openRelease is null ? null : LocalizationService.Get("查看新版本"), action: openRelease);
        }

        UpdateStatusButton.IsEnabled = _aboutUpdateState is
            AboutUpdateState.Initial or AboutUpdateState.UpdateAvailable or AboutUpdateState.Failed;
        AutomationProperties.SetName(UpdateStatusButton, UpdateStatusLabel.Text);
    }

    private void OnProjectLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton { Tag: string target })
        {
            _projectLinkCommand.Execute(target);
        }
    }

    internal static string FormatDisplayVersion(string? informationalVersion, string? assemblyVersion)
    {
        string version = string.IsNullOrWhiteSpace(informationalVersion)
            ? assemblyVersion ?? string.Empty
            : informationalVersion.Trim();
        int metadataIndex = version.IndexOf('+', StringComparison.Ordinal);
        return metadataIndex >= 0 ? version[..metadataIndex] : version;
    }

    private static string ReadDisplayVersion()
    {
        Assembly assembly = typeof(SettingsView).Assembly;
        string? informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string? assemblyVersion = assembly.GetName().Version?.ToString();
        return FormatDisplayVersion(informationalVersion, assemblyVersion);
    }

    private async void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (!_active || _leaving || _resetPrompt is not null) return;
        EndCapture();
        var host = WindowNotifications.FindHost(_owner);
        if (host is null) return;
        int generation = _pageGeneration;
        Task<int> prompt = host.ChooseAsync(LocalizationService.Get("重置设置") + "\n" +
            LocalizationService.Get("确定要恢复默认设置吗？"),
            [LocalizationService.Get("取消"), LocalizationService.Get("确定")]);
        _resetPrompt = prompt;
        int choice = await prompt;
        if (ReferenceEquals(_resetPrompt, prompt)) _resetPrompt = null;
        if (choice == 1 && IsCurrentPage(generation)) await ResetToDefaultsAsync();
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
        if (!_active || _leaving || !button.IsEnabled || !button.IsVisible) return;
        EndCapture();
        _capturingAction = action;
        _capturingButton = button;
        _coordinator.SetHotkeyCaptureActive(true);
        _heldModifiers.Clear();
        _captureMainKey = null;
        ClearHotkey(_captureCandidate);
        ClearValidation();
        ClearConflict();
        (_captureFeedback ??= new(this)).Show(LocalizationService.Get("按下快捷键..."), persistent: true);
        button.Focus();
    }

    internal void HandlePreviewKeyDown(WpfKeyEventArgs e)
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

    internal void HandlePreviewKeyUp(WpfKeyEventArgs e)
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
        bool wasCapturing = IsCapturingHotkey;
        _captureFeedback?.Clear();
        _capturingAction = null;
        _capturingButton = null;
        _heldModifiers.Clear();
        _captureMainKey = null;
        if (wasCapturing) _coordinator.SetHotkeyCaptureActive(false);
        UpdateHotkeyButtons();
    }

    private HotkeySetting GetHotkey(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleTopmost => _settings.ToggleTopmostHotkey,
        HotkeyAction.ToggleMemoTaskbar => _settings.ToggleMemoTaskbarHotkey,
        HotkeyAction.Minimize => _settings.MinimizeHotkey,
        HotkeyAction.ShowWindow => _settings.ShowWindowHotkey,
        HotkeyAction.QuickMemo => _settings.QuickMemoHotkey,
        HotkeyAction.QuickAdd => _settings.QuickAddHotkey,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private WpfButton GetHotkeyButton(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleTopmost => ToggleTopmostHotkeyButton,
        HotkeyAction.ToggleMemoTaskbar => ToggleMemoTaskbarHotkeyButton,
        HotkeyAction.Minimize => MinimizeHotkeyButton,
        HotkeyAction.ShowWindow => ShowWindowHotkeyButton,
        HotkeyAction.QuickMemo => QuickMemoHotkeyButton,
        HotkeyAction.QuickAdd => QuickAddHotkeyButton,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private void ShowValidation(string message)
    {
        (_hotkeyFeedback ??= new(this)).Show(message, NotificationKind.Error);
    }

    private void ClearValidation() => _hotkeyFeedback?.Clear();

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

    public void Dispose()
    {
        if (_disposed) return;
        CancelPageOperations();
        _disposed = true;
        _active = false;
        _positionRestore?.Abort();
        _categoryTransition.Dispose();
        _subpageTransition.Dispose();
        _taskStatusManager?.Dispose();
        _layout.Dispose();
        DetachTaskSettings();
        IsVisibleChanged -= OnPresentationChanged;
        IsHitTestVisibleChanged -= OnPresentationChanged;
        MotionPreferences.Changed -= OnMotionPreferencesChanged;
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        DockSizeSlider.ValueCommitted -= OnDockSizeValueCommitted;
        TrayClickToggle.ValueChanged -= OnTrayClickValueChanged;
        CloseActionSelector.SelectionChanged -= OnCloseActionSelectionChanged;
        ThemeSelector.SelectionChanged -= OnThemeSelectionChanged;
        MemoBackgroundSelector.SelectionChanged -= OnMemoColorSelectionChanged;
        MotionSelector.SelectionChanged -= OnMotionSelectionChanged;
        ReminderNotificationSelector.SelectionChanged -= OnReminderNotificationSelectionChanged;
        LanguageSelector.SelectionChanged -= OnLanguageSelectionChanged;
    }
}
