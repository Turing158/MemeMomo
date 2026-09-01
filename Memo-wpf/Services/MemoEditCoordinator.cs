using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Memo.Services;

public sealed class MemoEditCoordinator
{
    private sealed record Lease(object Owner, Func<Task<bool>> RelinquishAsync);
    private readonly Dictionary<Guid, Lease> _leases = new();
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _acquisitionGate = new(1, 1);

    public static MemoEditCoordinator Shared { get; } = new();

    public async Task<bool> AcquireAsync(Guid memoId, object owner, Func<Task<bool>> relinquishAsync)
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
                if (!_leases.TryGetValue(memoId, out existing))
                {
                    _leases[memoId] = new Lease(owner, relinquishAsync);
                    return true;
                }

                if (ReferenceEquals(existing.Owner, owner))
                {
                    _leases[memoId] = new Lease(owner, relinquishAsync);
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
                _leases[memoId] = new Lease(owner, relinquishAsync);
                return true;
            }
        }
        finally
        {
            _acquisitionGate.Release();
        }
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
