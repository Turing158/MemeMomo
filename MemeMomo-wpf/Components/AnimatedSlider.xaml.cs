using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MemeMomo.UI;
using MemeMomo.UI.Animation;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using KeyEventHandler = System.Windows.Input.KeyEventHandler;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseEventHandler = System.Windows.Input.MouseEventHandler;
using UserControl = System.Windows.Controls.UserControl;
using Window = System.Windows.Window;

namespace MemeMomo.Components;

public sealed class SliderValueChangedEventArgs(int oldValue, int newValue) : EventArgs
{
    public int OldValue { get; } = oldValue;
    public int NewValue { get; } = newValue;
}

public partial class AnimatedSlider : UserControl
{
    private const double IdleThumbScale = 1;
    private const double HoverThumbScale = 1.1;
    private const double PressedThumbScale = 0.96;
    private const double ThumbVisualDiameter = 18;
    private const double TooltipGap = 4;

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum),
        typeof(int),
        typeof(AnimatedSlider),
        new FrameworkPropertyMetadata(0, OnRangePropertyChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(int),
        typeof(AnimatedSlider),
        new FrameworkPropertyMetadata(100, OnRangePropertyChanged));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(int),
        typeof(AnimatedSlider),
        new FrameworkPropertyMetadata(
            0,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnValuePropertyChanged,
            CoerceValue));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step),
        typeof(int),
        typeof(AnimatedSlider),
        new FrameworkPropertyMetadata(1, OnRangePropertyChanged, CoerceStep));

    public static readonly DependencyProperty ValueSuffixProperty = DependencyProperty.Register(
        nameof(ValueSuffix),
        typeof(string),
        typeof(AnimatedSlider),
        new FrameworkPropertyMetadata(string.Empty, OnValueSuffixChanged));

    private readonly object _thumbAnimationChannel = new();
    private readonly object _thumbBackgroundAnimationChannel = new();
    private readonly object _tooltipAnimationChannel = new();
    private bool _syncingSlider;
    private bool _pointerInteraction;
    private bool _keyboardInteraction;
    private bool _sliderPointerOver;
    private bool _sliderHoverSubscribed;
    private int _lastCommittedValue;
    private Thumb? _thumb;
    private Border? _thumbFillVisual;
    private Window? _ownerWindow;

    public AnimatedSlider()
    {
        InitializeComponent();
        InnerSlider.ValueChanged += OnInnerSliderValueChanged;
        TooltipPopup.CustomPopupPlacementCallback = PlaceTooltip;
        InnerSlider.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnSliderMouseDown), true);
        InnerSlider.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnSliderMouseUp), true);
        InnerSlider.AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler(OnSliderLostMouseCapture), true);
        InnerSlider.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnSliderKeyDown), true);
        InnerSlider.AddHandler(Keyboard.PreviewKeyUpEvent, new KeyEventHandler(OnSliderKeyUp), true);
        InnerSlider.LostKeyboardFocus += OnSliderLostKeyboardFocus;
        InnerSlider.SizeChanged += OnSliderSizeChanged;
        BarTrack.SizeChanged += OnBarTrackSizeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsEnabledChanged += OnIsEnabledChanged;
        _lastCommittedValue = Value;
        SyncConfiguration();
        UpdateText();
    }

    public int Minimum
    {
        get => (int)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public int Maximum
    {
        get => (int)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public int Step
    {
        get => (int)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public string ValueSuffix
    {
        get => (string)GetValue(ValueSuffixProperty);
        set => SetValue(ValueSuffixProperty, value ?? string.Empty);
    }

    public event EventHandler<SliderValueChangedEventArgs>? ValueChanged;
    public event EventHandler<SliderValueChangedEventArgs>? ValueCommitted;

    internal Slider SliderPart => InnerSlider;
    internal bool IsTooltipOpen => TooltipPopup.IsOpen;
    internal string TooltipDisplayText => TooltipText.Text;

    private static object CoerceStep(DependencyObject d, object baseValue) => Math.Max(1, (int)baseValue);

    private static object CoerceValue(DependencyObject d, object baseValue) =>
        ((AnimatedSlider)d).NormalizeValue((int)baseValue);

    private static void OnRangePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        AnimatedSlider control = (AnimatedSlider)d;
        if (control.Maximum < control.Minimum)
        {
            control.SetCurrentValue(MaximumProperty, control.Minimum);
        }

        control.CoerceValue(ValueProperty);
        control.SyncConfiguration();
        control.UpdateText();
        control.UpdateBarProgress();
    }

    private static void OnValuePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        AnimatedSlider control = (AnimatedSlider)d;
        int oldValue = (int)e.OldValue;
        int newValue = (int)e.NewValue;
        control.SyncValueToSlider();
        if (!control.IsUserInteractionActive)
        {
            control._lastCommittedValue = newValue;
        }

        control.UpdateText();
        control.UpdateBarProgress();
        control.UpdateTooltipOffset();
        control.ValueChanged?.Invoke(control, new SliderValueChangedEventArgs(oldValue, newValue));
    }

    private static void OnValueSuffixChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((AnimatedSlider)d).UpdateText();

    private bool IsUserInteractionActive => _pointerInteraction || _keyboardInteraction;

    private int NormalizeValue(int value)
    {
        int maximum = Math.Max(Minimum, Maximum);
        int clamped = Math.Clamp(value, Minimum, maximum);
        if (Step <= 1)
        {
            return clamped;
        }

        int snapped = Minimum + ((int)Math.Round((clamped - Minimum) / (double)Step) * Step);
        return Math.Clamp(snapped, Minimum, maximum);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachOwnerWindow();
        SubscribeSliderHover();
        InnerSlider.ApplyTemplate();
        ResolveThumb();
        SyncConfiguration();
        UpdateBarProgress();
        UpdateTooltipOffset();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        EndPointerInteraction();
        _keyboardInteraction = false;
        CommitValue();
        HideTooltip(immediate: true);
        MotionAnimations.Cancel(_thumbAnimationChannel);
        MotionAnimations.Cancel(_thumbBackgroundAnimationChannel);
        MotionAnimations.Cancel(_tooltipAnimationChannel);
        if (_thumb?.RenderTransform is ScaleTransform transform)
        {
            transform.ScaleX = IdleThumbScale;
            transform.ScaleY = IdleThumbScale;
        }

        if (_thumbFillVisual is not null)
        {
            _thumbFillVisual.Opacity = 1;
        }

        UnsubscribeSliderHover();
        UnsubscribeThumb();
        DetachOwnerWindow();
    }

    private void SubscribeSliderHover()
    {
        if (_sliderHoverSubscribed)
        {
            return;
        }

        InnerSlider.MouseEnter += OnSliderMouseEnter;
        InnerSlider.MouseLeave += OnSliderMouseLeave;
        _sliderHoverSubscribed = true;
    }

    private void UnsubscribeSliderHover()
    {
        if (!_sliderHoverSubscribed)
        {
            return;
        }

        InnerSlider.MouseEnter -= OnSliderMouseEnter;
        InnerSlider.MouseLeave -= OnSliderMouseLeave;
        _sliderHoverSubscribed = false;
        _sliderPointerOver = false;
    }

    private void AttachOwnerWindow()
    {
        Window? owner = Window.GetWindow(this);
        if (ReferenceEquals(owner, _ownerWindow))
        {
            return;
        }

        DetachOwnerWindow();
        _ownerWindow = owner;
        if (_ownerWindow is not null)
        {
            _ownerWindow.Deactivated += OnOwnerDeactivated;
        }
    }

    private void DetachOwnerWindow()
    {
        if (_ownerWindow is not null)
        {
            _ownerWindow.Deactivated -= OnOwnerDeactivated;
            _ownerWindow = null;
        }
    }

    private void OnOwnerDeactivated(object? sender, EventArgs e)
    {
        EndPointerInteraction();
        if (_keyboardInteraction)
        {
            _keyboardInteraction = false;
            HideTooltip();
            CommitValue();
        }
    }

    private void SyncConfiguration()
    {
        _syncingSlider = true;
        try
        {
            InnerSlider.Minimum = Minimum;
            InnerSlider.Maximum = Math.Max(Minimum, Maximum);
            InnerSlider.TickFrequency = Step;
            InnerSlider.SmallChange = Step;
            InnerSlider.LargeChange = Math.Max(Step, Step * 5);
            InnerSlider.Value = NormalizeValue(Value);
        }
        finally
        {
            _syncingSlider = false;
        }
    }

    private void SyncValueToSlider()
    {
        if (_syncingSlider)
        {
            return;
        }

        _syncingSlider = true;
        try
        {
            InnerSlider.Value = Value;
        }
        finally
        {
            _syncingSlider = false;
        }
    }

    private void OnInnerSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_syncingSlider)
        {
            SetCurrentValue(ValueProperty, NormalizeValue((int)Math.Round(e.NewValue)));
        }
    }

    private void OnSliderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !IsEnabled)
        {
            return;
        }

        _pointerInteraction = true;
        ShowTooltip();
        AnimateThumbScale(PressedThumbScale);
    }

    private void OnSliderMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            EndPointerInteraction();
        }
    }

    private void OnSliderLostMouseCapture(object sender, MouseEventArgs e) => EndPointerInteraction();

    private void EndPointerInteraction()
    {
        if (!_pointerInteraction)
        {
            return;
        }

        _pointerInteraction = false;
        AnimateThumbScale(_sliderPointerOver ? HoverThumbScale : IdleThumbScale);
        HideTooltip();
        CommitValue();
    }

    private void OnSliderKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsSliderNavigationKey(e.Key))
        {
            return;
        }

        _keyboardInteraction = true;
        ShowTooltip();
        AnimateThumbScale(PressedThumbScale);
    }

    private void OnSliderKeyUp(object sender, KeyEventArgs e)
    {
        if (!IsSliderNavigationKey(e.Key) || !_keyboardInteraction)
        {
            return;
        }

        _keyboardInteraction = false;
        AnimateThumbScale(_sliderPointerOver ? HoverThumbScale : IdleThumbScale);
        HideTooltip();
        CommitValue();
    }

    private static bool IsSliderNavigationKey(Key key) => key is
        Key.Left or Key.Right or Key.Up or Key.Down or
        Key.PageUp or Key.PageDown or Key.Home or Key.End;

    private void CommitValue()
    {
        if (_lastCommittedValue == Value)
        {
            return;
        }

        int oldValue = _lastCommittedValue;
        _lastCommittedValue = Value;
        ValueCommitted?.Invoke(this, new SliderValueChangedEventArgs(oldValue, Value));
    }

    private void OnSliderLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_keyboardInteraction)
        {
            _keyboardInteraction = false;
            HideTooltip();
            CommitValue();
        }
    }

    private void ResolveThumb()
    {
        Thumb? thumb = FindVisualDescendant<Thumb>(InnerSlider);
        if (ReferenceEquals(_thumb, thumb))
        {
            return;
        }

        UnsubscribeThumb();
        _thumb = thumb;
        if (_thumb is null)
        {
            return;
        }

        _thumb.RenderTransform = new ScaleTransform(IdleThumbScale, IdleThumbScale);
        _thumb.ApplyTemplate();
        _thumbFillVisual = FindVisualDescendant<Border>(_thumb, "ThumbFillVisual");
        if (_thumbFillVisual is not null)
        {
            _thumbFillVisual.Opacity = 1;
        }

        _thumb.MouseEnter += OnThumbMouseEnter;
        _thumb.MouseLeave += OnThumbMouseLeave;
    }

    private void UnsubscribeThumb()
    {
        if (_thumb is null)
        {
            return;
        }

        _thumb.MouseEnter -= OnThumbMouseEnter;
        _thumb.MouseLeave -= OnThumbMouseLeave;
        _thumb = null;
        _thumbFillVisual = null;
    }

    private void OnSliderMouseEnter(object sender, MouseEventArgs e)
    {
        _sliderPointerOver = true;
        if (!_pointerInteraction)
        {
            AnimateThumbScale(HoverThumbScale);
        }
    }

    private void OnSliderMouseLeave(object sender, MouseEventArgs e)
    {
        _sliderPointerOver = false;
        if (!_pointerInteraction)
        {
            AnimateThumbScale(IdleThumbScale);
        }
    }

    private void OnThumbMouseEnter(object sender, MouseEventArgs e) => AnimateThumbBackground(0);

    private void OnThumbMouseLeave(object sender, MouseEventArgs e) => AnimateThumbBackground(1);

    private void AnimateThumbScale(double target)
    {
        if (_thumb?.RenderTransform is not ScaleTransform transform)
        {
            return;
        }

        double from = transform.ScaleX;
        if (Math.Abs(from - target) < 0.001)
        {
            return;
        }

        MotionAnimations.Start(_thumbAnimationChannel, MotionPreferences.FastDuration, MotionEasing.CubicEaseOut, progress =>
        {
            double scale = from + ((target - from) * progress);
            transform.ScaleX = scale;
            transform.ScaleY = scale;
        });
    }

    private void AnimateThumbBackground(double targetOpacity)
    {
        if (_thumbFillVisual is null)
        {
            return;
        }

        double fromOpacity = _thumbFillVisual.Opacity;
        if (Math.Abs(fromOpacity - targetOpacity) < 0.001)
        {
            return;
        }

        Border fillVisual = _thumbFillVisual;

        MotionAnimations.Start(
            _thumbBackgroundAnimationChannel,
            MotionPreferences.FastDuration,
            MotionEasing.CubicEaseOut,
            progress => fillVisual.Opacity = fromOpacity + ((targetOpacity - fromOpacity) * progress));
    }

    private void ShowTooltip()
    {
        UpdateText();
        TooltipPopup.PlacementTarget = _thumb is UIElement thumb ? thumb : InnerSlider;
        TooltipPopup.IsOpen = true;
        UpdateTooltipOffset();
        double fromOpacity = TooltipBubble.Opacity;
        double fromY = TooltipTransform.Y;
        MotionAnimations.Start(_tooltipAnimationChannel, MotionPreferences.FastDuration, MotionEasing.CubicEaseOut, progress =>
        {
            TooltipBubble.Opacity = fromOpacity + ((1 - fromOpacity) * progress);
            TooltipTransform.Y = fromY + ((0 - fromY) * progress);
        });
    }

    private void HideTooltip(bool immediate = false)
    {
        if (!TooltipPopup.IsOpen)
        {
            return;
        }

        if (immediate || !MotionPreferences.AnimationsEnabled)
        {
            MotionAnimations.Cancel(_tooltipAnimationChannel);
            TooltipBubble.Opacity = 0;
            TooltipTransform.Y = 3;
            TooltipPopup.IsOpen = false;
            return;
        }

        double fromOpacity = TooltipBubble.Opacity;
        double fromY = TooltipTransform.Y;
        MotionAnimations.Start(_tooltipAnimationChannel, MotionPreferences.FastDuration, MotionEasing.CubicEaseOut, progress =>
        {
            TooltipBubble.Opacity = fromOpacity * (1 - progress);
            TooltipTransform.Y = fromY + ((3 - fromY) * progress);
        }, () => TooltipPopup.IsOpen = false);
    }

    private void UpdateBarProgress()
    {
        double range = Math.Max(1, Maximum - Minimum);
        double ratio = Math.Clamp((Value - Minimum) / range, 0, 1);
        BarProgress.Width = Math.Max(0, BarTrack.ActualWidth * ratio);
    }

    private void UpdateTooltipOffset()
    {
        TooltipPopup.PlacementTarget = _thumb is UIElement thumb ? thumb : InnerSlider;
        // Changing an offset makes an open WPF Popup recalculate its placement
        // while the thumb is moving. The sub-pixel nudge is not perceptible.
        TooltipPopup.HorizontalOffset = TooltipPopup.HorizontalOffset == 0 ? 0.01 : 0;
    }

    private static CustomPopupPlacement[] PlaceTooltip(
        System.Windows.Size popupSize,
        System.Windows.Size targetSize,
        System.Windows.Point offset)
    {
        double thumbVisualTop = Math.Max(0, (targetSize.Height - ThumbVisualDiameter) / 2);
        return
        [
            new CustomPopupPlacement(
                new System.Windows.Point(
                    (targetSize.Width - popupSize.Width) / 2 + offset.X,
                    thumbVisualTop - TooltipGap - popupSize.Height + offset.Y),
                PopupPrimaryAxis.Horizontal)
        ];
    }

    private void UpdateText()
    {
        MinimumText.Text = FormatValue(Minimum);
        MaximumText.Text = FormatValue(Maximum);
        TooltipText.Text = FormatValue(Value);
        AutomationProperties.SetHelpText(
            InnerSlider,
            $"当前值 {FormatValue(Value)}，范围 {FormatValue(Minimum)} 到 {FormatValue(Maximum)}");
    }

    private string FormatValue(int value) => $"{value}{ValueSuffix}";

    private void OnSliderSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTooltipOffset();
    private void OnBarTrackSizeChanged(object sender, SizeChangedEventArgs e) => UpdateBarProgress();
    private void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) => Opacity = IsEnabled ? 1 : 0.5;

    private static T? FindVisualDescendant<T>(DependencyObject parent, string? name = null) where T : FrameworkElement
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match && (name is null || match.Name == name))
            {
                return match;
            }

            T? nested = FindVisualDescendant<T>(child, name);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
