using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfRect = System.Windows.Rect;
using WpfBrush = System.Windows.Media.Brush;
using WpfPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;

namespace MemeMomo.UI.Windows;

internal readonly record struct DockHandleRenderRequest(
    DockVisualFrame Frame,
    double DockSize,
    double Scale,
    double Opacity,
    DpiScale2 Dpi,
    WpfBrush Fill,
    WpfBrush Stroke,
    ImageSource? Icon);

internal sealed class DockHandleBitmap
{
    internal DockHandleBitmap(int pixelWidth, int pixelHeight, int stride, byte[] pixels)
    {
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        Stride = stride;
        Pixels = pixels;
    }

    internal int PixelWidth { get; }

    internal int PixelHeight { get; }

    internal int Stride { get; }

    internal byte[] Pixels { get; }

    internal bool HasPartialAlpha
    {
        get
        {
            for (int index = 3; index < Pixels.Length; index += 4)
            {
                if (Pixels[index] is > 0 and < 255)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

internal interface IDockHandleRenderer
{
    DockHandleBitmap Render(DockHandleRenderRequest request);
}

/// <summary>Renders the shared dock geometry into a premultiplied BGRA surface.</summary>
internal sealed class DockHandleRenderer : IDockHandleRenderer
{
    private const double IconSize = 24;
    private const double OutlineOpacity = 0.55;

    public DockHandleBitmap Render(DockHandleRenderRequest request)
    {
        Validate(request);
        int width = Math.Max(1, (int)Math.Round(request.Frame.Width * request.Dpi.ScaleX));
        int height = Math.Max(1, (int)Math.Round(request.Frame.Height * request.Dpi.ScaleY));
        RenderTargetBitmap target = new(width, height, 96, 96, PixelFormats.Pbgra32);
        DrawingVisual visual = new();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.PushOpacity(Math.Clamp(request.Opacity, 0, 1));
            Matrix transform = Matrix.Identity;
            double scale = Math.Clamp(request.Scale, 0.01, 4);
            transform.ScaleAt(
                scale,
                scale,
                request.Frame.ScaleOrigin.X * request.Frame.Width,
                request.Frame.ScaleOrigin.Y * request.Frame.Height);
            transform.Scale(request.Dpi.ScaleX, request.Dpi.ScaleY);
            drawing.PushTransform(new MatrixTransform(transform));

            Geometry geometry = request.Frame.Outline.CreateGeometry();
            WpfBrush stroke = request.Stroke.CloneCurrentValue();
            stroke.Opacity *= OutlineOpacity;
            stroke.Freeze();
            WpfPen outline = new(stroke, 1)
            {
                LineJoin = PenLineJoin.Round
            };
            drawing.DrawGeometry(request.Fill, outline, geometry);
            if (request.Icon is not null)
            {
                double iconSize = Math.Min(IconSize, Math.Max(1, request.DockSize));
                double inset = (request.DockSize - iconSize) / 2;
                WpfRect iconBounds = new(
                    request.Frame.ContentOffset.X + inset,
                    request.Frame.ContentOffset.Y + inset,
                    iconSize,
                    iconSize);
                drawing.DrawImage(request.Icon, iconBounds);
            }

            drawing.Pop();
            drawing.Pop();
        }

        target.Render(visual);
        int stride = checked(width * 4);
        byte[] pixels = new byte[checked(stride * height)];
        target.CopyPixels(pixels, stride, 0);
        return new DockHandleBitmap(width, height, stride, pixels);
    }

    internal static bool HitTest(DockHandleRenderRequest request, int xPixels, int yPixels)
    {
        if (request.Opacity <= 0.01
            || xPixels < 0
            || yPixels < 0
            || xPixels >= request.Frame.Width * request.Dpi.ScaleX
            || yPixels >= request.Frame.Height * request.Dpi.ScaleY)
        {
            return false;
        }

        WpfPoint point = new(
            xPixels / Math.Max(0.01, request.Dpi.ScaleX),
            yPixels / Math.Max(0.01, request.Dpi.ScaleY));
        Geometry geometry = request.Frame.Outline.CreateGeometry().Clone();
        double scale = Math.Clamp(request.Scale, 0.01, 4);
        geometry.Transform = new ScaleTransform(
            scale,
            scale,
            request.Frame.ScaleOrigin.X * request.Frame.Width,
            request.Frame.ScaleOrigin.Y * request.Frame.Height);
        return geometry.FillContains(point);
    }

    private static void Validate(DockHandleRenderRequest request)
    {
        if (!double.IsFinite(request.Frame.Width)
            || !double.IsFinite(request.Frame.Height)
            || request.Frame.Width <= 0
            || request.Frame.Height <= 0
            || !double.IsFinite(request.DockSize)
            || request.DockSize <= 0
            || request.Dpi.DpiX <= 0
            || request.Dpi.DpiY <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        ArgumentNullException.ThrowIfNull(request.Fill);
        ArgumentNullException.ThrowIfNull(request.Stroke);
    }
}
