using System.Windows;

namespace MemeMomo.UI;

public static class LayoutParity
{
    public static readonly DependencyProperty IsVisibleProperty =
        DependencyProperty.RegisterAttached(
            "IsVisible",
            typeof(bool),
            typeof(LayoutParity),
            new FrameworkPropertyMetadata(true, OnIsVisibleChanged));

    public static bool GetIsVisible(DependencyObject element) => (bool)element.GetValue(IsVisibleProperty);
    public static void SetIsVisible(DependencyObject element, bool value) => element.SetValue(IsVisibleProperty, value);

    public static Visibility ToVisibility(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private static void OnIsVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement element)
        {
            element.Visibility = ToVisibility((bool)e.NewValue);
        }
    }
}
