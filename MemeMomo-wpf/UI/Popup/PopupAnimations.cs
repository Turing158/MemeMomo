using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using WpfPopup = System.Windows.Controls.Primitives.Popup;

namespace MemeMomo.UI.Popup;

public static class PopupAnimations
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(PopupAnimations),
        new FrameworkPropertyMetadata(false, OnIsEnabledChanged));

    private static readonly ConditionalWeakTable<WpfPopup, PopupAnimationState> States = new();

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static void Open(WpfPopup popup, FrameworkElement? placementTarget = null)
    {
        ArgumentNullException.ThrowIfNull(popup);
        EnsureState(popup).Open(placementTarget);
    }

    public static void Close(WpfPopup popup, bool immediate = false, bool restoreFocus = true)
    {
        ArgumentNullException.ThrowIfNull(popup);
        if (States.TryGetValue(popup, out PopupAnimationState? state))
        {
            state.Close(immediate, restoreFocus);
        }
        else if (popup.IsOpen)
        {
            popup.IsOpen = false;
        }
    }

    internal static bool IsClosing(WpfPopup popup) =>
        States.TryGetValue(popup, out PopupAnimationState? state) && state.IsClosing;

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WpfPopup popup)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            EnsureState(popup);
        }
        else if (States.TryGetValue(popup, out PopupAnimationState? state))
        {
            state.Dispose();
            States.Remove(popup);
        }
    }

    private static PopupAnimationState EnsureState(WpfPopup popup)
    {
        if (States.TryGetValue(popup, out PopupAnimationState? existing))
        {
            return existing;
        }

        PopupAnimationState state = new(popup);
        States.Add(popup, state);
        state.Attach();
        return state;
    }

    private sealed class PopupAnimationState : IDisposable
    {
        private readonly WpfPopup _popup;
        private readonly PopupDismissalMonitor _dismissalMonitor;
        private IInputElement? _returnFocus;
        private bool _restoreFocus;
        private bool _closing;
        private int _disposed;

        internal PopupAnimationState(WpfPopup popup)
        {
            _popup = popup;
            _dismissalMonitor = new PopupDismissalMonitor(IsPointerInside, () => Close(false, restoreFocus: true));
        }

        internal bool IsClosing => _closing;

        internal void Attach()
        {
            _popup.StaysOpen = true;
            _popup.Opened += OnOpened;
            _popup.Closed += OnClosed;
        }

        internal void Open(FrameworkElement? placementTarget)
        {
            ThrowIfDisposed();
            _returnFocus = Keyboard.FocusedElement;
            if (placementTarget is not null)
            {
                _popup.PlacementTarget = placementTarget;
            }

            if (_popup.IsOpen)
            {
                _closing = false;
                _dismissalMonitor.Attach(_popup.PlacementTarget);
                if (_popup.Child is FrameworkElement openChild)
                {
                    PopupTransition.Open(openChild);
                }

                return;
            }

            if (_popup.Child is FrameworkElement child)
            {
                PopupTransition.PrepareClosed(child);
            }

            _popup.IsOpen = true;
        }

        internal void Close(bool immediate, bool restoreFocus)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_popup.IsOpen)
            {
                return;
            }

            _restoreFocus |= restoreFocus;
            FrameworkElement? child = _popup.Child as FrameworkElement;
            if (child is null || immediate)
            {
                _closing = false;
                if (child is not null)
                {
                    PopupTransition.MarkClosed(child);
                }

                _popup.IsOpen = false;
                return;
            }

            if (_closing)
            {
                return;
            }

            _closing = true;
            PopupTransition.Close(child, () =>
            {
                if (_closing && _popup.IsOpen)
                {
                    _popup.IsOpen = false;
                }
            });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _popup.Opened -= OnOpened;
            _popup.Closed -= OnClosed;
            _dismissalMonitor.Dispose();
            FrameworkElement? child = _popup.Child as FrameworkElement;
            if (child is not null)
            {
                PopupTransition.MarkClosed(child);
            }

            _popup.IsOpen = false;
            if (child is not null)
            {
                PopupTransition.Detach(child);
            }

            _returnFocus = null;
        }

        private bool IsPointerInside() => _popup.Child?.IsMouseOver == true;

        private void OnOpened(object? sender, EventArgs e)
        {
            _closing = false;
            _restoreFocus = false;
            _dismissalMonitor.Attach(_popup.PlacementTarget);
            if (_popup.Child is FrameworkElement child)
            {
                PopupTransition.Open(child);
            }
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _closing = false;
            _dismissalMonitor.Detach();
            if (_popup.Child is FrameworkElement child)
            {
                PopupTransition.MarkClosed(child);
            }

            if (_restoreFocus && _returnFocus is not null)
            {
                Keyboard.Focus(_returnFocus);
            }

            _restoreFocus = false;
            _returnFocus = null;
        }

        private void ThrowIfDisposed() =>
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
