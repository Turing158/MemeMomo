using System.ComponentModel;
using MemeMomo.Models;
using MemeMomo.Services;
using MemeMomo.Utils;

namespace MemeMomo.ViewModels;

/// <summary>One reminder editing session. Draft values never publish directly to the shared memo.</summary>
internal sealed class ReminderViewModel : IDisposable
{
    private readonly Func<MemoItem, DateTime?, Task> _save;
    private readonly long _lifecycleVersion;
    private bool _disposed;

    internal ReminderViewModel(MemoItem memo, Func<MemoItem, DateTime?, Task> save)
    {
        Memo = memo;
        _save = save;
        _lifecycleVersion = memo.LifecycleVersion;
        ResetDraft();
        memo.PropertyChanged += OnMemoChanged;
    }

    internal MemoItem Memo { get; }
    internal bool IsActive { get; private set; } = true;
    internal bool IsBusy { get; private set; }
    internal bool HasDraftChanges { get; private set; }
    internal bool HasSubmissionFailure { get; private set; }
    internal bool CanSubmit => IsActive && !IsBusy && IsModelValid && Memo.CanRemind;
    internal bool HasSavedReminder => Memo.ReminderAt is { } value && value > DateTimeUtils.Now;
    internal DateTime? SelectedDate { get; private set; }
    internal TimeSpan SelectedTime { get; private set; }
    internal DateTime? SelectedReminderAt => SelectedDate is { } date
        ? new DateTime(date.Year, date.Month, date.Day, SelectedTime.Hours, SelectedTime.Minutes, SelectedTime.Seconds, DateTimeKind.Local)
        : null;
    internal string ErrorMessage { get; private set; } = string.Empty;
    internal Task<bool> CurrentSubmission { get; private set; } = Task.FromResult(true);
    private bool IsModelValid => !Memo.IsDeleted && !Memo.IsTaskClosed && Memo.LifecycleVersion == _lifecycleVersion;

    internal event EventHandler? Changed;
    internal event EventHandler? DraftChanged;
    internal event EventHandler? Saved;
    internal event Action<string>? Invalidated;

    internal void UpdateDraft(DateTime? date, TimeSpan time)
    {
        if (!IsActive || IsBusy || (SelectedDate == date?.Date && SelectedTime == time)) return;
        SelectedDate = date?.Date;
        SelectedTime = new TimeSpan(time.Hours, time.Minutes, time.Seconds);
        HasDraftChanges = true;
        ErrorMessage = string.Empty;
        DraftChanged?.Invoke(this, EventArgs.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void ApplyQuickOffset(TimeSpan offset)
    {
        DateTime value = DateTimeUtils.Now.Add(offset);
        UpdateDraft(value.Date, new TimeSpan(value.Hour, value.Minute, value.Second));
    }

    internal Task<bool> ScheduleAsync()
    {
        RefreshModel();
        if (!CanSubmit) return Task.FromResult(false);
        if (SelectedReminderAt is not { } selected) return Reject("请选择有效的提醒日期。");
        if (selected <= DateTimeUtils.Now.AddSeconds(1)) return Reject("提醒时间需要晚于当前时间。");
        return Submit(selected, "设置提醒失败");
    }

    internal Task<bool> ClearReminderAsync()
    {
        RefreshModel();
        return CanSubmit ? Submit(null, "取消提醒失败") : Task.FromResult(false);
    }

    private Task<bool> Reject(string message)
    {
        ErrorMessage = LocalizationService.Get(message);
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(false);
    }

    private Task<bool> Submit(DateTime? reminderAt, string errorTitle)
    {
        IsBusy = true;
        ErrorMessage = string.Empty;
        CurrentSubmission = SubmitCoreAsync(reminderAt, errorTitle);
        Changed?.Invoke(this, EventArgs.Empty);
        return CurrentSubmission;
    }

    private async Task<bool> SubmitCoreAsync(DateTime? reminderAt, string errorTitle)
    {
        // Register the task before a synchronous persistence callback can request a return or close.
        await Task.Yield();
        try
        {
            if (!IsActive || !IsModelValid) return false;
            if (!Memo.CanRemind) throw new InvalidOperationException(LocalizationService.Get("正在保存，请稍后重试。"));
            await _save(Memo, reminderAt);
            HasSubmissionFailure = false;
            IsBusy = false;
            if (IsActive && IsModelValid)
            {
                Changed?.Invoke(this, EventArgs.Empty);
                Saved?.Invoke(this, EventArgs.Empty);
            }
            return true;
        }
        catch (Exception exception)
        {
            HasSubmissionFailure = true;
            if (IsActive && IsModelValid)
                ErrorMessage = LocalizationService.Format("{0}：{1}", LocalizationService.Get(errorTitle), exception.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
            if (IsActive) Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void RefreshModel()
    {
        if (!IsActive) return;
        if (!IsModelValid)
        {
            IsActive = false;
            Memo.PropertyChanged -= OnMemoChanged;
            string reason = Memo.IsTaskClosed ? "重新打开后可设置提醒" : "备忘录已变更，请重新打开。";
            Invalidated?.Invoke(LocalizationService.Get(reason));
            return;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnMemoChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MemoItem.ReminderAt) && !HasDraftChanges && !IsBusy && IsModelValid)
        {
            ResetDraft();
            DraftChanged?.Invoke(this, EventArgs.Empty);
        }
        RefreshModel();
    }

    private void ResetDraft()
    {
        DateTime value = HasSavedReminder ? Memo.ReminderAt!.Value : DateTimeUtils.Now.AddMinutes(10);
        if (value.Kind == DateTimeKind.Utc) value = value.ToLocalTime();
        SelectedDate = value.Date;
        SelectedTime = new TimeSpan(value.Hour, value.Minute, value.Second);
        HasDraftChanges = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsActive = false;
        Memo.PropertyChanged -= OnMemoChanged;
    }
}
