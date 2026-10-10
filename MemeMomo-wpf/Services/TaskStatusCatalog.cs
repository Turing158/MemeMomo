using System.Globalization;
using MemeMomo.Models;

namespace MemeMomo.Services;

public sealed class TaskStatusCatalog
{
    private readonly JsonTaskStatusStorage _storage;
    private IReadOnlyList<TaskStatusDefinition> _statuses = BuiltIns();
    private readonly Dictionary<string, string> _defaultReplacements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _selectionReplacements = new(StringComparer.Ordinal);
    public IReadOnlyList<TaskStatusDefinition> Statuses => _statuses;
    public IReadOnlyList<string> Diagnostics => _storage.Diagnostics;
    public event Action? Changed;
    public TaskStatusCatalog(JsonTaskStatusStorage? storage = null) => _storage = storage ?? new();
    public static readonly string[] ColorKeys = ["Blue", "Green", "Amber", "Purple", "Gray", "Red"];
    public static readonly string[] IconKeys = ["Circle", "Play", "Pause", "Check", "Cancel", "Star", "Flag", "Clock"];

    public async Task LoadAsync()
    {
        var loaded = (await _storage.LoadAsync()).ToList();
        foreach (var builtIn in BuiltIns())
            if (loaded.All(item => item.Id != builtIn.Id)) loaded.Add(builtIn);
        if (loaded.All(item => item.IsClosed || item.IsHidden))
        {
            int todo = loaded.FindIndex(item => item.Id == TaskStatusDefinition.DefaultId);
            loaded[todo] = loaded[todo] with { IsHidden = false };
        }
        _statuses = loaded;
        Changed?.Invoke();
    }

    public TaskStatusDefinition? Find(string? id) => _statuses.FirstOrDefault(status => status.Id == id);
    public IEnumerable<TaskStatusDefinition> Selectable => _statuses.Where(status => !status.IsHidden && !_selectionReplacements.ContainsKey(status.Id)).OrderBy(status => status.Category).ThenBy(status => status.Order);
    internal TaskStatusDefinition? FindForSelection(string? id)
    {
        HashSet<string> visited = [];
        while (id is not null && visited.Add(id) && _selectionReplacements.TryGetValue(id, out var next)) id = next;
        return Find(id);
    }
    internal void RedirectSelection(string source, string target) => _selectionReplacements[source] = target;
    private string ResolveDefaultId(string? id)
    {
        HashSet<string> visited = [];
        while (id is not null && visited.Add(id) && _defaultReplacements.TryGetValue(id, out var next)) id = next;
        return id ?? TaskStatusDefinition.DefaultId;
    }
    internal void RedirectDefault(string source, string replacement) => _defaultReplacements[source] = replacement;
    public TaskStatusDefinition Default(AppSettings settings) =>
        Find(ResolveDefaultId(settings.DefaultTaskStatusId)) is { IsClosed: false, IsHidden: false } status ? status :
            Selectable.FirstOrDefault(item => !item.IsClosed) ?? BuiltIns()[0];
    public void RepairDefault(AppSettings settings)
    {
        if (!Enum.IsDefined(settings.DefaultMemoKind)) settings.DefaultMemoKind = MemoKind.Note;
        settings.DefaultTaskStatusId = Default(settings).Id;
    }
    public static string CategoryName(TaskStatusCategory category) => LocalizationService.Get(category switch
    {
        TaskStatusCategory.Todo => "待办",
        TaskStatusCategory.InProgress => "进行中",
        TaskStatusCategory.OnHold => "暂缓",
        TaskStatusCategory.Done => "已完成",
        TaskStatusCategory.Canceled => "已取消",
        _ => "待办"
    });
    public string DisplayName(TaskStatusDefinition status) => status.IsBuiltIn ? CategoryName(status.Category) : status.Name;
    public string DisplayName(MemoItem memo) => !memo.IsTask ? LocalizationService.Get("普通备忘") :
        Find(memo.TaskStatusId) is { } status ? DisplayName(status) : LocalizationService.Get("状态已缺失");

    public void Normalize(MemoItem memo)
    {
        if (!memo.IsTask)
        {
            memo.TaskStatusId = null; memo.TaskStatusCategorySnapshot = null;
            memo.TaskClosedAt = null; memo.LastActiveTaskStatusId = null;
            return;
        }
        if (string.IsNullOrWhiteSpace(memo.TaskStatusId))
            memo.TaskStatusId = TaskStatusDefinition.BuiltInId(memo.TaskStatusCategorySnapshot ?? TaskStatusCategory.Todo);
        memo.TaskStatusCategorySnapshot = Find(memo.TaskStatusId)?.Category ?? memo.TaskStatusCategorySnapshot ?? TaskStatusCategory.Todo;
        if (memo.IsTaskClosed) memo.ReminderAt = null;
        else memo.TaskClosedAt = null;
    }

    public string? Validate(TaskStatusDefinition draft, AppSettings settings, string? replacingId = null)
    {
        if (!Enum.IsDefined(draft.Category)) return LocalizationService.Get("请选择有效类别。");
        var original = Find(draft.Id);
        if (draft.IsBuiltIn && (original is null || draft.Category != original.Category || draft.Name != original.Name))
            return LocalizationService.Get("内置状态的名称和类别不能修改。");
        string name = DisplayName(draft).Trim();
        if (new StringInfo(name).LengthInTextElements is < 1 or > 24) return LocalizationService.Get("状态名称须为 1～24 个字符。");
        if (_statuses.Any(item => item.Id != draft.Id && item.Id != replacingId && LocalizationService.Culture.CompareInfo.Compare(DisplayName(item), name, CompareOptions.IgnoreCase) == 0))
            return LocalizationService.Get("状态名称不能重复。");
        if (!ColorKeys.Contains(draft.ColorKey) || !IconKeys.Contains(draft.IconKey)) return LocalizationService.Get("请选择预设颜色和图标。");
        if (draft.Id == settings.DefaultTaskStatusId && (draft.IsHidden || draft.IsClosed)) return LocalizationService.Get("请先选择其他新任务默认状态。");
        if ((draft.IsHidden || draft.IsClosed) && !_statuses.Any(item => item.Id != draft.Id && !item.IsHidden && !item.IsClosed))
            return LocalizationService.Get("至少保留一个可选的未结束状态。");
        return null;
    }

    internal async Task<StorageResult> PersistAsync(IEnumerable<TaskStatusDefinition> statuses)
    {
        var snapshot = statuses.ToArray();
        var result = await _storage.TrySaveAsync(snapshot);
        if (result.Success) { _statuses = snapshot; Changed?.Invoke(); }
        return result;
    }

    private static TaskStatusDefinition[] BuiltIns() => Enum.GetValues<TaskStatusCategory>().Select((category, index) => new TaskStatusDefinition
    {
        Id = TaskStatusDefinition.BuiltInId(category),
        Category = category,
        Name = category.ToString(),
        ColorKey = new[] { "Blue", "Purple", "Amber", "Green", "Gray" }[index],
        IconKey = new[] { "Circle", "Play", "Pause", "Check", "Cancel" }[index]
    }).ToArray();
}
