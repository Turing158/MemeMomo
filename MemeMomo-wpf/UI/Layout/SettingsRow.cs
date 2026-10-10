using System.Windows;
using System.Windows.Controls;
using Panel = System.Windows.Controls.Panel;

namespace MemeMomo.UI.Layout;

/// <summary>Measures one description/control pair using the row's own available width.</summary>
public sealed class SettingsRow : Panel
{
    public static readonly DependencyProperty ControlWidthProperty = DependencyProperty.Register(
        nameof(ControlWidth), typeof(double), typeof(SettingsRow),
        new FrameworkPropertyMetadata(280d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ControlWidth
    {
        get => (double)GetValue(ControlWidthProperty);
        set => SetValue(ControlWidthProperty, value);
    }

    private bool _horizontal;
    private double _controlWidth;
    private const double Gap = 16;
    internal const double HorizontalThreshold = 480;

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
    {
        if (InternalChildren.Count != 2) return base.MeasureOverride(availableSize);
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : 800;
        _controlWidth = Math.Min(ControlWidth, width);
        _horizontal = width >= HorizontalThreshold && width - _controlWidth - Gap >= 180;
        var description = InternalChildren[0];
        var control = InternalChildren[1];
        control.Measure(new System.Windows.Size(_horizontal ? _controlWidth : width, double.PositiveInfinity));
        description.Measure(new System.Windows.Size(_horizontal ? width - _controlWidth - Gap : width, double.PositiveInfinity));
        return new System.Windows.Size(width, _horizontal
            ? Math.Max(description.DesiredSize.Height, control.DesiredSize.Height)
            : description.DesiredSize.Height + 8 + control.DesiredSize.Height);
    }

    protected override System.Windows.Size ArrangeOverride(System.Windows.Size finalSize)
    {
        if (InternalChildren.Count != 2) return finalSize;
        var description = InternalChildren[0];
        var control = InternalChildren[1];
        if (_horizontal)
        {
            description.Arrange(new Rect(0, 0, Math.Max(0, finalSize.Width - _controlWidth - Gap), finalSize.Height));
            control.Arrange(new Rect(finalSize.Width - _controlWidth, 0, _controlWidth, finalSize.Height));
        }
        else
        {
            description.Arrange(new Rect(0, 0, finalSize.Width, description.DesiredSize.Height));
            control.Arrange(new Rect(0, description.DesiredSize.Height + 8, finalSize.Width, control.DesiredSize.Height));
        }
        return finalSize;
    }
}
