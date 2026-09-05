using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using MemeMomo.Infrastructure;

namespace MemeMomo.UI.Popup;

internal static class PopupTimingPolicy
{
    internal static readonly TimeSpan HoverCloseDelay = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan PointerCheckInterval = TimeSpan.FromMilliseconds(50);
    internal static readonly TimeSpan TableEdgeMenuDelay = TimeSpan.FromMilliseconds(500);
}

internal sealed class PopupInteractionCoordinator : IDisposable
{
    private readonly System.Windows.Controls.Primitives.Popup _popup;
    private readonly DispatcherTimer _pointerTimer;
    private readonly Func<bool> _isPointerInside;
    private readonly IDisposable _timerLease;
    private IInputElement? _returnFocus;
    private DateTimeOffset? _closeAfter;
    private int _disposed;

    internal PopupInteractionCoordinator(
        System.Windows.Controls.Primitives.Popup popup,
        Func<bool> isPointerInside)
    {
        _popup = popup;
        _isPointerInside = isPointerInside;
        _pointerTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = PopupTimingPolicy.PointerCheckInterval
        };
        _pointerTimer.Tick += OnPointerTimerTick;
        _timerLease = UiResourceTracker.Acquire(UiResourceKind.DispatcherTimer);
    }

    internal bool IsOpen => _popup.IsOpen;

    internal void Open(FrameworkElement placementTarget)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _returnFocus = Keyboard.FocusedElement;
        _popup.PlacementTarget = placementTarget;
        _closeAfter = null;
        _popup.IsOpen = true;
        if (_popup.Child is IInputElement child)
        {
            Keyboard.Focus(child);
        }
    }

    internal void ScheduleClose(TimeSpan? delay = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _closeAfter = DateTimeOffset.UtcNow + (delay ?? PopupTimingPolicy.HoverCloseDelay);
        if (!_pointerTimer.IsEnabled)
        {
            _pointerTimer.Start();
        }
    }

    internal void CancelClose()
    {
        _closeAfter = null;
        _pointerTimer.Stop();
    }

    internal void Close(bool restoreFocus = true)
    {
        _closeAfter = null;
        _pointerTimer.Stop();
        _popup.IsOpen = false;
        if (restoreFocus && _returnFocus is not null)
        {
            Keyboard.Focus(_returnFocus);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Close(restoreFocus: false);
        _pointerTimer.Tick -= OnPointerTimerTick;
        _timerLease.Dispose();
        _returnFocus = null;
    }

    private void OnPointerTimerTick(object? sender, EventArgs e)
    {
        if (_closeAfter is null)
        {
            _pointerTimer.Stop();
            return;
        }

        if (_isPointerInside())
        {
            _closeAfter = null;
            _pointerTimer.Stop();
            return;
        }

        if (DateTimeOffset.UtcNow >= _closeAfter.Value)
        {
            Close();
        }
    }
}
