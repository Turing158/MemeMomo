using System.Windows;
using System.Windows.Controls;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfPanel = System.Windows.Controls.Panel;
using WpfRect = System.Windows.Rect;
using WpfSize = System.Windows.Size;

namespace Memo.UI.Layout;

public sealed class SpacedStackPanel : WpfPanel
{
    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation),
        typeof(WpfOrientation),
        typeof(SpacedStackPanel),
        new FrameworkPropertyMetadata(WpfOrientation.Vertical, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing),
        typeof(double),
        typeof(SpacedStackPanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public WpfOrientation Orientation
    {
        get => (WpfOrientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override WpfSize MeasureOverride(WpfSize availableSize)
    {
        double primary = 0;
        double secondary = 0;
        WpfSize childConstraint = Orientation == WpfOrientation.Vertical
            ? new WpfSize(availableSize.Width, double.PositiveInfinity)
            : new WpfSize(double.PositiveInfinity, availableSize.Height);
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            child.Measure(childConstraint);
            if (Orientation == WpfOrientation.Vertical)
            {
                primary += child.DesiredSize.Height;
                secondary = Math.Max(secondary, child.DesiredSize.Width);
            }
            else
            {
                primary += child.DesiredSize.Width;
                secondary = Math.Max(secondary, child.DesiredSize.Height);
            }
        }

        int visibleChildren = InternalChildren.Cast<UIElement>()
            .Count(child => child.Visibility != Visibility.Collapsed);
        primary += Math.Max(0, visibleChildren - 1) * Math.Max(0, Spacing);
        return Orientation == WpfOrientation.Vertical
            ? new WpfSize(secondary, primary)
            : new WpfSize(primary, secondary);
    }

    protected override WpfSize ArrangeOverride(WpfSize finalSize)
    {
        double offset = 0;
        bool hasPrevious = false;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Arrange(new WpfRect(0, 0, 0, 0));
                continue;
            }

            if (hasPrevious)
            {
                offset += Math.Max(0, Spacing);
            }

            if (Orientation == WpfOrientation.Vertical)
            {
                child.Arrange(new WpfRect(0, offset, finalSize.Width, child.DesiredSize.Height));
                offset += child.DesiredSize.Height;
            }
            else
            {
                child.Arrange(new WpfRect(offset, 0, child.DesiredSize.Width, finalSize.Height));
                offset += child.DesiredSize.Width;
            }

            hasPrevious = true;
        }

        return finalSize;
    }
}
