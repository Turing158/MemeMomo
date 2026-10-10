using MemeMomo.Models;
using MemeMomo.Utils;
using MemeMomo.ViewModels;

namespace MemeMomo.Services;

public sealed class MemoTaskService
{
    private readonly MainViewModel _viewModel;
    private readonly Func<Task<StorageResult>> _saveSettings;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly Dictionary<string, string> _pendingCategoryReplacements = new(StringComparer.Ordinal);
    internal Func<DateTime> Clock { get; set; } = () => DateTimeUtils.Now;
    public TaskStatusCatalog Catalog { get; }
    public AppSettings Settings { get; }
    public event Action<Guid>? ReminderCanceled;
    public event Action? DefaultsChanged;
    public event Action? MemosChanged;
    public Func<Guid, bool>? HasPendingNotification { get; set; }
    internal void NotifyMemosChanged() => MemosChanged?.Invoke();

    internal MemoTaskService(MainViewModel viewModel, TaskStatusCatalog catalog, AppSettings settings, Func<Task<StorageResult>>? saveSettings)
    {
        _viewModel = viewModel; Catalog = catalog; Settings = settings;
        _saveSettings = saveSettings ?? (() => Task.FromResult(StorageResult.Ok));
    }
    public void InitializeNew(MemoItem memo, MemoKind? kind = null, string? statusId = null)
    {
        if ((kind ?? Settings.DefaultMemoKind) != MemoKind.Task) return;
        var status = Catalog.FindForSelection(statusId) is { IsHidden: false } selected ? selected : Catalog.Default(Settings);
        ApplyTransition(memo, MemoKind.Task, status);
    }
    public Task<StorageResult> SetStatusAsync(Guid id, string statusId) => ChangeAsync(id, MemoKind.Task, statusId, false);
    public Task<StorageResult> SetKindAsync(Guid id, MemoKind kind) => ChangeAsync(id, kind, null, false);
    public Task<StorageResult> CompleteAsync(Guid id) => SetStatusAsync(id, TaskStatusDefinition.BuiltInId(TaskStatusCategory.Done));
    public Task<StorageResult> ReopenAsync(Guid id) => ChangeAsync(id, MemoKind.Task, null, true);

    private async Task<StorageResult> ChangeAsync(Guid id, MemoKind kind, string? statusId, bool reopen)
    {
        var pendingMemo = _viewModel.Memos.FirstOrDefault(item => item.Id == id);
        if (pendingMemo?.IsLifecyclePending == true || pendingMemo?.IsTaskCommitPending == true) return new(false, LocalizationService.Get("正在保存，请稍后重试。"));
        if (pendingMemo is not null) pendingMemo.IsTaskCommitPending = true;
        long version = pendingMemo?.LifecycleVersion ?? -1;
        await _mutationGate.WaitAsync();
        try
        {
            var live = _viewModel.Memos.FirstOrDefault(item => item.Id == id);
            if (live?.LifecycleVersion != version) return new(false, LocalizationService.Get("备忘录已变更，请重新打开。"));
            if (live is null || live.IsLifecyclePending) return new(false, LocalizationService.Get("备忘录已删除。"));
            if (reopen && !live.IsTaskClosed) return StorageResult.Ok;
            if (!reopen && statusId is null && live.Kind == kind) return StorageResult.Ok;
            var status = reopen && Catalog.Find(live.LastActiveTaskStatusId) is { IsClosed: false, IsHidden: false } last
                ? last : statusId is null ? Catalog.Default(Settings) : Catalog.Find(statusId);
            if (kind == MemoKind.Task && (status is null || (status.IsHidden && status.Id != live.TaskStatusId && statusId != TaskStatusDefinition.BuiltInId(TaskStatusCategory.Done))))
                return new(false, LocalizationService.Get("请选择有效状态。"));
            if (live.Kind == kind && (kind == MemoKind.Note || live.TaskStatusId == status!.Id)) return StorageResult.Ok;
            var result = await _viewModel.CommitMetadataAsync(item =>
            {
                if (item.Id != id) return null;
                var candidate = item.PersistenceCopy();
                ApplyTransition(candidate, kind, status);
                return candidate;
            });
            if (result.Success && live.IsTaskClosed) ReminderCanceled?.Invoke(id);
            if (result.Success) NotifyMemosChanged();
            return result;
        }
        finally { if (pendingMemo is not null) pendingMemo.IsTaskCommitPending = false; _mutationGate.Release(); }
    }

    private void ApplyTransition(MemoItem memo, MemoKind kind, TaskStatusDefinition? status)
    {
        bool wasClosed = memo.IsTaskClosed;
        var previousCategory = memo.TaskStatusCategorySnapshot;
        string? previousStatus = memo.TaskStatusId;
        memo.Kind = kind;
        if (kind == MemoKind.Note)
        {
            memo.TaskStatusId = null; memo.TaskStatusCategorySnapshot = null;
            memo.TaskClosedAt = null; memo.LastActiveTaskStatusId = null;
        }
        else
        {
            memo.TaskStatusId = status!.Id;
            memo.TaskStatusCategorySnapshot = status.Category;
            if (status.IsClosed)
            {
                if (!wasClosed && previousStatus is not null) memo.LastActiveTaskStatusId = previousStatus;
                if (!wasClosed || previousCategory != status.Category) memo.TaskClosedAt = Clock();
                memo.ReminderAt = null;
            }
            else
            {
                memo.TaskClosedAt = null;
                memo.LastActiveTaskStatusId = status.Id;
            }
        }
        memo.UpdatedAt = Clock();
    }

    public async Task<StorageResult> SetReminderAsync(Guid id, DateTime? reminderAt)
    {
        long version = _viewModel.Memos.FirstOrDefault(item => item.Id == id)?.LifecycleVersion ?? -1;
        await _mutationGate.WaitAsync();
        try
        {
            var memo = _viewModel.Memos.FirstOrDefault(item => item.Id == id);
            if (memo is null || memo.LifecycleVersion != version || !memo.CanRemind) return new(false, LocalizationService.Get("重新打开后可设置提醒"));
            return await _viewModel.CommitMetadataAsync(item =>
            {
                if (item.Id != id || item.ReminderAt == reminderAt) return null;
                var candidate = item.PersistenceCopy(); candidate.ReminderAt = reminderAt; return candidate;
            });
        }
        finally { _mutationGate.Release(); }
    }

    public int UsageCount(string id) => _viewModel.Memos.Count(item => item.IsTask && item.TaskStatusId == id);
    public int ReminderImpact(string id, TaskStatusCategory target) => target is TaskStatusCategory.Done or TaskStatusCategory.Canceled
        ? _viewModel.Memos.Count(item => item.TaskStatusId == id && (item.ReminderAt is not null || HasPendingNotification?.Invoke(item.Id) == true)) : 0;

    public async Task<StorageResult> SaveDefinitionAsync(TaskStatusDefinition draft)
    {
        await _mutationGate.WaitAsync();
        MemoItem[] pendingMemos = [];
        try
        {
            draft = draft with { Name = (draft.Name ?? string.Empty).Trim() };
            _pendingCategoryReplacements.TryGetValue(draft.Id, out var pendingReplacementId);
            if (Catalog.Validate(draft, Settings, pendingReplacementId) is { } error) return new(false, error);
            var original = Catalog.Find(draft.Id);
            if (original is not null && original.Category != draft.Category)
            {
                pendingMemos = MarkMigrationPending(original.Id);
                // A fresh identity makes each crash boundary safe: the old
                // definition keeps its old semantics until all references move.
                var replacement = draft with { Id = pendingReplacementId ?? Guid.NewGuid().ToString("N") };
                if (Catalog.Find(replacement.Id) is { } pending && pending.Category != replacement.Category)
                    return new(false, LocalizationService.Get("迁移尚未完成，请先重试原操作。"));
                if (Catalog.Validate(replacement, Settings, original.Id) is { } replacementError) return new(false, replacementError);
                var added = await Catalog.PersistAsync(Catalog.Statuses.Where(item => item.Id != replacement.Id).Append(replacement));
                if (!added.Success) return added;
                _pendingCategoryReplacements[draft.Id] = replacement.Id;
                var migrated = await MigrateCoreAsync(original, replacement);
                if (migrated.Success) _pendingCategoryReplacements.Remove(draft.Id);
                return migrated;
            }
            return await Catalog.PersistAsync(Catalog.Statuses.Where(item => item.Id != draft.Id).Append(draft));
        }
        finally { foreach (var memo in pendingMemos) memo.IsTaskCommitPending = false; _mutationGate.Release(); }
    }

    public async Task<StorageResult> DeleteDefinitionAsync(string id, string replacementId)
    {
        await _mutationGate.WaitAsync();
        MemoItem[] pendingMemos = [];
        try
        {
            var source = Catalog.Find(id); var replacement = Catalog.Find(replacementId);
            if (source is null || source.IsBuiltIn || replacement is null || replacement.IsHidden || source.Id == replacement.Id)
                return new(false, LocalizationService.Get("请选择有效替代状态；内置状态不能删除。"));
            pendingMemos = MarkMigrationPending(source.Id);
            return await MigrateCoreAsync(source, replacement);
        }
        finally { foreach (var memo in pendingMemos) memo.IsTaskCommitPending = false; _mutationGate.Release(); }
    }

    public async Task<StorageResult> MoveDefinitionAsync(string id, int direction)
    {
        await _mutationGate.WaitAsync();
        try
        {
            var status = Catalog.Find(id);
            if (status is null) return new(false, LocalizationService.Get("请选择有效状态。"));
            var group = Catalog.Statuses.Where(item => item.Category == status.Category).OrderBy(item => item.Order).ToList();
            int index = group.FindIndex(item => item.Id == id), target = index + Math.Sign(direction);
            if (target < 0 || target >= group.Count) return StorageResult.Ok;
            (group[index], group[target]) = (group[target], group[index]);
            var ordered = group.Select((item, order) => item with { Order = order });
            return await Catalog.PersistAsync(Catalog.Statuses.Where(item => item.Category != status.Category).Concat(ordered));
        }
        finally { _mutationGate.Release(); }
    }

    private MemoItem[] MarkMigrationPending(string sourceId)
    {
        var memos = _viewModel.Memos.Where(item => item.TaskStatusId == sourceId || item.LastActiveTaskStatusId == sourceId).ToArray();
        foreach (var memo in memos) memo.IsTaskCommitPending = true;
        return memos;
    }

    private async Task<StorageResult> MigrateCoreAsync(TaskStatusDefinition source, TaskStatusDefinition replacement)
    {
        var fallback = !replacement.IsClosed && !replacement.IsHidden ? replacement.Id :
            Catalog.Selectable.FirstOrDefault(item => item.Id != source.Id && !item.IsClosed)?.Id;
        if (fallback is null) return new(false, LocalizationService.Get("至少保留一个可选的未结束状态。"));
        var ensured = await Catalog.PersistAsync(Catalog.Statuses);
        if (!ensured.Success) return ensured;
        // New memos can be created while the asynchronous migration is saving.
        // Redirect their selections before taking the migration snapshot.
        Catalog.RedirectSelection(source.Id, replacement.Id);
        Catalog.RedirectDefault(source.Id, fallback);
        var affected = _viewModel.Memos.Where(item => item.TaskStatusId == source.Id).Select(item => item.Id).ToArray();
        var migrated = await _viewModel.CommitMetadataAsync(item =>
        {
            if (item.TaskStatusId != source.Id && item.LastActiveTaskStatusId != source.Id) return null;
            var candidate = item.PersistenceCopy();
            if (item.TaskStatusId == source.Id) ApplyTransition(candidate, MemoKind.Task, replacement);
            if (candidate.LastActiveTaskStatusId == source.Id)
                candidate.LastActiveTaskStatusId = !replacement.IsClosed && !replacement.IsHidden ? replacement.Id : null;
            return candidate;
        });
        if (!migrated.Success) return migrated;
        NotifyMemosChanged();
        if (replacement.IsClosed) foreach (var id in affected) ReminderCanceled?.Invoke(id);
        if (Settings.DefaultTaskStatusId == source.Id)
        {
            Settings.DefaultTaskStatusId = fallback;
            DefaultsChanged?.Invoke();
        }
        // Persist even when the current default is already repaired: an earlier
        // failed settings write may still reference the source on disk.
        var settingsSaved = await _saveSettings();
        if (!settingsSaved.Success) return settingsSaved;
        return await Catalog.PersistAsync(Catalog.Statuses.Where(item => item.Id != source.Id));
    }
}
