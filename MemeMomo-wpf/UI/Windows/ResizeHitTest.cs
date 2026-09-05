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
    internal static ResizeEdge Resolve(WpfPoint point, WpfSize size, double borderThickness)
    {
        bool left = point.X >= 0 && point.X < borderThickness;
        bool right = point.X <= size.Width && point.X > size.Width - borderThickness;
        bool top = point.Y >= 0 && point.Y < borderThickness;
        bool bottom = point.Y <= size.Height && point.Y > size.Height - borderThickness;

        if (top && left) return ResizeEdge.TopLeft;
        if (top && right) return ResizeEdge.TopRight;
        if (bottom && left) return ResizeEdge.BottomLeft;
        if (bottom && right) return ResizeEdge.BottomRight;
        if (left) return ResizeEdge.Left;
        if (right) return ResizeEdge.Right;
        if (top) return ResizeEdge.Top;
        if (bottom) return ResizeEdge.Bottom;
        return ResizeEdge.Client;
    }
}
