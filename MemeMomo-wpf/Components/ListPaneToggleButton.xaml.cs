using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using MemeMomo.UI;
using MemeMomo.UI.Animation;
using MemeMomo.UI.Text;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace MemeMomo.Components;

public partial class ListPaneToggleButton : Button, IDisposable
{
    private readonly FrameAnimation _animation;
    private readonly FrameworkElement[] _icons;
    private Window? _owner;
    private bool _collapsed;
    private bool _pointerOver;
    private bool _keyboardInteraction;
    private bool _disposed;
    private int _target;

    public ListPaneToggleButton() : this(null) { }

    internal ListPaneToggleButton(IAnimationFrameSource? frames)
    {
        InitializeComponent();
        _icons = [AppIcon, CollapseIcon, ExpandIcon];
        _animation = new FrameAnimation(frames);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnVisibilityChanged;
    }

    internal void UpdateState(bool canToggle, bool collapsed)
    {
        if (_disposed) return;
        _collapsed = collapsed;
        IsEnabled = IsHitTestVisible = Focusable = IsTabStop = canToggle;
        string description = canToggle ? (collapsed ? "展开左侧列表" : "收起左侧列表") : "MemeMomo";
        LocalizeExtension.Set(this, ToolTipProperty, description);
        LocalizeExtension.Set(this, AutomationProperties.NameProperty, description);
        UpdateIcon();
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        _pointerOver = true;
        UpdateIcon();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _pointerOver = false;
        UpdateIcon();
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _keyboardInteraction = false;
        base.OnPreviewMouseLeftButtonDown(e);
        UpdateIcon();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        _keyboardInteraction = true;
        UpdateIcon();
        base.OnPreviewKeyDown(e);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (Mouse.LeftButton != MouseButtonState.Pressed) _keyboardInteraction = true;
        UpdateIcon();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        UpdateIcon();
    }

    private void UpdateIcon(bool animate = true)
    {
        if (_disposed || _icons is null) return;
        bool showAction = IsEnabled && IsVisible && (_pointerOver || (_keyboardInteraction && IsKeyboardFocusWithin));
        int target = showAction ? (_collapsed ? 2 : 1) : 0;
        if (animate && target == _target) return;
        _target = target;
        double[] from = _icons.Select(icon => icon.Opacity).ToArray();
        _animation.Cancel();
        void Apply(double progress)
        {
            for (int i = 0; i < _icons.Length; i++)
            {
                double opacity = from[i] + ((i == target ? 1 : 0) - from[i]) * progress;
                _icons[i].Opacity = opacity;
                var scale = (ScaleTransform)_icons[i].RenderTransform;
                scale.ScaleX = scale.ScaleY = 0.76 + 0.24 * opacity;
            }
        }
        if (!animate || !IsLoaded || !IsVisible) Apply(1);
        else _animation.Start(TimeSpan.FromMilliseconds(180), MotionEasing.CubicEaseOut, Apply);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DetachOwner();
        _owner = Window.GetWindow(this);
        if (_owner is not null)
        {
            _owner.Deactivated += OnOwnerDeactivated;
            _owner.Activated += OnOwnerActivated;
        }
        _pointerOver = IsMouseOver;
        UpdateIcon(animate: false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachOwner();
        Reset();
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) Reset();
        else { _pointerOver = IsMouseOver; UpdateIcon(animate: false); }
    }

    private void OnOwnerDeactivated(object? sender, EventArgs e) => Reset();
    private void OnOwnerActivated(object? sender, EventArgs e)
    {
        _pointerOver = IsMouseOver;
        UpdateIcon();
    }

    private void Reset()
    {
        _pointerOver = _keyboardInteraction = false;
        UpdateIcon(animate: false);
    }

    private void DetachOwner()
    {
        if (_owner is null) return;
        _owner.Deactivated -= OnOwnerDeactivated;
        _owner.Activated -= OnOwnerActivated;
        _owner = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Reset();
        _disposed = true;
        DetachOwner();
        _animation.Dispose();
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        IsVisibleChanged -= OnVisibilityChanged;
    }
}
