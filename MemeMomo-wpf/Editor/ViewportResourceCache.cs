using System.Collections.Concurrent;

namespace MemeMomo.Editor;

internal sealed class ViewportResourceCache<T>(
    Func<Uri, CancellationToken, Task<T>> loader) : IDisposable
{
    private readonly ConcurrentDictionary<Uri, Lazy<Task<ResourceResult<T>>>> _entries = new();
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    internal int CachedResourceCount => _entries.Count;

    internal Task<ResourceResult<T>?> GetAsync(Uri uri, bool isInViewport)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!isInViewport)
        {
            return Task.FromResult<ResourceResult<T>?>(null);
        }

        Lazy<Task<ResourceResult<T>>> entry = _entries.GetOrAdd(
            uri,
            key => new Lazy<Task<ResourceResult<T>>>(
                () => LoadAsync(key, _lifetime.Token),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return AwaitResultAsync(entry.Value);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
        _entries.Clear();
    }

    private async Task<ResourceResult<T>> LoadAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            return ResourceResult<T>.Success(await loader(uri, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ResourceResult<T>.Failure(exception);
        }
    }

    private static async Task<ResourceResult<T>?> AwaitResultAsync(Task<ResourceResult<T>> result) =>
        await result;
}

internal sealed record ResourceResult<T>(T? Value, Exception? Error)
{
    internal bool IsSuccess => Error is null;

    internal static ResourceResult<T> Success(T value) => new(value, null);

    internal static ResourceResult<T> Failure(Exception error) => new(default, error);
}
