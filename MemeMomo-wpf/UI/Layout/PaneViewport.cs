using System.Windows;
using System.Windows.Controls;
using Size = System.Windows.Size;

namespace MemeMomo.UI.Layout;

/// <summary>Clips a shrinking pane without shrinking or clipping its content before translation.</summary>
public sealed class PaneViewport : Decorator
{
    public static readonly DependencyProperty ContentWidthProperty = DependencyProperty.Register(
        nameof(ContentWidth), typeof(double), typeof(PaneViewport),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ContentWidth
    {
        get => (double)GetValue(ContentWidthProperty);
        set => SetValue(ContentWidthProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null) return new Size();
        Child.Measure(new Size(double.IsNaN(ContentWidth) ? constraint.Width : ContentWidth, constraint.Height));
        return new Size(Math.Min(constraint.Width, Child.DesiredSize.Width), Child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(0, 0, double.IsNaN(ContentWidth) ? arrangeSize.Width : ContentWidth, arrangeSize.Height));
        return arrangeSize;
    }
}
