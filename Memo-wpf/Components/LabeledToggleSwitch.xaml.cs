using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Memo.UI;
using Memo.UI.Animation;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace Memo.Components;

public partial class LabeledToggleSwitch : UserControl
{
    public static readonly DependencyProperty LeftLabelProperty = DependencyProperty.Register(
        nameof(LeftLabel),
        typeof(string),
        typeof(LabeledToggleSwitch),
        new FrameworkPropertyMetadata(string.Empty, OnLabelChanged));

    public static readonly DependencyProperty RightLabelProperty = DependencyProperty.Register(
        nameof(RightLabel),
        typeof(string),
        typeof(LabeledToggleSwitch),
        new FrameworkPropertyMetadata(string.Empty, OnLabelChanged));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(bool),
        typeof(LabeledToggleSwitch),
        new FrameworkPropertyMetadata(
            false,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnValueChanged));

    private readonly object _thumbAnimationChannel = new();
    private double _columnWidth;

    public LabeledToggleSwitch()
    {
        InitializeComponent();
        PreviewMouseLeftButtonDown += OnMouseLeftButtonDown;
        PreviewMouseLeftButtonUp += OnMouseLeftButtonUp;
        LostMouseCapture += OnLostMouseCapture;
        MouseLeave += OnMouseLeave;
        SizeChanged += OnSizeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public string LeftLabel
    {
        get => (string)GetValue(LeftLabelProperty);
        set => SetValue(LeftLabelProperty, value ?? string.Empty);
    }

    public string RightLabel
    {
        get => (string)GetValue(RightLabelProperty);
        set => SetValue(RightLabelProperty, value ?? string.Empty);
    }

    public bool Value
    {
        get => (bool)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public event Action<bool>? ValueChanged;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsEnabled && e.Key is Key.Space or Key.Enter)
        {
            Value = !Value;
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((LabeledToggleSwitch)d).UpdateLabels();

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        LabeledToggleSwitch control = (LabeledToggleSwitch)d;
        control.UpdateVisuals(control.IsLoaded);
        control.ValueChanged?.Invoke((bool)e.NewValue);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RecalculateColumnWidth();
        UpdateLabels();
        UpdateVisuals(animate: false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        MotionAnimations.Cancel(_thumbAnimationChannel);
        InteractionState.SetIsPressedForTest(this, false);
        ReleaseMouseCapture();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RecalculateColumnWidth();
        UpdateVisuals(animate: false);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsEnabled)
        {
            return;
        }

        Focus();
        CaptureMouse();
        InteractionState.SetIsPressedForTest(this, true);
        Value = !Value;
        e.Handled = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        InteractionState.SetIsPressedForTest(this, false);
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e) =>
        InteractionState.SetIsPressedForTest(this, false);

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (Mouse.LeftButton != MouseButtonState.Pressed)
        {
            InteractionState.SetIsPressedForTest(this, false);
        }
    }

    private void RecalculateColumnWidth()
    {
        _columnWidth = Math.Max(0, OptionsGrid.ActualWidth / 2);
        Thumb.Width = _columnWidth;
    }

    private void UpdateLabels()
    {
        LeftLabelText.Text = LeftLabel;
        RightLabelText.Text = RightLabel;
        ThumbLabelText.Text = Value ? RightLabel : LeftLabel;
    }

    private void UpdateVisuals(bool animate)
    {
        UpdateLabels();
        if (_columnWidth <= 0)
        {
            RecalculateColumnWidth();
        }

        double target = Value ? _columnWidth : 0;
        if (!animate || !MotionPreferences.AnimationsEnabled)
        {
            MotionAnimations.Cancel(_thumbAnimationChannel);
            ThumbTransform.X = target;
            return;
        }

        double from = ThumbTransform.X;
        if (Math.Abs(from - target) < 0.01)
        {
            return;
        }

        MotionAnimations.Start(
            _thumbAnimationChannel,
            TimeSpan.FromMilliseconds(190),
            MotionEasing.CubicEaseOut,
            progress => ThumbTransform.X = from + ((target - from) * progress));
    }
}
