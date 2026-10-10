using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Image = System.Windows.Controls.Image;

namespace MemeMomo.UI.Animation;

/// <summary>Animates a shared page surface without retaining a second live editor.</summary>
internal sealed class PageTransitionController : IDisposable
{
    private readonly FrameworkElement _viewport;
    private readonly FrameworkElement _content;
    private readonly Image _snapshot;
    private readonly TranslateTransform _contentOffset = new();
    private readonly TranslateTransform _snapshotOffset = new();
    private readonly FrameAnimation _animation;
    private bool _disposed;

    internal PageTransitionController(FrameworkElement viewport, FrameworkElement content, Image snapshot,
        IAnimationFrameSource? frames = null)
    {
        _viewport = viewport;
        _content = content;
        _snapshot = snapshot;
        _animation = new FrameAnimation(frames);
        _content.RenderTransform = _contentOffset;
        _snapshot.RenderTransform = _snapshotOffset;
        _viewport.IsVisibleChanged += OnVisibilityChanged;
        _viewport.Unloaded += OnUnloaded;
        _viewport.SizeChanged += OnSizeChanged;
    }

    internal void CaptureCurrentPage()
    {
        if (_disposed || !MotionPreferences.AnimationsEnabled || !_viewport.IsVisible
            || _viewport.ActualWidth < 1 || _viewport.ActualHeight < 1)
        {
            Reset();
            return;
        }

        // Capture the composite before cancelling: reversing mid-transition keeps
        // the exact visible frame, including the previous outgoing page.
        var dpi = VisualTreeHelper.GetDpi(_viewport);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(_viewport.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(_viewport.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        try
        {
            bitmap.Render(_viewport);
            bitmap.Freeze();
        }
        catch (InvalidOperationException)
        {
            // A transient render failure must not prevent a valid mode change.
            Reset();
            return;
        }

        Reset();
        _snapshot.Source = bitmap;
        _snapshot.Opacity = 1;
        _snapshot.Visibility = Visibility.Visible;
    }

    internal void Start(bool forward)
    {
        if (_disposed || _snapshot.Source is null || !_viewport.IsVisible || !MotionPreferences.AnimationsEnabled)
        {
            Reset();
            return;
        }

        double distance = forward ? 24 : -24;
        _animation.Start(TimeSpan.FromMilliseconds(240), MotionEasing.CubicEaseOut, progress =>
        {
            _content.Opacity = progress;
            _contentOffset.X = distance * (1 - progress);
            _snapshot.Opacity = 1 - progress;
            _snapshotOffset.X = -distance * progress;
        }, Reset);
    }

    internal void Reset()
    {
        _animation.Cancel();
        _content.Opacity = 1;
        _contentOffset.X = 0;
        _snapshot.Visibility = Visibility.Collapsed;
        _snapshot.Source = null;
        _snapshot.Opacity = 0;
        _snapshotOffset.X = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Reset();
        _animation.Dispose();
        _viewport.IsVisibleChanged -= OnVisibilityChanged;
        _viewport.Unloaded -= OnUnloaded;
        _viewport.SizeChanged -= OnSizeChanged;
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_viewport.IsVisible) Reset();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Reset();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Reset();
}
