using System.Windows;
using System.Windows.Media;
using WpfPoint = System.Windows.Point;

namespace MemeMomo.UI.Animation;

internal sealed class WindowTransitionController : IDisposable
{
    private readonly Window _window;
    private readonly FrameworkElement _shell;
    private readonly WindowTransitionProfile _profile;
    private readonly FrameAnimation _animation;
    private ScaleTransform? _scale;
    private int _disposed;
    private bool _closeRequested;

    internal WindowTransitionController(
        Window window,
        FrameworkElement shell,
        WindowTransitionProfile? profile = null,
        IAnimationFrameSource? frames = null)
    {
        _window = window;
        _shell = shell;
        _scale = shell.RenderTransform as ScaleTransform;
        _profile = profile ?? WindowTransitionProfile.Default;
        _animation = new FrameAnimation(frames);
        _window.Closed += OnWindowClosed;
    }

    internal bool IsTransitioning => _animation.IsRunning;
    internal bool IsCloseRequested => _closeRequested;

    internal void PrepareOpen()
    {
        if (_disposed != 0 || (_window.IsVisible && IsTransitioning))
        {
            return;
        }

        Cancel();
        if (!MotionPreferences.AnimationsEnabled)
        {
            Reset();
            return;
        }

        Apply(_profile.OpenStart);
    }

    internal void PlayOpen(Action? completed = null)
    {
        if (_disposed != 0)
        {
            return;
        }

        Cancel();
        if (!MotionPreferences.AnimationsEnabled)
        {
            Reset();
            completed?.Invoke();
            return;
        }

        Play(_profile.OpenDuration, new WindowTransitionFrame(1, 1), _profile.OpenEasing, completed);
    }

    internal void CloseAfterTransition(Action close)
    {
        ArgumentNullException.ThrowIfNull(close);
        if (_disposed != 0 || _closeRequested)
        {
            return;
        }

        Cancel();
        _closeRequested = true;
        if (!MotionPreferences.AnimationsEnabled)
        {
            Apply(_profile.CloseEnd);
            close();
            return;
        }

        Play(_profile.CloseDuration, _profile.CloseEnd, _profile.CloseEasing, close);
    }

    internal void Cancel()
    {
        _animation.Cancel();
        _closeRequested = false;
    }

    internal void Reset()
    {
        Cancel();
        _window.Opacity = 1;
        SetScale(1);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _window.Closed -= OnWindowClosed;
            _animation.Dispose();
        }
    }

    private void Play(TimeSpan duration, WindowTransitionFrame target, MotionEasing easing, Action? completed)
    {
        WindowTransitionFrame from = new(_window.Opacity, _scale?.ScaleX ?? 1);
        double remaining = Math.Clamp(Math.Abs(target.Opacity - from.Opacity), 0, 1);
        TimeSpan remainingDuration = TimeSpan.FromTicks((long)(duration.Ticks * remaining));

        // The frame channel applies easing once; replacement starts at the visible frame.
        _animation.Start(
            remainingDuration,
            easing,
            progress => Apply(WindowTransitionSampler.Interpolate(from, target, progress)),
            completed);
    }

    private void Apply(WindowTransitionFrame frame)
    {
        _window.Opacity = frame.Opacity;
        SetScale(frame.Scale);
    }

    private void SetScale(double value)
    {
        if (_scale is null && value == 1)
        {
            return;
        }

        _scale ??= CreateScaleTransform();
        _scale.ScaleX = value;
        _scale.ScaleY = value;
    }

    private ScaleTransform CreateScaleTransform()
    {
        if (_shell.RenderTransform is ScaleTransform existing)
        {
            return existing;
        }

        ScaleTransform transform = new(1, 1);
        _shell.RenderTransformOrigin = new WpfPoint(0.5, 0.5);
        _shell.RenderTransform = transform;
        return transform;
    }

    private void OnWindowClosed(object? sender, EventArgs e) => Dispose();
}
