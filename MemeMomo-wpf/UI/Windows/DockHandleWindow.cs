using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MemeMomo.Infrastructure;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace MemeMomo.UI.Windows;

internal interface IDockHandleWindowEvents
{
    bool HitTest(int xPixels, int yPixels);
    void LeftButtonDown(WpfPoint screenPoint);
    void MouseMove(WpfPoint screenPoint, bool leftButtonPressed);
    void LeftButtonUp(WpfPoint screenPoint);
    void RightButtonDown(WpfPoint screenPoint);
    void RightButtonUp(WpfPoint screenPoint);
    void CaptureLost();
    void EnvironmentChanged();
}

internal interface IDockHandleNativeWindow : IDisposable
{
    bool IsVisible { get; }
    bool TryUpdate(DockHandleBitmap bitmap, WpfPoint positionPixels, out int error);
    void Show();
    void Hide();
    void SetTopmost(bool topmost);
    void ReleaseMouseCapture();
}

/// <summary>Owns the no-activate WS_POPUP used by the layered dock handle.</summary>
internal sealed class DockHandleWindow : IDockHandleNativeWindow
{
    internal const int WindowStyle = unchecked((int)0x80000000);
    internal const int ExtendedWindowStyle = 0x00080000 | 0x00000080 | 0x08000000;

    private const string WindowClassName = "MemeMomo.DockHandle.Layered";
    private const int WmDestroy = 0x0002;
    private const int WmCancelMode = 0x001F;
    private const int WmSettingChange = 0x001A;
    private const int WmDisplayChange = 0x007E;
    private const int WmNcHitTest = 0x0084;
    private const int WmMouseActivate = 0x0021;
    private const int WmMouseMove = 0x0200;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmRightButtonDown = 0x0204;
    private const int WmRightButtonUp = 0x0205;
    private const int WmCaptureChanged = 0x0215;
    private const int WmDpiChanged = 0x02E0;
    private const int HtClient = 1;
    private const int HtTransparent = -1;
    private const int MaNoActivate = 3;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const nint HwndTopmost = -1;
    private const nint HwndNoTopmost = -2;
    private const int ErrorClassAlreadyExists = 1410;

    private static readonly ConcurrentDictionary<nint, WeakReference<DockHandleWindow>> Windows = new();
    private static readonly WndProcDelegate WindowProcedure = StaticWndProc;
    private static readonly object RegistrationGate = new();
    private static bool s_registered;

    private readonly IDockHandleWindowEvents _events;
    private readonly IDisposable _windowLease;
    private nint _hwnd;
    private bool _releasingCapture;
    private int _disposed;

    private DockHandleWindow(nint owner, IDockHandleWindowEvents events)
    {
        _events = events;
        EnsureWindowClass();
        _hwnd = CreateWindowExW(
            ExtendedWindowStyle,
            WindowClassName,
            "MemeMomo Dock Handle",
            WindowStyle,
            0,
            0,
            1,
            1,
            owner,
            nint.Zero,
            GetModuleHandleW(null),
            nint.Zero);
        if (_hwnd == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create the layered dock handle window.");
        }

        Windows[_hwnd] = new WeakReference<DockHandleWindow>(this);
        _windowLease = UiResourceTracker.Acquire(UiResourceKind.NativeWindow);
    }

    internal nint Handle => _hwnd;

    public bool IsVisible => _hwnd != nint.Zero && IsWindowVisible(_hwnd);

    internal static bool TryCreate(
        nint owner,
        IDockHandleWindowEvents events,
        out DockHandleWindow? window,
        out Exception? error)
    {
        try
        {
            window = new DockHandleWindow(owner, events);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or ExternalException)
        {
            window = null;
            error = ex;
            return false;
        }
    }

    public bool TryUpdate(DockHandleBitmap bitmap, WpfPoint positionPixels, out int error)
    {
        if (_hwnd == nint.Zero)
        {
            error = 1400;
            return false;
        }

        return DockHandleLayeredSurface.TryUpdate(
            _hwnd,
            bitmap,
            new WpfRect(positionPixels.X, positionPixels.Y, bitmap.PixelWidth, bitmap.PixelHeight),
            out error);
    }

    public void Show()
    {
        if (_hwnd != nint.Zero && !IsWindowVisible(_hwnd))
        {
            _ = ShowWindow(_hwnd, SwShowNoActivate);
        }
    }

    public void Hide()
    {
        ReleaseMouseCapture();
        if (_hwnd != nint.Zero && IsWindowVisible(_hwnd))
        {
            _ = ShowWindow(_hwnd, SwHide);
        }
    }

    public void SetTopmost(bool topmost)
    {
        if (_hwnd != nint.Zero)
        {
            _ = SetWindowPos(
                _hwnd,
                topmost ? HwndTopmost : HwndNoTopmost,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate);
        }
    }

    public void ReleaseMouseCapture()
    {
        if (_hwnd == nint.Zero || GetCapture() != _hwnd)
        {
            return;
        }

        _releasingCapture = true;
        try
        {
            _ = ReleaseCapture();
        }
        finally
        {
            _releasingCapture = false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        ReleaseMouseCapture();
        nint hwnd = _hwnd;
        _hwnd = nint.Zero;
        if (hwnd != nint.Zero)
        {
            Windows.TryRemove(hwnd, out _);
            if (IsWindow(hwnd))
            {
                _ = DestroyWindow(hwnd);
            }
        }

        _windowLease.Dispose();
    }

    private static void EnsureWindowClass()
    {
        lock (RegistrationGate)
        {
            if (s_registered)
            {
                return;
            }

            WindowClass windowClass = new()
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                WindowProcedure = WindowProcedure,
                Instance = GetModuleHandleW(null),
                Cursor = LoadCursorW(nint.Zero, new nint(32512)),
                ClassName = WindowClassName
            };
            if (RegisterClassExW(ref windowClass) == 0)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != ErrorClassAlreadyExists)
                {
                    throw new Win32Exception(error, "Unable to register the layered dock handle window class.");
                }
            }

            s_registered = true;
        }
    }

    private static nint StaticWndProc(nint hwnd, int message, nint wParam, nint lParam)
    {
        if (Windows.TryGetValue(hwnd, out WeakReference<DockHandleWindow>? reference)
            && reference.TryGetTarget(out DockHandleWindow? window))
        {
            return window.WndProc(message, wParam, lParam);
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private nint WndProc(int message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WmNcHitTest:
                {
                    NativePoint point = ScreenPointFromMessage(lParam);
                    if (!ScreenToClient(_hwnd, ref point))
                    {
                        return HtTransparent;
                    }

                    return _events.HitTest(point.X, point.Y) ? HtClient : HtTransparent;
                }
            case WmMouseActivate:
                return MaNoActivate;
            case WmLeftButtonDown:
                _ = SetCapture(_hwnd);
                _events.LeftButtonDown(CurrentCursor());
                return 0;
            case WmMouseMove:
                _events.MouseMove(CurrentCursor(), GetCapture() == _hwnd);
                return 0;
            case WmLeftButtonUp:
                _events.LeftButtonUp(CurrentCursor());
                ReleaseMouseCapture();
                return 0;
            case WmRightButtonDown:
                _events.RightButtonDown(CurrentCursor());
                return 0;
            case WmRightButtonUp:
                _events.RightButtonUp(CurrentCursor());
                return 0;
            case WmCancelMode:
                ReleaseMouseCapture();
                _events.CaptureLost();
                return 0;
            case WmCaptureChanged:
                if (!_releasingCapture)
                {
                    _events.CaptureLost();
                }

                return 0;
            case WmDpiChanged:
            case WmDisplayChange:
            case WmSettingChange:
                _events.EnvironmentChanged();
                return 0;
            case WmDestroy:
                Windows.TryRemove(_hwnd, out _);
                break;
        }

        return DefWindowProcW(_hwnd, message, wParam, lParam);
    }

    private static WpfPoint CurrentCursor()
    {
        return GetCursorPos(out NativePoint point)
            ? new WpfPoint(point.X, point.Y)
            : default;
    }

    private static NativePoint ScreenPointFromMessage(nint lParam) => new(
        unchecked((short)(long)lParam),
        unchecked((short)((long)lParam >> 16)));

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProcDelegate(nint hwnd, int message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        internal uint Size;
        internal uint Style;
        internal WndProcDelegate WindowProcedure;
        internal int ClassExtra;
        internal int WindowExtra;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint Background;
        internal string? MenuName;
        internal string ClassName;
        internal nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;

        internal NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        int extendedStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint hwnd, int message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

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

    [DllImport("user32.dll")]
    private static extern nint SetCapture(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetCapture();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint hwnd, ref NativePoint point);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll")]
    private static extern nint LoadCursorW(nint instance, nint cursorName);
}
