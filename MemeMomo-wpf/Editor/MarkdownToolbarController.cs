using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MemeMomo.UI.Popup;
using Button = System.Windows.Controls.Button;
using WpfPanel = System.Windows.Controls.Panel;

namespace MemeMomo.Editor;

internal sealed class MarkdownToolbarController : IDisposable
{
    private static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PointerCheckInterval = TimeSpan.FromMilliseconds(50);

    private readonly FrameworkElement _toolbar;
    private readonly FrameworkElement _formatToolbar;
    private readonly FrameworkElement _actions;
    private readonly Button _editSourceButton;
    private readonly Button _moreButton;
    private readonly MenuItem _editSourceMenuItem;
    private readonly Separator _separator;
    private readonly IReadOnlyList<(Button Button, MenuItem MenuItem)> _responsiveItems;
    private readonly DispatcherTimer _closeTimer;
    private ContextMenu? _activeMenu;
    private Button? _activeAnchor;
    private DateTime? _outsideSince;
    private int _disposed;

    internal MarkdownToolbarController(
        FrameworkElement toolbar,
        FrameworkElement formatToolbar,
        FrameworkElement actions,
        Button editSourceButton,
        Button moreButton,
        MenuItem editSourceMenuItem,
        Separator separator,
        IReadOnlyList<(Button Button, MenuItem MenuItem)> responsiveItems)
    {
        _toolbar = toolbar;
        _formatToolbar = formatToolbar;
        _actions = actions;
        _editSourceButton = editSourceButton;
        _moreButton = moreButton;
        _editSourceMenuItem = editSourceMenuItem;
        _separator = separator;
        _responsiveItems = responsiveItems;
        _closeTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = PointerCheckInterval
        };
        _closeTimer.Tick += OnCloseTimerTick;
        _toolbar.SizeChanged += OnToolbarSizeChanged;
    }

    internal void UpdateResponsiveItems()
    {
        // The format stack is clipped by the grid's first column. Calculate that
        // column from the toolbar content box, then fit complete buttons in order;
        // estimating from the outer Border width can leave the last button partly
        // covered by the actions column.
        double available = Math.Max(
            0,
            GetToolbarContentWidth() - GetActionsWidth() - GetFixedFormatWidth());
        double consumed = 0;
        int visibleCount = 0;
        foreach ((Button button, _) in _responsiveItems)
        {
            double width = GetLayoutWidth(button);
            if (consumed + width > available + 0.01)
            {
                break;
            }

            consumed += width;
            visibleCount++;
        }

        for (int index = 0; index < _responsiveItems.Count; index++)
        {
            bool promoted = index < visibleCount;
            _responsiveItems[index].Button.Visibility = promoted ? Visibility.Visible : Visibility.Collapsed;
            _responsiveItems[index].MenuItem.Visibility = promoted ? Visibility.Collapsed : Visibility.Visible;
        }

        bool overflow = visibleCount < _responsiveItems.Count;
        _editSourceButton.Visibility = overflow ? Visibility.Collapsed : Visibility.Visible;
        _editSourceMenuItem.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        _separator.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        _moreButton.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        if (!overflow && _moreButton.ContextMenu is { IsOpen: true } menu)
        {
            ContextMenuAnimations.Close(menu);
        }
    }

    private double GetToolbarContentWidth()
    {
        double width = _toolbar.ActualWidth;
        if (_toolbar is Border border)
        {
            width -= border.Padding.Left + border.Padding.Right;
            width -= border.BorderThickness.Left + border.BorderThickness.Right;
        }

        return Math.Max(0, width);
    }

    private double GetActionsWidth()
    {
        if (_actions is not WpfPanel panel)
        {
            return Math.Max(0, _actions.ActualWidth);
        }

        double width = 0;
        foreach (FrameworkElement child in panel.Children.OfType<FrameworkElement>())
        {
            if (ReferenceEquals(child, _editSourceButton) || ReferenceEquals(child, _moreButton))
            {
                continue;
            }

            width += GetLayoutWidth(child);
        }

        // EditSource and More occupy the same action slot. Reserve the larger
        // measured width so either state remains fully visible at the boundary.
        width += Math.Max(GetLayoutWidth(_editSourceButton), GetLayoutWidth(_moreButton));
        return width;
    }

    private double GetFixedFormatWidth()
    {
        if (_formatToolbar is not WpfPanel panel)
        {
            return 0;
        }

        HashSet<Button> responsiveButtons = _responsiveItems
            .Select(item => item.Button)
            .ToHashSet();
        return panel.Children
            .OfType<FrameworkElement>()
            .Where(child => child is not Button button || !responsiveButtons.Contains(button))
            .Sum(GetLayoutWidth);
    }

    private static double GetLayoutWidth(FrameworkElement element)
    {
        double width = element.ActualWidth;
        if (width <= 0)
        {
            width = element.DesiredSize.Width;
        }

        if (width <= 0 && !double.IsNaN(element.Width))
        {
            width = element.Width;
        }

        Thickness margin = element.Margin;
        return Math.Max(0, width) + margin.Left + margin.Right;
    }

    internal void OpenMenu(Button anchor)
    {
        CancelClose();
        if (_activeMenu is { IsOpen: true } active && !ReferenceEquals(active, anchor.ContextMenu))
        {
            ContextMenuAnimations.Close(active);
        }

        if (anchor.ContextMenu is not { } menu)
        {
            return;
        }
        _activeAnchor = anchor;
        _activeMenu = menu;
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        ContextMenuAnimations.Open(menu);
    }

    internal void ToggleMenu(Button anchor)
    {
        CancelClose();
        if (anchor.ContextMenu is not { } menu)
        {
            return;
        }

        if (ReferenceEquals(menu, _activeMenu) && menu.IsOpen)
        {
            // A second click collapses the menu through the shared popup
            // transition instead of relying on WPF's immediate close path.
            ContextMenuAnimations.Close(menu);
            return;
        }

        OpenMenu(anchor);
    }

    internal void ScheduleClose()
    {
        _outsideSince ??= DateTime.UtcNow;
        if (!_closeTimer.IsEnabled)
        {
            _closeTimer.Start();
        }
    }

    internal void CancelClose()
    {
        _outsideSince = null;
        _closeTimer.Stop();
    }

    internal void NotifyMenuClosed(ContextMenu menu)
    {
        if (!ReferenceEquals(menu, _activeMenu))
        {
            return;
        }
        CancelClose();
        _activeMenu = null;
        _activeAnchor = null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _toolbar.SizeChanged -= OnToolbarSizeChanged;
        _closeTimer.Tick -= OnCloseTimerTick;
        _closeTimer.Stop();
        if (_activeMenu is { IsOpen: true } menu)
        {
            ContextMenuAnimations.Close(menu);
        }
    }

    private void OnToolbarSizeChanged(object sender, SizeChangedEventArgs e) =>
        _toolbar.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateResponsiveItems);

    private void OnCloseTimerTick(object? sender, EventArgs e)
    {
        if (_activeMenu is null || _activeAnchor is null)
        {
            CancelClose();
            return;
        }
        if (_activeMenu.IsMouseOver || _activeAnchor.IsMouseOver)
        {
            CancelClose();
            return;
        }
        if (_outsideSince is { } since && DateTime.UtcNow - since >= CloseDelay)
        {
            ContextMenuAnimations.Close(_activeMenu);
            CancelClose();
        }
    }
}
