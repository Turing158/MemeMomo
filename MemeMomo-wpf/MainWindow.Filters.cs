using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MemeMomo.Models;
using MemeMomo.Services;
using MemeMomo.UI.Popup;

namespace MemeMomo;

public partial class MainWindow
{
    private void RefreshFilterBadge()
    {
        var view = _viewModel.TaskView;
        string summary = view.Kind switch
        {
            MemoKind.Note => LocalizationService.Get("普通"),
            MemoKind.Task => LocalizationService.Get("任务"),
            _ => LocalizationService.Get("全部")
        };
        if (view.Status is { } status)
        {
            string label = status == "active" ? LocalizationService.Get("未结束") :
                status.StartsWith("category:", StringComparison.Ordinal) && int.TryParse(status[9..], out int category)
                    ? CategoryFilterLabel((TaskStatusCategory)category) :
                    _viewModel.Tasks.Catalog.Find(status) is { } definition ? _viewModel.Tasks.Catalog.DisplayName(definition) :
                        LocalizationService.Get("状态已缺失");
            summary += " | " + label;
        }
        bool filtered = !view.CanReorder;
        int count = IsRecycleBinMode ? _viewModel.DeletedMemos.Count : view.VisibleCount;
        if (IsRecycleBinMode) summary = LocalizationService.Get("回收站");
        FilterBadge.IsEnabled = !IsRecycleBinMode && !IsSettingsPageVisible && !_modeChanging && !_recycleActionPending;
        FilterBadge.Update(count, summary, filtered && !IsRecycleBinMode,
            LocalizationService.Get("筛选备忘录") + " · " + summary + " · " +
            LocalizationService.Format("显示 {0} / {1}", count, IsRecycleBinMode ? count : _viewModel.Memos.Count));
    }

    private void OnFilterBadgeClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (!FilterBadge.IsEnabled || _settingsNavigating || _disposed) return;
        if (FilterBadge.MenuTarget.ContextMenu is { IsOpen: true })
        {
            FilterBadge.CloseMenu();
            return;
        }
        var target = FilterBadge.MenuTarget;
        var menu = TaskContextMenu.Create(target, useListScrollBar: true);
        menu.MinWidth = 210;
        var recycle = TaskContextMenu.Item(target, LocalizationService.Format("回收站（{0}）", _viewModel.DeletedMemos.Count));
        recycle.Tag = "recycle";
        var recycleIcon = new System.Windows.Shapes.Path
        {
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            Data = (Geometry)FindResource("RecycleBinIcon")
        };
        recycleIcon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextSecondaryBrush");
        recycle.Icon = recycleIcon;
        recycle.Click += async (_, _) =>
        {
            FilterBadge.CloseMenu();
            await SetRecycleBinModeAsync(true);
        };
        menu.Items.Add(recycle);
        var clear = TaskContextMenu.Item(target, LocalizationService.Get("清除筛选"), iconKey: "Reopen");
        clear.Tag = "clear";
        clear.IsEnabled = !_viewModel.TaskView.CanReorder;
        clear.Click += (_, _) => _viewModel.TaskView.Clear();
        menu.Items.Add(clear);
        menu.Items.Add(TaskContextMenu.Separator(target));
        AddHeader("类型");
        AddChoice("kind:all", LocalizationService.Get("全部备忘"), _viewModel.TaskView.Kind is null,
            () => _viewModel.TaskView.Clear());
        AddChoice("kind:note", LocalizationService.Get("普通备忘"), _viewModel.TaskView.Kind == MemoKind.Note,
            () => _viewModel.TaskView.Kind = MemoKind.Note);
        AddChoice("kind:task", LocalizationService.Get("任务备忘"), _viewModel.TaskView.Kind == MemoKind.Task,
            () => _viewModel.TaskView.Kind = MemoKind.Task);
        menu.Items.Add(TaskContextMenu.Separator(target));
        AddHeader("任务状态");
        foreach (var option in StatusFilterOptions())
            AddChoice("status:" + option.Value, option.Label, option.Selected,
                () => _viewModel.TaskView.Status = option.Value, option.Icon, _viewModel.TaskView.Kind != MemoKind.Note);
        FilterBadge.OpenMenu(menu);

        void AddHeader(string label)
        {
            var heading = new MenuItem
            {
                Header = LocalizationService.Get(label),
                Tag = "heading",
                Focusable = false,
                IsHitTestVisible = false,
                IsEnabled = false,
                Template = (ControlTemplate)FindResource("FilterMenuHeadingTemplate")
            };
            menu.Items.Add(heading);
        }

        void AddChoice(string id, string label, bool selected, Action action, string? icon = null, bool enabled = true)
        {
            var item = TaskContextMenu.Item(target, label, selected, icon);
            item.Tag = id;
            item.IsEnabled = enabled;
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    private IEnumerable<(string? Value, string Label, bool Selected, string Icon)> StatusFilterOptions()
    {
        string? selectedStatus = _viewModel.TaskView.Status;
        yield return (null, LocalizationService.Get("状态不限"), selectedStatus is null, "Filter");
        yield return ("active", LocalizationService.Get("未结束"), selectedStatus == "active", "Clock");
        foreach (var category in Enum.GetValues<TaskStatusCategory>())
        {
            bool specific = HasSpecificStatusFilters(category);
            bool selected = selectedStatus == $"category:{(int)category}" ||
                (!specific && _viewModel.Tasks.Catalog.Find(selectedStatus)?.Category == category);
            string icon = _viewModel.Tasks.Catalog.Find(TaskStatusDefinition.BuiltInId(category))?.IconKey ?? "Circle";
            yield return ($"category:{(int)category}", CategoryFilterLabel(category), selected, icon);
            if (!specific) continue;
            foreach (var status in _viewModel.Tasks.Catalog.Statuses.Where(status => status.Category == category).OrderBy(status => status.Order))
                yield return (status.Id, _viewModel.Tasks.Catalog.DisplayName(status), selectedStatus == status.Id, status.IconKey);
        }
        foreach (var missing in _viewModel.Memos.Where(item => item.IsTask && _viewModel.Tasks.Catalog.Find(item.TaskStatusId) is null).Select(item => item.TaskStatusId).Distinct())
            yield return (missing, LocalizationService.Get("状态已缺失") + " · " + missing, selectedStatus == missing, "Circle");
    }

    private bool HasSpecificStatusFilters(TaskStatusCategory category) =>
        _viewModel.Tasks.Catalog.Statuses.Any(status => status.Category == category && !status.IsBuiltIn) ||
        _viewModel.Memos.Any(item => item.IsTask && (item.TaskStatusCategorySnapshot ?? TaskStatusCategory.Todo) == category && _viewModel.Tasks.Catalog.Find(item.TaskStatusId) is null);

    private string CategoryFilterLabel(TaskStatusCategory category) => HasSpecificStatusFilters(category)
        ? LocalizationService.Format("{0}（全部）", TaskStatusCatalog.CategoryName(category))
        : TaskStatusCatalog.CategoryName(category);
}
