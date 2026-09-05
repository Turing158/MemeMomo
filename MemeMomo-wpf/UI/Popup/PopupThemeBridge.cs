using System.Windows;
using System.Windows.Media;

namespace MemeMomo.UI.Popup;

internal static class PopupThemeBridge
{
    internal const string BackgroundKey = "MemeMomo.Popup.Background";
    internal const string BorderKey = "MemeMomo.Popup.Border";
    internal const string ForegroundKey = "MemeMomo.Popup.Foreground";

    internal static void Bind(FrameworkElement popupRoot)
    {
        popupRoot.SetResourceReference(FrameworkElement.TagProperty, BackgroundKey);
        if (popupRoot is System.Windows.Controls.Control control)
        {
            control.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, BackgroundKey);
            control.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, BorderKey);
            control.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, ForegroundKey);
        }
        else if (popupRoot is System.Windows.Controls.Border border)
        {
            border.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, BackgroundKey);
            border.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, BorderKey);
        }
    }

    internal static void ApplyLight(ResourceDictionary resources)
    {
        resources[BackgroundKey] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255));
        resources[BorderKey] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(224, 224, 224));
        resources[ForegroundKey] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 32, 32));
    }

    internal static void ApplyDark(ResourceDictionary resources)
    {
        resources[BackgroundKey] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 38, 38));
        resources[BorderKey] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(72, 72, 72));
        resources[ForegroundKey] = new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 245, 245));
    }
}
