using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MemeMomo.Components;
using MemeMomo.Models;
using MemeMomo.Services;
using MemeMomo.UI;
using MemeMomo.UI.Animation;
using MemeMomo.UI.Notifications;
using MemeMomo.UI.Popup;
using MemeMomo.Utils;
using MemeMomo.Views;

namespace MemeMomo;

public partial class MemoPopoutWindow
{
    private Func<MemoItem, DateTime?, Task>? _saveReminder;
    private ReminderView? _reminderView;
    private PageTransitionController? _reminderTransition;
    private NotificationFeedback? _reminderAvailabilityFeedback;
    private DispatcherOperation? _reminderFocusOperation;
    private IInputElement? _reminderReturnFocus;
    private MarkdownEditor.ViewPosition _reminderEditorPosition;
    private long _reminderEditorSession;
    private int _reminderNavigationVersion;
    private bool _reminderNavigating;
    private bool _reminderDockSuspended;

    public bool IsReminderPageVisible { get; private set; }
    internal ReminderView? ReminderPage => _reminderView;
    private bool CanFocusReminderBody => !_cleanupDone && !_sourceDeleted && !IsPreparingToClose
        && IsVisible && WindowState != WindowState.Minimized && !_reminderDockSuspended
        && BodyHost.IsVisible && BodyHost.IsHitTestVisible;

    private void InitializeReminderNavigation()
    {
        IsVisibleChanged += OnReminderHostVisibilityChanged;
        StateChanged += OnReminderHostStateChanged;
        BodyHost.IsVisibleChanged += OnReminderHostVisibilityChanged;
        BodyHost.IsHitTestVisibleChanged += OnReminderHostVisibilityChanged;
    }

    internal void ConfigureReminderSaving(Func<MemoItem, DateTime?, Task> save)
    {
        _saveReminder = save;
        UpdateReminderButtonVisual();
    }

    internal Task<bool> ToggleReminderPageAsync() => IsReminderPageVisible
        ? Task.FromResult(ReturnFromReminder()) : ShowReminderAsync();

    internal async Task<bool> ShowReminderAsync()
    {
        if (IsReminderPageVisible) return true;
        if (_reminderNavigating || IsPreparingToClose || !CanFocusReminderBody || IsQuickAddDraft
            || _memo?.CanRemind != true || _saveReminder is null) return false;
        _reminderNavigating = true;
        int version = ++_reminderNavigationVersion;
        try
        {
            _reminderReturnFocus = Keyboard.FocusedElement is DependencyObject focus && EditorHost.IsAncestorOf(focus)
                ? Keyboard.FocusedElement : MarkdownEditor.Adapter.Editor.TextArea;
            MarkdownEditor.ClosePageMenus();
            CloseMemoContextMenus(EditorHost);
            if (TaskStatusSelector.StatusMenu is { } statusMenu)
                ContextMenuAnimations.Close(statusMenu, immediate: true);
            if (Mouse.Captured is DependencyObject captured && EditorHost.IsAncestorOf(captured)) Mouse.Capture(null);
            // Commit IME input by moving focus while the editor is still writable.
            ReminderButton.Focus();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            if (version != _reminderNavigationVersion || !CanFocusReminderBody || _memo.CanRemind != true) return false;
            _reminderEditorSession = MarkdownEditor.SessionVersion;
            _reminderEditorPosition = MarkdownEditor.CaptureViewPosition();
            _reminderView ??= CreateReminderView();
            _reminderTransition ??= new PageTransitionController(PageViewport, PageSurface, PageTransitionSnapshot, _animationFrames);
            _reminderAvailabilityFeedback?.Clear();
            _reminderTransition.CaptureCurrentPage();
            IsReminderPageVisible = true;
            EditorHost.IsEnabled = EditorHost.IsHitTestVisible = false;
            EditorHost.Visibility = Visibility.Hidden;
            ReminderHost.Visibility = Visibility.Visible;
            _reminderView.BeginSession(_memo, _saveReminder);
            PageSurface.UpdateLayout();
            _reminderView.ResumeInteraction();
            _reminderTransition.Start(forward: true);
            QueueReminderFocus(version, entering: true);
            return true;
        }
        finally
        {
            _reminderNavigating = false;
            UpdateReminderButtonVisual();
            UpdateToolbarButtonVisual();
        }
    }

    private ReminderView CreateReminderView()
    {
        var view = new ReminderView
        {
            ReturnRequested = completed => ReturnFromReminder(completed),
            SessionInvalidated = reason =>
            {
                ReturnFromReminder(force: true);
                if (!_cleanupDone && !_sourceDeleted)
                    (_reminderAvailabilityFeedback ??= new NotificationFeedback(this)).Show(reason);
            }
        };
        view.StateChanged += OnReminderStateChanged;
        ReminderHost.Content = view;
        return view;
    }

    private static void CloseMemoContextMenus(DependencyObject root)
    {
        if (root is FrameworkElement { ContextMenu: { } menu }) ContextMenuAnimations.Close(menu, immediate: true);
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            CloseMemoContextMenus(VisualTreeHelper.GetChild(root, i));
    }

    internal bool ReturnFromReminder(bool force = false)
    {
        if (_cleanupDone || _sourceDeleted || !IsReminderPageVisible) return false;
        if (!force && (_reminderNavigating || IsPreparingToClose || _reminderView?.IsBusy == true)) return false;
        int version = ++_reminderNavigationVersion;
        _reminderFocusOperation?.Abort();
        if (CanFocusReminderBody) _reminderTransition?.CaptureCurrentPage();
        else _reminderTransition?.Reset();
        _reminderView?.EndSession();
        IsReminderPageVisible = false;
        ReminderHost.Visibility = Visibility.Collapsed;
        EditorHost.Visibility = Visibility.Visible;
        EditorHost.IsEnabled = EditorHost.IsHitTestVisible = true;
        PageSurface.UpdateLayout();
        UpdateReminderButtonVisual();
        UpdateToolbarButtonVisual();
        if (CanFocusReminderBody)
        {
            _reminderTransition?.Start(forward: false);
            QueueReminderFocus(version, entering: false);
        }
        return true;
    }

    private void QueueReminderFocus(int version, bool entering)
    {
        _reminderFocusOperation?.Abort();
        _reminderFocusOperation = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _reminderFocusOperation = null;
            if (version != _reminderNavigationVersion || !CanFocusReminderBody || IsReminderPageVisible != entering) return;
            if (entering)
            {
                _reminderView?.DateFieldPart.FieldPart.Focus();
                return;
            }
            if (_reminderReturnFocus is UIElement { IsVisible: true, IsEnabled: true } focus) Keyboard.Focus(focus);
            else MarkdownEditor.Adapter.FocusEditor();
            if (MarkdownEditor.SessionVersion == _reminderEditorSession)
            {
                MarkdownEditor.Adapter.SelectSourceRange(_reminderEditorPosition.Start, _reminderEditorPosition.End);
                MarkdownEditor.Adapter.Editor.ScrollToVerticalOffset(_reminderEditorPosition.VerticalOffset);
            }
            _reminderReturnFocus = null;
        }));
    }

    private void SynchronizeReminderVisibility()
    {
        if (_cleanupDone) return;
        if (!CanFocusReminderBody)
        {
            ++_reminderNavigationVersion;
            _reminderFocusOperation?.Abort();
            _reminderFocusOperation = null;
            _reminderTransition?.Reset();
            _reminderView?.SuspendInteraction();
        }
        else if (IsReminderPageVisible) _reminderView?.ResumeInteraction();
    }

    private void OnReminderHostVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => SynchronizeReminderVisibility();
    private void OnReminderHostStateChanged(object? sender, EventArgs e) => SynchronizeReminderVisibility();
    private void OnReminderStateChanged(object? sender, EventArgs e) => UpdateReminderButtonVisual();

    internal void InvalidateReminderForTask() => _reminderView?.RefreshModel();

    private bool HandleReminderKey(System.Windows.Input.KeyEventArgs e)
    {
        if (!IsReminderPageVisible && !_reminderNavigating) return false;
        if (e.Handled) return true;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_reminderView?.DismissCalendar() != true) ReturnFromReminder();
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) e.Handled = true;
        return true;
    }

    private void UpdateReminderButtonVisual()
    {
        if (_memo is null) return;
        bool busy = _reminderView?.IsBusy == true;
        bool saved = _memo.ReminderAt is { } reminderAt && reminderAt > DateTimeUtils.Now;
        ReminderButton.IsEnabled = !IsPreparingToClose && !_reminderNavigating && !busy
            && (IsReminderPageVisible || (!IsQuickAddDraft && _memo.CanRemind && _saveReminder is not null));
        InteractionState.SetIsPinActive(ReminderButton, IsReminderPageVisible);
        ReminderIndicator.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
        string description = busy ? LocalizationService.Get("正在保存…")
            : IsReminderPageVisible ? saved
                ? LocalizationService.Format("返回便签；提醒：{0:yyyy-MM-dd HH:mm:ss}", _memo.ReminderAt!.Value.ToLocalTime())
                : LocalizationService.Get("返回便签")
            : saved ? LocalizationService.Format("提醒：{0:yyyy-MM-dd HH:mm:ss}", _memo.ReminderAt!.Value.ToLocalTime())
                : LocalizationService.Get("设置提醒");
        if (!IsReminderPageVisible && _memo.IsTaskClosed) description = LocalizationService.Get("重新打开后可设置提醒");
        ReminderButton.ToolTip = description;
        AutomationProperties.SetName(ReminderButton, description);
    }

    private void DisposeReminderNavigation()
    {
        ++_reminderNavigationVersion;
        _reminderFocusOperation?.Abort();
        _reminderFocusOperation = null;
        _reminderReturnFocus = null;
        _reminderView?.Dispose();
        if (_reminderView is not null) _reminderView.StateChanged -= OnReminderStateChanged;
        _reminderTransition?.Dispose();
        _reminderAvailabilityFeedback?.Clear();
        IsVisibleChanged -= OnReminderHostVisibilityChanged;
        StateChanged -= OnReminderHostStateChanged;
        BodyHost.IsVisibleChanged -= OnReminderHostVisibilityChanged;
        BodyHost.IsHitTestVisibleChanged -= OnReminderHostVisibilityChanged;
    }
}
