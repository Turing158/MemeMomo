using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Memo.UI.Animation;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using KeyEventHandler = System.Windows.Input.KeyEventHandler;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseEventHandler = System.Windows.Input.MouseEventHandler;
using Point = System.Windows.Point;
using WpfControl = System.Windows.Controls.Control;
using Brush = System.Windows.Media.Brush;

namespace Memo.UI;

public enum InteractionAnimationProfile
{
    None,
    Button,
    Surface,
    MenuItem
}

/// <summary>
/// Adds layout-neutral hover and press motion to an interaction source. A named
/// descendant can be used as the visual target when the source owns other
/// transforms or when only the component surface should move.
/// </summary>
public static class InteractionAnimations
{
    private static readonly TimeSpan HoverInDuration = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan HoverOutDuration = TimeSpan.FromMilliseconds(170);
    private static readonly TimeSpan PressDuration = TimeSpan.FromMilliseconds(70);
    private static readonly TimeSpan ReleaseDuration = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan MinimumVisiblePressDuration = TimeSpan.FromMilliseconds(80);

    public static readonly DependencyProperty ProfileProperty = DependencyProperty.RegisterAttached(
        "Profile",
        typeof(InteractionAnimationProfile),
        typeof(InteractionAnimations),
        new FrameworkPropertyMetadata(InteractionAnimationProfile.None, OnProfileChanged));

    public static readonly DependencyProperty TargetNameProperty = DependencyProperty.RegisterAttached(
        "TargetName",
        typeof(string),
        typeof(InteractionAnimations),
        new FrameworkPropertyMetadata(string.Empty, OnSurfaceStructureChanged));

    public static readonly DependencyProperty HoverScaleProperty = RegisterMotionValue("HoverScale");
    public static readonly DependencyProperty PressedScaleProperty = RegisterMotionValue("PressedScale");
    public static readonly DependencyProperty HoverOffsetXProperty = RegisterMotionValue("HoverOffsetX");
    public static readonly DependencyProperty HoverOffsetYProperty = RegisterMotionValue("HoverOffsetY");
    public static readonly DependencyProperty PressedOffsetXProperty = RegisterMotionValue("PressedOffsetX");
    public static readonly DependencyProperty PressedOffsetYProperty = RegisterMotionValue("PressedOffsetY");

    public static readonly DependencyProperty SurfaceHoverLayerNameProperty = DependencyProperty.RegisterAttached(
        "SurfaceHoverLayerName",
        typeof(string),
        typeof(InteractionAnimations),
        new FrameworkPropertyMetadata(string.Empty, OnSurfaceStructureChanged));

    public static readonly DependencyProperty SurfacePressedLayerNameProperty = DependencyProperty.RegisterAttached(
        "SurfacePressedLayerName",
        typeof(string),
        typeof(InteractionAnimations),
        new FrameworkPropertyMetadata(string.Empty, OnSurfaceStructureChanged));

    /// <summary>Hover surface background used by a template layer. The engine only drives layer opacity;
    /// templates read this value through a binding.</summary>
    public static readonly DependencyProperty HoverBackgroundProperty = DependencyProperty.RegisterAttached(
        "HoverBackground",
        typeof(Brush),
        typeof(InteractionAnimations),
        new FrameworkPropertyMetadata(null));

    /// <summary>Pressed surface background used by a template layer.</summary>
    public static readonly DependencyProperty PressedBackgroundProperty = DependencyProperty.RegisterAttached(
        "PressedBackground",
        typeof(Brush),
        typeof(InteractionAnimations),
        new FrameworkPropertyMetadata(null));

    private static readonly ConditionalWeakTable<FrameworkElement, InteractionAnimationState> States = new();

    public static InteractionAnimationProfile GetProfile(DependencyObject element) =>
        (InteractionAnimationProfile)element.GetValue(ProfileProperty);

    public static void SetProfile(DependencyObject element, InteractionAnimationProfile value) =>
        element.SetValue(ProfileProperty, value);

    public static string GetTargetName(DependencyObject element) =>
        (string)element.GetValue(TargetNameProperty);

    public static void SetTargetName(DependencyObject element, string value) =>
        element.SetValue(TargetNameProperty, value ?? string.Empty);

    public static double GetHoverScale(DependencyObject element) => (double)element.GetValue(HoverScaleProperty);
    public static void SetHoverScale(DependencyObject element, double value) => element.SetValue(HoverScaleProperty, value);
    public static double GetPressedScale(DependencyObject element) => (double)element.GetValue(PressedScaleProperty);
    public static void SetPressedScale(DependencyObject element, double value) => element.SetValue(PressedScaleProperty, value);
    public static double GetHoverOffsetX(DependencyObject element) => (double)element.GetValue(HoverOffsetXProperty);
    public static void SetHoverOffsetX(DependencyObject element, double value) => element.SetValue(HoverOffsetXProperty, value);
    public static double GetHoverOffsetY(DependencyObject element) => (double)element.GetValue(HoverOffsetYProperty);
    public static void SetHoverOffsetY(DependencyObject element, double value) => element.SetValue(HoverOffsetYProperty, value);
    public static double GetPressedOffsetX(DependencyObject element) => (double)element.GetValue(PressedOffsetXProperty);
    public static void SetPressedOffsetX(DependencyObject element, double value) => element.SetValue(PressedOffsetXProperty, value);
    public static double GetPressedOffsetY(DependencyObject element) => (double)element.GetValue(PressedOffsetYProperty);
    public static void SetPressedOffsetY(DependencyObject element, double value) => element.SetValue(PressedOffsetYProperty, value);
    public static string GetSurfaceHoverLayerName(DependencyObject element) => (string)element.GetValue(SurfaceHoverLayerNameProperty);
    public static void SetSurfaceHoverLayerName(DependencyObject element, string value) => element.SetValue(SurfaceHoverLayerNameProperty, value ?? string.Empty);
    public static string GetSurfacePressedLayerName(DependencyObject element) => (string)element.GetValue(SurfacePressedLayerNameProperty);
    public static void SetSurfacePressedLayerName(DependencyObject element, string value) => element.SetValue(SurfacePressedLayerNameProperty, value ?? string.Empty);
    public static Brush? GetHoverBackground(DependencyObject element) => (Brush?)element.GetValue(HoverBackgroundProperty);
    public static void SetHoverBackground(DependencyObject element, Brush value) => element.SetValue(HoverBackgroundProperty, value);
    public static Brush? GetPressedBackground(DependencyObject element) => (Brush?)element.GetValue(PressedBackgroundProperty);
    public static void SetPressedBackground(DependencyObject element, Brush value) => element.SetValue(PressedBackgroundProperty, value);

    internal static InteractionAnimationSnapshot Capture(FrameworkElement element) =>
        States.TryGetValue(element, out InteractionAnimationState? state)
            ? state.Capture()
            : InteractionAnimationSnapshot.Idle;

    private static DependencyProperty RegisterMotionValue(string name) =>
        DependencyProperty.RegisterAttached(
            name,
            typeof(double),
            typeof(InteractionAnimations),
            new FrameworkPropertyMetadata(double.NaN, OnMotionValueChanged));

    private static void OnProfileChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        if (States.TryGetValue(element, out InteractionAnimationState? previous))
        {
            previous.Dispose();
            States.Remove(element);
        }

        if ((InteractionAnimationProfile)e.NewValue == InteractionAnimationProfile.None)
        {
            return;
        }

        InteractionAnimationState state = new(element);
        States.Add(element, state);
        state.Attach();
    }

    private static void OnSurfaceStructureChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element && States.TryGetValue(element, out InteractionAnimationState? state))
        {
            state.RefreshTarget();
        }
    }

    private static void OnMotionValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element && States.TryGetValue(element, out InteractionAnimationState? state))
        {
            state.RefreshMotion();
        }
    }

    private sealed class InteractionAnimationState : IDisposable
    {
        private readonly FrameworkElement _source;
        private readonly MouseButtonEventHandler _mouseDownHandler;
        private readonly MouseButtonEventHandler _mouseUpHandler;
        private readonly MouseEventHandler _lostCaptureHandler;
        private readonly KeyEventHandler _keyDownHandler;
        private readonly KeyEventHandler _keyUpHandler;
        private readonly DispatcherTimer _minimumPressTimer;
        private FrameworkElement? _target;
        private FrameworkElement? _hoverLayer;
        private FrameworkElement? _pressedLayer;
        private TransformGroup? _motionTransform;
        private ScaleTransform? _scale;
        private TranslateTransform? _translation;
        private object? _originalTransformValue;
        private object? _originalOriginValue;
        private object? _originalCacheModeValue;
        private bool _ownsAnimationCache;
        private bool _inputAttached;
        private bool _menuItemHooked;
        private bool _pointerPressed;
        private bool _keyboardPressed;
        private bool _minimumPressHeld;
        private long _pressStartedTimestamp;
        private InteractionVisualState _lastState;
        private int _disposed;

        internal InteractionAnimationState(FrameworkElement source)
        {
            _source = source;
            _mouseDownHandler = OnPreviewMouseDown;
            _mouseUpHandler = OnPreviewMouseUp;
            _lostCaptureHandler = OnLostMouseCapture;
            _keyDownHandler = OnPreviewKeyDown;
            _keyUpHandler = OnPreviewKeyUp;
            _minimumPressTimer = new DispatcherTimer(DispatcherPriority.Input, source.Dispatcher);
            _minimumPressTimer.Tick += OnMinimumPressTimerTick;
        }

        internal void Attach()
        {
            _source.Loaded += OnLoaded;
            _source.Unloaded += OnUnloaded;
            if (_source.IsLoaded)
            {
                AttachInput();
                AttachMenuItemHighlight();
                RefreshTarget();
            }
        }

        internal void RefreshTarget()
        {
            if (Volatile.Read(ref _disposed) != 0 || !_source.IsLoaded)
            {
                return;
            }

            if (_source is WpfControl control)
            {
                control.ApplyTemplate();
            }

            FrameworkElement target = ResolveElement(GetTargetName(_source)) ?? _source;
            if (ReferenceEquals(target, _target) && _scale is not null)
            {
                RefreshLayers();
                RefreshMotion();
                return;
            }

            RestoreTarget();
            _target = target;
            _originalTransformValue = target.ReadLocalValue(UIElement.RenderTransformProperty);
            _originalOriginValue = target.ReadLocalValue(UIElement.RenderTransformOriginProperty);
            _originalCacheModeValue = target.ReadLocalValue(UIElement.CacheModeProperty);

            TransformGroup group = new();
            Transform existing = target.RenderTransform;
            if (existing is not null && !ReferenceEquals(existing, Transform.Identity))
            {
                group.Children.Add(existing);
            }

            _scale = new ScaleTransform(1, 1);
            _translation = new TranslateTransform();
            group.Children.Add(_scale);
            group.Children.Add(_translation);
            _motionTransform = group;
            target.RenderTransformOrigin = new Point(0.5, 0.5);
            target.RenderTransform = group;
            RefreshLayers();
            RefreshMotion();
        }

        private void RefreshLayers()
        {
            _hoverLayer = ResolveElement(GetSurfaceHoverLayerName(_source));
            _pressedLayer = ResolveElement(GetSurfacePressedLayerName(_source));
            if (_lastState == InteractionVisualState.Idle)
            {
                if (_hoverLayer is not null)
                {
                    _hoverLayer.Opacity = 0;
                }

                if (_pressedLayer is not null)
                {
                    _pressedLayer.Opacity = 0;
                }
            }
        }

        internal void RefreshMotion() => AnimateTo(ResolveFrame());

        internal InteractionAnimationSnapshot Capture() => new(
            _scale?.ScaleX ?? 1,
            _translation?.X ?? 0,
            _translation?.Y ?? 0,
            MotionAnimations.IsRunning(this));

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _source.Loaded -= OnLoaded;
            _source.Unloaded -= OnUnloaded;
            _minimumPressTimer.Stop();
            _minimumPressTimer.Tick -= OnMinimumPressTimerTick;
            DetachInput();
            DetachMenuItemHighlight();
            MotionAnimations.Cancel(this);
            RestoreTarget();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            AttachInput();
            AttachMenuItemHighlight();
            RefreshTarget();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _pointerPressed = false;
            _keyboardPressed = false;
            ClearMinimumPressHold();
            DetachInput();
            DetachMenuItemHighlight();
            MotionAnimations.Cancel(this);
            _lastState = InteractionVisualState.Idle;
            ApplyFrame(SurfaceFrame.Idle);
            EndAnimationCache();
        }

        private void AttachInput()
        {
            if (_inputAttached)
            {
                return;
            }

            _source.MouseEnter += OnMouseEnter;
            _source.MouseLeave += OnMouseLeave;
            _source.AddHandler(Mouse.PreviewMouseDownEvent, _mouseDownHandler, true);
            _source.AddHandler(Mouse.PreviewMouseUpEvent, _mouseUpHandler, true);
            _source.AddHandler(Mouse.LostMouseCaptureEvent, _lostCaptureHandler, true);
            _source.AddHandler(Keyboard.PreviewKeyDownEvent, _keyDownHandler, true);
            _source.AddHandler(Keyboard.PreviewKeyUpEvent, _keyUpHandler, true);
            _source.IsEnabledChanged += OnIsEnabledChanged;
            _inputAttached = true;
        }

        private void DetachMenuItemHighlight()
        {
            if (!_menuItemHooked || _source is not MenuItem menuItem)
            {
                return;
            }

            DependencyPropertyDescriptor
                .FromProperty(MenuItem.IsHighlightedProperty, typeof(MenuItem))
                .RemoveValueChanged(menuItem, OnMenuHighlightChanged);
            _menuItemHooked = false;
        }

        private void AttachMenuItemHighlight()
        {
            if (_menuItemHooked || _source is not MenuItem menuItem)
            {
                return;
            }

            DependencyPropertyDescriptor
                .FromProperty(MenuItem.IsHighlightedProperty, typeof(MenuItem))
                .AddValueChanged(menuItem, OnMenuHighlightChanged);
            _menuItemHooked = true;
        }

        private void OnMenuHighlightChanged(object? sender, EventArgs e) => RefreshMotion();

        private void DetachInput()
        {
            if (!_inputAttached)
            {
                return;
            }

            _source.MouseEnter -= OnMouseEnter;
            _source.MouseLeave -= OnMouseLeave;
            _source.RemoveHandler(Mouse.PreviewMouseDownEvent, _mouseDownHandler);
            _source.RemoveHandler(Mouse.PreviewMouseUpEvent, _mouseUpHandler);
            _source.RemoveHandler(Mouse.LostMouseCaptureEvent, _lostCaptureHandler);
            _source.RemoveHandler(Keyboard.PreviewKeyDownEvent, _keyDownHandler);
            _source.RemoveHandler(Keyboard.PreviewKeyUpEvent, _keyUpHandler);
            _source.IsEnabledChanged -= OnIsEnabledChanged;
            _inputAttached = false;
        }

        private void OnMouseEnter(object sender, MouseEventArgs e) => RefreshMotion();

        private void OnMouseLeave(object sender, MouseEventArgs e)
        {
            _pointerPressed = false;
            EndVisualPress(Mouse.LeftButton == MouseButtonState.Released);
        }

        private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || !_source.IsEnabled)
            {
                return;
            }

            bool wasPressed = _pointerPressed || _keyboardPressed;
            _pointerPressed = true;
            if (!wasPressed)
            {
                BeginVisualPress();
            }
        }

        private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
            {
                return;
            }

            _pointerPressed = false;
            EndVisualPress(preserveMinimum: true);
        }

        private void OnLostMouseCapture(object sender, MouseEventArgs e)
        {
            _pointerPressed = false;
            EndVisualPress(Mouse.LeftButton == MouseButtonState.Released);
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_source.IsEnabled || e.IsRepeat || e.Key is not (Key.Space or Key.Enter))
            {
                return;
            }

            bool wasPressed = _pointerPressed || _keyboardPressed;
            _keyboardPressed = true;
            if (!wasPressed)
            {
                BeginVisualPress();
            }
        }

        private void OnPreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key is not (Key.Space or Key.Enter))
            {
                return;
            }

            _keyboardPressed = false;
            EndVisualPress(preserveMinimum: true);
        }

        private void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!_source.IsEnabled)
            {
                _pointerPressed = false;
                _keyboardPressed = false;
                ClearMinimumPressHold();
            }

            RefreshMotion();
        }

        private void BeginVisualPress()
        {
            _minimumPressTimer.Stop();
            _pressStartedTimestamp = Stopwatch.GetTimestamp();
            _minimumPressHeld = MotionPreferences.AnimationsEnabled;
            RefreshMotion();
        }

        private void EndVisualPress(bool preserveMinimum)
        {
            if (_pointerPressed || _keyboardPressed)
            {
                RefreshMotion();
                return;
            }

            if (!preserveMinimum || !_minimumPressHeld || !MotionPreferences.AnimationsEnabled)
            {
                ClearMinimumPressHold();
                RefreshMotion();
                return;
            }

            TimeSpan remaining = MinimumVisiblePressDuration - Stopwatch.GetElapsedTime(_pressStartedTimestamp);
            if (remaining <= TimeSpan.Zero)
            {
                ClearMinimumPressHold();
                RefreshMotion();
                return;
            }

            _minimumPressTimer.Stop();
            _minimumPressTimer.Interval = remaining;
            _minimumPressTimer.Start();
        }

        private void OnMinimumPressTimerTick(object? sender, EventArgs e)
        {
            ClearMinimumPressHold();
            RefreshMotion();
        }

        private void ClearMinimumPressHold()
        {
            _minimumPressTimer.Stop();
            _minimumPressHeld = false;
        }

        private FrameworkElement? ResolveElement(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            if (_source.FindName(name) is FrameworkElement named)
            {
                return named;
            }

            if (_source is WpfControl control && control.Template?.FindName(name, control) is FrameworkElement templatePart)
            {
                return templatePart;
            }

            return FindVisualDescendant(_source, name);
        }

        private static FrameworkElement? FindVisualDescendant(DependencyObject parent, string name)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                if (child is FrameworkElement element && string.Equals(element.Name, name, StringComparison.Ordinal))
                {
                    return element;
                }

                FrameworkElement? nested = FindVisualDescendant(child, name);
                if (nested is not null)
                {
                    return nested;
                }
            }

            return null;
        }

        private SurfaceFrame ResolveFrame()
        {
            InteractionAnimationProfile profile = GetProfile(_source);
            bool pressed = _source.IsEnabled && (_pointerPressed || _keyboardPressed || _minimumPressHeld);
            bool hovered = _source.IsEnabled && _source.IsMouseOver;
            if (profile == InteractionAnimationProfile.MenuItem && _source is MenuItem menuItem && menuItem.IsHighlighted)
            {
                hovered = true;
            }
            MotionFrame hover = profile switch
            {
                InteractionAnimationProfile.Button => new(1.04, 0, 0),
                InteractionAnimationProfile.Surface => new(1.02, 0, 0),
                InteractionAnimationProfile.MenuItem => new(1.03, 0, 0),
                _ => MotionFrame.Idle
            };
            MotionFrame pressedFrame = profile switch
            {
                InteractionAnimationProfile.Button => new(0.95, 0, 0),
                InteractionAnimationProfile.Surface => new(0.975, 0, 0),
                InteractionAnimationProfile.MenuItem => new(0.965, 0, 0),
                _ => MotionFrame.Idle
            };

            hover = ApplyOverrides(hover, isPressed: false);
            pressedFrame = ApplyOverrides(pressedFrame, isPressed: true);
            if (pressed)
            {
                return new SurfaceFrame(pressedFrame, 0, 1, InteractionVisualState.Pressed);
            }

            return hovered
                ? new SurfaceFrame(hover, 1, 0, InteractionVisualState.Hover)
                : new SurfaceFrame(MotionFrame.Idle, 0, 0, InteractionVisualState.Idle);
        }

        private MotionFrame ApplyOverrides(MotionFrame frame, bool isPressed)
        {
            double scale = isPressed ? GetPressedScale(_source) : GetHoverScale(_source);
            double x = isPressed ? GetPressedOffsetX(_source) : GetHoverOffsetX(_source);
            double y = isPressed ? GetPressedOffsetY(_source) : GetHoverOffsetY(_source);
            return new MotionFrame(
                double.IsNaN(scale) ? frame.Scale : scale,
                double.IsNaN(x) ? frame.X : x,
                double.IsNaN(y) ? frame.Y : y);
        }

        private void AnimateTo(SurfaceFrame target)
        {
            if (_scale is null || _translation is null)
            {
                return;
            }

            SurfaceFrame from = new(
                new MotionFrame(_scale.ScaleX, _translation.X, _translation.Y),
                _hoverLayer?.Opacity ?? 0,
                _pressedLayer?.Opacity ?? 0,
                _lastState);
            if (from.NearlyEquals(target))
            {
                MotionAnimations.Cancel(this);
                ApplyFrame(target);
                EndAnimationCache();
                return;
            }

            TimeSpan duration = target.State switch
            {
                InteractionVisualState.Pressed => PressDuration,
                InteractionVisualState.Hover => HoverInDuration,
                _ => _lastState == InteractionVisualState.Pressed ? ReleaseDuration : HoverOutDuration
            };
            _lastState = target.State;

            BeginAnimationCache();
            MotionAnimations.Start(
                this,
                MotionPreferences.Effective(duration),
                MotionEasing.CubicEaseOut,
                progress => ApplyFrame(SurfaceFrame.Lerp(from, target, progress)),
                () =>
                {
                    ApplyFrame(target);
                    EndAnimationCache();
                });
        }

        private void BeginAnimationCache()
        {
            if (_target is null || _ownsAnimationCache || _target.CacheMode is not null)
            {
                return;
            }

            _target.CacheMode = new BitmapCache
            {
                EnableClearType = true,
                SnapsToDevicePixels = false
            };
            _ownsAnimationCache = true;
        }

        private void EndAnimationCache()
        {
            if (!_ownsAnimationCache || _target is null)
            {
                return;
            }

            RestoreLocalValue(_target, UIElement.CacheModeProperty, _originalCacheModeValue);
            _ownsAnimationCache = false;
        }

        private void ApplyFrame(SurfaceFrame frame)
        {
            if (_scale is not null)
            {
                _scale.ScaleX = frame.Motion.Scale;
                _scale.ScaleY = frame.Motion.Scale;
            }

            if (_translation is not null)
            {
                _translation.X = frame.Motion.X;
                _translation.Y = frame.Motion.Y;
            }

            if (_hoverLayer is not null)
            {
                _hoverLayer.Opacity = frame.HoverOpacity;
            }

            if (_pressedLayer is not null)
            {
                _pressedLayer.Opacity = frame.PressedOpacity;
            }
        }

        private void RestoreTarget()
        {
            MotionAnimations.Cancel(this);
            EndAnimationCache();
            if (_target is not null && ReferenceEquals(_target.RenderTransform, _motionTransform))
            {
                RestoreLocalValue(_target, UIElement.RenderTransformProperty, _originalTransformValue);
                RestoreLocalValue(_target, UIElement.RenderTransformOriginProperty, _originalOriginValue);
            }

            if (_hoverLayer is not null)
            {
                _hoverLayer.Opacity = 0;
            }

            if (_pressedLayer is not null)
            {
                _pressedLayer.Opacity = 0;
            }

            _target = null;
            _hoverLayer = null;
            _pressedLayer = null;
            _motionTransform = null;
            _scale = null;
            _translation = null;
            _originalTransformValue = null;
            _originalOriginValue = null;
            _originalCacheModeValue = null;
            _ownsAnimationCache = false;
            _lastState = InteractionVisualState.Idle;
        }

        private static void RestoreLocalValue(DependencyObject target, DependencyProperty property, object? value)
        {
            if (value is null || ReferenceEquals(value, DependencyProperty.UnsetValue))
            {
                target.ClearValue(property);
            }
            else
            {
                target.SetValue(property, value);
            }
        }
    }

    private readonly record struct MotionFrame(double Scale, double X, double Y)
    {
        internal static MotionFrame Idle => new(1, 0, 0);

        internal static MotionFrame Lerp(MotionFrame from, MotionFrame to, double progress) => new(
            from.Scale + ((to.Scale - from.Scale) * progress),
            from.X + ((to.X - from.X) * progress),
            from.Y + ((to.Y - from.Y) * progress));

        internal bool NearlyEquals(MotionFrame other) =>
            Math.Abs(Scale - other.Scale) < 0.0001
            && Math.Abs(X - other.X) < 0.0001
            && Math.Abs(Y - other.Y) < 0.0001;
    }

    private enum InteractionVisualState
    {
        Idle,
        Hover,
        Pressed
    }

    private readonly record struct SurfaceFrame(MotionFrame Motion, double HoverOpacity, double PressedOpacity, InteractionVisualState State)
    {
        internal static SurfaceFrame Idle => new(MotionFrame.Idle, 0, 0, InteractionVisualState.Idle);

        internal static SurfaceFrame Lerp(SurfaceFrame from, SurfaceFrame to, double progress) => new(
            MotionFrame.Lerp(from.Motion, to.Motion, progress),
            from.HoverOpacity + ((to.HoverOpacity - from.HoverOpacity) * progress),
            from.PressedOpacity + ((to.PressedOpacity - from.PressedOpacity) * progress),
            to.State);

        internal bool NearlyEquals(SurfaceFrame other) =>
            Motion.NearlyEquals(other.Motion)
            && Math.Abs(HoverOpacity - other.HoverOpacity) < 0.0005
            && Math.Abs(PressedOpacity - other.PressedOpacity) < 0.0005;
    }
}

internal readonly record struct InteractionAnimationSnapshot(double Scale, double X, double Y, bool IsAnimating)
{
    internal static InteractionAnimationSnapshot Idle => new(1, 0, 0, false);
}
