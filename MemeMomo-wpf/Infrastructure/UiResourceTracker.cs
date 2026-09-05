namespace MemeMomo.Infrastructure;

internal enum UiResourceKind
{
    DispatcherOperation,
    DispatcherTimer,
    RenderingSubscription,
    SystemEventsSubscription,
    GlobalEventSubscription,
    NativeWindow
}

internal sealed record UiResourceSnapshot(IReadOnlyDictionary<UiResourceKind, int> Counts)
{
    public int this[UiResourceKind kind] => Counts.TryGetValue(kind, out int count) ? count : 0;
}

internal static class UiResourceTracker
{
    private static readonly int[] Counts = new int[Enum.GetValues<UiResourceKind>().Length];

    internal static IDisposable Acquire(UiResourceKind kind)
    {
        Interlocked.Increment(ref Counts[(int)kind]);
        return new ResourceLease(kind);
    }

    internal static UiResourceSnapshot Capture()
    {
        Dictionary<UiResourceKind, int> counts = Enum
            .GetValues<UiResourceKind>()
            .ToDictionary(kind => kind, kind => Volatile.Read(ref Counts[(int)kind]));
        return new UiResourceSnapshot(counts);
    }

    private sealed class ResourceLease(UiResourceKind kind) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref Counts[(int)kind]);
            }
        }
    }
}
