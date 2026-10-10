using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MemeMomo.Components;
using MemeMomo.Models;
using MemeMomo.Platform.Windows;
using MemeMomo.Services;
using MemeMomo.UI;
using MemeMomo.UI.Animation;
using MemeMomo.UI.Dock;
using MemeMomo.UI.Windows;
using MemeMomo.Utils;
using WpfPoint = System.Windows.Point;
using InputKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace MemeMomo;

/// <summary>
/// 独立便签窗口。窗口本身只负责宿主状态，编辑所有权由 <see cref="MemoEditCoordinator"/> 串行协调。
/// </summary>
public partial class MemoPopoutWindow : BorderlessWindow
{
    private readonly IAnimationFrameSource? _animationFrames;
    private readonly MonitorService _monitors = new();
    private MemoPopoutDockController? _dockController;
    private Func<Guid, double>? _dockPopLengthLoader;
    private Action<Guid, double>? _dockPopLengthSaver;
    private MemoItem? _memo;
    private Func<MemoItem, string, Task>? _saveMemo;
    private FrameAnimation? _timeAnimation;
    private FrameAnimation? _tabVisualAnimation;
    private FrameAnimation? _ghostCollapseAnimation;
    private bool _taskbarButtonEnabled;
    private bool _showTaskbarIcon = true;
    private bool _showPreviewToolbar;
    private bool _showFullTime;
    private bool _popoutDockEnabled = true;
    private bool _defaultPinApplied;
    private bool _sourceDeleted;
    private bool _loadedOnce;
    private bool _cleanupDone;
    private readonly ResourceDictionary _memoPalette = new();
    private MemoColorPreset _backgroundColor;

    /// <summary>
    /// 便签启用逐像素透明（layered）渲染：贴边 D 形标签的半圆弧由 WPF 抗锯齿绘制，
    /// 弧外像素保持透明并穿透点击；不再使用 SetWindowRgn（二值掩码必然阶梯锯齿）。
    /// 返回常量以支持基类构造期读取。
    /// </summary>
    protected override bool UsePerPixelTransparency => true;

    public MemoPopoutWindow()
    {
        InitializeComponent();
        InitializeReminderNavigation();
        // 便签内容与同级的贴边标签共享配色，背景和文字随设置、主题同步更新。
        WindowRoot.Resources.MergedDictionaries.Add(_memoPalette);
        ThemePreferences.PaletteChanged += OnPaletteChanged;
        MarkdownEditor.SaveRequestedAsync = SaveMarkdownAsync;
        MarkdownEditor.CancelRequestedAsync = CancelMarkdownAsync;
        MarkdownEditor.EditingCompleted += OnEditingCompleted;
        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
        LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    public MemoPopoutWindow(
        MemoItem memo,
        WpfPoint position,
        Func<MemoItem, string, Task> saveMemo) : this()
    {
        ArgumentNullException.ThrowIfNull(memo);
        ArgumentNullException.ThrowIfNull(saveMemo);
        _memo = memo;
        _saveMemo = saveMemo;
        Left = position.X;
        Top = position.Y;
        DataContext = memo;
        memo.PropertyChanged += OnMemoPropertyChanged;
        UpdateMemoVisuals();
    }

    internal MemoPopoutWindow(
        MemoItem memo,
        WpfPoint position,
        Func<MemoItem, string, Task> saveMemo,
        IAnimationFrameSource animationFrames) : this(memo, position, saveMemo)
    {
        _animationFrames = animationFrames;
    }

    public MemoItem Memo => _memo ?? throw new InvalidOperationException(LocalizationService.Get("便签尚未绑定备忘录。"));
    internal bool IsMemoBound => _memo is not null;
    internal Grid RootElement => WindowRoot;
    internal MemoPopoutDockTab TabLayer => DockTabLayer;
    public bool IsEdgeDocked => _dockController?.IsEdgeDocked ?? false;
    public MarkdownEditor Editor => MarkdownEditor;
    internal MemoTaskStatusSelector TaskStatusSelector => (MemoTaskStatusSelector)MarkdownEditor.ToolbarLeadingContent!;
    public bool IsTaskbarIconEnabled => _taskbarButtonEnabled;
    public bool IsTaskbarIconVisible => TaskbarIconVisibility.IsVisible(this);
    public bool IsPreviewToolbarVisible => _showPreviewToolbar;
    public bool IsSourceDeleted => _sourceDeleted;
    public bool IsEditorOwner => _memo is not null && MemoEditCoordinator.Shared.IsOwner(_memo.Id, this);

    public void ConfigureTaskService(MemoTaskService service)
    {
        TaskStatusSelector.Service = service;
        TaskStatusSelector.Memo = IsQuickAddDraft ? null : _memo;
        if (IsQuickAddDraft) TaskStatusSelector.ResetNewDefaults();
    }

    internal void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _backgroundColor = MemoWindowPalette.Normalize(settings.MemoBackgroundColor);
        ApplyColors();
        _taskbarButtonEnabled = settings.ShowMemoWindowTaskbarIcon;
        UpdateTaskbarButtonVisual();
        // 控制器在 Loaded 时才创建，开关先落字段，加载时再同步。
        _popoutDockEnabled = settings.PopoutDockEnabled;
        _dockController?.SetEdgeDockEnabled(_popoutDockEnabled);
        // 默认置顶只在首次下发时生效：之后用户可能已用图钉切换过，
        // 设置窗口的批量 ApplySettings 不得重置已有便签的置顶状态。
        if (!_defaultPinApplied)
        {
            _defaultPinApplied = true;
            SetPinned(settings.MemoWindowTopmostByDefault);
        }
    }

    /// <summary>注入贴边弹出长度的持久化回调（App 层接到 settings.json）。须在窗口 Loaded 前调用。</summary>
    internal void ConfigureDockPopLengths(Func<Guid, double>? loadPopLength, Action<Guid, double>? savePopLength)
    {
        _dockPopLengthLoader = loadPopLength;
        _dockPopLengthSaver = savePopLength;
    }

    /// <summary>程序化退出贴边并还原为完整浮动窗口（点击系统通知等外部入口）。返回是否发起了弹出。</summary>
    public bool PopOutFromDock() => _dockController?.PopOutFromDock() ?? false;

    public void TogglePinned() => SetPinned(!Topmost);

    public void SetPinned(bool isPinned)
    {
        Topmost = isPinned;
        InteractionState.SetIsPinActive(PinButton, isPinned);
        if (PinIcon.RenderTransform is RotateTransform rotation)
        {
            double target = isPinned ? -45 : 0;
            double from = rotation.Angle;
            if (!MotionPreferences.AnimationsEnabled)
            {
                rotation.Angle = target;
            }
            else
            {
                MotionAnimations.Start(
                    PinIcon,
                    TimeSpan.FromMilliseconds(190),
                    MotionEasing.CubicEaseOut,
                    progress => rotation.Angle = from + ((target - from) * progress));
            }
        }
    }

    public void ToggleTaskbarIcon()
    {
        if (!_taskbarButtonEnabled)
        {
            return;
        }

        _showTaskbarIcon = !_showTaskbarIcon;
        UpdateTaskbarButtonVisual();
    }

    internal void CloseBecauseSourceDeleted()
    {
        if (_sourceDeleted)
        {
            return;
        }

        _sourceDeleted = true;
        DisposeReminderNavigation();
        MarkdownEditor.AbortForSourceDeletion();
        ReleaseEditorOwnership();
        CloseImmediately();
    }

    internal void CloseImmediatelyForTest() { _quickCloseApproved = _normalCloseApproved = true; CloseImmediately(); }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == TopmostProperty && PinButton is not null)
        {
            InteractionState.SetIsPinActive(PinButton, Topmost);
        }
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce)
        {
            return;
        }

        _loadedOnce = true;
        UpdateMemoVisuals();
        if (IsQuickAddDraft)
        {
            MarkdownEditor.BeginNew();
            return;
        }
        InitializeDockController();
        await BeginEditAsync();
    }

    private void InitializeDockController()
    {
        _dockController ??= new MemoPopoutDockController(
            this,
            _monitors,
            _animationFrames,
            _dockPopLengthLoader,
            _dockPopLengthSaver);
        _dockController.SetEdgeDockEnabled(_popoutDockEnabled);
        _dockController.Attach();
    }

    private async Task BeginEditAsync()
    {
        if (IsQuickAddDraft || _sourceDeleted || _memo is null || _memo.IsDeleted || _memo.IsLifecyclePending || IsEditorOwner)
        {
            return;
        }

        if (!await MemoEditCoordinator.Shared.AcquireAsync(_memo.Id, this, RelinquishEditorAsync, MarkdownEditor.SuspendAndCollectAsync, MarkdownEditor.ResumeAfterLifecycleFailure))
        {
            return;
        }

        // The coordinator callback can await a save on another editor. The
        // source may be deleted or this window may close during that await;
        // never start editing after either lifecycle boundary has completed.
        if (_sourceDeleted || _cleanupDone)
        {
            ReleaseEditorOwnership();
            return;
        }

        MarkdownEditor.BeginExistingEdit(_memo.Content);
        UpdateToolbarButtonVisual();
    }

    internal Task EnsureEditorOwnershipAsync() => BeginEditAsync();

    private async Task<bool> RelinquishEditorAsync()
    {
        if (_sourceDeleted)
        {
            return true;
        }

        if (!await MarkdownEditor.CompleteEditingAsync())
        {
            return false;
        }

        ReleaseEditorOwnership();
        return true;
    }

    private async Task<bool> SaveMarkdownAsync(MarkdownSaveRequest request)
    {
        if (IsQuickAddDraft) return await SaveQuickAddDraftAsync(request);
        if (_sourceDeleted || _memo is null || _memo.IsDeleted || _memo.IsLifecyclePending || _saveMemo is null || request.IsNewMemo || !IsEditorOwner)
        {
            return false;
        }

        await _saveMemo(_memo, request.Markdown);
        UpdateMemoVisuals();
        return true;
    }

    private async Task CancelMarkdownAsync(string restoreMarkdown)
    {
        if (_sourceDeleted || _memo is null || _memo.IsDeleted || _memo.IsLifecyclePending || _saveMemo is null || !IsEditorOwner)
        {
            return;
        }

        await _saveMemo(_memo, restoreMarkdown);
        UpdateMemoVisuals();
    }

    private void OnMemoPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_memo is null || !ReferenceEquals(sender, _memo))
        {
            return;
        }

        if (e.PropertyName is nameof(MemoItem.Content) or nameof(MemoItem.Title))
        {
            UpdateMemoVisuals();
            if (e.PropertyName == nameof(MemoItem.Content) && IsEditorOwner)
            {
                MarkdownEditor.SetExternalMarkdown(_memo.Content);
            }
        }

        if (e.PropertyName is nameof(MemoItem.ReminderAt) or nameof(MemoItem.CanRemind))
        {
            UpdateReminderButtonVisual();
        }
    }

    private void OnEditingCompleted(object? sender, EventArgs e) => UpdateToolbarButtonVisual();

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        if (_dockController is { } controller)
        {
            if (controller.OnFloatingDragStarted(e))
            {
                e.Handled = true;
            }

            return;
        }

        DragMove();
        e.Handled = true;
    }

    /// <summary>贴边视觉切换：便签内容（标题栏 + 编辑器）与 D 形标签交叉淡化。</summary>
    internal void ShowTabVisual(bool showTab, TimeSpan duration)
    {
        _reminderDockSuspended = showTab;
        SynchronizeReminderVisibility();
        _tabVisualAnimation?.Cancel();
        _tabVisualAnimation?.Dispose();
        _tabVisualAnimation = null;
        if (!showTab)
        {
            // 恢复便签内容前先清除预览期的残影收拢（宽度冻结/贴边侧锚定）：
            // 内容须以窗口实际尺寸参与布局。
            ClearGhostCollapse();
        }

        if (showTab)
        {
            if (DockTabLayer.Visibility != Visibility.Visible)
            {
                DockTabLayer.Visibility = Visibility.Visible;
                DockTabLayer.Opacity = 0;
                TitleBar.Opacity = 1;
                BodyHost.Opacity = 1;
            }

            TitleBar.IsHitTestVisible = false;
            BodyHost.IsHitTestVisible = false;
            DockTabLayer.IsHitTestVisible = true;
        }
        else
        {
            if (TitleBar.Visibility != Visibility.Visible)
            {
                TitleBar.Visibility = Visibility.Visible;
                BodyHost.Visibility = Visibility.Visible;
                TitleBar.Opacity = 0;
                BodyHost.Opacity = 0;
                DockTabLayer.Visibility = Visibility.Visible;
                DockTabLayer.Opacity = 1;
            }

            TitleBar.IsHitTestVisible = true;
            BodyHost.IsHitTestVisible = true;
            DockTabLayer.IsHitTestVisible = false;
        }

        double tabFrom = DockTabLayer.Opacity;
        double memoFrom = TitleBar.Opacity;
        double tabTo = showTab ? 1 : 0;
        double memoTo = showTab ? 0 : 1;
        if (Math.Abs(tabFrom - tabTo) < 0.001 && Math.Abs(memoFrom - memoTo) < 0.001)
        {
            ApplyTabVisualTerminal(showTab);
            return;
        }

        FrameAnimation animation = new(_animationFrames);
        _tabVisualAnimation = animation;
        animation.Start(
            duration,
            MotionEasing.CubicEaseOut,
            progress =>
            {
                DockTabLayer.Opacity = tabFrom + ((tabTo - tabFrom) * progress);
                double memo = memoFrom + ((memoTo - memoFrom) * progress);
                TitleBar.Opacity = memo;
                BodyHost.Opacity = memo;
            },
            () =>
            {
                ApplyTabVisualTerminal(showTab);
                if (ReferenceEquals(_tabVisualAnimation, animation))
                {
                    _tabVisualAnimation = null;
                    animation.Dispose();
                }
            });
    }

    private void ApplyTabVisualTerminal(bool showTab)
    {
        DockTabLayer.Opacity = showTab ? 1 : 0;
        TitleBar.Opacity = showTab ? 0 : 1;
        BodyHost.Opacity = showTab ? 0 : 1;
        DockTabLayer.Visibility = showTab ? Visibility.Visible : Visibility.Collapsed;
        TitleBar.Visibility = showTab ? Visibility.Collapsed : Visibility.Visible;
        BodyHost.Visibility = showTab ? Visibility.Collapsed : Visibility.Visible;
        TitleBar.IsHitTestVisible = !showTab;
        BodyHost.IsHitTestVisible = !showTab;
        DockTabLayer.IsHitTestVisible = showTab;
    }

    /// <summary>
    /// 贴边预览期便签残影收拢：冻结内容宽度并锚定贴边侧，从当前宽度动画收拢到
    /// 贴边标签宽度。窗口矩形同时 morph 到更宽的贴边条，若内容随矩形横向拉伸，
    /// 逐帧位图滞后会把残影带着朝贴边对向方向飘移，故收拢由内容自身承担：
    /// 方向朝贴边、速度远快于矩形向内的扩张。
    /// </summary>
    internal void BeginGhostCollapse(bool anchorRight, TimeSpan duration)
    {
        CancelGhostCollapse();
        double from = Math.Max(MemoHost.ActualWidth, MemoPopoutDockOptions.CollapsedWidth);
        MemoHost.Width = from;
        AnchorMemoHost(anchorRight);
        StartGhostCollapse(from, duration);
    }

    /// <summary>预览中拖到另一侧边缘：残影收拢换到新贴边侧重新锚定，从当前宽度继续收拢。</summary>
    internal void ReanchorGhostCollapse(bool anchorRight, TimeSpan duration)
    {
        if (double.IsNaN(MemoHost.Width))
        {
            BeginGhostCollapse(anchorRight, duration);
            return;
        }

        CancelGhostCollapse();
        AnchorMemoHost(anchorRight);
        StartGhostCollapse(MemoHost.ActualWidth, duration);
    }

    /// <summary>清除残影收拢：恢复内容随窗口拉伸填充（退出预览/脱离贴边时）。</summary>
    internal void ClearGhostCollapse()
    {
        CancelGhostCollapse();
        MemoHost.Width = double.NaN;
        MemoHost.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        MemoHost.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
    }

    private void AnchorMemoHost(bool anchorRight)
    {
        MemoHost.HorizontalAlignment = anchorRight
            ? System.Windows.HorizontalAlignment.Right
            : System.Windows.HorizontalAlignment.Left;
        MemoHost.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
    }

    private void StartGhostCollapse(double from, TimeSpan duration)
    {
        double to = MemoPopoutDockOptions.CollapsedWidth;
        FrameAnimation animation = new(_animationFrames);
        _ghostCollapseAnimation = animation;
        animation.Start(
            duration,
            MotionEasing.CubicEaseOut,
            progress => MemoHost.Width = from + ((to - from) * progress),
            () =>
            {
                MemoHost.Width = to;
                if (ReferenceEquals(_ghostCollapseAnimation, animation))
                {
                    _ghostCollapseAnimation = null;
                    animation.Dispose();
                }
            });
    }

    private void CancelGhostCollapse()
    {
        _ghostCollapseAnimation?.Cancel();
        _ghostCollapseAnimation?.Dispose();
        _ghostCollapseAnimation = null;
    }

    /// <summary>贴边态抑制 resize 命中并切换外壳为标签模式（外壳停绘，D 形由标签层自绘）；浮动态恢复正常 chrome。</summary>
    internal void SetDockChromeActive(bool active)
    {
        SuppressResizeHitTest = active;
        SetCustomNativeRegionActive(active);
    }

    private void OnPinToggle(object sender, RoutedEventArgs e) => TogglePinned();

    private void OnTaskbarToggle(object sender, RoutedEventArgs e) => ToggleTaskbarIcon();

    private async void OnReminderClick(object sender, RoutedEventArgs e) => await ToggleReminderPageAsync();

    private void OnEditorSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Reserve room for toolbar padding, B/I/heading, More and Save at the 280 DIP minimum.
        TaskStatusSelector.MaxWidth = Math.Clamp(e.NewSize.Width - 164, 100, 128);
    }

    private void OnToolbarToggle(object sender, RoutedEventArgs e)
    {
        if (IsReminderPageVisible || _reminderNavigating || IsPreparingToClose) return;
        _showPreviewToolbar = !_showPreviewToolbar;
        MarkdownEditor.HideToolbarInPreview = !_showPreviewToolbar;
        UpdateToolbarButtonVisual();
    }

    private async void OnCloseClick(object sender, RoutedEventArgs e) => await RequestCloseAsync();

    private async void OnWindowPreviewKeyDown(object sender, InputKeyEventArgs e)
    {
        if (HandleReminderKey(e) || e.Handled || IsPreparingToClose || e.Key != Key.Escape || _sourceDeleted)
        {
            return;
        }

        await MarkdownEditor.CancelEditingAsync();
        e.Handled = true;
    }

    private void UpdateMemoVisuals()
    {
        if (_memo is null)
        {
            return;
        }

        string title = IsQuickAddDraft ? LocalizationService.Get("快速添加")
            : string.IsNullOrWhiteSpace(_memo.Title) ? LocalizationService.Get("备忘录") : _memo.Title;
        Title = title;
        TitleText.Text = title;
        DockTabLayer.TabTitle = title;
        RelativeTimeText.Text = _memo.RelativeTime;
        FullTimeText.Text = _memo.FullTime;
        UpdateReminderButtonVisual();
        UpdateToolbarButtonVisual();
    }

    private void UpdateToolbarButtonVisual()
    {
        ToolbarButton.IsEnabled = !IsReminderPageVisible && !_reminderNavigating && !IsPreparingToClose;
        string description = LocalizationService.Get(_showPreviewToolbar ? "隐藏 Markdown 工具栏" : "显示 Markdown 工具栏");
        ToolbarButton.ToolTip = description;
        AutomationProperties.SetName(ToolbarButton, description);
        InteractionState.SetIsPinActive(ToolbarButton, _showPreviewToolbar);
    }

    private void UpdateTaskbarButtonVisual()
    {
        TaskbarButton.Visibility = _taskbarButtonEnabled ? Visibility.Visible : Visibility.Collapsed;
        TaskbarIconVisibility.SetVisible(this, _taskbarButtonEnabled && _showTaskbarIcon);
        bool isVisible = TaskbarIconVisibility.IsVisible(this);
        string description = LocalizationService.Get(isVisible ? "从任务栏隐藏此便签" : "在任务栏显示此便签");
        TaskbarButton.ToolTip = description;
        AutomationProperties.SetName(TaskbarButton, description);
        InteractionState.SetIsPinActive(TaskbarButton, isVisible);
    }

    private void OnTimeClick(object sender, MouseButtonEventArgs e)
    {
        _showFullTime = !_showFullTime;
        _timeAnimation?.Cancel();
        _timeAnimation?.Dispose();
        _timeAnimation = new FrameAnimation(_animationFrames);
        double fromRelative = RelativeTimeText.Opacity;
        double fromFull = FullTimeText.Opacity;
        double toRelative = _showFullTime ? 0 : 1;
        double toFull = _showFullTime ? 1 : 0;
        _timeAnimation.Start(
            TimeSpan.FromMilliseconds(190),
            MotionEasing.CubicEaseOut,
            progress =>
            {
                RelativeTimeText.Opacity = fromRelative + ((toRelative - fromRelative) * progress);
                FullTimeText.Opacity = fromFull + ((toFull - fromFull) * progress);
            });
        e.Handled = true;
    }

    private void ReleaseEditorOwnership()
    {
        if (_memo is not null)
        {
            MemoEditCoordinator.Shared.Release(_memo.Id, this);
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        UpdateMemoVisuals();
        UpdateTaskbarButtonVisual();
    }

    private void OnPaletteChanged(object? sender, EventArgs e) => ApplyColors();

    private void ApplyColors()
    {
        MemoWindowPalette.Apply(_memoPalette, _backgroundColor);
        ShellBackground = EditorHost.TryFindResource("BgPrimaryBrush") as System.Windows.Media.Brush;
        ShellBorderBrush = EditorHost.TryFindResource("BorderDefaultBrush") as System.Windows.Media.Brush;
        MarkdownEditor.RefreshAppearance();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        ThemePreferences.PaletteChanged -= OnPaletteChanged;
        if (_cleanupDone)
        {
            return;
        }

        _cleanupDone = true;
        MotionAnimations.Cancel(PinIcon);
        DisposeReminderNavigation();
        _timeAnimation?.Cancel();
        _timeAnimation?.Dispose();
        _timeAnimation = null;
        _tabVisualAnimation?.Cancel();
        _tabVisualAnimation?.Dispose();
        _tabVisualAnimation = null;
        _dockController?.Dispose();
        _dockController = null;
        ReleaseEditorOwnership();
        MarkdownEditor.EditingCompleted -= OnEditingCompleted;
        if (_memo is not null)
        {
            _memo.PropertyChanged -= OnMemoPropertyChanged;
        }

        MarkdownEditor.Dispose();
    }
}
