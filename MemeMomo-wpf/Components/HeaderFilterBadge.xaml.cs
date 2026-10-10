using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MemeMomo.UI;
using MemeMomo.UI.Animation;
using MemeMomo.UI.Popup;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace MemeMomo.Components;

public partial class HeaderFilterBadge : UserControl
{
    private bool _pointerOver;
    private bool _keyboardInteraction;
    private ContextMenu? _menu;
    private Window? _owner;

    public event RoutedEventHandler? Click;
    internal Button MenuTarget => BadgeButton;
    internal bool IsExpanded { get; private set; }
    internal string Summary => SummaryText.Text;
    internal int Count { get; private set; }
    internal bool IsFiltered { get; private set; }

    public HeaderFilterBadge() => InitializeComponent();

    internal void Update(int count, string summary, bool filtered, string description)
    {
        Count = count;
        IsFiltered = filtered;
        CountText.Text = count.ToString(CultureInfo.CurrentCulture);
        SummaryText.Text = summary;
        BadgeButton.ToolTip = description;
        AutomationProperties.SetName(BadgeButton, description);
        UpdateExpansion(animate: false);
    }

    internal void OpenMenu(ContextMenu menu)
    {
        CloseMenu();
        _menu = menu;
        menu.Closed += OnMenuClosed;
        UpdateExpansion();
        TaskContextMenu.Open(menu);
    }

    internal void CloseMenu()
    {
        if (_menu is not { } menu) return;
        ContextMenuAnimations.Close(menu, immediate: true);
        // A quick second click can close the popup before its deferred Opened event.
        // This menu is disposable; remove that pending event's subscriptions too.
        ContextMenuAnimations.SetIsEnabled(menu, false);
    }

    private void OnClick(object sender, RoutedEventArgs e) => Click?.Invoke(this, e);
    private void OnMouseEnter(object sender, MouseEventArgs e) { _pointerOver = true; UpdateExpansion(); }
    private void OnMouseLeave(object sender, MouseEventArgs e) { _pointerOver = false; UpdateExpansion(); }
    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e) => _keyboardInteraction = false;
    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e) => e.Handled = true;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        _keyboardInteraction = true;
        UpdateExpansion();
        if (e.Key is Key.Down or Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
        {
            e.Handled = true;
            Click?.Invoke(this, new RoutedEventArgs(Button.ClickEvent));
        }
    }

    private void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Returning focus from a pointer-opened menu must not pin the badge open.
        if (_menu is null && Mouse.LeftButton != MouseButtonState.Pressed) _keyboardInteraction = true;
        UpdateExpansion();
    }

    private void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateExpansion();

    private void OnMenuClosed(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        menu.Closed -= OnMenuClosed;
        ContextMenuAnimations.SetIsEnabled(menu, false);
        if (!ReferenceEquals(_menu, menu)) return;
        _menu = null;
        _pointerOver = BadgeButton.IsMouseOver;
        UpdateExpansion();
    }

    private void UpdateExpansion(bool animate = true)
    {
        if (BadgeButton is null) return;
        bool expanded = IsVisible && IsEnabled && (_pointerOver || _menu is not null || (_keyboardInteraction && IsKeyboardFocusWithin));
        ActiveFilterMark.Visibility = IsFiltered ? Visibility.Visible : Visibility.Collapsed;
        double countWidth = TextWidth(CountText);
        CountText.Width = countWidth;
        double desiredTail = expanded ? 13 + TextWidth(SummaryText) : IsFiltered ? 14 : 0;
        double desiredWidth = Math.Max(24, 10 + countWidth + desiredTail);
        double available = ActualWidth > 0 ? ActualWidth : desiredWidth;
        double width = Math.Min(available, Math.Min(280, desiredWidth));
        double tailWidth = Math.Min(desiredTail, Math.Max(0, width - 10 - countWidth));
        double opacity = expanded ? 1 : 0;
        double markOpacity = IsFiltered && !expanded ? 1 : 0;
        IsExpanded = expanded;
        double fromWidth = BadgeButton.Width;
        double fromTail = BadgeTail.Width;
        double fromOpacity = Details.Opacity;
        double fromMarkOpacity = ActiveFilterMark.Opacity;
        MotionAnimations.Cancel(this);
        void Apply(double progress)
        {
            BadgeButton.Width = fromWidth + (width - fromWidth) * progress;
            // The transparent tail must release its layout space too, so the
            // visible content remains centered even inside the minimum-size pill.
            BadgeTail.Width = Math.Min(Math.Max(0, BadgeButton.Width - 10 - countWidth),
                fromTail + (tailWidth - fromTail) * progress);
            Details.Opacity = fromOpacity + (opacity - fromOpacity) * progress;
            ActiveFilterMark.Opacity = fromMarkOpacity + (markOpacity - fromMarkOpacity) * progress;
        }
        bool unchanged = Math.Abs(fromWidth - width) < 0.1 && Math.Abs(fromTail - tailWidth) < 0.1
            && Math.Abs(fromOpacity - opacity) < 0.001 && Math.Abs(fromMarkOpacity - markOpacity) < 0.001;
        if (!animate || !IsLoaded || !MotionPreferences.AnimationsEnabled || unchanged)
            Apply(1);
        else
            MotionAnimations.Start(this, TimeSpan.FromMilliseconds(200), MotionEasing.CubicEaseOut, Apply);
    }

    private static double TextWidth(TextBlock text) => Math.Ceiling(new FormattedText(text.Text,
        CultureInfo.CurrentUICulture, text.FlowDirection,
        new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize,
        text.Foreground, VisualTreeHelper.GetDpi(text).PixelsPerDip).WidthIncludingTrailingWhitespace);

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateExpansion(animate: false);
    private void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsEnabled) Reset();
        else UpdateExpansion(animate: false);
    }
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) Reset();
        else UpdateExpansion(animate: false);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _owner = Window.GetWindow(this);
        if (_owner is not null) _owner.Deactivated += OnOwnerDeactivated;
        UpdateExpansion(animate: false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_owner is not null) _owner.Deactivated -= OnOwnerDeactivated;
        _owner = null;
        Reset();
    }

    private void OnOwnerDeactivated(object? sender, EventArgs e) => Reset();
    private void Reset()
    {
        CloseMenu();
        _pointerOver = _keyboardInteraction = false;
        UpdateExpansion(animate: false);
    }
}
