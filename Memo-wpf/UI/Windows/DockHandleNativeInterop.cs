using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WpfRect = System.Windows.Rect;

namespace Memo.UI.Windows;

internal static class DockHandleLayeredSurface
{
    private const byte AcSrcAlpha = 1;
    private const uint BiRgb = 0;
    private const uint UlwAlpha = 2;

    internal static bool TryUpdate(
        nint hwnd,
        DockHandleBitmap bitmap,
        WpfRect bounds,
        out int error)
    {
        error = 0;
        using SafeScreenDcHandle screenDc = SafeScreenDcHandle.Acquire();
        if (screenDc.IsInvalid)
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        using SafeCompatibleDcHandle memoryDc = SafeCompatibleDcHandle.Create(screenDc);
        if (memoryDc.IsInvalid)
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        BitmapInfo info = new()
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = bitmap.PixelWidth,
                Height = -bitmap.PixelHeight,
                Planes = 1,
                BitCount = 32,
                Compression = BiRgb
            }
        };
        using SafeGdiObjectHandle dib = CreateDIBSection(
            screenDc.DangerousGetHandle(),
            ref info,
            0,
            out nint bits,
            nint.Zero,
            0);
        if (dib.IsInvalid || bits == nint.Zero)
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        Marshal.Copy(bitmap.Pixels, 0, bits, bitmap.Pixels.Length);
        nint oldBitmap = SelectObject(memoryDc.DangerousGetHandle(), dib.DangerousGetHandle());
        if (oldBitmap == nint.Zero || oldBitmap == new nint(-1))
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        try
        {
            NativePoint destination = new((int)Math.Round(bounds.X), (int)Math.Round(bounds.Y));
            NativeSize size = new(bitmap.PixelWidth, bitmap.PixelHeight);
            NativePoint source = new(0, 0);
            BlendFunction blend = new()
            {
                BlendOp = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AcSrcAlpha
            };
            bool updated = UpdateLayeredWindow(
                hwnd,
                screenDc.DangerousGetHandle(),
                ref destination,
                ref size,
                memoryDc.DangerousGetHandle(),
                ref source,
                0,
                ref blend,
                UlwAlpha);
            if (!updated)
            {
                error = Marshal.GetLastWin32Error();
            }

            return updated;
        }
        finally
        {
            _ = SelectObject(memoryDc.DangerousGetHandle(), oldBitmap);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(
        nint hwnd,
        nint destinationDc,
        ref NativePoint destination,
        ref NativeSize size,
        nint sourceDc,
        ref NativePoint source,
        uint colorKey,
        ref BlendFunction blend,
        uint flags);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern SafeGdiObjectHandle CreateDIBSection(
        nint dc,
        ref BitmapInfo bitmapInfo,
        uint usage,
        out nint bits,
        nint section,
        uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint SelectObject(nint dc, nint value);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeSize(int Width, int Height);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        internal byte BlendOp;
        internal byte BlendFlags;
        internal byte SourceConstantAlpha;
        internal byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        internal BitmapInfoHeader Header;
        internal uint Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal uint Compression;
        internal uint SizeImage;
        internal int XPixelsPerMeter;
        internal int YPixelsPerMeter;
        internal uint ColorsUsed;
        internal uint ColorsImportant;
    }
}

internal sealed class SafeGdiObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeGdiObjectHandle() : base(true) { }

    protected override bool ReleaseHandle() => DeleteObject(handle);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);
}

internal sealed class SafeCompatibleDcHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeCompatibleDcHandle() : base(true) { }

    internal static SafeCompatibleDcHandle Create(SafeScreenDcHandle source) =>
        CreateCompatibleDC(source.DangerousGetHandle());

    protected override bool ReleaseHandle() => DeleteDC(handle);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern SafeCompatibleDcHandle CreateCompatibleDC(nint source);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint dc);
}

internal sealed class SafeScreenDcHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeScreenDcHandle() : base(true) { }

    internal static SafeScreenDcHandle Acquire() => GetDC(nint.Zero);

    protected override bool ReleaseHandle() => ReleaseDC(nint.Zero, handle) == 1;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern SafeScreenDcHandle GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint dc);
}
