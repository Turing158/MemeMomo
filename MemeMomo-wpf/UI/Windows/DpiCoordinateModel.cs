using System.Windows;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace MemeMomo.UI.Windows;

public readonly record struct DpiScale2(double DpiX, double DpiY)
{
    internal static DpiScale2 Default => new(96, 96);

    internal double ScaleX => DpiX / 96d;

    internal double ScaleY => DpiY / 96d;

    internal WpfPoint DipToPixels(WpfPoint point) =>
        new(point.X * ScaleX, point.Y * ScaleY);

    internal WpfPoint PixelsToDip(WpfPoint point) =>
        new(point.X / ScaleX, point.Y / ScaleY);

    internal WpfRect DipToPixels(WpfRect rect)
    {
        WpfPoint topLeft = DipToPixels(rect.TopLeft);
        WpfPoint bottomRight = DipToPixels(rect.BottomRight);
        return new WpfRect(topLeft, bottomRight);
    }

    internal WpfRect PixelsToDip(WpfRect rect)
    {
        WpfPoint topLeft = PixelsToDip(rect.TopLeft);
        WpfPoint bottomRight = PixelsToDip(rect.BottomRight);
        return new WpfRect(topLeft, bottomRight);
    }
}

public static class DpiCoordinateModel
{
    internal static WpfRect ClampToWorkingArea(WpfRect windowPixels, WpfRect workingAreaPixels)
    {
        double width = Math.Min(windowPixels.Width, workingAreaPixels.Width);
        double height = Math.Min(windowPixels.Height, workingAreaPixels.Height);
        double left = Math.Clamp(
            windowPixels.Left,
            workingAreaPixels.Left,
            workingAreaPixels.Right - width);
        double top = Math.Clamp(
            windowPixels.Top,
            workingAreaPixels.Top,
            workingAreaPixels.Bottom - height);
        return new WpfRect(left, top, width, height);
    }

    internal static DpiScale2 FromVisual(System.Windows.Media.Visual visual)
    {
        DpiScale dpi = System.Windows.Media.VisualTreeHelper.GetDpi(visual);
        return new DpiScale2(dpi.PixelsPerInchX, dpi.PixelsPerInchY);
    }
}
