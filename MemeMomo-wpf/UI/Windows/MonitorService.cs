using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WpfPoint = System.Windows.Point;

namespace MemeMomo.UI.Windows;

public readonly record struct PixelMonitorInfo(Rect WorkingArea, Rect Bounds, DpiScale2 Dpi)
{
    public Rect Clamp(Rect windowPixels)
    {
        return DpiCoordinateModel.ClampToWorkingArea(windowPixels, WorkingArea);
    }
}

public interface IMonitorService
{
    PixelMonitorInfo FromWindow(Window window);
    PixelMonitorInfo FromPoint(WpfPoint screenPointPixels);
    IReadOnlyList<MonitorSnapshot> GetAll();
    MonitorSnapshot Primary { get; }
    WpfPoint ToScreenPixels(Window window, WpfPoint clientDip);
}

public interface ICursorStateService
{
    bool TryGetCursorState(out WpfPoint screenPointPixels, out bool leftButtonPressed);
}

public sealed class MonitorService : IMonitorService, ICursorStateService
{
    public MonitorSnapshot Primary
    {
        get
        {
            nint monitor = NativeMethods.MonitorFromPoint(
                new NativeMethods.NativePoint(0, 0),
                NativeMethods.MonitorDefaultToPrimary);
            NativeMethods.MonitorInfo info = NativeMethods.GetMonitorInfo(monitor);
            return new MonitorSnapshot(CreateInfo(info, NativeMethods.GetDpi(monitor)), true);
        }
    }

    public PixelMonitorInfo FromWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        WindowInteropHelper helper = new(window);
        if (helper.Handle == nint.Zero)
        {
            return FromPoint(new WpfPoint(0, 0));
        }

        NativeMethods.MonitorInfo monitor = NativeMethods.GetMonitorInfoForWindow(helper.Handle);
        DpiScale2 dpi = DpiCoordinateModel.FromVisual(window);
        return CreateInfo(monitor, dpi);
    }

    public PixelMonitorInfo FromPoint(WpfPoint screenPointPixels)
    {
        nint monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.NativePoint((int)Math.Round(screenPointPixels.X), (int)Math.Round(screenPointPixels.Y)),
            NativeMethods.MonitorDefaultToNearest);
        NativeMethods.MonitorInfo info = NativeMethods.GetMonitorInfo(monitor);
        return CreateInfo(info, NativeMethods.GetDpi(monitor));
    }

    public WpfPoint ToScreenPixels(Window window, WpfPoint clientDip)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.PointToScreen(clientDip);
    }

    public IReadOnlyList<MonitorSnapshot> GetAll()
    {
        List<MonitorSnapshot> monitors = [];
        NativeMethods.MonitorEnumProc callback = (monitor, _, _, _) =>
        {
            NativeMethods.MonitorInfo info = NativeMethods.GetMonitorInfo(monitor);
            monitors.Add(new MonitorSnapshot(
                CreateInfo(info, NativeMethods.GetDpi(monitor)),
                (info.Flags & NativeMethods.MonitorInfoPrimary) != 0));
            return true;
        };
        if (!NativeMethods.EnumDisplayMonitors(nint.Zero, nint.Zero, callback, nint.Zero) || monitors.Count == 0)
        {
            monitors.Add(Primary);
        }

        return monitors;
    }

    public bool TryGetCursorState(out WpfPoint screenPointPixels, out bool leftButtonPressed)
    {
        screenPointPixels = default;
        leftButtonPressed = false;
        try
        {
            if (!NativeMethods.GetCursorPos(out NativeMethods.NativePoint point))
            {
                return false;
            }

            screenPointPixels = new WpfPoint(point.X, point.Y);
            leftButtonPressed = (NativeMethods.GetAsyncKeyState(NativeMethods.LeftButtonVirtualKey) & 0x8000) != 0;
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static PixelMonitorInfo CreateInfo(NativeMethods.MonitorInfo info, DpiScale2 dpi) =>
        new(
            new Rect(info.Work.Left, info.Work.Top, info.Work.Width, info.Work.Height),
            new Rect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Width, info.Monitor.Height),
            dpi);

    private static class NativeMethods
    {
        internal const uint MonitorDefaultToPrimary = 1;
        internal const uint MonitorDefaultToNearest = 2;
        internal const uint MonitorInfoPrimary = 1;
        internal const int LeftButtonVirtualKey = 0x01;

        internal delegate bool MonitorEnumProc(nint monitor, nint hdc, nint monitorRect, nint data);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool EnumDisplayMonitors(
            nint hdc,
            nint clipRect,
            MonitorEnumProc callback,
            nint data);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out NativePoint point);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int virtualKey);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern nint MonitorFromPoint(NativePoint point, uint flags);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(nint monitor, ref NativeMonitorInfo info);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

        internal static MonitorInfo GetMonitorInfoForWindow(nint hwnd)
        {
            nint monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            return GetMonitorInfo(monitor);
        }

        internal static MonitorInfo GetMonitorInfo(nint monitor)
        {
            NativeMonitorInfo info = new() { CbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMonitorInfo>() };
            if (monitor != nint.Zero && GetMonitorInfo(monitor, ref info))
            {
                return new MonitorInfo(info.Monitor, info.Work, info.Flags);
            }

            return new MonitorInfo(
                new NativeRect(0, 0, 1280, 720),
                new NativeRect(0, 0, 1280, 680),
                MonitorInfoPrimary);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern nint MonitorFromWindow(nint hwnd, uint flags);

        [System.Runtime.InteropServices.DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(
            nint monitor,
            MonitorDpiType dpiType,
            out uint dpiX,
            out uint dpiY);

        internal static DpiScale2 GetDpi(nint monitor)
        {
            try
            {
                if (monitor != nint.Zero
                    && GetDpiForMonitor(monitor, MonitorDpiType.Effective, out uint dpiX, out uint dpiY) == 0
                    && dpiX > 0
                    && dpiY > 0)
                {
                    return new DpiScale2(dpiX, dpiY);
                }
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }

            return DpiScale2.Default;
        }

        private enum MonitorDpiType
        {
            Effective = 0,
            Angular = 1,
            Raw = 2,
            Default = 0
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal readonly struct NativePoint(int x, int y)
        {
            internal readonly int X = x;
            internal readonly int Y = y;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal readonly struct NativeRect(int left, int top, int right, int bottom)
        {
            internal readonly int Left = left;
            internal readonly int Top = top;
            internal readonly int Right = right;
            internal readonly int Bottom = bottom;
            internal int Width => Right - Left;
            internal int Height => Bottom - Top;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private struct NativeMonitorInfo
        {
            internal int CbSize;
            internal NativeRect Monitor;
            internal NativeRect Work;
            internal uint Flags;
        }

        internal readonly record struct MonitorInfo(NativeRect Monitor, NativeRect Work, uint Flags);
    }
}
