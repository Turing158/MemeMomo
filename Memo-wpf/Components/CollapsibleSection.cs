using System.Windows;
using System.Windows.Controls;
using Memo.UI;
using Memo.UI.Animation;
using Size = System.Windows.Size;

namespace Memo.Components;

public sealed class CollapsibleSection : ContentControl
{
    private static readonly TimeSpan MinimumReversalDuration = TimeSpan.FromMilliseconds(60);

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded),
        typeof(bool),
        typeof(CollapsibleSection),
        new FrameworkPropertyMetadata(
            true,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnIsExpandedChanged));

    public static readonly DependencyProperty SpacingCompensationProperty = DependencyProperty.Register(
        nameof(SpacingCompensation),
        typeof(double),
        typeof(CollapsibleSection),
        new FrameworkPropertyMetadata(0d, OnSpacingCompensationChanged, CoerceSpacingCompensation));

    private double _progress = 1;
    private double _fullHeight;

    static CollapsibleSection()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(CollapsibleSection),
            new FrameworkPropertyMetadata(typeof(ContentControl)));
    }

    public CollapsibleSection()
    {
        ClipToBounds = true;
        VerticalContentAlignment = VerticalAlignment.Top;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public double SpacingCompensation
    {
        get => (double)GetValue(SpacingCompensationProperty);
        set => SetValue(SpacingCompensationProperty, value);
    }

    internal double ExpansionProgress => _progress;

    protected override Size MeasureOverride(Size constraint)
    {
        Size desired = base.MeasureOverride(new Size(constraint.Width, double.PositiveInfinity));
        _fullHeight = desired.Height;
        return new Size(desired.Width, Math.Max(0, _fullHeight * _progress));
    }

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        base.ArrangeOverride(new Size(arrangeBounds.Width, Math.Max(arrangeBounds.Height, _fullHeight)));
        return arrangeBounds;
    }

    private static object CoerceSpacingCompensation(DependencyObject d, object baseValue) =>
        Math.Max(0, (double)baseValue);

    private static void OnIsExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CollapsibleSection)d).ApplyExpansionState(animate: ((CollapsibleSection)d).IsLoaded);

    private static void OnSpacingCompensationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CollapsibleSection)d).SetProgress(((CollapsibleSection)d)._progress);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetProgress(IsExpanded ? 1 : 0);
        Visibility = IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        IsHitTestVisible = IsExpanded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        MotionAnimations.Cancel(this);
        SetProgress(IsExpanded ? 1 : 0);
        IsHitTestVisible = IsExpanded;
    }

    private void ApplyExpansionState(bool animate)
    {
        double target = IsExpanded ? 1 : 0;
        if (!animate)
        {
            MotionAnimations.Cancel(this);
            SetProgress(target);
            Visibility = IsExpanded ? Visibility.Visible : Visibility.Collapsed;
            IsHitTestVisible = IsExpanded;
            return;
        }

        double from = _progress;
        double distance = Math.Abs(target - from);
        if (distance < 0.001)
        {
            MotionAnimations.Cancel(this);
            SetProgress(target);
            Visibility = IsExpanded ? Visibility.Visible : Visibility.Collapsed;
            IsHitTestVisible = IsExpanded;
            return;
        }

        TimeSpan duration = DurationForDistance(distance);
        if (IsExpanded)
        {
            Visibility = Visibility.Visible;
            IsHitTestVisible = true;
            MotionAnimations.Start(
                this,
                duration,
                MotionEasing.CubicEaseOut,
                progress => SetProgress(from + ((1 - from) * progress)),
                () => SetProgress(1));
        }
        else
        {
            IsHitTestVisible = false;
            MotionAnimations.Start(
                this,
                duration,
                MotionEasing.CubicEaseIn,
                progress => SetProgress(from * (1 - progress)),
                () =>
                {
                    SetProgress(0);
                    Visibility = Visibility.Collapsed;
                });
        }
    }

    private void SetProgress(double progress)
    {
        _progress = Math.Clamp(progress, 0, 1);
        Opacity = Math.Clamp(_progress * 1.5, 0, 1);
        Margin = new Thickness(0, 0, 0, -SpacingCompensation * (1 - _progress));
        InvalidateMeasure();
    }

    internal static TimeSpan DurationForDistance(double distance)
    {
        TimeSpan standard = MotionPreferences.StandardDuration;
        if (standard <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        long scaledTicks = (long)(standard.Ticks * Math.Clamp(distance, 0, 1));
        return TimeSpan.FromTicks(Math.Max(MinimumReversalDuration.Ticks, scaledTicks));
    }
}
