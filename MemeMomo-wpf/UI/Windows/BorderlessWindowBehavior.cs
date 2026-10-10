using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MemeMomo.Infrastructure;
using WpfButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace MemeMomo.UI.Windows;

internal sealed class BorderlessWindowBehavior : IDisposable
{
    private const int WmNcHitTest = 0x0084;
    private readonly Window _window;
    private readonly double _resizeBorder;
    private readonly double _resizeCorner;
    private HwndSource? _source;
    private IDisposable? _hookLease;
    private int _disposed;

    internal BorderlessWindowBehavior(
        Window window,
        double resizeBorder = ResizeHitTest.DefaultEdgeThickness,
        double resizeCorner = ResizeHitTest.DefaultCornerThickness)
    {
        _window = window;
        _resizeBorder = resizeBorder;
        _resizeCorner = resizeCorner;
        _window.Closed += OnWindowClosed;
    }

    internal void BeginDrag(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
        {
            _window.DragMove();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _window.Closed -= OnWindowClosed;
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }

        _hookLease?.Dispose();
        _hookLease = null;
    }

    // HwndSource invokes the most recently added hook first. Attach after
    // WindowChrome so its narrow edge regions cannot override our corners.
    internal void Attach()
    {
        if (_source is not null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
        if (_source is null)
        {
            return;
        }

        _source.AddHook(WndProc);
        _hookLease = UiResourceTracker.Acquire(UiResourceKind.NativeWindow);
    }

    private void OnWindowClosed(object? sender, EventArgs e) => Dispose();

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != WmNcHitTest || _window.WindowState == WindowState.Maximized)
        {
            return 0;
        }

        if (_window is BorderlessWindow borderless && borderless.SuppressResizeHitTest)
        {
            handled = true;
            return 1;
        }

        int screenX = unchecked((short)(long)lParam);
        int screenY = unchecked((short)((long)lParam >> 16));
        NativeMethods.GetWindowRect(hwnd, out NativeRect windowRect);
        WpfPoint positionPixels = new(screenX - windowRect.Left, screenY - windowRect.Top);
        WpfPoint positionDip = DpiCoordinateModel.FromVisual(_window).PixelsToDip(positionPixels);
        ResizeEdge edge = ResizeHitTest.Resolve(
            positionDip,
            new WpfSize(_window.ActualWidth, _window.ActualHeight),
            _resizeBorder,
            _resizeCorner);
        if (edge == ResizeEdge.Client)
        {
            return 0;
        }

        handled = true;
        if (_window.ResizeMode is not (ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
            || IsOverButton(positionDip))
        {
            return 1;
        }

        return edge switch
        {
            ResizeEdge.Left => 10,
            ResizeEdge.Right => 11,
            ResizeEdge.Top => 12,
            ResizeEdge.TopLeft => 13,
            ResizeEdge.TopRight => 14,
            ResizeEdge.Bottom => 15,
            ResizeEdge.BottomLeft => 16,
            ResizeEdge.BottomRight => 17,
            _ => 1
        };
    }

    private bool IsOverButton(WpfPoint positionDip)
    {
        if (!_window.IsLoaded)
        {
            return false;
        }

        IInputElement? hit = _window.InputHitTest(positionDip);
        for (DependencyObject? current = hit as DependencyObject;
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is WpfButtonBase)
            {
                return true;
            }
        }

        return false;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }
}
