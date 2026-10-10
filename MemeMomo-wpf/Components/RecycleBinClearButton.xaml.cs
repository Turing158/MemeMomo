using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MemeMomo.UI.Animation;
using Button = System.Windows.Controls.Button;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace MemeMomo.Components;

public partial class RecycleBinClearButton : Button
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(RecycleBinClearButton),
        new PropertyMetadata(string.Empty, OnLabelChanged));

    private readonly FrameAnimation _animation;
    private bool _pointerOver;

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public RecycleBinClearButton() : this(null) { }

    internal RecycleBinClearButton(IAnimationFrameSource? frames)
    {
        _animation = new FrameAnimation(frames);
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsEnabledChanged += OnIsEnabledChanged;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        _pointerOver = true;
        UpdateExpansion();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _pointerOver = false;
        UpdateExpansion();
    }

    private void UpdateExpansion(bool animate = true)
    {
        if (LabelClip is null) return;
        bool expanded = _pointerOver && IsEnabled && IsVisible;
        double labelWidth = expanded ? 6 + Math.Ceiling(new FormattedText(Label,
            CultureInfo.CurrentUICulture, LabelText.FlowDirection,
            new Typeface(LabelText.FontFamily, LabelText.FontStyle, LabelText.FontWeight, LabelText.FontStretch),
            LabelText.FontSize, LabelText.Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace) : 0;
        double opacity = expanded ? 1 : 0;
        double fromWidth = LabelClip.Width;
        double fromOpacity = LabelClip.Opacity;
        _animation.Cancel();
        void Apply(double progress)
        {
            LabelClip.Width = fromWidth + (labelWidth - fromWidth) * progress;
            LabelClip.Opacity = fromOpacity + (opacity - fromOpacity) * progress;
            Width = 32 + LabelClip.Width;
        }
        bool unchanged = Math.Abs(fromWidth - labelWidth) < 0.1 && Math.Abs(fromOpacity - opacity) < 0.001;
        if (!animate || !IsLoaded || unchanged) Apply(1);
        else _animation.Start(TimeSpan.FromMilliseconds(180), MotionEasing.CubicEaseOut, Apply);
    }

    private static void OnLabelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
        ((RecycleBinClearButton)sender).UpdateExpansion(animate: false);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _pointerOver = IsMouseOver;
        UpdateExpansion(animate: false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _pointerOver = false;
        UpdateExpansion(animate: false);
    }

    private void OnIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateExpansion(animate: false);

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _pointerOver = IsVisible && IsMouseOver;
        UpdateExpansion(animate: false);
    }
}
