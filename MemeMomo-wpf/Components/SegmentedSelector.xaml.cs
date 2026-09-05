using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MemeMomo.UI;
using MemeMomo.UI.Animation;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Rect = System.Windows.Rect;
using UserControl = System.Windows.Controls.UserControl;

namespace MemeMomo.Components;

public sealed record SegmentedSelectorOption(string Key, string Label);

public sealed class SegmentedSelectionChangedEventArgs(string? oldKey, string newKey) : EventArgs
{
    public string? OldKey { get; } = oldKey;
    public string NewKey { get; } = newKey;
}

public partial class SegmentedSelector : UserControl
{
    public static readonly DependencyProperty OptionsProperty = DependencyProperty.Register(
        nameof(Options),
        typeof(IReadOnlyList<SegmentedSelectorOption>),
        typeof(SegmentedSelector),
        new FrameworkPropertyMetadata(null, OnOptionsChanged));

    public static readonly DependencyProperty SelectedKeyProperty = DependencyProperty.Register(
        nameof(SelectedKey),
        typeof(string),
        typeof(SegmentedSelector),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnSelectedKeyChanged));

    private readonly object _indicatorAnimationChannel = new();
    private readonly List<Grid> _optionControls = [];
    private INotifyCollectionChanged? _observableOptions;
    private bool _normalizingSelection;
    private double _segmentWidth;

    public SegmentedSelector()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public IReadOnlyList<SegmentedSelectorOption> Options
    {
        get => (IReadOnlyList<SegmentedSelectorOption>?)GetValue(OptionsProperty) ?? Array.Empty<SegmentedSelectorOption>();
        set => SetValue(OptionsProperty, value ?? Array.Empty<SegmentedSelectorOption>());
    }

    public string? SelectedKey
    {
        get => (string?)GetValue(SelectedKeyProperty);
        set => SetValue(SelectedKeyProperty, value);
    }

    public event EventHandler<SegmentedSelectionChangedEventArgs>? SelectionChanged;

    internal double IndicatorLeft => Canvas.GetLeft(Indicator);
    internal double IndicatorWidth => Indicator.Width;
    internal Grid OptionsGridPart => OptionsGrid;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        IReadOnlyList<SegmentedSelectorOption> options = Options;
        if (options.Count == 0)
        {
            base.OnKeyDown(e);
            return;
        }

        int currentIndex = SelectedIndex;
        int targetIndex = e.Key switch
        {
            Key.Left => currentIndex <= 0 ? options.Count - 1 : currentIndex - 1,
            Key.Right => currentIndex >= options.Count - 1 ? 0 : currentIndex + 1,
            Key.Home => 0,
            Key.End => options.Count - 1,
            Key.Space or Key.Enter => currentIndex,
            _ => -1
        };

        if (targetIndex >= 0)
        {
            SetCurrentValue(SelectedKeyProperty, options[targetIndex].Key);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private static void OnOptionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        SegmentedSelector control = (SegmentedSelector)d;
        IReadOnlyList<SegmentedSelectorOption> options = e.NewValue as IReadOnlyList<SegmentedSelectorOption>
            ?? Array.Empty<SegmentedSelectorOption>();
        ValidateOptions(options);
        control.UnsubscribeOptions();
        control.SubscribeOptions(options);
        control.RebuildOptions();
    }

    private static void OnSelectedKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        SegmentedSelector control = (SegmentedSelector)d;
        if (control._normalizingSelection)
        {
            return;
        }

        string? normalized = control.NormalizeKey((string?)e.NewValue);
        if (!string.Equals(normalized, (string?)e.NewValue, StringComparison.Ordinal))
        {
            control._normalizingSelection = true;
            try
            {
                control.SetCurrentValue(SelectedKeyProperty, normalized);
            }
            finally
            {
                control._normalizingSelection = false;
            }
        }

        control.UpdateOptionStates();
        control.MoveIndicator(animate: control.IsLoaded);
        if (normalized is not null && !string.Equals((string?)e.OldValue, normalized, StringComparison.Ordinal))
        {
            control.SelectionChanged?.Invoke(
                control,
                new SegmentedSelectionChangedEventArgs((string?)e.OldValue, normalized));
        }
    }

    private static void ValidateOptions(IReadOnlyList<SegmentedSelectorOption> options)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (SegmentedSelectorOption option in options)
        {
            if (string.IsNullOrWhiteSpace(option.Key))
            {
                throw new ArgumentException("Segmented selector option keys cannot be empty.", nameof(options));
            }

            if (!keys.Add(option.Key))
            {
                throw new ArgumentException($"Segmented selector option key '{option.Key}' is duplicated.", nameof(options));
            }
        }
    }

    private int SelectedIndex
    {
        get
        {
            string? key = SelectedKey;
            for (int index = 0; index < Options.Count; index++)
            {
                if (string.Equals(Options[index].Key, key, StringComparison.Ordinal))
                {
                    return index;
                }
            }

            return 0;
        }
    }

    private string? NormalizeKey(string? key)
    {
        if (Options.Count == 0)
        {
            return null;
        }

        return Options.Any(option => string.Equals(option.Key, key, StringComparison.Ordinal))
            ? key
            : Options[0].Key;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeOptions(Options);
        RebuildOptions();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        MotionAnimations.Cancel(_indicatorAnimationChannel);
        UnsubscribeOptions();
        foreach (Grid option in _optionControls)
        {
            option.ReleaseMouseCapture();
            InteractionState.SetIsPressedForTest(option, false);
        }
    }

    private void SubscribeOptions(IReadOnlyList<SegmentedSelectorOption> options)
    {
        if (_observableOptions is not null || options is not INotifyCollectionChanged observable)
        {
            return;
        }

        _observableOptions = observable;
        _observableOptions.CollectionChanged += OnOptionsCollectionChanged;
    }

    private void UnsubscribeOptions()
    {
        if (_observableOptions is null)
        {
            return;
        }

        _observableOptions.CollectionChanged -= OnOptionsCollectionChanged;
        _observableOptions = null;
    }

    private void OnOptionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ValidateOptions(Options);
        string? previousKey = SelectedKey;
        RebuildOptions();
        if (previousKey is not null && !string.Equals(previousKey, SelectedKey, StringComparison.Ordinal) && SelectedKey is not null)
        {
            SelectionChanged?.Invoke(this, new SegmentedSelectionChangedEventArgs(previousKey, SelectedKey));
        }
    }

    private void RebuildOptions()
    {
        MotionAnimations.Cancel(_indicatorAnimationChannel);
        OptionsGrid.Children.Clear();
        OptionsGrid.ColumnDefinitions.Clear();
        _optionControls.Clear();

        for (int index = 0; index < Options.Count; index++)
        {
            SegmentedSelectorOption option = Options[index];
            OptionsGrid.ColumnDefinitions.Add(new ColumnDefinition());
            TextBlock label = new()
            {
                Text = option.Label,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                FontSize = 13,
                Margin = new Thickness(10, 0, 10, 0)
            };
            label.Name = "OptionLabel";
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            Border hoverLayer = new()
            {
                Name = "OptionHoverLayer",
                CornerRadius = new CornerRadius(9),
                IsHitTestVisible = false
            };
            hoverLayer.SetResourceReference(Border.BackgroundProperty, "BgHoverBrush");
            Border pressedLayer = new()
            {
                Name = "OptionPressedLayer",
                CornerRadius = new CornerRadius(9),
                IsHitTestVisible = false
            };
            pressedLayer.SetResourceReference(Border.BackgroundProperty, "SurfaceActiveBrush");
            Grid optionControl = new()
            {
                Tag = option.Key,
                MinHeight = 34,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            optionControl.Children.Add(pressedLayer);
            optionControl.Children.Add(hoverLayer);
            optionControl.Children.Add(label);
            InteractionAnimations.SetProfile(optionControl, InteractionAnimationProfile.Button);
            InteractionAnimations.SetTargetName(optionControl, "OptionLabel");
            InteractionAnimations.SetSurfaceHoverLayerName(optionControl, "OptionHoverLayer");
            InteractionAnimations.SetSurfacePressedLayerName(optionControl, "OptionPressedLayer");
            InteractionAnimations.SetHoverScale(optionControl, 1.05);
            InteractionAnimations.SetPressedScale(optionControl, 0.96);
            optionControl.MouseLeftButtonDown += OnOptionMouseDown;
            optionControl.MouseLeftButtonUp += OnOptionMouseUp;
            optionControl.LostMouseCapture += OnOptionLostMouseCapture;
            optionControl.MouseEnter += OnOptionMouseEnter;
            optionControl.MouseLeave += OnOptionMouseLeave;
            Grid.SetColumn(optionControl, index);
            OptionsGrid.Children.Add(optionControl);
            _optionControls.Add(optionControl);
        }

        string? previousKey = SelectedKey;
        string? normalized = NormalizeKey(previousKey);
        if (!string.Equals(normalized, previousKey, StringComparison.Ordinal))
        {
            _normalizingSelection = true;
            try
            {
                SetCurrentValue(SelectedKeyProperty, normalized);
            }
            finally
            {
                _normalizingSelection = false;
            }

        }

        UpdateOptionStates();
        CalibrateIndicator();
    }

    private void OnOptionMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Grid option || !IsEnabled)
        {
            return;
        }

        Focus();
        option.CaptureMouse();
        InteractionState.SetIsPressedForTest(option, true);
        UpdateOptionAppearance(option);
        e.Handled = true;
    }

    private void OnOptionMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Grid option)
        {
            return;
        }

        bool isInside = new Rect(option.RenderSize).Contains(e.GetPosition(option));
        InteractionState.SetIsPressedForTest(option, false);
        option.ReleaseMouseCapture();
        if (isInside && option.Tag is string key)
        {
            SetCurrentValue(SelectedKeyProperty, key);
        }

        UpdateOptionAppearance(option);
        e.Handled = true;
    }

    private void OnOptionLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (sender is Grid option)
        {
            InteractionState.SetIsPressedForTest(option, false);
            UpdateOptionAppearance(option);
        }
    }

    private void OnOptionMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Grid option)
        {
            UpdateOptionAppearance(option);
        }
    }

    private void OnOptionMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Grid option)
        {
            UpdateOptionAppearance(option);
        }
    }

    private void UpdateOptionStates()
    {
        foreach (Grid option in _optionControls)
        {
            InteractionState.SetIsSelected(option, Equals(option.Tag, SelectedKey));
            UpdateOptionAppearance(option);
        }
    }

    private void UpdateOptionAppearance(Grid option)
    {
        bool selected = InteractionState.GetIsSelected(option);
        if (option.Children.Count >= 3
            && option.Children[0] is Border pressedLayer
            && option.Children[1] is Border hoverLayer
            && option.Children[2] is TextBlock label)
        {
            if (selected)
            {
                pressedLayer.Background = Brushes.Transparent;
                hoverLayer.Background = Brushes.Transparent;
            }
            else
            {
                pressedLayer.SetResourceReference(Border.BackgroundProperty, "SurfaceActiveBrush");
                hoverLayer.SetResourceReference(Border.BackgroundProperty, "BgHoverBrush");
            }

            label.FontWeight = selected ? FontWeights.Medium : FontWeights.Normal;
            label.SetResourceReference(
                TextBlock.ForegroundProperty,
                selected ? "TextOnAccentBrush" : option.IsMouseOver ? "TextPrimaryBrush" : "TextSecondaryBrush");
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => CalibrateIndicator();

    private void CalibrateIndicator()
    {
        MotionAnimations.Cancel(_indicatorAnimationChannel);
        if (Options.Count == 0 || SelectedKey is null)
        {
            Indicator.Visibility = Visibility.Collapsed;
            return;
        }

        _segmentWidth = OptionsGrid.ActualWidth / Options.Count;
        double height = OptionsGrid.ActualHeight;
        if (_segmentWidth <= 0 || height <= 0)
        {
            return;
        }

        Indicator.Visibility = Visibility.Visible;
        Indicator.Width = _segmentWidth;
        Indicator.Height = height;
        Canvas.SetLeft(Indicator, SelectedIndex * _segmentWidth);
    }

    private void MoveIndicator(bool animate)
    {
        if (Options.Count == 0 || SelectedKey is null)
        {
            Indicator.Visibility = Visibility.Collapsed;
            return;
        }

        if (_segmentWidth <= 0)
        {
            CalibrateIndicator();
            return;
        }

        double target = SelectedIndex * _segmentWidth;
        double from = double.IsNaN(Canvas.GetLeft(Indicator)) ? target : Canvas.GetLeft(Indicator);
        Indicator.Visibility = Visibility.Visible;
        if (!animate || !MotionPreferences.AnimationsEnabled || Math.Abs(from - target) < 0.01)
        {
            MotionAnimations.Cancel(_indicatorAnimationChannel);
            Canvas.SetLeft(Indicator, target);
            return;
        }

        MotionAnimations.Start(
            _indicatorAnimationChannel,
            TimeSpan.FromMilliseconds(190),
            MotionEasing.CubicEaseOut,
            progress => Canvas.SetLeft(Indicator, from + ((target - from) * progress)));
    }
}
