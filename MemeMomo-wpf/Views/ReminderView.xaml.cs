using System.Windows;
using MemeMomo.Components;
using MemeMomo.Models;
using MemeMomo.Services;
using MemeMomo.UI.Notifications;
using MemeMomo.ViewModels;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;

namespace MemeMomo.Views;

/// <summary>Reminder form hosted by a memo window; activation and visibility are separate lifetimes.</summary>
public partial class ReminderView : UserControl, IDisposable
{
    private readonly List<Task<bool>> _submissions = [];
    private ReminderViewModel? _session;
    private NotificationFeedback? _feedback;
    private bool _updatingDraft;
    private bool _collectingDraft;
    private bool _suspended = true;
    private bool _hostClosing;
    private bool _disposed;

    public ReminderView()
    {
        InitializeComponent();
        TimeWheel.SelectedTimeChanged += OnTimeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    internal ReminderViewModel? Session => _session;
    public bool IsBusy => _session?.IsBusy == true;
    public string ErrorMessage => _session?.ErrorMessage ?? string.Empty;
    internal DateTime? SelectedReminderAt => _session?.SelectedReminderAt;
    internal DateFieldSelector DateFieldPart => DateField;
    internal TimeWheelSelector TimeWheelPart => TimeWheel;
    internal Func<bool, bool>? ReturnRequested { get; set; }
    internal Action<string>? SessionInvalidated { get; set; }
    internal event EventHandler? StateChanged;

    internal void BeginSession(MemoItem memo, Func<MemoItem, DateTime?, Task> save)
    {
        EndSession();
        _session = new ReminderViewModel(memo, save);
        _session.Changed += OnStateChanged;
        _session.DraftChanged += OnDraftChanged;
        _session.Saved += OnSaved;
        _session.Invalidated += OnSessionInvalidated;
        _feedback = new NotificationFeedback(this);
        ApplyDraft();
        RefreshVisuals();
        FormScrollViewer.ScrollToTop();
    }

    internal void EndSession()
    {
        SuspendInteraction();
        if (_session is { } session)
        {
            session.Changed -= OnStateChanged;
            session.DraftChanged -= OnDraftChanged;
            session.Saved -= OnSaved;
            session.Invalidated -= OnSessionInvalidated;
            session.Dispose();
            _session = null;
        }
        _feedback?.Clear();
    }

    public void ApplyQuickOffset(TimeSpan offset)
    {
        if (!_hostClosing) _session?.ApplyQuickOffset(offset);
    }

    internal Task<bool> ScheduleAsync()
    {
        if (_disposed || _hostClosing || _session is not { } session) return Task.FromResult(false);
        session.UpdateDraft(DateField.SelectedDate, TimeWheel.SelectedTime);
        return Track(session.ScheduleAsync());
    }

    internal Task<bool> CancelReminderAsync() => _disposed || _hostClosing || _session is null
        ? Task.FromResult(false) : Track(_session.ClearReminderAsync());

    private Task<bool> Track(Task<bool> task)
    {
        _submissions.RemoveAll(item => item.IsCompleted);
        if (!task.IsCompleted) _submissions.Add(task);
        return task;
    }

    internal async Task<bool> WaitForPendingSubmissionAsync()
    {
        bool success = true;
        foreach (Task<bool> task in _submissions.Where(item => !item.IsCompleted).ToArray())
            success &= await task;
        return success && _session?.HasSubmissionFailure != true;
    }

    internal void SetHostClosing(bool closing)
    {
        _hostClosing = closing;
        RefreshVisuals();
    }

    internal void SuspendInteraction()
    {
        _suspended = true;
        DateField.SuspendInteraction();
        TimeWheel.SuspendInteraction();
    }

    internal void ResumeInteraction()
    {
        if (_disposed) return;
        _suspended = false;
        _session?.RefreshModel();
        RefreshVisuals();
    }

    internal void RefreshModel() => _session?.RefreshModel();

    internal bool DismissCalendar()
    {
        if (!DateField.IsDropDownOpen) return false;
        DateField.CloseDropDown(restoreFocus: true);
        return true;
    }

    private void ApplyDraft()
    {
        if (_session is not { } session) return;
        _updatingDraft = true;
        try
        {
            DateField.SelectedDate = session.SelectedDate;
            TimeWheel.SetTime(session.SelectedTime);
        }
        finally { _updatingDraft = false; }
    }

    private void RefreshVisuals()
    {
        if (_disposed || _session is not { } session) return;
        CurrentReminderText.Text = session.HasSavedReminder
            ? LocalizationService.Format("当前提醒：{0:yyyy-MM-dd HH:mm:ss}", session.Memo.ReminderAt!.Value.ToLocalTime()) : string.Empty;
        CurrentReminderText.Visibility = CancelReminderButton.Visibility = session.HasSavedReminder ? Visibility.Visible : Visibility.Collapsed;
        ReminderForm.IsEnabled = session.IsActive && !IsBusy && !_hostClosing;
        ScheduleButton.IsEnabled = CancelReminderButton.IsEnabled = session.CanSubmit && !_hostClosing;
        CancelButton.IsEnabled = session.IsActive && !IsBusy && !_hostClosing;
        if (_suspended || !IsVisible || !ReminderForm.IsEnabled)
        {
            DateField.SuspendInteraction();
            TimeWheel.SuspendInteraction();
        }
        else
        {
            DateField.ResumeInteraction();
            TimeWheel.ResumeInteraction();
        }
        if (IsBusy) _feedback?.Show(LocalizationService.Get("正在保存…"), persistent: true);
        else if (session.ErrorMessage.Length > 0) _feedback?.Show(session.ErrorMessage, NotificationKind.Error);
        else _feedback?.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnStateChanged(object? sender, EventArgs e) => RefreshVisuals();
    private void OnDraftChanged(object? sender, EventArgs e)
    {
        if (!_collectingDraft) ApplyDraft();
    }
    private void OnSaved(object? sender, EventArgs e) => ReturnRequested?.Invoke(true);
    private void OnSessionInvalidated(string reason) => SessionInvalidated?.Invoke(reason);
    private void OnLoaded(object sender, RoutedEventArgs e) => RefreshVisuals();
    private void OnUnloaded(object sender, RoutedEventArgs e) => SuspendInteraction();
    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshVisuals();
    private void OnSelectedDateChanged(object? sender, DateSelectionChangedEventArgs e) => CollectDraft();
    private void OnTimeChanged(object? sender, EventArgs e) => CollectDraft();
    private void CollectDraft()
    {
        if (_updatingDraft || _hostClosing) return;
        _collectingDraft = true;
        try { _session?.UpdateDraft(DateField.SelectedDate, TimeWheel.SelectedTime); }
        finally { _collectingDraft = false; }
    }
    private void OnQuickTimeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string secondsText } && int.TryParse(secondsText, out int seconds))
            ApplyQuickOffset(TimeSpan.FromSeconds(seconds));
    }
    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (!IsBusy && !_hostClosing) ReturnRequested?.Invoke(false);
    }
    private async void OnScheduleClick(object sender, RoutedEventArgs e) => await ScheduleAsync();
    private async void OnCancelReminderClick(object sender, RoutedEventArgs e) => await CancelReminderAsync();

    public void Dispose()
    {
        if (_disposed) return;
        EndSession();
        _disposed = true;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        TimeWheel.SelectedTimeChanged -= OnTimeChanged;
        LocalizationService.LanguageChanged -= OnLanguageChanged;
        ReturnRequested = null;
        SessionInvalidated = null;
    }
}
