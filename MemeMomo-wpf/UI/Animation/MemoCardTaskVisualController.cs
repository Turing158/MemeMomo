using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MemeMomo.Components;
using MemeMomo.Services;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Controls.Control;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace MemeMomo.UI.Animation;

/// <summary>
/// Owns only the normal card's task surfaces. Card hover/press, list movement and
/// button feedback keep their existing transform owners. No business state is
/// inferred from the (possibly still outgoing) label or interpolated icon.
/// </summary>
internal sealed class MemoCardTaskVisualController : IDisposable
{
    internal const double IconWidth = 26;
    internal const double RevealDistance = 28;
    private readonly MemoTaskStatusSelector _selector;
    private readonly FrameworkElement _card;
    private readonly Grid _actions;
    private readonly Grid _row;
    private readonly Border _accent;
    private readonly Button _delete;
    private readonly Button _quick;
    private readonly TextBlock _text;
    private readonly SolidColorBrush _accentBrush;
    private readonly FrameAnimation _position;
    private readonly FrameAnimation _width;
    private readonly FrameAnimation _label;
    private readonly FrameAnimation _presence;
    private readonly FrameAnimation _color;
    private readonly TaskStatusIconMorph _morph;
    private Snapshot? _target;
    private bool _taskLayout;
    private bool _revealed;
    private bool _expanded;
    private bool _menuOpen;
    private bool _pressed;
    private bool _textExiting;
    private bool _disposed;
    private bool _resumeLayout;
    private Rect _hoverBridge = Rect.Empty;
    private DispatcherOperation? _pointerUpdate;
    private double _maxWidth = IconWidth;
    private double _widthTarget = IconWidth;

    private sealed record Snapshot(bool IsTask, string? StatusId, string Label, string Icon, Color Color, bool Closed);

    internal static MemoCardTaskVisualController? Attach(MemoTaskStatusSelector selector, Func<IAnimationFrameSource>? frames = null)
    {
        if (selector.Parent is not Grid { Name: "MemoActions" } actions || actions.Parent is not Grid row) return null;
        DependencyObject? ancestor = actions;
        while (ancestor is not null && ancestor is not FrameworkElement { Name: "MemoCard" }) ancestor = VisualTreeHelper.GetParent(ancestor);
        if (ancestor is not FrameworkElement card) return null;
        var accent = Descendants(card).OfType<Border>().First(element => element.Name == "AccentBar");
        var delete = actions.Children.OfType<Button>().First(element => element.Name == "DeleteButton");
        return new(selector, card, actions, row, accent, delete, frames);
    }

    private MemoCardTaskVisualController(MemoTaskStatusSelector selector, FrameworkElement card, Grid actions,
        Grid row, Border accent, Button delete, Func<IAnimationFrameSource>? frames)
    {
        _selector = selector;
        _card = card;
        _actions = actions;
        _row = row;
        _accent = accent;
        _delete = delete;
        _quick = selector.QuickButton;
        _text = selector.QuickText;
        _accentBrush = new(((SolidColorBrush)accent.Background).Color);
        accent.Background = _accentBrush;
        _position = new(frames?.Invoke());
        _width = new(frames?.Invoke());
        _label = new(frames?.Invoke());
        _presence = new(frames?.Invoke());
        _color = new(frames?.Invoke());
        _morph = new(selector.QuickIcon, frames?.Invoke());
        card.MouseEnter += OnPointer;
        card.MouseLeave += OnPointer;
        card.MouseMove += OnPointer;
        card.IsKeyboardFocusWithinChanged += OnFocus;
        _quick.IsKeyboardFocusWithinChanged += OnFocus;
        _quick.PreviewMouseLeftButtonDown += OnPress;
        _quick.LostMouseCapture += OnRelease;
        row.SizeChanged += OnSizeChanged;
        MotionPreferences.Changed += OnMotionChanged;
        Refresh();
    }

    internal bool IsRunning => _position.IsRunning || _width.IsRunning || _label.IsRunning || _presence.IsRunning || _color.IsRunning || _morph.IsRunning;
    internal double Offset => _selector.QuickOffset.X;
    internal bool Expanded => _expanded;
    internal double ReservedWidth => _actions.ActualWidth;
    internal long IconGeneration => _morph.Generation;

    internal void Refresh()
    {
        if (_disposed) return;
        var memo = _selector.Memo;
        bool task = memo?.IsTask == true;
        var status = _selector.Service?.Catalog.Find(memo?.TaskStatusId);
        var next = new Snapshot(task, memo?.TaskStatusId,
            status is null ? LocalizationService.Get("状态已缺失") : _selector.Service!.Catalog.DisplayName(status),
            TaskStatusIconMorph.NormalizeKey(status?.IconKey),
            task ? ((SolidColorBrush)TaskStatusAppearance.Foreground(status?.ColorKey)).Color : NormalColor(),
            memo?.IsTaskClosed == true);
        var previous = _target;
        _target = next;
        string description = next.Label + " · " + LocalizationService.Get(next.Closed ? "重新打开" : "完成任务");
        _quick.ToolTip = description;
        AutomationProperties.SetName(_quick, description);
        _selector.StatusButton.Visibility = Visibility.Collapsed;

        if (previous is null || previous.Color != next.Color)
        {
            Color from = _accentBrush.Color;
            Animate(_color, previous is not null && (previous.IsTask || task), 190, MotionEasing.CubicEaseInOut,
                t => _accentBrush.Color = Blend(from, next.Color, t));
        }
        if (task)
        {
            _selector.QuickIcon.Foreground = TaskStatusAppearance.Foreground(status?.ColorKey);
            _morph.SetIcon(next.Icon, previous?.IsTask == true);
        }
        if (previous is null || previous.IsTask != next.IsTask)
        {
            if (!task && _quick.IsMouseCaptured) _quick.ReleaseMouseCapture();
            if (!task && _quick.IsKeyboardFocusWithin) _card.Focus();
            if (task) UseTaskLayout();
            _quick.IsHitTestVisible = task;
            _quick.IsTabStop = task;
            _selector.QuickPresence.Visibility = Visibility.Visible;
            _quick.Visibility = Visibility.Visible;
            if (previous is null)
            {
                _selector.QuickPresence.Opacity = task ? 1 : 0;
                _selector.QuickScale.ScaleX = _selector.QuickScale.ScaleY = task ? 1 : 0.8;
            }
            double opacity = _selector.QuickPresence.Opacity;
            double scale = _selector.QuickScale.ScaleX;
            Animate(_presence, previous is not null, task ? 190 : 150, task ? MotionEasing.CubicEaseOut : MotionEasing.CubicEaseIn, t =>
            {
                _selector.QuickPresence.Opacity = Lerp(opacity, task ? 1 : 0, t);
                _selector.QuickScale.ScaleX = _selector.QuickScale.ScaleY = Lerp(scale, task ? 1 : 0.8, t);
            }, () => { if (_target?.IsTask != true) RestoreNoteLayout(); });
        }

        if (previous is null)
        {
            _text.Text = next.Label;
            UpdateInteraction(animate: false);
        }
        else
        {
            if (next.Label != previous.Label)
            {
                if (_expanded && task) ChangeLabel();
                else if (_text.Opacity == 0) _text.Text = next.Label;
            }
            UpdateInteraction();
        }
        UpdateAccentOpacity();
    }

    internal void UpdateInteraction(bool animate = true)
    {
        if (_disposed) return;
        UpdateAccentOpacity();
        if ((_menuOpen || _pressed) && _target?.IsTask == true) return;
        bool cardHover = _card.IsMouseOver;
        Point pointer = Mouse.GetPosition(_actions);
        bool buttonHover = _quick.IsMouseOver ||
            (!_hoverBridge.IsEmpty && (_position.IsRunning || _width.IsRunning) && _hoverBridge.Contains(pointer));
        SetInteraction(cardHover || _card.IsKeyboardFocusWithin, buttonHover || _quick.IsKeyboardFocusWithin, animate);
    }

    // Input policy is separate from WPF's moving hit target, also allowing exact
    // frame-source tests of the same transitions used by mouse and keyboard.
    internal void SetInteraction(bool cardActive, bool buttonActive, bool animate = true)
    {
        if (_disposed || ((_menuOpen || _pressed) && _target?.IsTask == true)) return;
        bool task = _target?.IsTask == true;
        bool reveal = task && (cardActive || buttonActive);
        bool expand = task && buttonActive;
        bool changed = reveal != _revealed || expand != _expanded;
        if (changed) ExtendHoverBridge();
        if (reveal != _revealed || !animate || _resumeLayout)
        {
            _revealed = reveal;
            double from = Offset;
            double opacity = _delete.Opacity;
            Animate(_position, animate, 150, MotionEasing.CubicEaseOut, t =>
            {
                _selector.QuickOffset.X = Lerp(from, reveal ? -RevealDistance : 0, t);
                if (_taskLayout) _delete.Opacity = Lerp(opacity, reveal ? 1 : 0, t);
                UpdateDeleteHit();
            }, SchedulePointerUpdate);
        }
        if (expand != _expanded || !animate || _resumeLayout)
        {
            _expanded = expand;
            SetWidth(expand ? LabelWidth(_text.Opacity > 0 ? _text.Text : _target!.Label) : IconWidth, animate, expand ? 190 : 150);
            ShowLabel(expand, animate);
        }
        if (!_taskLayout)
            InteractionState.SetIsRevealed(_delete, _card.IsMouseOver || InteractionState.GetIsEditing(_card));
        UpdateDeleteHit();
        _resumeLayout = false;
    }

    internal void SetMenuOpen(bool open)
    {
        if (_disposed) return;
        _menuOpen = open;
        if (!open) SchedulePointerUpdate();
    }

    private void ShowLabel(bool show, bool animate)
    {
        _textExiting = false;
        if (show && _text.Opacity > 0 && _text.Text != _target!.Label)
        {
            ChangeLabel();
            return;
        }
        double opacity = _text.Opacity;
        double offset = _selector.QuickTextOffset.X;
        if (show && opacity == 0) _text.Text = _target!.Label;
        Animate(_label, animate, show ? 180 : 90, MotionEasing.Linear, progress =>
        {
            double t = show ? Math.Clamp((progress * 180 - 30) / 150, 0, 1) : progress;
            t = FrameAnimationChannel.Sample(MotionEasing.CubicEaseOut, t);
            _text.Opacity = Lerp(opacity, show ? 1 : 0, t);
            _selector.QuickTextOffset.X = Lerp(offset, show ? 0 : -6, t);
            if (!show && progress >= 1) _text.Text = _target!.Label;
        });
    }

    private void ChangeLabel()
    {
        // While exiting, the swap below reads the latest committed target. Do not
        // restart the 60ms exit for each notification or enqueue obsolete names.
        if (_textExiting) return;
        _textExiting = true;
        bool swapped = false;
        double opacity = _text.Opacity;
        double offset = _selector.QuickTextOffset.X;
        _label.Start(TimeSpan.FromMilliseconds(150), MotionEasing.Linear, progress =>
        {
            if (progress < 0.4)
            {
                double t = FrameAnimationChannel.Sample(MotionEasing.CubicEaseOut, progress / 0.4);
                _text.Opacity = Lerp(opacity, 0, t);
                _selector.QuickTextOffset.X = Lerp(offset, -6, t);
                return;
            }
            if (!swapped)
            {
                _text.Opacity = 0;
                _text.Text = _target!.Label;
                swapped = true;
                _textExiting = false;
                SetWidth(LabelWidth(_text.Text), true, 150);
            }
            double enter = FrameAnimationChannel.Sample(MotionEasing.CubicEaseOut, (progress - 0.4) / 0.6);
            _text.Opacity = enter;
            _selector.QuickTextOffset.X = Lerp(-6, 0, enter);
        }, () => _textExiting = false);
    }

    private void SetWidth(double target, bool animate, int milliseconds)
    {
        target = Math.Clamp(target, IconWidth, _maxWidth);
        if (animate && Math.Abs(target - _widthTarget) < 0.01) return;
        _widthTarget = target;
        double from = _quick.Width;
        Animate(_width, animate, milliseconds, MotionEasing.CubicEaseOut,
            t => _quick.Width = Lerp(from, target, t), SchedulePointerUpdate);
    }

    private double LabelWidth(string label)
    {
        var text = new FormattedText(label, CultureInfo.CurrentUICulture, _text.FlowDirection,
            new Typeface(_text.FontFamily, _text.FontStyle, _text.FontWeight, _text.FontStretch),
            _text.FontSize, _text.Foreground, VisualTreeHelper.GetDpi(_text).PixelsPerDip);
        return Math.Clamp(Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 32, IconWidth, _maxWidth);
    }

    private void UseTaskLayout()
    {
        _taskLayout = true;
        _selector.Margin = new Thickness(0);
        _selector.MaxWidth = double.PositiveInfinity;
        _selector.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _actions.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        Grid.SetColumnSpan(_selector, 2);
        _selector.QuickPosition.Margin = new Thickness(0, -2, 0, -2);
        _quick.Margin = new Thickness(0);
        _quick.Padding = new Thickness(5, 0, 5, 0);
        _quick.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _quick.Cursor = Cursors.Hand;
        InteractionAnimations.SetHoverScale(_quick, 1);
        InteractionAnimations.SetPressedScale(_quick, 1);
        _selector.QuickTextClip.Visibility = Visibility.Visible;
        _delete.Opacity = _revealed ? 1 : 0;
        ResizeReservation();
    }

    private void RestoreNoteLayout()
    {
        _taskLayout = false;
        _selector.QuickPresence.Visibility = Visibility.Collapsed;
        _quick.Visibility = Visibility.Collapsed;
        _selector.Margin = new Thickness(0, 0, 4, 0);
        _selector.MaxWidth = 145;
        _selector.ClearValue(Control.HorizontalContentAlignmentProperty);
        _actions.ColumnDefinitions[0].Width = GridLength.Auto;
        Grid.SetColumnSpan(_selector, 1);
        _actions.ClearValue(FrameworkElement.WidthProperty);
        _delete.ClearValue(UIElement.OpacityProperty);
        _delete.ClearValue(UIElement.IsHitTestVisibleProperty);
        _delete.ClearValue(Control.IsTabStopProperty);
        InteractionState.SetIsRevealed(_delete, _card.IsMouseOver || InteractionState.GetIsEditing(_card));
    }

    private void ResizeReservation()
    {
        if (!_taskLayout) return;
        // Measured title-row width already excludes card padding, list margins and
        // scrollbar. Keep at least 80 DIP for title plus its existing 8 DIP margin.
        _maxWidth = Math.Clamp(_row.ActualWidth - 80 - 8 - RevealDistance, IconWidth, 128);
        _actions.Width = _maxWidth + RevealDistance;
        SetWidth(_expanded ? LabelWidth(_text.Text) : IconWidth, true, 150);
    }

    private void UpdateDeleteHit()
    {
        if (!_taskLayout) return;
        bool available = _revealed && Offset <= -_delete.Width && !_pressed && _selector.Memo?.IsTask == true;
        _delete.IsHitTestVisible = available;
        _delete.IsTabStop = available;
    }

    private void UpdateAccentOpacity() => _accent.Opacity = _target?.IsTask == true || InteractionState.GetIsEditing(_card) ? 1 : 0.5;
    private Color NormalColor() => ((SolidColorBrush)_card.FindResource("AccentPrimaryBrush")).Color;

    private void ExtendHoverBridge()
    {
        if (!_quick.IsVisible || _quick.ActualWidth <= 0) return;
        // Only hover continuity uses this range. WPF still hits the visible button
        // itself; empty reserved space never turns into an invisible click target.
        _hoverBridge = _quick.TransformToAncestor(_actions).TransformBounds(new Rect(_quick.RenderSize));
    }

    private void SchedulePointerUpdate()
    {
        if (_disposed || _pointerUpdate is not null) return;
        _pointerUpdate = _card.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            _pointerUpdate = null;
            if (_disposed) return;
            if (!_position.IsRunning && !_width.IsRunning) _hoverBridge = Rect.Empty;
            UpdateInteraction();
        }));
    }

    private void OnPointer(object sender, MouseEventArgs e) => UpdateInteraction();
    private void OnFocus(object sender, DependencyPropertyChangedEventArgs e) => UpdateInteraction();
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ResizeReservation();
    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _pressed = true;
        _position.Cancel();
        _width.Cancel();
        UpdateDeleteHit();
    }
    private void OnRelease(object sender, MouseEventArgs e)
    {
        _pressed = false;
        // Force interrupted layout tracks to resume from their frozen values.
        _resumeLayout = true;
        _widthTarget = double.NaN;
        SchedulePointerUpdate();
    }

    private void OnMotionChanged(object? sender, EventArgs e)
    {
        if (_disposed || MotionPreferences.AnimationsEnabled) return;
        bool menu = _menuOpen;
        bool pressed = _pressed;
        _menuOpen = _pressed = false;
        _text.Text = _target!.Label;
        SetInteraction(_revealed, _expanded, animate: false);
        _menuOpen = menu;
        _pressed = pressed;
        UpdateDeleteHit();
    }

    private static void Animate(FrameAnimation animation, bool animate, int milliseconds, MotionEasing easing, Action<double> update, Action? completed = null) =>
        animation.Start(TimeSpan.FromMilliseconds(animate ? milliseconds : 0), easing, update, completed);
    private static double Lerp(double from, double to, double t) => from + (to - from) * t;
    private static Color Blend(Color from, Color to, double t) => Color.FromArgb(
        (byte)Math.Round(Lerp(from.A, to.A, t)), (byte)Math.Round(Lerp(from.R, to.R, t)),
        (byte)Math.Round(Lerp(from.G, to.G, t)), (byte)Math.Round(Lerp(from.B, to.B, t)));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pointerUpdate?.Abort();
        _pointerUpdate = null;
        _position.Dispose(); _width.Dispose(); _label.Dispose(); _presence.Dispose(); _color.Dispose(); _morph.Dispose();
        _card.MouseEnter -= OnPointer;
        _card.MouseLeave -= OnPointer;
        _card.MouseMove -= OnPointer;
        _card.IsKeyboardFocusWithinChanged -= OnFocus;
        _quick.IsKeyboardFocusWithinChanged -= OnFocus;
        _quick.PreviewMouseLeftButtonDown -= OnPress;
        _quick.LostMouseCapture -= OnRelease;
        _row.SizeChanged -= OnSizeChanged;
        MotionPreferences.Changed -= OnMotionChanged;
        RestoreNoteLayout();
        _accent.SetResourceReference(Border.BackgroundProperty, "AccentPrimaryBrush");
        _selector.QuickOffset.X = 0;
        _selector.QuickPosition.Margin = new Thickness(0);
        _selector.QuickPresence.Visibility = Visibility.Visible;
        _selector.QuickPresence.Opacity = 1;
        _selector.QuickScale.ScaleX = _selector.QuickScale.ScaleY = 1;
        _quick.Width = IconWidth;
        _quick.Margin = new Thickness(3, 0, 0, 0);
        _quick.Padding = new Thickness(0);
        _quick.ClearValue(Control.HorizontalContentAlignmentProperty);
        _quick.ClearValue(FrameworkElement.CursorProperty);
        _quick.ClearValue(InteractionAnimations.HoverScaleProperty);
        _quick.ClearValue(InteractionAnimations.PressedScaleProperty);
        _quick.ClearValue(UIElement.IsHitTestVisibleProperty);
        _quick.ClearValue(Control.IsTabStopProperty);
        _selector.QuickIcon.ClearValue(TaskStatusIcon.ForegroundProperty);
        _selector.QuickTextClip.Visibility = Visibility.Collapsed;
    }
}
