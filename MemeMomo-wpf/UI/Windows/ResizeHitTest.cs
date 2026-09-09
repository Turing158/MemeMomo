using System.Windows;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace MemeMomo.UI.Windows;

internal enum ResizeEdge
{
    Client,
    Left,
    Top,
    Right,
    Bottom,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

internal static class ResizeHitTest
{
    // Keep the native resize hit-test contract at the original 8 DIP on every
    // side, including the four corners.
    internal const double DefaultEdgeThickness = 8;
    internal const double DefaultCornerThickness = 8;

    internal static ResizeEdge Resolve(WpfPoint point, WpfSize size, double borderThickness)
        => Resolve(point, size, borderThickness, borderThickness);

    internal static ResizeEdge Resolve(
        WpfPoint point,
        WpfSize size,
        double edgeThickness,
        double cornerThickness)
    {
        double safeEdgeThickness = Math.Max(0, edgeThickness);
        double safeCornerThickness = Math.Max(safeEdgeThickness, cornerThickness);
        bool left = point.X >= 0 && point.X < safeEdgeThickness;
        bool right = point.X <= size.Width && point.X > size.Width - safeEdgeThickness;
        bool top = point.Y >= 0 && point.Y < safeEdgeThickness;
        bool bottom = point.Y <= size.Height && point.Y > size.Height - safeEdgeThickness;

        bool cornerLeft = point.X >= 0 && point.X < safeCornerThickness;
        bool cornerRight = point.X <= size.Width && point.X > size.Width - safeCornerThickness;
        bool cornerTop = point.Y >= 0 && point.Y < safeCornerThickness;
        bool cornerBottom = point.Y <= size.Height && point.Y > size.Height - safeCornerThickness;

        if (cornerTop && cornerLeft) return ResizeEdge.TopLeft;
        if (cornerTop && cornerRight) return ResizeEdge.TopRight;
        if (cornerBottom && cornerLeft) return ResizeEdge.BottomLeft;
        if (cornerBottom && cornerRight) return ResizeEdge.BottomRight;
        if (left) return ResizeEdge.Left;
        if (right) return ResizeEdge.Right;
        if (top) return ResizeEdge.Top;
        if (bottom) return ResizeEdge.Bottom;
        return ResizeEdge.Client;
    }
}
