namespace MemeMomo.UI.Animation;

internal enum MotionEasing
{
    CubicEaseIn,
    CubicEaseOut,
    CubicEaseInOut,
    /// <summary>线性：不缓动，供动画在 update 内自行分段塑形。</summary>
    Linear
}

internal sealed class FrameAnimationChannel : IDisposable
{
    private readonly IAnimationFrameSource _frames;
    private Action<double>? _update;
    private Action? _completed;
    private TimeSpan _duration;
    private TimeSpan? _startedAt;
    private MotionEasing _easing;
    private long _generation;
    private int _disposed;

    internal FrameAnimationChannel(IAnimationFrameSource frames)
    {
        _frames = frames;
        _frames.Frame += OnFrame;
    }

    internal long Generation => _generation;

    internal bool IsRunning => _update is not null;

    internal long Start(
        TimeSpan duration,
        MotionEasing easing,
        Action<double> update,
        Action? completed = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Cancel();
        long generation = ++_generation;
        _duration = duration;
        _easing = easing;
        _update = update;
        _completed = completed;
        _startedAt = null;
        MotionPreferences.Changed += OnMotionPreferencesChanged;
        update(Sample(easing, 0));
        if (duration <= TimeSpan.Zero)
        {
            Complete(generation, applyTerminalState: true);
        }
        else
        {
            _frames.Start();
        }

        return generation;
    }

    internal void Cancel()
    {
        if (_update is null)
        {
            return;
        }

        _update = null;
        _completed = null;
        _startedAt = null;
        _frames.Stop();
        _generation++;
        MotionPreferences.Changed -= OnMotionPreferencesChanged;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Cancel();
        _frames.Frame -= OnFrame;
        _frames.Dispose();
        MotionPreferences.Changed -= OnMotionPreferencesChanged;
    }

    internal static double Sample(MotionEasing easing, double progress)
    {
        double t = Math.Clamp(progress, 0, 1);
        return easing switch
        {
            MotionEasing.CubicEaseIn => t * t * t,
            MotionEasing.CubicEaseOut => 1 - Math.Pow(1 - t, 3),
            MotionEasing.CubicEaseInOut => t < 0.5
                ? 4 * t * t * t
                : 1 - Math.Pow(-2 * t + 2, 3) / 2,
            MotionEasing.Linear => t,
            _ => t
        };
    }

    private void OnFrame(object? sender, TimeSpan timestamp)
    {
        Action<double>? update = _update;
        if (update is null)
        {
            return;
        }

        _startedAt ??= timestamp;
        double progress = _duration.Ticks == 0
            ? 1
            : (timestamp - _startedAt.Value).Ticks / (double)_duration.Ticks;
        update(Sample(_easing, progress));
        if (progress >= 1)
        {
            Complete(_generation, applyTerminalState: false);
        }
    }

    private void Complete(long generation, bool applyTerminalState)
    {
        if (generation != _generation)
        {
            return;
        }

        Action<double>? update = _update;
        Action? completed = _completed;
        if (applyTerminalState)
        {
            update?.Invoke(Sample(_easing, 1));
        }

        _update = null;
        _completed = null;
        _startedAt = null;
        _frames.Stop();
        MotionPreferences.Changed -= OnMotionPreferencesChanged;
        completed?.Invoke();
    }

    private void OnMotionPreferencesChanged(object? sender, EventArgs e)
    {
        if (IsRunning && !MotionPreferences.AnimationsEnabled)
        {
            Complete(_generation, applyTerminalState: true);
        }
    }
}
