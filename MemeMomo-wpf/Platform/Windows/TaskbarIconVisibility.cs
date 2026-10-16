using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MemeMomo.Platform.Windows;

/// <summary>
/// Changes taskbar presence through the existing HWND. No WPF window is
/// recreated, so bounds, owner, activation and z-order remain stable.
/// </summary>
public static class TaskbarIconVisibility
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExAppWindow = 0x00040000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private static readonly int TaskbarButtonCreated = RegisterWindowMessage("TaskbarButtonCreated");
    private static readonly ConditionalWeakTable<Window, NativeState> States = new();
    [ThreadStatic]
    private static ITaskbarList? _taskbarList;

    public static void SetVisible(Window window, bool visible)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Dispatcher.VerifyAccess();
        States.GetValue(window, static value => new NativeState(value)).SetVisible(visible);
    }

    public static bool IsVisible(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return States.TryGetValue(window, out NativeState? state)
            ? state.IsVisible
            : window.ShowInTaskbar;
    }

    internal static bool TryGetWindowHandle(Window window, out nint handle)
    {
        handle = new WindowInteropHelper(window).Handle;
        return handle != nint.Zero;
    }

    private static void ApplyNativeVisibility(nint hwnd, bool visible)
    {
        try
        {
            // APPWINDOW keeps each window eligible for Alt+Tab, including WPF
            // windows with a hidden owner. Taskbar buttons are controlled separately.
            long style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            long updated = (style | WsExAppWindow) & ~WsExToolWindow;
            if (updated != style)
            {
                SetWindowLongPtrChecked(hwnd, GwlExStyle, new nint(updated));
                if (!SetWindowPos(hwnd, nint.Zero, 0, 0, 0, 0,
                    SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged | SwpNoOwnerZOrder))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            TryUpdateTaskbarList(hwnd, visible && IsWindowVisible(hwnd));
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"[Taskbar] native visibility failed: {exception}");
        }
    }

    private static void TryUpdateTaskbarList(nint hwnd, bool visible)
    {
        try
        {
            int result = visible
                ? GetTaskbarList().AddTab(hwnd)
                : GetTaskbarList().DeleteTab(hwnd);
            Marshal.ThrowExceptionForHR(result);
        }
        catch (Exception exception)
        {
            // Explorer can restart independently of the application. Reconnect on
            // the next lifecycle notification instead of retaining a broken proxy.
            _taskbarList = null;
            System.Diagnostics.Debug.WriteLine($"[Taskbar] shell list unavailable: {exception}");
        }
    }

    private static ITaskbarList GetTaskbarList()
    {
        if (_taskbarList is not null) return _taskbarList;
        ITaskbarList taskbarList = (ITaskbarList)(object)new TaskbarList();
        Marshal.ThrowExceptionForHR(taskbarList.HrInit());
        return _taskbarList = taskbarList;
    }

    private static nint GetWindowLongPtr(nint hwnd, int index) => IntPtr.Size == 8
        ? GetWindowLongPtr64(hwnd, index)
        : new nint(GetWindowLong32(hwnd, index));

    private static void SetWindowLongPtrChecked(nint hwnd, int index, nint value)
    {
        Marshal.SetLastPInvokeError(0);
        _ = IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : new nint(SetWindowLong32(hwnd, index, value.ToInt32()));
        int error = Marshal.GetLastPInvokeError();
        if (error != 0) throw new Win32Exception(error);
    }

    private sealed class NativeState
    {
        private readonly Window _window;
        private HwndSource? _source;
        private DispatcherOperation? _pending;
        private bool _applying;
        private bool _closed;

        internal NativeState(Window window)
        {
            _window = window;
            window.SourceInitialized += OnSourceInitialized;
            window.IsVisibleChanged += OnVisibilityChanged;
            window.StateChanged += OnStateChanged;
            window.Activated += OnStateChanged;
            window.Closed += OnClosed;
        }

        internal bool IsVisible { get; private set; }

        internal void SetVisible(bool visible)
        {
            IsVisible = visible;
            Apply();
            QueueApply();
        }

        private void Apply()
        {
            if (_closed || _applying || !TryGetWindowHandle(_window, out nint hwnd)) return;
            HwndSource? source = HwndSource.FromHwnd(hwnd);
            if (_source != source)
            {
                if (_source is { IsDisposed: false }) _source.RemoveHook(WndProc);
                _source = source;
                _source?.AddHook(WndProc);
            }
            _applying = true;
            try { ApplyNativeVisibility(hwnd, IsVisible); }
            finally { _applying = false; }
        }

        private void QueueApply()
        {
            if (_closed || _pending is not null) return;
            // Coalesce native/WPF notifications, then run after their own show,
            // style and taskbar registration work has finished. Read the latest
            // preference here so rapid toggles cannot replay a stale value.
            _pending = _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _pending = null;
                Apply();
            }));
        }

        private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            if (message == TaskbarCreated)
            {
                _taskbarList = null;
                QueueApply();
            }
            else if (message == TaskbarButtonCreated)
            {
                // AddTab may itself cause this notification. Only a hidden
                // button needs correction, avoiding a registration loop.
                if (!IsVisible) QueueApply();
            }
            else if (!_applying && (message == 0x0018 || // WM_SHOWWINDOW
                message == 0x007D && wParam.ToInt64() == GwlExStyle)) // WM_STYLECHANGED
                QueueApply();
            return nint.Zero;
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            Apply();
            QueueApply();
        }
        private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => QueueApply();
        private void OnStateChanged(object? sender, EventArgs e) => QueueApply();
        private void OnClosed(object? sender, EventArgs e)
        {
            _closed = true;
            _pending?.Abort();
            _pending = null;
            if (_source is { IsDisposed: false }) _source.RemoveHook(WndProc);
            _source = null;
            _window.SourceInitialized -= OnSourceInitialized;
            _window.IsVisibleChanged -= OnVisibilityChanged;
            _window.StateChanged -= OnStateChanged;
            _window.Activated -= OnStateChanged;
            _window.Closed -= OnClosed;
        }
    }

    [ComImport, Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
    private sealed class TaskbarList { }

    [ComImport, Guid("56FDF342-FD6D-11D0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(nint hwnd);
        [PreserveSig] int DeleteTab(nint hwnd);
        [PreserveSig] int ActivateTab(nint hwnd);
        [PreserveSig] int SetActiveAlt(nint hwnd);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string message);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] private static extern int GetWindowLong32(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern nint GetWindowLongPtr64(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong32(nint hwnd, int index, int value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr64(nint hwnd, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
}
