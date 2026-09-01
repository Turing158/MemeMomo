using System.Windows;
using Memo.Models;
using Memo.UI.Windows;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;
using WpfSize = System.Windows.Size;

namespace Memo.Services;

/// <summary>
/// 便签窗口的屏幕定位适配器。输入和工作区均为物理像素，输出供 WPF Window.Left/Top 使用的 DIP。
/// </summary>
public static class MemoWindowPlacement
{
    public const double DefaultWidthDip = 360;
    public const double DefaultHeightDip = 280;
    public const double DefaultTitleBarHeightDip = 38;
    public const double DefaultMarginDip = 12;
    public const double DockedTabHeightDip = 28;

    public static WpfRect FromPointerPixels(
        WpfPoint pointerPixels,
        WpfSize windowDip,
        PixelMonitorInfo monitor,
        double titleBarHeightDip = DefaultTitleBarHeightDip,
        double marginDip = DefaultMarginDip)
    {
        double scaleX = Math.Max(0.01, monitor.Dpi.DpiX / 96d);
        double scaleY = Math.Max(0.01, monitor.Dpi.DpiY / 96d);
        double width = Math.Max(1, windowDip.Width * scaleX);
        double height = Math.Max(1, windowDip.Height * scaleY);
        double marginX = Math.Max(0, marginDip * scaleX);
        double marginY = Math.Max(0, marginDip * scaleY);
        WpfRect requested = new(
            pointerPixels.X - (width / 2),
            pointerPixels.Y - (titleBarHeightDip * scaleY),
            width,
            height);
        WpfRect area = monitor.WorkingArea;
        double minX = area.Left + marginX;
        double minY = area.Top + marginY;
        double maxX = area.Right - width - marginX;
        double maxY = area.Bottom - height - marginY;
        if (maxX < minX)
        {
            maxX = minX;
        }
        if (maxY < minY)
        {
            maxY = minY;
        }

        WpfRect clampedPixels = new(
            Math.Clamp(requested.Left, minX, maxX),
            Math.Clamp(requested.Top, minY, maxY),
            width,
            height);
        return new WpfRect(
            clampedPixels.Left / scaleX,
            clampedPixels.Top / scaleY,
            windowDip.Width,
            windowDip.Height);
    }

    public static WpfRect CenterPixels(WpfSize windowDip, PixelMonitorInfo monitor, double marginDip = DefaultMarginDip)
    {
        double scaleX = Math.Max(0.01, monitor.Dpi.DpiX / 96d);
        double scaleY = Math.Max(0.01, monitor.Dpi.DpiY / 96d);
        double width = Math.Max(1, windowDip.Width * scaleX);
        double height = Math.Max(1, windowDip.Height * scaleY);
        WpfRect area = monitor.WorkingArea;
        WpfPoint center = new(area.Left + ((area.Width - width) / 2), area.Top + ((area.Height - height) / 2));
        return FromPointerPixels(
            new WpfPoint(center.X + (width / 2), center.Y + DefaultTitleBarHeightDip * scaleY),
            windowDip,
            monitor,
            DefaultTitleBarHeightDip,
            marginDip);
    }

    /// <summary>
    /// 计算便签贴边（D 形标签）目标 rect（DIP）。贴边侧对齐工作区左/右边缘，
    /// Top clamp 在工作区内。仅支持 Left / Right，其余按 Left 处理。
    /// </summary>
    public static WpfRect DockedTabBoundsDip(
        MainWindowDockEdge edge,
        PixelMonitorInfo monitor,
        double topDip,
        double widthDip,
        double tabHeightDip = DockedTabHeightDip)
    {
        double scaleX = Math.Max(0.01, monitor.Dpi.DpiX / 96d);
        double scaleY = Math.Max(0.01, monitor.Dpi.DpiY / 96d);
        double widthPixels = Math.Max(1, Math.Round(widthDip * scaleX));
        double heightPixels = Math.Max(1, Math.Round(tabHeightDip * scaleY));
        WpfRect area = monitor.WorkingArea;
        double maxTop = Math.Max(area.Top, area.Bottom - heightPixels);
        double topPixels = Math.Clamp(topDip * scaleY, area.Top, maxTop);
        double leftPixels = edge == MainWindowDockEdge.Right
            ? area.Right - widthPixels
            : area.Left;
        return new WpfRect(leftPixels / scaleX, topPixels / scaleY, widthPixels / scaleX, heightPixels / scaleY);
    }

    /// <summary>沿边滑动的 Top clamp（输入输出均为物理像素）。</summary>
    public static double ClampWindowTopPixels(PixelMonitorInfo monitor, double topPixels, double heightPixels)
    {
        WpfRect area = monitor.WorkingArea;
        double maxTop = Math.Max(area.Top, area.Bottom - Math.Max(1, heightPixels));
        return Math.Clamp(topPixels, area.Top, maxTop);
    }

    /// <summary>解除贴边时把浮动快照 rect（DIP）按当前显示器工作区重 clamp。</summary>
    public static WpfRect ClampFloatingBoundsDip(WpfRect requestedDip, PixelMonitorInfo monitor)
    {
        double scaleX = Math.Max(0.01, monitor.Dpi.DpiX / 96d);
        double scaleY = Math.Max(0.01, monitor.Dpi.DpiY / 96d);
        WpfRect area = monitor.WorkingArea;
        double width = Math.Clamp(requestedDip.Width, 1, area.Width / scaleX);
        double height = Math.Clamp(requestedDip.Height, 1, area.Height / scaleY);
        double widthPixels = width * scaleX;
        double heightPixels = height * scaleY;
        double maxLeft = Math.Max(area.Left, area.Right - widthPixels);
        double maxTop = Math.Max(area.Top, area.Bottom - heightPixels);
        double leftPixels = Math.Clamp(requestedDip.Left * scaleX, area.Left, maxLeft);
        double topPixels = Math.Clamp(requestedDip.Top * scaleY, area.Top, maxTop);
        return new WpfRect(leftPixels / scaleX, topPixels / scaleY, width, height);
    }
}
