using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;

namespace Memo.Platform.Windows;

/// <summary>
/// Changes taskbar presence through the existing HWND. No WPF window is
/// recreated, so bounds, owner, activation and z-order remain stable.
/// </summary>
public static class TaskbarIconVisibility
{
    private const int GwOwner = 4;
    private const int GwlExStyle = -20;
    private const int GwlpHwndParent = -8;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExAppWindow = 0x00040000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly ConditionalWeakTable<Window, NativeState> States = new();
    [ThreadStatic]
    private static ITaskbarList? _taskbarList;

    public static void SetVisible(Window window, bool visible)
    {
        ArgumentNullException.ThrowIfNull(window);
        NativeState state = States.GetValue(window, static _ => new NativeState());
        if (OperatingSystem.IsWindows() && TryGetWindowHandle(window, out nint hwnd))
        {
            _ = TrySetNativeVisibility(hwnd, state, visible);
            state.IsVisible = visible;
            state.HasVisibility = true;
            return;
        }

        window.ShowInTaskbar = visible;
        state.IsVisible = visible;
        state.HasVisibility = true;
    }

    public static bool IsVisible(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return States.TryGetValue(window, out NativeState? state) && state.HasVisibility
            ? state.IsVisible
            : window.ShowInTaskbar;
    }

    internal static bool TryGetWindowHandle(Window window, out nint handle)
    {
        handle = new WindowInteropHelper(window).Handle;
        return handle != nint.Zero;
    }

    private static bool TrySetNativeVisibility(nint hwnd, NativeState state, bool visible)
    {
        try
        {
            nint owner = GetWindow(hwnd, GwOwner);
            if (owner != nint.Zero && !state.HasOwner)
            {
                state.Owner = owner;
                state.HasOwner = true;
            }

            if (!visible)
            {
                TryUpdateTaskbarList(hwnd, visible: false);
            }
            else if (owner != nint.Zero)
            {
                SetWindowLongPtrChecked(hwnd, GwlpHwndParent, nint.Zero);
            }

            long style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            long updated = visible
                ? (style | WsExAppWindow) & ~WsExToolWindow
                : (style & ~WsExAppWindow) | WsExToolWindow;
            if (updated != style)
            {
                SetWindowLongPtrChecked(hwnd, GwlExStyle, new nint(updated));
            }

            if (!visible && state.HasOwner && owner != state.Owner)
            {
                SetWindowLongPtrChecked(hwnd, GwlpHwndParent, state.Owner);
            }

            if (!SetWindowPos(hwnd, nint.Zero, 0, 0, 0, 0,
                    SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged | SwpNoOwnerZOrder))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (visible)
            {
                TryUpdateTaskbarList(hwnd, visible: true);
            }

            return true;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"[Taskbar] native visibility failed: {exception}");
            return false;
        }
    }

    private static void TryUpdateTaskbarList(nint hwnd, bool visible)
    {
        try
        {
            int result = visible
                ? GetTaskbarList().AddTab(hwnd)
                : GetTaskbarList().DeleteTab(hwnd);
            if (result < 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Taskbar] shell list update failed: 0x{result:X8}");
            }
        }
        catch (Exception exception)
        {
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
        internal bool HasOwner { get; set; }
        internal nint Owner { get; set; }
        internal bool HasVisibility { get; set; }
        internal bool IsVisible { get; set; }
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

    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetWindow(nint hwnd, int command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] private static extern int GetWindowLong32(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern nint GetWindowLongPtr64(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong32(nint hwnd, int index, int value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr64(nint hwnd, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
}
