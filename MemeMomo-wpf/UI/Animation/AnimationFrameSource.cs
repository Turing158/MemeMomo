using System.Windows.Media;
using MemeMomo.Infrastructure;

namespace MemeMomo.UI.Animation;

internal interface IAnimationFrameSource : IDisposable
{
    event EventHandler<TimeSpan>? Frame;
    void Start();
    void Stop();
}

internal sealed class CompositionAnimationFrameSource : IAnimationFrameSource
{
    private IDisposable? _lease;
    private bool _running;
    private int _disposed;

    public event EventHandler<TimeSpan>? Frame;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_running)
        {
            return;
        }

        CompositionTarget.Rendering += OnRendering;
        _lease = UiResourceTracker.Acquire(UiResourceKind.RenderingSubscription);
        _running = true;
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        CompositionTarget.Rendering -= OnRendering;
        _lease?.Dispose();
        _lease = null;
        _running = false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Stop();
            Frame = null;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs rendering)
        {
            Frame?.Invoke(this, rendering.RenderingTime);
        }
    }
}

internal sealed class ManualAnimationFrameSource : IAnimationFrameSource
{
    private bool _running;

    public event EventHandler<TimeSpan>? Frame;

    public void Start() => _running = true;

    public void Stop() => _running = false;

    public void AdvanceTo(TimeSpan timestamp)
    {
        if (_running)
        {
            Frame?.Invoke(this, timestamp);
        }
    }

    public void Dispose()
    {
        _running = false;
        Frame = null;
    }
}
