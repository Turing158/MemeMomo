using System.Collections.ObjectModel;
using MemeMomo.Models;
using MemeMomo.Services;
using MemeMomo.Utils;

namespace MemeMomo.ViewModels;

public partial class MainViewModel
{
    private readonly ObservableCollection<MemoItem> _deletedMemos = [];
    public ReadOnlyObservableCollection<MemoItem> DeletedMemos { get; }
    private MemoItem[] FullSnapshot() => Memos.Concat(_deletedMemos).Select(item => item.PersistenceCopy()).ToArray();

    public Task UpdateItemAndSaveAsync(Guid id, string content, long lifecycleVersion)
    {
        var item = Memos.FirstOrDefault(item => item.Id == id);
        if (item is null || item.IsLifecyclePending || item.LifecycleVersion != lifecycleVersion)
            throw new InvalidOperationException(LocalizationService.Get("备忘录已变更，请重新打开。"));
        return UpdateItemAndSaveAsync(id, content);
    }

    internal async Task<StorageResult> CommitLifecycleAsync(IReadOnlyCollection<Guid> ids, RecycleOperation operation)
    {
        bool deleting = operation == RecycleOperation.Delete;
        var source = deleting ? Memos : _deletedMemos;
        var targets = source.Where(item => ids.Contains(item.Id)).ToArray();
        if (targets.Length == 0) return new(false, LocalizationService.Get("记录已被处理。"));
        if (targets.Any(item => item.IsLifecyclePending || item.IsTaskCommitPending || MemoEditCoordinator.Shared.IsLifecycleReserved(item.Id)))
            return new(false, LocalizationService.Get("正在保存，请稍后重试。"));
        foreach (var item in targets) item.IsLifecyclePending = true;
        MemoEditCoordinator.LifecycleLease? lease = null;
        bool gateHeld = false;
        try
        {
            if (deleting) lease = await MemoEditCoordinator.Shared.CollectForLifecycleAsync(targets[0].Id);
            await _saveGate.WaitAsync();
            gateHeld = true;
            // Pending flags reserve precisely the confirmation's IDs, including across editor collection.
            if (targets.Any(item => !source.Contains(item))) return new(false, LocalizationService.Get("记录已被处理。"));
            var changes = targets.ToDictionary(item => item.Id, item => item.PersistenceCopy());
            bool expired = false;
            foreach (var candidate in changes.Values)
            {
                if (deleting)
                {
                    if (lease?.Content is { } content && candidate.Content != content)
                    { candidate.Content = content; candidate.UpdatedAt = DateTimeUtils.Now; }
                    candidate.DeletedAt = DateTimeOffset.UtcNow;
                }
                else if (operation == RecycleOperation.Restore)
                {
                    candidate.DeletedAt = null;
                    expired |= candidate.ReminderAt is { } at && at <= DateTimeUtils.Now;
                    if (candidate.IsTaskClosed || candidate.ReminderAt <= DateTimeUtils.Now) candidate.ReminderAt = null;
                }
            }
            var snapshot = FullSnapshot().Where(item => operation != RecycleOperation.Purge || !changes.ContainsKey(item.Id))
                .Select(item => changes.GetValueOrDefault(item.Id) ?? item).ToList();
            if (operation == RecycleOperation.Restore)
                snapshot = changes.Values.Concat(snapshot.Where(item => !changes.ContainsKey(item.Id))).ToList();
            // Always persist normal records first; their manual order is independent of deleted-date sorting.
            snapshot = snapshot.Where(item => !item.IsDeleted).Concat(snapshot.Where(item => item.IsDeleted)).ToList();
            var result = await _storage.TrySaveAsync(snapshot);
            if (!result.Success) return result;
            foreach (var item in targets)
            {
                item.LifecycleVersion++;
                source.Remove(item);
                if (operation != RecycleOperation.Purge)
                {
                    var candidate = changes[item.Id];
                    item.Content = candidate.Content;
                    item.UpdatedAt = candidate.UpdatedAt;
                    item.ReminderAt = candidate.ReminderAt;
                    item.DeletedAt = candidate.DeletedAt;
                    if (deleting) _deletedMemos.Add(item); else Memos.Insert(0, item);
                }
            }
            if (lease is not null) lease.Committed = true;
            if (deleting)
            {
                var id = targets[0].Id;
                if (EditingId == id) EditingId = null;
                MemoDeleted?.Invoke(id);
            }
            return new(true, expired ? LocalizationService.Get("原提醒已过期，请重新设置") : null);
        }
        catch (Exception error) { return new(false, error.Message); }
        finally
        {
            if (gateHeld) _saveGate.Release();
            lease?.Dispose();
            foreach (var item in targets) item.IsLifecyclePending = false;
        }
    }
}
