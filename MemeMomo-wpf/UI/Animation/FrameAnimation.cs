namespace MemeMomo.UI.Animation;

internal sealed class FrameAnimation : IDisposable
{
    private readonly FrameAnimationChannel _channel;
    private int _disposed;

    internal FrameAnimation(IAnimationFrameSource? frames = null)
    {
        _channel = new FrameAnimationChannel(frames ?? new CompositionAnimationFrameSource());
    }

    internal bool IsRunning => _channel.IsRunning;
    internal long Generation => _channel.Generation;

    internal long Start(TimeSpan duration, MotionEasing easing, Action<double> update, Action? completed = null) =>
        _channel.Start(MotionPreferences.Effective(duration), easing, update, completed);

    internal void Cancel() => _channel.Cancel();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _channel.Dispose();
        }
    }
}
