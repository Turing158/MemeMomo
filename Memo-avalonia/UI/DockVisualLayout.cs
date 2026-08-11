using Avalonia;
using Avalonia.Media;
using Memo.Models;
using System;

namespace Memo.UI;

internal enum DockCorner {
    None,
    TopLeft,
    TopRight,
    BottomRight,
    BottomLeft,
}

internal readonly record struct DockCubicCorner(
    Point Start,
    Point Control1,
    Point Control2,
    Point End) {

    public static DockCubicCorner Lerp(DockCubicCorner from, DockCubicCorner to, double amount) =>
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
    DockCubicCorner BottomLeft) {

    public static DockOutline Lerp(DockOutline from, DockOutline to, double amount) =>
        new(
            DockCubicCorner.Lerp(from.TopLeft, to.TopLeft, amount),
            DockCubicCorner.Lerp(from.TopRight, to.TopRight, amount),
            DockCubicCorner.Lerp(from.BottomRight, to.BottomRight, amount),
            DockCubicCorner.Lerp(from.BottomLeft, to.BottomLeft, amount));

    public Geometry CreateGeometry() {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(TopLeft.End, isFilled: true);
        context.LineTo(TopRight.Start);
        context.CubicBezierTo(TopRight.Control1, TopRight.Control2, TopRight.End);
        context.LineTo(BottomRight.Start);
        context.CubicBezierTo(BottomRight.Control1, BottomRight.Control2, BottomRight.End);
        context.LineTo(BottomLeft.Start);
        context.CubicBezierTo(BottomLeft.Control1, BottomLeft.Control2, BottomLeft.End);
        context.LineTo(TopLeft.Start);
        context.CubicBezierTo(TopLeft.Control1, TopLeft.Control2, TopLeft.End);
        context.EndFigure(isClosed: true);
        return geometry;
    }
}

internal readonly record struct DockVisualFrame(
    double Width,
    double Height,
    Point ContentOffset,
    Point ScaleOrigin,
    DockOutline Outline) {

    public static DockVisualFrame Lerp(DockVisualFrame from, DockVisualFrame to, double amount) {
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
    PixelPoint Position,
    double NormalizedPosition,
    DockVisualFrame Frame);

internal static class DockVisualLayoutCalculator {
    public const double TransitionSize = 5;

    [Flags]
    private enum AttachedEdges {
        None = 0,
        Left = 1,
        Top = 2,
        Right = 4,
        Bottom = 8,
    }

    public static DockVisualFrame ForEdge(MainWindowDockEdge edge, double size) {
        var transition = TransitionSize;
        return edge switch {
            MainWindowDockEdge.Left => BuildFrame(
                size,
                size + transition * 2,
                new Point(0, transition),
                new Point(0, 0.5),
                AttachedEdges.Left,
                size),
            MainWindowDockEdge.Right => BuildFrame(
                size,
                size + transition * 2,
                new Point(0, transition),
                new Point(1, 0.5),
                AttachedEdges.Right,
                size),
            MainWindowDockEdge.Top => BuildFrame(
                size + transition * 2,
                size,
                new Point(transition, 0),
                new Point(0.5, 0),
                AttachedEdges.Top,
                size),
            MainWindowDockEdge.Bottom => BuildFrame(
                size + transition * 2,
                size,
                new Point(transition, 0),
                new Point(0.5, 1),
                AttachedEdges.Bottom,
                size),
            _ => throw new ArgumentOutOfRangeException(nameof(edge)),
        };
    }

    public static DockVisualFrame ForCorner(DockCorner corner, double size) {
        var transition = TransitionSize;
        var full = size + transition;
        return corner switch {
            DockCorner.TopLeft => BuildFrame(
                full,
                full,
                new Point(0, 0),
                new Point(0, 0),
                AttachedEdges.Left | AttachedEdges.Top,
                size),
            DockCorner.TopRight => BuildFrame(
                full,
                full,
                new Point(transition, 0),
                new Point(1, 0),
                AttachedEdges.Top | AttachedEdges.Right,
                size),
            DockCorner.BottomRight => BuildFrame(
                full,
                full,
                new Point(transition, transition),
                new Point(1, 1),
                AttachedEdges.Right | AttachedEdges.Bottom,
                size),
            DockCorner.BottomLeft => BuildFrame(
                full,
                full,
                new Point(0, transition),
                new Point(0, 1),
                AttachedEdges.Bottom | AttachedEdges.Left,
                size),
            _ => throw new ArgumentOutOfRangeException(nameof(corner)),
        };
    }

    public static DockVisualFrame Interpolate(
        MainWindowDockEdge edge,
        DockCorner corner,
        double size,
        double progress) {
        var edgeFrame = ForEdge(edge, size);
        return corner == DockCorner.None
            ? edgeFrame
            : DockVisualFrame.Lerp(edgeFrame, ForCorner(corner, size), progress);
    }

    public static DockVisualTarget TargetFromCursor(
        MainWindowDockEdge edge,
        PixelRect workArea,
        double scaling,
        PixelPoint cursor,
        double size) {
        var edgeFrame = ForEdge(edge, size);
        var edgeWidth = ToPixels(edgeFrame.Width, scaling);
        var edgeHeight = ToPixels(edgeFrame.Height, scaling);
        var maxX = Math.Max(workArea.X, workArea.Right - edgeWidth);
        var maxY = Math.Max(workArea.Y, workArea.Bottom - edgeHeight);

        DockCorner corner;
        PixelPoint edgePosition;
        double normalized;
        if (edge is MainWindowDockEdge.Left or MainWindowDockEdge.Right) {
            var rawY = cursor.Y - edgeHeight / 2;
            if (rawY <= workArea.Y) {
                corner = StartCorner(edge);
                normalized = 0;
                edgePosition = EdgePosition(edge, workArea, edgeWidth, edgeHeight, 0);
            }
            else if (rawY >= maxY) {
                corner = EndCorner(edge);
                normalized = 1;
                edgePosition = EdgePosition(edge, workArea, edgeWidth, edgeHeight, 1);
            }
            else {
                corner = DockCorner.None;
                edgePosition = new PixelPoint(
                    edge == MainWindowDockEdge.Left ? workArea.X : maxX,
                    rawY);
                normalized = Normalize(rawY - workArea.Y, maxY - workArea.Y);
            }
        }
        else {
            var rawX = cursor.X - edgeWidth / 2;
            if (rawX <= workArea.X) {
                corner = StartCorner(edge);
                normalized = 0;
                edgePosition = EdgePosition(edge, workArea, edgeWidth, edgeHeight, 0);
            }
            else if (rawX >= maxX) {
                corner = EndCorner(edge);
                normalized = 1;
                edgePosition = EdgePosition(edge, workArea, edgeWidth, edgeHeight, 1);
            }
            else {
                corner = DockCorner.None;
                edgePosition = new PixelPoint(
                    rawX,
                    edge == MainWindowDockEdge.Top ? workArea.Y : maxY);
                normalized = Normalize(rawX - workArea.X, maxX - workArea.X);
            }
        }

        if (corner == DockCorner.None)
            return new DockVisualTarget(corner, edgePosition, normalized, edgeFrame);

        var cornerFrame = ForCorner(corner, size);
        return new DockVisualTarget(
            corner,
            CornerPosition(corner, workArea, cornerFrame, scaling),
            normalized,
            cornerFrame);
    }

    public static DockVisualTarget TargetFromNormalized(
        MainWindowDockEdge edge,
        PixelRect workArea,
        double scaling,
        double normalized,
        double size) {
        normalized = Math.Clamp(normalized, 0, 1);
        var corner = normalized <= 0
            ? StartCorner(edge)
            : normalized >= 1
                ? EndCorner(edge)
                : DockCorner.None;

        if (corner != DockCorner.None) {
            var cornerFrame = ForCorner(corner, size);
            return new DockVisualTarget(
                corner,
                CornerPosition(corner, workArea, cornerFrame, scaling),
                normalized,
                cornerFrame);
        }

        var edgeFrame = ForEdge(edge, size);
        var edgeWidth = ToPixels(edgeFrame.Width, scaling);
        var edgeHeight = ToPixels(edgeFrame.Height, scaling);
        return new DockVisualTarget(
            DockCorner.None,
            EdgePosition(edge, workArea, edgeWidth, edgeHeight, normalized),
            normalized,
            edgeFrame);
    }

    internal static DockCorner StartCorner(MainWindowDockEdge edge) => edge switch {
        MainWindowDockEdge.Left or MainWindowDockEdge.Top => DockCorner.TopLeft,
        MainWindowDockEdge.Right => DockCorner.TopRight,
        MainWindowDockEdge.Bottom => DockCorner.BottomLeft,
        _ => throw new ArgumentOutOfRangeException(nameof(edge)),
    };

    internal static DockCorner EndCorner(MainWindowDockEdge edge) => edge switch {
        MainWindowDockEdge.Left => DockCorner.BottomLeft,
        MainWindowDockEdge.Right or MainWindowDockEdge.Bottom => DockCorner.BottomRight,
        MainWindowDockEdge.Top => DockCorner.TopRight,
        _ => throw new ArgumentOutOfRangeException(nameof(edge)),
    };

    internal static double Lerp(double from, double to, double amount) =>
        from + (to - from) * amount;

    internal static Point Lerp(Point from, Point to, double amount) =>
        new(Lerp(from.X, to.X, amount), Lerp(from.Y, to.Y, amount));

    private static DockVisualFrame BuildFrame(
        double width,
        double height,
        Point contentOffset,
        Point scaleOrigin,
        AttachedEdges attached,
        double size) {
        var body = new Rect(contentOffset, new Size(size, size));
        var radius = Math.Min(10, size / 2);
        var outline = new DockOutline(
            TopLeftCurve(body, radius, attached),
            TopRightCurve(body, radius, attached),
            BottomRightCurve(body, radius, attached),
            BottomLeftCurve(body, radius, attached));
        return new DockVisualFrame(width, height, contentOffset, scaleOrigin, outline);
    }

    private static DockCubicCorner TopLeftCurve(Rect body, double radius, AttachedEdges attached) {
        var left = attached.HasFlag(AttachedEdges.Left);
        var top = attached.HasFlag(AttachedEdges.Top);
        if (left && top) return Square(body.Left, body.Top);
        if (left) {
            return new DockCubicCorner(
                new Point(body.Left, body.Top - TransitionSize),
                new Point(body.Left, body.Top - TransitionSize * 0.4),
                new Point(body.Left + TransitionSize * 0.4, body.Top),
                new Point(body.Left + TransitionSize, body.Top));
        }
        if (top) {
            return new DockCubicCorner(
                new Point(body.Left, body.Top + TransitionSize),
                new Point(body.Left, body.Top + TransitionSize * 0.4),
                new Point(body.Left - TransitionSize * 0.4, body.Top),
                new Point(body.Left - TransitionSize, body.Top));
        }
        return RoundedTopLeft(body, radius);
    }

    private static DockCubicCorner TopRightCurve(Rect body, double radius, AttachedEdges attached) {
        var right = attached.HasFlag(AttachedEdges.Right);
        var top = attached.HasFlag(AttachedEdges.Top);
        if (right && top) return Square(body.Right, body.Top);
        if (right) {
            return new DockCubicCorner(
                new Point(body.Right - TransitionSize, body.Top),
                new Point(body.Right - TransitionSize * 0.4, body.Top),
                new Point(body.Right, body.Top - TransitionSize * 0.4),
                new Point(body.Right, body.Top - TransitionSize));
        }
        if (top) {
            return new DockCubicCorner(
                new Point(body.Right + TransitionSize, body.Top),
                new Point(body.Right + TransitionSize * 0.4, body.Top),
                new Point(body.Right, body.Top + TransitionSize * 0.4),
                new Point(body.Right, body.Top + TransitionSize));
        }
        return RoundedTopRight(body, radius);
    }

    private static DockCubicCorner BottomRightCurve(Rect body, double radius, AttachedEdges attached) {
        var right = attached.HasFlag(AttachedEdges.Right);
        var bottom = attached.HasFlag(AttachedEdges.Bottom);
        if (right && bottom) return Square(body.Right, body.Bottom);
        if (right) {
            return new DockCubicCorner(
                new Point(body.Right, body.Bottom + TransitionSize),
                new Point(body.Right, body.Bottom + TransitionSize * 0.4),
                new Point(body.Right - TransitionSize * 0.4, body.Bottom),
                new Point(body.Right - TransitionSize, body.Bottom));
        }
        if (bottom) {
            return new DockCubicCorner(
                new Point(body.Right, body.Bottom - TransitionSize),
                new Point(body.Right, body.Bottom - TransitionSize * 0.4),
                new Point(body.Right + TransitionSize * 0.4, body.Bottom),
                new Point(body.Right + TransitionSize, body.Bottom));
        }
        return RoundedBottomRight(body, radius);
    }

    private static DockCubicCorner BottomLeftCurve(Rect body, double radius, AttachedEdges attached) {
        var left = attached.HasFlag(AttachedEdges.Left);
        var bottom = attached.HasFlag(AttachedEdges.Bottom);
        if (left && bottom) return Square(body.Left, body.Bottom);
        if (left) {
            return new DockCubicCorner(
                new Point(body.Left + TransitionSize, body.Bottom),
                new Point(body.Left + TransitionSize * 0.4, body.Bottom),
                new Point(body.Left, body.Bottom + TransitionSize * 0.4),
                new Point(body.Left, body.Bottom + TransitionSize));
        }
        if (bottom) {
            return new DockCubicCorner(
                new Point(body.Left - TransitionSize, body.Bottom),
                new Point(body.Left - TransitionSize * 0.4, body.Bottom),
                new Point(body.Left, body.Bottom - TransitionSize * 0.4),
                new Point(body.Left, body.Bottom - TransitionSize));
        }
        return RoundedBottomLeft(body, radius);
    }

    private static DockCubicCorner RoundedTopLeft(Rect body, double radius) =>
        new(
            new Point(body.Left, body.Top + radius),
            new Point(body.Left, body.Top + radius / 3),
            new Point(body.Left + radius / 3, body.Top),
            new Point(body.Left + radius, body.Top));

    private static DockCubicCorner RoundedTopRight(Rect body, double radius) =>
        new(
            new Point(body.Right - radius, body.Top),
            new Point(body.Right - radius / 3, body.Top),
            new Point(body.Right, body.Top + radius / 3),
            new Point(body.Right, body.Top + radius));

    private static DockCubicCorner RoundedBottomRight(Rect body, double radius) =>
        new(
            new Point(body.Right, body.Bottom - radius),
            new Point(body.Right, body.Bottom - radius / 3),
            new Point(body.Right - radius / 3, body.Bottom),
            new Point(body.Right - radius, body.Bottom));

    private static DockCubicCorner RoundedBottomLeft(Rect body, double radius) =>
        new(
            new Point(body.Left + radius, body.Bottom),
            new Point(body.Left + radius / 3, body.Bottom),
            new Point(body.Left, body.Bottom - radius / 3),
            new Point(body.Left, body.Bottom - radius));

    private static DockCubicCorner Square(double x, double y) {
        var point = new Point(x, y);
        return new DockCubicCorner(point, point, point, point);
    }

    private static PixelPoint EdgePosition(
        MainWindowDockEdge edge,
        PixelRect workArea,
        int width,
        int height,
        double normalized) {
        var availableX = Math.Max(0, workArea.Width - width);
        var availableY = Math.Max(0, workArea.Height - height);
        var x = workArea.X + (int)Math.Round(availableX * normalized);
        var y = workArea.Y + (int)Math.Round(availableY * normalized);
        return edge switch {
            MainWindowDockEdge.Left => new PixelPoint(workArea.X, y),
            MainWindowDockEdge.Right => new PixelPoint(workArea.Right - width, y),
            MainWindowDockEdge.Top => new PixelPoint(x, workArea.Y),
            MainWindowDockEdge.Bottom => new PixelPoint(x, workArea.Bottom - height),
            _ => throw new ArgumentOutOfRangeException(nameof(edge)),
        };
    }

    private static PixelPoint CornerPosition(
        DockCorner corner,
        PixelRect workArea,
        DockVisualFrame frame,
        double scaling) {
        var width = ToPixels(frame.Width, scaling);
        var height = ToPixels(frame.Height, scaling);
        return corner switch {
            DockCorner.TopLeft => new PixelPoint(workArea.X, workArea.Y),
            DockCorner.TopRight => new PixelPoint(workArea.Right - width, workArea.Y),
            DockCorner.BottomRight => new PixelPoint(workArea.Right - width, workArea.Bottom - height),
            DockCorner.BottomLeft => new PixelPoint(workArea.X, workArea.Bottom - height),
            _ => throw new ArgumentOutOfRangeException(nameof(corner)),
        };
    }

    private static int ToPixels(double value, double scaling) =>
        Math.Max(1, (int)Math.Round(value * Math.Max(0.01, scaling)));

    private static double Normalize(int offset, int available) =>
        available <= 0 ? 0 : Math.Clamp(offset / (double)available, 0, 1);
}
