using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;

namespace MemeMomo.UI.Popup;

internal static class TaskContextMenu
{
    internal static ContextMenu Create(FrameworkElement target, PlacementMode placement = PlacementMode.Bottom, bool useListScrollBar = false)
    {
        if (target.ContextMenu is { IsOpen: true } previous)
            ContextMenuAnimations.Close(previous, immediate: true);

        var menu = new ContextMenu
        {
            PlacementTarget = target,
            Placement = placement,
            Style = (Style)target.FindResource("TaskMenuStyle")
        };
        if (useListScrollBar)
        {
            menu.Resources[typeof(ScrollBar)] = target.FindResource("EditorOverlayScrollBarStyle");
            // ScrollViewer's template also reads this metric for the scrollbar slot.
            menu.Resources[SystemParameters.VerticalScrollBarWidthKey] = 10d;
        }
        ContextMenuAnimations.SetIsEnabled(menu, true);
        target.ContextMenu = menu;
        target.Unloaded += OnTargetUnloaded;
        menu.Closed += (_, _) =>
        {
            target.Unloaded -= OnTargetUnloaded;
            if (!ReferenceEquals(target.ContextMenu, menu)) return;
            target.ContextMenu = null;
            if (target.IsLoaded && target.IsVisible && target.IsEnabled) target.Focus();
        };
        return menu;

        void OnTargetUnloaded(object sender, RoutedEventArgs e) => ContextMenuAnimations.Close(menu, immediate: true);
    }

    internal static MenuItem Item(FrameworkElement owner, string label, bool? isChecked = null, string? iconKey = null) => new()
    {
        Header = label,
        ToolTip = label,
        IsCheckable = isChecked.HasValue,
        IsChecked = isChecked == true,
        Icon = iconKey is null ? null : new TaskStatusIcon { IconKey = iconKey, Width = 16, Height = 16 },
        Style = (Style)owner.FindResource("TaskMenuItemStyle")
    };

    internal static Separator Separator(FrameworkElement owner) => new()
    {
        Style = (Style)owner.FindResource("MarkdownToolbarMenuSeparatorTheme")
    };

    internal static void Open(ContextMenu menu)
    {
        ContextMenuAnimations.Open(menu);
        // Focus the menu itself so arrows/Esc work without highlighting the first row.
        menu.Focus();
    }
}
