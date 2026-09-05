using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace MemeMomo.UI.Popup;

public static class ContextMenuAnimations
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(ContextMenuAnimations),
        new FrameworkPropertyMetadata(false, OnIsEnabledChanged));

    private static readonly ConditionalWeakTable<ContextMenu, ContextMenuAnimationState> States = new();

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static void Open(ContextMenu menu)
    {
        ArgumentNullException.ThrowIfNull(menu);
        EnsureState(menu).Open();
    }

    public static void Close(ContextMenu menu, bool immediate = false)
    {
        ArgumentNullException.ThrowIfNull(menu);
        if (States.TryGetValue(menu, out ContextMenuAnimationState? state))
        {
            state.Close(immediate);
        }
        else if (menu.IsOpen)
        {
            menu.IsOpen = false;
        }
    }

    internal static bool IsClosing(ContextMenu menu) =>
        States.TryGetValue(menu, out ContextMenuAnimationState? state) && state.IsClosing;

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ContextMenu menu)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            EnsureState(menu);
        }
        else if (States.TryGetValue(menu, out ContextMenuAnimationState? state))
        {
            state.Dispose();
            States.Remove(menu);
        }
    }

    private static ContextMenuAnimationState EnsureState(ContextMenu menu)
    {
        if (States.TryGetValue(menu, out ContextMenuAnimationState? existing))
        {
            return existing;
        }

        ContextMenuAnimationState state = new(menu);
        States.Add(menu, state);
        state.Attach();
        return state;
    }

    private sealed class ContextMenuAnimationState : IDisposable
    {
        private readonly ContextMenu _menu;
        private readonly PopupDismissalMonitor _dismissalMonitor;
        private bool _closing;
        private int _disposed;

        internal ContextMenuAnimationState(ContextMenu menu)
        {
            _menu = menu;
            _dismissalMonitor = new PopupDismissalMonitor(() => _menu.IsMouseOver, () => Close(immediate: false));
        }

        internal bool IsClosing => _closing;

        internal void Attach()
        {
            _menu.StaysOpen = true;
            _menu.Opened += OnOpened;
            _menu.Closed += OnClosed;
            _menu.AddHandler(MenuItem.ClickEvent, new RoutedEventHandler(OnMenuItemClick), true);
        }

        internal void Open()
        {
            ThrowIfDisposed();
            if (_menu.IsOpen)
            {
                _closing = false;
                _dismissalMonitor.Attach(_menu.PlacementTarget);
                PopupTransition.Open(_menu);
                return;
            }

            PopupTransition.PrepareClosed(_menu);
            _menu.IsOpen = true;
        }

        internal void Close(bool immediate)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_menu.IsOpen)
            {
                return;
            }

            if (immediate)
            {
                _closing = false;
                PopupTransition.MarkClosed(_menu);
                _menu.IsOpen = false;
                return;
            }

            if (_closing)
            {
                return;
            }

            _closing = true;
            PopupTransition.Close(_menu, () =>
            {
                if (_closing && _menu.IsOpen)
                {
                    _menu.IsOpen = false;
                }
            });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _menu.Opened -= OnOpened;
            _menu.Closed -= OnClosed;
            _menu.RemoveHandler(MenuItem.ClickEvent, new RoutedEventHandler(OnMenuItemClick));
            _dismissalMonitor.Dispose();
            if (_menu.IsOpen)
            {
                PopupTransition.MarkClosed(_menu);
                _menu.IsOpen = false;
            }

            PopupTransition.Detach(_menu);
        }

        private void OnOpened(object sender, RoutedEventArgs e)
        {
            _closing = false;
            _dismissalMonitor.Attach(_menu.PlacementTarget);
            PopupTransition.Open(_menu);
        }

        private void OnClosed(object sender, RoutedEventArgs e)
        {
            _closing = false;
            _dismissalMonitor.Detach();
            PopupTransition.MarkClosed(_menu);
        }

        private void OnMenuItemClick(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is MenuItem { IsEnabled: true, HasItems: false })
            {
                Close(immediate: false);
            }
        }

        private void ThrowIfDisposed() =>
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
