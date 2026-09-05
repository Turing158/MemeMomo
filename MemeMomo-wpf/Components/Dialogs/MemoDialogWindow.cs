using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MemeMomo.UI.Windows;
using Window = System.Windows.Window;

namespace MemeMomo.Components.Dialogs;

public abstract class MemoDialogWindow : BorderlessWindow
{
    private IInputElement? _focusToRestore;
    private bool _resultAssigned;
    private bool _closeRequested;
    private Window? _subscribedOwner;

    protected MemoDialogWindow()
    {
        UseBuiltInTitleBar = false;
        TitleBarHeight = 0;
        ShowMinimizeButton = false;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CornerRadius = new CornerRadius(14);
        ShellBorderBrush = TryFindResource("BorderEmphasisBrush") as System.Windows.Media.Brush;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        Loaded += OnDialogLoaded;
        Closed += OnDialogClosed;
    }

    protected void CloseDialog(Action assignResult)
    {
        ArgumentNullException.ThrowIfNull(assignResult);
        if (_closeRequested)
        {
            return;
        }

        _closeRequested = true;
        assignResult();
        _resultAssigned = true;
        CloseWithTransition();
    }

    protected T ShowModal<T>(Window owner, Func<T> getResult)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(getResult);
        _focusToRestore = Keyboard.FocusedElement;
        Owner = owner;
        base.ShowDialog();
        RestoreOwnerFocus();
        return getResult();
    }

    protected void DragDialog(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_resultAssigned)
        {
            AssignCancelResult();
            _resultAssigned = true;
        }

        base.OnClosing(e);
    }

    protected abstract void AssignCancelResult();

    private void OnDialogLoaded(object sender, RoutedEventArgs e)
    {
        if (Owner is null || ReferenceEquals(_subscribedOwner, Owner))
        {
            return;
        }

        _subscribedOwner = Owner;
        _subscribedOwner.Closed += OnOwnerClosed;
    }

    private void OnOwnerClosed(object? sender, EventArgs e)
    {
        CloseDialog(AssignCancelResult);
    }

    private void OnDialogClosed(object? sender, EventArgs e)
    {
        if (_subscribedOwner is not null)
        {
            _subscribedOwner.Closed -= OnOwnerClosed;
            _subscribedOwner = null;
        }

        RestoreOwnerFocus();
        Loaded -= OnDialogLoaded;
        Closed -= OnDialogClosed;
    }

    private void RestoreOwnerFocus()
    {
        IInputElement? target = _focusToRestore;
        _focusToRestore = null;
        if (target is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Keyboard.Focus(target)));
    }
}
