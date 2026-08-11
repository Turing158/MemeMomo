using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Memo.Models;
using Memo.UI;
using Memo.Utils;
using System;
using System.Threading.Tasks;

namespace Memo.Views;

public partial class ReminderWindow : Window {
    private readonly WindowTransitionController _transition;
    private MemoItem? _memo;
    private Func<MemoItem, DateTime?, Task> _saveReminderAsync = (_, _) => Task.CompletedTask;
    private bool _isClosingAfterTransition;
    private bool _isBusy;
    private bool _dateInputIsValid = true;

    public ReminderWindow() {
        InitializeComponent();
        _transition = new WindowTransitionController(this, _reminderShell);
        _transition.PrepareOpen();
        Opened += (_, _) => _transition.PlayOpen();
        Closed += (_, _) => _transition.Cancel();
        KeyDown += OnWindowKeyDown;
        SetSelectedDateTime(DateTimeUtils.Now.AddMinutes(10));
    }

    public ReminderWindow(MemoItem memo, Func<MemoItem, DateTime?, Task> saveReminderAsync)
        : this() {
        _memo = memo;
        _saveReminderAsync = saveReminderAsync;
        _memoTitleText.Text = string.IsNullOrWhiteSpace(memo.Title) ? "备忘录" : memo.Title;

        if (memo.ReminderAt is { } reminderAt && reminderAt > DateTimeUtils.Now) {
            SetSelectedDateTime(reminderAt);
            _currentReminderText.Text = $"当前提醒：{reminderAt:yyyy-MM-dd HH:mm:ss}";
            _currentReminderText.IsVisible = true;
            _cancelReminderButton.IsVisible = true;
        }
    }

    internal DateTime? SelectedReminderAt => TryGetSelectedDateTime(out var value) ? value : null;

    internal void ApplyQuickOffset(TimeSpan offset) =>
        SetSelectedDateTime(DateTimeUtils.Now.Add(offset));

    private void OnWindowKeyDown(object? sender, KeyEventArgs e) {
        if (e.Key != Key.Escape || _isBusy) return;
        e.Handled = true;
        CloseWithTransition();
    }

    private void OnQuickTimeClick(object? sender, RoutedEventArgs e) {
        if (sender is not Button { Tag: string value } || !int.TryParse(value, out var seconds)) return;
        ApplyQuickOffset(TimeSpan.FromSeconds(seconds));
        _errorText.Text = string.Empty;
    }

    private async void OnScheduleClick(object? sender, RoutedEventArgs e) {
        if (_memo == null || _isBusy) return;
        if (!TryGetSelectedDateTime(out var reminderAt)) {
            ShowError("请选择有效的提醒日期。");
            return;
        }
        if (reminderAt <= DateTimeUtils.Now.AddSeconds(1)) {
            ShowError("提醒时间需要晚于当前时间。");
            return;
        }

        await SaveAndCloseAsync(reminderAt, "设置提醒失败");
    }

    private async void OnCancelReminderClick(object? sender, RoutedEventArgs e) {
        if (_memo == null || _isBusy) return;
        await SaveAndCloseAsync(null, "取消提醒失败");
    }

    private async Task SaveAndCloseAsync(DateTime? reminderAt, string errorTitle) {
        SetBusy(true);
        try {
            await _saveReminderAsync(_memo!, reminderAt);
            CloseWithTransition();
        }
        catch (Exception ex) {
            ShowError($"{errorTitle}：{ex.Message}");
            SetBusy(false);
        }
    }

    private void OnDateValidationError(object? sender, CalendarDatePickerDateValidationErrorEventArgs e) {
        e.ThrowException = false;
        _dateInputIsValid = false;
        ShowError("日期格式无效，请输入 yyyy-MM-dd 或从日历选择。");
    }

    private void OnSelectedDateChanged(object? sender, SelectionChangedEventArgs e) {
        _dateInputIsValid = true;
        _errorText.Text = string.Empty;
    }

    private bool TryGetSelectedDateTime(out DateTime value) {
        if (!_dateInputIsValid || _datePicker.SelectedDate is not { } selectedDate) {
            value = default;
            return false;
        }

        var date = selectedDate.Date;
        var time = _timeWheel.SelectedTime;
        value = new DateTime(
            date.Year, date.Month, date.Day,
            time.Hours, time.Minutes, time.Seconds,
            DateTimeKind.Local);
        return true;
    }

    private void SetSelectedDateTime(DateTime value) {
        var local = value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
        local = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, local.Second, DateTimeKind.Local);
        _dateInputIsValid = true;
        _datePicker.SelectedDate = local.Date;
        _timeWheel.SetTime(local.TimeOfDay);
    }

    private void SetBusy(bool value) {
        _isBusy = value;
        _scheduleButton.IsEnabled = !value;
        _cancelReminderButton.IsEnabled = !value;
        _scheduleButton.Content = value ? "正在设置…" : "设置提醒";
    }

    private void ShowError(string message) => _errorText.Text = message;

    private void OnCloseClick(object? sender, RoutedEventArgs e) {
        if (!_isBusy) CloseWithTransition();
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (TitleBarDragHelper.CanStartDrag(this, e)) {
            BeginMoveDrag(e);
        }
    }

    private void CloseWithTransition() {
        if (_isClosingAfterTransition) return;
        _isClosingAfterTransition = true;
        _transition.CloseAfterTransition(Close);
    }

    protected override void OnClosing(WindowClosingEventArgs e) {
        if (_isBusy && !_isClosingAfterTransition) {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }
}
