using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using MemeMomo.Infrastructure;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace MemeMomo.UI.Windows;

internal sealed class BorderlessWindowBehavior : IDisposable
{
    private const int WmNcHitTest = 0x0084;
    private readonly Window _window;
    private readonly double _resizeBorder;
    private HwndSource? _source;
    private IDisposable? _hookLease;
    private int _disposed;

    internal BorderlessWindowBehavior(Window window, double resizeBorder = 8)
    {
        _window = window;
        _resizeBorder = resizeBorder;
        _window.SourceInitialized += OnSourceInitialized;
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

        _window.SourceInitialized -= OnSourceInitialized;
        _window.Closed -= OnWindowClosed;
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }

        _hookLease?.Dispose();
        _hookLease = null;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
        _source?.AddHook(WndProc);
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
            return 0;
        }

        int screenX = unchecked((short)(long)lParam);
        int screenY = unchecked((short)((long)lParam >> 16));
        NativeMethods.GetWindowRect(hwnd, out NativeRect windowRect);
        WpfPoint positionPixels = new(screenX - windowRect.Left, screenY - windowRect.Top);
        WpfPoint positionDip = DpiCoordinateModel.FromVisual(_window).PixelsToDip(positionPixels);
        ResizeEdge edge = ResizeHitTest.Resolve(
            positionDip,
            new WpfSize(_window.ActualWidth, _window.ActualHeight),
            _resizeBorder);
        if (edge == ResizeEdge.Client)
        {
            return 0;
        }

        handled = true;
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
