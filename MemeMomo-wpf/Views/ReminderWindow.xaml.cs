using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MemeMomo.Components;
using MemeMomo.Models;
using MemeMomo.UI.Windows;
using MemeMomo.Utils;

namespace MemeMomo.Views;

using WpfButton = System.Windows.Controls.Button;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

/// <summary>Schedules one memo reminder; the application owns the timer and persistence.</summary>
public partial class ReminderWindow : BorderlessWindow
{
    private MemoItem? _memo;
    private Func<MemoItem, DateTime?, Task> _saveReminderAsync = static (_, _) => Task.CompletedTask;
    private bool _busy;
    private bool _closeRequested;

    public ReminderWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        TimeWheel.SelectedTimeChanged += OnTimeChanged;
        SetSelectedDateTime(DateTimeUtils.Now.AddMinutes(10));
    }

    public ReminderWindow(MemoItem memo, Func<MemoItem, DateTime?, Task> saveReminderAsync)
        : this()
    {
        ArgumentNullException.ThrowIfNull(memo);
        ArgumentNullException.ThrowIfNull(saveReminderAsync);
        _memo = memo;
        _saveReminderAsync = saveReminderAsync;
        MemoTitleText.Text = string.IsNullOrWhiteSpace(memo.Title) ? "备忘录" : memo.Title;

        if (memo.ReminderAt is { } reminderAt && reminderAt > DateTimeUtils.Now)
        {
            SetSelectedDateTime(reminderAt);
            CurrentReminderText.Text = $"当前提醒：{reminderAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            CurrentReminderText.Visibility = Visibility.Visible;
            CancelReminderButton.Visibility = Visibility.Visible;
        }
    }

    public MemoItem? Memo => _memo;
    public bool IsBusy => _busy;
    public string ErrorMessage => ErrorText.Text;
    internal DateTime? SelectedReminderAt => TryGetSelectedDateTime(out DateTime value) ? value : null;
    internal DateFieldSelector DateFieldPart => DateField;
    internal TimeWheelSelector TimeWheelPart => TimeWheel;
    internal void CloseImmediatelyForTest() => CloseImmediately();

    public void ApplyQuickOffset(TimeSpan offset) => SetSelectedDateTime(DateTimeUtils.Now.Add(offset));

    internal async Task<bool> ScheduleAsync() => await SaveAndCloseAsync(TryGetSelectedDateTime(out DateTime value) ? value : null, "设置提醒失败", requireFuture: true);
    internal async Task<bool> CancelReminderAsync() => await SaveAndCloseAsync(null, "取消提醒失败", requireFuture: false);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Owner is not null)
        {
            Owner.Closing += OnOwnerClosing;
            Owner.Closed += OnOwnerClosed;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (Owner is not null)
        {
            Owner.Closing -= OnOwnerClosing;
            Owner.Closed -= OnOwnerClosed;
        }
    }

    private void OnOwnerClosing(object? sender, CancelEventArgs e)
    {
        if (_closeRequested)
        {
            return;
        }

        _closeRequested = true;
        CloseImmediately();
    }

    private void OnOwnerClosed(object? sender, EventArgs e)
    {
        // The owner may close while persistence is still in flight. Close the
        // child deterministically, but leave the save task running so the
        // memo state is not abandoned halfway through an async operation.
        if (!_closeRequested)
        {
            _closeRequested = true;
            CloseImmediately();
        }
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
            e.Handled = true;
        }
    }

    private void OnWindowPreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_busy)
        {
            e.Handled = true;
            CloseWithTransition();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (!_busy) CloseWithTransition();
    }

    private void OnQuickTimeClick(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton { Tag: string secondsText } && int.TryParse(secondsText, out int seconds))
        {
            ApplyQuickOffset(TimeSpan.FromSeconds(seconds));
            ErrorText.Text = string.Empty;
        }
    }

    private async void OnScheduleClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _memo is null)
        {
            return;
        }

        if (!TryGetSelectedDateTime(out DateTime reminderAt))
        {
            ShowError("请选择有效的提醒日期。");
            return;
        }

        if (reminderAt <= DateTimeUtils.Now.AddSeconds(1))
        {
            ShowError("提醒时间需要晚于当前时间。");
            return;
        }

        await SaveAndCloseAsync(reminderAt, "设置提醒失败", requireFuture: false);
    }

    private async void OnCancelReminderClick(object sender, RoutedEventArgs e)
    {
        if (!_busy && _memo is not null)
        {
            await SaveAndCloseAsync(null, "取消提醒失败", requireFuture: false);
        }
    }

    private async Task<bool> SaveAndCloseAsync(DateTime? reminderAt, string errorTitle, bool requireFuture)
    {
        if (_memo is null || _busy)
        {
            return false;
        }

        if (requireFuture && (reminderAt is null || reminderAt <= DateTimeUtils.Now.AddSeconds(1)))
        {
            ShowError("提醒时间需要晚于当前时间。");
            return false;
        }

        SetBusy(true);
        try
        {
            DateTime? local = reminderAt is { } value
                ? DateTime.SpecifyKind(value.ToLocalTime(), DateTimeKind.Local)
                : null;
            await _saveReminderAsync(_memo, local);
            _closeRequested = true;
            if (IsVisible)
            {
                CloseWithTransition();
            }
            return true;
        }
        catch (Exception ex)
        {
            // An owner/application shutdown can close this window while the
            // callback is running. Do not touch a closed visual tree in that
            // path; the operation has still completed deterministically.
            if (IsVisible)
            {
                ShowError($"{errorTitle}：{ex.Message}");
                SetBusy(false);
            }
            return false;
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ScheduleButton.IsEnabled = !busy;
        CancelReminderButton.IsEnabled = !busy;
        ScheduleButton.Content = busy ? "正在保存…" : "设置提醒";
    }

    private void OnSelectedDateChanged(object? sender, DateSelectionChangedEventArgs e)
    {
        if (DateField.SelectedDate is not null)
        {
            ErrorText.Text = string.Empty;
        }
    }

    private void OnTimeChanged(object? sender, EventArgs e) { }

    private bool TryGetSelectedDateTime(out DateTime value)
    {
        if (DateField.SelectedDate is not DateTime date)
        {
            value = default;
            return false;
        }

        TimeSpan time = TimeWheel.SelectedTime;
        value = new DateTime(date.Year, date.Month, date.Day, time.Hours, time.Minutes, time.Seconds, DateTimeKind.Local);
        return true;
    }

    private void SetSelectedDateTime(DateTime value)
    {
        DateTime local = value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
        local = DateTime.SpecifyKind(local, DateTimeKind.Local);
        DateField.SelectedDate = local.Date;
        TimeWheel.SetTime(local.TimeOfDay);
    }

    private void ShowError(string message) => ErrorText.Text = message;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_busy && !IsCloseApproved)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }
}
