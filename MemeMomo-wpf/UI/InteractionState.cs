using System.Windows;
using System.Windows.Input;

namespace MemeMomo.UI;

/// <summary>
/// Composable state attached properties used by resource styles. State is kept on
/// the control instead of swapping Style instances, so hover, pressed, editing,
/// pin and danger states can be combined and inspected by tests.
/// </summary>
public static class InteractionState
{
    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.RegisterAttached(
            "IsSelected",
            typeof(bool),
            typeof(InteractionState),
            new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsEditingProperty =
        DependencyProperty.RegisterAttached(
            "IsEditing",
            typeof(bool),
            typeof(InteractionState),
            new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsPinActiveProperty =
        DependencyProperty.RegisterAttached(
            "IsPinActive",
            typeof(bool),
            typeof(InteractionState),
            new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsDangerProperty =
        DependencyProperty.RegisterAttached(
            "IsDanger",
            typeof(bool),
            typeof(InteractionState),
            new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsRevealedProperty =
        DependencyProperty.RegisterAttached(
            "IsRevealed",
            typeof(bool),
            typeof(InteractionState),
            new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsInvalidProperty =
        DependencyProperty.RegisterAttached(
            "IsInvalid",
            typeof(bool),
            typeof(InteractionState),
            new FrameworkPropertyMetadata(false));

    private static readonly DependencyPropertyKey IsPressedPropertyKey =
        DependencyProperty.RegisterAttachedReadOnly(
            "IsPressed",
            typeof(bool),
            typeof(InteractionState),
            new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty IsPressedProperty = IsPressedPropertyKey.DependencyProperty;

    public static readonly DependencyProperty TrackPointerStateProperty =
        DependencyProperty.RegisterAttached(
            "TrackPointerState",
            typeof(bool),
            typeof(InteractionState),
            new FrameworkPropertyMetadata(false, OnTrackPointerStateChanged));

    public static bool GetIsSelected(DependencyObject element) => (bool)element.GetValue(IsSelectedProperty);
    public static void SetIsSelected(DependencyObject element, bool value) => element.SetValue(IsSelectedProperty, value);
    public static bool GetIsEditing(DependencyObject element) => (bool)element.GetValue(IsEditingProperty);
    public static void SetIsEditing(DependencyObject element, bool value) => element.SetValue(IsEditingProperty, value);
    public static bool GetIsPinActive(DependencyObject element) => (bool)element.GetValue(IsPinActiveProperty);
    public static void SetIsPinActive(DependencyObject element, bool value) => element.SetValue(IsPinActiveProperty, value);
    public static bool GetIsDanger(DependencyObject element) => (bool)element.GetValue(IsDangerProperty);
    public static void SetIsDanger(DependencyObject element, bool value) => element.SetValue(IsDangerProperty, value);
    public static bool GetIsRevealed(DependencyObject element) => (bool)element.GetValue(IsRevealedProperty);
    public static void SetIsRevealed(DependencyObject element, bool value) => element.SetValue(IsRevealedProperty, value);
    public static bool GetIsInvalid(DependencyObject element) => (bool)element.GetValue(IsInvalidProperty);
    public static void SetIsInvalid(DependencyObject element, bool value) => element.SetValue(IsInvalidProperty, value);
    public static bool GetIsPressed(DependencyObject element) => (bool)element.GetValue(IsPressedProperty);
    internal static void SetIsPressedForTest(DependencyObject element, bool value) => SetPressed(element, value);
    public static bool GetTrackPointerState(DependencyObject element) => (bool)element.GetValue(TrackPointerStateProperty);
    public static void SetTrackPointerState(DependencyObject element, bool value) => element.SetValue(TrackPointerStateProperty, value);

    private static void OnTrackPointerStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            element.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            element.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
            element.LostMouseCapture += OnLostMouseCapture;
            if (element is FrameworkElement frameworkElement)
            {
                frameworkElement.Unloaded += OnUnloaded;
            }
        }
        else
        {
            element.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
            element.PreviewMouseLeftButtonUp -= OnPreviewMouseLeftButtonUp;
            element.LostMouseCapture -= OnLostMouseCapture;
            if (element is FrameworkElement frameworkElement)
            {
                frameworkElement.Unloaded -= OnUnloaded;
            }
            SetPressed(element, false);
        }
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is UIElement element)
        {
            SetPressed(element, true);
        }
    }

    private static void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is UIElement element)
        {
            SetPressed(element, false);
        }
    }

    private static void OnLostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is UIElement element)
        {
            SetPressed(element, false);
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement element)
        {
            SetTrackPointerState(element, false);
        }
    }

    private static void SetPressed(DependencyObject element, bool value) =>
        element.SetValue(IsPressedPropertyKey, value);
}

public static class CornerRadiusProxy
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.RegisterAttached(
            "Value",
            typeof(CornerRadius),
            typeof(CornerRadiusProxy),
            new FrameworkPropertyMetadata(new CornerRadius(8)));

    public static CornerRadius GetValue(DependencyObject element) => (CornerRadius)element.GetValue(ValueProperty);
    public static void SetValue(DependencyObject element, CornerRadius value) => element.SetValue(ValueProperty, value);
}
