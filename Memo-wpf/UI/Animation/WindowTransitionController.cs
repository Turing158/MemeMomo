using System.Windows;
using System.Windows.Media;
using WpfPoint = System.Windows.Point;

namespace Memo.UI.Animation;

internal sealed class WindowTransitionController : IDisposable
{
    private readonly Window _window;
    private readonly FrameworkElement _shell;
    private FrameAnimation? _animation;
    private ScaleTransform? _scale;
    private int _disposed;
    private bool _closeRequested;

    internal WindowTransitionController(Window window, FrameworkElement shell)
    {
        _window = window;
        _shell = shell;
        _window.Closed += OnWindowClosed;
    }

    internal bool IsTransitioning => _animation?.IsRunning == true;

    internal void PrepareOpen()
    {
        Cancel();
        _closeRequested = false;
        if (!MotionPreferences.AnimationsEnabled)
        {
            Reset();
            return;
        }

        _window.Opacity = 0;
        SetScale(0.97);
    }

    internal void PlayOpen(Action? completed = null)
    {
        if (!MotionPreferences.AnimationsEnabled)
        {
            Reset();
            completed?.Invoke();
            return;
        }

        Play(TimeSpan.FromMilliseconds(190), WindowTransitionSampler.Open, MotionEasing.CubicEaseOut, completed);
    }

    internal void CloseAfterTransition(Action close)
    {
        ArgumentNullException.ThrowIfNull(close);
        if (_closeRequested)
        {
            return;
        }

        _closeRequested = true;
        if (!MotionPreferences.AnimationsEnabled)
        {
            Reset();
            close();
            return;
        }

        Play(TimeSpan.FromMilliseconds(150), WindowTransitionSampler.Close, MotionEasing.CubicEaseIn, close);
    }

    internal void Cancel()
    {
        _animation?.Dispose();
        _animation = null;
    }

    internal void Reset()
    {
        _window.Opacity = 1;
        SetScale(1);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _window.Closed -= OnWindowClosed;
            Cancel();
            Reset();
        }
    }

    private void Play(TimeSpan duration, Func<double, WindowTransitionFrame> sampler, MotionEasing easing, Action? completed)
    {
        Cancel();
        _animation = new FrameAnimation();
        _animation.Start(duration, easing, progress => Apply(sampler(progress)), () =>
        {
            FrameAnimation? animation = _animation;
            _animation = null;
            animation?.Dispose();
            completed?.Invoke();
        });
    }

    private void Apply(WindowTransitionFrame frame)
    {
        _window.Opacity = frame.Opacity;
        SetScale(frame.Scale);
    }

    private void SetScale(double value)
    {
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
