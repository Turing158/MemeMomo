using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using MemeMomo.Components;
using MemeMomo.Models;
using MemeMomo.Platform.Windows;
using MemeMomo.Services;
using MemeMomo.UI;
using MemeMomo.Utils;
using System;
using System.ComponentModel;
using System.Threading.Tasks;

namespace MemeMomo.Views;

public partial class MemoPopoutWindow : Window {
    private readonly WindowTransitionController _transition;
    private Func<MemoItem, string, Task>? _saveMemo;
    private MemoItem? _memo;
    private bool _isClosingAfterTransition;
    private bool _isPinned;
    private bool _isTaskbarButtonEnabled;
    private bool _showTaskbarIcon = true;
    private bool _showPreviewToolbar;
    private bool _showFullTime;
    private bool _sourceDeleted;

    /// <summary>当前窗体关联的备忘录项。</summary>
    public MemoItem Memo => _memo!;
    public event Action<MemoPopoutWindow, MemoItem>? ReminderRequested;

    public MemoPopoutWindow() {
        InitializeComponent();
        _markdownEditor.SaveRequestedAsync = SaveMarkdownAsync;
        _markdownEditor.CancelRequestedAsync = CancelMarkdownAsync;
        _markdownEditor.EditingCompleted += (_, _) => UpdateToolbarButtonVisual();

        Loaded += (_, _) => this.AssignResizeCursors();
        _transition = new WindowTransitionController(this, this.FindControl<Border>("_popoutShell")!);
        _transition.PrepareOpen();
        Opened += async (_, _) => { _transition.PlayOpen(); await BeginEditAsync(); };
        Closed += OnWindowClosed;
    }

    public MemoPopoutWindow(
        MemoItem memo,
        PixelPoint position,
        Func<MemoItem, string, Task> saveMemo) : this() {
        _memo = memo;
        _saveMemo = saveMemo;
        DataContext = memo;
        UpdateTitle(memo);
        Position = position;
        _markdownEditor.ShowExistingPreview(memo.Content);
        memo.PropertyChanged += OnMemoPropertyChanged;
        UpdateReminderButtonVisual();
    }

    public bool IsPinned => _isPinned;

    internal void ApplySettings(AppSettings settings) {
        _isTaskbarButtonEnabled = settings.ShowMemoWindowTaskbarIcon;
        UpdateTaskbarButtonVisual();
    }

    public void TogglePinned() => SetPinned(!_isPinned);

    public void ToggleTaskbarIcon() {
        if (!_isTaskbarButtonEnabled) return;
        _showTaskbarIcon = !_showTaskbarIcon;
        UpdateTaskbarButtonVisual();
    }

    public void SetPinned(bool isPinned) {
        _isPinned = isPinned;
        Topmost = _isPinned;
        UpdatePinButtonVisual();
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (TitleBarDragHelper.CanStartDrag(this, e))
            BeginMoveDrag(e);
    }

    private void OnResizeHandlePointerPressed(object? sender, PointerPressedEventArgs e) {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (sender is not Border handle || handle.Tag is not string tag) return;
        var edge = tag switch {
            "Top"         => WindowEdge.North,
            "Bottom"      => WindowEdge.South,
            "Left"        => WindowEdge.West,
            "Right"       => WindowEdge.East,
            "TopLeft"     => WindowEdge.NorthWest,
            "TopRight"    => WindowEdge.NorthEast,
            "BottomLeft"  => WindowEdge.SouthWest,
            "BottomRight" => WindowEdge.SouthEast,
            _             => WindowEdge.North,
        };
        BeginResizeDrag(edge, e);
    }

    private void OnPinToggle(object? sender, RoutedEventArgs e) => TogglePinned();

    private void OnTaskbarToggle(object? sender, RoutedEventArgs e) => ToggleTaskbarIcon();

    private void OnReminderClick(object? sender, RoutedEventArgs e) {
        if (_memo != null) ReminderRequested?.Invoke(this, _memo);
    }

    private void OnToolbarToggle(object? sender, RoutedEventArgs e) {
        _showPreviewToolbar = !_showPreviewToolbar;
        _markdownEditor.HideToolbarInPreview = !_showPreviewToolbar;
        UpdateToolbarButtonVisual();
    }

    private async void OnCloseClick(object? sender, RoutedEventArgs e) {
        if (!await _markdownEditor.CompleteEditingAsync()) return;
        if (_memo != null) MemoEditCoordinator.Shared.Release(_memo.Id, this);
        CloseWithTransition();
    }

    private async Task BeginEditAsync() {
        if (_sourceDeleted || _memo == null || MemoEditCoordinator.Shared.IsOwner(_memo.Id, this)) return;
        if (!await MemoEditCoordinator.Shared.AcquireAsync(_memo.Id, this, RelinquishEditorAsync))
            return;
        _markdownEditor.BeginExistingEdit(_memo.Content);
        UpdateToolbarButtonVisual();
    }

    private async Task<bool> RelinquishEditorAsync() {
        if (_sourceDeleted) return true;
        if (!await _markdownEditor.CompleteEditingAsync()) return false;
        if (_memo != null) MemoEditCoordinator.Shared.Release(_memo.Id, this);
        return true;
    }

    private async Task<bool> SaveMarkdownAsync(MarkdownSaveRequest request) {
        if (_sourceDeleted || _memo == null || _saveMemo == null || request.IsNewMemo) return false;
        await _saveMemo(_memo, request.Markdown);
        UpdateTitle(_memo);
        return true;
    }

    private async Task CancelMarkdownAsync(string restoreMarkdown) {
        if (_sourceDeleted || _memo == null || _saveMemo == null) return;
        await _saveMemo(_memo, restoreMarkdown);
        UpdateTitle(_memo);
    }

    private void OnMemoPropertyChanged(object? sender, PropertyChangedEventArgs e) {
        if (_memo == null) return;
        if (e.PropertyName is nameof(MemoItem.Content) or nameof(MemoItem.Title)) {
            UpdateTitle(_memo);
            _markdownEditor.SetExternalMarkdown(_memo.Content);
        }
        if (e.PropertyName == nameof(MemoItem.ReminderAt)) UpdateReminderButtonVisual();
    }

    private void UpdateTitle(MemoItem memo) {
        var title = string.IsNullOrWhiteSpace(memo.Title) ? "MemeMomo" : memo.Title;
        Title = title;
        this.FindControl<TextBlock>("_titleText")!.Text = title;
    }

    private void CloseWithTransition() {
        if (_isClosingAfterTransition) return;
        _isClosingAfterTransition = true;
        _transition.CloseAfterTransition(() => Close());
    }

    private void UpdatePinButtonVisual() {
        var button = this.FindControl<Button>("_pinButton");
        if (button == null) return;

        button.Classes.Set("PinActive", _isPinned);
        if (button.Content is PathIcon pi) {
            var target = _isPinned ? -45 : 0;
            MotionAnimations.AnimateRotation(pi, this, target, animate: IsVisible);
        }
    }

    private void UpdateTaskbarButtonVisual() {
        var button = this.FindControl<Button>("_taskbarButton");
        if (button == null) return;

        button.IsVisible = _isTaskbarButtonEnabled;
        var taskbarIconVisible = _isTaskbarButtonEnabled && _showTaskbarIcon;
        TaskbarIconVisibility.SetVisible(this, taskbarIconVisible);
        button.Classes.Set("PinActive", taskbarIconVisible);

        var description = taskbarIconVisible
            ? "从任务栏隐藏此便签"
            : "在任务栏显示此便签";
        ToolTip.SetTip(button, description);
        AutomationProperties.SetName(button, description);
    }

    private void UpdateReminderButtonVisual() {
        var button = this.FindControl<Button>("_reminderButton");
        if (button == null) return;

        var reminderAt = _memo?.ReminderAt;
        var isActive = reminderAt > DateTimeUtils.Now;
        button.Classes.Set("PinActive", isActive);
        var description = isActive
            ? $"提醒：{reminderAt:yyyy-MM-dd HH:mm:ss}"
            : "设置提醒";
        ToolTip.SetTip(button, description);
        AutomationProperties.SetName(button, description);
    }

    internal void CloseBecauseSourceDeleted() {
        if (_sourceDeleted) return;
        _sourceDeleted = true;
        _markdownEditor.AbortForSourceDeletion();
        if (_memo != null) MemoEditCoordinator.Shared.Release(_memo.Id, this);
        _transition.Cancel();
        Close();
    }

    private void UpdateToolbarButtonVisual() {
        var button = this.FindControl<Button>("_toolbarButton");
        if (button == null) return;

        button.IsVisible = true;
        button.Classes.Set("PinActive", _showPreviewToolbar);
        ToolTip.SetTip(button, _showPreviewToolbar
            ? "隐藏 Markdown 工具栏"
            : "显示 Markdown 工具栏");
    }

    private void OnTimeTapped(object? sender, TappedEventArgs e) {
        _showFullTime = !_showFullTime;
        var full = this.FindControl<TextBlock>("_timeFullText")!;
        var shortText = this.FindControl<TextBlock>("_timeShortText")!;
        var fromFull = full.Opacity;
        var fromShort = shortText.Opacity;
        var toFull = _showFullTime ? 1 : 0;
        var toShort = _showFullTime ? 0 : 1;
        MotionAnimations.Start(full, this, MotionPreferences.StandardDuration, new CubicEaseOut(), progress => {
            full.Opacity = MotionAnimations.Lerp(fromFull, toFull, progress);
            shortText.Opacity = MotionAnimations.Lerp(fromShort, toShort, progress);
        });
        e.Handled = true;
    }

    private void OnWindowClosed(object? sender, EventArgs e) {
        _transition.Cancel();
        if (_memo != null) {
            _memo.PropertyChanged -= OnMemoPropertyChanged;
            MemoEditCoordinator.Shared.Release(_memo.Id, this);
        }
        if (this.FindControl<Button>("_pinButton")?.Content is PathIcon pinIcon)
            MotionAnimations.Cancel(pinIcon);
        var full = this.FindControl<TextBlock>("_timeFullText");
        if (full != null) MotionAnimations.Cancel(full);
    }
}
