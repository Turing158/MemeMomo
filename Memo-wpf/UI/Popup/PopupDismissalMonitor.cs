using System.Windows;
using System.Windows.Input;
using Memo.Infrastructure;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace Memo.UI.Popup;

internal sealed class PopupDismissalMonitor : IDisposable
{
    private readonly Func<bool> _isPointerInside;
    private readonly Action _requestClose;
    private Window? _owner;
    private IDisposable? _subscriptionLease;
    private bool _attached;
    private int _disposed;

    internal PopupDismissalMonitor(Func<bool> isPointerInside, Action requestClose)
    {
        _isPointerInside = isPointerInside;
        _requestClose = requestClose;
    }

    internal void Attach(DependencyObject? placementTarget)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Window? owner = placementTarget is null ? null : Window.GetWindow(placementTarget);
        if (_attached && ReferenceEquals(owner, _owner))
        {
            return;
        }

        Detach();
        _owner = owner;
        if (_owner is not null)
        {
            _owner.Deactivated += OnOwnerDeactivated;
        }

        InputManager.Current.PreProcessInput += OnPreProcessInput;
        _subscriptionLease = UiResourceTracker.Acquire(UiResourceKind.GlobalEventSubscription);
        _attached = true;
    }

    internal void Detach()
    {
        if (!_attached)
        {
            return;
        }

        InputManager.Current.PreProcessInput -= OnPreProcessInput;
        if (_owner is not null)
        {
            _owner.Deactivated -= OnOwnerDeactivated;
            _owner = null;
        }

        _subscriptionLease?.Dispose();
        _subscriptionLease = null;
        _attached = false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Detach();
        }
    }

    private void OnOwnerDeactivated(object? sender, EventArgs e) => _requestClose();

    private void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        switch (e.StagingItem.Input)
        {
            case MouseButtonEventArgs mouse when
                mouse.RoutedEvent == Mouse.PreviewMouseDownEvent && !_isPointerInside():
                _requestClose();
                break;
            case KeyEventArgs key when
                key.RoutedEvent == Keyboard.PreviewKeyDownEvent && key.Key == Key.Escape:
                key.Handled = true;
                _requestClose();
                break;
        }
    }
}
