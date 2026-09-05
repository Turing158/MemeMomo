using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace MemeMomo.UI.Windows;

/// <summary>
/// Owns the physical HWND boundary for docking. Position and window rects are
/// physical pixels; WPF Width/Height remain DIP and are synchronized only at
/// transition endpoints.
/// </summary>
internal sealed class DockWindowAdapter : IDisposable
{
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const int WindingFillMode = 2;

    private readonly Window _window;
    private nint _hwnd;
    private HwndSource? _source;
    private WpfRect? _lastSubmittedBounds;
    private WpfRect? _parkedLogicalBounds;
    private bool _customRegionActive;
    private bool _inputTransparent;
    private int _disposed;

    internal DockWindowAdapter(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _window.SourceInitialized += OnSourceInitialized;
        _window.Closed += OnClosed;
        _hwnd = new WindowInteropHelper(window).Handle;
    }

    internal bool HasHandle => _hwnd != nint.Zero;

    internal bool IsSynchronizingWpf { get; private set; }

    internal bool IsInputTransparent => _inputTransparent;

    internal bool IsParked => _parkedLogicalBounds.HasValue;

    internal WpfRect ToPixelBounds(
        WpfPoint positionPixels,
        double widthDip,
        double heightDip,
        DpiScale2 dpi) =>
        new(
            Math.Round(positionPixels.X),
            Math.Round(positionPixels.Y),
            Math.Max(1, Math.Round(widthDip * Math.Max(0.01, dpi.ScaleX))),
            Math.Max(1, Math.Round(heightDip * Math.Max(0.01, dpi.ScaleY))));

    internal bool Commit(
        WpfPoint positionPixels,
        double widthDip,
        double heightDip,
        DpiScale2 dpi,
        DockOutline? outline,
        bool synchronizeWpf)
    {
        WpfRect bounds = ToPixelBounds(positionPixels, widthDip, heightDip, dpi);

        // The layered handle owns the visible pixels while the main HWND is
        // parked off-screen. Keep receiving logical dock bounds for layout,
        // animation and hit testing without moving the hidden host back under
        // the anti-aliased layered window.
        if (_parkedLogicalBounds.HasValue)
        {
            _parkedLogicalBounds = bounds;
            _lastSubmittedBounds = bounds;
            if (synchronizeWpf)
            {
                // Keep the captured host off-screen for the whole preview
                // drag. Updating Left/Top here lets WPF briefly move the HWND
                // back to the visible logical position before SetWindowPos
                // parks it again, exposing the native background for a frame.
                SynchronizeWpf(bounds, widthDip, heightDip, dpi, synchronizePosition: false);
            }

            if (outline.HasValue)
            {
                ApplyOutline(outline.Value, dpi);
            }

            return MoveParkedWindow(bounds);
        }

        bool submitted = TrySetWindowBounds(bounds);

        if (synchronizeWpf)
        {
            SynchronizeWpf(bounds, widthDip, heightDip, dpi);
            submitted = TrySetWindowBounds(bounds) || submitted;
        }

        if (outline.HasValue)
        {
            ApplyOutline(outline.Value, dpi);
        }

        return submitted;
    }

    internal bool TryGetBounds(out WpfRect bounds)
    {
        bounds = default;
        if (_hwnd == nint.Zero || !GetWindowRect(_hwnd, out NativeRect rect))
        {
            return false;
        }

        bounds = new WpfRect(
            rect.Left,
            rect.Top,
            Math.Max(1, rect.Right - rect.Left),
            Math.Max(1, rect.Bottom - rect.Top));
        return true;
    }

    /// <summary>
    /// Returns the bounds represented by the visible dock surface. When the
    /// WPF host is parked, this is the cached logical dock rectangle rather
    /// than the temporary off-screen HWND rectangle.
    /// </summary>
    internal bool TryGetLogicalBounds(out WpfRect bounds)
    {
        if (_parkedLogicalBounds is WpfRect logical)
        {
            bounds = logical;
            return true;
        }

        return TryGetBounds(out bounds);
    }

    /// <summary>
    /// Moves the opaque WPF host outside the virtual desktop while preserving
    /// its logical bounds for the independent layered dock handle.
    /// </summary>
    internal bool ParkOffscreen()
    {
        if (_hwnd == nint.Zero)
        {
            return false;
        }

        if (_parkedLogicalBounds is null)
        {
            if (!TryGetBounds(out WpfRect actual))
            {
                return false;
            }

            _parkedLogicalBounds = actual;
            _lastSubmittedBounds = actual;
        }

        if (MoveParkedWindow(_parkedLogicalBounds.Value))
        {
            return true;
        }

        _parkedLogicalBounds = null;
        _lastSubmittedBounds = null;
        return false;
    }

    /// <summary>Restores the WPF host to its last logical dock rectangle.</summary>
    internal bool Unpark()
    {
        if (_hwnd == nint.Zero)
        {
            return false;
        }

        if (_parkedLogicalBounds is not WpfRect logical)
        {
            return true;
        }

        _lastSubmittedBounds = null;
        bool restored = TrySetWindowBounds(logical);
        if (restored)
        {
            _parkedLogicalBounds = null;
            _lastSubmittedBounds = logical;
        }

        return restored;
    }

    internal void ClearRegion()
    {
        if (_hwnd != nint.Zero && _customRegionActive)
        {
            _ = SetWindowRgn(_hwnd, nint.Zero, true);
        }

        _customRegionActive = false;
    }

    internal void SetInputTransparent(bool transparent) => _inputTransparent = transparent;

    /// <summary>
    /// Applies the outline that is actually visible after the dock visual's
    /// scale transform. The HWND is not a layered window, so any part of the
    /// client rectangle outside this region can expose the native background.
    /// </summary>
    internal void ApplyVisibleOutline(
        DockOutline outline,
        DpiScale2 dpi,
        double scale,
        WpfPoint scaleOrigin,
        double frameWidth,
        double frameHeight)
    {
        if (!_customRegionActive || _hwnd == nint.Zero)
        {
            return;
        }

        Matrix transform = Matrix.Identity;
        double clampedScale = Math.Clamp(scale, 0.01, 4);
        if (Math.Abs(clampedScale - 1) > 0.0001)
        {
            transform.ScaleAt(
                clampedScale,
                clampedScale,
                scaleOrigin.X * frameWidth,
                scaleOrigin.Y * frameHeight);
        }

        ApplyOutline(outline, dpi, transform);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        ClearRegion();
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }

        _window.SourceInitialized -= OnSourceInitialized;
        _window.Closed -= OnClosed;
        _parkedLogicalBounds = null;
        _hwnd = nint.Zero;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
    }

    private void OnClosed(object? sender, EventArgs e) => Dispose();

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmNcHitTest && _inputTransparent)
        {
            handled = true;
            return HtTransparent;
        }

        return nint.Zero;
    }

    private bool TrySetWindowBounds(WpfRect bounds)
    {
        if (_hwnd == nint.Zero)
        {
            return false;
        }

        if (_lastSubmittedBounds == bounds
            && TryGetBounds(out WpfRect current)
            && BoundsAreClose(current, bounds))
        {
            return true;
        }

        bool submitted = SetWindowPos(
            _hwnd,
            nint.Zero,
            (int)bounds.X,
            (int)bounds.Y,
            (int)bounds.Width,
            (int)bounds.Height,
            SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
        if (!submitted
            || !TryGetBounds(out WpfRect actual)
            || !BoundsAreClose(actual, bounds))
        {
            _lastSubmittedBounds = null;
            return false;
        }

        _lastSubmittedBounds = bounds;
        return true;
    }

    private bool MoveParkedWindow(WpfRect logicalBounds)
    {
        if (_hwnd == nint.Zero)
        {
            return false;
        }

        int virtualLeft = GetSystemMetrics(SystemMetricVirtualScreenLeft);
        int virtualTop = GetSystemMetrics(SystemMetricVirtualScreenTop);
        int width = Math.Max(1, (int)Math.Round(logicalBounds.Width));
        int height = Math.Max(1, (int)Math.Round(logicalBounds.Height));
        int parkedX = virtualLeft - width - 32;
        int parkedY = virtualTop - height - 32;

        return SetWindowPos(
            _hwnd,
            nint.Zero,
            parkedX,
            parkedY,
            width,
            height,
            SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
    }

    private static bool BoundsAreClose(WpfRect actual, WpfRect expected) =>
        Math.Abs(actual.Left - expected.Left) < 1
        && Math.Abs(actual.Top - expected.Top) < 1
        && Math.Abs(actual.Width - expected.Width) < 1
        && Math.Abs(actual.Height - expected.Height) < 1;

    private void SynchronizeWpf(
        WpfRect pixelBounds,
        double widthDip,
        double heightDip,
        DpiScale2 dpi,
        bool synchronizePosition = true)
    {
        IsSynchronizingWpf = true;
        try
        {
            double leftDip = pixelBounds.Left / Math.Max(0.01, dpi.ScaleX);
            double topDip = pixelBounds.Top / Math.Max(0.01, dpi.ScaleY);
            if (synchronizePosition && Math.Abs(_window.Left - leftDip) > 0.01)
            {
                _window.Left = leftDip;
            }

            if (synchronizePosition && Math.Abs(_window.Top - topDip) > 0.01)
            {
                _window.Top = topDip;
            }

            if (Math.Abs(_window.Width - widthDip) > 0.01)
            {
                _window.Width = widthDip;
            }

            if (Math.Abs(_window.Height - heightDip) > 0.01)
            {
                _window.Height = heightDip;
            }
        }
        finally
        {
            IsSynchronizingWpf = false;
        }
    }

    private void ApplyOutline(DockOutline outline, DpiScale2 dpi, Matrix? transform = null)
    {
        if (_hwnd == nint.Zero)
        {
            return;
        }

        PathGeometry flattened = outline.CreateGeometry().GetFlattenedPathGeometry(0.2, ToleranceType.Absolute);
        List<NativePoint> points = [];
        foreach (PathFigure figure in flattened.Figures)
        {
            AddPoint(points, figure.StartPoint, dpi, transform);
            foreach (PathSegment segment in figure.Segments)
            {
                switch (segment)
                {
                    case LineSegment line:
                        AddPoint(points, line.Point, dpi, transform);
                        break;
                    case PolyLineSegment polyLine:
                        foreach (WpfPoint point in polyLine.Points)
                        {
                            AddPoint(points, point, dpi, transform);
                        }
                        break;
                }
            }
        }

        if (points.Count < 3)
        {
            return;
        }

        nint region = CreatePolygonRgn(points.ToArray(), points.Count, WindingFillMode);
        if (region == nint.Zero)
        {
            return;
        }

        if (SetWindowRgn(_hwnd, region, true) != 0)
        {
            _customRegionActive = true;
        }
        else
        {
            _ = DeleteObject(region);
        }
    }

    private static void AddPoint(
        List<NativePoint> points,
        WpfPoint point,
        DpiScale2 dpi,
        Matrix? transform)
    {
        if (transform.HasValue)
        {
            point = transform.Value.Transform(point);
        }

        NativePoint native = new(
            (int)Math.Round(point.X * Math.Max(0.01, dpi.ScaleX)),
            (int)Math.Round(point.Y * Math.Max(0.01, dpi.ScaleY)));
        if (points.Count == 0 || points[^1] != native)
        {
            points.Add(native);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    private const int SystemMetricVirtualScreenLeft = 76;
    private const int SystemMetricVirtualScreenTop = 77;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("gdi32.dll")]
    private static extern nint CreatePolygonRgn(NativePoint[] points, int count, int fillMode);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }
}
