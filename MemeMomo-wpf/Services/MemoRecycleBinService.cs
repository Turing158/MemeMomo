using MemeMomo.ViewModels;

namespace MemeMomo.Services;

internal enum RecycleOperation { Delete, Restore, Purge }

public sealed class MemoRecycleBinService
{
    private readonly MainViewModel _viewModel;
    private readonly HashSet<Task<StorageResult>> _pending = [];
    private readonly object _pendingGate = new();
    internal MemoRecycleBinService(MainViewModel viewModel) => _viewModel = viewModel;
    public Task<StorageResult> DeleteAsync(Guid id) => RunAsync([id], RecycleOperation.Delete);
    public Task<StorageResult> RestoreAsync(Guid id) => RunAsync([id], RecycleOperation.Restore);
    public Task<StorageResult> PurgeAsync(Guid id) => ClearAsync([id]);
    public Task<StorageResult> ClearAsync(IReadOnlyCollection<Guid> confirmedIds) =>
        RunAsync(confirmedIds, RecycleOperation.Purge);

    internal Task WaitForIdleAsync()
    {
        lock (_pendingGate) return Task.WhenAll(_pending.ToArray());
    }
    private async Task<StorageResult> RunAsync(IReadOnlyCollection<Guid> ids, RecycleOperation operation)
    {
        var task = _viewModel.CommitLifecycleAsync(ids, operation);
        lock (_pendingGate) _pending.Add(task);
        try { return await task; }
        finally { lock (_pendingGate) _pending.Remove(task); }
    }
}
