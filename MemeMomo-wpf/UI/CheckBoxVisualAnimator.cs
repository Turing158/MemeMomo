using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MemeMomo.UI.Animation;
using CheckBox = System.Windows.Controls.CheckBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using KeyEventHandler = System.Windows.Input.KeyEventHandler;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using MouseEventHandler = System.Windows.Input.MouseEventHandler;

namespace MemeMomo.UI;

internal sealed class CheckBoxVisualAnimator
{
    private const double HiddenSelectionScale = 0.65;
    private const double PressPreviewScale = 0.5;

    private readonly CheckBox _checkBox;
    private readonly string _fillName;
    private readonly string _checkMarkName;
    private readonly string? _indeterminateMarkName;
    private readonly string _pressPreviewName;
    private readonly object _selectionChannel = new();
    private readonly object _previewChannel = new();
    private readonly MouseButtonEventHandler _mouseDownHandler;
    private readonly MouseButtonEventHandler _mouseUpHandler;
    private readonly MouseEventHandler _lostCaptureHandler;
    private readonly MouseEventHandler _mouseLeaveHandler;
    private readonly KeyEventHandler _keyDownHandler;
    private readonly KeyEventHandler _keyUpHandler;

    private FrameworkElement? _fill;
    private FrameworkElement? _checkMark;
    private FrameworkElement? _indeterminateMark;
    private FrameworkElement? _pressPreview;
    private bool _inputAttached;
    private bool _pointerPressed;
    private bool _keyboardPressed;

    internal CheckBoxVisualAnimator(
        CheckBox checkBox,
        string fillName,
        string checkMarkName,
        string pressPreviewName,
        string? indeterminateMarkName = null)
    {
        _checkBox = checkBox;
        _fillName = fillName;
        _checkMarkName = checkMarkName;
        _pressPreviewName = pressPreviewName;
        _indeterminateMarkName = indeterminateMarkName;
        _mouseDownHandler = OnPreviewMouseDown;
        _mouseUpHandler = OnPreviewMouseUp;
        _lostCaptureHandler = OnLostMouseCapture;
        _mouseLeaveHandler = OnMouseLeave;
        _keyDownHandler = OnPreviewKeyDown;
        _keyUpHandler = OnPreviewKeyUp;

        _checkBox.Loaded += OnLoaded;
        _checkBox.Unloaded += OnUnloaded;
        _checkBox.Checked += OnCheckedStateChanged;
        _checkBox.Unchecked += OnCheckedStateChanged;
        _checkBox.Indeterminate += OnCheckedStateChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachInput();
        RefreshParts();
        ApplySelectionState();
        ApplyPressPreviewState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachInput();
        MotionAnimations.Cancel(_selectionChannel);
        MotionAnimations.Cancel(_previewChannel);
        _pointerPressed = false;
        _keyboardPressed = false;
        _fill = null;
        _checkMark = null;
        _indeterminateMark = null;
        _pressPreview = null;
    }

    private void OnCheckedStateChanged(object sender, RoutedEventArgs e)
    {
        if (!EnsureParts())
        {
            return;
        }

        AnimateSelectionState();
        AnimatePressPreviewState();
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_checkBox.IsEnabled || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _pointerPressed = true;
        AnimatePressPreviewState();
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _pointerPressed = false;
        AnimatePressPreviewState();
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        _pointerPressed = false;
        AnimatePressPreviewState();
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (Mouse.LeftButton == MouseButtonState.Released)
        {
            _pointerPressed = false;
            AnimatePressPreviewState();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_checkBox.IsEnabled || e.IsRepeat || e.Key is not (Key.Space or Key.Enter))
        {
            return;
        }

        _keyboardPressed = true;
        AnimatePressPreviewState();
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter))
        {
            return;
        }

        _keyboardPressed = false;
        AnimatePressPreviewState();
    }

    private void AttachInput()
    {
        if (_inputAttached)
        {
            return;
        }

        _checkBox.AddHandler(Mouse.PreviewMouseDownEvent, _mouseDownHandler, true);
        _checkBox.AddHandler(Mouse.PreviewMouseUpEvent, _mouseUpHandler, true);
        _checkBox.AddHandler(Mouse.LostMouseCaptureEvent, _lostCaptureHandler, true);
        _checkBox.MouseLeave += _mouseLeaveHandler;
        _checkBox.AddHandler(Keyboard.PreviewKeyDownEvent, _keyDownHandler, true);
        _checkBox.AddHandler(Keyboard.PreviewKeyUpEvent, _keyUpHandler, true);
        _inputAttached = true;
    }

    private void DetachInput()
    {
        if (!_inputAttached)
        {
            return;
        }

        _checkBox.RemoveHandler(Mouse.PreviewMouseDownEvent, _mouseDownHandler);
        _checkBox.RemoveHandler(Mouse.PreviewMouseUpEvent, _mouseUpHandler);
        _checkBox.RemoveHandler(Mouse.LostMouseCaptureEvent, _lostCaptureHandler);
        _checkBox.MouseLeave -= _mouseLeaveHandler;
        _checkBox.RemoveHandler(Keyboard.PreviewKeyDownEvent, _keyDownHandler);
        _checkBox.RemoveHandler(Keyboard.PreviewKeyUpEvent, _keyUpHandler);
        _inputAttached = false;
    }

    private bool EnsureParts()
    {
        if (_fill is not null && _checkMark is not null && _pressPreview is not null)
        {
            return true;
        }

        RefreshParts();
        return _fill is not null && _checkMark is not null && _pressPreview is not null;
    }

    private void RefreshParts()
    {
        _checkBox.ApplyTemplate();
        _fill = ResolvePart(_fillName);
        _checkMark = ResolvePart(_checkMarkName);
        _indeterminateMark = _indeterminateMarkName is null ? null : ResolvePart(_indeterminateMarkName);
        _pressPreview = ResolvePart(_pressPreviewName);
    }

    private FrameworkElement? ResolvePart(string name) =>
        _checkBox.Template?.FindName(name, _checkBox) as FrameworkElement;

    private void ApplySelectionState()
    {
        MotionAnimations.Cancel(_selectionChannel);
        bool isChecked = _checkBox.IsChecked == true;
        bool isIndeterminate = _checkBox.IsChecked is null;
        ApplyVisual(_fill, isChecked || isIndeterminate ? 1 : HiddenSelectionScale, isChecked || isIndeterminate ? 1 : 0);
        ApplyVisual(_checkMark, isChecked ? 1 : HiddenSelectionScale, isChecked ? 1 : 0);
        ApplyVisual(_indeterminateMark, isIndeterminate ? 1 : HiddenSelectionScale, isIndeterminate ? 1 : 0);
    }

    private void AnimateSelectionState()
    {
        bool isChecked = _checkBox.IsChecked == true;
        bool isIndeterminate = _checkBox.IsChecked is null;
        VisualTween[] tweens =
        [
            CreateTween(_fill, isChecked || isIndeterminate ? 1 : HiddenSelectionScale, isChecked || isIndeterminate ? 1 : 0),
            CreateTween(_checkMark, isChecked ? 1 : HiddenSelectionScale, isChecked ? 1 : 0),
            CreateTween(_indeterminateMark, isIndeterminate ? 1 : HiddenSelectionScale, isIndeterminate ? 1 : 0)
        ];
        Animate(_selectionChannel, MotionPreferences.StandardDuration, tweens);
    }

    private void ApplyPressPreviewState()
    {
        MotionAnimations.Cancel(_previewChannel);
        bool visible = IsPressActive() && _checkBox.IsChecked == false;
        ApplyVisual(_pressPreview, visible ? PressPreviewScale : 0, visible ? 1 : 0);
    }

    private void AnimatePressPreviewState()
    {
        if (!EnsureParts())
        {
            return;
        }

        bool visible = IsPressActive() && _checkBox.IsChecked == false;
        Animate(
            _previewChannel,
            MotionPreferences.FastDuration,
            [CreateTween(_pressPreview, visible ? PressPreviewScale : 0, visible ? 1 : 0)]);
    }

    private bool IsPressActive() => _checkBox.IsEnabled && (_pointerPressed || _keyboardPressed);

    private static VisualTween CreateTween(FrameworkElement? element, double scale, double opacity)
    {
        if (element is null)
        {
            return default;
        }

        ScaleTransform transform = EnsureScaleTransform(element);
        return new VisualTween(element, transform, transform.ScaleX, element.Opacity, scale, opacity);
    }

    private static void Animate(object channel, TimeSpan duration, VisualTween[] tweens)
    {
        MotionAnimations.Start(channel, duration, MotionEasing.CubicEaseOut, progress =>
        {
            foreach (VisualTween tween in tweens)
            {
                tween.Apply(progress);
            }
        });
    }

    private static void ApplyVisual(FrameworkElement? element, double scale, double opacity)
    {
        if (element is null)
        {
            return;
        }

        ScaleTransform transform = EnsureScaleTransform(element);
        transform.ScaleX = scale;
        transform.ScaleY = scale;
        element.Opacity = opacity;
    }

    private static ScaleTransform EnsureScaleTransform(FrameworkElement element)
    {
        if (element.RenderTransform is ScaleTransform scale)
        {
            if (!scale.IsFrozen)
            {
                return scale;
            }

            scale = scale.CloneCurrentValue();
            element.RenderTransform = scale;
            return scale;
        }

        scale = new ScaleTransform(1, 1);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = scale;
        return scale;
    }

    private readonly record struct VisualTween(
        FrameworkElement? Element,
        ScaleTransform? Transform,
        double FromScale,
        double FromOpacity,
        double ToScale,
        double ToOpacity)
    {
        internal void Apply(double progress)
        {
            if (Element is null || Transform is null)
            {
                return;
            }

            double scale = FromScale + ((ToScale - FromScale) * progress);
            Transform.ScaleX = scale;
            Transform.ScaleY = scale;
            Element.Opacity = FromOpacity + ((ToOpacity - FromOpacity) * progress);
        }
    }
}
