using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Image = System.Windows.Controls.Image;

namespace MemeMomo.UI.Animation;

/// <summary>Blends the visible layout into the new panes without stretching text.</summary>
internal sealed class PaneLayoutTransition : IDisposable
{
    private readonly FrameworkElement _surface;
    private readonly FrameworkElement _viewport;
    private readonly Image _snapshot;
    private readonly Pane[] _panes;
    private readonly FrameAnimation _animation;
    private bool _disposed;

    internal sealed record CapturedLayout(BitmapSource Bitmap, Rect[] Bounds);

    internal PaneLayoutTransition(FrameworkElement surface, FrameworkElement viewport, Image snapshot,
        FrameworkElement[] panes, IAnimationFrameSource? frames = null)
    {
        _surface = surface;
        _viewport = viewport;
        _snapshot = snapshot;
        _panes = panes.Select(element => new Pane(element)).ToArray();
        _animation = new FrameAnimation(frames);
    }

    internal CapturedLayout? Capture()
    {
        if (_disposed || !MotionPreferences.AnimationsEnabled || !_viewport.IsVisible
            || _viewport.ActualWidth < 1 || _viewport.ActualHeight < 1) return null;

        var bounds = _panes.Select(pane => pane.Element.TransformToAncestor(_surface)
            .TransformBounds(new Rect(pane.Element.RenderSize))).ToArray();
        var dpi = VisualTreeHelper.GetDpi(_viewport);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(_viewport.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(_viewport.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        try
        {
            // Includes the previous outgoing frame on a rapid reversal. Capture
            // before Reset so the next transition starts at the visible composite.
            bitmap.Render(_viewport);
            bitmap.Freeze();
            return new CapturedLayout(bitmap, bounds);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    internal void AnimateFrom(CapturedLayout capture)
    {
        Reset();
        if (_disposed || !MotionPreferences.AnimationsEnabled || !_viewport.IsVisible) return;
        _snapshot.Source = capture.Bitmap;
        // Keep the old frame at its original DIP size while the window continues
        // resizing. The viewport clips it; Stretch must never distort its text.
        _snapshot.Width = capture.Bitmap.Width;
        _snapshot.Height = capture.Bitmap.Height;
        _snapshot.Visibility = Visibility.Visible;
        for (int i = 0; i < _panes.Length; i++)
        {
            var pane = _panes[i];
            Vector target = VisualTreeHelper.GetOffset(pane.Element);
            pane.From = new Vector(Math.Clamp(capture.Bounds[i].X - target.X, -24, 24),
                Math.Clamp(capture.Bounds[i].Y - target.Y, -16, 16));
            pane.Attach();
        }
        _animation.Start(TimeSpan.FromMilliseconds(240), MotionEasing.CubicEaseOut, progress =>
        {
            _snapshot.Opacity = 1 - progress;
            foreach (var pane in _panes)
            {
                pane.Offset.X = pane.From.X * (1 - progress);
                pane.Offset.Y = pane.From.Y * (1 - progress);
                pane.Element.SetCurrentValue(UIElement.OpacityProperty, pane.OriginalOpacity * progress);
            }
        }, Reset);
    }

    internal void Reset()
    {
        _animation.Cancel();
        foreach (var pane in _panes) pane.Restore();
        _snapshot.Source = null;
        _snapshot.Visibility = Visibility.Collapsed;
        _snapshot.Opacity = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Reset();
        _animation.Dispose();
    }

    private sealed class Pane(FrameworkElement element)
    {
        private Transform? _originalTransform;
        internal FrameworkElement Element { get; } = element;
        internal TranslateTransform Offset { get; } = new();
        internal Vector From { get; set; }
        internal double OriginalOpacity { get; private set; }

        internal void Attach()
        {
            _originalTransform = Element.RenderTransform;
            OriginalOpacity = Element.Opacity;
            Element.SetCurrentValue(UIElement.RenderTransformProperty, Offset);
        }

        internal void Restore()
        {
            if (_originalTransform is null) return;
            if (ReferenceEquals(Element.RenderTransform, Offset))
                Element.SetCurrentValue(UIElement.RenderTransformProperty, _originalTransform);
            Element.SetCurrentValue(UIElement.OpacityProperty, OriginalOpacity);
            _originalTransform = null;
            Offset.X = Offset.Y = 0;
        }
    }
}
