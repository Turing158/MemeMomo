using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MemeMomo.Models;
using MemeMomo.Services;
using MemeMomo.UI;
using MemeMomo.UI.Popup;
using MemeMomo.UI.Notifications;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using UserControl = System.Windows.Controls.UserControl;

namespace MemeMomo.Views;

public partial class TaskStatusManagerView : UserControl, IDisposable
{
    private readonly MemoTaskService _service;
    private TaskStatusDefinition _draft = new();
    private bool _refreshing;
    private bool _busy;
    private NotificationFeedback? _feedback;
    private NotificationFeedback Feedback => _feedback ??= new(this);
    private string? _replacementId;
    private sealed record Choice(object Value, string Label, string? IconKey = null, Brush? Swatch = null);
    private sealed record StatusRow(TaskStatusDefinition Status, string Name, string Details, Brush Color, Brush Background)
    {
        public string Label => Name + " · " + Details + (Status.IsHidden ? " · " + LocalizationService.Get("已隐藏") : "");
    }
    public TaskStatusManagerView(MemoTaskService service)
    {
        _service = service;
        InitializeComponent();
        service.Catalog.Changed += RefreshList;
        service.MemosChanged += RefreshList;
        LocalizationService.LanguageChanged += OnAppearanceChanged;
        ThemePreferences.PaletteChanged += OnAppearanceChanged;
        IsVisibleChanged += OnPresentationChanged;
        IsHitTestVisibleChanged += OnPresentationChanged;
        RefreshList(); BeginDraft(service.Catalog.Statuses[0]);
    }
    private void OnAppearanceChanged(object? sender, EventArgs e)
    {
        CloseChoiceMenus();
        RefreshList(); PopulateChoices(); UpdatePreview();
        if (_draft.IsBuiltIn) NameBox.Text = _service.Catalog.DisplayName(_draft);
    }
    private void RefreshList()
    {
        _refreshing = true;
        var rows = _service.Catalog.Statuses.OrderBy(item => item.Category).ThenBy(item => item.Order)
            .Select(item => new StatusRow(item, _service.Catalog.DisplayName(item),
                (item.IsBuiltIn ? LocalizationService.Get("内置状态") : TaskStatusCatalog.CategoryName(item.Category)) + " · " + LocalizationService.Format("{0} 条任务", _service.UsageCount(item.Id)),
                TaskStatusAppearance.Foreground(item.ColorKey), TaskStatusAppearance.Background(item.ColorKey))).ToArray();
        StatusList.ItemsSource = rows;
        StatusList.SelectedItem = rows.FirstOrDefault(row => row.Status.Id == _draft.Id);
        StatusCountText.Text = LocalizationService.Format("{0} 个状态", rows.Length);
        _refreshing = false;
    }
    private void PopulateChoices()
    {
        SetChoice(CategoryBox, ChoicesFor(CategoryBox).First(choice => Equals(choice.Value, _draft.Category)));
        SetChoice(ColorBox, ChoicesFor(ColorBox).First(choice => Equals(choice.Value, _draft.ColorKey)));
        SetChoice(IconBox, ChoicesFor(IconBox).First(choice => Equals(choice.Value, _draft.IconKey)));
        Choice[] replacements = ChoicesFor(ReplacementBox);
        Choice? replacement = replacements.FirstOrDefault(choice => Equals(choice.Value, _replacementId)) ?? replacements.FirstOrDefault();
        _replacementId = replacement?.Value as string;
        SetChoice(ReplacementBox, replacement);
    }

    private Choice[] ChoicesFor(Button button)
    {
        if (button == CategoryBox) return Enum.GetValues<TaskStatusCategory>()
            .Select(value => new Choice(value, TaskStatusCatalog.CategoryName(value))).ToArray();
        if (button == ColorBox) return TaskStatusCatalog.ColorKeys.Select(key => new Choice(key,
            LocalizationService.Get(key switch { "Blue" => "蓝色", "Green" => "绿色", "Amber" => "琥珀色", "Purple" => "紫色", "Red" => "红色", _ => "灰色" }),
            Swatch: TaskStatusAppearance.Foreground(key))).ToArray();
        if (button == IconBox) return TaskStatusCatalog.IconKeys.Select(key => new Choice(key, TaskStatusAppearance.IconLabel(key), key)).ToArray();
        return _service.Catalog.Selectable.Where(item => item.Id != _draft.Id)
            .Select(item => new Choice(item.Id, _service.Catalog.DisplayName(item), item.IconKey)).ToArray();
    }

    private static void SetChoice(Button button, Choice? choice)
    {
        button.Content = choice;
        button.Tag = choice?.Value;
    }

    private void OnOpenChoiceMenu(object sender, RoutedEventArgs e)
    {
        if (!CanInteract || sender is not Button { IsEnabled: true } button) return;
        CloseChoiceMenus();
        var menu = TaskContextMenu.Create(button);
        menu.MinWidth = Math.Min(300, Math.Max(160, button.ActualWidth));
        foreach (Choice choice in ChoicesFor(button))
        {
            var item = TaskContextMenu.Item(button, choice.Label, Equals(choice.Value, button.Tag), choice.IconKey);
            if (choice.Swatch is not null)
                item.Icon = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(4), Background = choice.Swatch };
            item.Tag = choice.Value;
            item.Click += (_, _) =>
            {
                if (button == CategoryBox) _draft = _draft with { Category = (TaskStatusCategory)choice.Value };
                else if (button == ColorBox) _draft = _draft with { ColorKey = (string)choice.Value };
                else if (button == IconBox) _draft = _draft with { IconKey = (string)choice.Value };
                else _replacementId = (string)choice.Value;
                SetChoice(button, choice);
                UpdatePreview();
            };
            menu.Items.Add(item);
        }
        TaskContextMenu.Open(menu);
    }

    private void CloseChoiceMenus()
    {
        foreach (Button button in new[] { CategoryBox, ColorBox, IconBox, ReplacementBox })
            if (button.ContextMenu is { } menu) ContextMenuAnimations.Close(menu, immediate: true);
    }
    private void BeginDraft(TaskStatusDefinition status)
    {
        CloseChoiceMenus();
        _refreshing = true;
        _draft = status with { };
        _replacementId = null;
        StatusList.SelectedItem = StatusList.Items.OfType<StatusRow>().FirstOrDefault(row => row.Status.Id == status.Id);
        NameBox.Text = status.IsBuiltIn ? _service.Catalog.DisplayName(status) : status.Name;
        NameBox.IsEnabled = CategoryBox.IsEnabled = !status.IsBuiltIn;
        BuiltInHint.Visibility = status.IsBuiltIn ? Visibility.Visible : Visibility.Collapsed;
        HiddenCheck.IsChecked = status.IsHidden;
        DeleteButton.IsEnabled = !status.IsBuiltIn && _service.Catalog.Find(status.Id) is not null;
        MigrationCard.Visibility = DeleteButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        Feedback.Clear();
        _refreshing = false;
        PopulateChoices(); UpdatePreview();
    }
    private TaskStatusDefinition ReadDraft() => _draft with
    {
        Name = _draft.IsBuiltIn ? _draft.Name : NameBox.Text,
        IsHidden = HiddenCheck.IsChecked == true
    };
    private void OnSelected(object sender, SelectionChangedEventArgs e)
    { if (!_refreshing && CanInteract && StatusList.SelectedItem is StatusRow row) BeginDraft(row.Status); }
    private void OnNew(object sender, RoutedEventArgs e)
    {
        if (!CanInteract) return;
        BeginDraft(new TaskStatusDefinition { Order = _service.Catalog.Statuses.Where(item => item.Category == TaskStatusCategory.Todo).Select(item => item.Order).DefaultIfEmpty().Max() + 1 });
        NameBox.BringIntoView(); NameBox.Focus();
    }
    private void OnNameChanged(object sender, TextChangedEventArgs e) { if (!_refreshing && IsInitialized) UpdatePreview(); }
    private void UpdatePreview()
    {
        if (PreviewText is null) return;
        var draft = ReadDraft();
        PreviewText.Text = _draft.IsBuiltIn ? _service.Catalog.DisplayName(_draft) : NameBox.Text;
        PreviewIcon.IconKey = draft.IconKey;
        PreviewIcon.Foreground = TaskStatusAppearance.Foreground(draft.ColorKey);
        PreviewText.Foreground = TaskStatusAppearance.Foreground(draft.ColorKey); PreviewBadge.Background = TaskStatusAppearance.Background(draft.ColorKey);
        var target = _service.Catalog.Find(_replacementId);
        ImpactText.Text = Impact(draft.Category) + (target is null ? "" : "\n" + LocalizationService.Get("删除时迁移到") + " " + _service.Catalog.DisplayName(target) + ": " + Impact(target.Category));
        var siblings = _service.Catalog.Statuses.Where(status => status.Category == _draft.Category).OrderBy(status => status.Order).ToArray();
        int index = Array.FindIndex(siblings, status => status.Id == _draft.Id);
        MoveUpButton.IsEnabled = index > 0;
        MoveDownButton.IsEnabled = index >= 0 && index < siblings.Length - 1;
    }
    private string Impact(TaskStatusCategory category) => LocalizationService.Format("影响 {0} 条任务；将取消 {1} 条提醒；目标类别：{2}", _service.UsageCount(_draft.Id), _service.ReminderImpact(_draft.Id, category), TaskStatusCatalog.CategoryName(category));
    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (!CanInteract) return;
        var draft = ReadDraft();
        if (_service.Catalog.Find(draft.Id) is { } original && original.Category != draft.Category && _service.UsageCount(draft.Id) > 0
            && !await ConfirmMigrationAsync(Impact(draft.Category))) return;
        await RunAsync(() => _service.SaveDefinitionAsync(draft));
    }
    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (!CanInteract || _replacementId is not string replacement || _service.Catalog.Find(replacement) is not { } target) return;
        string sourceId = _draft.Id;
        if (!await ConfirmMigrationAsync(Impact(target.Category))) return;
        await RunAsync(() => _service.DeleteDefinitionAsync(sourceId, replacement));
    }
    private Task RunAsync(Func<Task<StorageResult>> operation) => _pendingOperation = RunOperationAsync(operation);

    private async Task RunOperationAsync(Func<Task<StorageResult>> operation)
    {
        _busy = true; IsEnabled = false;
        try
        {
            var result = await operation();
            if (_disposed) return;
            if (!result.Success) Feedback.Show(LocalizationService.Format("保存失败，请重试：{0}", result.Error), NotificationKind.Error);
            else { RefreshList(); BeginDraft(_service.Catalog.Find(_draft.Id) ?? _service.Catalog.Statuses[0]); }
        }
        finally { _busy = false; if (!_disposed) IsEnabled = true; }
    }
    private async void OnMoveUp(object sender, RoutedEventArgs e) => await MoveAsync(-1);
    private async void OnMoveDown(object sender, RoutedEventArgs e) => await MoveAsync(1);
    private async Task MoveAsync(int direction)
    {
        if (!CanInteract) return;
        await RunAsync(() => _service.MoveDefinitionAsync(_draft.Id, direction));
    }
    private async void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_active) return;
        e.Handled = true;
        if (_busy) return;
        var openSelector = new[] { CategoryBox, ColorBox, IconBox, ReplacementBox }.FirstOrDefault(selector => selector.ContextMenu is { IsOpen: true });
        if (openSelector is not null) { CloseChoiceMenus(); openSelector.Focus(); }
        else if (_confirmation is not null) CancelTransientOperations();
        else if (ReturnRequested is { } navigate) await navigate();
    }
}
