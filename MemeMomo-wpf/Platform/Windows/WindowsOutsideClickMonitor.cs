using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using MemeMomo.Infrastructure;

namespace MemeMomo.Platform.Windows;

/// <summary>Observes outside clicks while a non-activating flyout is open.</summary>
internal sealed class WindowsOutsideClickMonitor : IDisposable
{
    private const int WhMouseLl = 14;
    private readonly nint _window;
    private readonly Dispatcher _dispatcher;
    private readonly Action _outsideClick;
    private readonly MouseHookCallback _callback;
    private nint _hook;
    private IDisposable? _hookLease;
    private DispatcherOperation? _pendingDismissal;

    private WindowsOutsideClickMonitor(nint window, Dispatcher dispatcher, Action outsideClick)
    {
        _window = window;
        _dispatcher = dispatcher;
        _outsideClick = outsideClick;
        _callback = OnMouseInput;
    }

    internal static WindowsOutsideClickMonitor? TryStart(nint window, Dispatcher dispatcher, Action outsideClick)
    {
        dispatcher.VerifyAccess();
        if (window == nint.Zero)
        {
            return null;
        }

        WindowsOutsideClickMonitor monitor = new(window, dispatcher, outsideClick);
        monitor._hook = SetWindowsHookEx(WhMouseLl, monitor._callback, GetModuleHandle(null), 0);
        if (monitor._hook == nint.Zero)
        {
            Debug.WriteLine($"[Tray] outside-click monitoring failed: {Marshal.GetLastWin32Error()}");
            return null;
        }

        monitor._hookLease = UiResourceTracker.Acquire(UiResourceKind.GlobalEventSubscription);
        return monitor;
    }

    private nint OnMouseInput(int code, nint message, nint data)
    {
        // Observe button presses only; never consume the click intended for another app.
        if (code >= 0 && _hook != nint.Zero && _pendingDismissal is null
            && (int)message is 0x0201 or 0x0204 or 0x0207 or 0x020B
            && GetWindowRect(_window, out NativeRect bounds))
        {
            // MSLLHOOKSTRUCT begins with the pointer's physical screen coordinates.
            NativePoint point = Marshal.PtrToStructure<NativePoint>(data);
            bool inside = point.X >= bounds.Left && point.X < bounds.Right
                && point.Y >= bounds.Top && point.Y < bounds.Bottom;
            if (!inside && !_dispatcher.HasShutdownStarted)
            {
                _pendingDismissal = _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    _pendingDismissal = null;
                    if (_hook != nint.Zero)
                    {
                        _outsideClick();
                    }
                }));
            }
        }

        return CallNextHookEx(nint.Zero, code, message, data);
    }

    public void Dispose()
    {
        _pendingDismissal?.Abort();
        _pendingDismissal = null;
        nint hook = _hook;
        _hook = nint.Zero;
        if (hook != nint.Zero)
        {
            UnhookWindowsHookEx(hook);
        }

        _hookLease?.Dispose();
        _hookLease = null;
    }

    private delegate nint MouseHookCallback(int code, nint message, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hookId, MouseHookCallback callback, nint module, uint threadId);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect bounds);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
