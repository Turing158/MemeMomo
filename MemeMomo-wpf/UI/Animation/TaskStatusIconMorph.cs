using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using Point = System.Windows.Point;

namespace MemeMomo.UI.Animation;

/// <summary>
/// Three canonical strokes (outline, primary, secondary), each sampled at the same
/// arc-length fractions. Missing strokes collapse to the centre. SVG parsing and
/// flattening happen once; interrupted transitions retain their actual point cloud.
/// </summary>
internal sealed class TaskStatusIconMorph : IDisposable
{
    internal const int SamplesPerStroke = 129;
    private static readonly ConcurrentDictionary<string, Point[]> Shapes = new(StringComparer.Ordinal);
    private readonly TaskStatusIcon _icon;
    private readonly FrameAnimation _animation;
    private string? _key;
    private Point[] _current = [];

    internal TaskStatusIconMorph(TaskStatusIcon icon, IAnimationFrameSource? frames = null)
    {
        _icon = icon;
        _animation = new(frames);
    }

    internal bool IsRunning => _animation.IsRunning;
    internal long Generation => _animation.Generation;
    internal Point[] Capture() => (Point[])_current.Clone();
    internal static string NormalizeKey(string? key) => key is "Play" or "Pause" or "Check" or "Cancel" or "Star" or "Flag" or "Clock" ? key : "Circle";

    internal void SetIcon(string? key, bool animate)
    {
        string targetKey = NormalizeKey(key);
        if (_key == targetKey && animate) return;
        _key = targetKey;
        _icon.IconKey = targetKey;
        var target = Shapes.GetOrAdd(targetKey, CreateShape);
        if (!animate || _current.Length == 0)
        {
            _animation.Cancel();
            _current = (Point[])target.Clone();
            _icon.MorphGeometry = null;
            return;
        }

        var from = Capture();
        _animation.Start(TimeSpan.FromMilliseconds(190), MotionEasing.CubicEaseInOut, progress =>
        {
            for (int i = 0; i < _current.Length; i++)
                _current[i] = from[i] + (target[i] - from[i]) * progress;
            _icon.MorphGeometry = progress >= 1 ? null : Draw(_current);
        });
    }

    private static Point[] CreateShape(string key)
    {
        var source = TaskStatusIcon.SourceGeometry(key).GetFlattenedPathGeometry(0.002, ToleranceType.Absolute);
        var result = Enumerable.Repeat(new Point(12, 12), SamplesPerStroke * 3).ToArray();
        for (int stroke = 0; stroke < source.Figures.Count; stroke++)
        {
            var figure = source.Figures[stroke];
            List<Point> points = [figure.StartPoint];
            foreach (var segment in figure.Segments)
            {
                if (segment is PolyLineSegment poly) points.AddRange(poly.Points);
                else if (segment is LineSegment line) points.Add(line.Point);
            }
            if (figure.IsClosed)
            {
                if (points[^1] == points[0]) points.RemoveAt(points.Count - 1);
                // Identical circles keep both their phase and winding. Other closed
                // contours start at the rightmost vertex and use the same winding.
                double area = 0;
                for (int i = 0; i < points.Count; i++)
                {
                    var next = points[(i + 1) % points.Count];
                    area += points[i].X * next.Y - next.X * points[i].Y;
                }
                if (area < 0) points.Reverse();
                int start = Enumerable.Range(0, points.Count).OrderByDescending(i => points[i].X).ThenBy(i => Math.Abs(points[i].Y - 12)).First();
                points = points.Skip(start).Concat(points.Take(start)).ToList();
                points.Add(points[0]);
            }
            else if (stroke > 0 && points[0].X > points[^1].X)
                points.Reverse();

            double[] lengths = new double[points.Count];
            for (int i = 1; i < points.Count; i++) lengths[i] = lengths[i - 1] + (points[i] - points[i - 1]).Length;
            int edge = 1;
            for (int sample = 0; sample < SamplesPerStroke; sample++)
            {
                double distance = lengths[^1] * sample / (SamplesPerStroke - 1);
                while (edge < lengths.Length - 1 && lengths[edge] < distance) edge++;
                double length = lengths[edge] - lengths[edge - 1];
                result[stroke * SamplesPerStroke + sample] = length <= 0 ? points[edge] :
                    points[edge - 1] + (points[edge] - points[edge - 1]) * ((distance - lengths[edge - 1]) / length);
            }
        }
        return result;
    }

    private static Geometry Draw(Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (int stroke = 0; stroke < 3; stroke++)
            {
                int start = stroke * SamplesPerStroke;
                bool visible = false;
                for (int i = 1; i < SamplesPerStroke && !visible; i++) visible = (points[start + i] - points[start]).LengthSquared > 0.000001;
                if (!visible) continue;
                context.BeginFigure(points[start], false, points[start] == points[start + SamplesPerStroke - 1]);
                for (int i = 1; i < SamplesPerStroke; i++) context.LineTo(points[start + i], true, true);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    public void Dispose()
    {
        _animation.Dispose();
        _icon.MorphGeometry = null;
    }
}
