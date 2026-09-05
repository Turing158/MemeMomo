using System.Windows;
using System.Windows.Media;
using MemeMomo.Models;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;
using WpfSize = System.Windows.Size;

namespace MemeMomo.UI;

internal enum DockCorner
{
    None,
    TopLeft,
    TopRight,
    BottomRight,
    BottomLeft
}

internal readonly record struct DockCubicCorner(
    WpfPoint Start,
    WpfPoint Control1,
    WpfPoint Control2,
    WpfPoint End)
{
    internal static DockCubicCorner Lerp(DockCubicCorner from, DockCubicCorner to, double amount) =>
        new(
            DockVisualLayoutCalculator.Lerp(from.Start, to.Start, amount),
            DockVisualLayoutCalculator.Lerp(from.Control1, to.Control1, amount),
            DockVisualLayoutCalculator.Lerp(from.Control2, to.Control2, amount),
            DockVisualLayoutCalculator.Lerp(from.End, to.End, amount));
}

internal readonly record struct DockOutline(
    DockCubicCorner TopLeft,
    DockCubicCorner TopRight,
    DockCubicCorner BottomRight,
    DockCubicCorner BottomLeft)
{
    internal static DockOutline Lerp(DockOutline from, DockOutline to, double amount) =>
        new(
            DockCubicCorner.Lerp(from.TopLeft, to.TopLeft, amount),
            DockCubicCorner.Lerp(from.TopRight, to.TopRight, amount),
            DockCubicCorner.Lerp(from.BottomRight, to.BottomRight, amount),
            DockCubicCorner.Lerp(from.BottomLeft, to.BottomLeft, amount));

    internal StreamGeometry CreateGeometry()
    {
        StreamGeometry geometry = new();
        using StreamGeometryContext context = geometry.Open();
        context.BeginFigure(TopLeft.End, isFilled: true, isClosed: true);
        context.LineTo(TopRight.Start, isStroked: true, isSmoothJoin: false);
        context.BezierTo(TopRight.Control1, TopRight.Control2, TopRight.End, isStroked: true, isSmoothJoin: false);
        context.LineTo(BottomRight.Start, isStroked: true, isSmoothJoin: false);
        context.BezierTo(BottomRight.Control1, BottomRight.Control2, BottomRight.End, isStroked: true, isSmoothJoin: false);
        context.LineTo(BottomLeft.Start, isStroked: true, isSmoothJoin: false);
        context.BezierTo(BottomLeft.Control1, BottomLeft.Control2, BottomLeft.End, isStroked: true, isSmoothJoin: false);
        context.LineTo(TopLeft.Start, isStroked: true, isSmoothJoin: false);
        context.BezierTo(TopLeft.Control1, TopLeft.Control2, TopLeft.End, isStroked: true, isSmoothJoin: false);
        geometry.Freeze();
        return geometry;
    }
}

internal readonly record struct DockVisualFrame(
    double Width,
    double Height,
    WpfPoint ContentOffset,
    WpfPoint ScaleOrigin,
    DockOutline Outline)
{
    internal static DockVisualFrame Lerp(DockVisualFrame from, DockVisualFrame to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return new DockVisualFrame(
            DockVisualLayoutCalculator.Lerp(from.Width, to.Width, amount),
            DockVisualLayoutCalculator.Lerp(from.Height, to.Height, amount),
            DockVisualLayoutCalculator.Lerp(from.ContentOffset, to.ContentOffset, amount),
            DockVisualLayoutCalculator.Lerp(from.ScaleOrigin, to.ScaleOrigin, amount),
            DockOutline.Lerp(from.Outline, to.Outline, amount));
    }
}

internal readonly record struct DockVisualTarget(
    DockCorner Corner,
    WpfPoint Position,
    double NormalizedPosition,
    DockVisualFrame Frame);

internal static class DockVisualLayoutCalculator
{
    // Extra geometry beyond the handle makes the edge-to-body transition
    // remain visible while the idle 0.9 scale is applied.
    internal const double TransitionSize = 6;
    // Standard quarter-circle cubic approximation for the edge transition.
    private const double TransitionControlFactor = 0.5523;

    [Flags]
    private enum AttachedEdges
    {
        None = 0,
        Left = 1,
        Top = 2,
        Right = 4,
        Bottom = 8
    }

    internal static DockVisualFrame ForEdge(MainWindowDockEdge edge, double size) => edge switch
    {
        MainWindowDockEdge.Left => BuildFrame(size, size + TransitionSize * 2, new WpfPoint(0, TransitionSize), new WpfPoint(0, 0.5), AttachedEdges.Left, size),
        MainWindowDockEdge.Right => BuildFrame(size, size + TransitionSize * 2, new WpfPoint(0, TransitionSize), new WpfPoint(1, 0.5), AttachedEdges.Right, size),
        MainWindowDockEdge.Top => BuildFrame(size + TransitionSize * 2, size, new WpfPoint(TransitionSize, 0), new WpfPoint(0.5, 0), AttachedEdges.Top, size),
        MainWindowDockEdge.Bottom => BuildFrame(size + TransitionSize * 2, size, new WpfPoint(TransitionSize, 0), new WpfPoint(0.5, 1), AttachedEdges.Bottom, size),
        _ => throw new ArgumentOutOfRangeException(nameof(edge))
    };

    internal static DockVisualFrame ForCorner(DockCorner corner, double size)
    {
        double full = size + TransitionSize;
        return corner switch
        {
            DockCorner.TopLeft => BuildFrame(full, full, new WpfPoint(0, 0), new WpfPoint(0, 0), AttachedEdges.Left | AttachedEdges.Top, size),
            DockCorner.TopRight => BuildFrame(full, full, new WpfPoint(TransitionSize, 0), new WpfPoint(1, 0), AttachedEdges.Top | AttachedEdges.Right, size),
            DockCorner.BottomRight => BuildFrame(full, full, new WpfPoint(TransitionSize, TransitionSize), new WpfPoint(1, 1), AttachedEdges.Right | AttachedEdges.Bottom, size),
            DockCorner.BottomLeft => BuildFrame(full, full, new WpfPoint(0, TransitionSize), new WpfPoint(0, 1), AttachedEdges.Bottom | AttachedEdges.Left, size),
            _ => throw new ArgumentOutOfRangeException(nameof(corner))
        };
    }

    internal static DockVisualFrame Interpolate(MainWindowDockEdge edge, DockCorner corner, double size, double progress)
    {
        DockVisualFrame edgeFrame = ForEdge(edge, size);
        return corner == DockCorner.None ? edgeFrame : DockVisualFrame.Lerp(edgeFrame, ForCorner(corner, size), progress);
    }

    internal static DockVisualTarget TargetFromCursor(
        MainWindowDockEdge edge,
        WpfRect workArea,
        double scaling,
        WpfPoint cursor,
        double size)
    {
        DockVisualFrame edgeFrame = ForEdge(edge, size);
        int edgeWidth = ToPixels(edgeFrame.Width, scaling);
        int edgeHeight = ToPixels(edgeFrame.Height, scaling);
        double maxX = Math.Max(workArea.Left, workArea.Right - edgeWidth);
        double maxY = Math.Max(workArea.Top, workArea.Bottom - edgeHeight);

        DockCorner corner;
        WpfPoint edgePosition;
        double normalized;
        if (edge is MainWindowDockEdge.Left or MainWindowDockEdge.Right)
        {
            double rawY = cursor.Y - edgeHeight / 2d;
            if (rawY <= workArea.Top)
            {
                corner = StartCorner(edge);
                normalized = 0;
                edgePosition = EdgePosition(edge, workArea, edgeWidth, edgeHeight, 0);
            }
            else if (rawY >= maxY)
            {
                corner = EndCorner(edge);
                normalized = 1;
                edgePosition = EdgePosition(edge, workArea, edgeWidth, edgeHeight, 1);
            }
            else
            {
                corner = DockCorner.None;
                edgePosition = new WpfPoint(edge == MainWindowDockEdge.Left ? workArea.Left : maxX, rawY);
                normalized = Normalize(rawY - workArea.Top, maxY - workArea.Top);
            }
        }
        else
        {
            double rawX = cursor.X - edgeWidth / 2d;
            if (rawX <= workArea.Left)
            {
                corner = StartCorner(edge);
                normalized = 0;
                edgePosition = EdgePosition(edge, workArea, edgeWidth, edgeHeight, 0);
            }
            else if (rawX >= maxX)
            {
                corner = EndCorner(edge);
                normalized = 1;
                edgePosition = EdgePosition(edge, workArea, edgeWidth, edgeHeight, 1);
            }
            else
            {
                corner = DockCorner.None;
                edgePosition = new WpfPoint(rawX, edge == MainWindowDockEdge.Top ? workArea.Top : maxY);
                normalized = Normalize(rawX - workArea.Left, maxX - workArea.Left);
            }
        }

        if (corner == DockCorner.None)
        {
            return new DockVisualTarget(corner, edgePosition, normalized, edgeFrame);
        }

        DockVisualFrame cornerFrame = ForCorner(corner, size);
        return new DockVisualTarget(corner, CornerPosition(corner, workArea, cornerFrame, scaling), normalized, cornerFrame);
    }

    internal static DockVisualTarget TargetFromNormalized(MainWindowDockEdge edge, WpfRect workArea, double scaling, double normalized, double size)
    {
        normalized = Math.Clamp(normalized, 0, 1);
        DockCorner corner = normalized <= 0 ? StartCorner(edge) : normalized >= 1 ? EndCorner(edge) : DockCorner.None;
        if (corner != DockCorner.None)
        {
            DockVisualFrame cornerFrame = ForCorner(corner, size);
            return new DockVisualTarget(corner, CornerPosition(corner, workArea, cornerFrame, scaling), normalized, cornerFrame);
        }

        DockVisualFrame edgeFrame = ForEdge(edge, size);
        int edgeWidth = ToPixels(edgeFrame.Width, scaling);
        int edgeHeight = ToPixels(edgeFrame.Height, scaling);
        return new DockVisualTarget(DockCorner.None, EdgePosition(edge, workArea, edgeWidth, edgeHeight, normalized), normalized, edgeFrame);
    }

    internal static DockCorner StartCorner(MainWindowDockEdge edge) => edge switch
    {
        MainWindowDockEdge.Left or MainWindowDockEdge.Top => DockCorner.TopLeft,
        MainWindowDockEdge.Right => DockCorner.TopRight,
        MainWindowDockEdge.Bottom => DockCorner.BottomLeft,
        _ => throw new ArgumentOutOfRangeException(nameof(edge))
    };

    internal static DockCorner EndCorner(MainWindowDockEdge edge) => edge switch
    {
        MainWindowDockEdge.Left => DockCorner.BottomLeft,
        MainWindowDockEdge.Right or MainWindowDockEdge.Bottom => DockCorner.BottomRight,
        MainWindowDockEdge.Top => DockCorner.TopRight,
        _ => throw new ArgumentOutOfRangeException(nameof(edge))
    };

    internal static double Lerp(double from, double to, double amount) => from + (to - from) * amount;
    internal static WpfPoint Lerp(WpfPoint from, WpfPoint to, double amount) => new(Lerp(from.X, to.X, amount), Lerp(from.Y, to.Y, amount));

    private static DockVisualFrame BuildFrame(double width, double height, WpfPoint contentOffset, WpfPoint scaleOrigin, AttachedEdges attached, double size)
    {
        WpfRect body = new(contentOffset, new WpfSize(size, size));
        double radius = Math.Min(10, size / 2);
        DockOutline outline = new(
            TopLeftCurve(body, radius, attached),
            TopRightCurve(body, radius, attached),
            BottomRightCurve(body, radius, attached),
            BottomLeftCurve(body, radius, attached));
        return new DockVisualFrame(width, height, contentOffset, scaleOrigin, outline);
    }

    private static DockCubicCorner TopLeftCurve(WpfRect body, double radius, AttachedEdges attached)
    {
        bool left = attached.HasFlag(AttachedEdges.Left);
        bool top = attached.HasFlag(AttachedEdges.Top);
        if (left && top) return Square(body.Left, body.Top);
        if (left) return new DockCubicCorner(new WpfPoint(body.Left, body.Top - TransitionSize), new WpfPoint(body.Left, body.Top - TransitionSize * TransitionControlFactor), new WpfPoint(body.Left + TransitionSize * TransitionControlFactor, body.Top), new WpfPoint(body.Left + TransitionSize, body.Top));
        if (top) return new DockCubicCorner(new WpfPoint(body.Left, body.Top + TransitionSize), new WpfPoint(body.Left, body.Top + TransitionSize * TransitionControlFactor), new WpfPoint(body.Left - TransitionSize * TransitionControlFactor, body.Top), new WpfPoint(body.Left - TransitionSize, body.Top));
        return RoundedTopLeft(body, radius);
    }

    private static DockCubicCorner TopRightCurve(WpfRect body, double radius, AttachedEdges attached)
    {
        bool right = attached.HasFlag(AttachedEdges.Right);
        bool top = attached.HasFlag(AttachedEdges.Top);
        if (right && top) return Square(body.Right, body.Top);
        if (right) return new DockCubicCorner(new WpfPoint(body.Right - TransitionSize, body.Top), new WpfPoint(body.Right - TransitionSize * TransitionControlFactor, body.Top), new WpfPoint(body.Right, body.Top - TransitionSize * TransitionControlFactor), new WpfPoint(body.Right, body.Top - TransitionSize));
        if (top) return new DockCubicCorner(new WpfPoint(body.Right + TransitionSize, body.Top), new WpfPoint(body.Right + TransitionSize * TransitionControlFactor, body.Top), new WpfPoint(body.Right, body.Top + TransitionSize * TransitionControlFactor), new WpfPoint(body.Right, body.Top + TransitionSize));
        return RoundedTopRight(body, radius);
    }

    private static DockCubicCorner BottomRightCurve(WpfRect body, double radius, AttachedEdges attached)
    {
        bool right = attached.HasFlag(AttachedEdges.Right);
        bool bottom = attached.HasFlag(AttachedEdges.Bottom);
        if (right && bottom) return Square(body.Right, body.Bottom);
        if (right) return new DockCubicCorner(new WpfPoint(body.Right, body.Bottom + TransitionSize), new WpfPoint(body.Right, body.Bottom + TransitionSize * TransitionControlFactor), new WpfPoint(body.Right - TransitionSize * TransitionControlFactor, body.Bottom), new WpfPoint(body.Right - TransitionSize, body.Bottom));
        if (bottom) return new DockCubicCorner(new WpfPoint(body.Right, body.Bottom - TransitionSize), new WpfPoint(body.Right, body.Bottom - TransitionSize * TransitionControlFactor), new WpfPoint(body.Right + TransitionSize * TransitionControlFactor, body.Bottom), new WpfPoint(body.Right + TransitionSize, body.Bottom));
        return RoundedBottomRight(body, radius);
    }

    private static DockCubicCorner BottomLeftCurve(WpfRect body, double radius, AttachedEdges attached)
    {
        bool left = attached.HasFlag(AttachedEdges.Left);
        bool bottom = attached.HasFlag(AttachedEdges.Bottom);
        if (left && bottom) return Square(body.Left, body.Bottom);
        if (left) return new DockCubicCorner(new WpfPoint(body.Left + TransitionSize, body.Bottom), new WpfPoint(body.Left + TransitionSize * TransitionControlFactor, body.Bottom), new WpfPoint(body.Left, body.Bottom + TransitionSize * TransitionControlFactor), new WpfPoint(body.Left, body.Bottom + TransitionSize));
        if (bottom) return new DockCubicCorner(new WpfPoint(body.Left - TransitionSize, body.Bottom), new WpfPoint(body.Left - TransitionSize * TransitionControlFactor, body.Bottom), new WpfPoint(body.Left, body.Bottom - TransitionSize * TransitionControlFactor), new WpfPoint(body.Left, body.Bottom - TransitionSize));
        return RoundedBottomLeft(body, radius);
    }

    private static DockCubicCorner RoundedTopLeft(WpfRect body, double radius) => new(new WpfPoint(body.Left, body.Top + radius), new WpfPoint(body.Left, body.Top + radius / 3), new WpfPoint(body.Left + radius / 3, body.Top), new WpfPoint(body.Left + radius, body.Top));
    private static DockCubicCorner RoundedTopRight(WpfRect body, double radius) => new(new WpfPoint(body.Right - radius, body.Top), new WpfPoint(body.Right - radius / 3, body.Top), new WpfPoint(body.Right, body.Top + radius / 3), new WpfPoint(body.Right, body.Top + radius));
    private static DockCubicCorner RoundedBottomRight(WpfRect body, double radius) => new(new WpfPoint(body.Right, body.Bottom - radius), new WpfPoint(body.Right, body.Bottom - radius / 3), new WpfPoint(body.Right - radius / 3, body.Bottom), new WpfPoint(body.Right - radius, body.Bottom));
    private static DockCubicCorner RoundedBottomLeft(WpfRect body, double radius) => new(new WpfPoint(body.Left + radius, body.Bottom), new WpfPoint(body.Left + radius / 3, body.Bottom), new WpfPoint(body.Left, body.Bottom - radius / 3), new WpfPoint(body.Left, body.Bottom - radius));
    private static DockCubicCorner Square(double x, double y) { WpfPoint point = new(x, y); return new DockCubicCorner(point, point, point, point); }

    private static WpfPoint EdgePosition(MainWindowDockEdge edge, WpfRect workArea, int width, int height, double normalized)
    {
        double availableX = Math.Max(0, workArea.Width - width);
        double availableY = Math.Max(0, workArea.Height - height);
        double x = workArea.Left + Math.Round(availableX * normalized);
        double y = workArea.Top + Math.Round(availableY * normalized);
        return edge switch
        {
            MainWindowDockEdge.Left => new WpfPoint(workArea.Left, y),
            MainWindowDockEdge.Right => new WpfPoint(workArea.Right - width, y),
            MainWindowDockEdge.Top => new WpfPoint(x, workArea.Top),
            MainWindowDockEdge.Bottom => new WpfPoint(x, workArea.Bottom - height),
            _ => throw new ArgumentOutOfRangeException(nameof(edge))
        };
    }

    private static WpfPoint CornerPosition(DockCorner corner, WpfRect workArea, DockVisualFrame frame, double scaling)
    {
        int width = ToPixels(frame.Width, scaling);
        int height = ToPixels(frame.Height, scaling);
        return corner switch
        {
            DockCorner.TopLeft => new WpfPoint(workArea.Left, workArea.Top),
            DockCorner.TopRight => new WpfPoint(workArea.Right - width, workArea.Top),
            DockCorner.BottomRight => new WpfPoint(workArea.Right - width, workArea.Bottom - height),
            DockCorner.BottomLeft => new WpfPoint(workArea.Left, workArea.Bottom - height),
            _ => throw new ArgumentOutOfRangeException(nameof(corner))
        };
    }

    private static int ToPixels(double value, double scaling) => Math.Max(1, (int)Math.Round(value * Math.Max(0.01, scaling)));
    private static double Normalize(double offset, double available) => available <= 0 ? 0 : Math.Clamp(offset / available, 0, 1);
}
