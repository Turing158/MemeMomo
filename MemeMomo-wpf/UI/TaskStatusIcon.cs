using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml.Linq;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Size = System.Windows.Size;

namespace MemeMomo.UI;

public sealed class TaskStatusIcon : FrameworkElement
{
    public static readonly DependencyProperty IconKeyProperty = DependencyProperty.Register(
        nameof(IconKey), typeof(string), typeof(TaskStatusIcon),
        new FrameworkPropertyMetadata("Circle", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(TaskStatusIcon), new FrameworkPropertyMetadata(Brushes.Black,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly ConcurrentDictionary<string, IconSource> Sources = new(StringComparer.Ordinal);
    private Geometry? _morphGeometry;

    // Card-only animation surface. All other consumers continue to draw the exact SVG.
    internal Geometry? MorphGeometry
    {
        get => _morphGeometry;
        set { _morphGeometry = value; InvalidateVisual(); }
    }

    internal static Geometry SourceGeometry(string key) => GetSource(key).Geometry;

    public string? IconKey { get => (string?)GetValue(IconKeyProperty); set => SetValue(IconKeyProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public TaskStatusIcon() => IsHitTestVisible = false;

    protected override Size MeasureOverride(Size availableSize) => new(16, 16);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        double scale = Math.Min(RenderSize.Width, RenderSize.Height) / 24;
        if (scale <= 0) return;
        var source = GetSource(IconKey);
        var pen = new Pen(Foreground, source.StrokeWidth)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        drawingContext.PushTransform(new TranslateTransform((RenderSize.Width - 24 * scale) / 2, (RenderSize.Height - 24 * scale) / 2));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        drawingContext.DrawGeometry(null, pen, _morphGeometry ?? source.Geometry);
        drawingContext.Pop();
        drawingContext.Pop();
    }

    private static IconSource GetSource(string? key)
    {
        // Only bundled, single-path SVGs are supported; unknown persisted keys use the circle.
        string name = key is "Circle" or "Play" or "Pause" or "Check" or "Cancel" or "Star" or "Flag" or "Clock"
            or "Reopen" or "Selection" or "ChevronDown" or "Filter" or "Back" ? key : "Circle";
        return Sources.GetOrAdd(name, static name =>
        {
            using Stream stream = System.Windows.Application.GetResourceStream(
                new Uri($"/MemeMomo;component/Assets/TaskIcons/{name}.svg", UriKind.Relative))!.Stream;
            var svg = XDocument.Load(stream).Root!;
            XNamespace ns = "http://www.w3.org/2000/svg";
            var geometry = Geometry.Parse(svg.Element(ns + "path")!.Attribute("d")!.Value);
            geometry.Freeze();
            return new IconSource(geometry, double.Parse(svg.Attribute("stroke-width")!.Value, CultureInfo.InvariantCulture));
        });
    }

    private sealed record IconSource(Geometry Geometry, double StrokeWidth);
}
