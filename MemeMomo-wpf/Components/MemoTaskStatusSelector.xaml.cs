using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MemeMomo.Models;
using MemeMomo.Services;
using MemeMomo.UI;
using MemeMomo.UI.Animation;
using MemeMomo.UI.Popup;
using MemeMomo.UI.Notifications;
using UserControl = System.Windows.Controls.UserControl;

namespace MemeMomo.Components;

public partial class MemoTaskStatusSelector : UserControl
{
    public static readonly DependencyProperty MemoProperty = DependencyProperty.Register(nameof(Memo), typeof(MemoItem), typeof(MemoTaskStatusSelector), new PropertyMetadata(null, OnSourceChanged));
    public static readonly DependencyProperty ServiceProperty = DependencyProperty.Register(nameof(Service), typeof(MemoTaskService), typeof(MemoTaskStatusSelector), new PropertyMetadata(null, OnSourceChanged));
    public static readonly DependencyProperty DefaultStatusOnlyProperty = DependencyProperty.Register(nameof(DefaultStatusOnly), typeof(bool), typeof(MemoTaskStatusSelector), new PropertyMetadata(false, OnSourceChanged));
    public static readonly DependencyProperty CardPresentationProperty = DependencyProperty.Register(nameof(CardPresentation), typeof(bool), typeof(MemoTaskStatusSelector), new PropertyMetadata(false, OnSourceChanged));
    public MemoItem? Memo { get => (MemoItem?)GetValue(MemoProperty); set => SetValue(MemoProperty, value); }
    public MemoTaskService? Service { get => (MemoTaskService?)GetValue(ServiceProperty); set => SetValue(ServiceProperty, value); }
    public bool DefaultStatusOnly { get => (bool)GetValue(DefaultStatusOnlyProperty); set => SetValue(DefaultStatusOnlyProperty, value); }
    public bool CardPresentation { get => (bool)GetValue(CardPresentationProperty); set => SetValue(CardPresentationProperty, value); }
    internal MemoCardTaskVisualController? CardVisuals { get; private set; }
    internal Func<IAnimationFrameSource>? CardFrameFactory { get; set; }
    public event EventHandler? SelectionChanged;
    public bool CompactNote { get; set; }
    public MemoKind NewKind { get; set; }
    public string? NewStatusId { get; set; }
    private MemoItem? _observedMemo;
    private MemoTaskService? _observedService;
    private ContextMenu? _menu;
    internal ContextMenu? StatusMenu => _menu;
    private bool _busy;
    private bool _active;
    private DispatcherOperation? _refreshOperation;
    private long _feedbackVersion;
    private NotificationFeedback? _feedback;
    private NotificationFeedback Feedback => _feedback ??= new(this);
    public MemoTaskStatusSelector()
    {
        InitializeComponent();
        Loaded += (_, _) => Activate();
        Unloaded += (_, _) => Deactivate();
        IsVisibleChanged += (_, _) => { if (IsVisible && IsLoaded) Activate(); else if (!IsVisible) Deactivate(); };
    }
    private void Activate()
    {
        if (_active || !IsVisible) return;
        _active = true;
        Subscribe();
        LocalizationService.LanguageChanged += OnAppearanceChanged;
        ThemePreferences.PaletteChanged += OnAppearanceChanged;
        if (CardPresentation) CardVisuals = MemoCardTaskVisualController.Attach(this, CardFrameFactory);
        Refresh();
    }
    private void Deactivate()
    {
        _active = false;
        _refreshOperation?.Abort();
        _refreshOperation = null;
        ResetFeedback();
        Unsubscribe();
        LocalizationService.LanguageChanged -= OnAppearanceChanged;
        ThemePreferences.PaletteChanged -= OnAppearanceChanged;
        CloseMenu();
        CardVisuals?.Dispose();
        CardVisuals = null;
    }
    public void ResetNewDefaults()
    {
        NewKind = Service?.Settings.DefaultMemoKind ?? MemoKind.Note;
        NewStatusId = Service?.Catalog.Default(Service.Settings).Id;
        Refresh();
    }
    private static void OnSourceChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        var selector = (MemoTaskStatusSelector)owner;
        selector.Deactivate();
        if (selector.IsLoaded) selector.Activate();
        selector.Refresh();
    }
    private void ResetFeedback()
    {
        ++_feedbackVersion;
        _feedback?.Clear();
    }
    private void Subscribe()
    {
        _observedMemo = Memo; if (_observedMemo is not null) _observedMemo.PropertyChanged += OnMemoChanged;
        _observedService = Service; if (_observedService is not null) _observedService.Catalog.Changed += Refresh;
    }
    private void Unsubscribe()
    {
        if (_observedMemo is not null) _observedMemo.PropertyChanged -= OnMemoChanged;
        if (_observedService is not null) _observedService.Catalog.Changed -= Refresh;
        _observedMemo = null; _observedService = null;
    }
    private void OnMemoChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Memo?.IsDeleted == true && _menu is not null) ContextMenuAnimations.Close(_menu, immediate: true);
        Refresh();
    }
    private void OnAppearanceChanged(object? sender, EventArgs e) => Refresh();
    public void Refresh()
    {
        if (StatusButton is null) return;
        UpdateAvailability();
        if (CardPresentation)
        {
            StatusButton.Visibility = Visibility.Collapsed;
            if (!_active || _refreshOperation is not null) return;
            // A successful commit publishes several fields synchronously. Resolve
            // one committed visual snapshot after all those notifications arrive.
            _refreshOperation = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
            {
                _refreshOperation = null;
                CardVisuals?.Refresh();
            }));
            return;
        }
        if (DefaultStatusOnly) NewKind = MemoKind.Task;
        bool task = Memo?.IsTask ?? NewKind == MemoKind.Task;
        if (Memo is null && Service is not null)
            NewStatusId = Service.Catalog.FindForSelection(NewStatusId) is { IsHidden: false } selected && (!DefaultStatusOnly || !selected.IsClosed)
                ? selected.Id : Service.Catalog.Default(Service.Settings).Id;
        var status = Service?.Catalog.Find(Memo is null ? NewStatusId : Memo.TaskStatusId);
        string label = !task ? LocalizationService.Get("普通备忘") : status is null ? LocalizationService.Get("状态已缺失") : Service!.Catalog.DisplayName(status);
        StatusButton.Visibility = CompactNote && !task ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = label;
        StatusIcon.IconKey = status?.IconKey;
        StatusIcon.Visibility = task ? Visibility.Visible : Visibility.Collapsed;
        StatusButton.ToolTip = label;
        StatusArrow.Visibility = Memo?.IsDeleted == true ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(StatusButton, LocalizationService.Get(task ? "任务备忘" : "普通备忘") + " · " + label + " · " + LocalizationService.Get("选择状态"));
        StatusButton.Foreground = TaskStatusAppearance.Foreground(task ? status?.ColorKey : "Gray");
        StatusButton.Background = TaskStatusAppearance.Background(task ? status?.ColorKey : "Gray");
        StatusContent.Opacity = Memo?.IsTaskClosed == true ? 0.8 : 1;
        QuickButton.Visibility = !DefaultStatusOnly && task && Memo is not null && !Memo.IsDeleted ? Visibility.Visible : Visibility.Collapsed;
        QuickIcon.IconKey = Memo?.IsTaskClosed == true ? "Reopen" : "Selection";
        string action = LocalizationService.Get(Memo?.IsTaskClosed == true ? "重新打开" : "完成任务");
        QuickButton.ToolTip = action; AutomationProperties.SetName(QuickButton, action);
    }
    private void UpdateAvailability()
    {
        if (StatusButton is not null)
            StatusButton.IsEnabled = QuickButton.IsEnabled = !_busy && Memo?.IsDeleted != true && Memo?.IsLifecyclePending != true && Memo?.IsTaskCommitPending != true && Service is not null;
    }
    private void OnOpenMenu(object sender, RoutedEventArgs e) { e.Handled = true; OpenMenu(); }
    internal void CloseMenu()
    {
        var menu = _menu;
        _menu = null;
        if (menu is not null)
        {
            ContextMenuAnimations.Close(menu, immediate: true);
            // Menus are single-use. Dispose even if hidden before WPF delivers
            // Opened/Closed, so a queued popup cannot retain its input monitor.
            ContextMenuAnimations.SetIsEnabled(menu, false);
        }
        CardVisuals?.SetMenuOpen(false);
    }
    internal void OpenMenu(FrameworkElement? target = null, PlacementMode placement = PlacementMode.Bottom)
    {
        if (!IsVisible || !IsEnabled || Service is null || _busy || Memo?.IsDeleted == true || Memo?.IsLifecyclePending == true || Memo?.IsTaskCommitPending == true) return;
        CloseMenu();
        var menu = TaskContextMenu.Create(target ?? (CardPresentation ? QuickButton : StatusButton), placement);
        if (DefaultStatusOnly) menu.MaxHeight = 350;
        bool isTask = Memo?.IsTask ?? NewKind == MemoKind.Task;
        string? selectedStatusId = Memo is null ? NewStatusId : Memo.TaskStatusId;
        if (!DefaultStatusOnly)
        {
            var note = TaskContextMenu.Item(this, LocalizationService.Get(Memo is null ? "普通备忘" : Memo.IsTask ? "转为普通备忘" : "设为任务"),
                isChecked: Memo is null ? !isTask : null);
            note.Click += async (_, _) =>
            {
                if (!ReferenceEquals(_menu, menu)) return;
                if (Memo is null) SelectNew(MemoKind.Note, NewStatusId);
                else await RunAsync(() => Service.SetKindAsync(Memo.Id, Memo.IsTask ? MemoKind.Note : MemoKind.Task));
            };
            menu.Items.Add(note);
            menu.Items.Add(TaskContextMenu.Separator(this));
        }
        foreach (var status in Service.Catalog.Selectable.Where(status => !DefaultStatusOnly || !status.IsClosed))
        {
            string name = Service.Catalog.DisplayName(status);
            var choice = TaskContextMenu.Item(this, name,
                isChecked: isTask && status.Id == selectedStatusId, iconKey: status.IconKey);
            choice.Click += async (_, _) =>
            {
                if (!ReferenceEquals(_menu, menu)) return;
                if (DefaultStatusOnly && !Service.Catalog.Selectable.Any(current => current.Id == status.Id && !current.IsClosed)) return;
                if (Memo is null) SelectNew(MemoKind.Task, status.Id);
                else await RunAsync(() => Service.SetStatusAsync(Memo.Id, status.Id));
            };
            menu.Items.Add(choice);
        }
        _menu = menu;
        CardVisuals?.SetMenuOpen(true);
        menu.Closed += (_, _) =>
        {
            ContextMenuAnimations.SetIsEnabled(menu, false);
            if (!ReferenceEquals(_menu, menu)) return;
            _menu = null;
            CardVisuals?.SetMenuOpen(false);
        };
        TaskContextMenu.Open(menu);
    }
    private void SelectNew(MemoKind kind, string? statusId)
    {
        if (NewKind == kind && NewStatusId == statusId) return;
        NewKind = kind;
        NewStatusId = statusId;
        Refresh();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
    private async void OnQuickClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Memo is null || Service is null) return;
        await RunAsync(() => Memo.IsTaskClosed ? Service.ReopenAsync(Memo.Id) : Service.CompleteAsync(Memo.Id));
    }
    private async Task RunAsync(Func<Task<StorageResult>> operation)
    {
        if (_busy) return;
        _busy = true; Feedback.Clear(); Refresh();
        long version = _feedbackVersion;
        try
        {
            var result = await operation();
            if (!result.Success && version == _feedbackVersion)
                Feedback.Show(LocalizationService.Format("保存失败，请重试：{0}", result.Error), NotificationKind.Error);
        }
        finally { _busy = false; Refresh(); }
    }
}
