using Avalonia;
using Avalonia.Controls;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Memo.Platform.Windows;

internal static class TaskbarIconVisibility {
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

    private static readonly ConditionalWeakTable<Window, NativeWindowState> NativeStates = new();
    private static ITaskbarList? _taskbarList;

    public static void SetVisible(Window window, bool visible) {
        if (window.IsVisible && TrySetNativeVisibility(window, visible)) return;

        // Updating Avalonia's property is safe before Show/after Hide and remains
        // the portable fallback used by the headless test platform.
        if (window.ShowInTaskbar != visible) window.ShowInTaskbar = visible;
    }

    private static bool TrySetNativeVisibility(Window window, bool visible) {
        if (!OperatingSystem.IsWindows()) return false;

        var platformHandle = window.TryGetPlatformHandle();
        if (platformHandle?.HandleDescriptor != "HWND" || platformHandle.Handle == IntPtr.Zero)
            return false;

        try {
            var taskbarList = GetTaskbarList();
            var hwnd = platformHandle.Handle;
            var state = NativeStates.GetValue(window, _ => new NativeWindowState());
            var owner = GetWindow(hwnd, GwOwner);

            if (owner != IntPtr.Zero && !state.HasHiddenOwner) {
                state.HiddenOwner = owner;
                state.HasHiddenOwner = true;
            }

            if (!visible) Marshal.ThrowExceptionForHR(taskbarList.DeleteTab(hwnd));

            if (visible && owner != IntPtr.Zero)
                SetWindowLongPtrChecked(hwnd, GwlpHwndParent, IntPtr.Zero);

            var extendedStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            var updatedStyle = visible
                ? (extendedStyle | WsExAppWindow) & ~WsExToolWindow
                : (extendedStyle & ~WsExAppWindow) | WsExToolWindow;
            if (updatedStyle != extendedStyle)
                SetWindowLongPtrChecked(hwnd, GwlExStyle, new IntPtr(updatedStyle));

            if (!visible && state.HasHiddenOwner && owner != state.HiddenOwner)
                SetWindowLongPtrChecked(hwnd, GwlpHwndParent, state.HiddenOwner);

            if (!SetWindowPos(
                    hwnd,
                    IntPtr.Zero,
                    0,
                    0,
                    0,
                    0,
                    SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate |
                    SwpFrameChanged | SwpNoOwnerZOrder)) {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (visible) Marshal.ThrowExceptionForHR(taskbarList.AddTab(hwnd));
            return true;
        }
        catch (Exception ex) {
            Debug.WriteLine($"Unable to update the taskbar icon without recreating the window: {ex}");
            return false;
        }
    }

    private static ITaskbarList GetTaskbarList() {
        if (_taskbarList != null) return _taskbarList;

        var taskbarList = (ITaskbarList)(object)new TaskbarList();
        Marshal.ThrowExceptionForHR(taskbarList.HrInit());
        _taskbarList = taskbarList;
        return taskbarList;
    }

    private static void SetWindowLongPtrChecked(IntPtr hwnd, int index, IntPtr value) {
        Marshal.SetLastPInvokeError(0);
        var previousValue = SetWindowLongPtr(hwnd, index, value);
        var error = Marshal.GetLastPInvokeError();
        if (previousValue == IntPtr.Zero && error != 0) throw new Win32Exception(error);
    }

    private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) => IntPtr.Size == 8
        ? GetWindowLongPtr64(hwnd, index)
        : new IntPtr(GetWindowLong32(hwnd, index));

    private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value) => IntPtr.Size == 8
        ? SetWindowLongPtr64(hwnd, index, value)
        : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));

    private sealed class NativeWindowState {
        public bool HasHiddenOwner { get; set; }
        public IntPtr HiddenOwner { get; set; }
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
    private sealed class TaskbarList { }

    [ComImport]
    [Guid("56FDF342-FD6D-11D0-958A-006097C9A090")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList {
        [PreserveSig]
        int HrInit();

        [PreserveSig]
        int AddTab(IntPtr hwnd);

        [PreserveSig]
        int DeleteTab(IntPtr hwnd);

        [PreserveSig]
        int ActivateTab(IntPtr hwnd);

        [PreserveSig]
        int SetActiveAlt(IntPtr hwnd);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr hwndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
