using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MemeMomo.Services;

public sealed class MemoEditCoordinator
{
    private sealed record Lease(object Owner, Func<Task<bool>> RelinquishAsync,
        Func<Task<string>>? CollectAsync, Action? Resume);
    private readonly Dictionary<Guid, Lease> _leases = new();
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _acquisitionGate = new(1, 1);
    private readonly HashSet<Guid> _lifecycleIds = [];

    public static MemoEditCoordinator Shared { get; } = new();

    public async Task<bool> AcquireAsync(Guid memoId, object owner, Func<Task<bool>> relinquishAsync,
        Func<Task<string>>? collectAsync = null, Action? resume = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(relinquishAsync);

        // Keep the read/relinquish/replace sequence together. Without this
        // gate two duplicate windows can both await the old owner and then
        // overwrite each other's lease when their saves complete out of order.
        await _acquisitionGate.WaitAsync().ConfigureAwait(true);
        try
        {
            Lease? existing;
            lock (_stateGate)
            {
                if (_lifecycleIds.Contains(memoId)) return false;
                if (!_leases.TryGetValue(memoId, out existing))
                {
                    _leases[memoId] = new Lease(owner, relinquishAsync, collectAsync, resume);
                    return true;
                }

                if (ReferenceEquals(existing.Owner, owner))
                {
                    _leases[memoId] = new Lease(owner, relinquishAsync, collectAsync, resume);
                    return true;
                }
            }

            if (!await existing.RelinquishAsync().ConfigureAwait(true))
            {
                return false;
            }

            lock (_stateGate)
            {
                // The relinquish callback may release the old lease itself;
                // assigning here is correct in either case and cannot race a
                // second AcquireAsync because the acquisition gate is held.
                _leases[memoId] = new Lease(owner, relinquishAsync, collectAsync, resume);
                return true;
            }
        }
        finally
        {
            _acquisitionGate.Release();
        }
    }

    // Lock order: editor acquisition/collection, then release this gate, then storage.
    // Never wait for an editor while holding the storage gate.
    internal async Task<LifecycleLease> CollectForLifecycleAsync(Guid id)
    {
        await _acquisitionGate.WaitAsync();
        Lease? lease = null;
        bool reserved = false;
        try
        {
            lock (_stateGate)
            {
                if (!_lifecycleIds.Add(id)) throw new InvalidOperationException(LocalizationService.Get("正在保存，请稍后重试。"));
                reserved = true;
                _leases.TryGetValue(id, out lease);
            }
            string? content = null;
            if (lease?.CollectAsync is { } collect) content = await collect();
            else if (lease is not null && !await lease.RelinquishAsync())
                throw new InvalidOperationException(LocalizationService.Get("保存失败"));
            return new LifecycleLease(this, id, content, lease?.Resume);
        }
        catch
        {
            if (reserved) { lock (_stateGate) _lifecycleIds.Remove(id); }
            lease?.Resume?.Invoke();
            throw;
        }
        finally { _acquisitionGate.Release(); }
    }

    internal sealed class LifecycleLease(MemoEditCoordinator coordinator, Guid id, string? content, Action? resume) : IDisposable
    {
        private bool _disposed;
        public string? Content { get; } = content;
        public bool Committed { get; set; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (coordinator._stateGate)
            {
                coordinator._lifecycleIds.Remove(id);
                if (Committed) coordinator._leases.Remove(id);
            }
            if (!Committed) resume?.Invoke();
        }
    }

    internal bool IsLifecycleReserved(Guid id)
    {
        lock (_stateGate) return _lifecycleIds.Contains(id);
    }

    public void Release(Guid memoId, object owner)
    {
        lock (_stateGate)
        {
            if (_leases.TryGetValue(memoId, out var existing) && ReferenceEquals(existing.Owner, owner))
            {
                _leases.Remove(memoId);
            }
        }
    }

    public bool IsOwner(Guid memoId, object owner)
    {
        lock (_stateGate)
        {
            return _leases.TryGetValue(memoId, out var existing) && ReferenceEquals(existing.Owner, owner);
        }
    }
}
